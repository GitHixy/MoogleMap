using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Lumina.Data.Files;
using MoogleMap.Models;

namespace MoogleMap.Map;

/// <summary>One processed map ready to draw: fill texture and wall lines, and how visible it is right now.</summary>
public sealed record MapLayer(MapInfo Map, IDalamudTextureWrap Texture, IReadOnlyList<WallLine> Walls)
{
    public float Alpha { get; set; }
}

/// <summary>
/// Follows the map the player is on and keeps a processed texture of it ready to draw.
/// </summary>
/// <remarks>
/// Processing happens on worker threads, and nothing ever pops: a new version of the map fades in
/// over the old one, and a map left behind fades out. Processed maps are kept in memory, and the
/// other maps of the same territory (a dungeon's other floors, a town's districts) are prepared
/// in the background as soon as the first is up, so moving between them is instant.
/// While the explorer finds more floor, the map is redrawn with it every couple of seconds.
/// </remarks>
public sealed class MapService : IDisposable
{
    /// <summary>Seconds between redraws while the explorer keeps finding floor.</summary>
    private const double FuseInterval = 1.5;
    /// <summary>Seconds a new version takes to fade in, and an old one to fade out.</summary>
    private const double FadeTime = 0.4;
    /// <summary>Processed maps kept in memory.</summary>
    private const int CacheSize = 10;

    private sealed record Result(MapInfo Map, IDalamudTextureWrap Texture, StylizedMap Shown, int Cells, long Milliseconds);

    private readonly object gate = new();
    private readonly List<IDalamudTextureWrap> retired = [];
    private readonly List<(MapLayer Layer, double Since)> fading = [];
    private readonly List<MapLayer> visible = [];

    /// <summary>Maps as traced from their picture alone, by map and floor colour, most recent last.</summary>
    private readonly LinkedList<((uint Map, Vector4 Colour) Key, StylizedMap Map)> cache = new();

    private CancellationTokenSource? loading;
    private CancellationTokenSource? prefetching;
    private Result? pending;

    private MapLayer? front;
    private double frontSince;

    private Vector4 requested;
    private bool logEvents;
    private double restyleAt = double.MaxValue;
    private int fusedRevision = -1;
    /// <summary>Cell size of the last explorer snapshot, needed to place its cells on the map.</summary>
    private float snapshotCell = 1f;
    private double fuseAt;

    public MapInfo? Current { get; private set; }
    /// <summary>What to draw this frame, oldest first, each with its fade.</summary>
    public IReadOnlyList<MapLayer> Layers => visible;
    public bool Loading => loading is not null;
    public string Status { get; private set; } = "No map yet";
    public float PaperShare { get; private set; }
    public float FloorShare { get; private set; }
    /// <summary>Whether the floor on screen is (partly) traced from the map picture, rather than only explored.</summary>
    public bool FromPicture { get; private set; }
    /// <summary>
    /// Where exploring may go on the current map, by world X/Z, or null for anywhere. Set for maps
    /// traced from their picture: past what the picture draws is scenery, not the duty.
    /// </summary>
    public Func<float, float, bool>? Bounds { get; private set; }

    /// <summary>Longest side of the floor on screen, in yalms, or null when there's none yet.</summary>
    public float? AreaSize { get; private set; }
    /// <summary>Middle of the floor on screen, by world X/Z, or null when there's none yet.</summary>
    public Vector2? AreaCenter { get; private set; }
    /// <summary>Explored cells in the texture on screen.</summary>
    public int FusedCells { get; private set; }
    public int CachedMaps
    {
        get { lock (gate) return cache.Count; }
    }

    /// <summary>The static icons and names of the current map.</summary>
    public StaticMarkers Markers { get; } = new();

    /// <summary>
    /// Framework tick: notices map changes, settings that need a new texture, and new floor from
    /// the explorer worth redrawing for. Pass null when the explorer isn't surveying this map.
    /// </summary>
    public void Update(Configuration config, double now, Explorer? survey)
    {
        logEvents = config.LogEvents;
        var mapId = Plugin.ClientState.MapId;
        if (mapId != (Current?.RowId ?? 0))
            SwitchTo(mapId, config, survey, now);

        if (Current is not { HasTexture: true } map)
            return;

        if (config.FloorColor != requested)
        {
            // Colour pickers fire every frame while dragged; wait for them to settle.
            requested = config.FloorColor;
            restyleAt = now + 0.35;
        }

        if (now >= restyleAt)
        {
            restyleAt = double.MaxValue;
            Load(map, requested, Cells(survey));
            return;
        }

        if (survey is not null && survey.Revision != fusedRevision && now >= fuseAt && !Loading)
        {
            fuseAt = now + FuseInterval;
            Load(map, requested, Cells(survey));
        }
    }

    private (long[] Cells, CellSide[] Sides) Cells(Explorer? survey)
    {
        if (survey is null) return ([], []);
        fusedRevision = survey.Revision;
        snapshotCell = survey.Cell;
        return (survey.Snapshot(), survey.Walls());
    }

    /// <summary>Draw-thread hook: brings in finished maps, advances fades and frees what faded out.</summary>
    public void BeginFrame(double now, bool reducedMotion)
    {
        foreach (var old in retired)
            old.Dispose();
        retired.Clear();

        Result? done;
        lock (gate)
        {
            done = pending;
            pending = null;
        }

        if (done is not null)
            Present(done, now);

        var fadeTime = reducedMotion ? 1e-3 : FadeTime;
        visible.Clear();

        for (var i = 0; i < fading.Count; i++)
        {
            var (layer, since) = fading[i];
            var t = Ease((now - since) / fadeTime);
            if (t >= 1f)
            {
                retired.Add(layer.Texture);
                fading.RemoveAt(i--);
                continue;
            }

            layer.Alpha = 1f - t;
            visible.Add(layer);
        }

        if (front is not null)
        {
            front.Alpha = Ease((now - frontSince) / fadeTime);
            visible.Add(front);
        }
    }

    private void Present(Result done, double now)
    {
        if (done.Map.RowId != (Current?.RowId ?? 0))
        {
            // Finished after the player had already moved on.
            done.Texture.Dispose();
            return;
        }

        if (front is not null)
            fading.Add((front, now));

        front = new MapLayer(done.Map, done.Texture, done.Shown.Walls);
        frontSince = now;

        FromPicture = done.Shown.FromPicture;
        Bounds = done.Shown is { FromPicture: true, Footprint: { } area } ? Inside(done.Map, area, done.Shown.Size) : null;
        var extent = Extent(done.Shown.Walls, done.Map);
        AreaSize = extent?.Size;
        AreaCenter = extent?.Center;
        PaperShare = done.Shown.PaperShare;
        FloorShare = done.Shown.FloorShare;
        FusedCells = done.Cells;
        Status = done.Shown.Walls.Count > 0
            ? $"{done.Shown.Walls.Count} walls, ready in {done.Milliseconds} ms"
            : "Nothing yet: the floor fills in as it's explored";
        if (logEvents)
            Plugin.Log.Debug("Map {Map} ready: {Walls} walls, picture {Picture}, {Cells} explored cells, {Ms} ms",
                done.Map.Key, done.Shown.Walls.Count, done.Shown.FromPicture, done.Cells, done.Milliseconds);
    }

    private void SwitchTo(uint mapId, Configuration config, Explorer? survey, double now)
    {
        loading?.Cancel();
        loading = null;
        prefetching?.Cancel();
        prefetching = null;
        fusedRevision = -1;

        // The old map fades out on its own; it isn't dropped here.
        if (front is not null)
        {
            fading.Add((front, now));
            front = null;
        }

        FusedCells = 0;
        Current = null;
        AreaSize = null;
        AreaCenter = null;
        Bounds = null;

        if (mapId != 0 && Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Map>().GetRowOrDefault(mapId) is { } row)
            Current = new MapInfo(row);

        Markers.Load(Current);

        if (Current is null)
            Status = "No map";
        else if (!Current.HasTexture)
            Status = "The game has no picture of this map";
        else
        {
            requested = config.FloorColor;
            Load(Current, requested, Cells(survey));
        }

        if (config.LogEvents)
            Plugin.Log.Debug("Map changed to {Id} ({Key}) {Name}", mapId, Current?.Key ?? "-", Current?.DisplayName ?? "-");
    }

    private void Load(MapInfo map, Vector4 colour, (long[] Cells, CellSide[] Sides) explored)
    {
        var (cells, sides) = explored;
        loading?.Cancel();
        var cts = new CancellationTokenSource();
        loading = cts;
        if (front is null)
            Status = "Loading...";
        var cell = snapshotCell;

        Task.Run(async () =>
        {
            var watch = Stopwatch.StartNew();
            var traced = Traced(map, colour, cts.Token);

            cts.Token.ThrowIfCancellationRequested();
            var shown = cells.Length > 0 ? MapStylizer.Fuse(traced, map, cells, sides, cell, colour) : traced;
            cts.Token.ThrowIfCancellationRequested();

            var texture = await Plugin.TextureProvider.CreateFromRawAsync(
                RawImageSpecification.Rgba32(shown.Size, shown.Size), shown.Rgba, $"MoogleMap {map.Key}", cts.Token);

            return new Result(map, texture, shown, cells.Length, watch.ElapsedMilliseconds);
        }, cts.Token).ContinueWith(task =>
        {
            if (ReferenceEquals(loading, cts))
                loading = null;

            if (task.IsCanceled || cts.IsCancellationRequested)
            {
                if (task.IsCompletedSuccessfully)
                    task.Result.Texture.Dispose();
                return;
            }

            if (task.Exception is { } ex)
            {
                Status = "Couldn't load this map";
                Plugin.Log.Error(ex.GetBaseException(), "Failed to load map {Key}", map.Key);
                return;
            }

            lock (gate)
            {
                pending?.Texture.Dispose();
                pending = task.Result;
            }

            Prefetch(map, colour);
        }, TaskScheduler.Default);
    }

    /// <summary>The map traced from its picture, from memory when it's been seen before.</summary>
    private StylizedMap Traced(MapInfo map, Vector4 colour, CancellationToken token)
    {
        var key = (map.RowId, colour);
        lock (gate)
        {
            for (var node = cache.First; node is not null; node = node.Next)
            {
                if (node.Value.Key != key) continue;
                cache.Remove(node);
                cache.AddLast(node);
                return node.Value.Map;
            }
        }

        var file = Plugin.DataManager.GetFile<TexFile>(map.TexturePath)
                   ?? throw new InvalidOperationException($"Map texture {map.TexturePath} not found");
        var buffer = file.TextureBuffer.Filter(0, 0, TexFile.TextureFormat.B8G8R8A8);
        token.ThrowIfCancellationRequested();
        var traced = MapStylizer.Run(buffer.RawData, buffer.Width, colour, map.IsOpenWorld, map.Scale);

        lock (gate)
        {
            if (cache.All(entry => entry.Key != key))
            {
                cache.AddLast((key, traced));
                while (cache.Count > CacheSize)
                    cache.RemoveFirst();
            }
        }

        return traced;
    }

    /// <summary>Prepares the other maps of the same territory in the background, one at a time.</summary>
    private void Prefetch(MapInfo loaded, Vector4 colour)
    {
        if (prefetching is not null) return;

        var siblings = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Map>()
            .Where(row => row.TerritoryType.RowId == loaded.TerritoryId && row.RowId != loaded.RowId)
            .Select(row => new MapInfo(row))
            .Where(map => map.HasTexture)
            .Take(CacheSize - 1)
            .ToList();
        if (siblings.Count == 0) return;

        var cts = new CancellationTokenSource();
        prefetching = cts;
        Task.Run(() =>
        {
            foreach (var map in siblings)
            {
                if (cts.IsCancellationRequested) return;
                try
                {
                    Traced(map, colour, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Plugin.Log.Debug(ex, "Couldn't prepare map {Key}", map.Key);
                }
            }

            if (logEvents)
                Plugin.Log.Debug("Prepared {Count} more maps of territory {Territory}", siblings.Count, loaded.TerritoryId);
        }, cts.Token);
    }

    private static Func<float, float, bool> Inside(MapInfo map, bool[] area, int n)
    {
        var toProcessed = n / MapInfo.TextureSize;
        return (x, z) =>
        {
            var p = map.WorldToTexture(new Vector3(x, 0f, z)) * toProcessed;
            var px = (int)p.X;
            var py = (int)p.Y;
            return px >= 0 && py >= 0 && px < n && py < n && area[py * n + px];
        };
    }

    private static (float Size, Vector2 Center)? Extent(IReadOnlyList<WallLine> walls, MapInfo map)
    {
        if (walls.Count == 0) return null;
        var min = new Vector2(float.MaxValue);
        var max = new Vector2(float.MinValue);
        foreach (var wall in walls)
        {
            min = Vector2.Min(min, wall.Min);
            max = Vector2.Max(max, wall.Max);
        }

        var size = max - min;
        return (Math.Max(size.X, size.Y) / map.Scale, map.TextureToWorld((min + max) * 0.5f));
    }

    private static float Ease(double t)
    {
        var x = (float)Math.Clamp(t, 0, 1);
        return x * x * (3f - 2f * x);
    }

    /// <summary>
    /// Saves the current map picture, and the same picture with the floor (blue) and walls (red)
    /// found on it, to look at outside the game. Returns the folder, or null when there's no map.
    /// </summary>
    public string? Dump(string folder)
    {
        if (Current is not { HasTexture: true } map) return null;

        var file = Plugin.DataManager.GetFile<TexFile>(map.TexturePath);
        if (file is null) return null;
        var buffer = file.TextureBuffer.Filter(0, 0, TexFile.TextureFormat.B8G8R8A8);
        var size = buffer.Width;
        var bgra = buffer.RawData;

        System.IO.Directory.CreateDirectory(folder);
        var name = map.Key.Replace("/", "_");

        var original = new byte[size * size * 4];
        for (var k = 0; k < size * size; k++)
        {
            original[k * 4] = bgra[k * 4 + 2];
            original[k * 4 + 1] = bgra[k * 4 + 1];
            original[k * 4 + 2] = bgra[k * 4];
            original[k * 4 + 3] = 255;
        }
        Services.PngWriter.Write(System.IO.Path.Combine(folder, $"{name}_original.png"), original, size, size);

        // The traced floor, at the processed resolution, over the picture.
        var traced = Traced(map, requested, CancellationToken.None);
        var n = traced.Size;
        var step = size / n;
        var marked = new byte[n * n * 4];
        for (var y = 0; y < n; y++)
        {
            for (var x = 0; x < n; x++)
            {
                var src = (y * step * size + x * step) * 4;
                var o = (y * n + x) * 4;
                var r = original[src] * 0.6f;
                var g = original[src + 1] * 0.6f;
                var b = original[src + 2] * 0.6f;
                if (traced.Traced[y * n + x])
                {
                    r = r * 0.5f + 20f;
                    g = g * 0.5f + 70f;
                    b = b * 0.5f + 127f;
                }
                marked[o] = (byte)Math.Min(255f, r);
                marked[o + 1] = (byte)Math.Min(255f, g);
                marked[o + 2] = (byte)Math.Min(255f, b);
                marked[o + 3] = 255;
            }
        }

        // Walls as drawn, which include what exploring added.
        var walls = front?.Map.RowId == map.RowId ? front.Walls : traced.Walls;
        foreach (var wall in walls)
        {
            var count = wall.Closed ? wall.Points.Length : wall.Points.Length - 1;
            for (var i = 0; i < count; i++)
            {
                var a = wall.Points[i] / step;
                var b = wall.Points[(i + 1) % wall.Points.Length] / step;
                var steps = (int)MathF.Ceiling(Vector2.Distance(a, b) * 2f) + 1;
                for (var t = 0; t <= steps; t++)
                {
                    var p = Vector2.Lerp(a, b, t / (float)steps);
                    var x = (int)p.X;
                    var y = (int)p.Y;
                    if (x < 0 || y < 0 || x >= n || y >= n) continue;
                    var o = (y * n + x) * 4;
                    marked[o] = 255;
                    marked[o + 1] = 40;
                    marked[o + 2] = 40;
                }
            }
        }
        Services.PngWriter.Write(System.IO.Path.Combine(folder, $"{name}_traced.png"), marked, n, n);

        System.IO.File.WriteAllText(System.IO.Path.Combine(folder, $"{name}_info.txt"),
            $"map {map.RowId} {map.Key} \"{map.DisplayName}\" territory {map.TerritoryId}\n"
            + $"kind {map.Kind}, scale {map.Scale}, offset {map.Offset}\n"
            + $"paper {traced.PaperShare:P1}, traced floor {traced.FloorShare:P2}, from picture {traced.FromPicture}, footprint {traced.Footprint is not null}\n"
            + $"traced walls {traced.Walls.Count}, drawn walls {walls.Count}, explored cells {FusedCells}\n"
            + $"status: {Status}\n");

        return folder;
    }

    /// <summary>Called on logout: drop everything tied to the old map.</summary>
    public void Reset()
    {
        loading?.Cancel();
        loading = null;
        prefetching?.Cancel();
        prefetching = null;
        if (front is not null) retired.Add(front.Texture);
        foreach (var (layer, _) in fading) retired.Add(layer.Texture);
        front = null;
        fading.Clear();
        visible.Clear();
        FusedCells = 0;
        Current = null;
        AreaSize = null;
        AreaCenter = null;
        Bounds = null;
        Markers.Load(null);
        Status = "No map yet";
    }

    public void Dispose()
    {
        loading?.Cancel();
        prefetching?.Cancel();
        front?.Texture.Dispose();
        foreach (var (layer, _) in fading) layer.Texture.Dispose();
        foreach (var old in retired) old.Dispose();
        retired.Clear();
        lock (gate)
        {
            pending?.Texture.Dispose();
            pending = null;
            cache.Clear();
        }
    }
}

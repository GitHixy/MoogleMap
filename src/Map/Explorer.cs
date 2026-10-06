using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;

namespace MoogleMap.Map;

/// <summary>
/// Finds where you can walk from the game's collision, one cell at a time, outwards from places
/// known to be walkable: wherever the player, an NPC or another player is standing.
/// </summary>
/// <remarks>
/// <para>
/// Every step from a cell to its neighbour casts a ray at chest height first, so walls stop the
/// search, then a short ray down from just above the feet to find the ground on the same level.
/// Starting that ray low is what lets it pass under bridges, arches and upper decks instead of
/// landing on top of them, and each cell keeps every level it was reached on, so a street under
/// a walkway and the walkway itself are both walkable.
/// </para>
/// <para>
/// Two ways of running: with a reach, only around the player and growing as they walk, like fog
/// of war (deep dungeons, which have no map picture); without one, over the whole area, to fill
/// in what the map picture leaves out (towns with stacked levels, narrow alleys).
/// </para>
/// </remarks>
public sealed class Explorer
{
    /// <summary>
    /// Cell edge in yalms, matched to the map's detail: a yalm in towns and duties, two in open
    /// fields, whose maps are drawn at half the scale. Finer than a map pixel is wasted raycasts.
    /// </summary>
    public float Cell { get; private set; } = 1f;

    /// <summary>Highest rise between neighbouring cells that still counts as walking: stairs and slopes, not ledges.</summary>
    private float MaxStep => 1.2f * Cell;
    /// <summary>How far down a flying player's ground is looked for, to keep exploring from the air.</summary>
    private const float FlightReach = 250f;
    /// <summary>Height of the wall test above the feet. Below most overhangs, above most steps.</summary>
    private const float ChestHeight = 1.4f;
    /// <summary>Two floors closer than this in the same cell are the same floor.</summary>
    private const float SameLevel = 1.6f;
    /// <summary>Frontier entries looked at per tick when they're out of reach, to bound the cost of skipping them.</summary>
    private const int ScanLimit = 3000;

    private const int FileMagic = 0x4D4D5850; // "MMXP"
    private const int FileVersion = 2;

    private readonly record struct Pending(int X, int Z, float FromHeight, int FromX, int FromZ);

    /// <summary>Floor heights found in each cell, one per level.</summary>
    private readonly Dictionary<long, float[]> cells = new();
    /// <summary>Steps already tried, by target cell and the level they came from, so none is cast twice.</summary>
    private readonly HashSet<(long Cell, int Level)> tried = [];
    /// <summary>Seeds that found no ground (someone flying, jumping or on a mount), not worth casting for again.</summary>
    private readonly HashSet<(long Cell, int Level)> barrenSeeds = [];
    private readonly List<Pending> frontier = [];
    private readonly Stopwatch watch = new();
    private int cursor;
    private bool warnedBarren;

    /// <summary>Bumped whenever a floor is found, so drawings know to rebuild.</summary>
    public int Revision { get; private set; }
    public int FloorCells => cells.Count;
    public long Raycasts { get; private set; }
    public int FrontierSize => frontier.Count;

    /// <summary>
    /// How much of what's in reach is mapped, 0 to 1: cells found against cells found plus cells
    /// still waiting to be looked at. Refreshed a couple of times a second.
    /// </summary>
    public float Progress { get; private set; } = 1f;
    private double progressAt;
    /// <summary>Territory the data belongs to, for saving.</summary>
    public uint Territory { get; private set; }
    /// <summary>Changed since the last save.</summary>
    public bool Dirty { get; private set; }

    public void Clear()
    {
        cells.Clear();
        tried.Clear();
        barrenSeeds.Clear();
        frontier.Clear();
        cursor = 0;
        warnedBarren = false;
        Progress = 1f;
        progressAt = 0;
        Dirty = false;
        Revision++;
    }

    /// <summary>Starts over for a territory, picking up what was saved for it before.</summary>
    public void Begin(uint territory, string? file, float cell)
    {
        Clear();
        Territory = territory;
        Cell = cell;
        if (file is not null && File.Exists(file))
            Load(file);
    }

    /// <summary>
    /// Spends up to <paramref name="budgetMs"/> on the search. Seeds are places known to be
    /// walkable; <paramref name="reach"/> limits the search to around the player, or 0 for none.
    /// </summary>
    public void Update(Vector3 player, IReadOnlyList<Vector3> seeds, float reach, double budgetMs)
    {
        watch.Restart();

        if (!Seed(player, remember: false))
            SeedBelow(player);
        if (cells.Count == 0 && Raycasts > 200 && !warnedBarren)
        {
            // Not even the ground under the player: something is off with the collision queries.
            warnedBarren = true;
            Plugin.Log.Warning("Explorer found no ground after {Rays} raycasts at {Position} in territory {Territory}", Raycasts, player, Territory);
        }
        foreach (var seed in seeds)
        {
            if (watch.Elapsed.TotalMilliseconds > budgetMs * 0.3) break;
            Seed(seed);
        }

        var reachSq = reach * reach;
        var scanned = 0;
        while (frontier.Count > 0 && watch.Elapsed.TotalMilliseconds < budgetMs)
        {
            if (cursor >= frontier.Count) cursor = 0;
            var p = frontier[cursor];

            if (reach > 0f)
            {
                if (scanned++ > ScanLimit) break;
                var dx = (p.X + 0.5f) * Cell - player.X;
                var dz = (p.Z + 0.5f) * Cell - player.Z;
                if (dx * dx + dz * dz > reachSq)
                {
                    cursor++;
                    continue;
                }
            }

            // Swap-remove: order doesn't matter and it keeps removal cheap.
            frontier[cursor] = frontier[^1];
            frontier.RemoveAt(frontier.Count - 1);
            Expand(p);
        }

        var now = Environment.TickCount64 / 1000.0;
        if (now >= progressAt)
        {
            progressAt = now + 0.5;
            UpdateProgress(player, reach);
        }
    }

    private void UpdateProgress(Vector3 player, float reach)
    {
        if (reach <= 0f)
        {
            Progress = cells.Count + frontier.Count == 0 ? 1f : cells.Count / (float)(cells.Count + frontier.Count);
            return;
        }

        // With a reach, only what's around the player counts: the rest waits for them to go there.
        var reachSq = reach * reach;
        bool Near(int x, int z)
        {
            var dx = (x + 0.5f) * Cell - player.X;
            var dz = (z + 0.5f) * Cell - player.Z;
            return dx * dx + dz * dz <= reachSq;
        }

        // Walk the grid in reach rather than every cell: stays cheap however much has been explored.
        var span = (int)MathF.Ceiling(reach / Cell);
        var px = Index(player.X);
        var pz = Index(player.Z);
        var found = 0;
        for (var z = pz - span; z <= pz + span; z++)
            for (var x = px - span; x <= px + span; x++)
                if (Near(x, z) && cells.ContainsKey(Key(x, z)))
                    found++;

        var waiting = 0;
        foreach (var p in frontier)
            if (Near(p.X, p.Z))
                waiting++;

        Progress = found + waiting == 0 ? 1f : found / (float)(found + waiting);
    }

    /// <summary>
    /// Marks the ground under a standing character as floor, if it isn't known yet. With
    /// <paramref name="remember"/>, a miss isn't tried again; the player's own spot always is,
    /// since they'll land from a jump right where they took off.
    /// </summary>
    /// <returns>Whether there's known ground under them now.</returns>
    private bool Seed(Vector3 at, bool remember = true)
    {
        var x = Index(at.X);
        var z = Index(at.Z);
        var key = Key(x, z);
        if (HasLevel(key, at.Y, out _)) return true;
        if (barrenSeeds.Contains((key, Level(at.Y)))) return false;

        // Characters stand on the ground; anything well above it is flying or jumping.
        if (Cast(new Vector3(at.X, at.Y + 0.5f, at.Z), -Vector3.UnitY, 1.5f, out var ground) && Walkable(ground))
        {
            AddFloor(x, z, ground.Point.Y);
            return true;
        }

        if (remember)
            barrenSeeds.Add((key, Level(at.Y)));
        return false;
    }

    /// <summary>
    /// For a player in the air: finds the ground far below and explores from there, so the map
    /// keeps filling in while flying.
    /// </summary>
    private void SeedBelow(Vector3 at)
    {
        if (!Cast(at, -Vector3.UnitY, FlightReach, out var ground) || !Walkable(ground)) return;

        var x = Index(at.X);
        var z = Index(at.Z);
        if (!HasLevel(Key(x, z), ground.Point.Y, out _))
            AddFloor(x, z, ground.Point.Y);
    }

    private void Expand(Pending p)
    {
        var key = Key(p.X, p.Z);
        if (HasLevel(key, p.FromHeight, out _)) return;
        if (!tried.Add((key, Level(p.FromHeight)))) return;

        var feet = p.FromHeight;
        var from = new Vector3((p.FromX + 0.5f) * Cell, feet + ChestHeight, (p.FromZ + 0.5f) * Cell);
        var to = new Vector3((p.X + 0.5f) * Cell, feet + ChestHeight, (p.Z + 0.5f) * Cell);

        // A wall between the two cells: this one waits for a path that isn't blocked.
        if (Cast(from, Vector3.Normalize(to - from), Cell, out _))
            return;

        // Down from just above step height: low enough to pass under whatever is overhead.
        var start = feet + MaxStep + 0.1f;
        if (Cast(new Vector3(to.X, start, to.Z), -Vector3.UnitY, MaxStep * 2f + 0.1f, out var ground)
            && Walkable(ground)
            && MathF.Abs(ground.Point.Y - feet) <= MaxStep)
        {
            AddFloor(p.X, p.Z, ground.Point.Y);
        }
    }

    private void AddFloor(int x, int z, float height)
    {
        var key = Key(x, z);
        if (cells.TryGetValue(key, out var levels))
        {
            if (HasLevel(levels, height)) return;
            Array.Resize(ref levels, levels.Length + 1);
            levels[^1] = height;
            cells[key] = levels;
        }
        else
        {
            cells[key] = [height];
        }

        Revision++;
        Dirty = true;

        Queue(x + 1, z, height, x, z);
        Queue(x - 1, z, height, x, z);
        Queue(x, z + 1, height, x, z);
        Queue(x, z - 1, height, x, z);
    }

    private void Queue(int x, int z, float height, int fromX, int fromZ)
    {
        var key = Key(x, z);
        if (HasLevel(key, height, out _) || tried.Contains((key, Level(height)))) return;
        frontier.Add(new Pending(x, z, height, fromX, fromZ));
    }

    private bool HasLevel(long key, float height, out float[]? levels)
        => cells.TryGetValue(key, out levels) && HasLevel(levels, height);

    private static bool HasLevel(float[] levels, float height)
    {
        foreach (var h in levels)
            if (MathF.Abs(h - height) < SameLevel)
                return true;
        return false;
    }

    private static int Level(float height) => (int)MathF.Floor(height / SameLevel);

    /// <summary>
    /// Flat enough to stand on. The slope comes from the triangle that was hit rather than the
    /// hit's normal, which isn't reliably filled in and can face either way.
    /// </summary>
    private static bool Walkable(in RaycastHit hit)
    {
        var face = Vector3.Cross(hit.V2 - hit.V1, hit.V3 - hit.V1);
        var length = face.Length();
        if (length < 1e-6f)
        {
            // No triangle to go by: trust the normal if there is one, otherwise the hit itself.
            var normal = hit.Normal.Length();
            return normal < 1e-6f || MathF.Abs(hit.Normal.Y) / normal > 0.5f;
        }

        return MathF.Abs(face.Y) / length > 0.5f;
    }

    private bool Cast(Vector3 origin, Vector3 direction, float distance, out RaycastHit hit)
    {
        Raycasts++;
        try
        {
            return BGCollisionModule.RaycastMaterialFilter(origin, direction, out hit, distance);
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, "Explorer raycast failed");
            hit = default;
            return false;
        }
    }

    public bool IsFloor(int x, int z) => cells.ContainsKey(Key(x, z));

    /// <summary>Every walkable cell, levels flattened, for drawing.</summary>
    public IEnumerable<(int X, int Z)> Floor()
    {
        foreach (var key in cells.Keys)
            yield return ((int)(key >> 32), (int)(key & 0xFFFFFFFF));
    }

    /// <summary>A copy of the walkable cells, safe to hand to a worker thread.</summary>
    public (int X, int Z)[] Snapshot()
    {
        var result = new (int X, int Z)[cells.Count];
        var i = 0;
        foreach (var key in cells.Keys)
            result[i++] = ((int)(key >> 32), (int)(key & 0xFFFFFFFF));
        return result;
    }

    // ------------------------------------------------------------------
    // Saving
    // ------------------------------------------------------------------

    public void Save(string file)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            using var stream = File.Create(file);
            using var writer = new BinaryWriter(stream);
            writer.Write(FileMagic);
            writer.Write(FileVersion);
            writer.Write(Territory);
            writer.Write(Cell);
            writer.Write(cells.Count);
            foreach (var (key, levels) in cells)
            {
                writer.Write(key);
                writer.Write((byte)Math.Min(levels.Length, 255));
                for (var i = 0; i < Math.Min(levels.Length, 255); i++)
                    writer.Write(levels[i]);
            }

            Dirty = false;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Couldn't save explored floor to {File}", file);
        }
    }

    private void Load(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            using var reader = new BinaryReader(stream);
            if (reader.ReadInt32() != FileMagic || reader.ReadInt32() != FileVersion || reader.ReadUInt32() != Territory)
                return;

            // Cells of another size don't line up with this grid; start over rather than mix them.
            if (MathF.Abs(reader.ReadSingle() - Cell) > 1e-3f)
                return;

            var count = reader.ReadInt32();
            for (var i = 0; i < count; i++)
            {
                var key = reader.ReadInt64();
                var levels = new float[reader.ReadByte()];
                for (var j = 0; j < levels.Length; j++)
                    levels[j] = reader.ReadSingle();
                cells[key] = levels;
            }

            // Pick up where it left off: every edge of what's known is worth one more look.
            foreach (var (key, levels) in cells)
            {
                var x = (int)(key >> 32);
                var z = (int)(key & 0xFFFFFFFF);
                foreach (var h in levels)
                {
                    Queue(x + 1, z, h, x, z);
                    Queue(x - 1, z, h, x, z);
                    Queue(x, z + 1, h, x, z);
                    Queue(x, z - 1, h, x, z);
                }
            }

            Revision++;
            Plugin.Log.Debug("Loaded {Count} explored cells for territory {Territory}", count, Territory);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Couldn't read explored floor from {File}", file);
            cells.Clear();
        }
    }

    public int Index(float world) => (int)MathF.Floor(world / Cell);

    private static long Key(int x, int z) => (long)x << 32 | (uint)z;
}

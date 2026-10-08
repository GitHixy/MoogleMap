using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Threading.Tasks;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;

namespace MoogleMap.Map;

/// <summary>
/// Finds where you can walk from the game's collision, one cell at a time, outwards from places
/// known to be walkable: wherever the player, an NPC or another player is standing.
/// </summary>
/// <remarks>
/// <para>
/// Every step from a cell to its neighbour casts a ray at chest height first, so walls stop the
/// search, and one at knee height for fences and low walls, then a short ray down from just above
/// the feet to find the ground on the same level.
/// Starting that ray low is what lets it pass under bridges, arches and upper decks instead of
/// landing on top of them, and each cell keeps every level it was reached on, so a street under
/// a walkway and the walkway itself are both walkable.
/// </para>
/// <para>
/// Walls thinner than a cell fall between two floor cells and leave no gap in the grid, so every
/// side between neighbouring floor cells is also tested once, and the blocked ones are kept as
/// walls of their own.
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
    /// <summary>Height of the low wall test: fences, railings and low walls the chest-height test passes over.</summary>
    private const float KneeHeight = 0.6f;
    /// <summary>
    /// A knee-height hit only counts with the ground this close to the feet on both sides: a low
    /// wall on flat ground, rather than the next step of a staircase.
    /// </summary>
    private const float FlatGround = 0.3f;
    /// <summary>
    /// Where the rays that test a side between floor cells cross it, as a share of the cell from
    /// its middle. A doorway off the grid catches one of them on its frame, never all three.
    /// </summary>
    private static readonly float[] SideOffsets = [0f, -0.35f, 0.35f];
    /// <summary>Two floors closer than this in the same cell are the same floor.</summary>
    private const float SameLevel = 1.6f;
    /// <summary>Frontier entries looked at per tick when they're out of reach, to bound the cost of skipping them.</summary>
    private const int ScanLimit = 3000;

    private const int FileMagic = 0x4D4D5850; // "MMXP"
    private const int FileVersion = 4;

    private readonly record struct Pending(int X, int Z, float FromHeight, int FromX, int FromZ);

    /// <summary>The east and south sides of a cell: whether each has been tested, and whether it's blocked.</summary>
    [Flags]
    private enum Sides : byte
    {
        EastTested = 1,
        EastBlocked = 2,
        SouthTested = 4,
        SouthBlocked = 8,
    }

    /// <summary>Floor heights found in each cell, one per level.</summary>
    private Dictionary<long, float[]> cells = new(CellKeys.Instance);
    /// <summary>
    /// Steps already tried, by target cell, the level they came from and the side they came in by,
    /// so none is cast twice. A cell blocked from one side is still tried from the others.
    /// </summary>
    private readonly HashSet<(long Cell, int Level, int From)> tried = new(CellKeys.Instance);
    /// <summary>What's known about the sides between cells, by the cell west or north of each.</summary>
    private Dictionary<long, Sides> sides = new(CellKeys.Instance);
    /// <summary>Floor cells whose sides with their floor neighbours still need testing.</summary>
    private Queue<long> sidesToTest = new();
    /// <summary>Every blocked side found, kept as a list so drawing doesn't have to look through every side.</summary>
    private List<CellSide> blockedSides = [];
    /// <summary>The save being written in the background, if any; saves queue up behind it.</summary>
    private Task saving = Task.CompletedTask;
    /// <summary>Seeds that found no ground (someone flying, jumping or on a mount), not worth casting for again.</summary>
    private readonly HashSet<(long Cell, int Level)> barrenSeeds = new(CellKeys.Instance);
    private List<Pending> frontier = [];
    /// <summary>
    /// What was saved for this territory, being read in the background: a big area takes a second
    /// or more, too long to stall the game for. Exploring waits until it's in.
    /// </summary>
    private Task<Loaded?>? loading;
    private readonly Stopwatch watch = new();
    private int cursor;
    private bool warnedBarren;

    /// <summary>Where the time goes, part by part, for the Diagnostics page.</summary>
    public Services.FrameTimes? Times { get; set; }

    /// <summary>Where the search may go, by world X/Z, or null for anywhere. Seeds are always taken.</summary>
    public Func<float, float, bool>? Bounds { get; set; }

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
        sides.Clear();
        sidesToTest.Clear();
        blockedSides.Clear();
        barrenSeeds.Clear();
        frontier.Clear();
        loading = null;
        cursor = 0;
        warnedBarren = false;
        Progress = 1f;
        progressAt = 0;
        progressScan = null;
        Dirty = false;
        Revision++;
    }

    /// <summary>Starts over for a territory, picking up what was saved for it before.</summary>
    public void Begin(uint territory, string? file, float cell)
    {
        Clear();
        Territory = territory;
        Cell = cell;

        if (file is null || !File.Exists(file)) return;

        // A save of this same area may still be on its way to disk; read what it wrote, not before.
        var previous = saving;
        loading = Task.Run(() =>
        {
            try
            {
                previous.Wait(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, "Couldn't finish saving explored floor");
            }
            return Load(file, territory, cell);
        });
    }

    /// <summary>Takes in what was read in the background, once it's ready. False while it's still being read.</summary>
    private bool FinishLoading()
    {
        if (loading is null) return true;
        if (!loading.IsCompleted) return false;

        var loaded = loading.IsCompletedSuccessfully ? loading.Result : null;
        loading = null;
        if (loaded is null) return true;

        cells = loaded.Cells;
        sides = loaded.Sides;
        blockedSides = loaded.BlockedSides;
        sidesToTest = loaded.SidesToTest;
        frontier = loaded.Frontier;
        cursor = 0;
        Revision++;
        Plugin.Log.Debug("Loaded {Count} explored cells for territory {Territory}", cells.Count, Territory);
        return true;
    }

    /// <summary>
    /// Spends up to <paramref name="budgetMs"/> on the search. Seeds are places known to be
    /// walkable; <paramref name="reach"/> limits the search to around the player, or 0 for none.
    /// </summary>
    public void Update(Vector3 player, IReadOnlyList<Vector3> seeds, float reach, double budgetMs)
    {
        if (!FinishLoading()) return;

        watch.Restart();
        var start = Services.FrameTimes.Start();

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
        Times?.Add("explorer seeds", start);
        start = Services.FrameTimes.Start();

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
        Times?.Add("explorer search", start);
        start = Services.FrameTimes.Start();

        // Spare time goes to the sides between known floor, where thin walls hide.
        while (sidesToTest.Count > 0 && watch.Elapsed.TotalMilliseconds < budgetMs)
            TestSides(sidesToTest.Dequeue());
        Times?.Add("explorer sides", start);

        var now = Environment.TickCount64 / 1000.0;
        if (progressScan is not null || now >= progressAt)
        {
            start = Services.FrameTimes.Start();
            UpdateProgress(player, reach);
            Times?.Add("explorer progress", start);
            if (progressScan is null)
                progressAt = now + 0.5;
        }
    }

    /// <summary>A count of the frontier near the player, carried over several frames.</summary>
    private sealed class ProgressScan
    {
        public Vector3 Player;
        public float ReachSq;
        public int Found;
        public int Waiting;
        public int Index;
    }

    private ProgressScan? progressScan;
    /// <summary>Time the progress count may take per frame: the frontier of a big field runs to millions.</summary>
    private const double ProgressBudgetMs = 0.4;

    private void UpdateProgress(Vector3 player, float reach)
    {
        if (reach <= 0f)
        {
            Progress = cells.Count + frontier.Count == 0 ? 1f : cells.Count / (float)(cells.Count + frontier.Count);
            return;
        }

        // A count already under way carries on from where the last frame left it, around where the
        // player was when it began: close enough for a progress bar.
        var scan = progressScan ??= StartProgress(player, reach);
        var counted = 0;
        var started = Stopwatch.GetTimestamp();
        var cell = Cell;
        while (scan.Index < frontier.Count)
        {
            var p = frontier[scan.Index++];
            var dx = (p.X + 0.5f) * cell - scan.Player.X;
            var dz = (p.Z + 0.5f) * cell - scan.Player.Z;
            if (dx * dx + dz * dz <= scan.ReachSq)
                scan.Waiting++;
            if (++counted % 4096 == 0 && Stopwatch.GetElapsedTime(started).TotalMilliseconds > ProgressBudgetMs)
                return;
        }

        Progress = scan.Found + scan.Waiting == 0 ? 1f : scan.Found / (float)(scan.Found + scan.Waiting);
        progressScan = null;
    }

    /// <summary>Begins a progress count: the floor already found in reach, then the frontier, a frame at a time.</summary>
    private ProgressScan StartProgress(Vector3 player, float reach)
    {
        // With a reach, only what's around the player counts: the rest waits for them to go there.
        var reachSq = reach * reach;
        bool Near(int x, int z)
        {
            var dx = (x + 0.5f) * Cell - player.X;
            var dz = (z + 0.5f) * Cell - player.Z;
            return dx * dx + dz * dz <= reachSq;
        }

        // Walk the grid in reach rather than every cell: stays cheap however much has been explored.
        // A wide reach is sampled every few cells, which is plenty for a progress bar.
        var span = (int)MathF.Ceiling(reach / Cell);
        var stride = Math.Max(1, span / 40);
        var px = Index(player.X);
        var pz = Index(player.Z);
        var found = 0;
        for (var z = pz - span; z <= pz + span; z += stride)
            for (var x = px - span; x <= px + span; x += stride)
                if (Near(x, z) && cells.ContainsKey(Key(x, z)))
                    found++;
        found *= stride * stride;

        return new ProgressScan { Player = player, ReachSq = reachSq, Found = found };
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
        if (Bounds is { } bounds && !bounds((p.X + 0.5f) * Cell, (p.Z + 0.5f) * Cell)) return;
        if (!tried.Add((key, Level(p.FromHeight), Side(p)))) return;

        var feet = p.FromHeight;

        // A wall between the two cells: this one waits for a path that isn't blocked. Whether
        // it's a wall worth drawing is for the fuller side test, once both cells are floor.
        if (WallBetween(p.FromX, p.FromZ, p.X, p.Z, feet, 0f))
            return;
        Record(p.FromX, p.FromZ, p.X, p.Z, blocked: false);

        if (Ground(p.X, p.Z, feet) is { } ground)
            AddFloor(p.X, p.Z, ground);
    }

    /// <summary>Which way a step comes into its cell, so steps from different sides are told apart.</summary>
    private static int Side(Pending p) => (p.X - p.FromX + 1) * 3 + (p.Z - p.FromZ + 1);

    /// <summary>
    /// Whether something stands between two neighbouring cells, for someone at
    /// <paramref name="feet"/>, on a ray <paramref name="offset"/> cells to the side of their middles.
    /// </summary>
    private bool WallBetween(int fromX, int fromZ, int toX, int toZ, float feet, float offset)
    {
        // Sideways to the step: along z for a step in x, along x for a step in z.
        var shift = (toX != fromX ? new Vector3(0f, 0f, offset) : new Vector3(offset, 0f, 0f)) * Cell;
        var from = new Vector3((fromX + 0.5f) * Cell, feet, (fromZ + 0.5f) * Cell) + shift;
        var to = new Vector3((toX + 0.5f) * Cell, feet, (toZ + 0.5f) * Cell) + shift;
        var direction = Vector3.Normalize(to - from);

        if (Cast(from + new Vector3(0f, ChestHeight, 0f), direction, Cell, out _))
            return true;
        if (!Cast(from + new Vector3(0f, KneeHeight, 0f), direction, Cell, out _))
            return false;

        // Something knee high: a fence or a low wall if the ground stays level past it, a step otherwise.
        return Ground(toX, toZ, feet) is { } ground && MathF.Abs(ground - feet) <= FlatGround;
    }

    /// <summary>The floor of a cell within a step of <paramref name="feet"/>, or null.</summary>
    private float? Ground(int x, int z, float feet)
    {
        // Down from just above step height: low enough to pass under whatever is overhead.
        var start = feet + MaxStep + 0.1f;
        if (Cast(new Vector3((x + 0.5f) * Cell, start, (z + 0.5f) * Cell), -Vector3.UnitY, MaxStep * 2f + 0.1f, out var ground)
            && Walkable(ground)
            && MathF.Abs(ground.Point.Y - feet) <= MaxStep)
            return ground.Point.Y;
        return null;
    }

    /// <summary>Tests the east and south sides of a floor cell, where its neighbour there is floor too.</summary>
    private void TestSides(long key)
    {
        if (!cells.TryGetValue(key, out var levels)) return;
        var (x, z) = Unpack(key);
        if (Bounds is { } bounds && !bounds((x + 0.5f) * Cell, (z + 0.5f) * Cell)) return;
        sides.TryGetValue(key, out var known);

        if ((known & Sides.EastTested) == 0)
            TestSide(x, z, x + 1, z, levels);
        if ((known & Sides.SouthTested) == 0)
            TestSide(x, z, x, z + 1, levels);
    }

    private void TestSide(int x, int z, int nx, int nz, float[] levels)
    {
        // Left untested until the neighbour is floor; it queues this cell again when it becomes so.
        if (!cells.TryGetValue(Key(nx, nz), out var other)) return;

        foreach (var h in levels)
        {
            foreach (var o in other)
            {
                if (MathF.Abs(o - h) > MaxStep) continue;
                Record(x, z, nx, nz, WallAcross(x, z, nx, nz, h));
                return;
            }
        }
    }

    /// <summary>
    /// Whether a side between two floor cells is walled all along: blocked on every test ray, not
    /// just where one grazes a door frame, a post or the edge of an opening.
    /// </summary>
    private bool WallAcross(int x, int z, int nx, int nz, float feet)
    {
        foreach (var offset in SideOffsets)
            if (!WallBetween(x, z, nx, nz, feet, offset))
                return false;
        return true;
    }

    /// <summary>Notes what's between two neighbouring cells, the first time it's tested.</summary>
    private void Record(int ax, int az, int bx, int bz, bool blocked)
    {
        // Each side belongs to the cell west or north of it.
        var east = az == bz;
        var key = east ? Key(Math.Min(ax, bx), az) : Key(ax, Math.Min(az, bz));
        sides.TryGetValue(key, out var known);
        var tested = east ? Sides.EastTested : Sides.SouthTested;
        if ((known & tested) != 0) return;

        var now = known | tested;
        if (blocked)
        {
            now |= east ? Sides.EastBlocked : Sides.SouthBlocked;
            var (x, z) = Unpack(key);
            blockedSides.Add(new CellSide(x, z, east));
            Revision++;
        }
        sides[key] = now;
        Dirty = true;
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

        // Its own sides, and the ones its west and north neighbours share with it.
        sidesToTest.Enqueue(key);
        sidesToTest.Enqueue(Key(x - 1, z));
        sidesToTest.Enqueue(Key(x, z - 1));

        Queue(x + 1, z, height, x, z);
        Queue(x - 1, z, height, x, z);
        Queue(x, z + 1, height, x, z);
        Queue(x, z - 1, height, x, z);
    }

    private void Queue(int x, int z, float height, int fromX, int fromZ)
    {
        var key = Key(x, z);
        var step = new Pending(x, z, height, fromX, fromZ);
        if (HasLevel(key, height, out _) || tried.Contains((key, Level(height), Side(step)))) return;
        frontier.Add(step);
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
            yield return Unpack(key);
    }

    /// <summary>
    /// A copy of the walkable cells as packed keys (see <see cref="Unpack"/>), safe to hand to a
    /// worker thread. A plain bulk copy: with a million cells it still takes a few milliseconds,
    /// where unpacking them here would stall the game.
    /// </summary>
    public long[] Snapshot()
    {
        var result = new long[cells.Count];
        cells.Keys.CopyTo(result, 0);
        return result;
    }

    /// <summary>
    /// Blocked sides between cells: walls too thin to leave a gap in the floor. Each is the east
    /// or south side of the cell given. Sides whose cells aren't both floor are for the drawing to
    /// skip, against the floor it actually draws.
    /// </summary>
    public CellSide[] Walls() => blockedSides.ToArray();

    // ------------------------------------------------------------------
    // Saving
    // ------------------------------------------------------------------

    /// <summary>
    /// Saves what's been found. The data is copied here in bulk and written to disk in the
    /// background, so a big area doesn't stall the game while the file is written. Level arrays are
    /// never changed once stored (a new level makes a new array), so sharing them is safe.
    /// </summary>
    public void Save(string file)
    {
        var cellCopy = new KeyValuePair<long, float[]>[cells.Count];
        ((ICollection<KeyValuePair<long, float[]>>)cells).CopyTo(cellCopy, 0);
        var sideCopy = new KeyValuePair<long, Sides>[sides.Count];
        ((ICollection<KeyValuePair<long, Sides>>)sides).CopyTo(sideCopy, 0);
        var territory = Territory;
        var cell = Cell;
        Dirty = false;

        saving = saving.ContinueWith(_ => Write(file, territory, cell, cellCopy, sideCopy), TaskScheduler.Default);
    }

    /// <summary>Waits for a save still being written, for when the plugin unloads.</summary>
    public void FinishSaving(TimeSpan timeout)
    {
        try
        {
            saving.Wait(timeout);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Couldn't finish saving explored floor");
        }
    }

    private static void Write(string file, uint territory, float cell, KeyValuePair<long, float[]>[] cellCopy, KeyValuePair<long, Sides>[] sideCopy)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);

            // Written next to the file and swapped in, so a crash mid-write never leaves half a file.
            var temp = file + ".tmp";
            using (var stream = new BufferedStream(File.Create(temp), 1 << 16))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(FileMagic);
                writer.Write(FileVersion);
                writer.Write(territory);
                writer.Write(cell);
                writer.Write(cellCopy.Length);
                foreach (var (key, levels) in cellCopy)
                {
                    writer.Write(key);
                    var count = Math.Min(levels.Length, 255);
                    writer.Write((byte)count);
                    for (var i = 0; i < count; i++)
                        writer.Write(levels[i]);
                }

                writer.Write(sideCopy.Length);
                foreach (var (key, known) in sideCopy)
                {
                    writer.Write(key);
                    writer.Write((byte)known);
                }
            }

            File.Move(temp, file, overwrite: true);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Couldn't save explored floor to {File}", file);
        }
    }

    /// <summary>Everything read back from a save, ready to be swapped in.</summary>
    private sealed record Loaded(
        Dictionary<long, float[]> Cells,
        Dictionary<long, Sides> Sides,
        List<CellSide> BlockedSides,
        Queue<long> SidesToTest,
        List<Pending> Frontier);

    /// <summary>Reads a save. Touches nothing of the explorer's own, so it can run in the background.</summary>
    private static Loaded? Load(string file, uint territory, float cellSize)
    {
        try
        {
            using var stream = File.OpenRead(file);
            using var reader = new BinaryReader(stream);
            if (reader.ReadInt32() != FileMagic)
                return null;
            // Older versions have the same floor; their sides (none in 2, tested with a single ray
            // in 3) are tested again.
            var version = reader.ReadInt32();
            if (version is not (2 or 3 or FileVersion) || reader.ReadUInt32() != territory)
                return null;

            // Cells of another size don't line up with this grid; start over rather than mix them.
            if (MathF.Abs(reader.ReadSingle() - cellSize) > 1e-3f)
                return null;

            var count = reader.ReadInt32();
            var cells = new Dictionary<long, float[]>(count, CellKeys.Instance);
            for (var i = 0; i < count; i++)
            {
                var key = reader.ReadInt64();
                var levels = new float[reader.ReadByte()];
                for (var j = 0; j < levels.Length; j++)
                    levels[j] = reader.ReadSingle();
                cells[key] = levels;
            }

            var sides = new Dictionary<long, Sides>(CellKeys.Instance);
            var blockedSides = new List<CellSide>();
            if (version >= FileVersion)
            {
                var sideCount = reader.ReadInt32();
                sides.EnsureCapacity(sideCount);
                for (var i = 0; i < sideCount; i++)
                {
                    var key = reader.ReadInt64();
                    var known = (Sides)reader.ReadByte();
                    sides[key] = known;
                    var (x, z) = Unpack(key);
                    if ((known & Sides.EastBlocked) != 0)
                        blockedSides.Add(new CellSide(x, z, East: true));
                    if ((known & Sides.SouthBlocked) != 0)
                        blockedSides.Add(new CellSide(x, z, East: false));
                }
            }

            // Pick up where it left off: every edge of what's known is worth one more look.
            var sidesToTest = new Queue<long>(cells.Count);
            var frontier = new List<Pending>();
            foreach (var (key, levels) in cells)
            {
                var (x, z) = Unpack(key);
                sidesToTest.Enqueue(key);
                foreach (var h in levels)
                {
                    Edge(x + 1, z, h, x, z);
                    Edge(x - 1, z, h, x, z);
                    Edge(x, z + 1, h, x, z);
                    Edge(x, z - 1, h, x, z);
                }
            }

            return new Loaded(cells, sides, blockedSides, sidesToTest, frontier);

            // Nothing has been tried yet, so only cells already known on that level are left out.
            void Edge(int x, int z, float height, int fromX, int fromZ)
            {
                if (!(cells.TryGetValue(Key(x, z), out var known) && HasLevel(known, height)))
                    frontier.Add(new Pending(x, z, height, fromX, fromZ));
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Couldn't read explored floor from {File}", file);
            return null;
        }
    }

    public int Index(float world) => (int)MathF.Floor(world / Cell);

    private static long Key(int x, int z) => (long)x << 32 | (uint)z;

    public static (int X, int Z) Unpack(long key) => ((int)(key >> 32), (int)(key & 0xFFFFFFFF));
}

/// <summary>
/// Hashing for cell keys. A key is X and Z packed into a long, and a long's own hash is its two
/// halves XORed, X ^ Z: every cell along a diagonal lands on the same hash, so a big field's
/// hundreds of thousands of cells pile into a few thousand buckets and every lookup crawls.
/// Mixing the bits spreads them out.
/// </summary>
internal sealed class CellKeys : IEqualityComparer<long>, IEqualityComparer<(long Cell, int Level)>, IEqualityComparer<(long Cell, int Level, int From)>
{
    public static readonly CellKeys Instance = new();

    private static int Mix(long key)
    {
        var h = (ulong)key * 0x9E3779B97F4A7C15UL;
        return (int)(h ^ (h >> 32));
    }

    public bool Equals(long a, long b) => a == b;
    public int GetHashCode(long key) => Mix(key);

    public bool Equals((long Cell, int Level) a, (long Cell, int Level) b) => a == b;
    public int GetHashCode((long Cell, int Level) key) => HashCode.Combine(Mix(key.Cell), key.Level);

    public bool Equals((long Cell, int Level, int From) a, (long Cell, int Level, int From) b) => a == b;
    public int GetHashCode((long Cell, int Level, int From) key) => HashCode.Combine(Mix(key.Cell), key.Level, key.From);
}

/// <summary>A blocked east (or south) side of explorer cell X, Z.</summary>
public readonly record struct CellSide(int X, int Z, bool East);

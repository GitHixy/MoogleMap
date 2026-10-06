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
    private readonly Dictionary<long, float[]> cells = new();
    /// <summary>
    /// Steps already tried, by target cell, the level they came from and the side they came in by,
    /// so none is cast twice. A cell blocked from one side is still tried from the others.
    /// </summary>
    private readonly HashSet<(long Cell, int Level, int From)> tried = [];
    /// <summary>What's known about the sides between cells, by the cell west or north of each.</summary>
    private readonly Dictionary<long, Sides> sides = new();
    /// <summary>Floor cells whose sides with their floor neighbours still need testing.</summary>
    private readonly Queue<long> sidesToTest = new();
    /// <summary>Seeds that found no ground (someone flying, jumping or on a mount), not worth casting for again.</summary>
    private readonly HashSet<(long Cell, int Level)> barrenSeeds = [];
    private readonly List<Pending> frontier = [];
    private readonly Stopwatch watch = new();
    private int cursor;
    private bool warnedBarren;

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

        // Spare time goes to the sides between known floor, where thin walls hide.
        while (sidesToTest.Count > 0 && watch.Elapsed.TotalMilliseconds < budgetMs)
            TestSides(sidesToTest.Dequeue());

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

    /// <summary>A copy of the walkable cells, safe to hand to a worker thread.</summary>
    public (int X, int Z)[] Snapshot()
    {
        var result = new (int X, int Z)[cells.Count];
        var i = 0;
        foreach (var key in cells.Keys)
            result[i++] = Unpack(key);
        return result;
    }

    /// <summary>
    /// Blocked sides between two floor cells: walls too thin to leave a gap in the floor. Each is
    /// the east or south side of the cell given.
    /// </summary>
    public CellSide[] Walls()
    {
        var result = new List<CellSide>();
        foreach (var (key, known) in sides)
        {
            if ((known & (Sides.EastBlocked | Sides.SouthBlocked)) == 0 || !cells.ContainsKey(key)) continue;
            var (x, z) = Unpack(key);
            if ((known & Sides.EastBlocked) != 0 && cells.ContainsKey(Key(x + 1, z)))
                result.Add(new CellSide(x, z, East: true));
            if ((known & Sides.SouthBlocked) != 0 && cells.ContainsKey(Key(x, z + 1)))
                result.Add(new CellSide(x, z, East: false));
        }
        return result.ToArray();
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

            writer.Write(sides.Count);
            foreach (var (key, known) in sides)
            {
                writer.Write(key);
                writer.Write((byte)known);
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
            if (reader.ReadInt32() != FileMagic)
                return;
            // Older versions have the same floor; their sides (none in 2, tested with a single ray
            // in 3) are tested again.
            var version = reader.ReadInt32();
            if (version is not (2 or 3 or FileVersion) || reader.ReadUInt32() != Territory)
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

            if (version >= FileVersion)
            {
                var sideCount = reader.ReadInt32();
                for (var i = 0; i < sideCount; i++)
                {
                    var key = reader.ReadInt64();
                    sides[key] = (Sides)reader.ReadByte();
                }
            }

            // Pick up where it left off: every edge of what's known is worth one more look.
            foreach (var (key, levels) in cells)
            {
                var (x, z) = Unpack(key);
                sidesToTest.Enqueue(key);
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
            sides.Clear();
            sidesToTest.Clear();
        }
    }

    public int Index(float world) => (int)MathF.Floor(world / Cell);

    private static long Key(int x, int z) => (long)x << 32 | (uint)z;

    private static (int X, int Z) Unpack(long key) => ((int)(key >> 32), (int)(key & 0xFFFFFFFF));
}

/// <summary>A blocked east (or south) side of explorer cell X, Z.</summary>
public readonly record struct CellSide(int X, int Z, bool East);

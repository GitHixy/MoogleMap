using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using MoogleMap.Map;
using MoogleMap.Models;

namespace MoogleMap.Rendering;

/// <summary>
/// The explored floor as rectangles and wall lines, merged along rows so a big room is a handful
/// of quads rather than hundreds of cells. Rebuilt only when the explorer has found something new.
/// </summary>
public sealed class ExploredMesh
{
    /// <summary>Longest merged run, in cells. Short enough that the rim fade still looks smooth.</summary>
    private const int MaxRun = 6;

    private readonly List<(Vector2 Min, Vector2 Max)> fills = [];
    private readonly List<(Vector2 A, Vector2 B)> edges = [];
    private int builtRevision = -1;
    private long builtAt;

    public void Invalidate() => builtRevision = -1;

    public void Draw(ImDrawListPtr dl, MapView view, Explorer explorer, Configuration config, float mapOpacity)
    {
        // While exploring the floor grows nearly every frame; a few rebuilds a second look the same.
        var now = Environment.TickCount64;
        if (builtRevision != explorer.Revision && (builtRevision < 0 || now - builtAt > 150))
        {
            builtAt = now;
            Rebuild(explorer);
        }

        var floor = config.FloorColor;
        var edge = config.EdgeColor;
        var opacity = view.Alpha * mapOpacity;
        var thickness = config.WallThickness * view.PixelScale;

        foreach (var (min, max) in fills)
        {
            var a = view.ToScreen(min);
            var b = view.ToScreen(new Vector2(max.X, min.Y));
            var c = view.ToScreen(max);
            var d = view.ToScreen(new Vector2(min.X, max.Y));
            var alpha = view.Fade((a + c) * 0.5f) * opacity;
            if (alpha <= 0.005f) continue;
            dl.AddQuadFilled(a, b, c, d, Draw2D.Color(floor, alpha));
        }

        foreach (var (p, q) in edges)
        {
            var a = view.ToScreen(p);
            var b = view.ToScreen(q);
            var alpha = view.Fade((a + b) * 0.5f) * opacity;
            if (alpha <= 0.005f) continue;
            if (config.WallGlow)
                dl.AddLine(a, b, Draw2D.Color(edge, alpha * 0.22f), thickness * 3.2f);
            dl.AddLine(a, b, Draw2D.Color(edge, alpha), thickness);
        }
    }

    /// <summary>Cell size of the explorer data the mesh was built from.</summary>
    private float cellSize = 1f;

    private void Rebuild(Explorer explorer)
    {
        cellSize = explorer.Cell;
        builtRevision = explorer.Revision;
        fills.Clear();
        edges.Clear();

        var cells = new HashSet<(int X, int Z)>(explorer.Floor());
        if (cells.Count == 0) return;

        var rows = new SortedDictionary<int, List<int>>();
        foreach (var (x, z) in cells)
        {
            if (!rows.TryGetValue(z, out var list))
                rows[z] = list = [];
            list.Add(x);
        }

        var s = cellSize;
        foreach (var (z, xs) in rows)
        {
            xs.Sort();

            // Floor: runs of neighbouring cells along the row.
            var start = xs[0];
            var prev = xs[0];
            for (var i = 1; i <= xs.Count; i++)
            {
                var x = i < xs.Count ? xs[i] : int.MinValue;
                if (x == prev + 1 && x - start < MaxRun)
                {
                    prev = x;
                    continue;
                }

                fills.Add((new Vector2(start * s, z * s), new Vector2((prev + 1) * s, (z + 1) * s)));
                start = prev = x;
            }

            // Walls above and below this row, merged the same way.
            AddRowEdges(cells, xs, z, z - 1, z * s);
            AddRowEdges(cells, xs, z, z + 1, (z + 1) * s);

            // Walls to the left and right of each cell.
            foreach (var x in xs)
            {
                if (!cells.Contains((x - 1, z)))
                    edges.Add((new Vector2(x * s, z * s), new Vector2(x * s, (z + 1) * s)));
                if (!cells.Contains((x + 1, z)))
                    edges.Add((new Vector2((x + 1) * s, z * s), new Vector2((x + 1) * s, (z + 1) * s)));
            }
        }
    }

    private void AddRowEdges(HashSet<(int X, int Z)> cells, List<int> xs, int z, int neighbourZ, float lineZ)
    {
        var s = cellSize;
        int? runStart = null;
        var last = 0;

        foreach (var x in xs)
        {
            var open = !cells.Contains((x, neighbourZ));
            if (open && runStart is { } rs && x == last + 1 && x - rs < MaxRun)
            {
                last = x;
                continue;
            }

            if (runStart is { } done)
                edges.Add((new Vector2(done * s, lineZ), new Vector2((last + 1) * s, lineZ)));

            runStart = open ? x : null;
            last = x;
        }

        if (runStart is { } tail)
            edges.Add((new Vector2(tail * s, lineZ), new Vector2((last + 1) * s, lineZ)));
    }
}

using System;
using System.Collections.Generic;
using System.Numerics;

namespace MoogleMap.Map;

/// <summary>
/// A processed map: the walkable floor as a fill texture, and its walls as lines in map texture
/// pixels, drawn on top at a fixed thickness so they stay sharp at any zoom.
/// </summary>
/// <param name="Traced">Floor traced from the map picture, kept so collision finds can be added to it later.</param>
public sealed record StylizedMap(int Size, byte[] Rgba, float PaperShare, float FloorShare, IReadOnlyList<WallLine> Walls, bool[] Traced)
{
    /// <summary>
    /// For towns: the area the picture draws as built-up, rooftops included. Explored floor only
    /// counts inside it, which keeps the seabed off the map while piers and docks stay on it.
    /// </summary>
    public bool[]? Footprint { get; init; }

    /// <summary>Whether any of the floor came from the picture rather than from exploring.</summary>
    public bool FromPicture { get; init; }
}

/// <summary>
/// Finds the walkable floor in a game map texture.
/// </summary>
/// <remarks>
/// Game maps are drawn on parchment. Dungeon, trial and raid maps keep that parchment perfectly
/// flat, and draw the floor as a slightly lighter, less yellow area hemmed in by a brown shadow
/// along every wall. That gives two independent ways to find the floor - its colour, and being
/// fenced in by the shadow - and each catches what the other misses, so both are used.
/// Town and field maps are painted edge to edge on mottled paper, and in them streets, plazas and
/// rooftops share the same pale tone, so nothing on the picture tells them apart. Those trace
/// nothing here; their floor comes from the game's collision instead, through <see cref="Fuse"/>.
/// Everything runs at half resolution, which is plenty for an overlay and four times cheaper.
/// </remarks>
public static class MapStylizer
{
    /// <summary>Below this share of identical paper pixels a map is painted, not drawn as a plan.</summary>
    private const float FlatPaperShare = 0.4f;

    /// <summary>
    /// Processes a BGRA texture. Pure CPU work with no game calls, so it is safe on a worker thread.
    /// </summary>
    /// <param name="openWorld">Open fields are painted edge to edge and walkable nearly everywhere; they get no footprint.</param>
    public static StylizedMap Run(byte[] bgra, int size, Vector4 colour, bool openWorld)
    {
        var n = size / 2;
        var (paper, share) = PaperColour(bgra);
        var r = new float[n * n];
        var g = new float[n * n];
        var b = new float[n * n];
        Downsample(bgra, size, r, g, b);

        if (share >= FlatPaperShare)
        {
            var floor = TraceFloor(r, g, b, n, paper);
            var count = Count(floor);

            // A trace that found next to nothing is a map that is mostly scenery; leave it to exploring.
            if (count > n * n / 2000)
                return Paint(floor, n, colour, share) with { FromPicture = true };
        }

        var empty = new StylizedMap(n, new byte[n * n * 4], share, 0f, [], new bool[n * n]);
        return openWorld ? empty : empty with { Footprint = TraceFootprint(r, g, b, n) };
    }

    /// <summary>
    /// Adds floor found from the game's collision to a processed map, and redraws fill and walls
    /// from the union. Always pass the map as it came from <see cref="Run"/> with every cell found
    /// so far, not a previous fusion.
    /// </summary>
    /// <param name="cells">Walkable cells in world space, <paramref name="cell"/> yalms square.</param>
    public static StylizedMap Fuse(StylizedMap source, MapInfo map, IReadOnlyList<(int X, int Z)> cells, float cell, Vector4 colour)
    {
        var n = source.Size;
        var floor = (bool[])source.Traced.Clone();
        var toProcessed = (float)n / MapInfo.TextureSize;

        var footprint = source.Footprint;
        var added = 0;
        foreach (var (cx, cz) in cells)
        {
            // Cover the whole cell, however many processed pixels it spans at this map's scale.
            var a = map.WorldToTexture(new Vector3(cx * cell, 0f, cz * cell)) * toProcessed;
            var b = map.WorldToTexture(new Vector3((cx + 1) * cell, 0f, (cz + 1) * cell)) * toProcessed;
            var x0 = Math.Max(0, (int)MathF.Floor(a.X));
            var y0 = Math.Max(0, (int)MathF.Floor(a.Y));
            var x1 = Math.Min(n - 1, Math.Max(x0, (int)MathF.Ceiling(b.X) - 1));
            var y1 = Math.Min(n - 1, Math.Max(y0, (int)MathF.Ceiling(b.Y) - 1));
            for (var y = y0; y <= y1; y++)
                for (var x = x0; x <= x1; x++)
                {
                    var k = y * n + x;
                    if (floor[k] || (footprint is not null && !footprint[k])) continue;
                    floor[k] = true;
                    added++;
                }
        }

        if (added == 0)
            return source;

        // Cells are coarse next to the picture; closing seals the seams between them, and filling
        // small holes swallows benches, stalls and fountains, which aren't walls.
        floor = Erode(Dilate(floor, n, 1), n, 1);
        Fill(floor, n, target: false, maxArea: Math.Max(60, (int)(10f * map.Scale * map.Scale)));

        return Paint(floor, n, colour, source.PaperShare) with { FromPicture = source.FromPicture, Footprint = source.Footprint };
    }

    // ------------------------------------------------------------------
    // Tracing
    // ------------------------------------------------------------------

    private static bool[] TraceFloor(float[] r, float[] g, float[] b, int n, Vector3 paper)
    {
        var len = n * n;
        var paperSum = paper.X + paper.Y + paper.Z;
        var paperYellow = (paper.X + paper.Y) / 2f - paper.Z;

        // Floor reads as lighter or less yellow than the paper, wall shadow as darker and browner.
        // Arena maps draw a small framed map in the middle and leave the rest pure black; that
        // black is neither paper nor wall.
        var blank = new bool[len];
        var whiteness = new float[len];
        var darkness = new float[len];
        for (var k = 0; k < len; k++)
        {
            var sum = r[k] + g[k] + b[k];
            if (sum < 24f)
            {
                blank[k] = true;
                whiteness[k] = -50f;
                continue;
            }

            whiteness[k] = paperYellow - ((r[k] + g[k]) / 2f - b[k]) + (sum - paperSum) / 3f;
            darkness[k] = MathF.Max(0f, paperSum - sum);
        }

        whiteness = Blur(whiteness, n, 1);
        darkness = Blur(darkness, n, 2);

        var floor = new bool[len];
        var wall = new bool[len];
        for (var k = 0; k < len; k++)
        {
            floor[k] = !blank[k] && whiteness[k] > 6f;
            wall[k] = !floor[k] && !blank[k] && darkness[k] > 9f;
        }

        // Whatever the wall shadow fences off from the edge of the map is floor too. Blank
        // surroundings count as outside, so the fill flows through them.
        var outside = new bool[len];
        var queue = new Queue<int>();
        for (var i = 0; i < n; i++)
        {
            Seed(i);
            Seed((n - 1) * n + i);
            Seed(i * n);
            Seed(i * n + n - 1);
        }

        while (queue.Count > 0)
        {
            var k = queue.Dequeue();
            var x = k % n;
            if (x > 0) Seed(k - 1);
            if (x < n - 1) Seed(k + 1);
            if (k >= n) Seed(k - n);
            if (k < len - n) Seed(k + n);
        }

        for (var k = 0; k < len; k++)
            if (!wall[k] && !outside[k])
                floor[k] = true;

        // Close the gaps left by place names and hatching, then drop crumbs.
        floor = Erode(Dilate(floor, n, 3), n, 3);
        floor = Dilate(Erode(floor, n, 1), n, 1);
        Fill(floor, n, target: false, maxArea: 900);
        Fill(floor, n, target: true, maxArea: 40);
        return floor;

        void Seed(int k)
        {
            if (wall[k] || outside[k]) return;
            outside[k] = true;
            queue.Enqueue(k);
        }
    }

    /// <summary>
    /// Where a town map draws its town: paler than the paper around it, with everything enclosed
    /// by that (parks, courtyards, rooftops) filled in, and a few pixels of slack at the edge so
    /// the explorer's coarse cells along a quay aren't clipped. Null when the picture doesn't draw
    /// the town as one coherent shape: clipping by a patchy footprint would cut real streets.
    /// </summary>
    private static bool[]? TraceFootprint(float[] r, float[] g, float[] b, int n)
    {
        const int reach = 24;
        var len = n * n;
        var blank = new bool[len];
        for (var k = 0; k < len; k++)
            blank[k] = r[k] + g[k] + b[k] < 24f;

        var rb = PaperBlur(r, blank, n, reach);
        var gb = PaperBlur(g, blank, n, reach);
        var bb = PaperBlur(b, blank, n, reach);

        var whiteness = new float[len];
        for (var k = 0; k < len; k++)
        {
            if (blank[k]) continue;
            var paperYellow = (rb[k] + gb[k]) / 2f - bb[k];
            var yellow = (r[k] + g[k]) / 2f - b[k];
            whiteness[k] = paperYellow - yellow + (r[k] + g[k] + b[k] - rb[k] - gb[k] - bb[k]) / 3f;
        }
        whiteness = Blur(whiteness, n, 2);

        var footprint = new bool[len];
        for (var k = 0; k < len; k++)
            footprint[k] = whiteness[k] > 5f;

        footprint = Erode(Dilate(footprint, n, 4), n, 4);
        Fill(footprint, n, target: false, maxArea: len / 8);

        // Mottled paper leaves specks everywhere; only big pieces are town.
        var (labels, areas) = Components(footprint, n);
        var largest = 0;
        var total = 0;
        foreach (var area in areas)
        {
            largest = Math.Max(largest, area);
            if (area >= 300) total += area;
        }

        if (largest < len / 200 || largest < total / 2)
            return null;

        for (var k = 0; k < len; k++)
            footprint[k] = labels[k] >= 0 && areas[labels[k]] >= largest / 20;
        return Dilate(footprint, n, 3);
    }

    /// <summary>Labels 4-connected regions of set pixels; -1 elsewhere.</summary>
    private static (int[] Labels, List<int> Areas) Components(bool[] mask, int n)
    {
        var labels = new int[mask.Length];
        Array.Fill(labels, -1);
        var areas = new List<int>();
        var queue = new Queue<int>();

        for (var start = 0; start < mask.Length; start++)
        {
            if (!mask[start] || labels[start] >= 0) continue;

            var id = areas.Count;
            var area = 0;
            labels[start] = id;
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var k = queue.Dequeue();
                area++;
                var x = k % n;
                if (x > 0) Visit(k - 1);
                if (x < n - 1) Visit(k + 1);
                if (k >= n) Visit(k - n);
                if (k < mask.Length - n) Visit(k + n);
            }

            areas.Add(area);

            void Visit(int k)
            {
                if (!mask[k] || labels[k] >= 0) return;
                labels[k] = id;
                queue.Enqueue(k);
            }
        }

        return (labels, areas);
    }

    /// <summary>A wide blur of one channel that ignores blank pixels, so black surroundings don't darken the paper.</summary>
    private static float[] PaperBlur(float[] channel, bool[] blank, int n, int radius)
    {
        var len = n * n;
        var weighted = new float[len];
        var weight = new float[len];
        for (var k = 0; k < len; k++)
        {
            if (blank[k]) continue;
            weighted[k] = channel[k];
            weight[k] = 1f;
        }

        weighted = Blur(Blur(weighted, n, radius), n, radius);
        weight = Blur(Blur(weight, n, radius), n, radius);
        for (var k = 0; k < len; k++)
            weighted[k] = weight[k] > 1e-3f ? weighted[k] / weight[k] : channel[k];
        return weighted;
    }

    /// <summary>The floor fill texture, plus the walls traced from its softened edge.</summary>
    private static StylizedMap Paint(bool[] floor, int n, Vector4 colour, float paperShare)
    {
        var len = n * n;
        var fill = new float[len];
        for (var k = 0; k < len; k++)
            fill[k] = floor[k] ? 1f : 0f;

        // A one pixel blur takes the stair-steps off curved walls, and gives the tracer sub-pixel edges.
        fill = Blur(fill, n, 1);

        var output = new byte[len * 4];
        var (cr, cg, cb) = (ToByte(colour.X), ToByte(colour.Y), ToByte(colour.Z));
        for (var k = 0; k < len; k++)
        {
            var a = fill[k] * colour.W;
            if (a <= 0.002f) continue;

            var o = k * 4;
            output[o] = cr;
            output[o + 1] = cg;
            output[o + 2] = cb;
            output[o + 3] = ToByte(a);
        }

        var walls = WallTracer.Trace(fill, n, MapInfo.TextureSize / n);
        return new StylizedMap(n, output, paperShare, Count(floor) / (float)len, walls, floor);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static void Downsample(byte[] bgra, int size, float[] r, float[] g, float[] b)
    {
        var n = size / 2;
        for (var y = 0; y < n; y++)
        {
            var row0 = y * 2 * size * 4;
            var row1 = row0 + size * 4;
            for (var x = 0; x < n; x++)
            {
                var i0 = row0 + x * 8;
                var i1 = row1 + x * 8;
                var k = y * n + x;
                b[k] = (bgra[i0] + bgra[i0 + 4] + bgra[i1] + bgra[i1 + 4]) * 0.25f;
                g[k] = (bgra[i0 + 1] + bgra[i0 + 5] + bgra[i1 + 1] + bgra[i1 + 5]) * 0.25f;
                r[k] = (bgra[i0 + 2] + bgra[i0 + 6] + bgra[i1 + 2] + bgra[i1 + 6]) * 0.25f;
            }
        }
    }

    /// <summary>The most common exact colour, and how much of the map it covers. Blank black isn't paper.</summary>
    private static (Vector3 Colour, float Share) PaperColour(byte[] bgra)
    {
        const int stride = 7;
        var counts = new Dictionary<int, int>();
        var samples = 0;
        for (var i = 0; i + 3 < bgra.Length; i += 4 * stride)
        {
            if (bgra[i] + bgra[i + 1] + bgra[i + 2] < 24) continue;
            var c = bgra[i] | bgra[i + 1] << 8 | bgra[i + 2] << 16;
            counts.TryGetValue(c, out var v);
            counts[c] = v + 1;
            samples++;
        }

        var best = 0;
        var bestCount = 0;
        foreach (var (c, v) in counts)
        {
            if (v <= bestCount) continue;
            best = c;
            bestCount = v;
        }

        return (new Vector3(best >> 16 & 255, best >> 8 & 255, best & 255), samples == 0 ? 0f : bestCount / (float)samples);
    }

    /// <summary>Flips enclosed regions of <paramref name="target"/> no bigger than maxArea to the other value.</summary>
    private static void Fill(bool[] mask, int n, bool target, int maxArea)
    {
        var seen = new bool[mask.Length];
        var queue = new Queue<int>();
        var region = new List<int>();

        for (var start = 0; start < mask.Length; start++)
        {
            if (mask[start] != target || seen[start]) continue;

            region.Clear();
            var touchesEdge = false;
            seen[start] = true;
            queue.Enqueue(start);

            while (queue.Count > 0)
            {
                var k = queue.Dequeue();
                region.Add(k);
                var x = k % n;
                var y = k / n;
                if (x == 0 || y == 0 || x == n - 1 || y == n - 1) touchesEdge = true;

                if (x > 0) Visit(k - 1);
                if (x < n - 1) Visit(k + 1);
                if (y > 0) Visit(k - n);
                if (y < n - 1) Visit(k + n);
            }

            // Holes have to be enclosed; crumbs are dropped wherever they are.
            if (region.Count <= maxArea && (target || !touchesEdge))
                foreach (var k in region)
                    mask[k] = !target;
        }

        void Visit(int k)
        {
            if (mask[k] != target || seen[k]) return;
            seen[k] = true;
            queue.Enqueue(k);
        }
    }

    /// <summary>Separable running box blur, clamped at the borders.</summary>
    private static float[] Blur(float[] src, int n, int radius)
    {
        var tmp = new float[src.Length];
        var dst = new float[src.Length];
        var inv = 1f / (2 * radius + 1);

        for (var y = 0; y < n; y++)
        {
            var row = y * n;
            var acc = 0f;
            for (var d = -radius; d <= radius; d++)
                acc += src[row + Math.Clamp(d, 0, n - 1)];
            for (var x = 0; x < n; x++)
            {
                tmp[row + x] = acc * inv;
                acc += src[row + Math.Min(x + radius + 1, n - 1)] - src[row + Math.Max(x - radius, 0)];
            }
        }

        for (var x = 0; x < n; x++)
        {
            var acc = 0f;
            for (var d = -radius; d <= radius; d++)
                acc += tmp[Math.Clamp(d, 0, n - 1) * n + x];
            for (var y = 0; y < n; y++)
            {
                dst[y * n + x] = acc * inv;
                acc += tmp[Math.Min(y + radius + 1, n - 1) * n + x] - tmp[Math.Max(y - radius, 0) * n + x];
            }
        }

        return dst;
    }

    private static bool[] Dilate(bool[] mask, int n, int radius)
    {
        var tmp = new bool[mask.Length];
        var dst = new bool[mask.Length];

        for (var y = 0; y < n; y++)
        {
            var row = y * n;
            for (var x = 0; x < n; x++)
            {
                var hit = false;
                for (var d = Math.Max(0, x - radius); d <= Math.Min(n - 1, x + radius) && !hit; d++)
                    hit = mask[row + d];
                tmp[row + x] = hit;
            }
        }

        for (var x = 0; x < n; x++)
        {
            for (var y = 0; y < n; y++)
            {
                var hit = false;
                for (var d = Math.Max(0, y - radius); d <= Math.Min(n - 1, y + radius) && !hit; d++)
                    hit = tmp[d * n + x];
                dst[y * n + x] = hit;
            }
        }

        return dst;
    }

    private static bool[] Erode(bool[] mask, int n, int radius)
    {
        var inverted = new bool[mask.Length];
        for (var i = 0; i < mask.Length; i++) inverted[i] = !mask[i];
        var grown = Dilate(inverted, n, radius);
        for (var i = 0; i < grown.Length; i++) grown[i] = !grown[i];
        return grown;
    }

    private static int Count(bool[] mask)
    {
        var count = 0;
        foreach (var m in mask)
            if (m) count++;
        return count;
    }

    private static byte ToByte(float v) => (byte)Math.Clamp((int)(v * 255f + 0.5f), 0, 255);
}

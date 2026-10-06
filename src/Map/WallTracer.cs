using System;
using System.Collections.Generic;
using System.Numerics;

namespace MoogleMap.Map;

/// <summary>A wall as a line through map texture pixels, with its bounds for quick culling.</summary>
public sealed record WallLine(Vector2[] Points, Vector2 Min, Vector2 Max, bool Closed);

/// <summary>
/// Turns floor coverage into wall lines with marching squares: the contour at half coverage,
/// placed between pixels by interpolation, so edges come out smooth instead of stair-stepped.
/// Lines are then thinned to the points that actually bend them.
/// </summary>
public static class WallTracer
{
    private const float Level = 0.5f;
    /// <summary>How far a dropped point may sit from the simplified line, in processed pixels.</summary>
    private const float Tolerance = 0.45f;
    /// <summary>Loops shorter than this, in processed pixels, are specks, not walls.</summary>
    private const float MinLength = 10f;

    private readonly record struct Segment(int EdgeA, int EdgeB);

    /// <param name="field">Coverage, 0 outside to 1 on the floor, n by n.</param>
    /// <param name="scale">Multiplier from processed pixels to map texture pixels.</param>
    public static List<WallLine> Trace(float[] field, int n, float scale)
    {
        var points = new Dictionary<int, Vector2>();
        var segments = new List<Segment>();
        var byEdge = new Dictionary<int, (int First, int Second)>();

        for (var y = 0; y < n - 1; y++)
        {
            for (var x = 0; x < n - 1; x++)
            {
                var tl = field[y * n + x];
                var tr = field[y * n + x + 1];
                var br = field[(y + 1) * n + x + 1];
                var bl = field[(y + 1) * n + x];

                var index = (tl >= Level ? 8 : 0) | (tr >= Level ? 4 : 0) | (br >= Level ? 2 : 0) | (bl >= Level ? 1 : 0);
                if (index is 0 or 15) continue;

                var top = Horizontal(x, y, n);
                var bottom = Horizontal(x, y + 1, n);
                var left = Vertical(x, y, n);
                var right = Vertical(x + 1, y, n);

                switch (index)
                {
                    case 1 or 14: Add(left, bottom); break;
                    case 2 or 13: Add(bottom, right); break;
                    case 3 or 12: Add(left, right); break;
                    case 4 or 11: Add(top, right); break;
                    case 6 or 9: Add(top, bottom); break;
                    case 7 or 8: Add(top, left); break;
                    case 5 or 10:
                    {
                        // Saddle: the centre decides which corners belong together.
                        var centreInside = (tl + tr + br + bl) * 0.25f >= Level;
                        var cutTopLeft = index == 5 ? centreInside : !centreInside;
                        if (cutTopLeft)
                        {
                            Add(top, left);
                            Add(bottom, right);
                        }
                        else
                        {
                            Add(top, right);
                            Add(bottom, left);
                        }
                        break;
                    }
                }

                void Add(int a, int b)
                {
                    Place(a);
                    Place(b);
                    var id = segments.Count;
                    segments.Add(new Segment(a, b));
                    Link(a, id);
                    Link(b, id);
                }
            }
        }

        return Chain(segments, byEdge, points, scale);

        void Place(int edge)
        {
            if (points.ContainsKey(edge)) return;

            var ex = edge / 2 % n;
            var ey = edge / 2 / n;
            Vector2 p;
            if ((edge & 1) == 0)
            {
                // Between (ex, ey) and (ex + 1, ey).
                var a = field[ey * n + ex];
                var b = field[ey * n + ex + 1];
                p = new Vector2(ex + Crossing(a, b), ey);
            }
            else
            {
                // Between (ex, ey) and (ex, ey + 1).
                var a = field[ey * n + ex];
                var b = field[(ey + 1) * n + ex];
                p = new Vector2(ex, ey + Crossing(a, b));
            }

            // Pixel centres sit half a pixel in from their corner.
            points[edge] = p + new Vector2(0.5f);
        }

        void Link(int edge, int segment)
        {
            if (!byEdge.TryGetValue(edge, out var slots))
                byEdge[edge] = (segment, -1);
            else
                byEdge[edge] = (slots.First, segment);
        }
    }

    private static int Horizontal(int x, int y, int n) => (y * n + x) * 2;
    private static int Vertical(int x, int y, int n) => (y * n + x) * 2 + 1;

    private static float Crossing(float a, float b)
    {
        var d = b - a;
        return Math.Abs(d) < 1e-5f ? 0.5f : Math.Clamp((Level - a) / d, 0f, 1f);
    }

    /// <summary>Joins segments that share an edge point into polylines, then simplifies them.</summary>
    private static List<WallLine> Chain(List<Segment> segments, Dictionary<int, (int First, int Second)> byEdge,
        Dictionary<int, Vector2> points, float scale)
    {
        var used = new bool[segments.Count];
        var lines = new List<WallLine>();
        var forward = new List<int>();
        var backward = new List<int>();

        for (var start = 0; start < segments.Count; start++)
        {
            if (used[start]) continue;
            used[start] = true;

            var seg = segments[start];
            forward.Clear();
            backward.Clear();
            forward.Add(seg.EdgeA);
            forward.Add(seg.EdgeB);

            Walk(seg.EdgeB, forward);
            var closed = forward.Count > 2 && forward[^1] == forward[0];
            if (!closed)
                Walk(seg.EdgeA, backward);

            var chain = new List<Vector2>(backward.Count + forward.Count);
            for (var i = backward.Count - 1; i >= 0; i--)
                chain.Add(points[backward[i]]);
            foreach (var e in forward)
                chain.Add(points[e]);

            if (Length(chain) < MinLength) continue;

            var simplified = Simplify(chain);
            var min = new Vector2(float.MaxValue);
            var max = new Vector2(float.MinValue);
            for (var i = 0; i < simplified.Length; i++)
            {
                simplified[i] *= scale;
                min = Vector2.Min(min, simplified[i]);
                max = Vector2.Max(max, simplified[i]);
            }

            lines.Add(new WallLine(simplified, min, max, closed));
        }

        return lines;

        void Walk(int edge, List<int> into)
        {
            while (byEdge.TryGetValue(edge, out var slots))
            {
                var next = slots.First >= 0 && !used[slots.First] ? slots.First
                    : slots.Second >= 0 && !used[slots.Second] ? slots.Second
                    : -1;
                if (next < 0) return;

                used[next] = true;
                var s = segments[next];
                edge = s.EdgeA == edge ? s.EdgeB : s.EdgeA;
                into.Add(edge);
            }
        }
    }

    private static float Length(List<Vector2> points)
    {
        var total = 0f;
        for (var i = 1; i < points.Count; i++)
            total += Vector2.Distance(points[i - 1], points[i]);
        return total;
    }

    /// <summary>Ramer-Douglas-Peucker, iterative so long walls can't overflow the stack.</summary>
    private static Vector2[] Simplify(List<Vector2> points)
    {
        if (points.Count < 3) return [.. points];

        var keep = new bool[points.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int From, int To)>();
        stack.Push((0, points.Count - 1));

        while (stack.Count > 0)
        {
            var (from, to) = stack.Pop();
            if (to - from < 2) continue;

            var a = points[from];
            var b = points[to];
            var ab = b - a;
            var lengthSq = ab.LengthSquared();

            var worst = -1f;
            var index = -1;
            for (var i = from + 1; i < to; i++)
            {
                var ap = points[i] - a;
                // A closed loop starts and ends on the same point; fall back to plain distance there.
                var d = lengthSq < 1e-6f ? ap.Length() : MathF.Abs(ab.X * ap.Y - ab.Y * ap.X) / MathF.Sqrt(lengthSq);
                if (d <= worst) continue;
                worst = d;
                index = i;
            }

            if (worst <= Tolerance) continue;
            keep[index] = true;
            stack.Push((from, index));
            stack.Push((index, to));
        }

        var result = new List<Vector2>();
        for (var i = 0; i < points.Count; i++)
            if (keep[i]) result.Add(points[i]);
        return [.. result];
    }
}

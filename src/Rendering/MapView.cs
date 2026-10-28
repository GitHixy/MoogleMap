using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace MoogleMap.Rendering;

/// <summary>
/// One frame's mapping from world X/Z to screen: centred on the player, scaled to pixels per
/// yalm, and turned so the camera's view points up (or north does).
/// </summary>
public readonly struct MapView
{
    public Vector2 Center { get; init; }
    /// <summary>Half the map's width and height, in pixels.</summary>
    public Vector2 Radii { get; init; }
    /// <summary>An ellipse when true, a rectangle with softened corners otherwise.</summary>
    public bool Round { get; init; }
    /// <summary>How far in from the rim the map fades, in pixels, the same all the way round.</summary>
    public float FadeWidth { get; init; }
    public Vector2 Origin { get; init; }
    /// <summary>Screen pixels per yalm.</summary>
    public float Scale { get; init; }
    public float Cos { get; init; }
    public float Sin { get; init; }
    public float Alpha { get; init; }
    /// <summary>1 at 1080p, for anything sized in pixels.</summary>
    public float PixelScale { get; init; }
    /// <summary>Opacity of the map layer itself, markers excluded.</summary>
    public float MapOpacity { get; init; }

    /// <summary>Rotation that puts the given world direction (X/Z) at the top of the screen.</summary>
    public static (float Cos, float Sin) FacingUp(Vector2 forward)
    {
        if (forward.LengthSquared() < 1e-6f) return (1f, 0f);
        // North-up maps world +X to screen right and +Z (south) to screen down.
        var angle = -MathF.PI / 2f - MathF.Atan2(forward.Y, forward.X);
        return (MathF.Cos(angle), MathF.Sin(angle));
    }

    public Vector2 ToScreen(Vector2 world)
    {
        var d = (world - Origin) * Scale;
        return Center + new Vector2(d.X * Cos - d.Y * Sin, d.X * Sin + d.Y * Cos);
    }

    public Vector2 ToScreen(Vector3 world) => ToScreen(new Vector2(world.X, world.Z));

    /// <summary>A world direction (X/Z) turned into screen space.</summary>
    public Vector2 Direction(Vector2 world)
        => new(world.X * Cos - world.Y * Sin, world.X * Sin + world.Y * Cos);

    /// <summary>Screen direction of a game rotation, where 0 faces south (+Z).</summary>
    public Vector2 Facing(float rotation) => Direction(new Vector2(MathF.Sin(rotation), MathF.Cos(rotation)));

    /// <summary>The shorter of the half width and half height.</summary>
    public float Inradius => MathF.Min(Radii.X, Radii.Y);

    /// <summary>The furthest the rim gets from the centre: what a turning map has to cover.</summary>
    public float Extent => Round ? MathF.Max(Radii.X, Radii.Y) : Radii.Length();

    /// <summary>How round the rectangle's corners are, in pixels.</summary>
    private float Corner => Inradius * 0.18f;

    /// <summary>How far inside the rim a screen point is, in pixels: negative outside it.</summary>
    public float Depth(Vector2 screen)
    {
        var p = screen - Center;
        if (Round)
        {
            var length = p.Length();
            var k = (p / Radii).Length();
            return k < 1e-6f ? Inradius : length / k - length;
        }

        // A rounded rectangle's distance field.
        var corner = Corner;
        var q = Vector2.Abs(p) - Radii + new Vector2(corner);
        return corner - Vector2.Max(q, Vector2.Zero).Length() - MathF.Min(MathF.Max(q.X, q.Y), 0f);
    }

    /// <summary>Distance from the centre to the rim along a screen direction, in pixels.</summary>
    public float RimDistance(Vector2 direction)
    {
        var length = direction.Length();
        if (length < 1e-6f) return Inradius;
        if (Round) return length / (direction / Radii).Length();

        // Halving down the ray to where the rounded rectangle's edge is, close enough to the pixel.
        var unit = direction / length;
        float lo = 0f, hi = Extent;
        for (var i = 0; i < 16; i++)
        {
            var mid = (lo + hi) * 0.5f;
            if (Depth(Center + unit * mid) > 0f) lo = mid;
            else hi = mid;
        }
        return lo;
    }

    /// <summary>How visible a point is: full inside, fading over the rim, gone outside.</summary>
    public float Fade(Vector2 screen)
    {
        var depth = Depth(screen);
        if (depth >= FadeWidth) return 1f;
        if (depth <= 0f) return 0f;
        var x = depth / FadeWidth;
        return x * x * (3f - 2f * x);
    }

    /// <summary>Adds the map's outline, scaled about its centre, to the draw list's path.</summary>
    public void PathOutline(ImDrawListPtr dl, float scale)
    {
        var radii = Radii * scale;
        if (!Round)
        {
            dl.PathRect(Center - radii, Center + radii, Corner * scale);
            return;
        }

        const int segments = 96;
        for (var i = 0; i < segments; i++)
        {
            var a = MathF.PI * 2f * i / segments;
            dl.PathLineTo(Center + new Vector2(MathF.Cos(a) * radii.X, MathF.Sin(a) * radii.Y));
        }
    }

    /// <summary>Pulls a point inside the rim, for markers that should stay visible from afar.</summary>
    public Vector2 ClampToRim(Vector2 screen, float inset, out bool clamped)
    {
        var d = screen - Center;
        var rim = RimDistance(d);
        var max = rim - FadeWidth * 0.65f - inset;
        var len = d.Length();
        clamped = len > max;
        return clamped ? Center + d / len * max : screen;
    }
}

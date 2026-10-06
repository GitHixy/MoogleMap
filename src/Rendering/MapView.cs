using System;
using System.Numerics;

namespace MoogleMap.Rendering;

/// <summary>
/// One frame's mapping from world X/Z to screen: centred on the player, scaled to pixels per
/// yalm, and turned so the camera's view points up (or north does).
/// </summary>
public readonly struct MapView
{
    public Vector2 Center { get; init; }
    /// <summary>Radius of the map's circle in pixels, before any trimming.</summary>
    public float Radius { get; init; }
    /// <summary>
    /// How much of the width and height of the circle is shown, 0 to 1. (1, 1) is the whole
    /// circle; (1, 0.5) trims it top and bottom to a wide oval, at the same scale.
    /// </summary>
    public Vector2 Stretch
    {
        get => stretch == Vector2.Zero ? Vector2.One : stretch;
        init => stretch = value;
    }

    private readonly Vector2 stretch;
    /// <summary>Where the map's rim starts to fade, as a fraction of the radius.</summary>
    public float FadeStart { get; init; }
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

    /// <summary>Half the map's width and height, in pixels.</summary>
    public Vector2 Radii => Stretch * Radius;

    /// <summary>How far out a screen point is: 0 at the centre, 1 on the rim, whatever the map's shape.</summary>
    public float Reach(Vector2 screen) => ((screen - Center) / Radii).Length();

    /// <summary>Distance from the centre to the rim along a screen direction, in pixels.</summary>
    public float RimDistance(Vector2 direction)
    {
        var length = (direction / Radii).Length();
        return length < 1e-6f ? Radius : direction.Length() / length;
    }

    /// <summary>How visible a point is: full inside, fading over the rim, gone outside.</summary>
    public float Fade(Vector2 screen)
    {
        var t = Reach(screen);
        if (t <= FadeStart) return 1f;
        if (t >= 1f) return 0f;
        var x = 1f - (t - FadeStart) / (1f - FadeStart);
        return x * x * (3f - 2f * x);
    }

    /// <summary>Pulls a point inside the rim, for markers that should stay visible from afar.</summary>
    public Vector2 ClampToRim(Vector2 screen, float inset, out bool clamped)
    {
        var d = screen - Center;
        var rim = RimDistance(d);
        var max = rim * FadeStart + (rim - rim * FadeStart) * 0.35f - inset;
        var len = d.Length();
        clamped = len > max;
        return clamped ? Center + d / len * max : screen;
    }
}

using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace MoogleMap.Rendering;

/// <summary>The overlay's finishing touches, built from plain circles and lines so they cost next to nothing.</summary>
public static class Effects
{
    /// <summary>A band of light at some distance from the centre, and how strongly it shines.</summary>
    public readonly record struct SweepBand(Vector2 Centre, float Radius, float Width, float Strength)
    {
        /// <summary>How lit a point is: full on the band, nothing a band's width away.</summary>
        public float At(Vector2 screen)
        {
            var d = MathF.Abs(Vector2.Distance(screen, Centre) - Radius) / Width;
            return d >= 1f ? 0f : (1f - d) * (1f - d);
        }
    }

    /// <summary>Seconds between sweeps, and how long each takes to cross the map.</summary>
    private const float SweepEvery = 5f;
    private const float SweepTime = 1.6f;

    /// <summary>Where the sweep is now, or null between sweeps.</summary>
    public static SweepBand? Sweep(MapView view, float now)
    {
        var t = now % SweepEvery / SweepTime;
        if (t >= 1f) return null;
        var ease = 1f - (1f - t) * (1f - t);
        return new SweepBand(view.Center, view.Radius * ease, 26f * view.PixelScale, (1f - t) * 0.9f);
    }

    /// <summary>A ring rippling out from the player's arrow every few seconds.</summary>
    public static void Ripple(ImDrawListPtr dl, Vector2 at, float px, float now, float alpha)
    {
        var t = now % 3f / 1.3f;
        if (t >= 1f) return;
        var ease = 1f - (1f - t) * (1f - t);
        dl.AddCircle(at, (10f + 24f * ease) * px, Draw2D.Color(Draw2D.Gold, alpha * (1f - t) * (1f - t) * 0.6f), 32, 1.5f * px);
    }
}

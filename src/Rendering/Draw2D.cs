using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace MoogleMap.Rendering;

/// <summary>The small shapes markers are made of. Everything takes a final alpha so the map can fade as one.</summary>
public static class Draw2D
{
    public static readonly Vector4 Parchment = new(0.95f, 0.91f, 0.82f, 1f);
    public static readonly Vector4 Gold = new(0.84f, 0.69f, 0.41f, 1f);
    public static readonly Vector4 Aether = new(0.55f, 0.85f, 1f, 1f);
    public static readonly Vector4 Gathering = new(0.6f, 0.9f, 0.5f, 1f);
    private static readonly Vector4 Shadow = new(0.02f, 0.03f, 0.06f, 1f);

    public static uint Color(Vector4 c, float alpha) => ImGui.GetColorU32(new Vector4(c.X, c.Y, c.Z, Math.Clamp(c.W * alpha, 0f, 1f)));

    public static void Dot(ImDrawListPtr dl, Vector2 at, float r, Vector4 c, float alpha, bool outline = false)
    {
        dl.AddCircleFilled(at, r + 1.5f, Color(Shadow, alpha * 0.55f), 16);
        dl.AddCircleFilled(at, r, Color(c, alpha), 16);
        if (outline)
            dl.AddCircle(at, r, Color(Parchment, alpha * 0.9f), 16, 1.3f);
    }

    /// <summary>A short line out of a dot in the direction it faces.</summary>
    public static void Tick(ImDrawListPtr dl, Vector2 at, Vector2 dir, float r, Vector4 c, float alpha)
        => dl.AddLine(at + dir * r, at + dir * (r + Math.Max(3f, r * 0.6f)), Color(c, alpha), 1.6f);

    public static void Diamond(ImDrawListPtr dl, Vector2 at, float r, Vector4 c, float alpha, bool outline = false)
    {
        var top = at + new Vector2(0, -r);
        var right = at + new Vector2(r, 0);
        var bottom = at + new Vector2(0, r);
        var left = at + new Vector2(-r, 0);
        dl.AddQuadFilled(top + new Vector2(0, -1.5f), right + new Vector2(1.5f, 0), bottom + new Vector2(0, 1.5f), left + new Vector2(-1.5f, 0), Color(Shadow, alpha * 0.5f));
        dl.AddQuadFilled(top, right, bottom, left, Color(c, alpha));
        if (outline)
            dl.AddQuad(top, right, bottom, left, Color(Parchment, alpha * 0.8f), 1.2f);
    }

    /// <summary>A little chest: box with a lighter lid.</summary>
    public static void Chest(ImDrawListPtr dl, Vector2 at, float r, Vector4 c, float alpha)
    {
        var min = at - new Vector2(r, r * 0.75f);
        var max = at + new Vector2(r, r * 0.75f);
        dl.AddRectFilled(min - new Vector2(1.5f), max + new Vector2(1.5f), Color(Shadow, alpha * 0.55f), 2f);
        dl.AddRectFilled(min, max, Color(c, alpha), 1.5f);
        dl.AddRectFilled(min, new Vector2(max.X, at.Y - r * 0.15f), Color(Lighten(c, 0.35f), alpha), 1.5f);
        dl.AddLine(new Vector2(at.X, at.Y - r * 0.4f), new Vector2(at.X, at.Y + r * 0.2f), Color(Gold, alpha), 1.5f);
    }

    /// <summary>An aetheryte-style crystal: tall diamond with a bright core.</summary>
    public static void Crystal(ImDrawListPtr dl, Vector2 at, float r, Vector4 c, float alpha)
    {
        var top = at + new Vector2(0, -r * 1.25f);
        var right = at + new Vector2(r * 0.6f, 0);
        var bottom = at + new Vector2(0, r * 1.25f);
        var left = at + new Vector2(-r * 0.6f, 0);
        dl.AddCircleFilled(at, r * 1.4f, Color(c, alpha * 0.15f), 20);
        dl.AddQuadFilled(top, right, bottom, left, Color(c, alpha));
        dl.AddLine(top, bottom, Color(Lighten(c, 0.6f), alpha), 1.2f);
        dl.AddQuad(top, right, bottom, left, Color(Parchment, alpha * 0.85f), 1.2f);
    }

    /// <summary>The player: a gold-rimmed arrowhead pointing where you face.</summary>
    public static void Arrow(ImDrawListPtr dl, Vector2 at, Vector2 dir, float size, float alpha)
    {
        var side = new Vector2(-dir.Y, dir.X);
        var tip = at + dir * size * 1.25f;
        var left = at - dir * size * 0.8f + side * size * 0.85f;
        var right = at - dir * size * 0.8f - side * size * 0.85f;
        var notch = at - dir * size * 0.35f;

        dl.AddCircleFilled(at, size * 1.6f, Color(Gold, alpha * 0.14f), 24);
        dl.AddTriangleFilled(tip, left, notch, Color(Parchment, alpha));
        dl.AddTriangleFilled(tip, notch, right, Color(Lighten(Gold, 0.25f), alpha));
        dl.AddLine(tip, left, Color(Gold, alpha), 1.5f);
        dl.AddLine(left, notch, Color(Gold, alpha), 1.5f);
        dl.AddLine(notch, right, Color(Gold, alpha), 1.5f);
        dl.AddLine(right, tip, Color(Gold, alpha), 1.5f);
    }

    /// <summary>A soft wedge out from the player, for the camera's view.</summary>
    public static void Cone(ImDrawListPtr dl, Vector2 at, Vector2 dir, float length, float halfAngle, Vector4 c, float alpha)
    {
        var angle = MathF.Atan2(dir.Y, dir.X);
        // Three nested wedges fake a fade along the cone without a custom shader.
        for (var i = 0; i < 3; i++)
        {
            var l = length * (1f - i * 0.28f);
            dl.PathLineTo(at);
            dl.PathArcTo(at, l, angle - halfAngle, angle + halfAngle, 20);
            dl.PathFillConvex(Color(c, alpha * (0.5f + i * 0.25f)));
        }
    }

    /// <summary>
    /// Strokes a polyline whose points fade with the map's rim. Consecutive points of the same
    /// (quantised) fade go out as one path, so joints stay clean and the call count stays low.
    /// Takes points already on screen, with each one's rim fade, so a line projected once a frame
    /// can be stroked several times over.
    /// </summary>
    /// <param name="band">Lights only where a sweep band crosses, on top of the rim fade.</param>
    public static void FadedPolyline(ImDrawListPtr dl, ReadOnlySpan<Vector2> points, ReadOnlySpan<float> fades,
        Vector4 colour, float alpha, float thickness, Effects.SweepBand? band = null)
    {
        const float steps = 16f;
        var count = 0;
        var level = -1;
        var previous = Vector2.Zero;

        for (var i = 0; i < points.Length; i++)
        {
            var s = points[i];
            var shape = band is { } b ? b.At(s) : 1f;
            var l = (int)MathF.Round(fades[i] * shape * steps);

            if (l != level)
            {
                if (count > 1 && level > 0)
                {
                    // The segment into this point still belongs to the old run; the new one starts here.
                    dl.PathLineTo(s);
                    dl.PathStroke(Color(colour, alpha * level / steps), ImDrawFlags.None, thickness);
                    dl.PathLineTo(s);
                    level = l;
                    count = 1;
                    previous = s;
                    continue;
                }

                // Nothing worth stroking so far: start over, taking the segment into this point.
                dl.PathClear();
                level = l;
                count = 0;
                if (i > 0)
                {
                    dl.PathLineTo(previous);
                    count = 1;
                }
            }

            dl.PathLineTo(s);
            count++;
            previous = s;
        }

        if (count > 1 && level > 0)
            dl.PathStroke(Color(colour, alpha * level / steps), ImDrawFlags.None, thickness);
        else
            dl.PathClear();
    }

    /// <summary>A FontAwesome glyph centred on a point, with the same soft shadow as labels.</summary>
    public static void Glyph(ImDrawListPtr dl, Vector2 at, FontAwesomeIcon icon, float size, Vector4 c, float alpha)
    {
        var font = UiBuilder.IconFont;
        var text = icon.ToIconString();
        // Font Awesome glyphs are close to square; near enough to centre one without measuring.
        var extent = new Vector2(size * 0.9f, size);
        var pos = at - extent * 0.5f;
        dl.AddText(font, size, pos + new Vector2(1f, 1f), Color(Shadow, alpha * 0.85f), text);
        dl.AddText(font, size, pos, Color(c, alpha), text);
    }

    /// <summary>Text with a soft dark shadow, so it reads over any background.</summary>
    public static void Label(ImDrawListPtr dl, Vector2 at, string text, Vector4 c, float alpha, float scale, bool centered = false)
    {
        var extent = ImGui.CalcTextSize(text) * scale;
        Text(dl, ImGui.GetFont(), ImGui.GetFontSize() * scale, extent, at, text, c, alpha, centered);
    }

    /// <summary>A label in a given font, <paramref name="size"/> pixels tall.</summary>
    public static void Label(ImDrawListPtr dl, ImFontPtr font, float size, Vector2 at, string text, Vector4 c, float alpha, bool centered = false)
    {
        ImGui.PushFont(font);
        var extent = ImGui.CalcTextSize(text) * (size / ImGui.GetFontSize());
        ImGui.PopFont();
        Text(dl, font, size, extent, at, text, c, alpha, centered);
    }

    private static void Text(ImDrawListPtr dl, ImFontPtr font, float size, Vector2 extent, Vector2 at, string text, Vector4 c, float alpha, bool centered)
    {
        var pos = centered ? at - extent * 0.5f : at;
        pos = new Vector2(MathF.Round(pos.X), MathF.Round(pos.Y));

        var shadow = Color(Shadow, alpha * 0.85f);
        dl.AddText(font, size, pos + new Vector2(1, 1), shadow, text);
        dl.AddText(font, size, pos + new Vector2(-1, 1), shadow, text);
        dl.AddText(font, size, pos + new Vector2(0, 2), shadow, text);
        dl.AddText(font, size, pos, Color(c, alpha), text);
    }

    /// <summary>Text beside a marker. Side follows the game's MapMarker convention: 1 left, 2 right, 3 above, 4 below.</summary>
    public static void SideLabel(ImDrawListPtr dl, Vector2 at, float gap, string text, byte side, Vector4 c, float alpha, float scale)
    {
        var extent = ImGui.CalcTextSize(text) * scale;
        gap += 3f;

        var pos = side switch
        {
            1 => new Vector2(at.X - gap - extent.X, at.Y - extent.Y * 0.5f),
            3 => new Vector2(at.X - extent.X * 0.5f, at.Y - gap - extent.Y),
            4 => new Vector2(at.X - extent.X * 0.5f, at.Y + gap),
            _ => new Vector2(at.X + gap, at.Y - extent.Y * 0.5f),
        };
        Label(dl, pos, text, c, alpha, scale);
    }

    public static Vector4 Lighten(Vector4 c, float t)
        => new(c.X + (1f - c.X) * t, c.Y + (1f - c.Y) * t, c.Z + (1f - c.Z) * t, c.W);
}

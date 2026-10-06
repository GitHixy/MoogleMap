using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace MoogleMap.UI;

/// <summary>
/// Central design system for MoogleMap, shared with FateLimit, LootView and HitSpark.
/// A dark, translucent "Eorzean" panel language: deep navy glass, brass rules,
/// crystal-blue highlights and warm parchment text.
/// </summary>
public static class Theme
{
    // ------------------------------------------------------------------
    // Palette
    // ------------------------------------------------------------------

    /// <summary>Base window fill - deep midnight navy.</summary>
    public static readonly Vector4 Ink = Rgb(0x0B, 0x0F, 0x18, 0.96f);
    /// <summary>Recessed surface, one step above the window fill.</summary>
    public static readonly Vector4 Panel = Rgb(0x13, 0x19, 0x26);
    /// <summary>Raised surface - cards, list rows, toolbars.</summary>
    public static readonly Vector4 Surface = Rgb(0x1B, 0x23, 0x33);
    /// <summary>Surface under the cursor.</summary>
    public static readonly Vector4 SurfaceHover = Rgb(0x26, 0x31, 0x45);
    /// <summary>Surface while pressed or selected.</summary>
    public static readonly Vector4 SurfaceActive = Rgb(0x2F, 0x3D, 0x56);

    /// <summary>Hairline rule between regions.</summary>
    public static readonly Vector4 Line = Rgb(0x2C, 0x38, 0x4D);
    /// <summary>Barely-there rule used inside dense lists.</summary>
    public static readonly Vector4 LineSoft = Rgb(0x1F, 0x27, 0x36);

    /// <summary>Primary accent - the brass of an Eorzean window frame.</summary>
    public static readonly Vector4 Gold = Rgb(0xD6, 0xB0, 0x68);
    public static readonly Vector4 GoldBright = Rgb(0xF2, 0xD7, 0x96);
    public static readonly Vector4 GoldDim = Rgb(0x7E, 0x66, 0x33);

    /// <summary>Secondary accent - aetheryte crystal blue.</summary>
    public static readonly Vector4 Crystal = Rgb(0x6F, 0xB8, 0xE0);
    public static readonly Vector4 CrystalBright = Rgb(0x9E, 0xD9, 0xF5);
    public static readonly Vector4 CrystalDim = Rgb(0x35, 0x5D, 0x76);

    /// <summary>Warm parchment body text.</summary>
    public static readonly Vector4 Text = Rgb(0xE7, 0xE1, 0xD3);
    public static readonly Vector4 TextMuted = Rgb(0x95, 0xA0, 0xB3);
    public static readonly Vector4 TextFaint = Rgb(0x5F, 0x6A, 0x7E);

    public static readonly Vector4 Good = Rgb(0x7C, 0xD3, 0x8B);
    public static readonly Vector4 Warn = Rgb(0xE8, 0xB8, 0x4B);
    public static readonly Vector4 Bad = Rgb(0xE4, 0x69, 0x5F);

    /// <summary>Discord's brand blurple, used only on the buttons that open the community server.</summary>
    public static readonly Vector4 DiscordBlurple = Rgb(0x58, 0x65, 0xF2);
    /// <summary>Patreon's brand coral, the one other exception to the Eorzean palette.</summary>
    public static readonly Vector4 PatreonCoral = Rgb(0xFF, 0x42, 0x4D);

    // ------------------------------------------------------------------
    // Metrics
    // ------------------------------------------------------------------

    public const float Radius = 6f;
    public const float RadiusLarge = 9f;
    public const float RowHeight = 30f;

    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    /// <summary>Seconds since the plugin loaded - the clock every animation reads from.</summary>
    public static float Time => (float)Clock.Elapsed.TotalSeconds;

    // ------------------------------------------------------------------
    // Colour helpers
    // ------------------------------------------------------------------

    private static Vector4 Rgb(int r, int g, int b, float a = 1f)
        => new(r / 255f, g / 255f, b / 255f, a);

    /// <summary>Same colour at a different opacity.</summary>
    public static Vector4 Alpha(Vector4 c, float a) => new(c.X, c.Y, c.Z, c.W * a);

    /// <summary>Packed colour, optionally re-scaled in opacity.</summary>
    public static uint U32(Vector4 c, float a = 1f) => ImGui.GetColorU32(Alpha(c, a));

    public static Vector4 Mix(Vector4 a, Vector4 b, float t)
        => new(
            a.X + (b.X - a.X) * t,
            a.Y + (b.Y - a.Y) * t,
            a.Z + (b.Z - a.Z) * t,
            a.W + (b.W - a.W) * t);

    /// <summary>Brighten a colour toward white without touching its alpha.</summary>
    public static Vector4 Lighten(Vector4 c, float t)
        => new(c.X + (1f - c.X) * t, c.Y + (1f - c.Y) * t, c.Z + (1f - c.Z) * t, c.W);

    // ------------------------------------------------------------------
    // Global style scope
    // ------------------------------------------------------------------

    /// <summary>
    /// Pushes the full MoogleMap look. Must be called <em>before</em> ImGui.Begin so the
    /// window frame, title bar and scrollbars pick it up too.
    /// </summary>
    public static IDisposable Push()
    {
        var colors = ImRaii.PushColor(ImGuiCol.WindowBg, Ink)
            .Push(ImGuiCol.ChildBg, new Vector4(0, 0, 0, 0))
            .Push(ImGuiCol.PopupBg, Rgb(0x10, 0x16, 0x22, 0.98f))
            .Push(ImGuiCol.Border, Line)
            .Push(ImGuiCol.BorderShadow, new Vector4(0, 0, 0, 0))
            .Push(ImGuiCol.Text, Text)
            .Push(ImGuiCol.TextDisabled, TextFaint)
            .Push(ImGuiCol.TitleBg, Rgb(0x0D, 0x12, 0x1C, 0.98f))
            .Push(ImGuiCol.TitleBgActive, Rgb(0x14, 0x1C, 0x2B, 0.98f))
            .Push(ImGuiCol.TitleBgCollapsed, Rgb(0x0D, 0x12, 0x1C, 0.8f))
            .Push(ImGuiCol.MenuBarBg, Panel)
            .Push(ImGuiCol.FrameBg, Surface)
            .Push(ImGuiCol.FrameBgHovered, SurfaceHover)
            .Push(ImGuiCol.FrameBgActive, SurfaceActive)
            .Push(ImGuiCol.Button, Surface)
            .Push(ImGuiCol.ButtonHovered, SurfaceHover)
            .Push(ImGuiCol.ButtonActive, SurfaceActive)
            .Push(ImGuiCol.Header, Alpha(Crystal, 0.16f))
            .Push(ImGuiCol.HeaderHovered, Alpha(Crystal, 0.26f))
            .Push(ImGuiCol.HeaderActive, Alpha(Crystal, 0.34f))
            .Push(ImGuiCol.Separator, Line)
            .Push(ImGuiCol.SeparatorHovered, Alpha(Gold, 0.6f))
            .Push(ImGuiCol.SeparatorActive, Gold)
            .Push(ImGuiCol.CheckMark, Gold)
            .Push(ImGuiCol.SliderGrab, Gold)
            .Push(ImGuiCol.SliderGrabActive, GoldBright)
            .Push(ImGuiCol.ResizeGrip, Alpha(Gold, 0.25f))
            .Push(ImGuiCol.ResizeGripHovered, Alpha(Gold, 0.55f))
            .Push(ImGuiCol.ResizeGripActive, Alpha(Gold, 0.85f))
            .Push(ImGuiCol.Tab, new Vector4(0, 0, 0, 0))
            .Push(ImGuiCol.TabHovered, Alpha(Crystal, 0.18f))
            .Push(ImGuiCol.TabActive, Alpha(Crystal, 0.14f))
            .Push(ImGuiCol.TabUnfocused, new Vector4(0, 0, 0, 0))
            .Push(ImGuiCol.TabUnfocusedActive, Alpha(Crystal, 0.10f))
            .Push(ImGuiCol.TableHeaderBg, Rgb(0x16, 0x1E, 0x2C))
            .Push(ImGuiCol.TableBorderStrong, Line)
            .Push(ImGuiCol.TableBorderLight, LineSoft)
            .Push(ImGuiCol.TableRowBg, new Vector4(0, 0, 0, 0))
            .Push(ImGuiCol.TableRowBgAlt, Alpha(Surface, 0.45f))
            .Push(ImGuiCol.ScrollbarBg, new Vector4(0, 0, 0, 0))
            .Push(ImGuiCol.ScrollbarGrab, Alpha(Line, 1.4f))
            .Push(ImGuiCol.ScrollbarGrabHovered, Alpha(Crystal, 0.5f))
            .Push(ImGuiCol.ScrollbarGrabActive, Alpha(Crystal, 0.75f))
            .Push(ImGuiCol.PlotHistogram, Crystal)
            .Push(ImGuiCol.PlotHistogramHovered, CrystalBright)
            .Push(ImGuiCol.PlotLines, Gold)
            .Push(ImGuiCol.PlotLinesHovered, GoldBright)
            .Push(ImGuiCol.NavHighlight, Alpha(Gold, 0.7f))
            .Push(ImGuiCol.DragDropTarget, Gold);

        var styles = ImRaii.PushStyle(ImGuiStyleVar.WindowRounding, RadiusLarge)
            .Push(ImGuiStyleVar.WindowBorderSize, 1f)
            .Push(ImGuiStyleVar.WindowPadding, new Vector2(14, 12))
            .Push(ImGuiStyleVar.ChildRounding, Radius)
            .Push(ImGuiStyleVar.FrameRounding, Radius)
            .Push(ImGuiStyleVar.FrameBorderSize, 0f)
            .Push(ImGuiStyleVar.FramePadding, new Vector2(9, 5))
            .Push(ImGuiStyleVar.PopupRounding, Radius)
            .Push(ImGuiStyleVar.PopupBorderSize, 1f)
            .Push(ImGuiStyleVar.ItemSpacing, new Vector2(8, 6))
            .Push(ImGuiStyleVar.ItemInnerSpacing, new Vector2(6, 5))
            .Push(ImGuiStyleVar.IndentSpacing, 18f)
            .Push(ImGuiStyleVar.ScrollbarSize, 11f)
            .Push(ImGuiStyleVar.ScrollbarRounding, 6f)
            .Push(ImGuiStyleVar.GrabMinSize, 12f)
            .Push(ImGuiStyleVar.GrabRounding, 5f)
            .Push(ImGuiStyleVar.TabRounding, Radius)
            .Push(ImGuiStyleVar.CellPadding, new Vector2(8, 5));

        return new Scope(colors, styles);
    }

    private sealed class Scope : IDisposable
    {
        private readonly IDisposable[] parts;
        private bool disposed;

        public Scope(params IDisposable[] parts) => this.parts = parts;

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            for (var i = parts.Length - 1; i >= 0; i--)
                parts[i].Dispose();
        }
    }

    // ------------------------------------------------------------------
    // Fonts
    // ------------------------------------------------------------------

    /// <summary>Temporarily scales the window font - use for titles and stat numerals.</summary>
    public readonly struct FontScale : IDisposable
    {
        public FontScale(float scale) => ImGui.SetWindowFontScale(scale);
        public void Dispose() => ImGui.SetWindowFontScale(1f);
    }

    /// <summary>Draws a FontAwesome glyph inline.</summary>
    public static void Icon(FontAwesomeIcon icon, Vector4? color = null)
    {
        using var font = ImRaii.PushFont(UiBuilder.IconFont);
        if (color.HasValue)
            ImGui.TextColored(color.Value, icon.ToIconString());
        else
            ImGui.Text(icon.ToIconString());
    }

    /// <summary>Width of a FontAwesome glyph at the current font size.</summary>
    public static float IconWidth(FontAwesomeIcon icon)
    {
        using var font = ImRaii.PushFont(UiBuilder.IconFont);
        return ImGui.CalcTextSize(icon.ToIconString()).X;
    }

    /// <summary>Glyph followed by a label on one baseline.</summary>
    public static void IconText(FontAwesomeIcon icon, string label, Vector4 color)
    {
        Icon(icon, color);
        ImGui.SameLine(0, 6);
        ImGui.TextColored(color, label);
    }

    // ------------------------------------------------------------------
    // Window chrome
    // ------------------------------------------------------------------

    /// <summary>
    /// Lays a soft vertical gradient and a brass top edge over the current window,
    /// which is what gives the panels their Eorzean glass look.
    /// </summary>
    public static void DrawWindowBackdrop()
    {
        var dl = ImGui.GetWindowDrawList();
        var min = ImGui.GetWindowPos();
        var max = min + ImGui.GetWindowSize();

        dl.PushClipRect(min, max, true);

        // Cool highlight bleeding down from the top of the panel.
        dl.AddRectFilledMultiColor(
            min,
            new Vector2(max.X, min.Y + 110f),
            U32(Crystal, 0.055f), U32(Crystal, 0.055f),
            U32(Crystal, 0f), U32(Crystal, 0f));

        // Warm pool in the bottom corner, echoing the game's lantern-lit frames.
        dl.AddRectFilledMultiColor(
            new Vector2(min.X, max.Y - 160f),
            max,
            U32(Gold, 0f), U32(Gold, 0f),
            U32(Gold, 0.05f), U32(Gold, 0.012f));

        // Brass hairline along the top edge.
        dl.AddLine(
            new Vector2(min.X + RadiusLarge, min.Y + 0.5f),
            new Vector2(max.X - RadiusLarge, min.Y + 0.5f),
            U32(Gold, 0.35f), 1f);

        dl.PopClipRect();
    }

    /// <summary>
    /// The masthead every window opens with: crest glyph, title, optional subtitle and a
    /// right-aligned slot for actions. Returns the Y the caller should draw actions at.
    /// </summary>
    public static void WindowHeader(FontAwesomeIcon icon, string title, string? subtitle = null, Action? actions = null)
    {
        var dl = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var height = subtitle is null ? 30f : 40f;

        // Crest: a small rotated diamond, the shape FFXIV uses for job/duty markers.
        var crestCenter = new Vector2(origin.X + 11f, origin.Y + height * 0.5f);
        DrawDiamond(dl, crestCenter, 11f, U32(Gold, 0.18f), U32(Gold, 0.85f), 1.4f);

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            var glyph = icon.ToIconString();
            var gs = ImGui.CalcTextSize(glyph);
            dl.AddText(crestCenter - gs * 0.5f, U32(GoldBright), glyph);
        }

        ImGui.SetCursorScreenPos(new Vector2(origin.X + 30f, origin.Y + (subtitle is null ? 5f : 1f)));
        using (new FontScale(1.16f))
        {
            ImGui.TextColored(Text, title);
        }

        if (subtitle is not null)
        {
            ImGui.SetCursorScreenPos(new Vector2(origin.X + 30f, origin.Y + 22f));
            using var s = new FontScale(0.88f);
            ImGui.TextColored(TextMuted, subtitle);
        }

        if (actions is not null)
        {
            ImGui.SetCursorScreenPos(new Vector2(origin.X, origin.Y + (height - ImGui.GetFrameHeight()) * 0.5f));
            actions();
        }

        ImGui.SetCursorScreenPos(new Vector2(origin.X, origin.Y + height));
        ImGui.Dummy(new Vector2(width, 0));
        Rule();
    }

    /// <summary>A separator that fades out toward both ends, with a brass core.</summary>
    public static void Rule(float verticalPadding = 6f)
    {
        ImGui.Dummy(new Vector2(0, verticalPadding));
        var dl = ImGui.GetWindowDrawList();
        var p = ImGui.GetCursorScreenPos();
        var w = ImGui.GetContentRegionAvail().X;
        var mid = w * 0.5f;

        dl.AddRectFilledMultiColor(
            new Vector2(p.X, p.Y), new Vector2(p.X + mid, p.Y + 1f),
            U32(Gold, 0f), U32(Gold, 0.45f), U32(Gold, 0.45f), U32(Gold, 0f));
        dl.AddRectFilledMultiColor(
            new Vector2(p.X + mid, p.Y), new Vector2(p.X + w, p.Y + 1f),
            U32(Gold, 0.45f), U32(Gold, 0f), U32(Gold, 0f), U32(Gold, 0.45f));

        ImGui.Dummy(new Vector2(w, 1f));
        ImGui.Dummy(new Vector2(0, verticalPadding));
    }

    /// <summary>A plain hairline for use inside dense lists.</summary>
    public static void HairLine(float alpha = 1f)
    {
        var dl = ImGui.GetWindowDrawList();
        var p = ImGui.GetCursorScreenPos();
        var w = ImGui.GetContentRegionAvail().X;
        dl.AddLine(p, new Vector2(p.X + w, p.Y), U32(LineSoft, alpha), 1f);
        ImGui.Dummy(new Vector2(w, 1f));
    }

    /// <summary>Section label: brass caps preceded by a short rule.</summary>
    public static void SectionHeader(string label, FontAwesomeIcon? icon = null)
    {
        ImGui.Dummy(new Vector2(0, 2));
        var dl = ImGui.GetWindowDrawList();
        var p = ImGui.GetCursorScreenPos();
        var h = ImGui.GetTextLineHeight();

        // Vertical brass tick to the left of the label.
        dl.AddRectFilled(new Vector2(p.X, p.Y + 1), new Vector2(p.X + 2.5f, p.Y + h - 1), U32(Gold, 0.9f), 1.5f);

        ImGui.SetCursorScreenPos(new Vector2(p.X + 10, p.Y));
        if (icon.HasValue)
        {
            Icon(icon.Value, Gold);
            ImGui.SameLine(0, 7);
        }

        ImGui.TextColored(GoldBright, label.ToUpperInvariant());

        // Trailing rule running to the edge of the region.
        var after = ImGui.GetItemRectMax();
        var right = p.X + ImGui.GetContentRegionAvail().X + 10;
        if (right > after.X + 12)
        {
            var y = p.Y + h * 0.5f;
            dl.AddRectFilledMultiColor(
                new Vector2(after.X + 10, y), new Vector2(right, y + 1f),
                U32(Gold, 0.35f), U32(Gold, 0f), U32(Gold, 0f), U32(Gold, 0.35f));
        }

        ImGui.Dummy(new Vector2(0, 3));
    }


    /// <summary>Draws text into a fixed width, trimming with an ellipsis when it overruns.</summary>
    public static void ClipText(ImDrawListPtr dl, Vector2 pos, float maxWidth, string text, uint color)
    {
        if (maxWidth <= 8f || string.IsNullOrEmpty(text)) return;

        if (ImGui.CalcTextSize(text).X <= maxWidth)
        {
            dl.AddText(pos, color, text);
            return;
        }

        var budget = maxWidth - ImGui.CalcTextSize("...").X;
        var length = text.Length;
        while (length > 1 && ImGui.CalcTextSize(text[..length]).X > budget)
            length--;

        dl.AddText(pos, color, text[..length] + "...");
    }

    /// <summary>
    /// A wrapping row of pill tabs with glyphs. Replaces ImGui's tab bar wherever there are
    /// enough sections that the default strip would crowd or clip.
    /// </summary>
    public static bool TabStrip(string id, ref int selected, params (FontAwesomeIcon Icon, string Label)[] tabs)
    {
        var dl = ImGui.GetWindowDrawList();
        var regionLeft = ImGui.GetCursorScreenPos().X;
        var regionWidth = ImGui.GetContentRegionAvail().X;
        var h = ImGui.GetFrameHeight() + 6f;

        var changed = false;
        var x = regionLeft;
        var y = ImGui.GetCursorScreenPos().Y;
        var rows = 1;

        for (var i = 0; i < tabs.Length; i++)
        {
            var (icon, label) = tabs[i];
            var iconW = IconWidth(icon);
            var w = iconW + 8f + ImGui.CalcTextSize(label).X + 24f;

            if (x + w > regionLeft + regionWidth && x > regionLeft)
            {
                x = regionLeft;
                y += h + 5f;
                rows++;
            }

            ImGui.SetCursorScreenPos(new Vector2(x, y));
            if (ImGui.InvisibleButton($"{id}_{i}", new Vector2(w, h)))
            {
                if (selected != i) changed = true;
                selected = i;
            }

            var hovered = ImGui.IsItemHovered();
            var isSel = selected == i;
            var min = new Vector2(x, y);
            var max = new Vector2(x + w, y + h);

            if (isSel)
            {
                dl.AddRectFilled(min, max, U32(Gold, 0.16f), Radius);
                dl.AddRect(min, max, U32(Gold, 0.55f), Radius, ImDrawFlags.None, 1f);
                dl.AddRectFilled(new Vector2(min.X + 8, max.Y - 2.5f), new Vector2(max.X - 8, max.Y - 1f), U32(Gold, 0.95f), 1f);
            }
            else if (hovered)
            {
                dl.AddRectFilled(min, max, U32(Crystal, 0.14f), Radius);
            }

            var tint = isSel ? GoldBright : hovered ? Text : TextMuted;

            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                var glyph = icon.ToIconString();
                var gs = ImGui.CalcTextSize(glyph);
                dl.AddText(new Vector2(min.X + 12, min.Y + (h - gs.Y) * 0.5f), U32(tint), glyph);
            }

            var ts = ImGui.CalcTextSize(label);
            dl.AddText(new Vector2(min.X + 12 + iconW + 8, min.Y + (h - ts.Y) * 0.5f), U32(tint), label);

            x += w + 5f;
        }

        ImGui.SetCursorScreenPos(new Vector2(regionLeft, y + h));
        ImGui.Dummy(new Vector2(regionWidth, 0));
        return changed;
    }

    // ------------------------------------------------------------------
    // Panels & cards
    // ------------------------------------------------------------------

    /// <summary>
    /// A scoped child window that always calls EndChild on dispose, which ImGui requires
    /// whether or not the child turned out to be visible.
    /// </summary>
    public readonly struct CardScope : IDisposable
    {
        private readonly bool visible;
        internal CardScope(bool visible) => this.visible = visible;
        public void Dispose() => ImGui.EndChild();
        public static implicit operator bool(CardScope scope) => scope.visible;
    }

    /// <summary>A plain scrolling region. Ends unconditionally, as ImGui requires.</summary>
    public static CardScope Region(string id, Vector2 size, ImGuiWindowFlags flags = ImGuiWindowFlags.None)
        => new(ImGui.BeginChild(id, size, false, flags));

    /// <summary>
    /// A raised, bordered child. Dispose ends it unconditionally, so the body may be
    /// skipped safely when the returned handle is falsy.
    /// </summary>
    public static CardScope Card(string id, Vector2 size, bool accentTop = false, Vector4? accent = null)
    {
        var dl = ImGui.GetWindowDrawList();
        var p = ImGui.GetCursorScreenPos();
        var w = size.X > 0 ? size.X : ImGui.GetContentRegionAvail().X;
        var h = size.Y > 0 ? size.Y : ImGui.GetContentRegionAvail().Y;

        dl.AddRectFilled(p, new Vector2(p.X + w, p.Y + h), U32(Surface, 0.85f), Radius);
        dl.AddRect(p, new Vector2(p.X + w, p.Y + h), U32(Line), Radius, ImDrawFlags.None, 1f);

        if (accentTop)
        {
            var a = accent ?? Gold;
            dl.AddRectFilledMultiColor(
                new Vector2(p.X + Radius, p.Y + 1), new Vector2(p.X + w - Radius, p.Y + 2.5f),
                U32(a, 0f), U32(a, 0.9f), U32(a, 0.9f), U32(a, 0f));
        }

        ImGui.SetCursorScreenPos(p);
        using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(11, 9)))
        {
            return new CardScope(ImGui.BeginChild(id, new Vector2(w, h), false, ImGuiWindowFlags.AlwaysUseWindowPadding));
        }
    }

    /// <summary>
    /// A metric tile: large value, small caption, tinted glyph watermark.
    /// </summary>
    public static void StatCard(FontAwesomeIcon icon, string caption, string value, Vector4 accent, float width, string? hint = null)
    {
        var dl = ImGui.GetWindowDrawList();
        var p = ImGui.GetCursorScreenPos();
        const float h = 64f;
        var max = new Vector2(p.X + width, p.Y + h);

        dl.AddRectFilled(p, max, U32(Surface, 0.9f), Radius);
        dl.AddRectFilledMultiColor(p, max,
            U32(accent, 0.14f), U32(accent, 0.03f),
            U32(accent, 0.0f), U32(accent, 0.06f));
        dl.AddRect(p, max, U32(Line), Radius, ImDrawFlags.None, 1f);

        // Accent spine on the left edge.
        dl.AddRectFilled(new Vector2(p.X, p.Y + 6), new Vector2(p.X + 2.5f, max.Y - 6), U32(accent, 0.95f), 1.5f);

        // Oversized glyph watermark in the corner.
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            var glyph = icon.ToIconString();
            ImGui.SetWindowFontScale(1.9f);
            var gs = ImGui.CalcTextSize(glyph);
            dl.AddText(new Vector2(max.X - gs.X - 11, p.Y + (h - gs.Y) * 0.5f), U32(accent, 0.16f), glyph);
            ImGui.SetWindowFontScale(1f);
        }

        ImGui.SetCursorScreenPos(new Vector2(p.X + 13, p.Y + 10));
        using (new FontScale(1.34f))
        {
            ImGui.TextColored(accent, value);
        }

        ImGui.SetCursorScreenPos(new Vector2(p.X + 13, p.Y + 38));
        using (new FontScale(0.86f))
        {
            ImGui.TextColored(TextMuted, caption.ToUpperInvariant());
        }

        ImGui.SetCursorScreenPos(p);
        ImGui.Dummy(new Vector2(width, h));
        if (hint is not null && ImGui.IsItemHovered())
            Tooltip(hint);
    }

    /// <summary>A rounded pill used for counts, tags and states.</summary>
    public static void Badge(string label, Vector4 color, bool filled = false)
    {
        var dl = ImGui.GetWindowDrawList();
        var p = ImGui.GetCursorScreenPos();
        var ts = ImGui.CalcTextSize(label);
        var padX = 7f;
        var padY = 2.5f;
        var max = new Vector2(p.X + ts.X + padX * 2, p.Y + ts.Y + padY * 2);
        var r = (max.Y - p.Y) * 0.5f;

        dl.AddRectFilled(p, max, U32(color, filled ? 0.85f : 0.16f), r);
        if (!filled)
            dl.AddRect(p, max, U32(color, 0.45f), r, ImDrawFlags.None, 1f);

        dl.AddText(new Vector2(p.X + padX, p.Y + padY), filled ? U32(Ink, 1f / Math.Max(Ink.W, 0.01f)) : U32(color), label);

        ImGui.Dummy(new Vector2(max.X - p.X, max.Y - p.Y));
    }

    public static void DrawDiamond(ImDrawListPtr dl, Vector2 center, float r, uint fill, uint border, float thickness)
    {
        Span<Vector2> pts =
        [
            new(center.X, center.Y - r),
            new(center.X + r, center.Y),
            new(center.X, center.Y + r),
            new(center.X - r, center.Y),
        ];

        dl.AddQuadFilled(pts[0], pts[1], pts[2], pts[3], fill);
        dl.AddQuad(pts[0], pts[1], pts[2], pts[3], border, thickness);
    }

    // ------------------------------------------------------------------
    // Controls
    // ------------------------------------------------------------------

    /// <summary>Filled brass button for the single most important action in a view.</summary>
    public static bool PrimaryButton(string label, Vector2 size = default)
    {
        using var c = ImRaii.PushColor(ImGuiCol.Button, Alpha(Gold, 0.9f))
            .Push(ImGuiCol.ButtonHovered, GoldBright)
            .Push(ImGuiCol.ButtonActive, Alpha(Gold, 1f))
            .Push(ImGuiCol.Text, Rgb(0x14, 0x11, 0x08));
        return ImGui.Button(label, size);
    }

    /// <summary>Outlined button - the default for secondary actions.</summary>
    public static bool GhostButton(string label, Vector2 size = default, Vector4? tint = null)
    {
        var t = tint ?? Crystal;
        using var c = ImRaii.PushColor(ImGuiCol.Button, Alpha(t, 0.10f))
            .Push(ImGuiCol.ButtonHovered, Alpha(t, 0.22f))
            .Push(ImGuiCol.ButtonActive, Alpha(t, 0.32f))
            .Push(ImGuiCol.Text, Lighten(t, 0.25f));
        return ImGui.Button(label, size);
    }

    /// <summary>Destructive action - muted until hovered so it never shouts.</summary>
    public static bool DangerButton(string label, Vector2 size = default)
    {
        using var c = ImRaii.PushColor(ImGuiCol.Button, Alpha(Bad, 0.16f))
            .Push(ImGuiCol.ButtonHovered, Alpha(Bad, 0.75f))
            .Push(ImGuiCol.ButtonActive, Alpha(Bad, 0.95f))
            .Push(ImGuiCol.Text, Lighten(Bad, 0.3f));
        return ImGui.Button(label, size);
    }

    /// <summary>Square glyph button with a hover glow. <paramref name="tint"/> colours the glyph.</summary>
    public static bool IconButton(string id, FontAwesomeIcon icon, string? tooltip = null, Vector4? tint = null, bool active = false, float size = 30f)
    {
        var dl = ImGui.GetWindowDrawList();
        var p = ImGui.GetCursorScreenPos();
        var max = new Vector2(p.X + size, p.Y + size);

        var clicked = ImGui.InvisibleButton(id, new Vector2(size, size));
        var hovered = ImGui.IsItemHovered();
        var held = ImGui.IsItemActive();

        var accent = tint ?? Crystal;
        var bg = held ? Alpha(accent, 0.34f)
               : hovered ? Alpha(accent, 0.22f)
               : active ? Alpha(accent, 0.16f)
               : Alpha(Surface, 0.9f);

        dl.AddRectFilled(p, max, U32(bg), Radius);
        dl.AddRect(p, max, U32(hovered || active ? Alpha(accent, 0.55f) : Line), Radius, ImDrawFlags.None, 1f);

        if (active)
        {
            // Selected state gets a brass underline, like a chosen tab in-game.
            dl.AddRectFilled(new Vector2(p.X + 6, max.Y - 2.5f), new Vector2(max.X - 6, max.Y - 1f), U32(accent, 0.9f), 1f);
        }

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            var glyph = icon.ToIconString();
            var gs = ImGui.CalcTextSize(glyph);
            var col = hovered || active ? Lighten(accent, 0.35f) : TextMuted;
            dl.AddText(new Vector2(p.X + (size - gs.X) * 0.5f, p.Y + (size - gs.Y) * 0.5f), U32(col), glyph);
        }

        if (tooltip is not null && hovered)
            Tooltip(tooltip);

        return clicked;
    }

    private static readonly Dictionary<string, float> ToggleAnim = new();

    /// <summary>
    /// An animated sliding switch. Reads far better than a checkbox for on/off settings.
    /// </summary>
    public static bool Toggle(string id, ref bool value)
    {
        const float w = 38f;
        const float h = 20f;

        var dl = ImGui.GetWindowDrawList();
        var p = ImGui.GetCursorScreenPos();
        var yOff = (ImGui.GetFrameHeight() - h) * 0.5f;
        p = new Vector2(p.X, p.Y + Math.Max(yOff, 0));

        ImGui.SetCursorScreenPos(p);
        var changed = ImGui.InvisibleButton(id, new Vector2(w, h));
        if (changed) value = !value;
        var hovered = ImGui.IsItemHovered();

        if (!ToggleAnim.TryGetValue(id, out var t)) t = value ? 1f : 0f;
        var target = value ? 1f : 0f;
        t += (target - t) * Math.Clamp(ImGui.GetIO().DeltaTime * 14f, 0f, 1f);
        if (Math.Abs(target - t) < 0.002f) t = target;
        ToggleAnim[id] = t;

        var track = Mix(Surface, Alpha(Gold, 0.55f), t);
        var max = new Vector2(p.X + w, p.Y + h);
        dl.AddRectFilled(p, max, U32(track), h * 0.5f);
        dl.AddRect(p, max, U32(Mix(Line, Gold, t), hovered ? 1f : 0.7f), h * 0.5f, ImDrawFlags.None, 1f);

        var knobR = h * 0.5f - 3f;
        var knobX = p.X + 3f + knobR + (w - 6f - knobR * 2f) * t;
        var knobC = new Vector2(knobX, p.Y + h * 0.5f);

        if (t > 0.05f)
            dl.AddCircleFilled(knobC, knobR + 3.5f * t, U32(GoldBright, 0.22f * t), 20);

        dl.AddCircleFilled(knobC, knobR, U32(Mix(TextMuted, GoldBright, t)), 20);

        ImGui.SetCursorScreenPos(new Vector2(p.X, p.Y - Math.Max(yOff, 0)));
        ImGui.Dummy(new Vector2(w, ImGui.GetFrameHeight()));

        return changed;
    }

    /// <summary>Toggle with a label and an optional help bubble, laid out as one settings row.</summary>
    public static bool ToggleRow(string label, ref bool value, string? help = null)
    {
        var changed = Toggle($"##tgl_{label}", ref value);
        ImGui.SameLine(0, 10);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(value ? Text : TextMuted, label);
        if (help is not null)
        {
            ImGui.SameLine(0, 6);
            HelpMarker(help);
        }
        return changed;
    }

    /// <summary>A muted question mark that reveals a styled tooltip.</summary>
    public static void HelpMarker(string text)
    {
        var dl = ImGui.GetWindowDrawList();
        var p = ImGui.GetCursorScreenPos();
        var r = 7f;
        var center = new Vector2(p.X + r, p.Y + ImGui.GetTextLineHeight() * 0.5f + 1);

        ImGui.Dummy(new Vector2(r * 2, ImGui.GetTextLineHeight()));
        var hovered = ImGui.IsItemHovered();

        dl.AddCircle(center, r, U32(hovered ? Crystal : TextFaint, 0.9f), 16, 1.2f);
        var ts = ImGui.CalcTextSize("?");
        dl.AddText(center - ts * 0.5f, U32(hovered ? CrystalBright : TextFaint), "?");

        if (hovered)
            Tooltip(text);
    }

    /// <summary>Tooltip in the plugin's own frame rather than the ImGui default.</summary>
    public static void Tooltip(string text)
    {
        using var s = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(11, 9))
            .Push(ImGuiStyleVar.WindowRounding, Radius);
        using var c = ImRaii.PushColor(ImGuiCol.PopupBg, Rgb(0x0E, 0x14, 0x1F, 0.98f))
            .Push(ImGuiCol.Border, Alpha(Gold, 0.4f));

        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 24f);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    /// <summary>
    /// A segmented control - one capsule, N options, brass fill on the active one.
    /// Returns true when the selection changed.
    /// </summary>
    public static bool SegmentedControl(string id, ref int selected, params string[] options)
    {
        var dl = ImGui.GetWindowDrawList();
        var p = ImGui.GetCursorScreenPos();
        var h = ImGui.GetFrameHeight() + 2f;

        var widths = new float[options.Length];
        var total = 0f;
        for (var i = 0; i < options.Length; i++)
        {
            widths[i] = ImGui.CalcTextSize(options[i]).X + 22f;
            total += widths[i];
        }

        dl.AddRectFilled(p, new Vector2(p.X + total + 6, p.Y + h), U32(Panel, 0.9f), Radius);
        dl.AddRect(p, new Vector2(p.X + total + 6, p.Y + h), U32(Line), Radius, ImDrawFlags.None, 1f);

        var changed = false;
        var x = p.X + 3f;

        for (var i = 0; i < options.Length; i++)
        {
            ImGui.SetCursorScreenPos(new Vector2(x, p.Y + 3f));
            if (ImGui.InvisibleButton($"{id}_{i}", new Vector2(widths[i], h - 6f)))
            {
                if (selected != i) changed = true;
                selected = i;
            }

            var hovered = ImGui.IsItemHovered();
            var isSel = selected == i;
            var segMin = new Vector2(x, p.Y + 3f);
            var segMax = new Vector2(x + widths[i], p.Y + h - 3f);

            if (isSel)
            {
                dl.AddRectFilled(segMin, segMax, U32(Gold, 0.85f), Radius - 2f);
            }
            else if (hovered)
            {
                dl.AddRectFilled(segMin, segMax, U32(Crystal, 0.16f), Radius - 2f);
            }

            var ts = ImGui.CalcTextSize(options[i]);
            var tc = isSel ? Rgb(0x14, 0x11, 0x08) : hovered ? Text : TextMuted;
            dl.AddText(new Vector2(x + (widths[i] - ts.X) * 0.5f, p.Y + (h - ts.Y) * 0.5f), U32(tc), options[i]);

            x += widths[i];
        }

        ImGui.SetCursorScreenPos(p);
        ImGui.Dummy(new Vector2(total + 6, h));
        return changed;
    }

    /// <summary>Slim slider with a brass fill and the value rendered inside the track.</summary>
    public static bool Slider(string label, ref float value, float min, float max, string format = "%.2f", float width = 180f)
    {
        using var c = ImRaii.PushColor(ImGuiCol.FrameBg, Panel)
            .Push(ImGuiCol.FrameBgHovered, Surface)
            .Push(ImGuiCol.FrameBgActive, Surface)
            .Push(ImGuiCol.SliderGrab, Gold)
            .Push(ImGuiCol.SliderGrabActive, GoldBright);
        using var s = ImRaii.PushStyle(ImGuiStyleVar.GrabRounding, 4f);

        ImGui.SetNextItemWidth(width);
        return ImGui.SliderFloat(label, ref value, min, max, format);
    }

    public static bool SliderInt(string label, ref int value, int min, int max, float width = 180f)
    {
        using var c = ImRaii.PushColor(ImGuiCol.FrameBg, Panel)
            .Push(ImGuiCol.FrameBgHovered, Surface)
            .Push(ImGuiCol.FrameBgActive, Surface)
            .Push(ImGuiCol.SliderGrab, Gold)
            .Push(ImGuiCol.SliderGrabActive, GoldBright);
        using var s = ImRaii.PushStyle(ImGuiStyleVar.GrabRounding, 4f);

        ImGui.SetNextItemWidth(width);
        return ImGui.SliderInt(label, ref value, min, max);
    }

    // ------------------------------------------------------------------
    // States & feedback
    // ------------------------------------------------------------------

    /// <summary>Centred placeholder for empty lists - glyph, headline, one line of guidance.</summary>
    public static void EmptyState(FontAwesomeIcon icon, string title, string? detail = null)
    {
        var avail = ImGui.GetContentRegionAvail();
        var blockHeight = detail is null ? 86f : 108f;
        var top = ImGui.GetCursorPosY() + Math.Max((avail.Y - blockHeight) * 0.45f, 8f);

        ImGui.SetCursorPosY(top);

        var dl = ImGui.GetWindowDrawList();
        var center = new Vector2(
            ImGui.GetWindowPos().X + ImGui.GetWindowSize().X * 0.5f,
            ImGui.GetCursorScreenPos().Y + 24f);

        // Slowly breathing ring behind the glyph.
        var pulse = 0.5f + 0.5f * MathF.Sin(Time * 1.3f);
        dl.AddCircle(center, 26f + pulse * 3f, U32(Gold, 0.18f + pulse * 0.12f), 48, 1.2f);
        dl.AddCircle(center, 19f, U32(Gold, 0.1f), 32, 1f);

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            ImGui.SetWindowFontScale(1.6f);
            var glyph = icon.ToIconString();
            var gs = ImGui.CalcTextSize(glyph);
            dl.AddText(center - gs * 0.5f, U32(Gold, 0.55f), glyph);
            ImGui.SetWindowFontScale(1f);
        }

        ImGui.Dummy(new Vector2(0, 54));

        CenteredText(title, Text);
        if (detail is not null)
        {
            ImGui.Dummy(new Vector2(0, 2));
            CenteredText(detail, TextFaint);
        }
    }

    public static void CenteredText(string text, Vector4 color)
    {
        var w = ImGui.GetContentRegionAvail().X;
        var ts = ImGui.CalcTextSize(text);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max((w - ts.X) * 0.5f, 0));
        ImGui.TextColored(color, text);
    }

    /// <summary>An arc that chases its own tail - used while something loads.</summary>
    public static void Spinner(float radius = 13f, float thickness = 2.5f, Vector4? color = null)
    {
        var p = ImGui.GetCursorScreenPos();
        DrawSpinner(ImGui.GetWindowDrawList(), new Vector2(p.X + radius, p.Y + radius), radius, thickness, color);
        ImGui.Dummy(new Vector2(radius * 2, radius * 2));
    }

    /// <summary>
    /// The spinner arc, drawn straight into a draw list at a given centre. For places that
    /// lay themselves out rather than using the cursor.
    /// </summary>
    public static void DrawSpinner(ImDrawListPtr dl, Vector2 center, float radius, float thickness, Vector4? color = null)
    {
        var c = color ?? Gold;

        var t = Time * 2.4f;
        const int segments = 40;
        const float arc = MathF.PI * 1.35f;

        dl.AddCircle(center, radius, U32(c, 0.12f), 48, thickness);

        dl.PathClear();
        for (var i = 0; i <= segments; i++)
        {
            var a = t + arc * i / segments;
            dl.PathLineTo(new Vector2(center.X + MathF.Cos(a) * radius, center.Y + MathF.Sin(a) * radius));
        }
        dl.PathStroke(U32(c, 0.95f), ImDrawFlags.None, thickness);
    }

    /// <summary>A bordered notice box - info, caution or error.</summary>
    public static void Callout(FontAwesomeIcon icon, string title, string body, Vector4 accent)
    {
        var dl = ImGui.GetWindowDrawList();
        var p = ImGui.GetCursorScreenPos();
        var w = ImGui.GetContentRegionAvail().X;

        ImGui.SetCursorScreenPos(new Vector2(p.X + 34, p.Y + 8));
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + w - 46);
        ImGui.TextColored(accent, title);
        ImGui.TextColored(TextMuted, body);
        ImGui.PopTextWrapPos();
        var bottom = ImGui.GetCursorScreenPos().Y + 8;

        var max = new Vector2(p.X + w, bottom);
        dl.AddRectFilled(p, max, U32(accent, 0.07f), Radius);
        dl.AddRect(p, max, U32(accent, 0.3f), Radius, ImDrawFlags.None, 1f);
        dl.AddRectFilled(p, new Vector2(p.X + 2.5f, max.Y), U32(accent, 0.8f), 1.5f);

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            var glyph = icon.ToIconString();
            var gs = ImGui.CalcTextSize(glyph);
            dl.AddText(new Vector2(p.X + 17 - gs.X * 0.5f, p.Y + 10), U32(accent), glyph);
        }

        ImGui.SetCursorScreenPos(p);
        ImGui.Dummy(new Vector2(w, bottom - p.Y));
    }

    /// <summary>Horizontal meter with an optional inline label.</summary>
    public static void Meter(float fraction, float width, float height, Vector4 color, string? label = null)
    {
        var dl = ImGui.GetWindowDrawList();
        var p = ImGui.GetCursorScreenPos();
        var max = new Vector2(p.X + width, p.Y + height);
        var r = height * 0.5f;

        dl.AddRectFilled(p, max, U32(Panel, 0.9f), r);

        var f = Math.Clamp(fraction, 0f, 1f);
        if (f > 0.001f)
        {
            var fillMax = new Vector2(p.X + Math.Max(width * f, height), max.Y);
            dl.AddRectFilledMultiColor(p, fillMax,
                U32(color, 0.65f), U32(Lighten(color, 0.3f), 0.95f),
                U32(Lighten(color, 0.3f), 0.95f), U32(color, 0.65f));
            dl.AddRectFilled(p, fillMax, 0, r);
        }

        if (label is not null)
        {
            var ts = ImGui.CalcTextSize(label);
            dl.AddText(new Vector2(max.X - ts.X - 6, p.Y + (height - ts.Y) * 0.5f), U32(Text, 0.9f), label);
        }

        ImGui.Dummy(new Vector2(width, height));
    }
}

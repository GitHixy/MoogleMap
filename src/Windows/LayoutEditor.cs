using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using MoogleMap.Rendering;

namespace MoogleMap.Windows;

/// <summary>
/// Moving and resizing the map right on screen. While it's on, the map shows its outline with a
/// handle on each corner and side: drag inside to move it, drag a handle to resize it from that
/// side, with Shift to keep its proportions. The outline is never shown otherwise.
/// </summary>
public sealed class LayoutEditor
{
    private readonly Plugin plugin;

    /// <summary>The handle being dragged, as which way it points from the centre, or null.</summary>
    private Vector2? dragging;

    public LayoutEditor(Plugin plugin)
    {
        this.plugin = plugin;
    }

    public bool Editing { get; private set; }

    public void Start() => Editing = true;

    public void Stop()
    {
        if (!Editing) return;
        Editing = false;
        dragging = null;
        plugin.ConfigService.Save();
    }

    public void Draw(MapView? shown)
    {
        if (!Editing || shown is not { } view) return;

        var viewport = ImGui.GetMainViewport();
        var px = view.PixelScale;
        var handle = 7f * px;
        var margin = new Vector2(handle * 3f);
        var min = view.Center - view.Radii;
        var max = view.Center + view.Radii;

        ImGui.SetNextWindowPos(min - margin);
        ImGui.SetNextWindowSize(max - min + margin * 2f);
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoSavedSettings
                                       | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoBringToFrontOnFocus
                                       | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoScrollbar;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        var open = ImGui.Begin("##MoogleMapLayout", flags);
        ImGui.PopStyleVar(2);
        if (!open)
        {
            ImGui.End();
            return;
        }

        var dl = ImGui.GetWindowDrawList();
        var mouse = ImGui.GetMousePos();

        // The button to finish sits inside the map near its bottom, where it's easy to find.
        const string doneLabel = "Done";
        var doneSize = ImGui.CalcTextSize(doneLabel) + new Vector2(28f, 12f) * px;
        var doneCentre = new Vector2(view.Center.X, max.Y - view.FadeWidth * 0.5f - doneSize.Y);
        var doneMin = doneCentre - doneSize * 0.5f;
        var doneMax = doneCentre + doneSize * 0.5f;
        var overDone = mouse.X >= doneMin.X && mouse.X <= doneMax.X && mouse.Y >= doneMin.Y && mouse.Y <= doneMax.Y;

        // One invisible button over everything takes the mouse from the game; what it grabbed is
        // worked out by hand, so overlapping handles never fight over it.
        ImGui.SetCursorScreenPos(min - margin);
        ImGui.InvisibleButton("##layout", max - min + margin * 2f);
        var hovered = ImGui.IsItemHovered();
        var hover = hovered && !overDone ? HitTest(view, mouse, handle * 1.8f) : null;

        if (ImGui.IsItemActivated())
        {
            if (overDone)
            {
                ImGui.End();
                Stop();
                return;
            }
            dragging = hover;
        }

        if (ImGui.IsItemActive() && dragging is { } side)
            Apply(view, side, ImGui.GetIO().MouseDelta, ImGui.GetIO().KeyShift, viewport.Size);
        else if (dragging is not null)
        {
            dragging = null;
            plugin.ConfigService.Save();
        }

        var active = dragging ?? hover;
        if (active is { } cursor)
            ImGui.SetMouseCursor(Cursor(cursor));

        // The outline, and for a round map the box its handles sit on.
        var gold = Draw2D.Gold;
        if (view.Round)
            dl.AddRect(min, max, Draw2D.Color(gold, 0.25f), 0f, ImDrawFlags.None, 1f * px);
        view.PathOutline(dl, 1f);
        dl.PathStroke(Draw2D.Color(gold, 0.9f), ImDrawFlags.Closed, 1.5f * px);

        for (var y = -1; y <= 1; y++)
        {
            for (var x = -1; x <= 1; x++)
            {
                if (x == 0 && y == 0) continue;
                var at = view.Center + new Vector2(x, y) * view.Radii;
                var lit = active is { } a && a == new Vector2(x, y);
                dl.AddRectFilled(at - new Vector2(handle), at + new Vector2(handle), Draw2D.Color(lit ? Draw2D.Parchment : gold, 0.95f), 2f * px);
                dl.AddRect(at - new Vector2(handle), at + new Vector2(handle), Draw2D.Color(new Vector4(0.03f, 0.05f, 0.09f, 1f), 0.9f), 2f * px, ImDrawFlags.None, 1f * px);
            }
        }

        var hint = "Drag to move, drag the edges to resize, Shift keeps the proportions";
        Draw2D.Label(dl, new Vector2(view.Center.X, min.Y + view.FadeWidth * 0.5f + 14f * px), hint, Draw2D.Parchment, 0.9f, 0.9f, centered: true);

        dl.AddRectFilled(doneMin, doneMax, Draw2D.Color(new Vector4(0.03f, 0.05f, 0.09f, 1f), 0.85f), doneSize.Y * 0.5f);
        dl.AddRect(doneMin, doneMax, Draw2D.Color(gold, overDone ? 1f : 0.7f), doneSize.Y * 0.5f, ImDrawFlags.None, 1.5f * px);
        Draw2D.Label(dl, doneCentre, doneLabel, overDone ? Draw2D.Parchment : gold, 1f, 1f, centered: true);

        ImGui.End();
    }

    /// <summary>
    /// What's under the mouse: a handle, as which way it points from the centre, (0, 0) for the
    /// inside of the map, or null for neither.
    /// </summary>
    private static Vector2? HitTest(MapView view, Vector2 mouse, float reach)
    {
        Vector2? best = null;
        var bestDistance = reach;
        for (var y = -1; y <= 1; y++)
        {
            for (var x = -1; x <= 1; x++)
            {
                if (x == 0 && y == 0) continue;
                var side = new Vector2(x, y);
                var distance = Vector2.Distance(mouse, view.Center + side * view.Radii);
                if (distance < bestDistance)
                {
                    best = side;
                    bestDistance = distance;
                }
            }
        }

        if (best is not null) return best;
        return view.Depth(mouse) > 0f ? Vector2.Zero : null;
    }

    /// <summary>Moves the map, or resizes it from one side so the opposite side stays where it is.</summary>
    private void Apply(MapView view, Vector2 side, Vector2 delta, bool keepProportions, Vector2 screen)
    {
        var config = plugin.Configuration;
        if (delta == Vector2.Zero) return;

        if (side == Vector2.Zero)
        {
            config.Offset = Vector2.Clamp(config.Offset + delta / screen, new Vector2(-0.5f), new Vector2(0.5f));
            return;
        }

        var before = config.Size;
        if (keepProportions)
        {
            // Grows evenly from the centre by however far the handle moved outwards.
            var outward = Vector2.Dot(delta, Vector2.Normalize(side * view.Radii));
            var k = Math.Max(0.05f, 1f + outward / view.Radii.Length());
            config.Resize(before * k);
            return;
        }

        // Half of what the handle moved goes to the size, and the centre follows by the rest.
        config.Resize(before + delta * side * 0.5f / screen.Y);
        var grown = (config.Size - before) * screen.Y;
        config.Offset = Vector2.Clamp(config.Offset + grown * side / screen, new Vector2(-0.5f), new Vector2(0.5f));
    }

    private static ImGuiMouseCursor Cursor(Vector2 side) => (side.X, side.Y) switch
    {
        (0, 0) => ImGuiMouseCursor.ResizeAll,
        (0, _) => ImGuiMouseCursor.ResizeNs,
        (_, 0) => ImGuiMouseCursor.ResizeEw,
        _ when side.X == side.Y => ImGuiMouseCursor.ResizeNwse,
        _ => ImGuiMouseCursor.ResizeNesw,
    };
}

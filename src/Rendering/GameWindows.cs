using System;
using System.Collections.Generic;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace MoogleMap.Rendering;

/// <summary>
/// Keeps the map under the game's own windows. ImGui always draws over the game UI, so instead
/// the screen is cut into the rectangles not covered by an open window, and the map is drawn
/// clipped to each of them: menus then sit on top of it, edges exact.
/// </summary>
public static class GameWindows
{
    /// <summary>
    /// Past this many pieces the map would be drawn too many times over; it's drawn whole instead.
    /// Merging the free cells keeps even a busy screen (crafting, with every bar up) well under it.
    /// </summary>
    private const int MaxPieces = 96;

    private static readonly List<(Vector2 Min, Vector2 Max)> Blockers = [];
    private static readonly List<(Vector2 Min, Vector2 Max)> Pieces = [];
    private static readonly List<string> Names = [];
    private static readonly List<float> Xs = [];
    private static readonly List<float> Ys = [];
    /// <summary>Free runs reaching down to the row being built, as column spans, with where each started.</summary>
    private static readonly List<(int First, int Last, float Top)> Open = [];
    private static readonly List<(int First, int Last, float Top)> Still = [];

    /// <summary>Whether the map was left uncut on the last frame because the screen was too busy.</summary>
    public static bool GaveUp { get; private set; }

    /// <summary>Game windows the map was kept clear of on the last frame, for the Diagnostics page.</summary>
    public static string Covering => Names.Count == 0 ? "none" : string.Join(", ", Names);

    /// <summary>
    /// Rectangles of the screen the map may draw in this frame, or null when no game window is
    /// in the way. Only windows overlapping <paramref name="area"/> (the map's bounds) matter.
    /// </summary>
    /// <param name="hud">Keep the HUD (bars, party list, gauges, notifications) on top too, not just windows.</param>
    public static unsafe IReadOnlyList<(Vector2 Min, Vector2 Max)>? FreeAreas(Vector2 origin, Vector2 screen, Vector2 areaMin, Vector2 areaMax,
        ICollection<string> ignored, bool hud)
    {
        Blockers.Clear();
        Names.Clear();
        GaveUp = false;

        var manager = RaptureAtkUnitManager.Instance();
        if (manager is null) return null;

        var list = manager->AtkUnitManager.AllLoadedUnitsList;
        var entries = list.Entries;
        for (var i = 0; i < list.Count && i < entries.Length; i++)
        {
            var unit = entries[i].Value;
            if (unit is null || !unit->IsVisible || unit->RootNode is null || !unit->RootNode->IsVisible()) continue;

            // Loaded and flagged visible but faded right out: nothing is actually on screen.
            if (unit->Alpha == 0 || unit->RootNode->Color.A == 0) continue;

            var name = unit->NameString;
            if (IsOverlay(name) || (!hud && IsHud(name)) || ignored.Contains(name)) continue;

            var min = origin + new Vector2(unit->X, unit->Y);
            var size = new Vector2(unit->GetScaledWidth(true), unit->GetScaledHeight(true));
            if (size.X < 8f || size.Y < 8f) continue;

            // Full screen layers (fades, cutscene bars, screen effects) aren't windows.
            if (size.X >= screen.X * 0.9f && size.Y >= screen.Y * 0.9f) continue;

            var max = min + size;
            if (max.X <= areaMin.X || min.X >= areaMax.X || max.Y <= areaMin.Y || min.Y >= areaMax.Y) continue;

            // A little slack for the soft shadow around game windows.
            Blockers.Add((min - new Vector2(2f), max + new Vector2(2f)));
            Names.Add(name);
        }

        if (Blockers.Count == 0) return null;

        Cut(areaMin, areaMax);
        if (Pieces.Count > MaxPieces)
        {
            GaveUp = true;
            return null;
        }
        return Pieces;
    }

    /// <summary>
    /// Cuts the area into the rectangles no blocker covers. Every blocker edge becomes a grid
    /// line; free cells are joined into runs along each row, and a run carries on down while the
    /// rows below have the same one, so overlapping and touching windows don't multiply pieces.
    /// </summary>
    private static void Cut(Vector2 areaMin, Vector2 areaMax)
    {
        Lines(Xs, areaMin.X, areaMax.X, true);
        Lines(Ys, areaMin.Y, areaMax.Y, false);
        Pieces.Clear();
        Open.Clear();

        for (var row = 0; row < Ys.Count - 1; row++)
        {
            var top = Ys[row];
            var bottom = Ys[row + 1];
            var mid = (top + bottom) * 0.5f;

            Still.Clear();
            var first = -1;
            for (var col = 0; col <= Xs.Count - 1; col++)
            {
                var free = col < Xs.Count - 1 && !Covered((Xs[col] + Xs[col + 1]) * 0.5f, mid);
                if (free && first < 0) first = col;
                if (free || first < 0) continue;

                // A run ends: continue the one above it if it spans the same columns.
                var last = col - 1;
                var start = top;
                var above = Open.FindIndex(r => r.First == first && r.Last == last);
                if (above >= 0)
                {
                    start = Open[above].Top;
                    Open.RemoveAt(above);
                }
                Still.Add((first, last, start));
                first = -1;
            }

            // Runs above that didn't carry on end at this row's top.
            foreach (var (f, l, t) in Open)
                Pieces.Add((new Vector2(Xs[f], t), new Vector2(Xs[l + 1], top)));
            Open.Clear();
            Open.AddRange(Still);
        }

        foreach (var (f, l, t) in Open)
            Pieces.Add((new Vector2(Xs[f], t), new Vector2(Xs[l + 1], Ys[^1])));
    }

    /// <summary>The area's edges and every blocker edge inside it, sorted, along one axis.</summary>
    private static void Lines(List<float> into, float min, float max, bool x)
    {
        into.Clear();
        into.Add(min);
        into.Add(max);
        foreach (var (bMin, bMax) in Blockers)
        {
            var a = x ? bMin.X : bMin.Y;
            var b = x ? bMax.X : bMax.Y;
            if (a > min && a < max) into.Add(a);
            if (b > min && b < max) into.Add(b);
        }
        into.Sort();

        // Lines closer than a pixel make slivers of cells; keep one.
        for (var i = into.Count - 1; i > 0; i--)
            if (into[i] - into[i - 1] < 1f)
                into.RemoveAt(i == into.Count - 1 ? i - 1 : i);
    }

    private static bool Covered(float x, float y)
    {
        foreach (var (bMin, bMax) in Blockers)
            if (x >= bMin.X && x < bMax.X && y >= bMin.Y && y < bMax.Y)
                return true;
        return false;
    }

    /// <summary>
    /// HUD elements: bars, party list, gauges, the chat, notifications. Their names start with an
    /// underscore, apart from the chat and the job gauges.
    /// </summary>
    private static bool IsHud(string name)
        => name[0] == '_'
           || name.StartsWith("ChatLog", StringComparison.Ordinal)
           // Job gauges are HUD too, though unlike the rest their names have no underscore. A
           // gauge not unlocked yet is still loaded and "visible" with its contents hidden.
           || name.StartsWith("JobHud", StringComparison.Ordinal);

    /// <summary>
    /// Layers that are never kept clear: they cover the whole screen or float over the world, and
    /// cutting the map around them would leave holes with nothing in them.
    /// </summary>
    private static bool IsOverlay(string name)
        => name.Length == 0
           || name.StartsWith("NamePlate", StringComparison.Ordinal)
           || name.StartsWith("ScreenInfo", StringComparison.Ordinal)
           || name.StartsWith("Fade", StringComparison.Ordinal)
           // Speech bubbles over NPCs' heads, and other overlays that come and go on their own.
           || name.StartsWith("MiniTalk", StringComparison.Ordinal)
           || name.StartsWith("Bubble", StringComparison.Ordinal)
           || name.StartsWith("FlyText", StringComparison.Ordinal)
           || name.StartsWith("PopUpText", StringComparison.Ordinal)
           || name.StartsWith("ScreenLog", StringComparison.Ordinal)
           || name.StartsWith("TargetCursor", StringComparison.Ordinal)
           // Big text across the middle of the screen: zone names, errors, duty messages.
           || name.StartsWith("_ScreenText", StringComparison.Ordinal)
           || name.StartsWith("_AreaText", StringComparison.Ordinal)
           || name.StartsWith("_WideText", StringComparison.Ordinal)
           || name.StartsWith("_TextError", StringComparison.Ordinal)
           || name.StartsWith("_TextClassChange", StringComparison.Ordinal)
           || name.StartsWith("_LocationTitle", StringComparison.Ordinal)
           || name.StartsWith("_PopUpText", StringComparison.Ordinal)
           || name.StartsWith("_FlyText", StringComparison.Ordinal)
           || name.StartsWith("_MiniTalk", StringComparison.Ordinal);

    /// <summary>Names of the windows the map was kept clear of on the last frame.</summary>
    public static IReadOnlyList<string> CoveringNames => Names;
}

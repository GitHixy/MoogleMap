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
    /// <summary>Past this many pieces the screen is too busy to cut up; the map is simply drawn.</summary>
    private const int MaxPieces = 48;

    private static readonly List<(Vector2 Min, Vector2 Max)> Blockers = [];
    private static readonly List<(Vector2 Min, Vector2 Max)> Pieces = [];
    private static readonly List<(Vector2 Min, Vector2 Max)> Next = [];
    private static readonly List<string> Names = [];

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

        Pieces.Clear();
        Pieces.Add((areaMin, areaMax));
        foreach (var (bMin, bMax) in Blockers)
        {
            Next.Clear();
            foreach (var piece in Pieces)
                Subtract(piece, bMin, bMax, Next);

            Pieces.Clear();
            Pieces.AddRange(Next);
            if (Pieces.Count > MaxPieces) return null;
        }

        return Pieces;
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

    /// <summary>Adds what's left of <paramref name="piece"/> outside the blocker, as up to four rectangles.</summary>
    private static void Subtract((Vector2 Min, Vector2 Max) piece, Vector2 bMin, Vector2 bMax, List<(Vector2 Min, Vector2 Max)> into)
    {
        var (min, max) = piece;
        if (bMax.X <= min.X || bMin.X >= max.X || bMax.Y <= min.Y || bMin.Y >= max.Y)
        {
            into.Add(piece);
            return;
        }

        // Bands above and below span the full width; left and right fill in between.
        if (bMin.Y > min.Y) into.Add((min, new Vector2(max.X, bMin.Y)));
        if (bMax.Y < max.Y) into.Add((new Vector2(min.X, bMax.Y), max));

        var top = Math.Max(min.Y, bMin.Y);
        var bottom = Math.Min(max.Y, bMax.Y);
        if (bMin.X > min.X) into.Add((new Vector2(min.X, top), new Vector2(bMin.X, bottom)));
        if (bMax.X < max.X) into.Add((new Vector2(bMax.X, top), new Vector2(max.X, bottom)));
    }
}

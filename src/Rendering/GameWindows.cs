using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.UI;
using Bounds = FFXIVClientStructs.FFXIV.Common.Math.Bounds;
using FFXIVClientStructs.FFXIV.Component.GUI;

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
    private static readonly List<Banner> BannerList = [];
    private static readonly List<float> Xs = [];
    private static readonly List<float> Ys = [];
    /// <summary>Free runs reaching down to the row being built, as column spans, with where each started.</summary>
    private static readonly List<(int First, int Last, float Top)> Open = [];
    private static readonly List<(int First, int Last, float Top)> Still = [];

    /// <summary>Whether the map was left uncut on the last frame because the screen was too busy.</summary>
    public static bool GaveUp { get; private set; }

    /// <summary>A picture the game is showing across the screen, to draw again over the map.</summary>
    public readonly record struct Banner(ImTextureID Texture, Vector2 Min, Vector2 Max, Vector2 Uv0, Vector2 Uv1, float Alpha);

    /// <summary>
    /// The pictures of the banners in the middle of the screen (quest accepted, quest complete, duty
    /// commenced) found on the last frame. Drawn again on top of the map, so the lettering shows over
    /// it without cutting a box out of it.
    /// </summary>
    public static IReadOnlyList<Banner> Banners => BannerList;

    /// <summary>Game windows the map was kept clear of on the last frame, for the Diagnostics page.</summary>
    public static string Covering => Names.Count == 0 ? "none" : string.Join(", ", Names);

    /// <summary>
    /// Rectangles of the screen the map may draw in this frame, or null when no game window is
    /// in the way. Only windows overlapping <paramref name="area"/> (the map's bounds) matter.
    /// </summary>
    /// <param name="hud">Keep the HUD (bars, party list, gauges, notifications) on top too, not just windows.</param>
    /// <param name="messages">Gather the banners across the screen into <see cref="Banners"/>, to draw over the map.</param>
    public static unsafe IReadOnlyList<(Vector2 Min, Vector2 Max)>? FreeAreas(Vector2 origin, Vector2 screen, Vector2 areaMin, Vector2 areaMax,
        ICollection<string> ignored, bool hud, bool messages)
    {
        Blockers.Clear();
        Names.Clear();
        BannerList.Clear();
        GaveUp = false;

        var manager = RaptureAtkUnitManager.Instance();
        if (manager is null) return null;

        var now = Environment.TickCount64;
        budget = MeasuresPerFrame;
        if (now - sweptAt > 2000)
        {
            sweptAt = now;
            Stale.Clear();
            foreach (var (key, entry) in DrawnCache)
                if (now - entry.At > 2000)
                    Stale.Add(key);
            foreach (var key in Stale)
                DrawnCache.Remove(key);
        }

        var list = manager->AtkUnitManager.AllLoadedUnitsList;
        var entries = list.Entries;
        for (var i = 0; i < list.Count && i < entries.Length; i++)
        {
            var unit = entries[i].Value;
            if (unit is null || !unit->IsVisible || unit->RootNode is null || !unit->RootNode->IsVisible()) continue;

            // Loaded and flagged visible but faded right out: nothing is actually on screen.
            if (unit->Alpha == 0 || unit->RootNode->Color.A == 0) continue;

            var name = unit->NameString;
            if (name.Length == 0 || ignored.Contains(name)) continue;

            // Banners aren't cut around: their pictures are drawn again over the map instead.
            if (IsBanner(name))
            {
                if (messages)
                    GatherBanners(&unit->UldManager, origin, screen, 0);
                continue;
            }

            if (IsOverlay(name) || (!hud && IsHud(name))) continue;

            var min = origin + new Vector2(unit->X, unit->Y);
            var size = new Vector2(unit->GetScaledWidth(true), unit->GetScaledHeight(true));
            if (size.X < 8f || size.Y < 8f) continue;

            // Full screen layers (fades, cutscene bars, screen effects) aren't windows.
            if (size.X >= screen.X * 0.9f && size.Y >= screen.Y * 0.9f) continue;

            var max = min + size;
            if (max.X <= areaMin.X || min.X >= areaMax.X || max.Y <= areaMin.Y || min.Y >= areaMax.Y) continue;

            // Some stay open with nothing in them, like a target's status list when it has none:
            // only what a window actually draws is kept clear, and nothing when it draws nothing.
            var (drawnMin, drawnMax) = DrawnArea(unit, min - origin, max - origin, screen, now);
            min = Vector2.Max(min, origin + drawnMin);
            max = Vector2.Min(max, origin + drawnMax);
            if (max.X - min.X < 2f || max.Y - min.Y < 2f) continue;
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

    /// <summary>The picture banners in the middle of the screen: quest accepted, quest complete, duty commenced, level up.</summary>
    private static bool IsBanner(string name)
        => name.StartsWith("_Image", StringComparison.Ordinal);

    /// <summary>Every picture a banner shows, with where it is on screen and how faded in it is.</summary>
    private static unsafe void GatherBanners(AtkUldManager* uld, Vector2 origin, Vector2 screen, int depth)
    {
        if (uld is null || uld->NodeList is null || depth > 4) return;
        for (var i = 0; i < uld->NodeListCount; i++)
        {
            var node = uld->NodeList[i];
            if (node is null) continue;
            var alpha = Opacity(node);
            if (alpha <= 0.004f) continue;

            // Components keep their pieces in their own list.
            if ((ushort)node->Type >= 1000)
            {
                var component = node->GetComponent();
                if (component is not null)
                    GatherBanners(&component->UldManager, origin, screen, depth + 1);
                continue;
            }

            if (node->Type != NodeType.Image) continue;
            var image = node->GetAsAtkImageNode();
            if (image->PartsList is null || image->PartId >= image->PartsList->PartCount) continue;
            var part = image->PartsList->Parts[image->PartId];
            if (part.UldAsset is null) continue;

            var texture = &part.UldAsset->AtkTexture;
            if (!texture->IsTextureReady()) continue;
            var kernel = texture->GetKernelTexture();
            if (kernel is null || kernel->D3D11ShaderResourceView is null || kernel->ActualWidth == 0 || kernel->ActualHeight == 0) continue;

            Bounds bounds;
            node->GetBounds(&bounds);
            var min = new Vector2(bounds.Pos1.X, bounds.Pos1.Y);
            var max = new Vector2(bounds.Pos2.X, bounds.Pos2.Y);
            var size = max - min;
            // Full screen pieces are fades and tints, not the banner.
            if (size.X < 2f || size.Y < 2f || (size.X >= screen.X * 0.9f && size.Y >= screen.Y * 0.9f)) continue;

            // Parts are laid out for the standard texture; the high resolution one is twice the size.
            var scale = HighResolution(texture) ? 2f : 1f;
            var texels = new Vector2(kernel->ActualWidth, kernel->ActualHeight);
            var uv0 = new Vector2(part.U, part.V) * scale / texels;
            var uv1 = new Vector2(part.U + part.Width, part.V + part.Height) * scale / texels;

            BannerList.Add(new Banner(new ImTextureID(kernel->D3D11ShaderResourceView), origin + min, origin + max, uv0, uv1, alpha));
        }
    }

    /// <summary>
    /// What each window was last measured to draw, relative to its corner. Measuring walks its
    /// nodes, so each window is measured a few times a second, and only a couple per frame, so the
    /// cost is spread out rather than landing on one frame.
    /// </summary>
    private static readonly Dictionary<nint, (long At, Vector2 WindowSize, Vector2 Min, Vector2 Max)> DrawnCache = [];
    private static readonly List<nint> Stale = [];
    private static long sweptAt;
    private static int budget;
    private const long RemeasureMs = 250;
    private const int MeasuresPerFrame = 2;

    /// <summary>The box around everything a window shows, relative to the game window; empty when it shows nothing.</summary>
    private static unsafe (Vector2 Min, Vector2 Max) DrawnArea(AtkUnitBase* unit, Vector2 windowMin, Vector2 windowMax, Vector2 screen, long now)
    {
        // A window that only moved draws the same, just elsewhere. One that changed size or is due
        // is measured again when this frame's budget allows; until then the last answer stands.
        var windowSize = windowMax - windowMin;
        if (DrawnCache.TryGetValue((nint)unit, out var cached)
            && (budget <= 0 || (now - cached.At < RemeasureMs && cached.WindowSize == windowSize)))
            return (windowMin + cached.Min, windowMin + cached.Max);

        budget--;
        var walk = new DrawnWalk { Min = new Vector2(float.MaxValue), Max = new Vector2(float.MinValue), WindowMin = windowMin, WindowMax = windowMax, Screen = screen };
        Drawn(unit->RootNode, 1f, ref walk, 0);
        // Spread when each window falls due, so they don't all come round on the same frame.
        DrawnCache[(nint)unit] = (now + Random.Shared.Next(0, 60), windowSize, walk.Min - windowMin, walk.Max - windowMin);
        return (walk.Min, walk.Max);
    }

    private struct DrawnWalk
    {
        public Vector2 Min, Max, WindowMin, WindowMax, Screen;
        /// <summary>Set once what's found covers the whole window: nothing more can change the answer.</summary>
        public bool Done;
    }

    /// <summary>
    /// Walks a node and its siblings and children, top down: hidden or faded out branches are
    /// skipped whole. Pictures, frames and text that isn't empty count as drawn.
    /// </summary>
    private static unsafe void Drawn(AtkResNode* first, float alpha, ref DrawnWalk walk, int depth)
    {
        if (depth > 24) return;
        for (var node = first; node is not null && !walk.Done; node = node->PrevSiblingNode)
        {
            if (!node->IsVisible()) continue;
            var a = alpha * (node->Color.A / 255f);
            if (a <= 0.004f) continue;

            if ((ushort)node->Type >= 1000)
            {
                // Components keep their pieces under their own root.
                var component = node->GetComponent();
                if (component is not null)
                    Drawn(component->UldManager.RootNode, a, ref walk, depth + 1);
            }
            else if (node->Type is NodeType.Image or NodeType.NineGrid
                     || (node->Type == NodeType.Text && node->GetAsAtkTextNode()->NodeText.StringLength > 0))
            {
                Bounds bounds;
                node->GetBounds(&bounds);
                var min = new Vector2(bounds.Pos1.X, bounds.Pos1.Y);
                var max = new Vector2(bounds.Pos2.X, bounds.Pos2.Y);
                var size = max - min;
                // Full screen pieces are fades and tints, not the window.
                if (size.X >= 2f && size.Y >= 2f && !(size.X >= walk.Screen.X * 0.9f && size.Y >= walk.Screen.Y * 0.9f))
                {
                    walk.Min = Vector2.Min(walk.Min, min);
                    walk.Max = Vector2.Max(walk.Max, max);
                    walk.Done = walk.Min.X <= walk.WindowMin.X && walk.Min.Y <= walk.WindowMin.Y
                                && walk.Max.X >= walk.WindowMax.X && walk.Max.Y >= walk.WindowMax.Y;
                }
            }

            if (node->ChildNode is not null)
                Drawn(node->ChildNode, a, ref walk, depth + 1);
        }
    }

    private static unsafe bool HighResolution(AtkTexture* texture)
    {
        if (texture->TextureType != TextureType.Resource || texture->Resource is null || texture->Resource->TexFileResourceHandle is null)
            return false;
        return texture->Resource->TexFileResourceHandle->ResourceHandle.FileName.AsSpan().IndexOf("_hr1"u8) >= 0;
    }

    /// <summary>How opaque a node shows, from it and everything above it; zero when any of them is hidden.</summary>
    private static unsafe float Opacity(AtkResNode* node)
    {
        var alpha = 1f;
        for (var n = node; n is not null; n = n->ParentNode)
        {
            if (!n->IsVisible()) return 0f;
            alpha *= n->Color.A / 255f;
        }
        return alpha;
    }

    /// <summary>Names of the windows the map was kept clear of on the last frame.</summary>
    public static IReadOnlyList<string> CoveringNames => Names;
}

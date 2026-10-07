using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Textures;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using MoogleMap.Map;
using MoogleMap.Models;

namespace MoogleMap.Rendering;

/// <summary>
/// Draws the see-through map: the processed map texture (or the explored floor), then fixed map
/// icons, then everything live, then you. All of it goes on the background draw list, under every
/// ImGui window and over the game.
/// </summary>
public sealed class MapOverlay : IDisposable
{
    /// <summary>Cells per side of the mesh the map texture is drawn on. Per-vertex alpha gives the soft rim.</summary>
    private const int Grid = 36;

    private readonly Plugin plugin;
    private readonly ExploredMesh explored = new();
    /// <summary>The game's own headline font, for the compass: big and crisp where a scaled-up ImGui font goes soft.</summary>
    private readonly IFontHandle compassFont =
        Plugin.PluginInterface.UiBuilder.FontAtlas.NewGameFontHandle(new GameFontStyle(GameFontFamilyAndSize.TrumpGothic34));

    private float shown;
    private float zoom;
    private Vector2 cameraForward = new(0f, -1f);

    public MapOverlay(Plugin plugin)
    {
        this.plugin = plugin;
    }

    /// <summary>How far the fade in has got, 0 to 1.</summary>
    public float Shown => shown;

    public void Draw(bool visible, float dt)
    {
        var config = plugin.Configuration;
        var reduced = plugin.ReducedMotion;

        // A sonar wave goes out each time the map is opened from closed.
        if (visible && shown <= 0.05f && !reduced && config.Sonar)
            sonarAt = plugin.Now;

        // Ease in fast, out a touch slower, so a quick tap still reads as a deliberate open.
        var target = visible ? 1f : 0f;
        if (reduced)
            shown = target;
        else
            shown = MoveTowards(shown, target, dt * (visible ? 7.5f : 5.5f));

        if (zoom <= 0f || reduced)
            zoom = plugin.Zoom;
        else
            zoom += (plugin.Zoom - zoom) * Math.Clamp(dt * 12f, 0f, 1f);

        if (shown <= 0.001f) return;

        var player = plugin.Live.PlayerPosition;
        if (player is null) return;

        var view = BuildView(config, player.Value, reduced);
        var dl = ImGui.GetBackgroundDrawList();
        UpdateProgress(dt);

        // Open game windows stay on top: draw only into the parts of the screen they leave free.
        var viewport = ImGui.GetMainViewport();
        var reach = new Vector2(view.Radius + 40f * view.PixelScale);
        var free = config.StayUnderGameWindows
            ? GameWindows.FreeAreas(viewport.Pos, viewport.Size, view.Center - reach, view.Center + reach, config.IgnoredWindows, config.StayUnderHud)
            : null;
        if (free is null)
        {
            DrawContent(dl, view, config, player.Value);
            return;
        }

        foreach (var (min, max) in free)
        {
            dl.PushClipRect(min, max, true);
            DrawContent(dl, view, config, player.Value);
            dl.PopClipRect();
        }
    }

    private void DrawContent(ImDrawListPtr dl, MapView view, Configuration config, Vector3 player)
    {
        var effects = config.Effects;
        var still = plugin.ReducedMotion;

        // Older versions first, so the newest fades in on top of what it replaces.
        foreach (var layer in plugin.Maps.Layers)
        {
            if (layer.Alpha <= 0.002f) continue;
            DrawTexture(dl, view, layer.Map, layer.Texture.Handle, view.MapOpacity * layer.Alpha);
            DrawWalls(dl, view, layer.Map, layer.Walls, config, view.MapOpacity * layer.Alpha,
                effects && config.EffectSweep && !still ? (float)plugin.Now : null);
        }

        if (plugin.ExplorerMode == ExploreMode.Reveal)
            explored.Draw(dl, view, plugin.Explorer, config, view.MapOpacity);

        if (config.ShowTrail)
            DrawTrail(dl, view, config);

        if (config.ShowCompass)
            DrawCompass(dl, view, config);

        DrawSonar(dl, view, config);

        var map = plugin.Maps.Current;

        var markerAlpha = view.Alpha * config.MarkerOpacity;
        if (map is not null)
            DrawStaticMarkers(dl, view, map, config, markerAlpha);

        DrawLive(dl, view, config, markerAlpha);
        DrawSelf(dl, view, config, player, markerAlpha);
        DrawProgress(dl, view);
    }

    private float progressShown;
    private float lastProgress;

    private void UpdateProgress(float dt)
    {
        var progress = plugin.MappingProgress;
        progressShown = MoveTowards(progressShown, progress is null ? 0f : 1f, dt * 3f);
        if (progress is { } p) lastProgress = p;
    }

    /// <summary>
    /// A pill showing how far along the map is, fading out once it's done. In towns and duties it
    /// sits in the middle, just above the player's arrow: it's there for a few seconds and worth
    /// seeing. In open fields the map keeps growing as you travel, so it moves low on the map,
    /// smaller, out of the way of what's around you.
    /// </summary>
    private void DrawProgress(ImDrawListPtr dl, MapView view)
    {
        if (progressShown <= 0.01f) return;

        var field = plugin.Maps.Current?.IsOpenWorld == true;
        var a = view.Alpha * progressShown * (field ? 0.8f : 1f);
        var px = view.PixelScale;
        var label = plugin.Maps.Loading && plugin.Maps.Layers.Count == 0
            ? "Preparing map"
            : $"Mapping {lastProgress * 100f:0}%";

        var scale = field ? 0.8f : 1.15f;
        var text = ImGui.CalcTextSize(label) * scale;
        var width = Math.Max(text.X + (field ? 22f : 36f) * px, (field ? 120f : 190f) * px);
        var height = text.Y + (field ? 12f : 20f) * px;
        var centre = field
            ? view.Center + new Vector2(0f, view.Radii.Y * view.FadeStart * 0.9f - height * 0.5f)
            : view.Center - new Vector2(0f, 46f * px + height * 0.5f);
        var min = centre - new Vector2(width, height) * 0.5f;
        var max = centre + new Vector2(width, height) * 0.5f;

        dl.AddRectFilled(min - new Vector2(4f * px), max + new Vector2(4f * px), Draw2D.Color(Draw2D.Aether, a * 0.08f), height * 0.5f + 4f * px);
        dl.AddRectFilled(min, max, Draw2D.Color(new Vector4(0.03f, 0.05f, 0.09f, 1f), a * 0.85f), height * 0.5f);
        dl.AddRect(min, max, Draw2D.Color(Draw2D.Gold, a * 0.6f), height * 0.5f, ImDrawFlags.None, 1.5f * px);

        // The bar runs along the bottom edge of the pill.
        var barMin = new Vector2(min.X + height * 0.5f, max.Y - 5f * px);
        var barMax = new Vector2(max.X - height * 0.5f, max.Y - 2.5f * px);
        dl.AddRectFilled(barMin, barMax, Draw2D.Color(Draw2D.Aether, a * 0.2f), 1f);
        dl.AddRectFilled(barMin, new Vector2(barMin.X + (barMax.X - barMin.X) * lastProgress, barMax.Y), Draw2D.Color(Draw2D.Aether, a * 0.9f), 1f);

        Draw2D.Label(dl, new Vector2(centre.X, min.Y + (field ? 4f : 6f) * px + text.Y * 0.5f), label, Draw2D.Parchment, a, scale, centered: true);
    }

    private MapView BuildView(Configuration config, Vector3 player, bool reduced)
    {
        var viewport = ImGui.GetMainViewport();
        var pixelScale = Math.Max(viewport.Size.Y / 1080f, 0.5f);

        var center = viewport.Pos + viewport.Size * 0.5f;
        if (config.Anchor == MapAnchor.Character && Plugin.GameGui.WorldToScreen(player, out var feet))
            center = feet;
        center += config.Offset * viewport.Size;

        // Read even with north up: the camera cone still follows it.
        cameraForward = CameraForward() ?? cameraForward;
        var (cos, sin) = config.RotateWithCamera ? MapView.FacingUp(cameraForward) : (1f, 0f);

        // A slight grow as it fades in sells the "unfolding" without getting in the way.
        var ease = shown * shown * (3f - 2f * shown);
        var grow = reduced ? 1f : 0.94f + 0.06f * ease;

        return new MapView
        {
            Center = center,
            Radius = plugin.View.Radius * viewport.Size.Y * grow,
            // Width and height only trim the circle; its size and the map's scale stay put.
            Stretch = new Vector2(Math.Clamp(plugin.View.Width, 0.3f, 1f), Math.Clamp(plugin.View.Height, 0.3f, 1f)),
            FadeStart = 1f - Math.Clamp(config.EdgeFade, 0.02f, 0.9f),
            Origin = new Vector2(player.X, player.Z),
            Scale = zoom * pixelScale * grow,
            Cos = cos,
            Sin = sin,
            Alpha = ease,
            PixelScale = pixelScale,
            MapOpacity = plugin.View.MapOpacity,
        };
    }

    /// <summary>Where the camera looks, flattened onto the ground.</summary>
    private static unsafe Vector2? CameraForward()
    {
        var manager = CameraManager.Instance();
        if (manager is null) return null;
        var camera = manager->GetActiveCamera();
        if (camera is null) return null;

        var scene = &camera->CameraBase.SceneCamera;
        Vector3 eye = scene->Object.Position;
        Vector3 target = scene->LookAtVector;
        var forward = new Vector2(target.X - eye.X, target.Z - eye.Z);
        return forward.LengthSquared() > 1e-4f ? Vector2.Normalize(forward) : null;
    }

    // ------------------------------------------------------------------
    // Map texture
    // ------------------------------------------------------------------

    /// <summary>
    /// The texture goes on a grid covering just the part of the map inside the circle, so the rim
    /// can fade per vertex and the map can turn freely. The circle's bounding box in texture space
    /// doesn't depend on rotation, which keeps this simple.
    /// </summary>
    private static void DrawTexture(ImDrawListPtr dl, MapView view, MapInfo map, ImTextureID texture, float opacity)
    {
        var pixelsPerTexel = view.Scale / map.Scale;
        var reach = view.Radius / pixelsPerTexel;
        var centre = map.WorldToTexture(new Vector3(view.Origin.X, 0f, view.Origin.Y));

        var min = Vector2.Clamp(centre - new Vector2(reach), Vector2.Zero, new Vector2(MapInfo.TextureSize));
        var max = Vector2.Clamp(centre + new Vector2(reach), Vector2.Zero, new Vector2(MapInfo.TextureSize));
        if (max.X - min.X < 1f || max.Y - min.Y < 1f) return;

        const int side = Grid + 1;
        Span<Vector2> pos = stackalloc Vector2[side * side];
        Span<Vector2> uv = stackalloc Vector2[side * side];
        Span<uint> col = stackalloc uint[side * side];

        var any = false;
        for (var j = 0; j < side; j++)
        {
            for (var i = 0; i < side; i++)
            {
                var t = new Vector2(min.X + (max.X - min.X) * i / Grid, min.Y + (max.Y - min.Y) * j / Grid);
                var s = view.ToScreen(map.TextureToWorld(t));
                var a = view.Fade(s) * view.Alpha * opacity;
                var k = j * side + i;
                pos[k] = s;
                uv[k] = t / MapInfo.TextureSize;
                col[k] = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, a));
                any |= a > 0.002f;
            }
        }

        if (!any) return;

        dl.PushTextureID(texture);

        // Only cells with something visible in them.
        var cells = 0;
        for (var j = 0; j < Grid; j++)
            for (var i = 0; i < Grid; i++)
                if (Visible(col, j * side + i, side)) cells++;

        dl.PrimReserve(cells * 6, cells * 4);
        for (var j = 0; j < Grid; j++)
        {
            for (var i = 0; i < Grid; i++)
            {
                var k = j * side + i;
                if (!Visible(col, k, side)) continue;

                var idx = (ushort)dl.VtxCurrentIdx;
                dl.PrimWriteVtx(pos[k], uv[k], col[k]);
                dl.PrimWriteVtx(pos[k + 1], uv[k + 1], col[k + 1]);
                dl.PrimWriteVtx(pos[k + side + 1], uv[k + side + 1], col[k + side + 1]);
                dl.PrimWriteVtx(pos[k + side], uv[k + side], col[k + side]);
                dl.PrimWriteIdx(idx);
                dl.PrimWriteIdx((ushort)(idx + 1));
                dl.PrimWriteIdx((ushort)(idx + 2));
                dl.PrimWriteIdx(idx);
                dl.PrimWriteIdx((ushort)(idx + 2));
                dl.PrimWriteIdx((ushort)(idx + 3));
            }
        }

        dl.PopTextureID();
    }

    /// <summary>
    /// Wall lines on top of the floor fill, at a fixed pixel thickness so they stay sharp however far
    /// the map is zoomed. Each line is stroked in runs of equal fade, so the rim still softens them.
    /// </summary>
    private static void DrawWalls(ImDrawListPtr dl, MapView view, MapInfo map, IReadOnlyList<WallLine> walls, Configuration config, float fade, float? sweepTime)
    {
        if (walls.Count == 0) return;

        var reach = view.Radius / (view.Scale / map.Scale);
        var centre = map.WorldToTexture(new Vector3(view.Origin.X, 0f, view.Origin.Y));
        var min = centre - new Vector2(reach);
        var max = centre + new Vector2(reach);
        var opacity = view.Alpha * fade;
        var thickness = config.WallThickness * view.PixelScale;

        var visible = new List<WallLine>();
        foreach (var wall in walls)
            if (wall.Max.X >= min.X && wall.Min.X <= max.X && wall.Max.Y >= min.Y && wall.Min.Y <= max.Y)
                visible.Add(wall);
        if (visible.Count == 0) return;

        if (config.WallGlow)
            foreach (var wall in visible)
                Draw2D.FadedPolyline(dl, view, wall.Points, p => view.ToScreen(map.TextureToWorld(p)),
                    config.EdgeColor, opacity * 0.22f, thickness * 3.2f);

        foreach (var wall in visible)
            Draw2D.FadedPolyline(dl, view, wall.Points, p => view.ToScreen(map.TextureToWorld(p)),
                config.EdgeColor, opacity, thickness);

        // The sweep: a band of light running out from the player, lighting walls as it crosses them.
        if (sweepTime is { } now && Effects.Sweep(view, now) is { } band)
        {
            var bright = Draw2D.Lighten(config.EdgeColor, 0.65f);
            foreach (var wall in visible)
                Draw2D.FadedPolyline(dl, view, wall.Points, p => view.ToScreen(map.TextureToWorld(p)),
                    bright, opacity * band.Strength, thickness * 1.7f, screen => band.At(screen));
        }
    }

    private static bool Visible(Span<uint> col, int k, int side)
        => (col[k] | col[k + 1] | col[k + side] | col[k + side + 1]) >> 24 != 0;

    // ------------------------------------------------------------------
    // Fixed map icons and names
    // ------------------------------------------------------------------

    private void DrawStaticMarkers(ImDrawListPtr dl, MapView view, MapInfo map, Configuration config, float alpha)
    {
        if (!config.ShowMapIcons && !config.ShowPlaceNames) return;

        var iconSize = 22f * config.IconScale * view.PixelScale;
        foreach (var marker in plugin.Maps.Markers.All)
        {
            var s = view.ToScreen(map.TextureToWorld(marker.Position));
            var a = view.Fade(s) * alpha;
            if (a <= 0.01f) continue;

            if (marker.IsArea)
            {
                if (config.ShowPlaceNames)
                    Draw2D.Label(dl, s, marker.Label, config.PlaceNameColor, a, 1.05f * config.LabelScale, centered: true);
                continue;
            }

            if (!config.ShowMapIcons) continue;

            DrawIcon(dl, marker.Icon, s, iconSize, a);
            if (config.ShowPlaceNames && marker.Label.Length > 0)
                Draw2D.SideLabel(dl, s, iconSize * 0.5f, marker.Label, marker.LabelSide, config.IconLabelColor, a, 0.85f * config.LabelScale);
        }
    }

    /// <summary>Icons the game doesn't have, so they aren't looked up every frame.</summary>
    private static readonly HashSet<uint> MissingIcons = [];

    private static void DrawIcon(ImDrawListPtr dl, uint icon, Vector2 at, float size, float alpha)
    {
        if (icon == 0 || MissingIcons.Contains(icon)) return;

        // Some markers name icons the game doesn't ship; asking for one again every frame would
        // only fail again.
        if (!Plugin.TextureProvider.TryGetFromGameIcon(new GameIconLookup(icon), out var texture))
        {
            MissingIcons.Add(icon);
            Plugin.Log.Debug("Icon {Icon} not found; markers using it are drawn without it", icon);
            return;
        }
        var wrap = texture.GetWrapOrEmpty();
        var half = new Vector2(size * 0.5f);
        dl.AddImage(wrap.Handle, at - half, at + half, Vector2.Zero, Vector2.One, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, alpha)));
    }

    // ------------------------------------------------------------------
    // Live markers
    // ------------------------------------------------------------------

    private void DrawLive(ImDrawListPtr dl, MapView view, Configuration config, float alpha)
    {
        var markers = plugin.Live.All;
        var px = view.PixelScale;
        var time = (float)plugin.Now;

        // Areas first so every dot sits on top of them: quest zones, then FATEs.
        if (config.QuestAreas)
        {
            foreach (var m in markers)
            {
                if (m.Kind != MarkerKind.Quest || m.Radius < 1f) continue;
                var s = view.ToScreen(m.Position);
                var a = view.Fade(s) * alpha;
                if (a <= 0.01f) continue;
                var r = m.Radius * view.Scale;
                dl.AddCircleFilled(s, r, Draw2D.Color(QuestTint, a * 0.10f), 64);
                dl.AddCircle(s, r, Draw2D.Color(QuestTint, a * 0.6f), 64, 1.5f * px);
            }
        }

        foreach (var m in markers)
        {
            if (m.Kind != MarkerKind.Fate) continue;
            var s = view.ToScreen(m.Position);
            var a = view.Fade(s) * alpha;
            if (a <= 0.01f) continue;
            DrawFateArea(dl, view, m, s, a, time);
        }

        // Ordered so the important ones end up on top: objects, NPCs, enemies, people, then pins.
        DrawKinds(dl, view, config, markers, alpha, time, MarkerKind.Interactable, MarkerKind.Gathering, MarkerKind.Aetheryte, MarkerKind.Treasure);
        DrawKinds(dl, view, config, markers, alpha, time, MarkerKind.Npc, MarkerKind.Ally);
        DrawKinds(dl, view, config, markers, alpha, time, MarkerKind.Enemy);
        DrawKinds(dl, view, config, markers, alpha, time, MarkerKind.Player, MarkerKind.Party);
        DrawKinds(dl, view, config, markers, alpha, time, MarkerKind.Waymark);
        DrawKinds(dl, view, config, markers, alpha, time, MarkerKind.QuestNpc);
        DrawKinds(dl, view, config, markers, alpha, time, MarkerKind.Fate, MarkerKind.Quest, MarkerKind.Flag);

        if (config.ShowTargetRing && plugin.Live.Target is { } target)
            DrawTargetRing(dl, view, config, target.Position, target.Radius, alpha, time);

        // Signs float over the characters they mark, on top of everything.
        DrawKinds(dl, view, config, markers, alpha, time, MarkerKind.Sign);
    }

    /// <summary>A slowly turning ring of four arcs around the current target, like the game's own target circle.</summary>
    private void DrawTargetRing(ImDrawListPtr dl, MapView view, Configuration config, Vector3 position, float radius, float alpha, float time)
    {
        var s = view.ToScreen(position);
        var a = view.Fade(s) * alpha;
        if (a <= 0.01f) return;

        var px = view.PixelScale;
        var r = Math.Clamp(radius * view.Scale * 0.5f, 3.5f * px, 14f * px) + 6f * px;
        var spin = plugin.ReducedMotion ? 0f : time * 1.2f;
        for (var i = 0; i < 4; i++)
        {
            var start = spin + i * MathF.PI / 2f + 0.25f;
            dl.PathArcTo(s, r, start, start + MathF.PI / 2f - 0.5f, 10);
            dl.PathStroke(Draw2D.Color(config.TargetRingColor, a), ImDrawFlags.None, 2f * px);
        }
    }

    private double sonarAt = double.MinValue;

    /// <summary>Seconds the sonar wave takes to reach the rim.</summary>
    private const float SonarTime = 0.75f;

    /// <summary>
    /// The wave that sweeps out from you as the map opens: a bright ring with a softer echo
    /// behind it, easing out and fading as it reaches the rim.
    /// </summary>
    private void DrawSonar(ImDrawListPtr dl, MapView view, Configuration config)
    {
        var t = (float)((plugin.Now - sonarAt) / SonarTime);
        if (t < 0f || t >= 1f) return;

        var ease = 1f - (1f - t) * (1f - t);
        var fade = (1f - t) * (1f - t) * view.Alpha;
        var px = view.PixelScale;
        var colour = config.EdgeColor;

        var radii = view.Radii * ease;
        Ring(dl, view.Center, radii, Draw2D.Color(colour, fade * 0.25f), 10f * px);
        Ring(dl, view.Center, radii, Draw2D.Color(colour, fade * 0.9f), 2.5f * px);

        var echo = view.Radii * Math.Max(0f, ease - 0.12f);
        if (echo.X > 2f && echo.Y > 2f)
            Ring(dl, view.Center, echo, Draw2D.Color(colour, fade * 0.35f), 1.5f * px);
    }

    /// <summary>A ring the shape of the map: a circle, or an ellipse when it's stretched.</summary>
    private static void Ring(ImDrawListPtr dl, Vector2 centre, Vector2 radii, uint colour, float thickness)
    {
        const int segments = 96;
        for (var i = 0; i < segments; i++)
        {
            var a = MathF.PI * 2f * i / segments;
            dl.PathLineTo(centre + new Vector2(MathF.Cos(a) * radii.X, MathF.Sin(a) * radii.Y));
        }
        dl.PathStroke(colour, ImDrawFlags.Closed, thickness);
    }

    /// <summary>
    /// N, E, S and W, turning with the map. They sit well inside the rim rather than on it, so in
    /// big open areas they don't crowd the edge where markers and the fade already are.
    /// </summary>
    private void DrawCompass(ImDrawListPtr dl, MapView view, Configuration config)
    {
        var inset = Math.Clamp(config.CompassInset, 0.4f, 0.95f);
        var alpha = view.Alpha * config.MarkerOpacity;
        ReadOnlySpan<(string Letter, Vector2 World)> points =
        [
            ("N", new Vector2(0f, -1f)), ("E", new Vector2(1f, 0f)), ("S", new Vector2(0f, 1f)), ("W", new Vector2(-1f, 0f)),
        ];

        // Falls back to the ImGui font, scaled, for the moment the game font is still loading.
        using var locked = compassFont.Available ? compassFont.Lock() : null;
        var font = locked?.ImFont ?? ImGui.GetFont();
        var size = 34f * view.PixelScale * Math.Clamp(config.CompassScale, 0.5f, 2f);

        foreach (var (letter, world) in points)
        {
            var direction = view.Direction(world);
            var at = view.Center + direction * view.RimDistance(direction) * inset;
            var north = letter == "N";
            var colour = north ? Draw2D.Gold : Draw2D.Parchment;
            var a = alpha * (north ? 0.85f : 0.45f) * view.Fade(at);
            if (a <= 0.01f) continue;
            Draw2D.Label(dl, font, (north ? 1f : 0.82f) * size, at, letter, colour, a, centered: true);
        }
    }

    /// <summary>
    /// The trail of recent steps: a dashed line that fades with age and breaks at teleports. The
    /// dashes are laid by distance walked, so they stay where they are on the ground as the trail
    /// ages instead of crawling along it.
    /// </summary>
    private void DrawTrail(ImDrawListPtr dl, MapView view, Configuration config)
    {
        var points = plugin.Trail.Points;
        if (points.Count < 2) return;

        var now = plugin.Now;
        var keep = Math.Max(1f, config.TrailSeconds);
        var opacity = view.Alpha * config.MarkerOpacity;
        var px = view.PixelScale;
        var thickness = 2.2f * px;

        // Dash and gap in yalms, sized from pixels so they look the same at any zoom.
        var dash = 6f * px / view.Scale;
        var period = dash + 5f * px / view.Scale;

        for (var i = 1; i < points.Count; i++)
        {
            var from = points[i - 1];
            var to = points[i];
            var length = to.Walked - from.Walked;
            if (length <= 1e-4f || Vector3.Distance(from.Position, to.Position) > Trail.Break) continue;

            var age = (float)Math.Clamp((now - to.Time) / keep, 0, 1);
            var a = view.ToScreen(from.Position);
            var b = view.ToScreen(to.Position);
            var fade = (1f - age) * (1f - age) * view.Fade((a + b) * 0.5f) * opacity;
            if (fade <= 0.01f) continue;
            var colour = Draw2D.Color(config.TrailColor, fade);

            // Every dash that overlaps this segment, clipped to it.
            var first = MathF.Floor(from.Walked / period) * period;
            for (var start = first; start < to.Walked; start += period)
            {
                var s0 = Math.Max(start, from.Walked);
                var s1 = Math.Min(start + dash, to.Walked);
                if (s1 <= s0) continue;
                dl.AddLine(Vector2.Lerp(a, b, (s0 - from.Walked) / length), Vector2.Lerp(a, b, (s1 - from.Walked) / length), colour, thickness);
            }
        }
    }

    private void DrawKinds(ImDrawListPtr dl, MapView view, Configuration config, IReadOnlyList<LiveMarker> markers,
        float alpha, float time, params MarkerKind[] kinds)
    {
        var px = view.PixelScale;
        foreach (var m in markers)
        {
            if (Array.IndexOf(kinds, m.Kind) < 0) continue;

            // Aetherytes and shards are already on the map as its own icons; one is enough.
            if (m.Kind == MarkerKind.Aetheryte && config.ShowMapIcons && plugin.Maps.Markers.HasIconNear(new Vector2(m.Position.X, m.Position.Z), 6f))
                continue;

            var s = view.ToScreen(m.Position);

            // Chosen kinds stay on the rim when they're off the map, as a compass.
            var pinned = m.Kind switch
            {
                MarkerKind.Flag => config.PinFlag,
                MarkerKind.Party => config.PinParty,
                MarkerKind.Quest => config.PinQuests,
                _ => false,
            };
            var clamped = false;
            if (pinned)
                s = view.ClampToRim(s, 10f * px, out clamped);

            var a = (pinned ? Math.Max(view.Fade(s), 0.75f) : view.Fade(s)) * alpha;
            if (clamped) a *= 0.7f;
            if (a <= 0.01f) continue;

            switch (m.Kind)
            {
                case MarkerKind.Enemy:
                {
                    var r = Math.Clamp(m.Radius * view.Scale * 0.5f, 3.5f * px, 14f * px);
                    if (m.Boss) r = Math.Max(r, 9f * px);
                    // Whoever is after you first, then whoever is fighting at all.
                    var c = m.TargetsYou ? config.TargetingYouColor : m.Active ? config.AggroColor : config.EnemyColor;
                    if (m.TargetsYou)
                        dl.AddCircle(s, r + 1.5f * px, Draw2D.Color(Draw2D.Parchment, a * 0.9f), 20, 1.5f * px);
                    if (m.Active)
                    {
                        var pulse = 0.5f + 0.5f * MathF.Sin(time * 6f);
                        dl.AddCircle(s, r + (3f + 2f * pulse) * px, Draw2D.Color(c, a * (0.35f + 0.3f * pulse)), 20, 1.5f * px);
                    }
                    Draw2D.Dot(dl, s, r, c, a);
                    Draw2D.Tick(dl, s, view.Facing(m.Rotation), r, c, a);
                    if (m.Boss)
                    {
                        // The boss: a crown over it, and its name and health always shown.
                        Draw2D.Glyph(dl, s - new Vector2(0f, r + 9f * px), Dalamud.Interface.FontAwesomeIcon.Crown, 14f * px, Draw2D.Gold, a);
                        var label = m.Name is { Length: > 0 } ? $"{m.Name}  {m.Health * 100f:0}%" : $"{m.Health * 100f:0}%";
                        Draw2D.SideLabel(dl, s, r, label, 2, NameColour(config, c), a, 0.9f * config.LabelScale);
                    }
                    else if (config.EnemyNames && m.Name is { Length: > 0 })
                        Draw2D.SideLabel(dl, s, r, m.Name, 2, NameColour(config, c), a * 0.9f, 0.8f * config.LabelScale);
                    break;
                }
                case MarkerKind.Party:
                {
                    var c = config.PartyByRole ? RoleColour(config, m.Role) : config.PartyColor;
                    if (config.PartyHealth && m.Dead)
                    {
                        // Knocked out: a skull instead of a dot, so it reads at a glance. Green and glowing
                        // when a raise is already on its way, so nobody casts a second one. Once they're
                        // back on their feet this frame's data says so and the dot returns by itself.
                        if (m.Raised)
                        {
                            var glow = 0.5f + 0.5f * MathF.Sin(time * 4f);
                            dl.AddCircleFilled(s, 10f * px, Draw2D.Color(new Vector4(0.45f, 0.95f, 0.55f, 1f), a * (0.15f + 0.15f * glow)), 20);
                        }
                        Draw2D.Glyph(dl, s, Dalamud.Interface.FontAwesomeIcon.Skull, 13f * px,
                            m.Raised ? new Vector4(0.55f, 1f, 0.6f, 1f) : Draw2D.Parchment, a);
                        if (config.PartyJobs && m.Job is { Length: > 0 })
                            Draw2D.SideLabel(dl, s, 7f * px, m.Job, 2, NameColour(config, c), a * 0.9f, 0.72f * config.LabelScale);
                        break;
                    }

                    if (config.PartyHealth && !clamped)
                        DrawHealth(dl, s, 5f * px + 3f * px, m.Health, a, time, px);
                    Draw2D.Dot(dl, s, 5f * px, c, a, outline: true);
                    if (!clamped) Draw2D.Tick(dl, s, view.Facing(m.Rotation), 5f * px, c, a);
                    if (config.PartyJobs && m.Job is { Length: > 0 })
                        Draw2D.SideLabel(dl, s, 5f * px, m.Job, 2, NameColour(config, c), a * 0.9f, 0.72f * config.LabelScale);
                    break;
                }
                case MarkerKind.Player:
                    Draw2D.Dot(dl, s, 3.5f * px, config.PlayerColor, a * 0.8f);
                    break;
                case MarkerKind.Ally:
                    Draw2D.Dot(dl, s, 3.5f * px, config.PartyColor, a * 0.7f);
                    break;
                case MarkerKind.Npc:
                    Draw2D.Diamond(dl, s, 4f * px, config.NpcColor, a * 0.9f);
                    if (config.NpcNames && m.Name is { Length: > 0 })
                        Draw2D.SideLabel(dl, s, 4f * px, m.Name, 2, NameColour(config, config.NpcColor), a * 0.85f, 0.8f * config.LabelScale);
                    break;
                case MarkerKind.Treasure:
                    Draw2D.Chest(dl, s, 5.5f * px, config.ObjectColor, a);
                    if (config.ObjectNames && m.Name is { Length: > 0 })
                        Draw2D.SideLabel(dl, s, 6f * px, m.Name, 2, NameColour(config, config.ObjectColor), a * 0.9f, 0.8f * config.LabelScale);
                    break;
                case MarkerKind.Aetheryte:
                    Draw2D.Crystal(dl, s, 7f * px, Draw2D.Aether, a);
                    if (config.ObjectNames && m.Name is { Length: > 0 })
                        Draw2D.SideLabel(dl, s, 7f * px, m.Name, 2, NameColour(config, Draw2D.Aether), a * 0.9f, 0.8f * config.LabelScale);
                    break;
                case MarkerKind.Gathering:
                    Draw2D.Diamond(dl, s, 5f * px, Draw2D.Gathering, a, outline: true);
                    if (config.ObjectNames && m.Name is { Length: > 0 })
                        Draw2D.SideLabel(dl, s, 5f * px, m.Name, 2, NameColour(config, Draw2D.Gathering), a * 0.85f, 0.8f * config.LabelScale);
                    break;
                case MarkerKind.Interactable:
                    dl.AddCircle(s, 4f * px, Draw2D.Color(config.ObjectColor, a * 0.9f), 12, 1.6f * px);
                    if (config.ObjectNames && m.Name is { Length: > 0 })
                        Draw2D.SideLabel(dl, s, 4f * px, m.Name, 2, NameColour(config, config.ObjectColor), a * 0.8f, 0.78f * config.LabelScale);
                    break;
                case MarkerKind.Waymark:
                    DrawIcon(dl, m.Icon, s, 22f * config.IconScale * px, a);
                    break;
                case MarkerKind.QuestNpc:
                {
                    // The same icon the game floats over their head, a little larger than other NPCs.
                    var size = 22f * config.IconScale * px;
                    DrawIcon(dl, m.Icon, s, size, a);
                    if (config.NpcNames && m.Name is { Length: > 0 })
                        Draw2D.SideLabel(dl, s, size * 0.5f, m.Name, 2, NameColour(config, config.NpcColor), a * 0.9f, 0.8f * config.LabelScale);
                    break;
                }
                case MarkerKind.Sign:
                {
                    // Above the marked character's dot, so both stay readable.
                    var size = 20f * config.IconScale * px;
                    DrawIcon(dl, m.Icon, s - new Vector2(0f, size * 0.85f), size, a);
                    break;
                }
                case MarkerKind.Fate:
                case MarkerKind.Quest:
                case MarkerKind.Flag:
                {
                    // On the rim an icon is a pointer, not a place: smaller, with how far it is.
                    var size = 24f * config.IconScale * px * (clamped ? 0.8f : 1f);
                    if (m.Kind == MarkerKind.Fate)
                    {
                        var (grow, fade) = FateLook(m);
                        size *= grow;
                        a *= fade;
                        if (a <= 0.01f) break;
                    }
                    DrawIcon(dl, m.Icon, s, size, a);
                    if (m.Kind == MarkerKind.Quest && config.QuestNames && !clamped && m.Name is { Length: > 0 })
                        Draw2D.SideLabel(dl, s, size * 0.5f, m.Name, 2, config.IconLabelColor, a, 0.85f * config.LabelScale);
                    if (m.Kind == MarkerKind.Fate && config.FateDetails && !clamped && m.Leaving < 0f)
                        DrawFateDetails(dl, config, m, s, size, m.Radius * view.Scale, a);
                    if (clamped)
                    {
                        var distance = Vector2.Distance(new Vector2(m.Position.X, m.Position.Z), view.Origin);
                        var inward = Vector2.Normalize(view.Center - s);
                        var side = inward.X >= 0f ? (byte)2 : (byte)1;
                        Draw2D.SideLabel(dl, s, size * 0.5f, $"{distance:0}y", side, config.IconLabelColor, a, 0.8f * config.LabelScale);
                    }
                    break;
                }
            }
        }
    }

    /// <summary>
    /// A ring around you or a party member that empties with their health: green, yellow under half,
    /// red and pulsing under a quarter.
    /// </summary>
    private static void DrawHealth(ImDrawListPtr dl, Vector2 at, float r, float health, float alpha, float time, float px)
    {
        var colour = health > 0.5f ? new Vector4(0.45f, 0.9f, 0.5f, 1f)
            : health > 0.25f ? new Vector4(0.98f, 0.82f, 0.3f, 1f)
            : new Vector4(1f, 0.3f, 0.3f, 1f);
        if (health <= 0.25f)
            alpha *= 0.65f + 0.35f * MathF.Sin(time * 8f);

        dl.AddCircle(at, r, Draw2D.Color(new Vector4(0.03f, 0.05f, 0.09f, 1f), alpha * 0.55f), 24, 3f * px);
        if (health <= 0.005f) return;

        var start = -MathF.PI / 2f;
        dl.PathArcTo(at, r, start, start + MathF.PI * 2f * health, 24);
        dl.PathStroke(Draw2D.Color(colour, alpha), ImDrawFlags.None, 2f * px);
    }

    private static readonly Vector4 FateTint = new(1f, 0.62f, 0.25f, 1f);
    /// <summary>The light blue the game uses for quest areas on its map.</summary>
    private static readonly Vector4 QuestTint = new(0.4f, 0.72f, 1f, 1f);
    private static readonly Vector4 FateWon = new(1f, 0.86f, 0.42f, 1f);
    private static readonly Vector4 FateLost = new(0.55f, 0.55f, 0.6f, 1f);

    /// <summary>How big and how visible a FATE's icon is through its entrance and exit.</summary>
    private static (float Grow, float Fade) FateLook(LiveMarker fate)
    {
        if (fate.Leaving >= 0f)
        {
            var t = fate.Leaving;
            return fate.Outcome switch
            {
                FateOutcome.Completed => (1f + 0.45f * Ease(t), 1f - t * t),
                FateOutcome.Failed => (1f - 0.25f * t, 1f - t),
                _ => (1f, 1f - t),
            };
        }

        var enter = Ease(Math.Clamp(fate.Age / 0.5f, 0f, 1f));
        return (0.6f + 0.4f * enter, enter);
    }

    /// <summary>
    /// A FATE's area: it opens out as the FATE appears; on a win it flares gold and widens with
    /// sparks flying off it, on a loss it shrinks and greys out, and otherwise it just fades.
    /// </summary>
    private void DrawFateArea(ImDrawListPtr dl, MapView view, LiveMarker m, Vector2 s, float a, float time)
    {
        var px = view.PixelScale;
        var r = m.Radius * view.Scale;

        if (m.Leaving < 0f)
        {
            var enter = Ease(Math.Clamp(m.Age / 0.5f, 0f, 1f));
            if (plugin.ReducedMotion) enter = 1f;
            var rr = r * (0.7f + 0.3f * enter);
            dl.AddCircleFilled(s, rr, Draw2D.Color(FateTint, a * enter * (m.Active ? 0.12f : 0.06f)), 64);
            dl.AddCircle(s, rr, Draw2D.Color(FateTint, a * enter * 0.55f), 64, 1.5f * px);
            return;
        }

        if (plugin.ReducedMotion) return;
        var t = m.Leaving;
        switch (m.Outcome)
        {
            case FateOutcome.Completed:
            {
                var rr = r * (1f + 0.25f * Ease(t));
                var fade = (1f - t) * (1f - t);
                var colour = Vector4.Lerp(FateTint, FateWon, Math.Min(1f, t * 3f));
                dl.AddCircleFilled(s, rr, Draw2D.Color(colour, a * fade * 0.18f), 64);
                dl.AddCircle(s, rr, Draw2D.Color(colour, a * fade), 64, (1.5f + 2.5f * (1f - t)) * px);

                // Sparks thrown off the rim, spreading and fading.
                for (var i = 0; i < 12; i++)
                {
                    var angle = i * MathF.PI / 6f + 0.26f;
                    var dir = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
                    var from = s + dir * (rr + 2f * px + 14f * px * Ease(t));
                    var to = from + dir * 8f * px * (1f - t);
                    dl.AddLine(from, to, Draw2D.Color(FateWon, a * fade), 2f * px);
                }

                Draw2D.Label(dl, s - new Vector2(0f, rr + 10f * px + 12f * px * Ease(t)), "FATE complete", FateWon, a * fade, 0.95f, centered: true);
                break;
            }
            case FateOutcome.Failed:
            {
                var rr = r * (1f - 0.15f * t);
                var colour = Vector4.Lerp(FateTint, FateLost, Math.Min(1f, t * 3f));
                dl.AddCircleFilled(s, rr, Draw2D.Color(colour, a * (1f - t) * 0.08f), 64);
                dl.AddCircle(s, rr, Draw2D.Color(colour, a * (1f - t) * 0.6f), 64, 1.5f * px);
                Draw2D.Label(dl, s - new Vector2(0f, rr + 10f * px), "Failed", FateLost, a * (1f - t), 0.9f, centered: true);
                break;
            }
            default:
                dl.AddCircle(s, r, Draw2D.Color(FateTint, a * (1f - t) * 0.55f), 64, 1.5f * px);
                break;
        }
    }

    private static float Ease(float t) => t * t * (3f - 2f * t);

    /// <summary>The game's own role colours: blue tanks, green healers, red damage.</summary>
    private static Vector4 RoleColour(Configuration config, byte role) => role switch
    {
        1 => config.TankColor,
        4 => config.HealerColor,
        2 or 3 => config.DpsColor,
        _ => config.PartyColor,
    };

    /// <summary>
    /// An arc around a FATE's icon for how far along it is, and its progress and clock in white on
    /// the top edge of its area, where they don't cover the icon or what's inside.
    /// </summary>
    private static void DrawFateDetails(ImDrawListPtr dl, Configuration config, LiveMarker fate, Vector2 at, float size, float area, float alpha)
    {
        var px = size / (24f * config.IconScale);
        var r = size * 0.62f;
        var tint = new Vector4(1f, 0.62f, 0.25f, 1f);

        if (fate.Active)
        {
            // Track, then progress clockwise from the top.
            dl.AddCircle(at, r, Draw2D.Color(new Vector4(0.03f, 0.05f, 0.09f, 1f), alpha * 0.6f), 32, 3.5f * px);
            if (fate.Progress > 0.005f)
            {
                var start = -MathF.PI / 2f;
                dl.PathArcTo(at, r, start, start + MathF.PI * 2f * Math.Clamp(fate.Progress, 0f, 1f), 32);
                dl.PathStroke(Draw2D.Color(tint, alpha), ImDrawFlags.None, 2.5f * px);
            }
        }

        var label = fate.Remaining >= 0
            ? $"{fate.Progress * 100f:0}%  {fate.Remaining / 60}:{fate.Remaining % 60:00}"
            : fate.Active ? $"{fate.Progress * 100f:0}%" : "Waiting";
        // On the top of the circle, or just over the icon when the circle is smaller than it at this zoom.
        var scale = 0.8f * config.LabelScale;
        var lift = Math.Max(area, r + ImGui.GetFontSize() * scale * 0.6f + 2f * px);
        Draw2D.Label(dl, at - new Vector2(0f, lift), label, new Vector4(1f, 1f, 1f, 1f), alpha, scale, centered: true);
    }

    private static Vector4 NameColour(Configuration config, Vector4 marker)
        => config.NamesMatchMarkers ? marker : config.NameColor;

    // ------------------------------------------------------------------
    // You
    // ------------------------------------------------------------------

    private void DrawSelf(ImDrawListPtr dl, MapView view, Configuration config, Vector3 player, float alpha)
    {
        var s = view.ToScreen(player);
        var px = view.PixelScale;

        if (config.ShowViewCone)
        {
            var forward = config.RotateWithCamera ? new Vector2(0f, -1f) : Vector2.Normalize(view.Direction(cameraForward));
            Draw2D.Cone(dl, s, forward, 70f * px, 0.85f, Draw2D.Parchment, alpha * 0.13f);
        }

        if (config.Effects && config.EffectRipple && !plugin.ReducedMotion)
            Effects.Ripple(dl, s, px, (float)plugin.Now, alpha);

        if (config.SelfHealth && plugin.Live.PlayerHealth is { } health)
            DrawHealth(dl, s, 13f * px, health, alpha, (float)plugin.Now, px);

        Draw2D.Arrow(dl, s, view.Facing(plugin.Live.PlayerRotation), 9f * px, alpha);
    }

    public void Dispose() => compassFont.Dispose();

    /// <summary>Forgets per-zone state.</summary>
    public void Reset() => explored.Invalidate();

    private static float MoveTowards(float value, float target, float step)
        => value < target ? Math.Min(value + step, target) : Math.Max(value - step, target);
}

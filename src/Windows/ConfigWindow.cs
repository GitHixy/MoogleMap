using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using MoogleMap.Input;
using MoogleMap.Map;
using MoogleMap.Models;
using MoogleMap.Services;
using MoogleMap.UI;

namespace MoogleMap.Windows;

public class ConfigWindow : Window
{
    private readonly Plugin plugin;

    private int section;

    private const int ReportLineLimit = 200;
    private const float LabelWidth = 170f;
    private const float ControlWidth = 230f;

    private bool changed;
    private double saveAt = double.MaxValue;

    /// <summary>The bind waiting for a key press, if any.</summary>
    private Keybind? capturing;

    /// <summary>Which kind of place the Placement page edits; follows the player until a tab is picked.</summary>
    private ContentKind editKind;
    private bool pickedKind;

    // Diagnostics view state. The filtered list is rebuilt only when the log or the filter changes.
    private int logLevelFilter;
    private string logSearch = string.Empty;
    private bool logAutoScroll = true;
    private readonly HashSet<LogEntry> selectedLogLines = [];
    private List<LogEntry> visibleLogLines = [];
    private (int Revision, int Level, string Search) logViewKey = (-1, -1, string.Empty);
    private int scrolledRevision = -1;

    private static readonly (FontAwesomeIcon Icon, string Label, string Blurb)[] Sections =
    [
        (FontAwesomeIcon.Keyboard, "Controls", "Your map key, how it behaves and when the map hides"),
        (FontAwesomeIcon.PaintBrush, "Look", "How the map itself is drawn"),
        (FontAwesomeIcon.Crosshairs, "Placement", "Size, zoom, rotation and where it sits"),
        (FontAwesomeIcon.MapMarkerAlt, "Markers", "What shows up on the map, and in which colours"),
        (FontAwesomeIcon.Bug, "Diagnostics", "Current map, log and bug reports"),
        (FontAwesomeIcon.InfoCircle, "About", "Version, links and support"),
    ];

    public ConfigWindow(Plugin plugin)
        : base("MoogleMap Settings###MoogleMapConfig")
    {
        this.plugin = plugin;

        Size = new Vector2(820, 640);
        SizeConstraintMin = new Vector2(700, 500);
        SizeConstraintMax = new Vector2(1400, 1200);
        WindowFlags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
    }

    /// <summary>While true the map keys are left alone, so pressing one to bind it doesn't toggle the map.</summary>
    public bool CapturingKey => capturing is not null && IsOpen;

    private ConfigurationService ConfigService => plugin.ConfigService;
    private Configuration Config => plugin.Configuration;

    public static string Version => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";

    protected override void DrawContents()
    {
        try
        {
            BgAlpha = 0.94f;
            changed = false;

            Theme.WindowHeader(FontAwesomeIcon.Map, "MoogleMap", Sections[section].Blurb, DrawHeaderActions);

            var avail = ImGui.GetContentRegionAvail();
            const float railWidth = 158f;

            DrawNavRail(new Vector2(railWidth, avail.Y));

            ImGui.SameLine(0, 12);

            using (var pane = Theme.Region("##ConfigPane", new Vector2(0, avail.Y)))
            {
                if (pane)
                {
                    switch (section)
                    {
                        case 0: DrawControls(); break;
                        case 1: DrawLook(); break;
                        case 2: DrawPlacement(); break;
                        case 3: DrawMarkers(); break;
                        case 4: DrawDiagnostics(); break;
                        case 5: DrawAbout(); break;
                    }

                    ImGui.Dummy(new Vector2(0, 8));
                }
            }

            if (changed)
                saveAt = plugin.Now + 1.0;

            // Sliders fire every frame while dragged; save once things settle.
            if (plugin.Now >= saveAt)
            {
                saveAt = double.MaxValue;
                ConfigService.Save();
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Error drawing config window");
            ImGui.TextColored(Theme.Bad, "Error drawing settings.");
        }
    }

    protected override void OnClosed()
    {
        capturing = null;
        pickedKind = false;
        if (saveAt != double.MaxValue)
        {
            saveAt = double.MaxValue;
            ConfigService.Save();
        }
    }

    private void DrawHeaderActions()
    {
        var width = ImGui.GetContentRegionAvail().X;
        var preview = Config.PreviewWhileConfiguring;
        var label = preview ? "Preview on" : "Preview off";
        var buttonWidth = 130f;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + width - buttonWidth);
        if (preview ? Theme.PrimaryButton(label, new Vector2(buttonWidth, 0)) : Theme.GhostButton(label, new Vector2(buttonWidth, 0), Theme.Gold))
        {
            Config.PreviewWhileConfiguring = !preview;
            changed = true;
        }
        if (ImGui.IsItemHovered())
            Theme.Tooltip("Keeps the map on screen while this window is open, so you see every change as you make it.");
    }

    private void DrawNavRail(Vector2 size)
    {
        var dl = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();

        dl.AddRectFilled(origin, origin + size, Theme.U32(Theme.Panel, 0.55f), Theme.Radius);
        dl.AddRect(origin, origin + size, Theme.U32(Theme.Line, 0.8f), Theme.Radius, ImDrawFlags.None, 1f);

        using var child = Theme.Region("##ConfigNav", size);
        if (!child) return;

        ImGui.Dummy(new Vector2(0, 4));

        for (var i = 0; i < Sections.Length; i++)
        {
            var (icon, label, _) = Sections[i];
            var selected = section == i;

            var p = ImGui.GetCursorScreenPos();
            var w = ImGui.GetContentRegionAvail().X - 8;
            const float h = 34f;

            ImGui.SetCursorScreenPos(new Vector2(p.X + 4, p.Y));
            if (ImGui.InvisibleButton($"##nav{i}", new Vector2(w, h)))
            {
                section = i;
                capturing = null;
            }

            var hovered = ImGui.IsItemHovered();
            var min = new Vector2(p.X + 4, p.Y);
            var max = new Vector2(p.X + 4 + w, p.Y + h);

            if (selected)
            {
                dl.AddRectFilled(min, max, Theme.U32(Theme.Gold, 0.13f), Theme.Radius);
                dl.AddRectFilled(min, new Vector2(min.X + 2.5f, max.Y), Theme.U32(Theme.Gold, 0.95f), 1.5f);
            }
            else if (hovered)
            {
                dl.AddRectFilled(min, max, Theme.U32(Theme.Crystal, 0.12f), Theme.Radius);
            }

            var tint = selected ? Theme.GoldBright : hovered ? Theme.Text : Theme.TextMuted;

            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                var glyph = icon.ToIconString();
                var gs = ImGui.CalcTextSize(glyph);
                dl.AddText(new Vector2(min.X + 15 - gs.X * 0.5f, min.Y + (h - gs.Y) * 0.5f), Theme.U32(tint), glyph);
            }

            var ts = ImGui.CalcTextSize(label);
            dl.AddText(new Vector2(min.X + 30, min.Y + (h - ts.Y) * 0.5f), Theme.U32(tint), label);

            ImGui.Dummy(new Vector2(0, 2));
        }

        // Status pill at the bottom of the rail: is the map up right now?
        var (statusText, statusColor) = StatusSummary();
        var pillY = origin.Y + size.Y - 34f;
        var pillMin = new Vector2(origin.X + 8, pillY);
        var pillMax = new Vector2(origin.X + size.X - 8, pillY + 24f);
        dl.AddRectFilled(pillMin, pillMax, Theme.U32(statusColor, 0.12f), 12f);
        dl.AddCircleFilled(new Vector2(pillMin.X + 12, pillMin.Y + 12), 4f, Theme.U32(statusColor), 12);
        dl.AddText(new Vector2(pillMin.X + 22, pillMin.Y + (24f - ImGui.GetTextLineHeight()) * 0.5f), Theme.U32(statusColor), statusText);
    }

    private (string Text, Vector4 Color) StatusSummary()
    {
        if (!Config.Enabled) return ("Turned off", Theme.Warn);
        if (plugin.HiddenReason is not null) return ("Hidden", Theme.TextMuted);
        if (plugin.MappingProgress is { } progress) return ($"Mapping {progress * 100f:0}%", Theme.Crystal);
        if (plugin.Maps.Loading) return ("Loading map", Theme.Crystal);
        if (Config.Shown || Config.Trigger == TriggerMode.Hold) return ("Ready", Theme.Good);
        return ("Map closed", Theme.TextMuted);
    }

    // ------------------------------------------------------------------
    // Controls
    // ------------------------------------------------------------------

    private void DrawControls()
    {
        Theme.SectionHeader("MoogleMap", FontAwesomeIcon.PowerOff);
        ToggleRow("Enabled", Config.Enabled, v => Config.Enabled = v, "The master switch. Off, the map key does nothing and nothing is drawn.");

        ImGui.Dummy(new Vector2(0, 8));
        Theme.SectionHeader("Map key", FontAwesomeIcon.Keyboard);

        KeyRow("Show the map", Config.ToggleKey);

        Row("Behaviour");
        var mode = (int)Config.Trigger;
        if (Theme.SegmentedControl("##Trigger", ref mode, "Press to toggle", "Hold to show"))
        {
            Config.Trigger = (TriggerMode)mode;
            changed = true;
        }

        ToggleRow("Hide key from game", Config.ConsumeKey, v => Config.ConsumeKey = v,
            "The game won't also see the key, so it doesn't run whatever it's bound to there. Turn off if you want both.");

        ToggleRow("Server info bar", Config.ShowInServerBar, v => Config.ShowInServerBar = v,
            "An entry in the bar at the top right of the screen: click to show or hide the map, right-click for these settings, "
            + "scroll to zoom. It also shows how far along the map is while it's being built.");

        ImGui.Dummy(new Vector2(0, 4));
        Wrapped("Pick a key the game doesn't use, or one you don't mind sharing. Typing in chat never opens the map. "
                + "/mmap toggle does the same as the key, if you'd rather put it on a macro or a hotbar.");

        ImGui.Dummy(new Vector2(0, 8));
        Theme.SectionHeader("Zoom keys", FontAwesomeIcon.SearchPlus);
        KeyRow("Zoom in", Config.ZoomInKey);
        KeyRow("Zoom out", Config.ZoomOutKey);
        Wrapped("Only active while the map is up, so they're free for the game the rest of the time.");

        ImGui.Dummy(new Vector2(0, 8));
        Theme.SectionHeader("Hide the map", FontAwesomeIcon.EyeSlash);
        ToggleRow("With the game UI", Config.HideWithGameUi, v => Config.HideWithGameUi = v,
            "Hide in cutscenes, GPose and while the game UI is hidden.");
        ToggleRow("In combat", Config.HideInCombat, v => Config.HideInCombat = v,
            "Hide while you're in combat, and bring it back when the fight ends.");

        ImGui.Dummy(new Vector2(0, 8));
        Theme.SectionHeader("Game UI", FontAwesomeIcon.WindowRestore);
        ToggleRow("Stay under game windows", Config.StayUnderGameWindows, v => Config.StayUnderGameWindows = v,
            "Menus, dialogues and tooltips sit on top of the map instead of under it. If a square hole ever shows up in the map with "
            + "nothing there, see Diagnostics: it names the window responsible and can ignore it.");
        if (Config.StayUnderGameWindows)
            ToggleRow("Stay under the HUD", Config.StayUnderHud, v => Config.StayUnderHud = v,
                "Hotbars, HP bars, the party list, job gauges, the chat and notices like the loot one sit on top of the map too. "
                + "Off, only menus and dialogues do, and the map draws over the HUD.");
        ToggleRow("Hide game minimap", Config.HideGameMinimap, v => Config.HideGameMinimap = v,
            "Hides the game's own minimap, ring and buttons included, for a cleaner screen with MoogleMap a key away. "
            + "Put back as soon as you turn this off.");

        ImGui.Dummy(new Vector2(0, 10));
        Theme.Callout(FontAwesomeIcon.ShieldAlt, "Purely visual",
            "MoogleMap only shows what the game already lets you see: hidden objects stay hidden, and it switches itself off in PvP. "
            + "It never moves your character or sends anything to the game.",
            Theme.Good);
    }

    private void KeyRow(string label, Keybind bind)
    {
        Row(label);
        var waiting = ReferenceEquals(capturing, bind);

        if (waiting)
        {
            Theme.PrimaryButton("Press a key... (Esc cancels)", new Vector2(ControlWidth, 0));
            PollCapture(bind);
        }
        else if (Theme.GhostButton($"{bind}##{label}", new Vector2(ControlWidth, 0), bind.IsBound ? Theme.GoldBright : Theme.TextMuted))
        {
            capturing = bind;
        }

        if (bind.IsBound && !waiting)
        {
            ImGui.SameLine(0, 6);
            if (Theme.IconButton($"##clear{label}", FontAwesomeIcon.Times, "Unbind", Theme.TextMuted, size: 26f))
            {
                bind.Key = VirtualKey.NO_KEY;
                bind.Ctrl = bind.Shift = bind.Alt = false;
                changed = true;
            }
        }
    }

    private void PollCapture(Keybind bind)
    {
        if (plugin.Keys.IsDown(VirtualKey.ESCAPE))
        {
            capturing = null;
            return;
        }

        foreach (var key in Plugin.KeyState.GetValidVirtualKeys())
        {
            if (!Keybind.IsCapturable(key) || !plugin.Keys.IsDown(key)) continue;

            bind.Key = key;
            bind.Ctrl = plugin.Keys.IsDown(VirtualKey.CONTROL);
            bind.Shift = plugin.Keys.IsDown(VirtualKey.SHIFT);
            bind.Alt = plugin.Keys.IsDown(VirtualKey.MENU);
            capturing = null;
            changed = true;

            // The key is still down; don't let it toggle the map on the way out.
            plugin.Keys.Suppress(bind);
            return;
        }
    }

    // ------------------------------------------------------------------
    // Look
    // ------------------------------------------------------------------

    private void DrawLook()
    {
        Theme.SectionHeader("Colours", FontAwesomeIcon.Palette);
        ColorRow("Floor", Config.FloorColor, v => Config.FloorColor = v);
        ColorRow("Walls", Config.EdgeColor, v => Config.EdgeColor = v);
        FloatRow("Wall thickness", Config.WallThickness, v => Config.WallThickness = v, 0.8f, 5f, "%.1f px");
        ToggleRow("Wall glow", Config.WallGlow, v => Config.WallGlow = v,
            "A soft, wider light under every wall, so walls still read over bright scenery.");

        ImGui.Dummy(new Vector2(0, 8));
        Theme.SectionHeader("Opacity", FontAwesomeIcon.Adjust);
        FloatRow("Markers", Config.MarkerOpacity, v => Config.MarkerOpacity = v, 0.1f, 1f, "%.2f");
        Wrapped("The map's own opacity is set per kind of place, under Placement.");

        ImGui.Dummy(new Vector2(0, 8));
        Theme.SectionHeader("Effects", FontAwesomeIcon.Magic);
        ToggleRow("Effects", Config.Effects, v => Config.Effects = v, "Turns every effect below on or off at once.");
        if (Config.Effects)
        {
            ToggleRow("Wall sweep", Config.EffectSweep, v => Config.EffectSweep = v,
                "Every few seconds a band of light runs out from you and lights up the walls as it crosses them, like a radar.");
            ToggleRow("Ripple", Config.EffectRipple, v => Config.EffectRipple = v, "A ring rippling out from your arrow now and then.");
            Wrapped("Both are off with reduced motion.");
        }

        ImGui.Dummy(new Vector2(0, 8));
        Theme.SectionHeader("Walkable floor", FontAwesomeIcon.Route);
        ToggleRow("Floor from the world", Config.SurveyMaps, v => Config.SurveyMaps = v,
            "MoogleMap finds where you can walk from the game's own collision, starting from wherever you, NPCs and other players "
            + "are standing. Towns and fields get their whole map this way, since their map pictures can't tell a street from a "
            + "rooftop; duties use it to complete the traced picture. It fills in over a few seconds, in fields as you travel, "
            + "and is remembered for next time. Off, only dungeon, trial and raid maps are drawn.");
        if (Config.SurveyMaps)
        {
            FloatRow("Reach in fields", Config.FieldExploreRadius, v => Config.FieldExploreRadius = v, 40f, 200f, "%.0f yalms");
            HelpAfter("Open fields are mapped around you as you travel, on foot or flying. Further reaches more at once, at the cost of a little more work each frame.");
            Row(string.Empty);
            if (Theme.GhostButton("Forget this area", new Vector2(150, 26)))
                plugin.ResetExplored();
            if (ImGui.IsItemHovered())
                Theme.Tooltip("Starts the current area over, if something walkable is missing or something that isn't shows up. Also /mmap explore reset.");
        }

        ToggleRow("Explore unmapped places", Config.ExploreUnmapped, v => Config.ExploreUnmapped = v,
            "Deep dungeon floors and a few other places have no map picture at all. There the floor appears around you as you walk, "
            + "room by room, and is forgotten when you leave the floor.");
        if (Config.ExploreUnmapped)
            FloatRow("Reach", Config.ExploreRadius, v => Config.ExploreRadius = v, 12f, 60f, "%.0f yalms");
    }

    // ------------------------------------------------------------------
    // Placement
    // ------------------------------------------------------------------

    private void DrawPlacement()
    {
        Theme.SectionHeader("View for each kind of place", FontAwesomeIcon.ExpandArrowsAlt);
        Wrapped("Duties, towns and open fields each keep their own zoom, size and opacity, and the map switches between them "
                + "as you travel. Zoom keys and the server bar change the one you're in.");
        ImGui.Dummy(new Vector2(0, 4));

        // Follow where the player is until they pick a tab themselves.
        if (!pickedKind)
            editKind = plugin.CurrentKind;

        Row("Editing");
        var kind = (int)editKind;
        string Tab(ContentKind k, string name) => plugin.CurrentKind == k ? $"{name} (here)" : name;
        if (Theme.SegmentedControl("##ViewKind", ref kind, Tab(ContentKind.Duty, "Duties"), Tab(ContentKind.Town, "Towns"), Tab(ContentKind.Field, "Fields")))
        {
            editKind = (ContentKind)kind;
            pickedKind = true;
        }

        var view = Config.ViewFor(editKind);
        FloatRow("Zoom", view.Zoom, v => view.Zoom = v, Plugin.MinZoom, Plugin.MaxZoom, "%.1f px/yalm");
        ToggleRow("Fit small places", Config.FitSmallAreas, v => Config.FitSmallAreas = v,
            "Zooms in further on small places, like inn rooms, trial arenas and short dungeon floors, so they fill the map instead of a "
            + "corner of it. Never zooms out past the setting above. Places mapped by exploring are fitted once the mapping is done.");
        FloatRow("Radius", view.Radius, v => view.Radius = v, 0.15f, 0.6f, "%.2f");
        HelpAfter("How big the map is, as a share of your screen height.");
        FloatRow("Width", view.Width, v => view.Width = v, 0.3f, 1f, "%.2f");
        HelpAfter("How much of the circle shows side to side. Lower trims the left and right off, without shrinking anything. "
                  + "With a bigger radius, a lower height gives a wide strip that sees further left and right.");
        FloatRow("Height", view.Height, v => view.Height = v, 0.3f, 1f, "%.2f");
        HelpAfter("How much of the circle shows top to bottom. Lower trims the top and bottom off, without shrinking anything.");
        FloatRow("Map opacity", view.MapOpacity, v => view.MapOpacity = v, 0.1f, 1f, "%.2f");
        Row(string.Empty);
        if (Theme.GhostButton("Reset this view", new Vector2(150, 26)))
        {
            Config.Views[editKind] = ViewPreset.DefaultsFor(editKind);
            changed = true;
        }

        ImGui.Dummy(new Vector2(0, 8));
        Theme.SectionHeader("Edge", FontAwesomeIcon.Circle);
        FloatRow("Soft rim", Config.EdgeFade, v => Config.EdgeFade = v, 0.05f, 0.8f, "%.2f");
        HelpAfter("How much of the map's edge fades away instead of ending sharply.");

        ImGui.Dummy(new Vector2(0, 8));
        Theme.SectionHeader("Orientation", FontAwesomeIcon.Compass);
        ToggleRow("Sonar on open", Config.Sonar, v => Config.Sonar = v,
            "A wave sweeps out from you each time the map opens. Not shown with reduced motion.");
        ToggleRow("Turn with the camera", Config.RotateWithCamera, v => Config.RotateWithCamera = v,
            "The top of the map is always where your camera looks, so the map lines up with the world behind it. Off keeps north up.");

        ImGui.Dummy(new Vector2(0, 8));
        Theme.SectionHeader("Position", FontAwesomeIcon.Crosshairs);
        Row("Centre on");
        var anchor = (int)Config.Anchor;
        if (Theme.SegmentedControl("##Anchor", ref anchor, "Screen", "My character"))
        {
            Config.Anchor = (MapAnchor)anchor;
            changed = true;
        }
        HelpAfter("My character keeps you at the middle of the map wherever the camera puts you on screen.");

        var offset = Config.Offset;
        FloatRow("Horizontal offset", offset.X, v => Config.Offset = Config.Offset with { X = v }, -0.4f, 0.4f, "%.2f");
        FloatRow("Vertical offset", offset.Y, v => Config.Offset = Config.Offset with { Y = v }, -0.4f, 0.4f, "%.2f");
        ImGui.Dummy(new Vector2(0, 2));
        if (Theme.GhostButton("Re-centre", new Vector2(120, 28)))
        {
            Config.Offset = Vector2.Zero;
            changed = true;
        }

        ImGui.Dummy(new Vector2(0, 8));
        Theme.SectionHeader("Motion", FontAwesomeIcon.Running);
        Row("Reduced motion");
        var motion = (int)Config.ReducedMotion;
        if (Theme.SegmentedControl("##Motion", ref motion, "Follow Dalamud", "On", "Off"))
        {
            Config.ReducedMotion = (ReducedMotionMode)motion;
            changed = true;
        }
        HelpAfter("When on, the map appears and disappears at once instead of fading, and enemies in combat stop pulsing.");
    }

    // ------------------------------------------------------------------
    // Markers
    // ------------------------------------------------------------------

    private void DrawMarkers()
    {
        Theme.SectionHeader("From the map", FontAwesomeIcon.Map);
        ToggleRow("Map icons", Config.ShowMapIcons, v => Config.ShowMapIcons = v, "Aetherytes, shops, exits and every other icon the game's map shows.");
        ToggleRow("Place names", Config.ShowPlaceNames, v => Config.ShowPlaceNames = v);
        ToggleRow("Quest NPCs", Config.ShowQuestNpcs, v => Config.ShowQuestNpcs = v,
            "NPCs and objects with a quest icon over their heads - a quest to pick up, someone to talk to, something to hand in - "
            + "shown on the map with that same icon, even with NPCs turned off.");
        ToggleRow("Quest markers", Config.ShowQuestMarkers, v => Config.ShowQuestMarkers = v,
            "Quest objectives and other markers the game puts on your map.");
        if (Config.ShowQuestMarkers)
        {
            ToggleRow("Quest areas", Config.QuestAreas, v => Config.QuestAreas = v,
                "Objectives that are a place rather than a point, drawn as the light blue circle the game's map uses.");
            ToggleRow("Quest names", Config.QuestNames, v => Config.QuestNames = v, "The quest's name beside its marker.");
        }
        ToggleRow("Flag", Config.ShowFlag, v => Config.ShowFlag = v);
        ToggleRow("FATEs", Config.ShowFates, v => Config.ShowFates = v);
        if (Config.ShowFates)
            ToggleRow("FATE progress", Config.FateDetails, v => Config.FateDetails = v,
                "A ring around each FATE's icon filling up as it's completed, with the time left underneath.");

        ImGui.Dummy(new Vector2(0, 8));
        Theme.SectionHeader("Around you", FontAwesomeIcon.Users);
        ToggleRow("Party and alliance", Config.ShowParty, v => Config.ShowParty = v, "Also trust and duty support allies.");
        if (Config.ShowParty)
        {
            ToggleRow("Party by role", Config.PartyByRole, v => Config.PartyByRole = v,
                "Tanks, healers and damage in their role colours, like the game's party list. Off, everyone uses the party colour.");
            ToggleRow("Party jobs", Config.PartyJobs, v => Config.PartyJobs = v, "The job beside each party member: WAR, WHM, NIN...");
            ToggleRow("Party health", Config.PartyHealth, v => Config.PartyHealth = v,
                "A ring around each party member that empties with their health, red and pulsing when they're low. The knocked out show a skull, "
                + "green once a raise is waiting for them, and go back to normal as soon as they're up.");
        }
        ToggleRow("My health", Config.SelfHealth, v => Config.SelfHealth = v,
            "The same health ring around your own arrow: green, yellow under half, red and pulsing when you're low.");
        ToggleRow("Other players", Config.ShowPlayers, v => Config.ShowPlayers = v);
        ToggleRow("Enemies", Config.ShowEnemies, v => Config.ShowEnemies = v, "Enemies already in a fight are drawn in the combat colour and pulse.");
        if (Config.ShowEnemies)
            ToggleRow("Boss spotlight", Config.BossSpotlight, v => Config.BossSpotlight = v,
                "In duties, the boss gets a crown, a bigger marker, and its name and health always on show.");
        ToggleRow("NPCs", Config.ShowNpcs, v => Config.ShowNpcs = v);
        ToggleRow("Objects", Config.ShowObjects, v => Config.ShowObjects = v, "Treasure coffers, aetherytes, doors, exits and other things you can click.");
        ToggleRow("Gathering points", Config.ShowGathering, v => Config.ShowGathering = v);
        ToggleRow("Camera cone", Config.ShowViewCone, v => Config.ShowViewCone = v);
        ToggleRow("Compass", Config.ShowCompass, v => Config.ShowCompass = v, "N, E, S and W inside the map, turning with it. North in gold.");
        if (Config.ShowCompass)
        {
            FloatRow("Compass distance", Config.CompassInset, v => Config.CompassInset = v, 0.4f, 0.95f, "%.2f");
            FloatRow("Compass size", Config.CompassScale, v => Config.CompassScale = v, 0.5f, 2f, "%.2fx");
        }
        ToggleRow("Target ring", Config.ShowTargetRing, v => Config.ShowTargetRing = v, "A turning ring around whatever you have targeted.");

        ImGui.Dummy(new Vector2(0, 8));
        Theme.SectionHeader("Party markings", FontAwesomeIcon.Flag);
        ToggleRow("Waymarks", Config.ShowWaymarks, v => Config.ShowWaymarks = v, "A, B, C, D and 1 to 4, where they've been placed on the ground.");
        ToggleRow("Signs", Config.ShowSigns, v => Config.ShowSigns = v, "Attack, bind, ignore and the shapes, over whoever they're on.");

        ImGui.Dummy(new Vector2(0, 8));
        Theme.SectionHeader("Trail", FontAwesomeIcon.ShoePrints);
        ToggleRow("Show my trail", Config.ShowTrail, v =>
        {
            Config.ShowTrail = v;
            if (!v) plugin.Trail.Clear();
        }, "A dashed line where you've just walked, fading over a few seconds. Handy for not going round in circles.");
        if (Config.ShowTrail)
        {
            FloatRow("Fades after", Math.Clamp(Config.TrailSeconds, 3f, 30f), v => Config.TrailSeconds = v, 3f, 30f, "%.0f s");
            ColorRow("Trail colour", Config.TrailColor, v => Config.TrailColor = v);
        }

        ImGui.Dummy(new Vector2(0, 8));
        Theme.SectionHeader("Keep on the rim", FontAwesomeIcon.Compass);
        Wrapped("When these are off the map, they stay on its edge pointing the way, with how far they are.");
        ToggleRow("Flag on rim", Config.PinFlag, v => Config.PinFlag = v);
        ToggleRow("Party on rim", Config.PinParty, v => Config.PinParty = v);
        ToggleRow("Quests on rim", Config.PinQuests, v => Config.PinQuests = v,
            "Off by default: in open fields there are many quests far away, and they'd crowd the edge.");

        ImGui.Dummy(new Vector2(0, 8));
        Theme.SectionHeader("Names", FontAwesomeIcon.Font);
        ToggleRow("Enemy names", Config.EnemyNames, v => Config.EnemyNames = v);
        ToggleRow("NPC names", Config.NpcNames, v => Config.NpcNames = v, "Towns have a lot of NPCs; this gets busy.");
        ToggleRow("Object names", Config.ObjectNames, v => Config.ObjectNames = v);

        ImGui.Dummy(new Vector2(0, 8));
        Theme.SectionHeader("Size", FontAwesomeIcon.TextHeight);
        FloatRow("Icons", Config.IconScale, v => Config.IconScale = v, 0.5f, 2f, "%.2fx");
        FloatRow("Labels", Config.LabelScale, v => Config.LabelScale = v, 0.6f, 1.8f, "%.2fx");

        ImGui.Dummy(new Vector2(0, 8));
        Theme.SectionHeader("Colours", FontAwesomeIcon.Palette);
        ColorRow("Enemies", Config.EnemyColor, v => Config.EnemyColor = v);
        ColorRow("Enemies in combat", Config.AggroColor, v => Config.AggroColor = v);
        ColorRow("Enemies on you", Config.TargetingYouColor, v => Config.TargetingYouColor = v);
        HelpAfter("Enemies whose target is you, with a light outline so they stand out from the rest of the fight.");
        ColorRow("Target ring", Config.TargetRingColor, v => Config.TargetRingColor = v);
        ColorRow("Party", Config.PartyColor, v => Config.PartyColor = v);
        if (Config.PartyByRole)
        {
            ColorRow("Tanks", Config.TankColor, v => Config.TankColor = v);
            ColorRow("Healers", Config.HealerColor, v => Config.HealerColor = v);
            ColorRow("Damage", Config.DpsColor, v => Config.DpsColor = v);
        }
        ColorRow("Other players", Config.PlayerColor, v => Config.PlayerColor = v);
        ColorRow("NPCs", Config.NpcColor, v => Config.NpcColor = v);
        ColorRow("Objects", Config.ObjectColor, v => Config.ObjectColor = v);

        ImGui.Dummy(new Vector2(0, 8));
        Theme.SectionHeader("Text colours", FontAwesomeIcon.Font);
        ColorRow("Place names", Config.PlaceNameColor, v => Config.PlaceNameColor = v);
        ColorRow("Icon labels", Config.IconLabelColor, v => Config.IconLabelColor = v);
        ToggleRow("Names in marker colour", Config.NamesMatchMarkers, v => Config.NamesMatchMarkers = v,
            "Enemy names in the enemy colour, NPC names in the NPC colour and so on. Off, every name uses the colour below.");
        if (!Config.NamesMatchMarkers)
            ColorRow("Names", Config.NameColor, v => Config.NameColor = v);
        ImGui.Dummy(new Vector2(0, 2));
        if (Theme.GhostButton("Reset text colours", new Vector2(160, 28)))
        {
            var defaults = new Configuration();
            Config.PlaceNameColor = defaults.PlaceNameColor;
            Config.IconLabelColor = defaults.IconLabelColor;
            Config.NamesMatchMarkers = defaults.NamesMatchMarkers;
            Config.NameColor = defaults.NameColor;
            changed = true;
        }
    }

    // ------------------------------------------------------------------
    // Diagnostics
    // ------------------------------------------------------------------

    private void DrawDiagnostics()
    {
        Theme.SectionHeader("Status", FontAwesomeIcon.Heartbeat);
        DrawStatus();

        ImGui.Dummy(new Vector2(0, 6));
        var covering = MoogleMap.Rendering.GameWindows.CoveringNames;
        if (covering.Count > 0)
        {
            if (Theme.GhostButton("Ignore these windows", new Vector2(180, 28), Theme.Warn))
            {
                foreach (var name in covering)
                    Config.IgnoredWindows.Add(name);
                ConfigService.Save();
            }
            if (ImGui.IsItemHovered())
                Theme.Tooltip("If the map has a square hole with nothing on screen there, the window named above is the cause. "
                              + "This makes the map stop making room for it.");
            ImGui.SameLine(0, 8);
        }

        if (Config.IgnoredWindows.Count > 0 && Theme.GhostButton("Clear ignored windows", new Vector2(180, 28)))
        {
            Config.IgnoredWindows.Clear();
            ConfigService.Save();
        }

        ImGui.Dummy(new Vector2(0, 8));
        var logEvents = Config.LogEvents;
        if (Theme.ToggleRow("Log map changes", ref logEvents,
                "Writes each map change (id, folder, name) and how it was processed to the log below. "
                + "Useful when a map looks wrong. Leave it off otherwise."))
        {
            Config.LogEvents = logEvents;
            ConfigService.Save();
        }

        ImGui.Dummy(new Vector2(0, 8));
        Theme.SectionHeader("Feedback", FontAwesomeIcon.CommentDots);

        Theme.Callout(FontAwesomeIcon.Bug, "Something not working?",
            "1. Turn on Log map changes, go back to where the problem is, then come back to this page.\n" +
            "2. Press Copy report. It copies your MoogleMap version, a few settings, the current map and the log below. " +
            "Click individual lines first if you only want to share those.\n" +
            "3. Press Report a bug, tell me what you did, what you expected and what happened instead, " +
            "then paste the report into the issue. A screenshot of the map helps a lot.",
            Theme.Warn);

        ImGui.Dummy(new Vector2(0, 8));
        if (Theme.PrimaryButton("Report a bug", new Vector2(140, 32)))
            OpenUrl(NewIssueUrl("bug", "[Bug] ",
                "**What happened?**\n\n\n**What did you expect?**\n\n\n**Where?** (zone, duty, floor)\n\n" +
                "**Diagnostics report**\n<!-- Settings → Diagnostics → Copy report, then paste here -->\n"));

        ImGui.SameLine(0, 8);
        if (Theme.GhostButton("Suggest a feature", new Vector2(150, 32), Theme.Crystal))
            OpenUrl(NewIssueUrl("enhancement", "[Feature] ",
                "**What would you like MoogleMap to do?**\n\n\n**How would it help you?**\n\n"));

        ImGui.SameLine(0, 8);
        if (Theme.GhostButton("Discord", new Vector2(100, 32), Theme.DiscordBlurple))
            OpenUrl(Plugin.DiscordUrl);

        ImGui.Dummy(new Vector2(0, 12));
        Theme.SectionHeader("Log", FontAwesomeIcon.Terminal);

        RefreshLogView();

        if (Theme.SegmentedControl("##LogLevel", ref logLevelFilter, "All", "Info", "Warnings", "Errors"))
            RefreshLogView();

        ImGui.SameLine(0, 10);
        ImGui.SetNextItemWidth(Math.Max(ImGui.GetContentRegionAvail().X, 120f));
        if (ImGui.InputTextWithHint("##LogSearch", "Filter log lines", ref logSearch, 100))
            RefreshLogView();

        ImGui.Dummy(new Vector2(0, 4));
        var copyLabel = selectedLogLines.Count > 0 ? $"Copy report ({selectedLogLines.Count})" : "Copy report";
        if (Theme.PrimaryButton(copyLabel, new Vector2(150, 30)))
            ImGui.SetClipboardText(BuildReport());
        if (ImGui.IsItemHovered())
            Theme.Tooltip(selectedLogLines.Count > 0
                ? "Copies your setup and the selected lines, ready to paste into an issue."
                : $"Copies your setup and the last {ReportLineLimit} lines shown, ready to paste into an issue.");

        ImGui.SameLine(0, 8);
        if (Theme.GhostButton("Copy lines only", new Vector2(130, 30)))
            ImGui.SetClipboardText(string.Join("\n", ReportLines().Select(e => e.Format())));

        if (selectedLogLines.Count > 0)
        {
            ImGui.SameLine(0, 8);
            if (Theme.GhostButton("Deselect", new Vector2(90, 30)))
                selectedLogLines.Clear();
        }

        ImGui.SameLine(0, 8);
        if (Theme.DangerButton("Clear", new Vector2(70, 30)))
        {
            Plugin.Log.Clear();
            selectedLogLines.Clear();
            RefreshLogView();
        }

        ImGui.SameLine(0, 12);
        ImGui.AlignTextToFramePadding();
        ImGui.Checkbox("Auto-scroll", ref logAutoScroll);

        ImGui.Dummy(new Vector2(0, 4));
        DrawLogLines();
    }

    private void DrawStatus()
    {
        var hidden = plugin.HiddenReason;
        var map = plugin.Maps.Current;
        var width = (ImGui.GetContentRegionAvail().X - 16f) / 3f;

        Theme.StatCard(FontAwesomeIcon.Map, "Map", map is null ? "-" : map.Key.Length > 0 ? map.Key : $"#{map.RowId}", Theme.Gold, width,
            map?.DisplayName);
        ImGui.SameLine(0, 8);
        Theme.StatCard(FontAwesomeIcon.Image, "Floor from", map is null ? "-" : plugin.Maps.FromPicture ? "Picture + world" : "World",
            Theme.Crystal, width, plugin.Maps.Status);
        ImGui.SameLine(0, 8);
        Theme.StatCard(hidden is null ? FontAwesomeIcon.CheckCircle : FontAwesomeIcon.EyeSlash, "Drawing",
            !Config.Enabled ? "Off" : hidden is null ? "Yes" : "Hidden", hidden is null && Config.Enabled ? Theme.Good : Theme.Warn, width,
            hidden is null ? null : $"Hidden: {hidden}.");

        ImGui.Dummy(new Vector2(0, 6));
        using var table = ImRaii.Table("##State", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp);
        if (!table) return;

        ImGui.TableSetupColumn("What", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch, 2.4f);

        StateRow("Territory / map", $"{Plugin.ClientState.TerritoryType} / {Plugin.ClientState.MapId}");
        StateRow("Map name", map?.DisplayName ?? "-");
        StateRow("Texture", plugin.Maps.Status);
        if (map is { HasTexture: true })
            StateRow("Paper / floor", $"{plugin.Maps.PaperShare:P0} flat paper, {plugin.Maps.FloorShare:P1} traced floor");
        StateRow("Map markers", plugin.Maps.Markers.All.Count.ToString());
        StateRow("Live markers", string.Join(", ", plugin.Live.All.GroupBy(m => m.Kind).Select(g => $"{g.Key} {g.Count()}")) is { Length: > 0 } s ? s : "none");
        StateRow("Explorer", plugin.ExplorerMode == ExploreMode.None
            ? "not used here"
            : $"{plugin.ExplorerMode}: {plugin.Explorer.FloorCells} cells, {plugin.Explorer.FrontierSize} to visit, {plugin.Explorer.Raycasts} raycasts");
        if (plugin.ExplorerMode == ExploreMode.Survey)
            StateRow("On the map", $"{plugin.Maps.FusedCells} explored cells drawn");
        StateRow("Game windows on top", MoogleMap.Rendering.GameWindows.GaveUp
            ? $"{MoogleMap.Rendering.GameWindows.Covering} (too many to cut around: map drawn over them)"
            : MoogleMap.Rendering.GameWindows.Covering);
        if (Config.IgnoredWindows.Count > 0)
            StateRow("Ignored windows", string.Join(", ", Config.IgnoredWindows));
        StateRow("Maps in memory", plugin.Maps.CachedMaps.ToString());
        StateRow("Frame errors", plugin.FrameErrors.ToString());
    }

    private static void StateRow(string label, string value)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextColored(Theme.TextMuted, label);
        ImGui.TableNextColumn();
        ImGui.TextColored(Theme.Text, value);
    }

    private void DrawLogLines()
    {
        var height = Math.Max(ImGui.GetContentRegionAvail().Y - 10f, 220f);
        using var card = Theme.Card("##LogCard", new Vector2(0, height));
        if (!card) return;

        using var rows = Theme.Region("##LogRows", Vector2.Zero, ImGuiWindowFlags.HorizontalScrollbar);
        if (!rows) return;

        if (visibleLogLines.Count == 0)
        {
            ImGui.TextColored(Theme.TextFaint, logViewKey.Level == 0 && logSearch.Length == 0
                ? "Nothing logged yet."
                : "No lines match the current filter.");
            return;
        }

        for (var i = 0; i < visibleLogLines.Count; i++)
        {
            var entry = visibleLogLines[i];
            var selected = selectedLogLines.Contains(entry);

            using (ImRaii.PushColor(ImGuiCol.Text, LevelColor(entry.Level)))
            {
                if (ImGui.Selectable($"{entry.Format()}##log{i}", selected))
                {
                    if (!selectedLogLines.Remove(entry))
                        selectedLogLines.Add(entry);
                }
            }

            using var popup = ImRaii.ContextPopupItem($"##logctx{i}");
            if (popup.Success && ImGui.Selectable("Copy this line"))
                ImGui.SetClipboardText(entry.Format());
        }

        if (logAutoScroll && scrolledRevision != Plugin.Log.Revision)
        {
            ImGui.SetScrollHereY(1f);
            scrolledRevision = Plugin.Log.Revision;
        }
    }

    private void RefreshLogView()
    {
        var key = (Plugin.Log.Revision, logLevelFilter, logSearch);
        if (key == logViewKey) return;
        logViewKey = key;

        var minLevel = (LogLevel)logLevelFilter;
        visibleLogLines = Plugin.Log.Snapshot()
            .Where(e => e.Level >= minLevel)
            .Where(e => logSearch.Length == 0
                        || e.Message.Contains(logSearch, StringComparison.OrdinalIgnoreCase)
                        || (e.Exception?.Contains(logSearch, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToList();

        // Lines that scrolled out of the buffer can't be copied any more.
        var all = Plugin.Log.Snapshot().ToHashSet();
        selectedLogLines.RemoveWhere(e => !all.Contains(e));
    }

    private IEnumerable<LogEntry> ReportLines()
    {
        return selectedLogLines.Count > 0
            ? selectedLogLines.OrderBy(e => e.Time)
            : visibleLogLines.Skip(Math.Max(0, visibleLogLines.Count - ReportLineLimit));
    }

    private string BuildReport()
    {
        var sb = new StringBuilder();
        var map = plugin.Maps.Current;

        sb.AppendLine("### MoogleMap diagnostics");
        sb.AppendLine($"- MoogleMap: {Version}");
        sb.AppendLine($"- Dalamud: {typeof(Dalamud.Plugin.IDalamudPluginInterface).Assembly.GetName().Version}");
        sb.AppendLine($"- Game language: {Plugin.ClientState.ClientLanguage}, territory {Plugin.ClientState.TerritoryType}, map {Plugin.ClientState.MapId}");
        sb.AppendLine($"- Map: {map?.Key ?? "-"} \"{map?.DisplayName}\", scale {map?.Scale:0.00}, offset {map?.Offset}, texture {OnOff(map?.HasTexture == true)}");
        sb.AppendLine($"- Processing: {plugin.Maps.Status}, picture {OnOff(plugin.Maps.FromPicture)}, paper {plugin.Maps.PaperShare:P0}, floor {plugin.Maps.FloorShare:P1}");
        sb.AppendLine($"- Settings: trigger {Config.Trigger} on {Config.ToggleKey}, place {plugin.CurrentKind}, zoom {plugin.View.Zoom:0.0} (shown {plugin.Zoom:0.0}, area {plugin.Maps.AreaSize?.ToString("0") ?? "-"} y), radius {plugin.View.Radius:0.00}, "
                      + $"rotate {OnOff(Config.RotateWithCamera)}, anchor {Config.Anchor}, explore {OnOff(Config.ExploreUnmapped)}");
        sb.AppendLine($"- State: hidden {plugin.HiddenReason ?? "no"}, shown {OnOff(Config.Shown)}, frame errors {plugin.FrameErrors}, "
                      + $"explorer {plugin.ExplorerMode} {plugin.Explorer.FloorCells} cells / {plugin.Explorer.Raycasts} raycasts, fused {plugin.Maps.FusedCells}");
        sb.AppendLine($"- Generated: {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine();
        sb.AppendLine("```text");
        foreach (var entry in ReportLines())
            sb.AppendLine(entry.Format());
        sb.AppendLine("```");

        return sb.ToString();

        static string OnOff(bool value) => value ? "on" : "off";
    }

    private static Vector4 LevelColor(LogLevel level) => level switch
    {
        LogLevel.Error => Theme.Bad,
        LogLevel.Warning => Theme.Warn,
        LogLevel.Info => Theme.Text,
        _ => Theme.TextMuted,
    };

    private static string NewIssueUrl(string label, string title, string body)
        => $"{Plugin.RepoUrl}/issues/new?labels={label}&title={Uri.EscapeDataString(title)}&body={Uri.EscapeDataString(body)}";

    // ------------------------------------------------------------------
    // About
    // ------------------------------------------------------------------

    private void DrawAbout()
    {
        Theme.SectionHeader("MoogleMap", FontAwesomeIcon.Map);

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.TextMuted, $"Version {Version}  ·  by GitHixy");

        ImGui.Dummy(new Vector2(0, 6));
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + Math.Min(ImGui.GetContentRegionAvail().X, 440f));
        ImGui.TextColored(Theme.Text,
            "A see-through map over your screen, one key away: the game's own maps traced into clean outlines, " +
            "the floor mapped as you explore where there's no map, and everyone around you, live.");
        ImGui.PopTextWrapPos();

        ImGui.Dummy(new Vector2(0, 14));
        Theme.SectionHeader("Support", FontAwesomeIcon.Heart);

        using (ImRaii.PushColor(ImGuiCol.Button, Theme.Alpha(Theme.PatreonCoral, 0.85f))
                   .Push(ImGuiCol.ButtonHovered, Theme.Lighten(Theme.PatreonCoral, 0.12f))
                   .Push(ImGuiCol.ButtonActive, new Vector4(0.85f, 0.20f, 0.24f, 1.0f))
                   .Push(ImGuiCol.Text, Theme.Text))
        {
            if (ImGui.Button("Support me on Patreon", new Vector2(230, 34)))
                OpenUrl(Plugin.PatreonUrl);
        }

        ImGui.SameLine(0, 10);
        if (Theme.GhostButton("Join the Discord", new Vector2(150, 34), Theme.DiscordBlurple))
            OpenUrl(Plugin.DiscordUrl);

        ImGui.SameLine(0, 10);
        if (Theme.GhostButton("GitHub", new Vector2(110, 34)))
            OpenUrl(Plugin.RepoUrl);

        ImGui.Dummy(new Vector2(0, 2));
        if (Theme.GhostButton("Report a problem", new Vector2(150, 30)))
            section = Array.FindIndex(Sections, s => s.Label == "Diagnostics");

        ImGui.Dummy(new Vector2(0, 14));
        Theme.SectionHeader("Shortcuts", FontAwesomeIcon.Keyboard);

        ShortcutRow("/mooglemap", "Open these settings (also /mmap)");
        ShortcutRow("/mmap toggle", "Show or hide the map (also show, hide)");
        ShortcutRow("/mmap zoom in", "Zoom in, or out with zoom out");
        ShortcutRow("/mmap explore reset", "Forget the floor mapped so far");

        ImGui.Dummy(new Vector2(0, 14));
        Theme.SectionHeader("Also by GitHixy", FontAwesomeIcon.Cubes);
        ShortcutRow("LootView", "Live loot tracker with history and statistics");
        ShortcutRow("HitSpark", "Animated, styleable combat text");
        ShortcutRow("FateLimit", "FATE borders in the world, with edge warnings");
    }

    private static void ShortcutRow(string command, string description)
    {
        var dl = ImGui.GetWindowDrawList();
        var p = ImGui.GetCursorScreenPos();
        var ts = ImGui.CalcTextSize(command);

        dl.AddRectFilled(p, new Vector2(p.X + ts.X + 14, p.Y + ts.Y + 5), Theme.U32(Theme.Panel, 0.95f), 4f);
        dl.AddRect(p, new Vector2(p.X + ts.X + 14, p.Y + ts.Y + 5), Theme.U32(Theme.Line), 4f, ImDrawFlags.None, 1f);
        dl.AddText(new Vector2(p.X + 7, p.Y + 2.5f), Theme.U32(Theme.GoldBright), command);

        ImGui.Dummy(new Vector2(Math.Max(ts.X + 14, 150f), ts.Y + 5));
        ImGui.SameLine(0, 12);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.TextMuted, description);
        ImGui.Dummy(new Vector2(0, 2));
    }

    // ------------------------------------------------------------------
    // Setting rows
    // ------------------------------------------------------------------

    private static void Row(string label)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.TextMuted, label);

        // A label longer than the column pushes its control along instead of running under it.
        var labelEnd = ImGui.CalcTextSize(label).X + 12f;
        ImGui.SameLine(Math.Max(LabelWidth, labelEnd));
    }

    private static void Wrapped(string text)
    {
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + Math.Min(ImGui.GetContentRegionAvail().X, 520f));
        ImGui.TextColored(Theme.TextMuted, text);
        ImGui.PopTextWrapPos();
    }

    private static void HelpAfter(string help)
    {
        ImGui.SameLine(0, 8);
        Theme.HelpMarker(help);
    }

    private void FloatRow(string label, float value, Action<float> set, float min, float max, string format)
    {
        Row(label);
        var v = value;
        if (Theme.Slider($"##{label}", ref v, min, max, format, ControlWidth))
        {
            set(Math.Clamp(v, min, max));
            changed = true;
        }
    }

    private void ToggleRow(string label, bool value, Action<bool> set, string? help = null)
    {
        Row(label);
        var v = value;
        if (Theme.Toggle($"##tgl_{label}", ref v))
        {
            set(v);
            changed = true;
        }

        if (help is not null)
            HelpAfter(help);
    }

    private void ColorRow(string label, Vector4 value, Action<Vector4> set)
    {
        Row(label);
        var v = value;
        if (ImGui.ColorEdit4($"##{label}", ref v, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.AlphaPreviewHalf))
        {
            set(v);
            changed = true;
        }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Failed to open {Url}", url);
        }
    }
}

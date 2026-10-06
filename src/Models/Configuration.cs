using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Keys;
using MoogleMap.Input;

namespace MoogleMap.Models;

public enum ReducedMotionMode
{
    /// <summary>Follow Dalamud's reduced motion setting.</summary>
    Auto,
    On,
    Off,
}

/// <summary>How the map key behaves.</summary>
public enum TriggerMode
{
    /// <summary>Press once to show, again to hide.</summary>
    Toggle,
    /// <summary>Shown only while the key is held.</summary>
    Hold,
}

/// <summary>Where the centre of the map sits on screen.</summary>
public enum MapAnchor
{
    /// <summary>Centre of the screen, plus the offset.</summary>
    Screen,
    /// <summary>Over your character, wherever the camera puts them.</summary>
    Character,
}

/// <summary>Kinds of place that want different map views.</summary>
public enum ContentKind
{
    /// <summary>Dungeons, trials, raids, deep dungeons: tight spaces, close zoom.</summary>
    Duty,
    /// <summary>Cities, towns, housing: streets, medium zoom.</summary>
    Town,
    /// <summary>Open fields: huge, far zoom.</summary>
    Field,
}

/// <summary>How the map is framed for one kind of place.</summary>
[Serializable]
public class ViewPreset
{
    /// <summary>Screen pixels per yalm, at 1080p.</summary>
    public float Zoom { get; set; } = 5f;
    /// <summary>Radius of the visible map as a fraction of screen height.</summary>
    public float Radius { get; set; } = 0.42f;
    /// <summary>Multiplies the opacity of the whole map layer, markers excluded.</summary>
    public float MapOpacity { get; set; } = 0.85f;

    public static ViewPreset DefaultsFor(ContentKind kind) => kind switch
    {
        ContentKind.Duty => new ViewPreset { Zoom = 6f, Radius = 0.42f, MapOpacity = 0.85f },
        ContentKind.Town => new ViewPreset { Zoom = 4.5f, Radius = 0.42f, MapOpacity = 0.8f },
        _ => new ViewPreset { Zoom = 2f, Radius = 0.45f, MapOpacity = 0.75f },
    };
}

/// <summary>Configuration settings for the MoogleMap plugin.</summary>
[Serializable]
public class Configuration
{
    public int Version { get; set; } = 1;

    /// <summary>Master switch. Off, the key does nothing and nothing is drawn.</summary>
    public bool Enabled { get; set; } = true;

    // Trigger
    public Keybind ToggleKey { get; set; } = new(VirtualKey.M, alt: true);
    public TriggerMode Trigger { get; set; } = TriggerMode.Toggle;
    /// <summary>Hide the map key from the game, so it doesn't also run whatever the game binds to it.</summary>
    public bool ConsumeKey { get; set; } = true;
    public Keybind ZoomInKey { get; set; } = new();
    public Keybind ZoomOutKey { get; set; } = new();
    /// <summary>An entry in the server info bar to toggle the map, open settings and zoom.</summary>
    public bool ShowInServerBar { get; set; } = true;
    /// <summary>Whether the map was showing when the game closed, restored on login in toggle mode.</summary>
    public bool Shown { get; set; }

    // Game UI
    /// <summary>Hide the game's own minimap. Off by default.</summary>
    public bool HideGameMinimap { get; set; }
    /// <summary>Cut the map around open game windows so menus sit on top of it.</summary>
    public bool StayUnderGameWindows { get; set; } = true;
    /// <summary>Game windows the map never makes room for, by addon name.</summary>
    public HashSet<string> IgnoredWindows { get; set; } = [];

    // Look
    public Vector4 FloorColor { get; set; } = new(0.42f, 0.66f, 0.90f, 0.30f);
    public Vector4 EdgeColor { get; set; } = new(0.78f, 0.92f, 1f, 0.95f);
    /// <summary>Wall line thickness in pixels, at 1080p.</summary>
    public float WallThickness { get; set; } = 2f;
    /// <summary>A soft wider line under each wall, so walls read over bright scenery.</summary>
    public bool WallGlow { get; set; } = true;
    /// <summary>Master switch for the overlay's effects.</summary>
    public bool Effects { get; set; } = true;
    /// <summary>A band of light running out from you every few seconds, lighting the walls it crosses.</summary>
    public bool EffectSweep { get; set; } = true;
    /// <summary>A ring rippling out from your arrow now and then.</summary>
    public bool EffectRipple { get; set; } = true;
    /// <summary>Multiplies the opacity of markers and labels.</summary>
    public float MarkerOpacity { get; set; } = 1f;

    // Placement
    /// <summary>Zoom, size and opacity for each kind of place, so a dungeon and a field each look right.</summary>
    public Dictionary<ContentKind, ViewPreset> Views { get; set; } = new();
    /// <summary>Turn the map with the camera so "up" is where you're looking. Off is north-up.</summary>
    public bool RotateWithCamera { get; set; } = true;
    public MapAnchor Anchor { get; set; } = MapAnchor.Screen;
    /// <summary>Offset from the anchor, as a fraction of screen size.</summary>
    public Vector2 Offset { get; set; } = Vector2.Zero;
    /// <summary>Fraction of the radius over which the map fades out at its rim.</summary>
    public float EdgeFade { get; set; } = 0.3f;
    public ReducedMotionMode ReducedMotion { get; set; } = ReducedMotionMode.Auto;

    // Layers
    public bool ShowMapIcons { get; set; } = true;
    public bool ShowPlaceNames { get; set; } = true;
    public bool ShowQuestMarkers { get; set; } = true;
    public bool ShowFlag { get; set; } = true;
    /// <summary>NPCs and objects with a quest icon over them, shown with that icon.</summary>
    public bool ShowQuestNpcs { get; set; } = true;
    /// <summary>Quest objectives that are an area drawn as a circle, like the game's map.</summary>
    public bool QuestAreas { get; set; } = true;
    /// <summary>Quest names beside their markers.</summary>
    public bool QuestNames { get; set; } = true;
    /// <summary>Off-map markers kept on the rim as a compass, by kind.</summary>
    public bool PinFlag { get; set; } = true;
    public bool PinParty { get; set; } = true;
    /// <summary>Off by default: open fields are full of far-off quests that would crowd the rim.</summary>
    public bool PinQuests { get; set; }
    public bool ShowFates { get; set; } = true;
    /// <summary>Progress ring and time left on FATE icons.</summary>
    public bool FateDetails { get; set; } = true;
    public bool ShowParty { get; set; } = true;
    public bool ShowPlayers { get; set; } = true;
    public bool ShowEnemies { get; set; } = true;
    public bool ShowNpcs { get; set; } = true;
    public bool ShowObjects { get; set; } = true;
    public bool ShowGathering { get; set; } = true;
    /// <summary>Waymarks placed on the ground: A, B, C, D, 1 to 4.</summary>
    public bool ShowWaymarks { get; set; } = true;
    /// <summary>Signs over characters: attack, bind, ignore and the shapes.</summary>
    public bool ShowSigns { get; set; } = true;
    /// <summary>A ring around whatever you have targeted.</summary>
    public bool ShowTargetRing { get; set; } = true;
    /// <summary>Where you've walked lately, fading with time.</summary>
    public bool ShowTrail { get; set; } = true;
    /// <summary>How long a step stays on the trail, in seconds.</summary>
    public float TrailSeconds { get; set; } = 12f;
    /// <summary>In duties, the boss gets a crown, a bigger marker and its name and health always shown.</summary>
    public bool BossSpotlight { get; set; } = true;
    /// <summary>A wave sweeping out from you each time the map opens.</summary>
    public bool Sonar { get; set; } = true;
    /// <summary>Names next to enemies.</summary>
    public bool EnemyNames { get; set; }
    /// <summary>Names next to NPCs.</summary>
    public bool NpcNames { get; set; }
    /// <summary>Names next to treasure, aetherytes and other objects.</summary>
    public bool ObjectNames { get; set; } = true;
    /// <summary>N, E, S and W inside the map, turning with it.</summary>
    public bool ShowCompass { get; set; } = true;
    /// <summary>How far out the compass letters sit, as a fraction of the map's radius.</summary>
    public float CompassInset { get; set; } = 0.72f;
    /// <summary>Your camera's field of view, drawn as a soft cone from your marker.</summary>
    public bool ShowViewCone { get; set; } = true;
    public float IconScale { get; set; } = 1f;
    public float LabelScale { get; set; } = 1f;

    // Marker colours
    public Vector4 EnemyColor { get; set; } = new(0.93f, 0.33f, 0.28f, 1f);
    public Vector4 AggroColor { get; set; } = new(1f, 0.62f, 0.2f, 1f);
    /// <summary>Enemies whose target is you.</summary>
    public Vector4 TargetingYouColor { get; set; } = new(1f, 0.25f, 0.55f, 1f);
    public Vector4 TargetRingColor { get; set; } = new(0.97f, 0.86f, 0.55f, 1f);
    public Vector4 TrailColor { get; set; } = new(0.95f, 0.85f, 0.55f, 0.65f);
    public Vector4 PartyColor { get; set; } = new(0.45f, 0.85f, 0.55f, 1f);
    /// <summary>Colour party members by role instead of all in <see cref="PartyColor"/>.</summary>
    public bool PartyByRole { get; set; } = true;
    /// <summary>Job abbreviations beside party members.</summary>
    public bool PartyJobs { get; set; }
    /// <summary>Health rings around party members, and a skull for the knocked out.</summary>
    public bool PartyHealth { get; set; } = true;
    public Vector4 TankColor { get; set; } = new(0.36f, 0.56f, 0.96f, 1f);
    public Vector4 HealerColor { get; set; } = new(0.42f, 0.82f, 0.42f, 1f);
    public Vector4 DpsColor { get; set; } = new(0.86f, 0.36f, 0.36f, 1f);
    public Vector4 PlayerColor { get; set; } = new(0.55f, 0.7f, 1f, 1f);
    public Vector4 NpcColor { get; set; } = new(0.95f, 0.85f, 0.5f, 1f);
    public Vector4 ObjectColor { get; set; } = new(0.75f, 0.55f, 1f, 1f);

    // Text colours
    /// <summary>Region names printed on the map.</summary>
    public Vector4 PlaceNameColor { get; set; } = new(0.95f, 0.91f, 0.82f, 0.85f);
    /// <summary>Text beside map icons: shops, aetherytes, exits.</summary>
    public Vector4 IconLabelColor { get; set; } = new(0.95f, 0.91f, 0.82f, 0.9f);
    /// <summary>Enemy, NPC and object names take their marker's colour; off, they all use <see cref="NameColor"/>.</summary>
    public bool NamesMatchMarkers { get; set; } = true;
    public Vector4 NameColor { get; set; } = new(0.95f, 0.91f, 0.82f, 0.9f);

    // Exploring
    /// <summary>Where the game has no map, like deep dungeon floors, map the floor around you as you walk.</summary>
    public bool ExploreUnmapped { get; set; } = true;
    /// <summary>
    /// Find walkable floor from the game's collision and add it to the map: the only source in towns
    /// and fields, whose map pictures can't tell streets from rooftops, and a complement in duties.
    /// </summary>
    public bool SurveyMaps { get; set; } = true;
    /// <summary>How far around you the explorer reaches in open fields, in yalms.</summary>
    public float FieldExploreRadius { get; set; } = 100f;
    /// <summary>How far around you the explorer reaches in unmapped places like deep dungeons, in yalms.</summary>
    public float ExploreRadius { get; set; } = 32f;

    // When to show
    /// <summary>Hide while the cutscene, GPose or loading screens are up, and with the game UI hidden.</summary>
    public bool HideWithGameUi { get; set; } = true;
    public bool HideInCombat { get; set; }
    /// <summary>Keep the map on screen while the settings window is open, to see changes live.</summary>
    public bool PreviewWhileConfiguring { get; set; } = true;

    // Diagnostics
    public bool LogEvents { get; set; }

    /// <summary>The view for a kind of place, created from its defaults on first use.</summary>
    public ViewPreset ViewFor(ContentKind kind)
    {
        if (!Views.TryGetValue(kind, out var view) || view is null)
            Views[kind] = view = ViewPreset.DefaultsFor(kind);
        return view;
    }
}

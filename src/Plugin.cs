using System;
using System.Diagnostics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using MoogleMap.Input;
using MoogleMap.Map;
using MoogleMap.Models;
using MoogleMap.Rendering;
using MoogleMap.Services;
using MoogleMap.Windows;

namespace MoogleMap;

public enum ExploreMode
{
    None,
    /// <summary>Around the player only, like fog of war.</summary>
    Reveal,
    /// <summary>The whole area, to complete the map picture.</summary>
    Survey,
}

/// <summary>
/// Main plugin class for MoogleMap
/// </summary>
public sealed class Plugin : IDalamudPlugin
{
    public string Name => "MoogleMap";

    private const string CommandName = "/mooglemap";
    private const string CommandAlt = "/mmap";

    public const string RepoUrl = "https://github.com/GitHixy/MoogleMap";
    public const string PatreonUrl = "https://www.patreon.com/GitHixy";
    public const string DiscordUrl = "https://discord.gg/9kjUjWDBkJ";

    public const float MinZoom = 1f;
    public const float MaxZoom = 24f;

    // Dalamud Services
    [PluginService] public static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] public static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] public static IClientState ClientState { get; private set; } = null!;
    [PluginService] public static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] public static IGameGui GameGui { get; private set; } = null!;
    [PluginService] public static IPluginLog DalamudLog { get; private set; } = null!;
    [PluginService] public static ICondition Condition { get; private set; } = null!;
    [PluginService] public static IFateTable FateTable { get; private set; } = null!;
    [PluginService] public static IFramework Framework { get; private set; } = null!;
    [PluginService] public static IDataManager DataManager { get; private set; } = null!;
    [PluginService] public static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] public static IKeyState KeyState { get; private set; } = null!;
    [PluginService] public static IDtrBar DtrBar { get; private set; } = null!;
    [PluginService] public static ITargetManager TargetManager { get; private set; } = null!;

    /// <summary>Plugin-wide logger: writes to Dalamud's log and keeps a copy for the Diagnostics page.</summary>
    public static DiagnosticsLog Log { get; } = new(() => DalamudLog);

    // Plugin Services
    public ConfigurationService ConfigService { get; }
    public MapService Maps { get; }
    public LiveMarkers Live { get; }
    public Explorer Explorer { get; }
    public MapOverlay Overlay { get; }
    public KeyWatcher Keys { get; } = new();
    public ServerBarEntry ServerBar { get; }
    public Trail Trail { get; } = new();

    // Windows
    public ConfigWindow ConfigWindow { get; }

    public Configuration Configuration => ConfigService.Configuration;

    private readonly Stopwatch clock = Stopwatch.StartNew();

    /// <summary>Held down right now, in hold mode.</summary>
    private bool holding;

    public Plugin()
    {
        try
        {
            Log.Info("MoogleMap plugin initializing...");

            ConfigService = new ConfigurationService();
            Maps = new MapService();
            Live = new LiveMarkers();
            Explorer = new Explorer();
            Overlay = new MapOverlay(this);
            ServerBar = new ServerBarEntry(this);

            ConfigWindow = new ConfigWindow(this);

            CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
            {
                HelpMessage = "Open MoogleMap settings\n→ /mooglemap toggle - Show or hide the map\n→ /mooglemap zoom in|out - Zoom the map\n→ /mooglemap explore reset - Forget the explored floor"
            });
            CommandManager.AddHandler(CommandAlt, new CommandInfo(OnCommand) { ShowInHelp = false });

            Framework.Update += OnFrameworkUpdate;
            ClientState.TerritoryChanged += OnTerritoryChanged;
            ClientState.Logout += OnLogout;
            Condition.ConditionChange += OnConditionChange;

            PluginInterface.UiBuilder.Draw += Draw;
            PluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;
            PluginInterface.UiBuilder.OpenMainUi += OpenConfigUi;

            Log.Info("MoogleMap plugin initialized successfully!");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize MoogleMap plugin");
            throw;
        }
    }

    public void Dispose()
    {
        try
        {
            Log.Info("MoogleMap plugin disposing...");

            PluginInterface.UiBuilder.Draw -= Draw;
            PluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUi;
            PluginInterface.UiBuilder.OpenMainUi -= OpenConfigUi;

            Framework.Update -= OnFrameworkUpdate;
            ClientState.TerritoryChanged -= OnTerritoryChanged;
            ClientState.Logout -= OnLogout;
            Condition.ConditionChange -= OnConditionChange;

            SaveExplored();
            GameMinimap.SetHidden(false);
            ServerBar?.Dispose();
            ConfigWindow?.Dispose();
            Maps?.Dispose();
            ConfigService?.Dispose();

            CommandManager.RemoveHandler(CommandName);
            CommandManager.RemoveHandler(CommandAlt);

            Log.Info("MoogleMap plugin disposed successfully!");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error disposing MoogleMap plugin");
        }
    }

    private void OnCommand(string command, string args)
    {
        var parts = args.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (parts.Length > 0 ? parts[0] : string.Empty)
        {
            case "toggle":
                ToggleMap();
                break;
            case "show":
                SetShown(true);
                break;
            case "hide":
                SetShown(false);
                break;
            case "zoom" when parts.Length > 1 && parts[1] is "in" or "+":
                ZoomBy(1.25f);
                break;
            case "zoom" when parts.Length > 1 && parts[1] is "out" or "-":
                ZoomBy(0.8f);
                break;
            case "explore" when parts.Length > 1 && parts[1] == "reset":
                ResetExplored();
                Log.Info("Explored floor forgotten");
                break;
            default:
                ConfigWindow.IsOpen = !ConfigWindow.IsOpen;
                break;
        }
    }

    private void OpenConfigUi() => ConfigWindow.IsOpen = true;

    public void ToggleMap() => SetShown(!Configuration.Shown);

    public void SetShown(bool shown)
    {
        if (Configuration.Shown == shown) return;
        Configuration.Shown = shown;
        ConfigService.Save();
    }

    public void ZoomBy(float factor)
    {
        View.Zoom = Math.Clamp(View.Zoom * factor, MinZoom, MaxZoom);
        ConfigService.Save();
    }

    private void OnTerritoryChanged(uint territory)
    {
        Trail.Clear();
        Live.ForgetFates();
        SaveExplored();
        explorerKey = null;
        Overlay.Reset();
    }

    private void OnLogout(int type, int code)
    {
        GameMinimap.SetHidden(false);
        SaveExplored();
        explorerKey = null;
        Maps.Reset();
        Live.Clear();
        Explorer.Clear();
    }

    private void OnConditionChange(ConditionFlag flag, bool value)
    {
        // Deep dungeon floors change without a new territory, but always behind a loading screen.
        if (value && flag is ConditionFlag.BetweenAreas or ConditionFlag.BetweenAreas51 && ExplorerMode == ExploreMode.Reveal)
        {
            Explorer.Clear();
            Overlay.Reset();
        }
    }

    // ------------------------------------------------------------------
    // Exploring
    // ------------------------------------------------------------------

    /// <summary>Which territory and mode the explorer's data is for; null forces a fresh start.</summary>
    private (uint Territory, ExploreMode Mode)? explorerKey;
    private double saveExploredAt;

    /// <summary>
    /// Reveal where the game has no map picture: only around you, forgotten on every floor.
    /// Survey everywhere else, kept on disk: the whole area in towns and duties, and in open
    /// fields only around you, since they're too big and their collision loads in pieces.
    /// </summary>
    public ExploreMode ExplorerMode
    {
        get
        {
            var config = Configuration;
            if (!ClientState.IsLoggedIn) return ExploreMode.None;
            if (Maps.Current is not { HasTexture: true } map)
                return config.ExploreUnmapped ? ExploreMode.Reveal : ExploreMode.None;
            return config.SurveyMaps ? ExploreMode.Survey : ExploreMode.None;
        }
    }

    /// <summary>How far from the player the explorer works, or 0 for no limit.</summary>
    private float ExplorerReach(ExploreMode mode)
        => mode == ExploreMode.Reveal ? Configuration.ExploreRadius
           : Maps.Current?.IsOpenWorld == true ? Configuration.FieldExploreRadius
           : 0f;

    /// <summary>
    /// Explorer cells as big as a pixel of the processed map: a yalm in towns and duties, two in
    /// fields. Deep dungeons keep a yalm; their rooms are drawn from the cells directly.
    /// </summary>
    private float ExplorerCell(ExploreMode mode)
        => mode == ExploreMode.Survey && Maps.Current is { } map ? Math.Clamp(MathF.Round(2f / map.Scale), 1f, 4f) : 1f;

    /// <summary>
    /// How far along the map on screen is, 0 to 1, eased so it moves smoothly; null when there's
    /// nothing being built. Covers both exploring and the redraws that follow it.
    /// </summary>
    public float? MappingProgress { get; private set; }

    private float shownProgress = 1f;

    private void UpdateProgress(ExploreMode mode, float dt)
    {
        float target;
        if (Maps.Current is { HasTexture: true } && Maps.Layers.Count == 0)
            target = 0f;
        else if (mode == ExploreMode.None)
            target = 1f;
        else
        {
            // Exploring is done only once the map on screen has caught up with it too.
            var drawn = mode == ExploreMode.Survey && Explorer.FloorCells > 0
                ? Math.Clamp(Maps.FusedCells / (float)Explorer.FloorCells, 0f, 1f)
                : 1f;
            target = Math.Min(Explorer.Progress, drawn);
        }

        // Ease towards the real value; dropping back (new area found) is allowed but gentle.
        var rate = target > shownProgress ? 4f : 1.5f;
        shownProgress += (target - shownProgress) * Math.Clamp(dt * rate, 0f, 1f);
        if (Math.Abs(target - shownProgress) < 0.002f) shownProgress = target;

        MappingProgress = shownProgress >= 0.999f && target >= 0.999f ? null : shownProgress;
    }

    private static string ExploredFile(uint territory)
        => System.IO.Path.Combine(PluginInterface.ConfigDirectory.FullName, "explored", $"{territory}.bin");

    private void UpdateExplorer(Configuration config, ExploreMode mode)
    {
        var key = (ClientState.TerritoryType, mode);
        if (explorerKey != key)
        {
            SaveExplored();
            explorerKey = key;
            Explorer.Begin(key.TerritoryType, mode == ExploreMode.Survey ? ExploredFile(key.TerritoryType) : null, ExplorerCell(mode));
        }

        if (mode == ExploreMode.None || Live.PlayerPosition is not { } player
            || Condition[ConditionFlag.BetweenAreas] || Condition[ConditionFlag.BetweenAreas51])
            return;

        // Open fields get a little more time: there's far more ground to cover.
        var budget = Maps.Current?.IsOpenWorld == true ? 1.5 : 1.0;
        Explorer.Update(player, Live.Seeds, ExplorerReach(mode), budget);

        if (mode == ExploreMode.Survey && Explorer.Dirty && Now >= saveExploredAt)
        {
            saveExploredAt = Now + 30;
            SaveExplored();
        }
    }

    private void SaveExplored()
    {
        if (explorerKey is { Mode: ExploreMode.Survey } key && Explorer.Dirty && key.Territory == Explorer.Territory)
            Explorer.Save(ExploredFile(key.Territory));
    }

    /// <summary>Forgets the floor found so far, on disk too.</summary>
    public void ResetExplored()
    {
        Explorer.Clear();
        if (explorerKey is { Mode: ExploreMode.Survey } key)
            Explorer.Save(ExploredFile(key.Territory));
    }

    // ------------------------------------------------------------------
    // Frame
    // ------------------------------------------------------------------

    public double Now => clock.Elapsed.TotalSeconds;

    /// <summary>What kind of place the player is in, which picks the map view.</summary>
    public ContentKind CurrentKind => Maps.Current?.Kind ?? ContentKind.Town;

    /// <summary>Zoom, size and opacity for where the player is now.</summary>
    public ViewPreset View => Configuration.ViewFor(CurrentKind);

    public bool ReducedMotion => Configuration.ReducedMotion switch
    {
        ReducedMotionMode.On => true,
        ReducedMotionMode.Off => false,
        _ => PluginInterface.UiBuilder.ShouldUseReducedMotion,
    };

    /// <summary>Why the map is currently not drawn, or null when nothing stops it.</summary>
    public string? HiddenReason
    {
        get
        {
            if (!ClientState.IsLoggedIn) return "not logged in";
            if (ClientState.IsPvP) return "PvP";
            if (Condition[ConditionFlag.BetweenAreas] || Condition[ConditionFlag.BetweenAreas51]) return "loading";
            if (Configuration.HideWithGameUi)
            {
                if (GameGui.GameUiHidden) return "game UI hidden";
                if (ClientState.IsGPosing) return "GPose";
                if (Condition[ConditionFlag.OccupiedInCutSceneEvent]) return "cutscene event";
                if (Condition[ConditionFlag.WatchingCutscene] || Condition[ConditionFlag.WatchingCutscene78]) return "watching cutscene";
            }
            if (Configuration.HideInCombat && Condition[ConditionFlag.InCombat]) return "in combat";
            return null;
        }
    }

    /// <summary>Whether the map should be on screen this frame.</summary>
    public bool WantsMap
    {
        get
        {
            var config = Configuration;
            if (!config.Enabled || HiddenReason is not null) return false;
            if (config.PreviewWhileConfiguring && ConfigWindow.IsOpen) return true;
            // In hold mode, the toggle (server bar, /mmap toggle) pins the map open on top of the key.
            return config.Trigger == TriggerMode.Hold ? holding || config.Shown : config.Shown;
        }
    }

    /// <summary>Frames whose drawing threw, for the Diagnostics report.</summary>
    public int FrameErrors { get; private set; }
    private int loggedFrameErrors;

    private void OnFrameworkUpdate(IFramework framework)
    {
        try
        {
            var config = Configuration;
            HandleKeys(config);
            ServerBar.Update();
            GameMinimap.SetHidden(config.Enabled && config.HideGameMinimap && HiddenReason is null or "in combat");

            if (!ClientState.IsLoggedIn) return;

            var mode = ExplorerMode;
            Maps.Update(config, Now, mode == ExploreMode.Survey ? Explorer : null);
            Live.Update(config, Maps.Current, Now);
            UpdateExplorer(config, mode);
            if (config.ShowTrail && Live.PlayerPosition is { } walked && !Condition[ConditionFlag.BetweenAreas])
                Trail.Update(walked, Now, Math.Clamp(config.TrailSeconds, 3f, 30f));
            UpdateProgress(mode, (float)framework.UpdateDelta.TotalSeconds);
        }
        catch (Exception ex)
        {
            FrameErrors++;
            if (loggedFrameErrors++ < 5)
                Log.Error(ex, "Error during MoogleMap update");
        }
    }

    private void HandleKeys(Configuration config)
    {
        var binds = new[] { config.ToggleKey, config.ZoomInKey, config.ZoomOutKey };

        // Keys typed into chat, a text box or another window aren't meant for the map.
        if (!config.Enabled || !KeyWatcher.GameHasFocus || IsTyping() || ConfigWindow.CapturingKey)
        {
            Keys.Suppress(binds);
            holding = false;
            return;
        }

        if (config.Trigger == TriggerMode.Hold)
            holding = Keys.Held(KeyState, config.ToggleKey, config.ConsumeKey);
        else if (Keys.Pressed(KeyState, config.ToggleKey, config.ConsumeKey))
            ToggleMap();

        // Zoom keys only do anything while the map is up, so they're free for the game otherwise.
        var open = WantsMap;
        if (Keys.Pressed(KeyState, config.ZoomInKey, open) && open)
            ZoomBy(1.25f);
        if (Keys.Pressed(KeyState, config.ZoomOutKey, open) && open)
            ZoomBy(0.8f);
    }

    private static unsafe bool IsTyping()
    {
        if (ImGui.GetIO().WantTextInput) return true;
        var atk = RaptureAtkModule.Instance();
        return atk is not null && atk->AtkModule.IsTextInputActive();
    }

    private void Draw()
    {
        try
        {
            Maps.BeginFrame(Now, ReducedMotion);
            Overlay.Draw(WantsMap, ImGui.GetIO().DeltaTime);
            ConfigWindow.Draw();
        }
        catch (Exception ex)
        {
            // A per-frame error would flood the log; the first few are enough to diagnose it.
            FrameErrors++;
            if (loggedFrameErrors++ < 5)
                Log.Error(ex, "Error during MoogleMap frame");
        }
    }
}

using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MoogleMap.Rendering;

/// <summary>
/// Hides the game's own minimap (the _NaviMap addon) on request. Only whether its root node is
/// drawn is touched, and only what MoogleMap hid is ever shown again.
/// </summary>
public static class GameMinimap
{
    private const string Addon = "_NaviMap";

    /// <summary>Whether the root node was hidden by MoogleMap, so only that is ever undone.</summary>
    private static bool hiddenByUs;

    /// <summary>
    /// Keeps the game's minimap hidden or shown. Called every frame while hiding, since the game
    /// can rebuild the addon (zone changes, HUD layout changes) and show it again.
    /// </summary>
    public static unsafe void SetHidden(bool hide)
    {
        var manager = RaptureAtkUnitManager.Instance();
        var unit = manager is null ? null : manager->GetAddonByName(Addon);
        if (unit is null || unit->RootNode is null)
        {
            // Gone with whatever we did to it; a rebuilt one starts visible.
            if (!hide) hiddenByUs = false;
            return;
        }

        var root = unit->RootNode;
        if (hide)
        {
            if (!root->IsVisible()) return;
            root->ToggleVisibility(false);
            hiddenByUs = true;
        }
        else if (hiddenByUs)
        {
            root->ToggleVisibility(true);
            hiddenByUs = false;
        }
    }
}

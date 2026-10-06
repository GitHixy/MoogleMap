using System;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;

namespace MoogleMap.Services;

/// <summary>
/// MoogleMap's entry in the server info bar: shows whether the map is up (or how far along it
/// is), click to toggle it, right-click for settings, scroll to zoom.
/// </summary>
public sealed class ServerBarEntry : IDisposable
{
    private const string Title = "MoogleMap";

    private readonly Plugin plugin;
    private IDtrBarEntry? entry;
    private string? shownText;

    public ServerBarEntry(Plugin plugin)
    {
        this.plugin = plugin;
    }

    /// <summary>Framework tick: adds, updates or removes the entry to match the settings.</summary>
    public void Update()
    {
        if (!plugin.Configuration.ShowInServerBar || !plugin.Configuration.Enabled)
        {
            Remove();
            return;
        }

        if (entry is null)
        {
            entry = Plugin.DtrBar.Get(Title);
            entry.Tooltip = new SeStringBuilder()
                .AddText("MoogleMap")
                .AddText("\nClick: show or hide the map")
                .AddText("\nRight-click: settings")
                .AddText("\nScroll: zoom")
                .Build();
            entry.OnClick = OnClick;
            shownText = null;
        }

        var text = plugin.MappingProgress is { } progress && plugin.WantsMap
            ? $"Map: {progress * 100f:0}%"
            : plugin.WantsMap ? "Map: On" : "Map: Off";

        // Text changes rebuild the bar; only touch it when there's something new to say.
        if (text == shownText) return;
        shownText = text;
        entry.Text = text;
        entry.Shown = true;
    }

    private void OnClick(DtrInteractionEvent click)
    {
        switch (click.ScrollDirection)
        {
            case MouseScrollDirection.Up:
                plugin.ZoomBy(1.25f);
                return;
            case MouseScrollDirection.Down:
                plugin.ZoomBy(0.8f);
                return;
        }

        if (click.ClickType == MouseClickType.Right)
            plugin.ConfigWindow.IsOpen = !plugin.ConfigWindow.IsOpen;
        else
            plugin.ToggleMap();
    }

    private void Remove()
    {
        if (entry is null) return;
        entry.Remove();
        entry = null;
        shownText = null;
    }

    public void Dispose() => Remove();
}

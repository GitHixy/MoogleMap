using System;
using System.Collections.Generic;
using System.Numerics;
using Lumina.Excel.Sheets;

namespace MoogleMap.Map;

/// <summary>An icon or place name the game prints on a map, in map texture pixels.</summary>
public readonly record struct StaticMarker(Vector2 Position, uint Icon, string Label, byte LabelSide, bool IsArea);

/// <summary>The fixed icons and place names of a map, read once from the MapMarker sheet.</summary>
public sealed class StaticMarkers
{
    private readonly List<StaticMarker> markers = [];
    /// <summary>Where the icons sit in the world, X/Z.</summary>
    private readonly List<Vector2> iconsInWorld = [];

    public IReadOnlyList<StaticMarker> All => markers;

    /// <summary>Whether the map already prints an icon within <paramref name="yalms"/> of a world X/Z position.</summary>
    public bool HasIconNear(Vector2 world, float yalms)
    {
        var reach = yalms * yalms;
        foreach (var icon in iconsInWorld)
            if (Vector2.DistanceSquared(icon, world) <= reach)
                return true;
        return false;
    }

    public void Load(MapInfo? map)
    {
        markers.Clear();
        iconsInWorld.Clear();
        if (map is null || map.MarkerRange == 0) return;

        try
        {
            var sheet = Plugin.DataManager.GetSubrowExcelSheet<MapMarker>();
            if (!sheet.TryGetRow(map.MarkerRange, out var rows)) return;

            foreach (var row in rows)
            {
                var label = row.PlaceNameSubtext.ValueNullable?.Name.ToString() ?? string.Empty;
                if (row.Icon == 0 && label.Length == 0) continue;

                // Without an icon the entry is a region name printed straight onto the map.
                markers.Add(new StaticMarker(new Vector2(row.X, row.Y), row.Icon, label, row.SubtextOrientation, row.Icon == 0));
                if (row.Icon != 0)
                    iconsInWorld.Add(map.TextureToWorld(new Vector2(row.X, row.Y)));
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Couldn't read map markers for {Key}", map.Key);
        }
    }
}

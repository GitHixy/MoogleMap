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

    public IReadOnlyList<StaticMarker> All => markers;

    public void Load(MapInfo? map)
    {
        markers.Clear();
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
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Couldn't read map markers for {Key}", map.Key);
        }
    }
}

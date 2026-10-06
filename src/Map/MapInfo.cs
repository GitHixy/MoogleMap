using System.Numerics;
using MoogleMap.Models;
using Lumina.Excel.Sheets;

namespace MoogleMap.Map;

/// <summary>
/// One row of the Map sheet, with what's needed to place things on it. Map textures are
/// 2048 pixels square; a world position lands on pixel (world + offset) * scale + 1024, which is
/// also the space the MapMarker sheet stores its icons in.
/// </summary>
public sealed class MapInfo
{
    public const float TextureSize = 2048f;

    public uint RowId { get; }
    public uint TerritoryId { get; }
    /// <summary>Folder of the map under ui/map, like "s1d1/00".</summary>
    public string Key { get; }
    public string Name { get; }
    public string SubName { get; }
    /// <summary>Texture pixels per yalm.</summary>
    public float Scale { get; }
    public Vector2 Offset { get; }
    public uint MarkerRange { get; }

    /// <summary>
    /// Whether the game ships a picture of this map. Deep dungeons and a few instanced areas use
    /// "default/00", drawn by the game from room data instead.
    /// </summary>
    public bool HasTexture => Key.Length > 0 && !Key.StartsWith("default", System.StringComparison.Ordinal);

    public string TexturePath => $"ui/map/{Key}/{Key.Replace("/", string.Empty)}_m.tex";

    /// <summary>
    /// Open fields are drawn at half the scale of towns and duties: too big to survey, and
    /// mostly walkable anyway, so they keep the painted map.
    /// </summary>
    public bool IsOpenWorld => Scale < 1.5f;

    /// <summary>Instanced content: anything entered through the duty finder, deep dungeons included.</summary>
    public bool IsDuty { get; }

    public ContentKind Kind => IsOpenWorld ? ContentKind.Field : IsDuty ? ContentKind.Duty : ContentKind.Town;

    public string DisplayName => SubName.Length > 0 ? $"{Name} - {SubName}" : Name;

    public MapInfo(Lumina.Excel.Sheets.Map row)
    {
        RowId = row.RowId;
        TerritoryId = row.TerritoryType.RowId;
        Key = row.Id.ToString();
        Name = row.PlaceName.ValueNullable?.Name.ToString() ?? string.Empty;
        SubName = row.PlaceNameSub.ValueNullable?.Name.ToString() ?? string.Empty;
        Scale = row.SizeFactor / 100f;
        Offset = new Vector2(row.OffsetX, row.OffsetY);
        MarkerRange = row.MapMarkerRange;
        IsDuty = row.TerritoryType.ValueNullable?.ContentFinderCondition.RowId is > 0;
    }

    /// <summary>World X/Z to map texture pixels.</summary>
    public Vector2 WorldToTexture(Vector3 world)
        => (new Vector2(world.X, world.Z) + Offset) * Scale + new Vector2(TextureSize / 2f);

    /// <summary>Map texture pixels back to world X/Z.</summary>
    public Vector2 TextureToWorld(Vector2 texture)
        => (texture - new Vector2(TextureSize / 2f)) / Scale - Offset;
}

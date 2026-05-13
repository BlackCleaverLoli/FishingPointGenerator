using System.Numerics;
using Dalamud.Utility;
using FishingPointGenerator.Core.Models;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;
using OmenTools;
using OmenTools.Interop.Game.Helpers;

namespace FishingPointGenerator.Plugin.Services.Catalog;

internal sealed class LuminaFishingSpotCatalogBuilder
{
    private const string CatalogVersion = "1";

    private static readonly HashSet<uint> ExcludedTerritoryIds = [900, 1163];

    public FishingSpotCatalogDocument Build()
    {
        var data = DService.Instance().Data;
        var spots = data.GetExcelSheet<FishingSpot>()
            .Where(ShouldIncludeSpot)
            .Select(CreateTarget)
            .Where(target => target is not null)
            .Select(target => target!)
            .OrderBy(target => target.TerritoryId)
            .ThenBy(target => target.FishingSpotId)
            .ToList();

        return new FishingSpotCatalogDocument
        {
            CatalogVersion = CatalogVersion,
            SourceGameDataVersion = string.Empty,
            Spots = spots,
        };
    }

    private static bool ShouldIncludeSpot(FishingSpot spot)
    {
        if (spot.PlaceName.RowId == 0)
            return false;

        if (spot.TerritoryType.RowId == 0 || ExcludedTerritoryIds.Contains(spot.TerritoryType.RowId))
            return false;

        return true;
    }

    private static FishingSpotTarget? CreateTarget(FishingSpot spot)
    {
        var territory = spot.TerritoryType.ValueNullable;
        if (territory is null)
            return null;

        var map = territory.Value.Map.ValueNullable;
        var mapId = map?.RowId ?? 0;
        var mapX = 0f;
        var mapY = 0f;
        var worldX = 0f;
        var worldZ = 0f;
        if (map is { } mapRow)
        {
            var texturePoint = new Vector2((float)spot.X, (float)spot.Z);
            var mapPoint = PositionHelper.TextureToMap(
                (int)MathF.Round(texturePoint.X),
                (int)MathF.Round(texturePoint.Y),
                mapRow.SizeFactor);
            var worldPoint = PositionHelper.TextureToWorld(texturePoint, mapRow);
            mapX = mapPoint.X;
            mapY = mapPoint.Y;
            worldX = worldPoint.X;
            worldZ = worldPoint.Y;
        }

        return new FishingSpotTarget
        {
            FishingSpotId = spot.RowId,
            PlaceNameId = spot.PlaceName.RowId,
            Name = GetText(spot.PlaceName.ValueNullable?.Name),
            TerritoryId = spot.TerritoryType.RowId,
            TerritoryName = GetText(territory.Value.PlaceName.ValueNullable?.Name),
            MapId = mapId,
            MapX = mapX,
            MapY = mapY,
            WorldX = worldX,
            WorldZ = worldZ,
            Radius = spot.Radius,
            ItemIds = spot.Item
                .Select(item => item.RowId)
                .Where(itemId => itemId != 0)
                .Distinct()
                .OrderBy(itemId => itemId)
                .ToList(),
            CatalogVersion = CatalogVersion,
            SourceGameDataVersion = string.Empty,
        };
    }

    private static string GetText(ReadOnlySeString? text)
    {
        return text?.ToDalamudString().TextValue ?? string.Empty;
    }
}

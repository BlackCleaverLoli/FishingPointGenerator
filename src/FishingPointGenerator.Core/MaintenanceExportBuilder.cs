using FishingPointGenerator.Core.Models;

namespace FishingPointGenerator.Core;

public sealed class MaintenanceExportBuilder
{
    private const int ExportFloatDigits = 2;

    public ExportDocument Build(
        IEnumerable<SpotAnalysis> analyses,
        IEnumerable<TerritoryMaintenanceDocument> maintenanceDocuments)
    {
        ArgumentNullException.ThrowIfNull(analyses);
        ArgumentNullException.ThrowIfNull(maintenanceDocuments);

        var exportableKeys = analyses
            .Where(analysis => analysis.Exportable)
            .Select(analysis => analysis.Key)
            .ToHashSet();
        var exported = new List<(SpotKey Key, ExportedApproachPoint Point)>();

        foreach (var territory in maintenanceDocuments)
        {
            foreach (var spot in territory.Spots)
            {
                var key = new SpotKey(territory.TerritoryId, spot.FishingSpotId);
                if (!exportableKeys.Contains(key))
                    continue;

                foreach (var point in spot.ApproachPoints.Where(point => point.Status == ApproachPointStatus.Confirmed))
                {
                    exported.Add((
                        key,
                        new ExportedApproachPoint
                        {
                            PositionX = RoundExportFloat(point.Position.X),
                            PositionY = RoundExportFloat(point.Position.Y),
                            PositionZ = RoundExportFloat(point.Position.Z),
                            Rotation = RoundExportFloat(point.Rotation),
                        }));
                }
            }
        }

        return new ExportDocument
        {
            FishingSpots = exported
                .GroupBy(item => item.Key)
                .OrderBy(group => group.Key.TerritoryId)
                .ThenBy(group => group.Key.FishingSpotId)
                .Select(group => new ExportFishingSpot
                {
                    TerritoryId = group.Key.TerritoryId,
                    FishingSpotId = group.Key.FishingSpotId,
                    Points = group
                        .Select(item => item.Point)
                        .OrderBy(point => point.PositionX)
                        .ThenBy(point => point.PositionY)
                        .ThenBy(point => point.PositionZ)
                        .ThenBy(point => point.Rotation)
                        .ToList(),
                })
                .ToList(),
        };
    }

    private static float RoundExportFloat(float value)
    {
        return MathF.Round(value, ExportFloatDigits, MidpointRounding.AwayFromZero);
    }
}

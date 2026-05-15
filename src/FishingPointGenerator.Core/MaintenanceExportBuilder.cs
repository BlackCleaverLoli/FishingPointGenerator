using FishingPointGenerator.Core.Models;

namespace FishingPointGenerator.Core;

public sealed class MaintenanceExportBuilder
{
    private const int ExportFloatDigits = 2;

    public List<ExportedApproachPoint> Build(
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
                            FishingSpot = key.FishingSpotId,
                            PositionX = RoundExportFloat(point.Position.X),
                            PositionY = RoundExportFloat(point.Position.Y),
                            PositionZ = RoundExportFloat(point.Position.Z),
                            Rotation = RoundExportFloat(point.Rotation),
                        }));
                }
            }
        }

        return exported
            .OrderBy(item => item.Key.TerritoryId)
            .ThenBy(item => item.Key.FishingSpotId)
            .ThenBy(item => item.Point.PositionX)
            .ThenBy(item => item.Point.PositionY)
            .ThenBy(item => item.Point.PositionZ)
            .ThenBy(item => item.Point.Rotation)
            .Select(item => item.Point)
            .ToList();
    }

    private static float RoundExportFloat(float value)
    {
        return MathF.Round(value, ExportFloatDigits, MidpointRounding.AwayFromZero);
    }
}

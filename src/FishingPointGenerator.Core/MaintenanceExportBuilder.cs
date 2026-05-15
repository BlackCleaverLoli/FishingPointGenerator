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
        var exported = new List<(SpotKey Key, float[] Point)>();

        foreach (var territory in maintenanceDocuments)
        {
            foreach (var spot in territory.Spots)
            {
                var key = new SpotKey(territory.TerritoryId, spot.FishingSpotId);
                if (!exportableKeys.Contains(key))
                    continue;

                foreach (var point in spot.ApproachPoints.Where(IsExportableConfirmedPoint))
                {
                    exported.Add((
                        key,
                        new[]
                        {
                            RoundExportFloat(point.Position.X),
                            RoundExportFloat(point.Position.Y),
                            RoundExportFloat(point.Position.Z),
                            RoundExportFloat(point.Rotation),
                        }));
                }
            }
        }

        var spots = new SortedDictionary<uint, List<float[]>>();
        foreach (var group in exported
            .OrderBy(item => item.Key.TerritoryId)
            .ThenBy(item => item.Key.FishingSpotId)
            .ThenBy(item => item.Point[0])
            .ThenBy(item => item.Point[1])
            .ThenBy(item => item.Point[2])
            .ThenBy(item => item.Point[3])
            .GroupBy(item => item.Key.FishingSpotId))
        {
            spots[group.Key] = group
                .Select(item => item.Point)
                .ToList();
        }

        return new ExportDocument { Spots = spots };
    }

    private static bool IsExportableConfirmedPoint(ApproachPoint point)
    {
        return point.Status == ApproachPointStatus.Confirmed
            && (point.SourceKind == ApproachPointSourceKind.Candidate
                || point.SourceKind == ApproachPointSourceKind.AutoCastFill);
    }

    private static float RoundExportFloat(float value)
    {
        return MathF.Round(value, ExportFloatDigits, MidpointRounding.AwayFromZero);
    }
}

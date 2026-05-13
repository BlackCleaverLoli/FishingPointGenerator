using FishingPointGenerator.Core.Models;

namespace FishingPointGenerator.Core;

public sealed class MaintenanceExportBuilder
{
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
                            FishingSpot = spot.FishingSpotId,
                            PositionX = point.Position.X,
                            PositionY = point.Position.Y,
                            PositionZ = point.Position.Z,
                            Rotation = point.Rotation,
                            SourceBlockId = point.SourceBlockId,
                            SourceCandidateId = point.SourceCandidateId,
                            SourceCandidateFingerprint = point.SourceCandidateFingerprint,
                            SourceEvidenceId = point.EvidenceIds.FirstOrDefault() ?? string.Empty,
                            SourceScanId = point.SourceScanId,
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
                        .OrderBy(point => point.SourceEvidenceId, StringComparer.Ordinal)
                        .ThenBy(point => point.PositionX)
                        .ThenBy(point => point.PositionZ)
                        .ToList(),
                })
                .ToList(),
        };
    }
}

using FishingPointGenerator.Core.Models;

namespace FishingPointGenerator.Core;

public sealed class SpotExportBuilder
{
    private readonly SpotCandidateMatcher matcher;

    public SpotExportBuilder(SpotCandidateMatcher? matcher = null)
    {
        this.matcher = matcher ?? new SpotCandidateMatcher();
    }

    public ExportDocument Build(
        IEnumerable<SpotAnalysis> analyses,
        IEnumerable<SpotScanDocument> scans,
        IEnumerable<SpotLabelLedger> ledgers)
    {
        ArgumentNullException.ThrowIfNull(analyses);
        ArgumentNullException.ThrowIfNull(scans);
        ArgumentNullException.ThrowIfNull(ledgers);

        var scansByKey = scans.ToDictionary(scan => scan.Key);
        var ledgersByKey = ledgers.ToDictionary(ledger => ledger.Key);
        var exported = new List<(SpotKey Key, ExportedApproachPoint Point)>();

        foreach (var analysis in analyses.Where(analysis => analysis.Exportable))
        {
            if (!scansByKey.TryGetValue(analysis.Key, out var scan)
                || !ledgersByKey.TryGetValue(analysis.Key, out var ledger))
                continue;

            var rebind = matcher.Match(ledger, scan);
            foreach (var match in rebind.Matches)
            {
                var label = match.Label;
                var candidate = match.Candidate;
                var position = label.ConfirmedPosition ?? candidate.Position;
                exported.Add((
                    analysis.Key,
                    new ExportedApproachPoint
                    {
                        FishingSpot = analysis.Key.FishingSpotId,
                        PositionX = position.X,
                        PositionY = position.Y,
                        PositionZ = position.Z,
                        Rotation = label.ConfirmedRotation ?? candidate.Rotation,
                        Score = candidate.Score,
                        SourceBlockId = candidate.BlockId,
                        SourceCandidateId = candidate.SourceCandidateId,
                        SourceCandidateFingerprint = candidate.CandidateFingerprint,
                        SourceLabelId = label.EventId,
                        SourceScanId = scan.ScanId,
                    }));
            }
        }

        return new ExportDocument
        {
            FishingSpots = exported
                .GroupBy(point => point.Key)
                .OrderBy(group => group.Key.TerritoryId)
                .ThenBy(group => group.Key.FishingSpotId)
                .Select(group => new ExportFishingSpot
                {
                    TerritoryId = group.Key.TerritoryId,
                    FishingSpotId = group.Key.FishingSpotId,
                    Points = group
                        .Select(point => point.Point)
                        .OrderBy(point => point.SourceLabelId, StringComparer.Ordinal)
                        .ToList(),
                })
                .ToList(),
        };
    }
}

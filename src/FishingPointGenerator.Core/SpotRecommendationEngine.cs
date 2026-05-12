using FishingPointGenerator.Core.Models;

namespace FishingPointGenerator.Core;

public sealed class SpotRecommendationEngine
{
    public SpotAnalysis? PickNext(IEnumerable<SpotAnalysis> analyses)
    {
        ArgumentNullException.ThrowIfNull(analyses);

        return analyses
            .Where(analysis => analysis.Status is
                SpotAnalysisStatus.NeedsScan or
                SpotAnalysisStatus.NeedsVisit or
                SpotAnalysisStatus.NoCandidate or
                SpotAnalysisStatus.MixedRisk or
                SpotAnalysisStatus.OrphanedLabels or
                SpotAnalysisStatus.WeakCoverage)
            .OrderBy(analysis => GetPriority(analysis.Status))
            .ThenByDescending(analysis => analysis.RecommendedCandidate?.Score ?? 0f)
            .ThenBy(analysis => analysis.Key.TerritoryId)
            .ThenBy(analysis => analysis.Key.FishingSpotId)
            .FirstOrDefault();
    }

    private static int GetPriority(SpotAnalysisStatus status)
    {
        return status switch
        {
            SpotAnalysisStatus.OrphanedLabels => 0,
            SpotAnalysisStatus.MixedRisk => 1,
            SpotAnalysisStatus.NeedsScan => 2,
            SpotAnalysisStatus.NeedsVisit => 3,
            SpotAnalysisStatus.WeakCoverage => 4,
            SpotAnalysisStatus.NoCandidate => 5,
            _ => 99,
        };
    }
}

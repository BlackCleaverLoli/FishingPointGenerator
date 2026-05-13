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
                SpotAnalysisStatus.WeakCoverage)
            .OrderBy(analysis => GetPriority(analysis.Status))
            .ThenBy(analysis => analysis.RecommendedCandidate?.DistanceToTargetCenterMeters ?? float.MaxValue)
            .ThenBy(analysis => analysis.Key.TerritoryId)
            .ThenBy(analysis => analysis.Key.FishingSpotId)
            .FirstOrDefault();
    }

    private static int GetPriority(SpotAnalysisStatus status)
    {
        return status switch
        {
            SpotAnalysisStatus.MixedRisk => 1,
            SpotAnalysisStatus.NeedsScan => 2,
            SpotAnalysisStatus.NeedsVisit => 3,
            SpotAnalysisStatus.WeakCoverage => 4,
            SpotAnalysisStatus.NoCandidate => 5,
            _ => 99,
        };
    }
}

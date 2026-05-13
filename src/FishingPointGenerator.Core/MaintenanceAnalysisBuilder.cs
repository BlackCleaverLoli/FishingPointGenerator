using FishingPointGenerator.Core.Models;

namespace FishingPointGenerator.Core;

public sealed class MaintenanceAnalysisBuilder
{
    private readonly MaintenanceAnalysisOptions options;

    public MaintenanceAnalysisBuilder(MaintenanceAnalysisOptions? options = null)
    {
        this.options = options ?? new MaintenanceAnalysisOptions();
    }

    public SpotAnalysis Analyze(
        FishingSpotTarget target,
        SpotScanDocument? scan,
        SpotMaintenanceRecord? maintenance,
        SpotReviewDocument? legacyReview)
    {
        ArgumentNullException.ThrowIfNull(target);

        var reviewDecision = maintenance?.ReviewDecision ?? legacyReview?.Decision ?? SpotReviewDecision.None;
        if (HasDecision(reviewDecision, SpotReviewDecision.IgnoreSpot))
        {
            return new SpotAnalysis
            {
                Key = target.Key,
                Status = SpotAnalysisStatus.Ignored,
                CandidateCount = scan?.Candidates.Count ?? 0,
                ConfirmedApproachPointCount = CountConfirmedPoints(maintenance),
                Messages = ["该目标已在维护记录中明确忽略。"],
            };
        }

        var confirmedCount = CountConfirmedPoints(maintenance);
        var relevantCandidates = GetRelevantCandidates(scan);
        var candidateCount = relevantCandidates.Count;
        var hasMixedRisk = scan?.Candidates.Any(candidate =>
            candidate.NearbyFishingSpotIds.Any(id => id != 0 && id != target.FishingSpotId)) ?? false;

        if (hasMixedRisk && !HasDecision(reviewDecision, SpotReviewDecision.AllowRiskExport))
        {
            return new SpotAnalysis
            {
                Key = target.Key,
                Status = SpotAnalysisStatus.MixedRisk,
                CandidateCount = candidateCount,
                ConfirmedApproachPointCount = confirmedCount,
                HasMixedRisk = true,
                Messages = ["一个或多个候选点接近其它 FishingSpot 目标。"],
            };
        }

        if (confirmedCount > 0)
        {
            var status = confirmedCount >= options.MinimumConfirmedApproachPoints
                || HasDecision(reviewDecision, SpotReviewDecision.AllowWeakCoverageExport)
                    ? SpotAnalysisStatus.Confirmed
                    : SpotAnalysisStatus.WeakCoverage;

            return new SpotAnalysis
            {
                Key = target.Key,
                Status = status,
                CandidateCount = candidateCount,
                ConfirmedApproachPointCount = confirmedCount,
                HasMixedRisk = hasMixedRisk,
            };
        }

        if (scan is null || !scan.Key.IsValid)
        {
            return new SpotAnalysis
            {
                Key = target.Key,
                Status = SpotAnalysisStatus.NeedsScan,
                Messages = ["此 FishingSpot 没有关联的扫描缓存。"],
            };
        }

        if (candidateCount == 0)
        {
            return new SpotAnalysis
            {
                Key = target.Key,
                Status = SpotAnalysisStatus.NoCandidate,
                Messages = ["扫描已完成，但没有为此 FishingSpot 生成钓场范围内的候选点。"],
            };
        }

        return new SpotAnalysis
        {
            Key = target.Key,
            Status = SpotAnalysisStatus.NeedsVisit,
            CandidateCount = candidateCount,
            Messages = ["已有候选点，但尚未记录真实可钓点。"],
        };
    }

    private static int CountConfirmedPoints(SpotMaintenanceRecord? maintenance)
    {
        return maintenance?.ApproachPoints.Count(point => point.Status == ApproachPointStatus.Confirmed) ?? 0;
    }

    private static bool HasDecision(SpotReviewDecision decisions, SpotReviewDecision flag)
    {
        return (decisions & flag) == flag;
    }

    private static IReadOnlyList<SpotCandidate> GetRelevantCandidates(SpotScanDocument? scan)
    {
        if (scan is null || scan.Candidates.Count == 0)
            return [];

        var hasTargetRangeMetadata = scan.Candidates.Any(candidate =>
            candidate.IsWithinTargetSearchRadius || candidate.DistanceToTargetCenterMeters > 0f);
        if (!hasTargetRangeMetadata)
            return scan.Candidates;

        return scan.Candidates
            .Where(candidate => candidate.IsWithinTargetSearchRadius)
            .ToList();
    }
}

public sealed record MaintenanceAnalysisOptions
{
    public int MinimumConfirmedApproachPoints { get; init; } = 2;
}

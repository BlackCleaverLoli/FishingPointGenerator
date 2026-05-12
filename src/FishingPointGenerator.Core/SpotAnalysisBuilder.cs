using FishingPointGenerator.Core.Models;

namespace FishingPointGenerator.Core;

public sealed class SpotAnalysisBuilder
{
    private readonly SpotCandidateMatcher matcher;
    private readonly SpotAnalysisOptions options;

    public SpotAnalysisBuilder(SpotCandidateMatcher? matcher = null, SpotAnalysisOptions? options = null)
    {
        this.matcher = matcher ?? new SpotCandidateMatcher();
        this.options = options ?? new SpotAnalysisOptions();
    }

    public SpotAnalysis Analyze(
        FishingSpotTarget target,
        SpotScanDocument? scan,
        SpotLabelLedger? ledger,
        SpotReviewDocument? review)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (review?.Decision == SpotReviewDecision.IgnoreSpot)
        {
            return new SpotAnalysis
            {
                Key = target.Key,
                Status = SpotAnalysisStatus.Ignored,
                Messages = ["Target is explicitly ignored by review."],
            };
        }

        if (scan is null || !scan.Key.IsValid)
        {
            return new SpotAnalysis
            {
                Key = target.Key,
                Status = SpotAnalysisStatus.NeedsScan,
                Messages = ["No scan document exists for this fishing spot."],
            };
        }

        if (scan.Candidates.Count == 0)
        {
            return new SpotAnalysis
            {
                Key = target.Key,
                Status = SpotAnalysisStatus.NoCandidate,
                Messages = ["Scan completed but produced no candidates for this fishing spot."],
            };
        }

        var hasMixedRisk = scan.Candidates.Any(candidate =>
            candidate.NearbyFishingSpotIds.Any(id => id != 0 && id != target.FishingSpotId));

        var rebind = ledger is null
            ? new SpotLabelRebindResult([], [])
            : matcher.Match(ledger, scan);

        var confirmedCount = rebind.Matches.Count;
        var recommendedCandidate = PickRecommendedCandidate(scan, rebind, ledger);
        if (rebind.OrphanedLabels.Count > 0)
        {
            return new SpotAnalysis
            {
                Key = target.Key,
                Status = SpotAnalysisStatus.OrphanedLabels,
                CandidateCount = scan.Candidates.Count,
                ConfirmedLabelCount = confirmedCount,
                OrphanedLabelCount = rebind.OrphanedLabels.Count,
                HasMixedRisk = hasMixedRisk,
                RecommendedCandidate = recommendedCandidate,
                RecommendationReason = SpotRecommendationReason.OrphanedLabelReview,
                Messages = [$"{rebind.OrphanedLabels.Count} confirmed label(s) could not be rebound after the latest scan."],
            };
        }

        if (hasMixedRisk)
        {
            return new SpotAnalysis
            {
                Key = target.Key,
                Status = SpotAnalysisStatus.MixedRisk,
                CandidateCount = scan.Candidates.Count,
                ConfirmedLabelCount = confirmedCount,
                HasMixedRisk = true,
                RecommendedCandidate = recommendedCandidate,
                RecommendationReason = SpotRecommendationReason.MixedRiskReview,
                Messages = ["One or more candidates are close to another FishingSpot target."],
            };
        }

        if (confirmedCount > 0)
        {
            var status = (confirmedCount >= options.MinimumConfirmedLabels
                || review?.Decision == SpotReviewDecision.AllowWeakCoverageExport)
                    ? SpotAnalysisStatus.Confirmed
                    : SpotAnalysisStatus.WeakCoverage;

            return new SpotAnalysis
            {
                Key = target.Key,
                Status = status,
                CandidateCount = scan.Candidates.Count,
                ConfirmedLabelCount = confirmedCount,
                RecommendedCandidate = recommendedCandidate,
                RecommendationReason = status == SpotAnalysisStatus.WeakCoverage ? SpotRecommendationReason.WeakCoverage : null,
            };
        }

        return new SpotAnalysis
        {
            Key = target.Key,
            Status = SpotAnalysisStatus.NeedsVisit,
            CandidateCount = scan.Candidates.Count,
            RecommendedCandidate = recommendedCandidate,
            RecommendationReason = SpotRecommendationReason.NeedsVisit,
            Messages = ["Candidates exist, but no confirmed standing position has been recorded."],
        };
    }

    public SpotValidationReport BuildValidationReport(
        SpotAnalysis analysis,
        SpotLabelLedger? ledger,
        SpotScanDocument? scan)
    {
        ArgumentNullException.ThrowIfNull(analysis);

        List<SpotLabelEvent> orphanedLabels = ledger is not null && scan is not null
            ? matcher.Match(ledger, scan).OrphanedLabels.ToList()
            : [];
        var findings = analysis.Messages
            .Select(message => new SpotValidationFinding
            {
                Severity = analysis.Status is SpotAnalysisStatus.Confirmed ? SpotValidationSeverity.Info : SpotValidationSeverity.Warning,
                Code = analysis.Status.ToString(),
                Message = message,
            })
            .ToList();

        return new SpotValidationReport
        {
            Key = analysis.Key,
            Status = analysis.Status,
            Findings = findings,
            OrphanedLabels = orphanedLabels,
        };
    }

    private static SpotCandidate? PickRecommendedCandidate(
        SpotScanDocument scan,
        SpotLabelRebindResult rebind,
        SpotLabelLedger? ledger)
    {
        var excludedFingerprints = rebind.Matches
            .Select(match => match.Candidate.CandidateFingerprint)
            .ToHashSet(StringComparer.Ordinal);
        if (ledger is not null)
        {
            foreach (var labelEvent in ledger.Events)
            {
                if (labelEvent.EventType is SpotLabelEventType.Reject or SpotLabelEventType.Mismatch or SpotLabelEventType.IgnoreTarget
                    && !string.IsNullOrWhiteSpace(labelEvent.CandidateFingerprint))
                    excludedFingerprints.Add(labelEvent.CandidateFingerprint);
            }
        }

        return scan.Candidates
            .Where(candidate => candidate.Status is not CandidateStatus.Ignored and not CandidateStatus.Quarantined)
            .Where(candidate => !excludedFingerprints.Contains(candidate.CandidateFingerprint))
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.CandidateFingerprint, StringComparer.Ordinal)
            .FirstOrDefault()
            ?? scan.Candidates
                .OrderByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.CandidateFingerprint, StringComparer.Ordinal)
                .FirstOrDefault();
    }
}

public sealed record SpotAnalysisOptions
{
    public int MinimumConfirmedLabels { get; init; } = 2;
}

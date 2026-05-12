using FishingPointGenerator.Core.Geometry;
using FishingPointGenerator.Core.Models;

namespace FishingPointGenerator.Core;

public sealed class SpotCandidateMatcher
{
    private readonly SpotCandidateMatcherOptions options;

    public SpotCandidateMatcher(SpotCandidateMatcherOptions? options = null)
    {
        this.options = options ?? new SpotCandidateMatcherOptions();
    }

    public SpotLabelRebindResult Match(SpotLabelLedger ledger, SpotScanDocument scan)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(scan);

        var matches = new List<SpotLabelMatch>();
        var orphaned = new List<SpotLabelEvent>();
        var matchedCandidates = new HashSet<string>(StringComparer.Ordinal);
        var candidatesByFingerprint = scan.Candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.CandidateFingerprint))
            .GroupBy(candidate => candidate.CandidateFingerprint, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        foreach (var label in GetBindableEvents(ledger))
        {
            if (!string.IsNullOrWhiteSpace(label.CandidateFingerprint)
                && candidatesByFingerprint.TryGetValue(label.CandidateFingerprint, out var exactCandidate))
            {
                matches.Add(new SpotLabelMatch(label, exactCandidate, SpotLabelMatchType.ExactFingerprint));
                matchedCandidates.Add(exactCandidate.CandidateFingerprint);
                continue;
            }

            var spatialCandidate = FindSpatialMatch(label, scan.Candidates, matchedCandidates);
            if (spatialCandidate is not null)
            {
                matches.Add(new SpotLabelMatch(label, spatialCandidate, SpotLabelMatchType.Spatial));
                matchedCandidates.Add(spatialCandidate.CandidateFingerprint);
                continue;
            }

            orphaned.Add(label);
        }

        return new SpotLabelRebindResult(matches, orphaned);
    }

    private static IEnumerable<SpotLabelEvent> GetBindableEvents(SpotLabelLedger ledger)
    {
        return ledger.Events
            .Where(label => label.EventType is SpotLabelEventType.Confirm or SpotLabelEventType.Override)
            .Where(label => label.TerritoryId == ledger.Key.TerritoryId)
            .Where(label => label.FishingSpotId == ledger.Key.FishingSpotId);
    }

    private SpotCandidate? FindSpatialMatch(
        SpotLabelEvent label,
        IEnumerable<SpotCandidate> candidates,
        HashSet<string> matchedCandidates)
    {
        if (label.ConfirmedPosition is null && label.ConfirmedTargetPoint is null)
            return null;

        return candidates
            .Where(candidate => !matchedCandidates.Contains(candidate.CandidateFingerprint))
            .Select(candidate => new
            {
                Candidate = candidate,
                PositionDistance = label.ConfirmedPosition is { } position
                    ? candidate.Position.HorizontalDistanceTo(position)
                    : 0f,
                TargetDistance = label.ConfirmedTargetPoint is { } targetPoint
                    ? candidate.TargetPoint.HorizontalDistanceTo(targetPoint)
                    : 0f,
                RotationDistance = label.ConfirmedRotation is { } rotation
                    ? AngleMath.AngularDistance(candidate.Rotation, rotation)
                    : 0f,
            })
            .Where(match => label.ConfirmedPosition is null || match.PositionDistance <= options.ConfirmedPositionToleranceMeters)
            .Where(match => label.ConfirmedTargetPoint is null || match.TargetDistance <= options.ConfirmedTargetPointToleranceMeters)
            .Where(match => label.ConfirmedRotation is null || match.RotationDistance <= options.RotationToleranceRadians)
            .OrderBy(match => match.PositionDistance)
            .ThenBy(match => match.TargetDistance)
            .ThenBy(match => match.RotationDistance)
            .ThenBy(match => match.Candidate.CandidateFingerprint, StringComparer.Ordinal)
            .Select(match => match.Candidate)
            .FirstOrDefault();
    }
}

public sealed record SpotCandidateMatcherOptions
{
    public float ConfirmedPositionToleranceMeters { get; init; } = 4f;
    public float ConfirmedTargetPointToleranceMeters { get; init; } = 10f;
    public float RotationToleranceRadians { get; init; } = 0.65f;
}

public enum SpotLabelMatchType
{
    ExactFingerprint,
    Spatial,
}

public sealed record SpotLabelMatch(
    SpotLabelEvent Label,
    SpotCandidate Candidate,
    SpotLabelMatchType MatchType);

public sealed record SpotLabelRebindResult(
    IReadOnlyList<SpotLabelMatch> Matches,
    IReadOnlyList<SpotLabelEvent> OrphanedLabels);

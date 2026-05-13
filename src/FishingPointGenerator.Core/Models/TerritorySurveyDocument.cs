namespace FishingPointGenerator.Core.Models;

public sealed record TerritorySurveyDocument
{
    public int Version { get; init; } = 1;
    public uint TerritoryId { get; init; }
    public string TerritoryName { get; init; } = string.Empty;
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;
    public SurveyReachabilityMode ReachabilityMode { get; init; } = SurveyReachabilityMode.NotChecked;
    public Point3? ReachabilityOrigin { get; init; }
    public int RawCandidateCount { get; init; }
    public int ReachableCandidateCount { get; init; }
    public int UnreachableCandidateCount { get; init; }
    public string ReachabilityNote { get; init; } = string.Empty;
    public List<ApproachCandidate> Candidates { get; init; } = [];
}

public enum SurveyReachabilityMode
{
    NotChecked,
    Flyable,
    WalkPath,
}

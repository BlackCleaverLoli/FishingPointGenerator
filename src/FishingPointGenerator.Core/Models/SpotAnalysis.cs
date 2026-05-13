using System.Text.Json.Serialization;

namespace FishingPointGenerator.Core.Models;

public enum SpotAnalysisStatus
{
    NotStarted,
    NeedsScan,
    NeedsVisit,
    Confirmed,
    WeakCoverage,
    MixedRisk,
    NoCandidate,
    Ignored,
    Stale,
}

public enum SpotRecommendationReason
{
    NeedsVisit,
    WeakCoverage,
    MixedRiskReview,
}

public sealed record SpotAnalysis
{
    public SpotKey Key { get; init; }
    public SpotAnalysisStatus Status { get; init; }
    public int CandidateCount { get; init; }
    public int ConfirmedApproachPointCount { get; init; }
    public bool HasMixedRisk { get; init; }
    public SpotCandidate? RecommendedCandidate { get; init; }
    public SpotRecommendationReason? RecommendationReason { get; init; }
    public List<string> Messages { get; init; } = [];

    [JsonIgnore]
    public bool Exportable => Status == SpotAnalysisStatus.Confirmed;
}

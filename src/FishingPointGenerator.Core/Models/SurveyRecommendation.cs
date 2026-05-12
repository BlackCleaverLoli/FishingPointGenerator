namespace FishingPointGenerator.Core.Models;

public sealed record SurveyRecommendation
{
    public SurveyRecommendationReason Reason { get; init; }
    public string BlockId { get; init; } = string.Empty;
    public ApproachCandidate? Candidate { get; init; }
    public float DistanceMeters { get; init; }
}

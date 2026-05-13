namespace FishingPointGenerator.Core;

public sealed record SurveyBlockOptions
{
    public float RegionLinkDistanceMeters { get; init; } = 18f;
    public float BlockLinkDistanceMeters { get; init; } = 4f;
    public float BlockHeightToleranceMeters { get; init; } = 2f;
}

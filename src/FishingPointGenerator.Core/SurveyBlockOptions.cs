namespace FishingPointGenerator.Core;

public sealed record SurveyBlockOptions
{
    public float RegionLinkDistanceMeters { get; init; } = 18f;
    public float BlockLinkDistanceMeters { get; init; } = 4f;
    public float BlockHeightToleranceMeters { get; init; } = 2f;
    public float BlockRotationToleranceRadians { get; init; } = MathF.PI * 35f / 180f;
    public float MixedBlockQuarantinePaddingMeters { get; init; } = 3f;
}

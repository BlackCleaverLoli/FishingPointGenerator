namespace FishingPointGenerator.Core.Models;

[Flags]
public enum SpotReviewDecision
{
    None = 0,
    AllowWeakCoverageExport = 1,
    AllowRiskExport = 2,
    IgnoreSpot = 4,
    NeedsManualReview = 8,
}

public sealed record SpotReviewDocument
{
    public int Version { get; init; } = 1;
    public SpotKey Key { get; init; }
    public SpotReviewDecision Decision { get; init; }
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string Note { get; init; } = string.Empty;
}

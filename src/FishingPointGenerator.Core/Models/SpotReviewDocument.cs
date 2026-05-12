namespace FishingPointGenerator.Core.Models;

public enum SpotReviewDecision
{
    None,
    AllowWeakCoverageExport,
    IgnoreSpot,
    NeedsManualReview,
}

public sealed record SpotReviewDocument
{
    public int Version { get; init; } = 1;
    public SpotKey Key { get; init; }
    public SpotReviewDecision Decision { get; init; }
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string Note { get; init; } = string.Empty;
}

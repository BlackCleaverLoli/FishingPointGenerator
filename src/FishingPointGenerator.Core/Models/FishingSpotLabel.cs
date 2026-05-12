namespace FishingPointGenerator.Core.Models;

public sealed record FishingSpotLabel
{
    public string LabelId { get; init; } = Guid.NewGuid().ToString("N");
    public uint TerritoryId { get; init; }
    public string BlockId { get; init; } = string.Empty;
    public string? CandidateId { get; init; }
    public uint FishingSpotId { get; init; }
    public LabelStatus Status { get; init; }
    public Point3 ConfirmedPosition { get; init; }
    public float ConfirmedRotation { get; init; }
    public DateTimeOffset ConfirmedAt { get; init; } = DateTimeOffset.UtcNow;
    public string Note { get; init; } = string.Empty;
}

public sealed record TerritoryLabelsDocument
{
    public int Version { get; init; } = 1;
    public uint TerritoryId { get; init; }
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public List<FishingSpotLabel> Labels { get; init; } = [];
}

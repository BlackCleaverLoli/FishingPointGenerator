namespace FishingPointGenerator.Core.Models;

public sealed record ExportDocument
{
    public int Version { get; init; } = 1;
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;
    public List<ExportFishingSpot> FishingSpots { get; init; } = [];
}

public sealed record ExportFishingSpot
{
    public uint TerritoryId { get; init; }
    public uint FishingSpotId { get; init; }
    public List<ExportedApproachPoint> Points { get; init; } = [];
}

public sealed record ExportedApproachPoint
{
    public float X { get; init; }
    public float Y { get; init; }
    public float Z { get; init; }
    public float Rotation { get; init; }
    public float TargetX { get; init; }
    public float TargetY { get; init; }
    public float TargetZ { get; init; }
    public float Score { get; init; }
    public string SourceBlockId { get; init; } = string.Empty;
    public string SourceCandidateId { get; init; } = string.Empty;
    public string SourceCandidateFingerprint { get; init; } = string.Empty;
    public string SourceLabelId { get; init; } = string.Empty;
    public string SourceScanId { get; init; } = string.Empty;
}

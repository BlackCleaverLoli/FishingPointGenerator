using System.Text.Json.Serialization;

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
    [JsonPropertyName("FishingSpot")]
    public uint FishingSpot { get; init; }

    [JsonPropertyName("PositionX")]
    public float PositionX { get; init; }

    [JsonPropertyName("PositionY")]
    public float PositionY { get; init; }

    [JsonPropertyName("PositionZ")]
    public float PositionZ { get; init; }

    [JsonPropertyName("Rotation")]
    public float Rotation { get; init; }

    public float Score { get; init; }
    public string SourceBlockId { get; init; } = string.Empty;
    public string SourceCandidateId { get; init; } = string.Empty;
    public string SourceCandidateFingerprint { get; init; } = string.Empty;
    public string SourceLabelId { get; init; } = string.Empty;
    public string SourceScanId { get; init; } = string.Empty;
}

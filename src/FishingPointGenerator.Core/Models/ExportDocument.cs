using System.Text.Json.Serialization;

namespace FishingPointGenerator.Core.Models;

public sealed record ExportDocument
{
    public int Version { get; init; } = 2;
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
    [JsonPropertyName("PositionX")]
    public float PositionX { get; init; }

    [JsonPropertyName("PositionY")]
    public float PositionY { get; init; }

    [JsonPropertyName("PositionZ")]
    public float PositionZ { get; init; }

    [JsonPropertyName("Rotation")]
    public float Rotation { get; init; }
}

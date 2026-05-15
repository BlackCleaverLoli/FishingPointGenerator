using System.Text.Json.Serialization;

namespace FishingPointGenerator.Core.Models;

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
}

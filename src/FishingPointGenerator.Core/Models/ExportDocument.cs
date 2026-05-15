using System.Text.Json.Serialization;

namespace FishingPointGenerator.Core.Models;

public sealed record ExportDocument
{
    [JsonPropertyName("version")]
    public int Version { get; init; } = 2;

    [JsonPropertyName("spots")]
    public SortedDictionary<uint, List<float[]>> Spots { get; init; } = [];
}

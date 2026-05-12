using System.Text.Json.Serialization;

namespace FishingPointGenerator.Core.Models;

public readonly record struct SpotKey(uint TerritoryId, uint FishingSpotId)
{
    [JsonIgnore]
    public bool IsValid => TerritoryId != 0 && FishingSpotId != 0;

    public override string ToString() => $"{TerritoryId}:{FishingSpotId}";
}

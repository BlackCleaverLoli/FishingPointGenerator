using System.Text.Json.Serialization;

namespace FishingPointGenerator.Core.Models;

public sealed record FishingSpotCatalogDocument
{
    public int Version { get; init; } = 1;
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;
    public string CatalogVersion { get; init; } = "1";
    public string SourceGameDataVersion { get; init; } = string.Empty;
    public List<FishingSpotTarget> Spots { get; init; } = [];
}

public sealed record FishingSpotTarget
{
    public uint FishingSpotId { get; init; }
    public uint PlaceNameId { get; init; }
    public string Name { get; init; } = string.Empty;
    public uint TerritoryId { get; init; }
    public string TerritoryName { get; init; } = string.Empty;
    public uint MapId { get; init; }
    public float MapX { get; init; }
    public float MapY { get; init; }
    public float WorldX { get; init; }
    public float WorldZ { get; init; }
    public float Radius { get; init; }
    public List<uint> ItemIds { get; init; } = [];
    public string CatalogVersion { get; init; } = "1";
    public string SourceGameDataVersion { get; init; } = string.Empty;

    [JsonIgnore]
    public SpotKey Key => new(TerritoryId, FishingSpotId);
}

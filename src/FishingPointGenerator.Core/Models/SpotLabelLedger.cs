using System.Text.Json.Serialization;

namespace FishingPointGenerator.Core.Models;

public enum SpotLabelEventType
{
    Confirm,
    Reject,
    IgnoreTarget,
    Override,
}

public sealed record SpotLabelLedger
{
    public int Version { get; init; } = 1;
    public SpotKey Key { get; init; }
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public List<SpotLabelEvent> Events { get; init; } = [];
}

public sealed record SpotLabelEvent
{
    public string EventId { get; init; } = Guid.NewGuid().ToString("N");
    public SpotLabelEventType EventType { get; init; }
    public uint TerritoryId { get; init; }
    public uint FishingSpotId { get; init; }
    public string CandidateFingerprint { get; init; } = string.Empty;
    public Point3? ConfirmedPosition { get; init; }
    public float? ConfirmedRotation { get; init; }
    public string SourceScanId { get; init; } = string.Empty;
    public string SourceScannerVersion { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string Note { get; init; } = string.Empty;

    [JsonIgnore]
    public SpotKey Key => new(TerritoryId, FishingSpotId);
}

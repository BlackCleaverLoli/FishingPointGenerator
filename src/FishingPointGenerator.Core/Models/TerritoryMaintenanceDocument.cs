namespace FishingPointGenerator.Core.Models;

public sealed record TerritoryMaintenanceDocument
{
    public int Version { get; init; } = 1;
    public uint TerritoryId { get; init; }
    public string TerritoryName { get; init; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public List<SpotMaintenanceRecord> Spots { get; init; } = [];
}

public sealed record SpotMaintenanceRecord
{
    public uint FishingSpotId { get; init; }
    public string Name { get; init; } = string.Empty;
    public SpotReviewDecision ReviewDecision { get; init; }
    public string ReviewNote { get; init; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public List<ApproachPoint> ApproachPoints { get; init; } = [];
    public List<SpotEvidenceEvent> Evidence { get; init; } = [];
}

public enum ApproachPointStatus
{
    Confirmed,
    Rejected,
    Disabled,
}

public enum ApproachPointSourceKind
{
    Manual,
    Candidate,
    AutoCastFill,
    Imported,
}

public sealed record ApproachPoint
{
    public string PointId { get; init; } = string.Empty;
    public Point3 Position { get; init; }
    public float Rotation { get; init; }
    public ApproachPointStatus Status { get; init; } = ApproachPointStatus.Confirmed;
    public ApproachPointSourceKind SourceKind { get; init; } = ApproachPointSourceKind.Manual;
    public string SourceCandidateFingerprint { get; init; } = string.Empty;
    public string SourceCandidateId { get; init; } = string.Empty;
    public string SourceBlockId { get; init; } = string.Empty;
    public string SourceScanId { get; init; } = string.Empty;
    public string SourceScannerVersion { get; init; } = string.Empty;
    public List<string> EvidenceIds { get; init; } = [];
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string Note { get; init; } = string.Empty;
}

public enum SpotEvidenceEventType
{
    ManualConfirm,
    AutoCastFill,
    Reject,
    Review,
}

public sealed record SpotEvidenceEvent
{
    public string EventId { get; init; } = Guid.NewGuid().ToString("N");
    public SpotEvidenceEventType EventType { get; init; }
    public Point3? Position { get; init; }
    public float? Rotation { get; init; }
    public string CandidateFingerprint { get; init; } = string.Empty;
    public string SourceScanId { get; init; } = string.Empty;
    public string SourceScannerVersion { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string Note { get; init; } = string.Empty;
}

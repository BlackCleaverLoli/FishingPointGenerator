namespace FishingPointGenerator.Core.Models;

public sealed record SpotScanDocument
{
    public int Version { get; init; } = 1;
    public SpotKey Key { get; init; }
    public string ScanId { get; init; } = Guid.NewGuid().ToString("N");
    public string ScannerName { get; init; } = string.Empty;
    public string ScannerVersion { get; init; } = string.Empty;
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;
    public List<SpotCandidate> Candidates { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
}

public sealed record SpotCandidate
{
    public SpotKey Key { get; init; }
    public string CandidateFingerprint { get; init; } = string.Empty;
    public Point3 Position { get; init; }
    public float Rotation { get; init; }
    public Point3 TargetPoint { get; init; }
    public float Score { get; init; }
    public CandidateStatus Status { get; init; }
    public string SourceCandidateId { get; init; } = string.Empty;
    public List<uint> NearbyFishingSpotIds { get; init; } = [];
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

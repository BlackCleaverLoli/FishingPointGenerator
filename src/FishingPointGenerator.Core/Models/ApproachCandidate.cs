namespace FishingPointGenerator.Core.Models;

public sealed record ApproachCandidate
{
    public string CandidateId { get; init; } = Guid.NewGuid().ToString("N");
    public uint TerritoryId { get; init; }
    public string RegionId { get; init; } = string.Empty;
    public string BlockId { get; init; } = string.Empty;
    public string SurfaceGroupId { get; init; } = string.Empty;
    public Point3 Position { get; init; }
    public float Rotation { get; init; }
    public CandidateStatus Status { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

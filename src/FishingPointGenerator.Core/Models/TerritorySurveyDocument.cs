namespace FishingPointGenerator.Core.Models;

public sealed record TerritorySurveyDocument
{
    public int Version { get; init; } = 1;
    public uint TerritoryId { get; init; }
    public string TerritoryName { get; init; } = string.Empty;
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;
    public List<ApproachCandidate> Candidates { get; init; } = [];
}

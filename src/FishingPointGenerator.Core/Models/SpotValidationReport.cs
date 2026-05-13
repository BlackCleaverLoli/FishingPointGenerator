namespace FishingPointGenerator.Core.Models;

public enum SpotValidationSeverity
{
    Info,
    Warning,
    Error,
}

public sealed record SpotValidationReport
{
    public int Version { get; init; } = 1;
    public SpotKey Key { get; init; }
    public string TerritoryName { get; init; } = string.Empty;
    public string FishingSpotName { get; init; } = string.Empty;
    public SpotAnalysisStatus Status { get; init; }
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;
    public int CandidateCount { get; init; }
    public int ConfirmedApproachPointCount { get; init; }
    public List<SpotValidationFinding> Findings { get; init; } = [];
    public List<ApproachPoint> ApproachPoints { get; init; } = [];
    public List<SpotEvidenceEvent> Evidence { get; init; } = [];
}

public sealed record SpotValidationFinding
{
    public SpotValidationSeverity Severity { get; init; }
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

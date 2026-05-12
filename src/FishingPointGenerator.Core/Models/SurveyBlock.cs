namespace FishingPointGenerator.Core.Models;

public sealed record SurveyBlock
{
    public uint TerritoryId { get; init; }
    public string RegionId { get; init; } = string.Empty;
    public string BlockId { get; init; } = string.Empty;
    public IReadOnlyList<ApproachCandidate> Candidates { get; init; } = [];

    public Point3 Center
    {
        get
        {
            if (Candidates.Count == 0)
                return default;

            var x = 0f;
            var y = 0f;
            var z = 0f;
            foreach (var candidate in Candidates)
            {
                x += candidate.Position.X;
                y += candidate.Position.Y;
                z += candidate.Position.Z;
            }

            return new Point3(x / Candidates.Count, y / Candidates.Count, z / Candidates.Count);
        }
    }
}

public sealed record SurveyBlockState
{
    public SurveyBlock Block { get; init; } = new();
    public SurveyBlockStatus Status { get; init; }
    public IReadOnlySet<uint> FishingSpotIds { get; init; } = new HashSet<uint>();
    public int LabelCount { get; init; }
    public bool Exportable => Status == SurveyBlockStatus.SingleSpot;
}

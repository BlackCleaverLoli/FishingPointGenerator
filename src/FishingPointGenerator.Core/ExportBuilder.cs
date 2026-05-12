using FishingPointGenerator.Core.Models;

namespace FishingPointGenerator.Core;

public sealed class ExportBuilder
{
    public ExportDocument Build(IEnumerable<SurveyBlockState> states)
    {
        ArgumentNullException.ThrowIfNull(states);

        var points = states
            .Where(state => state.Exportable)
            .Where(state => state.FishingSpotIds.Count == 1)
            .SelectMany(state =>
            {
                var fishingSpotId = state.FishingSpotIds.Single();
                return state.Block.Candidates
                    .Where(candidate => candidate.Status is not CandidateStatus.Ignored and not CandidateStatus.Quarantined)
                    .Select(candidate => new
                    {
                        state.Block.TerritoryId,
                        FishingSpotId = fishingSpotId,
                        Point = new ExportedApproachPoint
                        {
                            X = candidate.Position.X,
                            Y = candidate.Position.Y,
                            Z = candidate.Position.Z,
                            Rotation = candidate.Rotation,
                            TargetX = candidate.TargetPoint.X,
                            TargetY = candidate.TargetPoint.Y,
                            TargetZ = candidate.TargetPoint.Z,
                            Score = candidate.Score,
                            SourceBlockId = state.Block.BlockId,
                            SourceCandidateId = candidate.CandidateId,
                        },
                    });
            })
            .ToList();

        var fishingSpots = points
            .GroupBy(point => new { point.TerritoryId, point.FishingSpotId })
            .OrderBy(group => group.Key.TerritoryId)
            .ThenBy(group => group.Key.FishingSpotId)
            .Select(group => new ExportFishingSpot
            {
                TerritoryId = group.Key.TerritoryId,
                FishingSpotId = group.Key.FishingSpotId,
                Points = group
                    .OrderBy(point => point.Point.SourceBlockId, StringComparer.Ordinal)
                    .ThenByDescending(point => point.Point.Score)
                    .ThenBy(point => point.Point.SourceCandidateId, StringComparer.Ordinal)
                    .Select(point => point.Point)
                    .ToList(),
            })
            .ToList();

        return new ExportDocument
        {
            FishingSpots = fishingSpots,
        };
    }
}

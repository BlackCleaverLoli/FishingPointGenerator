using FishingPointGenerator.Core.Geometry;
using FishingPointGenerator.Core.Models;

namespace FishingPointGenerator.Core;

public sealed class SurveyBlockBuilder
{
    private readonly SurveyBlockOptions options;

    public SurveyBlockBuilder(SurveyBlockOptions? options = null)
    {
        this.options = options ?? new SurveyBlockOptions();
    }

    public IReadOnlyList<SurveyBlock> BuildBlocks(IEnumerable<ApproachCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var blocks = new List<SurveyBlock>();
        foreach (var territoryGroup in candidates.GroupBy(candidate => candidate.TerritoryId).OrderBy(group => group.Key))
            blocks.AddRange(BuildTerritoryBlocks(territoryGroup.Key, territoryGroup.ToList()));

        return blocks;
    }

    private IReadOnlyList<SurveyBlock> BuildTerritoryBlocks(uint territoryId, IReadOnlyList<ApproachCandidate> candidates)
    {
        if (candidates.Count == 0)
            return [];

        var regions = BuildRegionComponents(candidates)
            .OrderBy(component => component.Center.X)
            .ThenBy(component => component.Center.Z)
            .ThenBy(component => component.FirstCandidateId, StringComparer.Ordinal)
            .ToList();

        var blocks = new List<SurveyBlock>();
        var generatedRegionNumber = 1;
        foreach (var region in regions)
        {
            var regionId = string.IsNullOrWhiteSpace(region.RegionId)
                ? $"t{territoryId}_region_{generatedRegionNumber++:D4}"
                : region.RegionId;

            blocks.AddRange(BuildRegionBlocks(territoryId, regionId, candidates, region.Indexes));
        }

        return blocks;
    }

    private IReadOnlyList<Component> BuildRegionComponents(IReadOnlyList<ApproachCandidate> candidates)
    {
        var set = new DisjointSet(candidates.Count);
        for (var left = 0; left < candidates.Count; left++)
        {
            for (var right = left + 1; right < candidates.Count; right++)
            {
                if (ShouldLinkRegion(candidates[left], candidates[right]))
                    set.Union(left, right);
            }
        }

        return BuildComponents(candidates, set, FindExistingRegionId);
    }

    private IReadOnlyList<SurveyBlock> BuildRegionBlocks(
        uint territoryId,
        string regionId,
        IReadOnlyList<ApproachCandidate> candidates,
        IReadOnlyList<int> indexes)
    {
        if (indexes.Count == 0)
            return [];

        var set = new DisjointSet(indexes.Count);
        for (var left = 0; left < indexes.Count; left++)
        {
            for (var right = left + 1; right < indexes.Count; right++)
            {
                if (ShouldLinkBlock(candidates[indexes[left]], candidates[indexes[right]]))
                    set.Union(left, right);
            }
        }

        var components = BuildComponents(indexes.Select(index => candidates[index]).ToList(), set, (_, _) => string.Empty)
            .OrderBy(component => component.Center.X)
            .ThenBy(component => component.Center.Z)
            .ThenBy(component => component.FirstCandidateId, StringComparer.Ordinal)
            .ToList();

        var blocks = new List<SurveyBlock>(components.Count);
        for (var blockIndex = 0; blockIndex < components.Count; blockIndex++)
        {
            var blockId = $"{regionId}_block_{blockIndex + 1:D4}";
            var blockCandidates = components[blockIndex]
                .Indexes
                .Select(index => candidates[indexes[index]] with
                {
                    RegionId = regionId,
                    BlockId = blockId,
                })
                .OrderBy(candidate => candidate.CandidateId, StringComparer.Ordinal)
                .ToList();

            blocks.Add(new SurveyBlock
            {
                TerritoryId = territoryId,
                RegionId = regionId,
                BlockId = blockId,
                Candidates = blockCandidates,
            });
        }

        return blocks;
    }

    private bool ShouldLinkRegion(ApproachCandidate left, ApproachCandidate right)
    {
        var leftHasRegion = !string.IsNullOrWhiteSpace(left.RegionId);
        var rightHasRegion = !string.IsNullOrWhiteSpace(right.RegionId);
        if (leftHasRegion || rightHasRegion)
            return leftHasRegion && rightHasRegion && string.Equals(left.RegionId, right.RegionId, StringComparison.Ordinal);

        return left.Position.HorizontalDistanceTo(right.Position) <= options.RegionLinkDistanceMeters;
    }

    private bool ShouldLinkBlock(ApproachCandidate left, ApproachCandidate right)
    {
        return MathF.Abs(left.Position.Y - right.Position.Y) <= options.BlockHeightToleranceMeters
            && left.Position.HorizontalDistanceTo(right.Position) <= options.BlockLinkDistanceMeters
            && AngleMath.AngularDistance(left.Rotation, right.Rotation) <= options.BlockRotationToleranceRadians;
    }

    private static IReadOnlyList<Component> BuildComponents(
        IReadOnlyList<ApproachCandidate> candidates,
        DisjointSet set,
        Func<IReadOnlyList<int>, IReadOnlyList<ApproachCandidate>, string> idSelector)
    {
        return Enumerable
            .Range(0, candidates.Count)
            .GroupBy(set.Find)
            .Select(group =>
            {
                var indexes = group.OrderBy(index => index).ToList();
                return new Component(
                    indexes,
                    idSelector(indexes, candidates),
                    CalculateCenter(candidates, indexes),
                    indexes.Select(index => candidates[index].CandidateId).OrderBy(id => id, StringComparer.Ordinal).FirstOrDefault() ?? string.Empty);
            })
            .ToList();
    }

    private static string FindExistingRegionId(IReadOnlyList<int> indexes, IReadOnlyList<ApproachCandidate> candidates)
    {
        return indexes
            .Select(index => candidates[index].RegionId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .FirstOrDefault() ?? string.Empty;
    }

    private static Point3 CalculateCenter(IReadOnlyList<ApproachCandidate> candidates, IReadOnlyList<int> indexes)
    {
        if (indexes.Count == 0)
            return default;

        var x = 0f;
        var y = 0f;
        var z = 0f;
        foreach (var index in indexes)
        {
            x += candidates[index].Position.X;
            y += candidates[index].Position.Y;
            z += candidates[index].Position.Z;
        }

        return new Point3(x / indexes.Count, y / indexes.Count, z / indexes.Count);
    }

    private sealed record Component(
        IReadOnlyList<int> Indexes,
        string RegionId,
        Point3 Center,
        string FirstCandidateId);
}

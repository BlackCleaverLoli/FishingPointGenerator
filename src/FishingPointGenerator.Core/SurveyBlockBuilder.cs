using FishingPointGenerator.Core.Geometry;
using FishingPointGenerator.Core.Models;

namespace FishingPointGenerator.Core;

public sealed class SurveyBlockBuilder
{
    private const float StationNodePositionQuantum = 0.05f;

    private readonly SurveyBlockOptions options;

    public SurveyBlockBuilder(SurveyBlockOptions? options = null)
    {
        this.options = options ?? new SurveyBlockOptions();
    }

    public IReadOnlyList<SurveyBlock> BuildBlocks(
        IEnumerable<ApproachCandidate> candidates,
        IProgress<SurveyBlockBuildProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var blocks = new List<SurveyBlock>();
        var territoryGroups = candidates
            .GroupBy(candidate => candidate.TerritoryId)
            .OrderBy(group => group.Key)
            .Select(group => new
            {
                TerritoryId = group.Key,
                Candidates = group.ToList(),
            })
            .ToList();

        for (var territoryIndex = 0; territoryIndex < territoryGroups.Count; territoryIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var territoryGroup = territoryGroups[territoryIndex];
            progress?.Report(new SurveyBlockBuildProgress(
                "territory",
                territoryIndex,
                Math.Max(1, territoryGroups.Count),
                $"正在处理区域 {territoryGroup.TerritoryId}（{territoryGroup.Candidates.Count} 个候选）。"));
            blocks.AddRange(BuildTerritoryBlocks(territoryGroup.TerritoryId, territoryGroup.Candidates, progress, cancellationToken));
        }

        progress?.Report(new SurveyBlockBuildProgress(
            "complete",
            Math.Max(1, territoryGroups.Count),
            Math.Max(1, territoryGroups.Count),
            $"已构建 {blocks.Count} 个块。"));

        return blocks;
    }

    private IReadOnlyList<SurveyBlock> BuildTerritoryBlocks(
        uint territoryId,
        IReadOnlyList<ApproachCandidate> candidates,
        IProgress<SurveyBlockBuildProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
            return [];

        var stations = BuildStationNodes(candidates);
        progress?.Report(new SurveyBlockBuildProgress(
            "stations",
            stations.Count,
            Math.Max(1, stations.Count),
            $"区域 {territoryId} 已将 {candidates.Count} 个候选压缩为 {stations.Count} 个站位节点。"));

        var regions = BuildRegionComponents(territoryId, stations, progress, cancellationToken)
            .OrderBy(component => component.Center.X)
            .ThenBy(component => component.Center.Z)
            .ThenBy(component => component.FirstCandidateId, StringComparer.Ordinal)
            .ToList();

        var blocks = new List<SurveyBlock>();
        var generatedRegionNumber = 1;
        for (var regionIndex = 0; regionIndex < regions.Count; regionIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var region = regions[regionIndex];
            var regionId = string.IsNullOrWhiteSpace(region.RegionId)
                ? $"t{territoryId}_region_{generatedRegionNumber++:D4}"
                : region.RegionId;

            progress?.Report(new SurveyBlockBuildProgress(
                "region-blocks",
                regionIndex,
                Math.Max(1, regions.Count),
                $"区域 {territoryId} 正在构建 {regions.Count} 个候选区域中的块。"));
            blocks.AddRange(BuildRegionBlocks(territoryId, regionId, candidates, stations, region.Indexes, progress, cancellationToken));
        }

        return blocks;
    }

    private IReadOnlyList<Component> BuildRegionComponents(
        uint territoryId,
        IReadOnlyList<StationNode> stations,
        IProgress<SurveyBlockBuildProgress>? progress,
        CancellationToken cancellationToken)
    {
        var set = new DisjointSet(stations.Count);
        UnionExistingRegionStations(stations, set);

        var stationIndexes = Enumerable.Range(0, stations.Count).ToList();
        var spatialIndex = new StationSpatialIndex(stations, stationIndexes, options.RegionLinkDistanceMeters);
        for (var left = 0; left < stations.Count; left++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReportCandidateLoopProgress(
                progress,
                "regions",
                left,
                stations.Count,
                $"区域 {territoryId} 正在按距离合并站位区域：{left}/{stations.Count}。");

            foreach (var right in spatialIndex.FindNear(stations[left].Position, options.RegionLinkDistanceMeters))
            {
                if (right <= left || !ShouldLinkRegion(stations[left], stations[right]))
                    continue;

                set.Union(left, right);
            }
        }

        ReportCandidateLoopProgress(
            progress,
            "regions",
            stations.Count,
            stations.Count,
            $"区域 {territoryId} 候选区域合并完成。");
        return BuildStationComponents(stations, stationIndexes, set, FindExistingRegionId);
    }

    private IReadOnlyList<SurveyBlock> BuildRegionBlocks(
        uint territoryId,
        string regionId,
        IReadOnlyList<ApproachCandidate> candidates,
        IReadOnlyList<StationNode> stations,
        IReadOnlyList<int> indexes,
        IProgress<SurveyBlockBuildProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (indexes.Count == 0)
            return [];

        var localIndexes = indexes
            .Select((stationIndex, localIndex) => new { StationIndex = stationIndex, LocalIndex = localIndex })
            .ToDictionary(item => item.StationIndex, item => item.LocalIndex);
        var spatialIndex = new StationSpatialIndex(stations, indexes, options.BlockLinkDistanceMeters);
        var set = new DisjointSet(indexes.Count);
        for (var left = 0; left < indexes.Count; left++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReportCandidateLoopProgress(
                progress,
                "blocks",
                left,
                indexes.Count,
                $"区域 {territoryId} 正在构建 {regionId} 的站位块：{left}/{indexes.Count}。");

            var leftStationIndex = indexes[left];
            foreach (var rightStationIndex in spatialIndex.FindNear(stations[leftStationIndex].Position, options.BlockLinkDistanceMeters))
            {
                if (!localIndexes.TryGetValue(rightStationIndex, out var right)
                    || right <= left
                    || !ShouldLinkBlock(stations[leftStationIndex], stations[rightStationIndex]))
                    continue;

                set.Union(left, right);
            }
        }

        ReportCandidateLoopProgress(
            progress,
            "blocks",
            indexes.Count,
            indexes.Count,
            $"区域 {territoryId} 的 {regionId} 站位块构建完成。");
        var components = BuildStationComponents(stations, indexes, set, (_, _) => string.Empty)
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
                .SelectMany(stationIndex => stations[stationIndex].CandidateIndexes)
                .Select(candidateIndex => candidates[candidateIndex] with
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

    private static IReadOnlyList<StationNode> BuildStationNodes(IReadOnlyList<ApproachCandidate> candidates)
    {
        var builders = new List<StationNodeBuilder>();
        var byKey = new Dictionary<StationNodeKey, StationNodeBuilder>();
        for (var candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
        {
            var candidate = candidates[candidateIndex];
            var key = StationNodeKey.From(candidate);
            if (!byKey.TryGetValue(key, out var builder))
            {
                builder = new StationNodeBuilder(candidate.RegionId);
                byKey[key] = builder;
                builders.Add(builder);
            }

            builder.Add(candidateIndex, candidate);
        }

        return builders
            .Select((builder, index) => builder.ToNode(index, candidates))
            .ToList();
    }

    private static void UnionExistingRegionStations(IReadOnlyList<StationNode> stations, DisjointSet set)
    {
        foreach (var group in stations
            .Where(station => !string.IsNullOrWhiteSpace(station.RegionId))
            .GroupBy(station => station.RegionId, StringComparer.Ordinal))
        {
            var firstIndex = -1;
            foreach (var station in group)
            {
                if (firstIndex < 0)
                {
                    firstIndex = station.Index;
                    continue;
                }

                set.Union(firstIndex, station.Index);
            }
        }
    }

    private bool ShouldLinkRegion(StationNode left, StationNode right)
    {
        var leftHasRegion = !string.IsNullOrWhiteSpace(left.RegionId);
        var rightHasRegion = !string.IsNullOrWhiteSpace(right.RegionId);
        if (leftHasRegion || rightHasRegion)
            return leftHasRegion && rightHasRegion && string.Equals(left.RegionId, right.RegionId, StringComparison.Ordinal);

        return HorizontalDistanceSquared(left.Position, right.Position) <= options.RegionLinkDistanceMeters * options.RegionLinkDistanceMeters;
    }

    private bool ShouldLinkBlock(StationNode left, StationNode right)
    {
        return MathF.Abs(left.Position.Y - right.Position.Y) <= options.BlockHeightToleranceMeters
            && HorizontalDistanceSquared(left.Position, right.Position) <= options.BlockLinkDistanceMeters * options.BlockLinkDistanceMeters;
    }

    private static float HorizontalDistanceSquared(Point3 left, Point3 right)
    {
        var dx = left.X - right.X;
        var dz = left.Z - right.Z;
        return (dx * dx) + (dz * dz);
    }

    private static void ReportCandidateLoopProgress(
        IProgress<SurveyBlockBuildProgress>? progress,
        string stage,
        int current,
        int total,
        string message)
    {
        if (progress is null || current % 32 != 0 && current < total)
            return;

        progress.Report(new SurveyBlockBuildProgress(stage, current, Math.Max(1, total), message));
    }

    private static IReadOnlyList<Component> BuildStationComponents(
        IReadOnlyList<StationNode> stations,
        IReadOnlyList<int> stationIndexes,
        DisjointSet set,
        Func<IReadOnlyList<int>, IReadOnlyList<StationNode>, string> idSelector)
    {
        return Enumerable
            .Range(0, stationIndexes.Count)
            .GroupBy(set.Find)
            .Select(group =>
            {
                var indexes = group
                    .Select(localIndex => stationIndexes[localIndex])
                    .OrderBy(index => index)
                    .ToList();
                return new Component(
                    indexes,
                    idSelector(indexes, stations),
                    CalculateCenter(stations, indexes),
                    indexes.Select(index => stations[index].FirstCandidateId).OrderBy(id => id, StringComparer.Ordinal).FirstOrDefault() ?? string.Empty);
            })
            .ToList();
    }

    private static string FindExistingRegionId(IReadOnlyList<int> indexes, IReadOnlyList<StationNode> stations)
    {
        return indexes
            .Select(index => stations[index].RegionId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .FirstOrDefault() ?? string.Empty;
    }

    private static Point3 CalculateCenter(IReadOnlyList<StationNode> stations, IReadOnlyList<int> indexes)
    {
        if (indexes.Count == 0)
            return default;

        var x = 0f;
        var y = 0f;
        var z = 0f;
        var totalWeight = 0;
        foreach (var index in indexes)
        {
            var station = stations[index];
            var weight = station.CandidateIndexes.Count;
            x += station.Position.X * weight;
            y += station.Position.Y * weight;
            z += station.Position.Z * weight;
            totalWeight += weight;
        }

        return totalWeight == 0
            ? default
            : new Point3(x / totalWeight, y / totalWeight, z / totalWeight);
    }

    private sealed class StationNodeBuilder(string regionId)
    {
        private float x;
        private float y;
        private float z;

        public List<int> CandidateIndexes { get; } = [];

        public void Add(int candidateIndex, ApproachCandidate candidate)
        {
            CandidateIndexes.Add(candidateIndex);
            x += candidate.Position.X;
            y += candidate.Position.Y;
            z += candidate.Position.Z;
        }

        public StationNode ToNode(int index, IReadOnlyList<ApproachCandidate> candidates)
        {
            var count = Math.Max(1, CandidateIndexes.Count);
            var firstCandidateId = CandidateIndexes
                .Select(candidateIndex => candidates[candidateIndex].CandidateId)
                .OrderBy(id => id, StringComparer.Ordinal)
                .FirstOrDefault() ?? string.Empty;
            return new StationNode(
                index,
                new Point3(x / count, y / count, z / count),
                regionId,
                CandidateIndexes.ToList(),
                firstCandidateId);
        }
    }

    private sealed class StationSpatialIndex
    {
        private readonly IReadOnlyList<StationNode> stations;
        private readonly float cellSize;
        private readonly Dictionary<StationGridCell, List<int>> cells = [];

        public StationSpatialIndex(IReadOnlyList<StationNode> stations, IEnumerable<int> stationIndexes, float cellSize)
        {
            this.stations = stations;
            this.cellSize = Math.Max(StationNodePositionQuantum, cellSize);
            foreach (var stationIndex in stationIndexes)
                Add(stationIndex);
        }

        public IEnumerable<int> FindNear(Point3 position, float radius)
        {
            var center = StationGridCell.From(position, cellSize);
            var range = (int)MathF.Ceiling(radius / cellSize);
            for (var x = center.X - range; x <= center.X + range; x++)
            {
                for (var z = center.Z - range; z <= center.Z + range; z++)
                {
                    if (!cells.TryGetValue(new StationGridCell(x, z), out var stationIndexes))
                        continue;

                    foreach (var stationIndex in stationIndexes)
                        yield return stationIndex;
                }
            }
        }

        private void Add(int stationIndex)
        {
            var cell = StationGridCell.From(stations[stationIndex].Position, cellSize);
            if (!cells.TryGetValue(cell, out var stationIndexes))
            {
                stationIndexes = [];
                cells[cell] = stationIndexes;
            }

            stationIndexes.Add(stationIndex);
        }
    }

    private sealed record StationNode(
        int Index,
        Point3 Position,
        string RegionId,
        IReadOnlyList<int> CandidateIndexes,
        string FirstCandidateId);

    private readonly record struct StationNodeKey(string RegionId, int X, int Y, int Z)
    {
        public static StationNodeKey From(ApproachCandidate candidate) => new(
            candidate.RegionId,
            Quantize(candidate.Position.X),
            Quantize(candidate.Position.Y),
            Quantize(candidate.Position.Z));

        private static int Quantize(float value) =>
            (int)MathF.Round(value / StationNodePositionQuantum, MidpointRounding.AwayFromZero);
    }

    private readonly record struct StationGridCell(int X, int Z)
    {
        public static StationGridCell From(Point3 point, float cellSize) => new(
            (int)MathF.Floor(point.X / cellSize),
            (int)MathF.Floor(point.Z / cellSize));
    }

    private sealed record Component(
        IReadOnlyList<int> Indexes,
        string RegionId,
        Point3 Center,
        string FirstCandidateId);
}

public sealed record SurveyBlockBuildProgress(
    string Stage,
    int Current,
    int Total,
    string Message);

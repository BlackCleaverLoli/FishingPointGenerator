using System.Numerics;
using Dalamud.Plugin.Services;
using FishingPointGenerator.Core.Geometry;
using FishingPointGenerator.Core.Models;
using OmenTools;

namespace FishingPointGenerator.Plugin.Services.Scanning;

internal sealed class VnavmeshSceneScanner : ICurrentTerritoryScanner
{
    private const float EdgeVertexQuantum = 0.25f;
    private const float MinimumBoundaryEdgeLength = 0.75f;
    private const float MinimumWalkableSurfaceArea = 0.5f;
    private const float MaximumCandidateDistanceFromFishableSurface = 2f;
    private const float FishableCoverageSatisfiedDistance = 2f;
    private const float FishableCoverageTargetKeyCellSize = 0.5f;
    private const float OpenWalkableClearanceMeters = 5f;
    private const float WalkableFishableMinimumVerticalDelta = 0.05f;
    private const float SurfaceIndexCellSize = 8f;
    private const float CandidateDedupeCellSize = 1.25f;
    private const float CandidateRotationDedupeRadians = 0.15f;
    private const float FishableCenterProbeStep = 0.5f;
    private const float FishableCenterProbeMaxDistance = 96f;
    private const int FishableCenterBoundaryRefineSteps = 6;
    private const float FacingProbeStartDistance = 0.5f;
    private const float FacingProbeStep = 0.5f;
    private const float FacingProbeMaxDistance = 2f;
    private const float FacingProbeLateralRadius = 0.75f;
    private const float FacingDirectionDedupeRadians = 0.05f;
    private const float DebugCellSize = 10f;
    private const int MaxCandidates = 99999;
    private const int MaxDebugCells = 18;
    private const int MaxDebugCandidates = 16;
    private const int MaxDebugWaterMaterials = 8;
    private const int MaxDebugWaterSamples = 8;
    private static readonly FishableCoverageRound[] FishableCoverageRounds =
    [
        new("粗扫", 4f, 12),
        // new("补扫", 2f, 12),
    ];
    private static readonly float[] CandidateSearchRadii =
    [
        0f,
        0.5f,
        1f,
        1.5f,
        MaximumCandidateDistanceFromFishableSurface,
    ];
    private static readonly float[] FishableCenterEntryOffsets =
    [
        0.05f,
        0.1f,
        0.25f,
        0.5f,
        1f,
        1.5f,
        2f,
    ];
    private static readonly float[] CandidateFacingAngleOffsets =
    [
        0f,
        MathF.PI / 12f,
        -MathF.PI / 12f,
        MathF.PI / 6f,
        -MathF.PI / 6f,
        MathF.PI / 4f,
        -MathF.PI / 4f,
        MathF.PI / 3f,
        -MathF.PI / 3f,
        MathF.PI / 2f,
        -MathF.PI / 2f,
        MathF.PI * 2f / 3f,
        -MathF.PI * 2f / 3f,
        MathF.PI * 5f / 6f,
        -MathF.PI * 5f / 6f,
        MathF.PI,
    ];

    private readonly IPluginLog pluginLog;

    public VnavmeshSceneScanner(IPluginLog pluginLog)
    {
        this.pluginLog = pluginLog;
    }

    public string Name => "当前布局可钓/可走边界扫描器";
    public bool IsPlaceholder => false;

    public NearbyScanDebugResult DebugScanNearby(float radiusMeters)
    {
        var service = DService.Instance();
        var player = service.ObjectTable.LocalPlayer;
        var currentTerritoryId = service.ClientState.TerritoryType;
        if (currentTerritoryId == 0 || player is null)
        {
            return new NearbyScanDebugResult
            {
                Message = "附近扫描失败：没有可用区域或本地玩家。",
            };
        }

        var radius = Math.Clamp(radiusMeters, 5f, 200f);
        var playerPosition = player.Position;
        var scene = new ActiveLayoutScene();
        scene.FillFromActiveLayout();
        var territoryId = scene.TerritoryId != 0 ? scene.TerritoryId : currentTerritoryId;
        var extractor = new CollisionSceneExtractor(scene);
        var allTriangles = extractor.ExtractTriangles();
        var filterRadius = radius + MaximumCandidateDistanceFromFishableSurface;
        var nearbyTriangles = allTriangles
            .Where(triangle => HorizontalDistanceToTriangle(playerPosition, triangle) <= filterRadius)
            .ToList();
        var fishableTriangles = nearbyTriangles
            .Where(triangle => triangle.IsFishable && triangle.Area > 0.05f)
            .ToList();
        var walkableTriangles = nearbyTriangles
            .Where(triangle => triangle.IsWalkable && triangle.Area > 0.05f)
            .ToList();

        var buckets = BuildEdgeBuckets(fishableTriangles, walkableTriangles);
        var sharedBuckets = buckets.Values.Count(bucket => bucket.FishableEdges.Count > 0 && bucket.WalkableEdges.Count > 0);
        var fishableEdges = BuildEdges(fishableTriangles);
        var walkableEdges = BuildEdges(walkableTriangles);
        var fishableOuterEdges = BuildOuterEdges(fishableTriangles);
        var walkableOuterEdges = BuildOuterEdges(walkableTriangles);
        var nearbySurfaceMatches = CountNearbySurfaceMatches(
            fishableOuterEdges.Count > 0 ? fishableOuterEdges : fishableEdges,
            walkableTriangles);
        var candidates = GenerateCandidates(territoryId, fishableTriangles, walkableTriangles)
            .Where(candidate => HorizontalDistance(candidate.Position.ToVector3(), playerPosition) <= radius)
            .ToList();
        var waterSummary = CreateWaterSurfaceSummary(playerPosition, fishableTriangles);

        pluginLog.Information(
            "FPG nearby scan: territory={TerritoryId} player=({PlayerX:F2},{PlayerY:F2},{PlayerZ:F2}) radius={Radius:F1} allTriangles={AllTriangles} nearbyTriangles={NearbyTriangles} fishableTriangles={FishableTriangles} walkableTriangles={WalkableTriangles} fishableEdges={FishableEdges} walkableEdges={WalkableEdges} fishableOuterEdges={FishableOuterEdges} walkableOuterEdges={WalkableOuterEdges} sharedEdgeBuckets={SharedEdgeBuckets} nearbySurfaceMatches={NearbySurfaceMatches} candidates={Candidates} water={WaterSummary}",
            territoryId,
            playerPosition.X,
            playerPosition.Y,
            playerPosition.Z,
            radius,
            allTriangles.Count,
            nearbyTriangles.Count,
            fishableTriangles.Count,
            walkableTriangles.Count,
            fishableEdges.Count,
            walkableEdges.Count,
            fishableOuterEdges.Count,
            walkableOuterEdges.Count,
            sharedBuckets,
            nearbySurfaceMatches,
            candidates.Count,
            FormatWaterSummary(waterSummary));
        LogDebugWaterSurfaces(playerPosition, fishableTriangles, waterSummary);
        LogDebugCells(playerPosition, fishableTriangles, walkableTriangles, candidates);
        LogDebugCandidates(playerPosition, candidates);

        return new NearbyScanDebugResult
        {
            Message = $"附近扫描 {radius:F1}m：fishable={fishableTriangles.Count} walkable={walkableTriangles.Count} water={FormatWaterSummary(waterSummary)} sharedBuckets={sharedBuckets} fishableOuterEdges={fishableOuterEdges.Count} nearbySurfaces={nearbySurfaceMatches} candidates={candidates.Count}。详情见 Dalamud log；Fishable/Walkable 面已送入 overlay 调试层。",
            TerritoryId = territoryId,
            PlayerPosition = playerPosition,
            RadiusMeters = radius,
            FishableTriangles = fishableTriangles
                .Select(ToDebugOverlayTriangle)
                .ToList(),
            WalkableTriangles = walkableTriangles
                .Select(ToDebugOverlayTriangle)
                .ToList(),
            Candidates = candidates,
        };
    }

    public TerritoryScanCapture CaptureCurrentTerritory()
    {
        var service = DService.Instance();
        var currentTerritoryId = service.ClientState.TerritoryType;
        var scene = new ActiveLayoutScene();
        if (currentTerritoryId != 0)
            scene.FillFromActiveLayout();

        var territoryName = scene.GetTerritoryName(currentTerritoryId);
        return new TerritoryScanCapture(currentTerritoryId, territoryName, scene);
    }

    public TerritorySurveyDocument ScanCapturedTerritory(
        TerritoryScanCapture capture,
        IProgress<TerritoryScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var territoryId = capture.TerritoryId;
        if (territoryId == 0)
            return Empty(territoryId, string.Empty);

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new TerritoryScanProgress("读取碰撞", 0, 4, "正在解析当前区域碰撞几何。"));
        var extractor = new CollisionSceneExtractor(capture.Scene);
        var triangles = extractor.ExtractTriangles();
        cancellationToken.ThrowIfCancellationRequested();

        progress?.Report(new TerritoryScanProgress("筛选碰撞", 1, 4, $"已读取 {triangles.Count} 个碰撞三角面。"));
        var fishableTriangles = triangles
            .Where(triangle => triangle.IsFishable && triangle.Area > 0.05f)
            .ToList();
        var walkableTriangles = triangles
            .Where(triangle => triangle.IsWalkable && triangle.Area > 0.05f)
            .ToList();
        cancellationToken.ThrowIfCancellationRequested();

        if (fishableTriangles.Count == 0 || walkableTriangles.Count == 0)
        {
            pluginLog.Warning(
                "FPG 场景扫描未找到可用几何体。Territory={TerritoryId}, fishable={FishableCount}, walkable={WalkableCount}",
                territoryId,
                fishableTriangles.Count,
                walkableTriangles.Count);
            return Empty(territoryId, capture.TerritoryName);
        }

        progress?.Report(new TerritoryScanProgress("生成候选", 2, 4, $"fishable={fishableTriangles.Count} walkable={walkableTriangles.Count}，正在生成候选。"));
        var candidates = GenerateCandidates(territoryId, fishableTriangles, walkableTriangles, progress, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        pluginLog.Information(
            "FPG 场景扫描完成。Territory={TerritoryId}, fishableTriangles={FishableCount}, walkableTriangles={WalkableCount}, candidates={CandidateCount}",
            territoryId,
            fishableTriangles.Count,
            walkableTriangles.Count,
            candidates.Count);

        progress?.Report(new TerritoryScanProgress("生成候选", 4, 4, $"已生成 {candidates.Count} 个候选。"));
        return new TerritorySurveyDocument
        {
            TerritoryId = territoryId,
            TerritoryName = capture.TerritoryName,
            Candidates = candidates,
        };
    }

    public TerritorySurveyDocument ScanCurrentTerritory()
    {
        var capture = CaptureCurrentTerritory();
        return ScanCapturedTerritory(capture, null, CancellationToken.None);
    }

    private static TerritorySurveyDocument Empty(uint territoryId, string territoryName) => new()
    {
        TerritoryId = territoryId,
        TerritoryName = territoryName,
    };

    private static List<ApproachCandidate> GenerateCandidates(
        uint territoryId,
        IReadOnlyList<ExtractedSceneTriangle> fishableTriangles,
        IReadOnlyList<ExtractedSceneTriangle> walkableTriangles,
        IProgress<TerritoryScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var candidateKeys = new HashSet<CandidateKey>();
        var candidates = new List<CandidateScratch>();

        AddWalkableSurfaceCandidates(territoryId, fishableTriangles, walkableTriangles, candidateKeys, candidates, progress, cancellationToken);

        return candidates
            .OrderBy(candidate => candidate.Position.X)
            .ThenBy(candidate => candidate.Position.Z)
            .ThenBy(candidate => candidate.Rotation)
            .Take(MaxCandidates)
            .OrderBy(candidate => candidate.Position.X)
            .ThenBy(candidate => candidate.Position.Z)
            .ThenBy(candidate => candidate.Rotation)
            .Select((candidate, index) => new ApproachCandidate
            {
                CandidateId = $"scene_{territoryId}_{index + 1:D5}",
                TerritoryId = territoryId,
                SurfaceGroupId = candidate.SurfaceGroupId,
                Position = Point3.From(candidate.Position),
                Rotation = candidate.Rotation,
                Status = CandidateStatus.Unlabeled,
            })
            .ToList();
    }

    private static Dictionary<EdgeKey, BoundaryEdgeBucket> BuildEdgeBuckets(
        IReadOnlyList<ExtractedSceneTriangle> fishableTriangles,
        IReadOnlyList<ExtractedSceneTriangle> walkableTriangles)
    {
        var buckets = new Dictionary<EdgeKey, BoundaryEdgeBucket>();
        foreach (var triangle in fishableTriangles)
            AddTriangleEdges(buckets, triangle, BoundarySurfaceKind.Fishable);
        foreach (var triangle in walkableTriangles)
            AddTriangleEdges(buckets, triangle, BoundarySurfaceKind.Walkable);

        return buckets;
    }

    private static void AddTriangleEdges(
        Dictionary<EdgeKey, BoundaryEdgeBucket> buckets,
        ExtractedSceneTriangle triangle,
        BoundarySurfaceKind kind)
    {
        AddEdge(buckets, new BoundaryEdge(triangle.A, triangle.B, triangle), kind);
        AddEdge(buckets, new BoundaryEdge(triangle.B, triangle.C, triangle), kind);
        AddEdge(buckets, new BoundaryEdge(triangle.C, triangle.A, triangle), kind);
    }

    private static void AddEdge(
        Dictionary<EdgeKey, BoundaryEdgeBucket> buckets,
        BoundaryEdge edge,
        BoundarySurfaceKind kind)
    {
        if (edge.HorizontalLength < MinimumBoundaryEdgeLength)
            return;

        var key = EdgeKey.From(edge.Start, edge.End);
        if (!buckets.TryGetValue(key, out var bucket))
        {
            bucket = new BoundaryEdgeBucket();
            buckets[key] = bucket;
        }

        if (kind == BoundarySurfaceKind.Fishable)
            bucket.FishableEdges.Add(edge);
        else
            bucket.WalkableEdges.Add(edge);
    }

    private static void AddWalkableSurfaceCandidates(
        uint territoryId,
        IReadOnlyList<ExtractedSceneTriangle> fishableTriangles,
        IReadOnlyList<ExtractedSceneTriangle> walkableTriangles,
        HashSet<CandidateKey> candidateKeys,
        List<CandidateScratch> candidates,
        IProgress<TerritoryScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var walkableSurfaces = walkableTriangles
            .Where(triangle => triangle.Area >= MinimumWalkableSurfaceArea)
            .ToList();
        if (walkableSurfaces.Count == 0)
            return;

        var fishableBlocks = BuildFishableSurfaceBlocks(territoryId, fishableTriangles);
        var walkableIndex = new TriangleIndex(walkableSurfaces, SurfaceIndexCellSize);
        var fishableIndex = new TriangleIndex(fishableTriangles, SurfaceIndexCellSize);
        AddCoverageDrivenCandidates(
            fishableBlocks,
            walkableIndex,
            fishableIndex,
            candidateKeys,
            candidates,
            progress,
            cancellationToken);
    }

    private static int CountNearbySurfaceMatches(
        IReadOnlyList<BoundaryEdge> fishableEdges,
        IReadOnlyList<ExtractedSceneTriangle> walkableTriangles)
    {
        var walkableSurfaces = walkableTriangles
            .Where(triangle => triangle.Area >= MinimumWalkableSurfaceArea)
            .ToList();
        if (fishableEdges.Count == 0 || walkableSurfaces.Count == 0)
            return 0;

        var walkableIndex = new TriangleIndex(walkableSurfaces, SurfaceIndexCellSize);
        var count = 0;
        foreach (var fishableEdge in fishableEdges)
        {
            if (walkableIndex.TryFindNearest(
                    fishableEdge.Midpoint,
                    MaximumCandidateDistanceFromFishableSurface,
                    out _,
                    out _,
                    out _))
                count++;
        }

        return count;
    }

    private static IReadOnlyList<BoundaryEdge> BuildEdges(IReadOnlyList<ExtractedSceneTriangle> triangles)
    {
        var edges = new List<BoundaryEdge>(triangles.Count * 3);
        foreach (var triangle in triangles)
        {
            AddEdge(edges, new BoundaryEdge(triangle.A, triangle.B, triangle));
            AddEdge(edges, new BoundaryEdge(triangle.B, triangle.C, triangle));
            AddEdge(edges, new BoundaryEdge(triangle.C, triangle.A, triangle));
        }

        return edges;
    }

    private static IReadOnlyList<BoundaryEdge> BuildOuterEdges(IReadOnlyList<ExtractedSceneTriangle> triangles)
    {
        var buckets = new Dictionary<EdgeKey, List<BoundaryEdge>>();
        foreach (var triangle in triangles)
        {
            AddOuterEdge(buckets, new BoundaryEdge(triangle.A, triangle.B, triangle));
            AddOuterEdge(buckets, new BoundaryEdge(triangle.B, triangle.C, triangle));
            AddOuterEdge(buckets, new BoundaryEdge(triangle.C, triangle.A, triangle));
        }

        return buckets
            .Where(bucket => bucket.Value.Count == 1)
            .Select(bucket => bucket.Value[0])
            .ToList();
    }

    private static IReadOnlyList<FishableSurfaceBlock> BuildFishableSurfaceBlocks(
        uint territoryId,
        IReadOnlyList<ExtractedSceneTriangle> triangles)
    {
        if (triangles.Count == 0)
            return [];

        var edgeBuckets = new Dictionary<EdgeKey, List<int>>();
        for (var index = 0; index < triangles.Count; index++)
        {
            var triangle = triangles[index];
            AddTriangleIndexEdge(edgeBuckets, triangle.A, triangle.B, index);
            AddTriangleIndexEdge(edgeBuckets, triangle.B, triangle.C, index);
            AddTriangleIndexEdge(edgeBuckets, triangle.C, triangle.A, index);
        }

        var adjacency = new List<int>[triangles.Count];
        for (var index = 0; index < adjacency.Length; index++)
            adjacency[index] = [];

        foreach (var connected in edgeBuckets.Values.Where(bucket => bucket.Count > 1))
        {
            for (var leftIndex = 0; leftIndex < connected.Count; leftIndex++)
            {
                for (var rightIndex = leftIndex + 1; rightIndex < connected.Count; rightIndex++)
                {
                    adjacency[connected[leftIndex]].Add(connected[rightIndex]);
                    adjacency[connected[rightIndex]].Add(connected[leftIndex]);
                }
            }
        }

        var visited = new bool[triangles.Count];
        var blocks = new List<FishableSurfaceBlock>();
        for (var start = 0; start < triangles.Count; start++)
        {
            if (visited[start])
                continue;

            var component = new List<ExtractedSceneTriangle>();
            var queue = new Queue<int>();
            visited[start] = true;
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                component.Add(triangles[current]);
                foreach (var next in adjacency[current])
                {
                    if (visited[next])
                        continue;

                    visited[next] = true;
                    queue.Enqueue(next);
                }
            }

            blocks.Add(CreateFishableSurfaceBlock(component));
        }

        return blocks
            .OrderBy(block => block.Center.X)
            .ThenBy(block => block.Center.Z)
            .ThenByDescending(block => block.Area)
            .Select((block, index) => block with
            {
                SurfaceGroupId = $"t{territoryId}_surface_{index + 1:D4}",
            })
            .ToList();
    }

    private static void AddTriangleIndexEdge(
        Dictionary<EdgeKey, List<int>> edgeBuckets,
        Vector3 start,
        Vector3 end,
        int triangleIndex)
    {
        var key = EdgeKey.From(start, end);
        if (!edgeBuckets.TryGetValue(key, out var list))
        {
            list = [];
            edgeBuckets[key] = list;
        }

        list.Add(triangleIndex);
    }

    private static FishableSurfaceBlock CreateFishableSurfaceBlock(IReadOnlyList<ExtractedSceneTriangle> triangles)
    {
        var weightedCenter = Vector3.Zero;
        var totalArea = 0f;
        foreach (var triangle in triangles)
        {
            weightedCenter += triangle.Centroid * triangle.Area;
            totalArea += triangle.Area;
        }

        var center = totalArea > 0.0001f
            ? weightedCenter / totalArea
            : triangles.Aggregate(Vector3.Zero, (current, triangle) => current + triangle.Centroid) / triangles.Count;
        return new FishableSurfaceBlock(
            string.Empty,
            triangles.ToList(),
            center,
            totalArea,
            new TriangleIndex(triangles, SurfaceIndexCellSize));
    }

    private static bool TryFindLocalFishableCenter(
        Vector3 candidatePosition,
        ExtractedSceneTriangle nearestFishableTriangle,
        Vector3 nearestFishablePoint,
        FishableSurfaceBlock block,
        out Vector3 center)
    {
        center = default;
        if (TryGetHorizontalDirection(nearestFishablePoint - candidatePosition, out var direction)
            && TryFindLocalFishableCenterAlongDirection(nearestFishablePoint, direction, block, out center))
            return true;

        if (TryGetHorizontalDirection(nearestFishableTriangle.Centroid - nearestFishablePoint, out direction)
            && TryFindLocalFishableCenterAlongDirection(nearestFishablePoint, direction, block, out center))
            return true;

        if (TryGetHorizontalDirection(block.Center - nearestFishablePoint, out direction)
            && TryFindLocalFishableCenterAlongDirection(nearestFishablePoint, direction, block, out center))
            return true;

        return false;
    }

    private static bool TryFindLocalFishableCenterAlongDirection(
        Vector3 entryPoint,
        Vector2 direction,
        FishableSurfaceBlock block,
        out Vector3 center)
    {
        center = default;
        if (!TryFindFishableInteriorOffset(entryPoint, direction, block, out var firstInsideOffset))
            return false;

        var entryOffset = RefineFishableEntry(entryPoint, direction, block, firstInsideOffset);
        var lastInsideOffset = firstInsideOffset;
        for (var offset = firstInsideOffset + FishableCenterProbeStep;
             offset <= FishableCenterProbeMaxDistance;
             offset += FishableCenterProbeStep)
        {
            if (block.Index.ContainsPoint(CreateProbePoint(entryPoint, direction, offset)))
            {
                lastInsideOffset = offset;
                continue;
            }

            var exitOffset = RefineFishableExit(entryPoint, direction, block, lastInsideOffset, offset);
            center = CreateProbePoint(entryPoint, direction, (entryOffset + exitOffset) * 0.5f);
            return true;
        }

        center = CreateProbePoint(entryPoint, direction, (entryOffset + lastInsideOffset) * 0.5f);
        return true;
    }

    private static bool TryFindFishableInteriorOffset(
        Vector3 entryPoint,
        Vector2 direction,
        FishableSurfaceBlock block,
        out float insideOffset)
    {
        foreach (var offset in FishableCenterEntryOffsets)
        {
            if (!block.Index.ContainsPoint(CreateProbePoint(entryPoint, direction, offset)))
                continue;

            insideOffset = offset;
            return true;
        }

        insideOffset = 0f;
        return false;
    }

    private static float RefineFishableEntry(
        Vector3 entryPoint,
        Vector2 direction,
        FishableSurfaceBlock block,
        float insideOffset)
    {
        var outsideOffset = 0f;
        if (block.Index.ContainsPoint(CreateProbePoint(entryPoint, direction, outsideOffset)))
            return outsideOffset;

        for (var index = 0; index < FishableCenterBoundaryRefineSteps; index++)
        {
            var midpoint = (outsideOffset + insideOffset) * 0.5f;
            if (block.Index.ContainsPoint(CreateProbePoint(entryPoint, direction, midpoint)))
                insideOffset = midpoint;
            else
                outsideOffset = midpoint;
        }

        return (outsideOffset + insideOffset) * 0.5f;
    }

    private static float RefineFishableExit(
        Vector3 entryPoint,
        Vector2 direction,
        FishableSurfaceBlock block,
        float insideOffset,
        float outsideOffset)
    {
        for (var index = 0; index < FishableCenterBoundaryRefineSteps; index++)
        {
            var midpoint = (insideOffset + outsideOffset) * 0.5f;
            if (block.Index.ContainsPoint(CreateProbePoint(entryPoint, direction, midpoint)))
                insideOffset = midpoint;
            else
                outsideOffset = midpoint;
        }

        return (insideOffset + outsideOffset) * 0.5f;
    }

    private static Vector3 CreateProbePoint(Vector3 entryPoint, Vector2 direction, float offset)
    {
        return new Vector3(
            entryPoint.X + (direction.X * offset),
            entryPoint.Y,
            entryPoint.Z + (direction.Y * offset));
    }

    private static bool TryGetHorizontalDirection(Vector3 vector, out Vector2 direction)
    {
        direction = new Vector2(vector.X, vector.Z);
        var lengthSquared = direction.LengthSquared();
        if (lengthSquared <= 0.0001f)
            return false;

        direction /= MathF.Sqrt(lengthSquared);
        return true;
    }

    private static void AddOuterEdge(Dictionary<EdgeKey, List<BoundaryEdge>> buckets, BoundaryEdge edge)
    {
        if (edge.HorizontalLength < MinimumBoundaryEdgeLength)
            return;

        var key = EdgeKey.From(edge.Start, edge.End);
        if (!buckets.TryGetValue(key, out var list))
        {
            list = [];
            buckets[key] = list;
        }

        list.Add(edge);
    }

    private static void AddEdge(List<BoundaryEdge> edges, BoundaryEdge edge)
    {
        if (edge.HorizontalLength >= MinimumBoundaryEdgeLength)
            edges.Add(edge);
    }

    private static void AddCoverageDrivenCandidates(
        IReadOnlyList<FishableSurfaceBlock> fishableBlocks,
        TriangleIndex walkableIndex,
        TriangleIndex fishableIndex,
        HashSet<CandidateKey> candidateKeys,
        List<CandidateScratch> candidates,
        IProgress<TerritoryScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        for (var roundIndex = 0; roundIndex < FishableCoverageRounds.Length; roundIndex++)
        {
            var round = FishableCoverageRounds[roundIndex];
            var targets = EnumerateFishableCoverageTargets(fishableBlocks, round.TargetSpacing).ToList();
            var additionsBeforeRound = candidates.Count;
            var progressTotal = Math.Max(1, targets.Count);
            for (var targetIndex = 0; targetIndex < targets.Count; targetIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (candidates.Count >= MaxCandidates)
                    return;

                if (targetIndex % 64 == 0)
                    progress?.Report(new TerritoryScanProgress(
                        "生成候选",
                        targetIndex,
                        progressTotal,
                        $"{round.Name} fishable 覆盖：{targetIndex}/{targets.Count}，已生成 {candidates.Count} 个。"));

                var target = targets[targetIndex];
                if (IsFishableTargetCovered(target, candidates, walkableIndex, fishableIndex))
                    continue;

                if (!TryCreateCandidateForFishableTarget(
                        target,
                        round,
                        walkableIndex,
                        fishableIndex,
                        candidateKeys,
                        out var candidate))
                    continue;

                var key = CandidateKey.From(candidate.Position, candidate.Rotation);
                if (candidateKeys.Add(key))
                    candidates.Add(candidate);
            }

            if (roundIndex > 0 && candidates.Count == additionsBeforeRound)
                break;
        }
    }

    private static IEnumerable<FishableCoverageTarget> EnumerateFishableCoverageTargets(
        IReadOnlyList<FishableSurfaceBlock> fishableBlocks,
        float spacing)
    {
        var keys = new HashSet<FishableCoverageTargetKey>();
        foreach (var block in fishableBlocks)
        {
            foreach (var triangle in block.Triangles)
            {
                var yielded = false;
                foreach (var point in EnumerateTriangleSamples(triangle, spacing))
                {
                    var key = FishableCoverageTargetKey.From(block.SurfaceGroupId, point);
                    if (!keys.Add(key))
                        continue;

                    yielded = true;
                    yield return new FishableCoverageTarget(point, triangle, block);
                }

                if (yielded)
                    continue;

                var fallbackKey = FishableCoverageTargetKey.From(block.SurfaceGroupId, triangle.Centroid);
                if (keys.Add(fallbackKey))
                    yield return new FishableCoverageTarget(triangle.Centroid, triangle, block);
            }
        }
    }

    private static IEnumerable<Vector3> EnumerateTriangleSamples(ExtractedSceneTriangle triangle, float spacing)
    {
        var minX = MathF.Min(triangle.A.X, MathF.Min(triangle.B.X, triangle.C.X));
        var maxX = MathF.Max(triangle.A.X, MathF.Max(triangle.B.X, triangle.C.X));
        var minZ = MathF.Min(triangle.A.Z, MathF.Min(triangle.B.Z, triangle.C.Z));
        var maxZ = MathF.Max(triangle.A.Z, MathF.Max(triangle.B.Z, triangle.C.Z));
        var firstX = MathF.Floor(minX / spacing) * spacing;
        var firstZ = MathF.Floor(minZ / spacing) * spacing;
        for (var x = firstX; x <= maxX; x += spacing)
        {
            for (var z = firstZ; z <= maxZ; z += spacing)
            {
                if (TryProjectYOnTriangleXz(triangle, x, z, out var y))
                    yield return new Vector3(x, y, z);
            }
        }
    }

    private static bool TryCreateCandidateForFishableTarget(
        FishableCoverageTarget target,
        FishableCoverageRound round,
        TriangleIndex walkableIndex,
        TriangleIndex fishableIndex,
        IReadOnlySet<CandidateKey> candidateKeys,
        out CandidateScratch candidate)
    {
        candidate = default;
        foreach (var probe in EnumerateCandidateSearchProbes(target.Point, round.DirectionCount))
        {
            if (!TryFindCandidateWalkablePoint(target, probe, walkableIndex, fishableIndex, out var position))
                continue;

            if (!TryResolveLegalFacingRotation(
                    position,
                    target,
                    walkableIndex,
                    fishableIndex,
                    out var rotation))
                continue;

            var candidateScratch = new CandidateScratch(position, rotation, target.Block.SurfaceGroupId);
            if (candidateKeys.Contains(CandidateKey.From(candidateScratch.Position, candidateScratch.Rotation)))
                continue;

            candidate = candidateScratch;
            return true;
        }

        return false;
    }

    private static IEnumerable<Vector3> EnumerateCandidateSearchProbes(Vector3 fishablePoint, int directionCount)
    {
        for (var radiusIndex = 0; radiusIndex < CandidateSearchRadii.Length; radiusIndex++)
        {
            var radius = CandidateSearchRadii[radiusIndex];
            if (radius <= 0.001f)
            {
                yield return fishablePoint;
                continue;
            }

            var angleShift = radiusIndex % 2 == 0 ? 0f : 0.5f;
            for (var directionIndex = 0; directionIndex < directionCount; directionIndex++)
            {
                var angle = MathF.Tau * (directionIndex + angleShift) / directionCount;
                yield return new Vector3(
                    fishablePoint.X + (MathF.Sin(angle) * radius),
                    fishablePoint.Y,
                    fishablePoint.Z + (MathF.Cos(angle) * radius));
            }
        }
    }

    private static bool TryFindCandidateWalkablePoint(
        FishableCoverageTarget target,
        Vector3 probe,
        TriangleIndex walkableIndex,
        TriangleIndex fishableIndex,
        out Vector3 walkablePoint)
    {
        walkablePoint = default;
        if (!walkableIndex.TryFindHighestContainingPoint(probe, out _, out var candidatePoint))
            return false;

        if (!IsOpenWalkablePoint(candidatePoint, walkableIndex)
            || !IsCandidateFishableForWalkablePoint(candidatePoint, target.Point))
            return false;

        walkablePoint = candidatePoint;
        return true;
    }

    private static bool TryResolveLegalFacingRotation(
        Vector3 position,
        FishableCoverageTarget target,
        TriangleIndex walkableIndex,
        TriangleIndex fishableIndex,
        out float rotation)
    {
        rotation = 0f;
        var baseDirections = new List<Vector2>();
        TryAddBaseDirection(baseDirections, target.Point - position);
        TryAddBaseDirection(baseDirections, target.Triangle.Centroid - position);
        if (TryFindLocalFishableCenter(position, target.Triangle, target.Point, target.Block, out var localFishableCenter))
            TryAddBaseDirection(baseDirections, localFishableCenter - position);
        TryAddBaseDirection(baseDirections, target.Block.Center - position);

        var testedDirections = new HashSet<int>();
        foreach (var baseDirection in baseDirections)
        {
            foreach (var offset in CandidateFacingAngleOffsets)
            {
                var direction = RotateDirection(baseDirection, offset);
                if (!TryAddTestedDirection(testedDirections, direction))
                    continue;

                if (!IsLegalFacingDirection(position, direction, target.Point, walkableIndex, fishableIndex))
                    continue;

                rotation = AngleMath.NormalizeRotation(MathF.Atan2(direction.X, direction.Y));
                return true;
            }
        }

        const int fullSweepSteps = 16;
        for (var index = 0; index < fullSweepSteps; index++)
        {
            var angle = MathF.Tau * index / fullSweepSteps;
            var direction = new Vector2(MathF.Sin(angle), MathF.Cos(angle));
            if (!TryAddTestedDirection(testedDirections, direction)
                || !IsLegalFacingDirection(position, direction, target.Point, walkableIndex, fishableIndex))
                continue;

            rotation = AngleMath.NormalizeRotation(angle);
            return true;
        }

        return false;
    }

    private static bool IsLegalFacingDirection(
        Vector3 position,
        Vector2 direction,
        Vector3 targetFishablePoint,
        TriangleIndex walkableIndex,
        TriangleIndex fishableIndex)
    {
        if (TryGetHorizontalDirection(targetFishablePoint - position, out var targetDirection)
            && Vector2.Dot(targetDirection, direction) <= 0f)
            return false;

        for (var offset = FacingProbeStartDistance;
             offset <= FacingProbeMaxDistance;
             offset += FacingProbeStep)
        {
            var probe = CreateProbePoint(position, direction, offset);
            if (!TryFindFacingFishable(
                    fishableIndex,
                    probe,
                    out var fishablePoint))
                continue;

            if (!TryGetHorizontalDirection(fishablePoint - position, out var fishableDirection)
                || Vector2.Dot(fishableDirection, direction) <= 0f)
                continue;

            if (!IsCandidateFishableForWalkablePoint(position, fishablePoint))
                continue;

            if (IsFishableCoveredByWalkable(fishablePoint, walkableIndex))
                continue;

            return true;
        }

        return false;
    }

    private static bool IsFishableTargetCovered(
        FishableCoverageTarget target,
        IReadOnlyList<CandidateScratch> candidates,
        TriangleIndex walkableIndex,
        TriangleIndex fishableIndex)
    {
        foreach (var candidate in candidates)
        {
            if (!string.Equals(candidate.SurfaceGroupId, target.Block.SurfaceGroupId, StringComparison.Ordinal)
                || HorizontalDistance(candidate.Position, target.Point) > FishableCoverageSatisfiedDistance
                || !IsCandidateFishableForWalkablePoint(candidate.Position, target.Point))
                continue;

            var forward = new Vector2(MathF.Sin(candidate.Rotation), MathF.Cos(candidate.Rotation));
            if (TryGetHorizontalDirection(target.Point - candidate.Position, out var targetDirection)
                && Vector2.Dot(targetDirection, forward) <= 0f)
                continue;

            if (!HasOpenFishableSightLine(candidate.Position, forward, walkableIndex, fishableIndex))
                continue;

            return true;
        }

        return false;
    }

    private static bool HasOpenFishableSightLine(
        Vector3 position,
        Vector2 direction,
        TriangleIndex walkableIndex,
        TriangleIndex fishableIndex)
    {
        for (var offset = FacingProbeStartDistance;
             offset <= FacingProbeMaxDistance;
             offset += FacingProbeStep)
        {
            var probe = CreateProbePoint(position, direction, offset);
            if (!TryFindFacingFishable(fishableIndex, probe, out var fishablePoint))
                continue;

            if (!IsCandidateFishableForWalkablePoint(position, fishablePoint)
                || IsFishableCoveredByWalkable(fishablePoint, walkableIndex))
                continue;

            return true;
        }

        return false;
    }

    private static bool IsFishableCoveredByWalkable(Vector3 fishablePoint, TriangleIndex walkableIndex)
    {
        return walkableIndex.TryFindHighestContainingPoint(fishablePoint, out _, out var walkablePoint)
            && walkablePoint.Y >= fishablePoint.Y + WalkableFishableMinimumVerticalDelta;
    }

    private static bool TryFindFacingFishable(
        TriangleIndex fishableIndex,
        Vector3 probe,
        out Vector3 fishablePoint)
    {
        if (fishableIndex.TryFindNearest(
                probe,
                FacingProbeLateralRadius,
                out _,
                out fishablePoint,
                out _))
            return true;

        fishablePoint = default;
        return false;
    }

    private static bool IsCandidateFishableForWalkablePoint(Vector3 walkablePoint, Vector3 fishablePoint)
    {
        var horizontalDistance = HorizontalDistance(walkablePoint, fishablePoint);
        if (horizontalDistance > 0.05f)
            return horizontalDistance <= MaximumCandidateDistanceFromFishableSurface;

        var walkableHeightAboveFishable = walkablePoint.Y - fishablePoint.Y;
        return walkableHeightAboveFishable >= WalkableFishableMinimumVerticalDelta;
    }

    private static bool IsOpenWalkablePoint(Vector3 walkablePoint, TriangleIndex walkableIndex)
    {
        return !walkableIndex.HasContainingPointAboveWithinVerticalRange(
            walkablePoint,
            WalkableFishableMinimumVerticalDelta,
            OpenWalkableClearanceMeters);
    }

    private static void TryAddBaseDirection(List<Vector2> directions, Vector3 vector)
    {
        if (TryGetHorizontalDirection(vector, out var direction))
            directions.Add(direction);
    }

    private static bool TryAddTestedDirection(HashSet<int> testedDirections, Vector2 direction)
    {
        if (direction.LengthSquared() <= 0.0001f)
            return false;

        direction = Vector2.Normalize(direction);
        var rotation = AngleMath.NormalizeRotation(MathF.Atan2(direction.X, direction.Y));
        var key = (int)MathF.Round(rotation / FacingDirectionDedupeRadians, MidpointRounding.AwayFromZero);
        return testedDirections.Add(key);
    }

    private static Vector2 RotateDirection(Vector2 direction, float offset)
    {
        var sin = MathF.Sin(offset);
        var cos = MathF.Cos(offset);
        return new Vector2(
            (direction.X * cos) + (direction.Y * sin),
            (direction.Y * cos) - (direction.X * sin));
    }

    private static bool TryProjectYOnTriangleXz(ExtractedSceneTriangle triangle, float x, float z, out float y)
    {
        var v0X = triangle.B.X - triangle.A.X;
        var v0Z = triangle.B.Z - triangle.A.Z;
        var v1X = triangle.C.X - triangle.A.X;
        var v1Z = triangle.C.Z - triangle.A.Z;
        var v2X = x - triangle.A.X;
        var v2Z = z - triangle.A.Z;
        var denominator = (v0X * v1Z) - (v1X * v0Z);
        if (MathF.Abs(denominator) <= 0.0001f)
        {
            y = 0f;
            return false;
        }

        var bWeight = ((v2X * v1Z) - (v1X * v2Z)) / denominator;
        var cWeight = ((v0X * v2Z) - (v2X * v0Z)) / denominator;
        var aWeight = 1f - bWeight - cWeight;
        if (aWeight < -0.001f || bWeight < -0.001f || cWeight < -0.001f)
        {
            y = 0f;
            return false;
        }

        y = (triangle.A.Y * aWeight) + (triangle.B.Y * bWeight) + (triangle.C.Y * cWeight);
        return true;
    }

    private static float HorizontalDistance(Vector3 left, Vector3 right)
    {
        var dx = left.X - right.X;
        var dz = left.Z - right.Z;
        return MathF.Sqrt((dx * dx) + (dz * dz));
    }

    private static float HorizontalDistanceToTriangle(Vector3 point, ExtractedSceneTriangle triangle)
    {
        var p = new Vector2(point.X, point.Z);
        var a = new Vector2(triangle.A.X, triangle.A.Z);
        var b = new Vector2(triangle.B.X, triangle.B.Z);
        var c = new Vector2(triangle.C.X, triangle.C.Z);
        if (PointInTriangle(p, a, b, c))
            return 0f;

        return MathF.Min(
            DistanceToSegment(p, a, b),
            MathF.Min(DistanceToSegment(p, b, c), DistanceToSegment(p, c, a)));
    }

    private static Vector3 ClosestPointOnTriangleXz(Vector3 point, ExtractedSceneTriangle triangle)
    {
        var p = new Vector2(point.X, point.Z);
        var a = new Vector2(triangle.A.X, triangle.A.Z);
        var b = new Vector2(triangle.B.X, triangle.B.Z);
        var c = new Vector2(triangle.C.X, triangle.C.Z);
        var closest = PointInTriangle(p, a, b, c)
            ? p
            : ClosestPointOnTriangleEdge(p, a, b, c);

        var result = new Vector3(closest.X, triangle.Centroid.Y, closest.Y);
        if (TryProjectYOnTriangleXz(triangle, result.X, result.Z, out var y))
            result.Y = y;

        return result;
    }

    private static Vector2 ClosestPointOnTriangleEdge(Vector2 point, Vector2 a, Vector2 b, Vector2 c)
    {
        var ab = ClosestPointOnSegment(point, a, b);
        var bc = ClosestPointOnSegment(point, b, c);
        var ca = ClosestPointOnSegment(point, c, a);
        var abDistance = Vector2.DistanceSquared(point, ab);
        var bcDistance = Vector2.DistanceSquared(point, bc);
        var caDistance = Vector2.DistanceSquared(point, ca);
        if (abDistance <= bcDistance && abDistance <= caDistance)
            return ab;

        return bcDistance <= caDistance ? bc : ca;
    }

    private static bool PointInTriangle(Vector2 point, Vector2 a, Vector2 b, Vector2 c)
    {
        var ab = Cross(point - a, b - a);
        var bc = Cross(point - b, c - b);
        var ca = Cross(point - c, a - c);
        var hasNegative = ab < 0f || bc < 0f || ca < 0f;
        var hasPositive = ab > 0f || bc > 0f || ca > 0f;
        return !(hasNegative && hasPositive);
    }

    private static float DistanceToSegment(Vector2 point, Vector2 start, Vector2 end)
    {
        return Vector2.Distance(point, ClosestPointOnSegment(point, start, end));
    }

    private static Vector2 ClosestPointOnSegment(Vector2 point, Vector2 start, Vector2 end)
    {
        var segment = end - start;
        var lengthSquared = segment.LengthSquared();
        if (lengthSquared <= 0.0001f)
            return start;

        var t = Math.Clamp(Vector2.Dot(point - start, segment) / lengthSquared, 0f, 1f);
        return start + (segment * t);
    }

    private static float Cross(Vector2 left, Vector2 right)
    {
        return (left.X * right.Y) - (left.Y * right.X);
    }

    private void LogDebugCells(
        Vector3 playerPosition,
        IReadOnlyList<ExtractedSceneTriangle> fishableTriangles,
        IReadOnlyList<ExtractedSceneTriangle> walkableTriangles,
        IReadOnlyList<ApproachCandidate> candidates)
    {
        var cells = new Dictionary<GridCell, DebugCellCounts>();
        foreach (var triangle in fishableTriangles)
            GetDebugCell(cells, playerPosition, triangle.Centroid).FishableTriangles++;
        foreach (var triangle in walkableTriangles)
            GetDebugCell(cells, playerPosition, triangle.Centroid).WalkableTriangles++;
        foreach (var candidate in candidates)
            GetDebugCell(cells, playerPosition, candidate.Position.ToVector3()).Candidates++;

        foreach (var item in cells
            .OrderBy(item => Math.Abs(item.Key.X) + Math.Abs(item.Key.Z))
            .ThenBy(item => item.Key.X)
            .ThenBy(item => item.Key.Z)
            .Take(MaxDebugCells))
        {
            pluginLog.Debug(
                "FPG nearby cell: cell=({CellX},{CellZ}) fishableTriangles={FishableTriangles} walkableTriangles={WalkableTriangles} candidates={Candidates}",
                item.Key.X,
                item.Key.Z,
                item.Value.FishableTriangles,
                item.Value.WalkableTriangles,
                item.Value.Candidates);
        }
    }

    private void LogDebugCandidates(Vector3 playerPosition, IReadOnlyList<ApproachCandidate> candidates)
    {
        var index = 0;
        foreach (var candidate in candidates
            .OrderBy(candidate => HorizontalDistance(candidate.Position.ToVector3(), playerPosition))
            .ThenBy(candidate => candidate.CandidateId, StringComparer.Ordinal)
            .Take(MaxDebugCandidates))
        {
            index++;
            var position = candidate.Position.ToVector3();
            pluginLog.Debug(
                "FPG nearby candidate {Index}: pos=({X:F2},{Y:F2},{Z:F2}) dist={Distance:F1} rotation={Rotation:F3} surface={SurfaceGroupId}",
                index,
                position.X,
                position.Y,
                position.Z,
                HorizontalDistance(position, playerPosition),
                candidate.Rotation,
                string.IsNullOrWhiteSpace(candidate.SurfaceGroupId) ? "-" : candidate.SurfaceGroupId);
        }
    }

    private void LogDebugWaterSurfaces(
        Vector3 playerPosition,
        IReadOnlyList<ExtractedSceneTriangle> fishableTriangles,
        WaterSurfaceSummary? summary)
    {
        if (summary is null)
        {
            pluginLog.Debug("FPG nearby water: none");
            return;
        }

        pluginLog.Debug(
            "FPG nearby water summary: triangles={TriangleCount} area={Area:F1} nearestDist={NearestDistance:F1} nearestY={NearestY:F2} y=[{MinY:F2},{MaxY:F2}] normalY=[{MinNormalY:F2},{MaxNormalY:F2}] nearestMaterial={NearestMaterial} nearestMesh={NearestMeshType}",
            fishableTriangles.Count,
            summary.TotalArea,
            summary.NearestDistance,
            summary.NearestY,
            summary.MinY,
            summary.MaxY,
            summary.MinNormalY,
            summary.MaxNormalY,
            summary.NearestMaterial,
            summary.NearestMeshType);

        foreach (var group in fishableTriangles
            .GroupBy(triangle => triangle.Material)
            .Select(group => group.ToList())
            .OrderByDescending(group => group.Count)
            .ThenBy(group => group[0].Material)
            .Take(MaxDebugWaterMaterials))
        {
            pluginLog.Debug(
                "FPG nearby water material: material={Material} triangles={TriangleCount} area={Area:F1} y=[{MinY:F2},{MaxY:F2}] normalY=[{MinNormalY:F2},{MaxNormalY:F2}] meshTypes={MeshTypes}",
                FormatMaterial(group[0].Material),
                group.Count,
                group.Sum(triangle => triangle.Area),
                group.Min(TriangleMinY),
                group.Max(TriangleMaxY),
                group.Min(triangle => triangle.Normal.Y),
                group.Max(triangle => triangle.Normal.Y),
                string.Join(",", group.Select(triangle => triangle.MeshType).Distinct().OrderBy(type => type.ToString())));
        }

        var index = 0;
        foreach (var item in fishableTriangles
            .Select(triangle => new
            {
                Triangle = triangle,
                Distance = HorizontalDistanceToTriangle(playerPosition, triangle),
            })
            .OrderBy(item => item.Distance)
            .ThenBy(item => MathF.Abs(item.Triangle.Centroid.Y - playerPosition.Y))
            .Take(MaxDebugWaterSamples))
        {
            index++;
            var triangle = item.Triangle;
            var centroid = triangle.Centroid;
            pluginLog.Debug(
                "FPG nearby water sample {Index}: centroid=({X:F2},{Y:F2},{Z:F2}) dist={Distance:F1} y=[{MinY:F2},{MaxY:F2}] normal=({NormalX:F2},{NormalY:F2},{NormalZ:F2}) material={Material} mesh={MeshType} area={Area:F2}",
                index,
                centroid.X,
                centroid.Y,
                centroid.Z,
                item.Distance,
                TriangleMinY(triangle),
                TriangleMaxY(triangle),
                triangle.Normal.X,
                triangle.Normal.Y,
                triangle.Normal.Z,
                FormatMaterial(triangle.Material),
                triangle.MeshType,
                triangle.Area);
        }
    }

    private static WaterSurfaceSummary? CreateWaterSurfaceSummary(
        Vector3 playerPosition,
        IReadOnlyList<ExtractedSceneTriangle> fishableTriangles)
    {
        if (fishableTriangles.Count == 0)
            return null;

        var nearest = fishableTriangles
            .Select(triangle => new
            {
                Triangle = triangle,
                Distance = HorizontalDistanceToTriangle(playerPosition, triangle),
            })
            .OrderBy(item => item.Distance)
            .ThenBy(item => MathF.Abs(item.Triangle.Centroid.Y - playerPosition.Y))
            .First();

        return new WaterSurfaceSummary(
            fishableTriangles.Sum(triangle => triangle.Area),
            nearest.Distance,
            nearest.Triangle.Centroid.Y,
            fishableTriangles.Min(TriangleMinY),
            fishableTriangles.Max(TriangleMaxY),
            fishableTriangles.Min(triangle => triangle.Normal.Y),
            fishableTriangles.Max(triangle => triangle.Normal.Y),
            FormatMaterial(nearest.Triangle.Material),
            nearest.Triangle.MeshType);
    }

    private static string FormatWaterSummary(WaterSurfaceSummary? summary)
    {
        return summary is null
            ? "none"
            : $"nearest={summary.NearestDistance:F1}m y={summary.NearestY:F2} rangeY=[{summary.MinY:F2},{summary.MaxY:F2}] material={summary.NearestMaterial} mesh={summary.NearestMeshType}";
    }

    private static float TriangleMinY(ExtractedSceneTriangle triangle)
    {
        return MathF.Min(triangle.A.Y, MathF.Min(triangle.B.Y, triangle.C.Y));
    }

    private static float TriangleMaxY(ExtractedSceneTriangle triangle)
    {
        return MathF.Max(triangle.A.Y, MathF.Max(triangle.B.Y, triangle.C.Y));
    }

    private static DebugOverlayTriangle ToDebugOverlayTriangle(ExtractedSceneTriangle triangle)
    {
        return new DebugOverlayTriangle(
            triangle.A,
            triangle.B,
            triangle.C,
            triangle.Material,
            triangle.MeshType);
    }

    private static string FormatMaterial(ulong material)
    {
        return "0x" + material.ToString("X");
    }

    private static DebugCellCounts GetDebugCell(
        Dictionary<GridCell, DebugCellCounts> cells,
        Vector3 origin,
        Vector3 position)
    {
        var cell = GridCell.From(position.X - origin.X, position.Z - origin.Z, DebugCellSize);
        if (!cells.TryGetValue(cell, out var counts))
        {
            counts = new DebugCellCounts();
            cells[cell] = counts;
        }

        return counts;
    }

    private sealed class TriangleIndex
    {
        private readonly float cellSize;
        private readonly Dictionary<GridCell, List<ExtractedSceneTriangle>> cells = [];

        public TriangleIndex(IReadOnlyList<ExtractedSceneTriangle> triangles, float cellSize)
        {
            this.cellSize = cellSize;
            foreach (var triangle in triangles)
                Add(triangle);
        }

        public bool ContainsPoint(Vector3 point)
        {
            var cell = GridCell.From(point.X, point.Z, cellSize);
            if (!cells.TryGetValue(cell, out var triangles))
                return false;

            var point2 = new Vector2(point.X, point.Z);
            foreach (var triangle in triangles)
            {
                if (PointInTriangle(
                        point2,
                        new Vector2(triangle.A.X, triangle.A.Z),
                        new Vector2(triangle.B.X, triangle.B.Z),
                        new Vector2(triangle.C.X, triangle.C.Z)))
                    return true;
            }

            return false;
        }

        public bool TryFindHighestContainingPoint(
            Vector3 point,
            out ExtractedSceneTriangle triangle,
            out Vector3 containingPoint)
        {
            triangle = default;
            containingPoint = default;
            var cell = GridCell.From(point.X, point.Z, cellSize);
            if (!cells.TryGetValue(cell, out var triangles))
                return false;

            var point2 = new Vector2(point.X, point.Z);
            var bestY = float.MinValue;
            foreach (var candidate in triangles)
            {
                if (!PointInTriangle(
                        point2,
                        new Vector2(candidate.A.X, candidate.A.Z),
                        new Vector2(candidate.B.X, candidate.B.Z),
                        new Vector2(candidate.C.X, candidate.C.Z))
                    || !TryProjectYOnTriangleXz(candidate, point.X, point.Z, out var candidateY)
                    || candidateY <= bestY)
                    continue;

                bestY = candidateY;
                triangle = candidate;
                containingPoint = new Vector3(point.X, candidateY, point.Z);
            }

            return bestY > float.MinValue;
        }

        public bool HasContainingPointAboveWithinVerticalRange(
            Vector3 point,
            float minimumDeltaY,
            float maximumDeltaY)
        {
            var cell = GridCell.From(point.X, point.Z, cellSize);
            if (!cells.TryGetValue(cell, out var triangles))
                return false;

            var point2 = new Vector2(point.X, point.Z);
            foreach (var candidate in triangles)
            {
                if (!PointInTriangle(
                        point2,
                        new Vector2(candidate.A.X, candidate.A.Z),
                        new Vector2(candidate.B.X, candidate.B.Z),
                        new Vector2(candidate.C.X, candidate.C.Z))
                    || !TryProjectYOnTriangleXz(candidate, point.X, point.Z, out var candidateY))
                    continue;

                var deltaY = candidateY - point.Y;
                if (deltaY >= minimumDeltaY && deltaY <= maximumDeltaY)
                    return true;
            }

            return false;
        }

        public bool TryFindNearest(
            Vector3 point,
            float radius,
            out ExtractedSceneTriangle triangle,
            out Vector3 nearestPoint,
            out float distance)
        {
            return TryFindNearest(point, radius, null, out triangle, out nearestPoint, out distance);
        }

        public bool TryFindNearest(
            Vector3 point,
            float radius,
            Func<ExtractedSceneTriangle, Vector3, bool>? predicate,
            out ExtractedSceneTriangle triangle,
            out Vector3 nearestPoint,
            out float distance)
        {
            var center = GridCell.From(point.X, point.Z, cellSize);
            var range = (int)MathF.Ceiling(radius / cellSize) + 1;
            triangle = default;
            nearestPoint = default;
            distance = float.MaxValue;

            for (var x = center.X - range; x <= center.X + range; x++)
            {
                for (var z = center.Z - range; z <= center.Z + range; z++)
                {
                    if (!cells.TryGetValue(new GridCell(x, z), out var triangles))
                        continue;

                    foreach (var candidate in triangles)
                    {
                        var candidatePoint = ClosestPointOnTriangleXz(point, candidate);
                        var candidateDistance = HorizontalDistance(point, candidatePoint);
                        if (candidateDistance > radius || candidateDistance >= distance)
                            continue;

                        if (predicate is not null && !predicate(candidate, candidatePoint))
                            continue;

                        distance = candidateDistance;
                        nearestPoint = candidatePoint;
                        triangle = candidate;
                    }
                }
            }

            return distance < float.MaxValue;
        }

        private void Add(ExtractedSceneTriangle triangle)
        {
            var minX = MathF.Min(triangle.A.X, MathF.Min(triangle.B.X, triangle.C.X));
            var maxX = MathF.Max(triangle.A.X, MathF.Max(triangle.B.X, triangle.C.X));
            var minZ = MathF.Min(triangle.A.Z, MathF.Min(triangle.B.Z, triangle.C.Z));
            var maxZ = MathF.Max(triangle.A.Z, MathF.Max(triangle.B.Z, triangle.C.Z));
            var minCell = GridCell.From(minX, minZ, cellSize);
            var maxCell = GridCell.From(maxX, maxZ, cellSize);

            for (var x = minCell.X; x <= maxCell.X; x++)
            {
                for (var z = minCell.Z; z <= maxCell.Z; z++)
                {
                    var cell = new GridCell(x, z);
                    if (!cells.TryGetValue(cell, out var list))
                    {
                        list = [];
                        cells[cell] = list;
                    }

                    list.Add(triangle);
                }
            }
        }
    }

    private sealed class BoundaryEdgeBucket
    {
        public List<BoundaryEdge> FishableEdges { get; } = [];
        public List<BoundaryEdge> WalkableEdges { get; } = [];
    }

    private sealed class DebugCellCounts
    {
        public int FishableTriangles { get; set; }
        public int WalkableTriangles { get; set; }
        public int Candidates { get; set; }
    }

    private sealed record WaterSurfaceSummary(
        float TotalArea,
        float NearestDistance,
        float NearestY,
        float MinY,
        float MaxY,
        float MinNormalY,
        float MaxNormalY,
        string NearestMaterial,
        SceneMeshType NearestMeshType);

    private sealed record FishableSurfaceBlock(
        string SurfaceGroupId,
        IReadOnlyList<ExtractedSceneTriangle> Triangles,
        Vector3 Center,
        float Area,
        TriangleIndex Index);

    private readonly record struct FishableCoverageRound(
        string Name,
        float TargetSpacing,
        int DirectionCount);

    private readonly record struct FishableCoverageTarget(
        Vector3 Point,
        ExtractedSceneTriangle Triangle,
        FishableSurfaceBlock Block);

    private readonly record struct FishableCoverageTargetKey(string SurfaceGroupId, int X, int Y, int Z)
    {
        public static FishableCoverageTargetKey From(string surfaceGroupId, Vector3 point) => new(
            surfaceGroupId,
            Quantize(point.X),
            Quantize(point.Y),
            Quantize(point.Z));

        private static int Quantize(float value) => (int)MathF.Floor(value / FishableCoverageTargetKeyCellSize);
    }

    private readonly record struct BoundaryEdge(Vector3 Start, Vector3 End, ExtractedSceneTriangle Triangle)
    {
        public Vector3 Midpoint => (Start + End) * 0.5f;

        public float HorizontalLength
        {
            get
            {
                var dx = End.X - Start.X;
                var dz = End.Z - Start.Z;
                return MathF.Sqrt((dx * dx) + (dz * dz));
            }
        }

        public Vector3 HorizontalDirection
        {
            get
            {
                var direction = End - Start;
                direction.Y = 0f;
                return direction.LengthSquared() > 0.0001f ? Vector3.Normalize(direction) : Vector3.UnitZ;
            }
        }
    }

    private readonly record struct CandidateScratch(Vector3 Position, float Rotation, string SurfaceGroupId);

    private readonly record struct CandidateKey(int X, int Y, int Z, int Rotation)
    {
        public static CandidateKey From(Vector3 position, float rotation) => new(
            (int)MathF.Floor(position.X / CandidateDedupeCellSize),
            (int)MathF.Floor(position.Y / 2f),
            (int)MathF.Floor(position.Z / CandidateDedupeCellSize),
            (int)MathF.Floor(AngleMath.NormalizeRotation(rotation) / CandidateRotationDedupeRadians));
    }

    private readonly record struct EdgeKey(EdgeVertexKey A, EdgeVertexKey B)
    {
        public static EdgeKey From(Vector3 start, Vector3 end)
        {
            var a = EdgeVertexKey.From(start);
            var b = EdgeVertexKey.From(end);
            return EdgeVertexKey.Compare(a, b) <= 0 ? new EdgeKey(a, b) : new EdgeKey(b, a);
        }
    }

    private readonly record struct EdgeVertexKey(int X, int Y, int Z)
    {
        public static EdgeVertexKey From(Vector3 point) => new(
            Quantize(point.X),
            Quantize(point.Y),
            Quantize(point.Z));

        public static int Compare(EdgeVertexKey left, EdgeVertexKey right)
        {
            var x = left.X.CompareTo(right.X);
            if (x != 0)
                return x;

            var y = left.Y.CompareTo(right.Y);
            return y != 0 ? y : left.Z.CompareTo(right.Z);
        }

        private static int Quantize(float value)
        {
            return (int)MathF.Round(value / EdgeVertexQuantum, MidpointRounding.AwayFromZero);
        }
    }

    private readonly record struct GridCell(int X, int Z)
    {
        public static GridCell From(float x, float z, float cellSize) => new(
            (int)MathF.Floor(x / cellSize),
            (int)MathF.Floor(z / cellSize));
    }

    private enum BoundarySurfaceKind
    {
        Fishable,
        Walkable,
    }
}

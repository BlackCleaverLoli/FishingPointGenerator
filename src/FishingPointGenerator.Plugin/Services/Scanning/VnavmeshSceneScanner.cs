using System.Numerics;
using Dalamud.Plugin.Services;
using FishingPointGenerator.Core.Geometry;
using FishingPointGenerator.Core.Models;
using OmenTools;

namespace FishingPointGenerator.Plugin.Services.Scanning;

internal sealed class VnavmeshSceneScanner : ICurrentTerritoryScanner
{
    private const float EdgeVertexQuantum = 0.25f;
    private const float BoundarySampleSpacing = 2f;
    private const float BoundaryPointOffsetTowardWalkableMeters = 0.5f;
    private const float MinimumBoundaryEdgeLength = 0.75f;
    private const float MinimumWalkableSurfaceArea = 0.5f;
    private const float MaximumCandidateDistanceFromFishableSurface = 1.5f;
    private const float SurfaceIndexCellSize = 8f;
    private const float CandidateDedupeCellSize = 1.25f;
    private const float CandidateRotationDedupeRadians = 0.15f;
    private const float FishableCenterProbeStep = 0.5f;
    private const float FishableCenterProbeMaxDistance = 96f;
    private const int FishableCenterBoundaryRefineSteps = 6;
    private const float DebugCellSize = 10f;
    private const int MaxSamplesPerEdge = 40;
    private const int MaxCandidates = 10000;
    private const int MaxDebugCells = 18;
    private const int MaxDebugCandidates = 16;
    private const int MaxDebugWaterMaterials = 8;
    private const int MaxDebugWaterSamples = 8;
    private static readonly float[] CandidateSurfaceOffsets =
    [
        BoundaryPointOffsetTowardWalkableMeters,
        0.25f,
        0.75f,
        1f,
        1.25f,
        0f,
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
        var filterRadius = radius + MaximumCandidateDistanceFromFishableSurface + BoundaryPointOffsetTowardWalkableMeters;
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

    public TerritorySurveyDocument ScanCurrentTerritory()
    {
        var service = DService.Instance();
        var currentTerritoryId = service.ClientState.TerritoryType;
        if (currentTerritoryId == 0)
            return Empty(currentTerritoryId, string.Empty);

        var scene = new ActiveLayoutScene();
        scene.FillFromActiveLayout();

        var territoryId = scene.TerritoryId != 0 ? scene.TerritoryId : currentTerritoryId;
        var territoryName = scene.GetTerritoryName(currentTerritoryId);
        var extractor = new CollisionSceneExtractor(scene);
        var triangles = extractor.ExtractTriangles();
        var fishableTriangles = triangles
            .Where(triangle => triangle.IsFishable && triangle.Area > 0.05f)
            .ToList();
        var walkableTriangles = triangles
            .Where(triangle => triangle.IsWalkable && triangle.Area > 0.05f)
            .ToList();

        if (fishableTriangles.Count == 0 || walkableTriangles.Count == 0)
        {
            pluginLog.Warning(
                "FPG 场景扫描未找到可用几何体。Territory={TerritoryId}, fishable={FishableCount}, walkable={WalkableCount}",
                territoryId,
                fishableTriangles.Count,
                walkableTriangles.Count);
            return Empty(territoryId, territoryName);
        }

        var candidates = GenerateCandidates(territoryId, fishableTriangles, walkableTriangles);
        pluginLog.Information(
            "FPG 场景扫描完成。Territory={TerritoryId}, fishableTriangles={FishableCount}, walkableTriangles={WalkableCount}, candidates={CandidateCount}",
            territoryId,
            fishableTriangles.Count,
            walkableTriangles.Count,
            candidates.Count);

        return new TerritorySurveyDocument
        {
            TerritoryId = territoryId,
            TerritoryName = territoryName,
            Candidates = candidates,
        };
    }

    private static TerritorySurveyDocument Empty(uint territoryId, string territoryName) => new()
    {
        TerritoryId = territoryId,
        TerritoryName = territoryName,
    };

    private static List<ApproachCandidate> GenerateCandidates(
        uint territoryId,
        IReadOnlyList<ExtractedSceneTriangle> fishableTriangles,
        IReadOnlyList<ExtractedSceneTriangle> walkableTriangles)
    {
        var candidateKeys = new HashSet<CandidateKey>();
        var candidates = new List<CandidateScratch>();

        AddWalkableSurfaceCandidates(territoryId, fishableTriangles, walkableTriangles, candidateKeys, candidates);

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
        List<CandidateScratch> candidates)
    {
        var fishableEdges = BuildOuterEdges(fishableTriangles);
        if (fishableEdges.Count == 0)
            fishableEdges = BuildEdges(fishableTriangles);

        var walkableSurfaces = walkableTriangles
            .Where(triangle => triangle.Area >= MinimumWalkableSurfaceArea)
            .ToList();
        if (walkableSurfaces.Count == 0)
            return;

        var fishableBlocks = BuildFishableSurfaceBlocks(territoryId, fishableTriangles);
        var fishableBlockByTriangle = BuildFishableBlockLookup(fishableBlocks);
        var walkableIndex = new TriangleIndex(walkableSurfaces, SurfaceIndexCellSize);
        var fishableIndex = new TriangleIndex(fishableTriangles, SurfaceIndexCellSize);
        foreach (var fishableEdge in fishableEdges)
        {
            var sampleCount = Math.Clamp(
                (int)MathF.Ceiling(fishableEdge.HorizontalLength / BoundarySampleSpacing),
                1,
                MaxSamplesPerEdge);

            for (var sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
            {
                var t = (sampleIndex + 0.5f) / sampleCount;
                var edgePoint = Vector3.Lerp(fishableEdge.Start, fishableEdge.End, t);
                if (!TryCreateSurfaceCandidate(
                        edgePoint,
                        fishableEdge,
                        walkableIndex,
                        fishableIndex,
                        fishableBlockByTriangle,
                        out var candidate))
                    continue;

                var key = CandidateKey.From(candidate.Position, candidate.Rotation);
                if (candidateKeys.Add(key))
                    candidates.Add(candidate);
            }
        }

        AddProjectedWalkableSurfaceCandidates(
            walkableSurfaces,
            fishableIndex,
            fishableBlockByTriangle,
            candidateKeys,
            candidates);
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

    private static IReadOnlyDictionary<ExtractedSceneTriangle, FishableSurfaceBlock> BuildFishableBlockLookup(
        IReadOnlyList<FishableSurfaceBlock> blocks)
    {
        var result = new Dictionary<ExtractedSceneTriangle, FishableSurfaceBlock>();
        foreach (var block in blocks)
        {
            foreach (var triangle in block.Triangles)
                result[triangle] = block;
        }

        return result;
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

    private static bool TryCreateSurfaceCandidate(
        Vector3 edgePoint,
        BoundaryEdge fishableEdge,
        TriangleIndex walkableIndex,
        TriangleIndex fishableIndex,
        IReadOnlyDictionary<ExtractedSceneTriangle, FishableSurfaceBlock> fishableBlockByTriangle,
        out CandidateScratch candidate)
    {
        candidate = default;
        if (!walkableIndex.TryFindNearest(
                edgePoint,
                MaximumCandidateDistanceFromFishableSurface,
                out var walkableTriangle,
                out var closestWalkablePoint,
                out _))
            return false;

        var towardWalkableInterior = walkableTriangle.Centroid - closestWalkablePoint;
        towardWalkableInterior.Y = 0f;
        if (towardWalkableInterior.LengthSquared() <= 0.0001f)
        {
            towardWalkableInterior = walkableTriangle.Centroid - edgePoint;
            towardWalkableInterior.Y = 0f;
        }

        if (towardWalkableInterior.LengthSquared() <= 0.0001f)
        {
            var edgeDirection = fishableEdge.HorizontalDirection;
            towardWalkableInterior = new Vector3(-edgeDirection.Z, 0f, edgeDirection.X);
        }

        towardWalkableInterior = Vector3.Normalize(towardWalkableInterior);
        foreach (var offset in CandidateSurfaceOffsets)
        {
            var position = closestWalkablePoint + (towardWalkableInterior * offset);
            if (!TryProjectYOnTriangleXz(walkableTriangle, position.X, position.Z, out var y))
                continue;

            position.Y = y;
            if (fishableIndex.ContainsPoint(position))
                continue;

            if (!fishableIndex.TryFindNearest(
                    position,
                    MaximumCandidateDistanceFromFishableSurface,
                    out var nearestFishableTriangle,
                    out var nearestFishablePoint,
                    out _))
                continue;

            var surfaceGroupId = string.Empty;
            var facingTarget = nearestFishablePoint;
            if (fishableBlockByTriangle.TryGetValue(nearestFishableTriangle, out var fishableBlock))
            {
                surfaceGroupId = fishableBlock.SurfaceGroupId;
                if (TryFindLocalFishableCenter(
                        position,
                        nearestFishableTriangle,
                        nearestFishablePoint,
                        fishableBlock,
                        out var localFishableCenter))
                    facingTarget = localFishableCenter;
            }
            var facing = facingTarget - position;
            facing.Y = 0f;
            if (facing.LengthSquared() <= 0.0001f)
            {
                facing = nearestFishablePoint - position;
                facing.Y = 0f;
            }

            if (facing.LengthSquared() <= 0.0001f)
                continue;

            var rotation = AngleMath.NormalizeRotation(MathF.Atan2(facing.X, facing.Z));
            candidate = new CandidateScratch(position, rotation, surfaceGroupId);
            return true;
        }

        return false;
    }

    private static void AddProjectedWalkableSurfaceCandidates(
        IReadOnlyList<ExtractedSceneTriangle> walkableSurfaces,
        TriangleIndex fishableIndex,
        IReadOnlyDictionary<ExtractedSceneTriangle, FishableSurfaceBlock> fishableBlockByTriangle,
        HashSet<CandidateKey> candidateKeys,
        List<CandidateScratch> candidates)
    {
        foreach (var walkableSurface in walkableSurfaces)
        {
            foreach (var sample in EnumerateWalkableSurfaceSamples(walkableSurface))
            {
                if (!TryCreateProjectedWalkableSurfaceCandidate(
                        sample,
                        fishableIndex,
                        fishableBlockByTriangle,
                        out var candidate))
                    continue;

                var key = CandidateKey.From(candidate.Position, candidate.Rotation);
                if (candidateKeys.Add(key))
                    candidates.Add(candidate);
            }
        }
    }

    private static IEnumerable<Vector3> EnumerateWalkableSurfaceSamples(ExtractedSceneTriangle triangle)
    {
        yield return triangle.Centroid;
        yield return (triangle.A + triangle.B) * 0.5f;
        yield return (triangle.B + triangle.C) * 0.5f;
        yield return (triangle.C + triangle.A) * 0.5f;
    }

    private static bool TryCreateProjectedWalkableSurfaceCandidate(
        Vector3 position,
        TriangleIndex fishableIndex,
        IReadOnlyDictionary<ExtractedSceneTriangle, FishableSurfaceBlock> fishableBlockByTriangle,
        out CandidateScratch candidate)
    {
        candidate = default;
        if (!fishableIndex.TryFindNearest(
                position,
                MaximumCandidateDistanceFromFishableSurface,
                out var nearestFishableTriangle,
                out var nearestFishablePoint,
                out _))
            return false;

        var surfaceGroupId = string.Empty;
        var facingTarget = nearestFishablePoint;
        if (fishableBlockByTriangle.TryGetValue(nearestFishableTriangle, out var fishableBlock))
        {
            surfaceGroupId = fishableBlock.SurfaceGroupId;
            if (TryFindLocalFishableCenter(
                    position,
                    nearestFishableTriangle,
                    nearestFishablePoint,
                    fishableBlock,
                    out var localFishableCenter))
                facingTarget = localFishableCenter;
        }

        var facing = facingTarget - position;
        facing.Y = 0f;
        if (facing.LengthSquared() <= 0.0001f)
        {
            facing = nearestFishableTriangle.Centroid - position;
            facing.Y = 0f;
        }

        if (facing.LengthSquared() <= 0.0001f)
            return false;

        var rotation = AngleMath.NormalizeRotation(MathF.Atan2(facing.X, facing.Z));
        candidate = new CandidateScratch(position, rotation, surfaceGroupId);
        return true;
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

        public bool TryFindNearest(
            Vector3 point,
            float radius,
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

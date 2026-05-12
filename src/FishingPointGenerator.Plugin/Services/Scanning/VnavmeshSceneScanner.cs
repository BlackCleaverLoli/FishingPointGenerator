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
    private const float NearbyEdgeIndexCellSize = 2f;
    private const float NearbyEdgeMatchDistance = 0.75f;
    private const float NearbyEdgeDirectionDot = 0.85f;
    private const float NearbyEdgeHeightTolerance = 1.25f;
    private const float CandidateDedupeCellSize = 1.25f;
    private const float CandidateRotationDedupeRadians = 0.15f;
    private const float DebugCellSize = 10f;
    private const int MaxSamplesPerEdge = 40;
    private const int MaxCandidates = 10000;
    private const int MaxDebugCells = 18;
    private const int MaxDebugCandidates = 16;
    private const int MaxDebugWaterMaterials = 8;
    private const int MaxDebugWaterSamples = 8;

    private readonly IPluginLog pluginLog;

    public VnavmeshSceneScanner(IPluginLog pluginLog)
    {
        this.pluginLog = pluginLog;
    }

    public string Name => "当前布局可钓/可走边界扫描器";
    public bool IsPlaceholder => false;

    public string DebugScanNearby(float radiusMeters)
    {
        var service = DService.Instance();
        var player = service.ObjectTable.LocalPlayer;
        var currentTerritoryId = service.ClientState.TerritoryType;
        if (currentTerritoryId == 0 || player is null)
            return "附近扫描失败：没有可用区域或本地玩家。";

        var radius = Math.Clamp(radiusMeters, 5f, 200f);
        var playerPosition = player.Position;
        var scene = new ActiveLayoutScene();
        scene.FillFromActiveLayout();
        var territoryId = scene.TerritoryId != 0 ? scene.TerritoryId : currentTerritoryId;
        var extractor = new CollisionSceneExtractor(scene);
        var allTriangles = extractor.ExtractTriangles();
        var filterRadius = radius + NearbyEdgeMatchDistance + BoundaryPointOffsetTowardWalkableMeters;
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
        var nearbyMatches = CountNearbyEdgeMatches(
            fishableOuterEdges.Count > 0 ? fishableOuterEdges : fishableEdges,
            walkableOuterEdges.Count > 0 ? walkableOuterEdges : walkableEdges);
        var candidates = GenerateCandidates(territoryId, fishableTriangles, walkableTriangles)
            .Where(candidate => HorizontalDistance(candidate.Position.ToVector3(), playerPosition) <= radius)
            .ToList();
        var waterSummary = CreateWaterSurfaceSummary(playerPosition, fishableTriangles);

        pluginLog.Information(
            "FPG nearby scan: territory={TerritoryId} player=({PlayerX:F2},{PlayerY:F2},{PlayerZ:F2}) radius={Radius:F1} allTriangles={AllTriangles} nearbyTriangles={NearbyTriangles} fishableTriangles={FishableTriangles} walkableTriangles={WalkableTriangles} fishableEdges={FishableEdges} walkableEdges={WalkableEdges} fishableOuterEdges={FishableOuterEdges} walkableOuterEdges={WalkableOuterEdges} sharedEdgeBuckets={SharedEdgeBuckets} nearbyEdgeMatches={NearbyEdgeMatches} candidates={Candidates} water={WaterSummary}",
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
            nearbyMatches,
            candidates.Count,
            FormatWaterSummary(waterSummary));
        LogDebugWaterSurfaces(playerPosition, fishableTriangles, waterSummary);
        LogDebugCells(playerPosition, fishableTriangles, walkableTriangles, candidates);
        LogDebugCandidates(playerPosition, candidates);

        return $"附近扫描 {radius:F1}m：fishable={fishableTriangles.Count} walkable={walkableTriangles.Count} water={FormatWaterSummary(waterSummary)} sharedBuckets={sharedBuckets} fishableOuterEdges={fishableOuterEdges.Count} nearbyEdges={nearbyMatches} candidates={candidates.Count}。详情见 Dalamud log。";
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
        var buckets = BuildEdgeBuckets(fishableTriangles, walkableTriangles);
        var candidateKeys = new HashSet<CandidateKey>();
        var candidates = new List<CandidateScratch>();

        AddSharedEdgeCandidates(buckets, candidateKeys, candidates);
        AddNearbyEdgeCandidates(fishableTriangles, walkableTriangles, candidateKeys, candidates);

        return candidates
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Position.X)
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
                Position = Point3.From(candidate.Position),
                Rotation = candidate.Rotation,
                Score = candidate.Score,
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

    private static void AddSharedEdgeCandidates(
        Dictionary<EdgeKey, BoundaryEdgeBucket> buckets,
        HashSet<CandidateKey> candidateKeys,
        List<CandidateScratch> candidates)
    {
        foreach (var bucket in buckets.Values)
        {
            if (bucket.FishableEdges.Count == 0 || bucket.WalkableEdges.Count == 0)
                continue;

            foreach (var fishableEdge in bucket.FishableEdges)
            {
                var walkableEdge = bucket.WalkableEdges
                    .OrderBy(edge => HorizontalDistance(edge.Midpoint, fishableEdge.Midpoint))
                    .First();
                AddBoundaryEdgeSamples(fishableEdge, walkableEdge, candidateKeys, candidates, exactSharedEdge: true);
            }
        }
    }

    private static void AddNearbyEdgeCandidates(
        IReadOnlyList<ExtractedSceneTriangle> fishableTriangles,
        IReadOnlyList<ExtractedSceneTriangle> walkableTriangles,
        HashSet<CandidateKey> candidateKeys,
        List<CandidateScratch> candidates)
    {
        var fishableEdges = BuildOuterEdges(fishableTriangles);
        if (fishableEdges.Count == 0)
            fishableEdges = BuildEdges(fishableTriangles);

        var walkableEdges = BuildOuterEdges(walkableTriangles);
        if (walkableEdges.Count == 0)
            walkableEdges = BuildEdges(walkableTriangles);

        var walkableIndex = new BoundaryEdgeIndex(walkableEdges, NearbyEdgeIndexCellSize);
        foreach (var fishableEdge in fishableEdges)
        {
            var nearestWalkableEdge = walkableIndex.FindNearest(fishableEdge);
            if (nearestWalkableEdge is null)
                continue;

            AddBoundaryEdgeSamples(fishableEdge, nearestWalkableEdge.Value, candidateKeys, candidates, exactSharedEdge: false);
        }
    }

    private static int CountNearbyEdgeMatches(
        IReadOnlyList<BoundaryEdge> fishableEdges,
        IReadOnlyList<BoundaryEdge> walkableEdges)
    {
        if (fishableEdges.Count == 0 || walkableEdges.Count == 0)
            return 0;

        var walkableIndex = new BoundaryEdgeIndex(walkableEdges, NearbyEdgeIndexCellSize);
        var count = 0;
        foreach (var fishableEdge in fishableEdges)
        {
            if (walkableIndex.FindNearest(fishableEdge) is not null)
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

    private static void AddBoundaryEdgeSamples(
        BoundaryEdge fishableEdge,
        BoundaryEdge walkableEdge,
        HashSet<CandidateKey> candidateKeys,
        List<CandidateScratch> candidates,
        bool exactSharedEdge)
    {
        if (!TryGetDirectionTowardWalkable(fishableEdge, walkableEdge, out var direction))
            return;

        var sampleCount = Math.Clamp(
            (int)MathF.Ceiling(fishableEdge.HorizontalLength / BoundarySampleSpacing),
            1,
            MaxSamplesPerEdge);
        var rotation = AngleMath.NormalizeRotation(MathF.Atan2(direction.X, direction.Z));
        var score = CalculateScore(fishableEdge, walkableEdge, exactSharedEdge);

        for (var sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
        {
            var t = (sampleIndex + 0.5f) / sampleCount;
            var edgePoint = Vector3.Lerp(fishableEdge.Start, fishableEdge.End, t);
            var position = edgePoint + (direction * BoundaryPointOffsetTowardWalkableMeters);
            if (!TryProjectYOnTriangleXz(walkableEdge.Triangle, position.X, position.Z, out var y))
                continue;

            position.Y = y;

            var key = CandidateKey.From(position, rotation);
            if (!candidateKeys.Add(key))
                continue;

            candidates.Add(new CandidateScratch(position, rotation, score));
        }
    }

    private static bool TryGetDirectionTowardWalkable(
        BoundaryEdge fishableEdge,
        BoundaryEdge walkableEdge,
        out Vector3 direction)
    {
        direction = default;
        var edgeVector = fishableEdge.End - fishableEdge.Start;
        edgeVector.Y = 0f;
        if (edgeVector.LengthSquared() <= 0.0001f)
            return false;

        edgeVector = Vector3.Normalize(edgeVector);
        var normal = new Vector3(-edgeVector.Z, 0f, edgeVector.X);
        var toWalkable = walkableEdge.Triangle.Centroid - fishableEdge.Midpoint;
        toWalkable.Y = 0f;
        if (toWalkable.LengthSquared() <= 0.0001f)
            toWalkable = walkableEdge.Midpoint - fishableEdge.Midpoint;
        toWalkable.Y = 0f;

        if (toWalkable.LengthSquared() <= 0.0001f)
            return false;

        if (Vector3.Dot(normal, toWalkable) < 0f)
            normal = -normal;

        direction = normal;
        return true;
    }

    private static float CalculateScore(BoundaryEdge fishableEdge, BoundaryEdge walkableEdge, bool exactSharedEdge)
    {
        var edgeLengthScore = Math.Clamp(fishableEdge.HorizontalLength / 12f, 0f, 1f);
        var normalScore = Math.Clamp((fishableEdge.Triangle.Normal.Y + walkableEdge.Triangle.Normal.Y) * 0.5f, 0f, 1f);
        var matchScore = exactSharedEdge
            ? 1f
            : 1f - Math.Clamp(HorizontalDistance(fishableEdge.Midpoint, walkableEdge.Midpoint) / NearbyEdgeMatchDistance, 0f, 1f);

        return (matchScore * 0.45f) + (edgeLengthScore * 0.25f) + (normalScore * 0.3f);
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

    private static float HorizontalDistanceToSegment(Vector3 point, BoundaryEdge edge)
    {
        var p = new Vector2(point.X, point.Z);
        var start = new Vector2(edge.Start.X, edge.Start.Z);
        var end = new Vector2(edge.End.X, edge.End.Z);
        var segment = end - start;
        var lengthSquared = segment.LengthSquared();
        if (lengthSquared <= 0.0001f)
            return Vector2.Distance(p, start);

        var t = Math.Clamp(Vector2.Dot(p - start, segment) / lengthSquared, 0f, 1f);
        return Vector2.Distance(p, start + (segment * t));
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
        var segment = end - start;
        var lengthSquared = segment.LengthSquared();
        if (lengthSquared <= 0.0001f)
            return Vector2.Distance(point, start);

        var t = Math.Clamp(Vector2.Dot(point - start, segment) / lengthSquared, 0f, 1f);
        return Vector2.Distance(point, start + (segment * t));
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
            .ThenByDescending(candidate => candidate.Score)
            .Take(MaxDebugCandidates))
        {
            index++;
            var position = candidate.Position.ToVector3();
            pluginLog.Debug(
                "FPG nearby candidate {Index}: pos=({X:F2},{Y:F2},{Z:F2}) dist={Distance:F1} rotation={Rotation:F3} score={Score:F3}",
                index,
                position.X,
                position.Y,
                position.Z,
                HorizontalDistance(position, playerPosition),
                candidate.Rotation,
                candidate.Score);
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

    private sealed class BoundaryEdgeIndex
    {
        private readonly float cellSize;
        private readonly Dictionary<GridCell, List<BoundaryEdge>> cells = [];

        public BoundaryEdgeIndex(IReadOnlyList<BoundaryEdge> edges, float cellSize)
        {
            this.cellSize = cellSize;
            foreach (var edge in edges)
                Add(edge);
        }

        public BoundaryEdge? FindNearest(BoundaryEdge fishableEdge)
        {
            var center = GridCell.From(fishableEdge.Midpoint.X, fishableEdge.Midpoint.Z, cellSize);
            var range = (int)MathF.Ceiling(NearbyEdgeMatchDistance / cellSize) + 1;
            BoundaryEdge? best = null;
            var bestDistance = float.MaxValue;

            for (var x = center.X - range; x <= center.X + range; x++)
            {
                for (var z = center.Z - range; z <= center.Z + range; z++)
                {
                    if (!cells.TryGetValue(new GridCell(x, z), out var edges))
                        continue;

                    foreach (var edge in edges)
                    {
                        if (!IsNearbyBoundaryMatch(fishableEdge, edge, out var distance))
                            continue;

                        if (distance < bestDistance)
                        {
                            bestDistance = distance;
                            best = edge;
                        }
                    }
                }
            }

            return best;
        }

        private void Add(BoundaryEdge edge)
        {
            var cell = GridCell.From(edge.Midpoint.X, edge.Midpoint.Z, cellSize);
            if (!cells.TryGetValue(cell, out var list))
            {
                list = [];
                cells[cell] = list;
            }

            list.Add(edge);
        }
    }

    private static bool IsNearbyBoundaryMatch(BoundaryEdge fishableEdge, BoundaryEdge walkableEdge, out float distance)
    {
        distance = float.MaxValue;
        if (MathF.Abs(fishableEdge.Midpoint.Y - walkableEdge.Midpoint.Y) > NearbyEdgeHeightTolerance)
            return false;

        var directionDot = MathF.Abs(Vector3.Dot(fishableEdge.HorizontalDirection, walkableEdge.HorizontalDirection));
        if (directionDot < NearbyEdgeDirectionDot)
            return false;

        var fishableToWalkable = HorizontalDistanceToSegment(fishableEdge.Midpoint, walkableEdge);
        var walkableToFishable = HorizontalDistanceToSegment(walkableEdge.Midpoint, fishableEdge);
        distance = MathF.Min(fishableToWalkable, walkableToFishable);
        return distance <= NearbyEdgeMatchDistance;
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

    private readonly record struct CandidateScratch(Vector3 Position, float Rotation, float Score);

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

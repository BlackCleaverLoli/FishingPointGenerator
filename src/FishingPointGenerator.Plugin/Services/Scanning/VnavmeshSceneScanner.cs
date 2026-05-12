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
    private const int MaxSamplesPerEdge = 40;
    private const int MaxCandidates = 10000;

    private readonly IPluginLog pluginLog;

    public VnavmeshSceneScanner(IPluginLog pluginLog)
    {
        this.pluginLog = pluginLog;
    }

    public string Name => "当前布局可钓/可走边界扫描器";
    public bool IsPlaceholder => false;

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
        var walkableIndex = new BoundaryEdgeIndex(BuildEdges(walkableTriangles), NearbyEdgeIndexCellSize);
        foreach (var fishableEdge in BuildEdges(fishableTriangles))
        {
            var nearestWalkableEdge = walkableIndex.FindNearest(fishableEdge);
            if (nearestWalkableEdge is null)
                continue;

            AddBoundaryEdgeSamples(fishableEdge, nearestWalkableEdge.Value, candidateKeys, candidates, exactSharedEdge: false);
        }
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
            if (TryProjectYOnTriangleXz(walkableEdge.Triangle, position.X, position.Z, out var y))
                position.Y = y;
            else
                position.Y = edgePoint.Y;

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

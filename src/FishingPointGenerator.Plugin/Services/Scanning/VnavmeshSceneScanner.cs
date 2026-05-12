using System.Numerics;
using Dalamud.Plugin.Services;
using FishingPointGenerator.Core.Geometry;
using FishingPointGenerator.Core.Models;
using OmenTools;

namespace FishingPointGenerator.Plugin.Services.Scanning;

internal sealed class VnavmeshSceneScanner : ICurrentTerritoryScanner
{
    private const float TargetSampleSpacing = 14f;
    private const float TargetDedupeCellSize = 10f;
    private const float CandidateDedupeCellSize = 3f;
    private const float WalkableIndexCellSize = 8f;
    private const float MinCastDistance = 6f;
    private const float MaxCastDistance = 26f;
    private const float MaxStandingAboveTarget = 8f;
    private const float MaxStandingBelowTarget = 2f;
    private const int DirectionCount = 24;
    private const int MaxTargetSamples = 3000;
    private const int MaxCandidates = 2500;

    private static readonly float[] StandingDistances = [7f, 10f, 14f, 18f, 22f];

    private readonly IPluginLog pluginLog;

    public VnavmeshSceneScanner(IPluginLog pluginLog)
    {
        this.pluginLog = pluginLog;
    }

    public string Name => "Active layout fishable-material scanner";
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
                "FPG scene scan found no usable geometry. Territory={TerritoryId}, fishable={FishableCount}, walkable={WalkableCount}",
                territoryId,
                fishableTriangles.Count,
                walkableTriangles.Count);
            return Empty(territoryId, territoryName);
        }

        var player = service.ObjectTable.LocalPlayer;
        var candidates = GenerateCandidates(territoryId, fishableTriangles, walkableTriangles, player?.Position);
        pluginLog.Information(
            "FPG scene scan finished. Territory={TerritoryId}, fishableTriangles={FishableCount}, walkableTriangles={WalkableCount}, candidates={CandidateCount}",
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
        IReadOnlyList<ExtractedSceneTriangle> walkableTriangles,
        Vector3? playerPosition)
    {
        var targetSamples = BuildTargetSamples(fishableTriangles);
        var walkableIndex = new WalkableTriangleIndex(walkableTriangles, WalkableIndexCellSize);
        var candidateKeys = new HashSet<CandidateKey>();
        var candidates = new List<CandidateScratch>();

        foreach (var target in targetSamples)
        {
            foreach (var distance in StandingDistances)
            {
                for (var directionIndex = 0; directionIndex < DirectionCount; directionIndex++)
                {
                    var angle = MathF.Tau * directionIndex / DirectionCount;
                    var direction = new Vector3(MathF.Sin(angle), 0f, MathF.Cos(angle));
                    var standingXZ = target.Position - (direction * distance);
                    if (!walkableIndex.TrySnapToWalkable(standingXZ.X, standingXZ.Z, target.Position.Y, out var standingPosition))
                        continue;

                    var horizontalDistance = HorizontalDistance(standingPosition, target.Position);
                    if (horizontalDistance is < MinCastDistance or > MaxCastDistance)
                        continue;

                    var verticalDelta = standingPosition.Y - target.Position.Y;
                    if (verticalDelta < -MaxStandingBelowTarget || verticalDelta > MaxStandingAboveTarget)
                        continue;

                    var key = CandidateKey.From(standingPosition, target.Position);
                    if (!candidateKeys.Add(key))
                        continue;

                    candidates.Add(new CandidateScratch(
                        standingPosition,
                        target.Position,
                        CalculateScore(standingPosition, target, horizontalDistance, playerPosition)));
                }
            }
        }

        return candidates
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Position.X)
            .ThenBy(candidate => candidate.Position.Z)
            .Take(MaxCandidates)
            .OrderBy(candidate => candidate.Position.X)
            .ThenBy(candidate => candidate.Position.Z)
            .ThenBy(candidate => candidate.Target.X)
            .ThenBy(candidate => candidate.Target.Z)
            .Select((candidate, index) =>
            {
                var position = Point3.From(candidate.Position);
                var target = Point3.From(candidate.Target);
                return new ApproachCandidate
                {
                    CandidateId = $"scene_{territoryId}_{index + 1:D5}",
                    TerritoryId = territoryId,
                    Position = position,
                    Rotation = AngleMath.RotationFromTo(position, target),
                    TargetPoint = target,
                    Score = candidate.Score,
                    Status = CandidateStatus.Unlabeled,
                };
            })
            .ToList();
    }

    private static IReadOnlyList<TargetSample> BuildTargetSamples(IReadOnlyList<ExtractedSceneTriangle> fishableTriangles)
    {
        var samples = new Dictionary<TargetKey, TargetSample>();
        foreach (var triangle in fishableTriangles)
        {
            if (!TryGetTriangleXzBounds(triangle, out var minX, out var maxX, out var minZ, out var maxZ))
            {
                AddTargetSample(samples, triangle.Centroid, triangle.Area);
                continue;
            }

            var projectedArea = ProjectedAreaXz(triangle);
            if (projectedArea < 4f)
            {
                AddTargetSample(samples, triangle.Centroid, triangle.Area);
                continue;
            }

            var firstX = MathF.Floor(minX / TargetSampleSpacing) * TargetSampleSpacing;
            var lastX = MathF.Ceiling(maxX / TargetSampleSpacing) * TargetSampleSpacing;
            var firstZ = MathF.Floor(minZ / TargetSampleSpacing) * TargetSampleSpacing;
            var lastZ = MathF.Ceiling(maxZ / TargetSampleSpacing) * TargetSampleSpacing;

            for (var x = firstX; x <= lastX; x += TargetSampleSpacing)
            {
                for (var z = firstZ; z <= lastZ; z += TargetSampleSpacing)
                {
                    if (TryProjectYOnTriangleXz(triangle, x, z, out var y))
                        AddTargetSample(samples, new Vector3(x, y, z), triangle.Area);
                }
            }

            AddTargetSample(samples, triangle.Centroid, triangle.Area);
        }

        var ordered = samples.Values
            .OrderBy(sample => sample.Position.X)
            .ThenBy(sample => sample.Position.Z)
            .ToList();

        if (ordered.Count <= MaxTargetSamples)
            return ordered;

        var stride = (float)ordered.Count / MaxTargetSamples;
        var limited = new List<TargetSample>(MaxTargetSamples);
        for (var index = 0; index < MaxTargetSamples; index++)
            limited.Add(ordered[(int)MathF.Floor(index * stride)]);

        return limited;
    }

    private static void AddTargetSample(Dictionary<TargetKey, TargetSample> samples, Vector3 position, float area)
    {
        var key = TargetKey.From(position);
        if (!samples.TryGetValue(key, out var existing) || area > existing.SourceArea)
            samples[key] = new TargetSample(position, area);
    }

    private static float CalculateScore(Vector3 standingPosition, TargetSample target, float horizontalDistance, Vector3? playerPosition)
    {
        var distanceScore = 1f - Math.Clamp(MathF.Abs(horizontalDistance - 12f) / 14f, 0f, 1f);
        var heightScore = 1f - Math.Clamp(MathF.Abs(standingPosition.Y - target.Position.Y) / 10f, 0f, 1f);
        var areaScore = Math.Clamp(MathF.Log(target.SourceArea + 1f) / 8f, 0f, 1f);
        var playerScore = playerPosition.HasValue
            ? 1f - Math.Clamp(HorizontalDistance(standingPosition, playerPosition.Value) / 300f, 0f, 1f)
            : 0.5f;

        return (distanceScore * 0.45f) + (heightScore * 0.3f) + (areaScore * 0.15f) + (playerScore * 0.1f);
    }

    private static bool TryGetTriangleXzBounds(ExtractedSceneTriangle triangle, out float minX, out float maxX, out float minZ, out float maxZ)
    {
        minX = MathF.Min(triangle.A.X, MathF.Min(triangle.B.X, triangle.C.X));
        maxX = MathF.Max(triangle.A.X, MathF.Max(triangle.B.X, triangle.C.X));
        minZ = MathF.Min(triangle.A.Z, MathF.Min(triangle.B.Z, triangle.C.Z));
        maxZ = MathF.Max(triangle.A.Z, MathF.Max(triangle.B.Z, triangle.C.Z));
        return maxX - minX > 0.01f && maxZ - minZ > 0.01f;
    }

    private static float ProjectedAreaXz(ExtractedSceneTriangle triangle)
    {
        var abX = triangle.B.X - triangle.A.X;
        var abZ = triangle.B.Z - triangle.A.Z;
        var acX = triangle.C.X - triangle.A.X;
        var acZ = triangle.C.Z - triangle.A.Z;
        return MathF.Abs((abX * acZ) - (abZ * acX)) * 0.5f;
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

    private sealed class WalkableTriangleIndex
    {
        private readonly float cellSize;
        private readonly Dictionary<GridCell, List<ExtractedSceneTriangle>> cells = [];

        public WalkableTriangleIndex(IReadOnlyList<ExtractedSceneTriangle> triangles, float cellSize)
        {
            this.cellSize = cellSize;
            foreach (var triangle in triangles)
                AddTriangle(triangle);
        }

        public bool TrySnapToWalkable(float x, float z, float targetY, out Vector3 position)
        {
            var center = GridCell.From(x, z, cellSize);
            var bestY = 0f;
            var bestScore = float.MaxValue;
            var found = false;

            for (var dx = -1; dx <= 1; dx++)
            {
                for (var dz = -1; dz <= 1; dz++)
                {
                    if (!cells.TryGetValue(new GridCell(center.X + dx, center.Z + dz), out var triangles))
                        continue;

                    foreach (var triangle in triangles)
                    {
                        if (!TryProjectYOnTriangleXz(triangle, x, z, out var y))
                            continue;

                        var verticalDelta = y - targetY;
                        if (verticalDelta < -MaxStandingBelowTarget || verticalDelta > MaxStandingAboveTarget)
                            continue;

                        var score = MathF.Abs(verticalDelta);
                        if (score >= bestScore)
                            continue;

                        bestY = y;
                        bestScore = score;
                        found = true;
                    }
                }
            }

            position = found ? new Vector3(x, bestY, z) : default;
            return found;
        }

        private void AddTriangle(ExtractedSceneTriangle triangle)
        {
            if (!TryGetTriangleXzBounds(triangle, out var minX, out var maxX, out var minZ, out var maxZ))
                return;

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

    private readonly record struct TargetSample(Vector3 Position, float SourceArea);

    private readonly record struct CandidateScratch(Vector3 Position, Vector3 Target, float Score);

    private readonly record struct GridCell(int X, int Z)
    {
        public static GridCell From(float x, float z, float cellSize) => new(
            (int)MathF.Floor(x / cellSize),
            (int)MathF.Floor(z / cellSize));
    }

    private readonly record struct TargetKey(int X, int Y, int Z)
    {
        public static TargetKey From(Vector3 position) => new(
            (int)MathF.Floor(position.X / TargetDedupeCellSize),
            (int)MathF.Floor(position.Y / 4f),
            (int)MathF.Floor(position.Z / TargetDedupeCellSize));
    }

    private readonly record struct CandidateKey(int X, int Y, int Z, int TargetX, int TargetZ)
    {
        public static CandidateKey From(Vector3 position, Vector3 target) => new(
            (int)MathF.Floor(position.X / CandidateDedupeCellSize),
            (int)MathF.Floor(position.Y / 2f),
            (int)MathF.Floor(position.Z / CandidateDedupeCellSize),
            (int)MathF.Floor(target.X / TargetDedupeCellSize),
            (int)MathF.Floor(target.Z / TargetDedupeCellSize));
    }
}

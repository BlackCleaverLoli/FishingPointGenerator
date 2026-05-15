using System.Diagnostics;
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
    private const float MaximumCandidateDistanceFromFishableSurface = 2f;
    private const float PotentialWalkableSurfaceProbeSpacing = 1f;
    private const float FishableProjectionContactTolerance = 0.15f;
    private const float FishableProjectionBlockerVerticalEpsilon = 0f;
    private const float WalkableBlockCandidateSampleSpacing = 2f;
    private const float CandidateFishableRayHeight = 0.5f;
    private const float CandidateFishableRayMaxDistance = MaximumCandidateDistanceFromFishableSurface;
    private const float FishableCoverageSatisfiedDistance = 2f;
    private const float FishableCoverageTargetKeyCellSize = 0.5f;
    private const float WalkableStandingClearanceMeters = 5f;
    private const float OpenFishableClearanceMeters = 5f;
    private const float WalkableFishableMinimumVerticalDelta = 0f;
    private const float ProbeCacheCellSize = 0.25f;
    private const float CandidateCoverageCellSize = 2f;
    private const float SurfaceIndexCellSize = 8f;
    private const float CandidateDedupeCellSize = 1.25f;
    private const float CandidateDedupeVerticalCellSize = 2f;
    private const float CandidateStartPointYCellSize = 0.5f;
    private const float CandidateFacingHintCellSize = CandidateDedupeCellSize;
    private const float FishableCenterProbeStep = 0.5f;
    private const float FishableCenterProbeMaxDistance = 96f;
    private const int FishableCenterBoundaryRefineSteps = 6;
    private const float FishableBoundaryNormalSmoothRadius = 8f;
    private const float FacingProbeStartDistance = 0.5f;
    private const float FacingProbeStep = 0.5f;
    private const float FacingProbeMaxDistance = 2f;
    private const float FacingProbeLateralRadius = 0.75f;
    private const float FacingCorridorProbeStep = 0.25f;
    private const float FacingCorridorBlockerMinimumHeight = 0.25f;
    private const float FacingDirectionDedupeRadians = 0.05f;
    private const float CollisionBlockerHorizontalBuffer = 1f;
    private const float SightLineIntersectionEpsilon = 0.001f;
    private const float SightLineCacheCellSize = 0.05f;
    private const float DebugCellSize = 10f;
    private const int MaxCandidates = 1000000;
    private const int MaxDebugCells = 18;
    private const int MaxDebugCandidates = 16;
    private const int MaxDebugCandidateTargets = 6;
    private const int MaxDebugWalkableLayerValues = 8;
    private const int MaxDebugWaterMaterials = 8;
    private const int MaxDebugWaterSamples = 8;
    private const float CandidateFacingLocalRefineOffsetRadians = MathF.PI / 6f;
    private const float CandidateFacingSectorMergeGapRadians = MathF.PI / 3f + 0.001f;
    private const int CandidateFacingHintSearchRadiusCells = 1;
    private const int MaxCandidateFacingHintsPerCell = 4;
    private static readonly FishableCoverageRound[] FishableCoverageRounds =
    [
        new("粗扫", 4f, 8),
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
    private static readonly float[] CandidateFacingPrimaryAngleOffsets =
    [
        0f,
        MathF.PI / 12f,
        -MathF.PI / 12f,
        MathF.PI / 6f,
        -MathF.PI / 6f,
    ];
    private static readonly float[] FishableCenterEntryOffsets =
    [
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
            .Where(triangle => triangle.IsFishable)
            .ToList();
        var walkableTriangles = nearbyTriangles
            .Where(triangle => triangle.IsWalkable)
            .ToList();
        var collisionBlockerTriangles = nearbyTriangles
            .Where(IsCollisionBlocker)
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
        var candidates = GenerateCandidates(territoryId, fishableTriangles, walkableTriangles, collisionBlockerTriangles, log: pluginLog)
            .Where(candidate => HorizontalDistance(candidate.Position.ToVector3(), playerPosition) <= radius)
            .ToList();
        var waterSummary = CreateWaterSurfaceSummary(playerPosition, fishableTriangles);

        pluginLog.Information(
            "FPG nearby scan: territory={TerritoryId} player=({PlayerX:F2},{PlayerY:F2},{PlayerZ:F2}) radius={Radius:F1} allTriangles={AllTriangles} nearbyTriangles={NearbyTriangles} fishableTriangles={FishableTriangles} walkableTriangles={WalkableTriangles} collisionBlockers={CollisionBlockers} fishableEdges={FishableEdges} walkableEdges={WalkableEdges} fishableOuterEdges={FishableOuterEdges} walkableOuterEdges={WalkableOuterEdges} sharedEdgeBuckets={SharedEdgeBuckets} nearbySurfaceMatches={NearbySurfaceMatches} candidates={Candidates} water={WaterSummary}",
            territoryId,
            playerPosition.X,
            playerPosition.Y,
            playerPosition.Z,
            radius,
            allTriangles.Count,
            nearbyTriangles.Count,
            fishableTriangles.Count,
            walkableTriangles.Count,
            collisionBlockerTriangles.Count,
            fishableEdges.Count,
            walkableEdges.Count,
            fishableOuterEdges.Count,
            walkableOuterEdges.Count,
            sharedBuckets,
            nearbySurfaceMatches,
            candidates.Count,
            FormatWaterSummary(waterSummary));
        LogDebugWaterSurfaces(playerPosition, fishableTriangles, waterSummary);
        LogDebugCandidateGeneration(territoryId, playerPosition, fishableTriangles, walkableTriangles, collisionBlockerTriangles);
        LogDebugCells(playerPosition, fishableTriangles, walkableTriangles, candidates);
        LogDebugCandidates(playerPosition, candidates);

        return new NearbyScanDebugResult
        {
            Message = $"附近扫描 {radius:F1}m：fishable={fishableTriangles.Count} walkable={walkableTriangles.Count} collisionBlockers={collisionBlockerTriangles.Count} water={FormatWaterSummary(waterSummary)} sharedBuckets={sharedBuckets} fishableOuterEdges={fishableOuterEdges.Count} nearbySurfaces={nearbySurfaceMatches} candidates={candidates.Count}。详情见 Dalamud log；Fishable/Walkable 面和候选点已送入 overlay 调试层。",
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
            .Where(triangle => triangle.IsFishable)
            .ToList();
        var walkableTriangles = triangles
            .Where(triangle => triangle.IsWalkable)
            .ToList();
        var collisionBlockerTriangles = triangles
            .Where(IsCollisionBlocker)
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

        progress?.Report(new TerritoryScanProgress("生成候选", 2, 4, $"fishable={fishableTriangles.Count} walkable={walkableTriangles.Count} collision={collisionBlockerTriangles.Count}，正在生成候选。"));
        var candidates = GenerateCandidates(territoryId, fishableTriangles, walkableTriangles, collisionBlockerTriangles, progress, cancellationToken, pluginLog);
        cancellationToken.ThrowIfCancellationRequested();
        pluginLog.Information(
            "FPG 场景扫描完成。Territory={TerritoryId}, fishableTriangles={FishableCount}, walkableTriangles={WalkableCount}, collisionBlockers={CollisionBlockers}, candidates={CandidateCount}",
            territoryId,
            fishableTriangles.Count,
            walkableTriangles.Count,
            collisionBlockerTriangles.Count,
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
        IReadOnlyList<ExtractedSceneTriangle> collisionBlockerTriangles,
        IProgress<TerritoryScanProgress>? progress = null,
        CancellationToken cancellationToken = default,
        IPluginLog? log = null)
    {
        var totalStopwatch = Stopwatch.StartNew();
        var diagnostics = log is null
            ? null
            : new CandidateGenerationDiagnostics(log, territoryId, fishableTriangles.Count, walkableTriangles.Count, collisionBlockerTriangles.Count);
        var candidateDedupe = new CandidateDedupeIndex();
        var candidates = new List<CandidateScratch>();

        AddWalkableSurfaceCandidates(territoryId, fishableTriangles, walkableTriangles, collisionBlockerTriangles, candidateDedupe, candidates, progress, cancellationToken, diagnostics);

        var orderStopwatch = Stopwatch.StartNew();
        var orderedCandidates = candidates
            .OrderBy(candidate => candidate.Position.X)
            .ThenBy(candidate => candidate.Position.Z)
            .ThenBy(candidate => candidate.Rotation)
            .Take(MaxCandidates)
            .ToList();
        diagnostics?.LogStep("order-candidates", orderStopwatch, $"input={candidates.Count} output={orderedCandidates.Count}");

        var result = orderedCandidates
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
        diagnostics?.LogSummary(totalStopwatch, result.Count);
        return result;
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
        IReadOnlyList<ExtractedSceneTriangle> collisionBlockerTriangles,
        CandidateDedupeIndex candidateDedupe,
        List<CandidateScratch> candidates,
        IProgress<TerritoryScanProgress>? progress,
        CancellationToken cancellationToken,
        CandidateGenerationDiagnostics? diagnostics)
    {
        var stepStopwatch = Stopwatch.StartNew();
        progress?.Report(new TerritoryScanProgress("生成候选", 0, 5, $"正在为 {fishableTriangles.Count} 个可钓面建立索引。"));
        var fishableIndex = new TriangleIndex(fishableTriangles, SurfaceIndexCellSize);
        diagnostics?.LogStep("build-fishable-index", stepStopwatch, $"triangles={fishableTriangles.Count} cells={fishableIndex.CellCount} entries={fishableIndex.EntryCount}");

        stepStopwatch.Restart();
        progress?.Report(new TerritoryScanProgress("水面分组", 0, Math.Max(1, fishableTriangles.Count), $"正在为 {fishableTriangles.Count} 个可钓面构筑水面分组。"));
        var fishableSurfaceGroupIds = BuildFishableSurfaceGroupIds(territoryId, fishableTriangles);
        var fishableSurfaceGroupCount = fishableSurfaceGroupIds.Values.Distinct(StringComparer.Ordinal).Count();
        diagnostics?.LogStep("build-fishable-surface-groups", stepStopwatch, $"groups={fishableSurfaceGroupCount}");
        progress?.Report(new TerritoryScanProgress("水面分组", fishableTriangles.Count, Math.Max(1, fishableTriangles.Count), $"已构筑 {fishableSurfaceGroupCount} 个水面分组。"));

        stepStopwatch.Restart();
        progress?.Report(new TerritoryScanProgress("生成候选", 1, 5, $"正在筛选 2m 内的潜在可走面。walkable={walkableTriangles.Count}。"));
        var walkableSurfaces = SelectPotentialWalkableSurfaces(walkableTriangles, fishableIndex);
        diagnostics?.LogStep("filter-walkable-surfaces", stepStopwatch, $"input={walkableTriangles.Count} output={walkableSurfaces.Count} maxHorizontalDistance={MaximumCandidateDistanceFromFishableSurface:F1}");
        if (walkableSurfaces.Count == 0)
            return;

        stepStopwatch.Restart();
        progress?.Report(new TerritoryScanProgress("生成候选", 2, 5, $"正在建立 {walkableSurfaces.Count} 个潜在可走面索引。"));
        var walkableIndex = new TriangleIndex(walkableSurfaces, SurfaceIndexCellSize);
        diagnostics?.LogStep("build-walkable-index", stepStopwatch, $"triangles={walkableSurfaces.Count} cells={walkableIndex.CellCount} entries={walkableIndex.EntryCount}");

        stepStopwatch.Restart();
        progress?.Report(new TerritoryScanProgress("生成候选", 3, 5, $"正在筛选 {collisionBlockerTriangles.Count} 个相关碰撞面。"));
        var relevantCollisionBlockers = FilterRelevantCollisionBlockers(fishableTriangles, collisionBlockerTriangles);
        diagnostics?.LogStep("filter-collision-blockers", stepStopwatch, $"input={collisionBlockerTriangles.Count} output={relevantCollisionBlockers.Count}");

        stepStopwatch.Restart();
        var collisionBlockerIndex = new TriangleIndex(relevantCollisionBlockers, SurfaceIndexCellSize);
        diagnostics?.LogStep("build-collision-blocker-index", stepStopwatch, $"triangles={relevantCollisionBlockers.Count} cells={collisionBlockerIndex.CellCount} entries={collisionBlockerIndex.EntryCount}");

        stepStopwatch.Restart();
        progress?.Report(new TerritoryScanProgress("生成候选", 4, 5, $"正在从 {walkableSurfaces.Count} 个潜在可走面下投生成候选。"));
        AddRayDropWalkableCandidates(
            territoryId,
            fishableIndex,
            fishableSurfaceGroupIds,
            walkableSurfaces,
            walkableIndex,
            collisionBlockerIndex,
            candidateDedupe,
            candidates,
            progress,
            cancellationToken,
            diagnostics);
        diagnostics?.LogStep("ray-drop-walkable-candidates", stepStopwatch, $"candidates={candidates.Count}");
    }

    private static IReadOnlyList<ExtractedSceneTriangle> FilterRelevantCollisionBlockers(
        IReadOnlyList<ExtractedSceneTriangle> fishableTriangles,
        IReadOnlyList<ExtractedSceneTriangle> collisionBlockerTriangles)
    {
        if (fishableTriangles.Count == 0 || collisionBlockerTriangles.Count == 0)
            return [];

        var bounds = CalculateHorizontalBounds(fishableTriangles);
        var buffer = MaximumCandidateDistanceFromFishableSurface
            + CandidateFishableRayMaxDistance
            + FacingProbeLateralRadius
            + CollisionBlockerHorizontalBuffer;
        var cells = BuildBufferedHorizontalCells(fishableTriangles, buffer, SurfaceIndexCellSize);
        return collisionBlockerTriangles
            .Where(triangle =>
                TriangleOverlapsHorizontalBounds(triangle, bounds, buffer)
                && TriangleOverlapsAnyCell(triangle, cells, SurfaceIndexCellSize))
            .ToList();
    }

    private static IReadOnlyList<ExtractedSceneTriangle> SelectPotentialWalkableSurfaces(
        IReadOnlyList<ExtractedSceneTriangle> walkableTriangles,
        TriangleIndex fishableIndex)
    {
        return walkableTriangles
            .Where(triangle => IsWalkableSurfaceNearFishableSurface(triangle, fishableIndex))
            .ToList();
    }

    private static bool IsWalkableSurfaceNearFishableSurface(
        ExtractedSceneTriangle walkableTriangle,
        TriangleIndex fishableIndex)
    {
        foreach (var point in EnumerateTriangleProximitySamples(walkableTriangle, PotentialWalkableSurfaceProbeSpacing))
        {
            if (fishableIndex.TryFindNearest(
                    point,
                    MaximumCandidateDistanceFromFishableSurface,
                    out _,
                    out _,
                    out _))
                return true;
        }

        return false;
    }

    private static IEnumerable<Vector3> EnumerateTriangleProximitySamples(
        ExtractedSceneTriangle triangle,
        float spacing)
    {
        var keys = new HashSet<ProbePointKey>();
        foreach (var point in EnumerateTriangleFeatureSamples(triangle, spacing))
        {
            if (keys.Add(ProbePointKey.From(point)))
                yield return point;
        }

        foreach (var point in EnumerateTriangleSamples(triangle, spacing))
        {
            if (keys.Add(ProbePointKey.From(point)))
                yield return point;
        }
    }

    private static IEnumerable<Vector3> EnumerateTriangleFeatureSamples(
        ExtractedSceneTriangle triangle,
        float spacing)
    {
        yield return triangle.A;
        yield return triangle.B;
        yield return triangle.C;
        yield return triangle.Centroid;

        foreach (var point in EnumerateSegmentSamples(triangle.A, triangle.B, spacing))
            yield return point;
        foreach (var point in EnumerateSegmentSamples(triangle.B, triangle.C, spacing))
            yield return point;
        foreach (var point in EnumerateSegmentSamples(triangle.C, triangle.A, spacing))
            yield return point;
    }

    private static IEnumerable<Vector3> EnumerateSegmentSamples(Vector3 start, Vector3 end, float spacing)
    {
        var horizontalLength = HorizontalDistance(start, end);
        var stepCount = Math.Max(1, (int)MathF.Ceiling(horizontalLength / spacing));
        for (var index = 1; index < stepCount; index++)
        {
            var t = index / (float)stepCount;
            yield return Vector3.Lerp(start, end, t);
        }
    }

    private static int CountNearbySurfaceMatches(
        IReadOnlyList<BoundaryEdge> fishableEdges,
        IReadOnlyList<ExtractedSceneTriangle> walkableTriangles)
    {
        var walkableSurfaces = walkableTriangles.ToList();
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

    private static (float MinX, float MaxX, float MinZ, float MaxZ) CalculateHorizontalBounds(
        IReadOnlyList<ExtractedSceneTriangle> triangles)
    {
        var minX = float.MaxValue;
        var maxX = float.MinValue;
        var minZ = float.MaxValue;
        var maxZ = float.MinValue;

        foreach (var triangle in triangles)
        {
            minX = MathF.Min(minX, triangle.A.X);
            minX = MathF.Min(minX, triangle.B.X);
            minX = MathF.Min(minX, triangle.C.X);
            maxX = MathF.Max(maxX, triangle.A.X);
            maxX = MathF.Max(maxX, triangle.B.X);
            maxX = MathF.Max(maxX, triangle.C.X);
            minZ = MathF.Min(minZ, triangle.A.Z);
            minZ = MathF.Min(minZ, triangle.B.Z);
            minZ = MathF.Min(minZ, triangle.C.Z);
            maxZ = MathF.Max(maxZ, triangle.A.Z);
            maxZ = MathF.Max(maxZ, triangle.B.Z);
            maxZ = MathF.Max(maxZ, triangle.C.Z);
        }

        return (minX, maxX, minZ, maxZ);
    }

    private static HorizontalBounds CalculateHorizontalBounds(ExtractedSceneTriangle triangle)
    {
        var minX = MathF.Min(triangle.A.X, MathF.Min(triangle.B.X, triangle.C.X));
        var maxX = MathF.Max(triangle.A.X, MathF.Max(triangle.B.X, triangle.C.X));
        var minZ = MathF.Min(triangle.A.Z, MathF.Min(triangle.B.Z, triangle.C.Z));
        var maxZ = MathF.Max(triangle.A.Z, MathF.Max(triangle.B.Z, triangle.C.Z));
        return new HorizontalBounds(minX, maxX, minZ, maxZ);
    }

    private static bool TriangleOverlapsHorizontalBounds(
        ExtractedSceneTriangle triangle,
        (float MinX, float MaxX, float MinZ, float MaxZ) bounds,
        float buffer)
    {
        var triangleMinX = MathF.Min(triangle.A.X, MathF.Min(triangle.B.X, triangle.C.X));
        var triangleMaxX = MathF.Max(triangle.A.X, MathF.Max(triangle.B.X, triangle.C.X));
        var triangleMinZ = MathF.Min(triangle.A.Z, MathF.Min(triangle.B.Z, triangle.C.Z));
        var triangleMaxZ = MathF.Max(triangle.A.Z, MathF.Max(triangle.B.Z, triangle.C.Z));

        return triangleMaxX >= bounds.MinX - buffer
            && triangleMinX <= bounds.MaxX + buffer
            && triangleMaxZ >= bounds.MinZ - buffer
            && triangleMinZ <= bounds.MaxZ + buffer;
    }

    private static HashSet<GridCell> BuildBufferedHorizontalCells(
        IReadOnlyList<ExtractedSceneTriangle> triangles,
        float buffer,
        float cellSize)
    {
        var cells = new HashSet<GridCell>();
        foreach (var triangle in triangles)
        {
            var minX = MathF.Min(triangle.A.X, MathF.Min(triangle.B.X, triangle.C.X)) - buffer;
            var maxX = MathF.Max(triangle.A.X, MathF.Max(triangle.B.X, triangle.C.X)) + buffer;
            var minZ = MathF.Min(triangle.A.Z, MathF.Min(triangle.B.Z, triangle.C.Z)) - buffer;
            var maxZ = MathF.Max(triangle.A.Z, MathF.Max(triangle.B.Z, triangle.C.Z)) + buffer;
            var minCell = GridCell.From(minX, minZ, cellSize);
            var maxCell = GridCell.From(maxX, maxZ, cellSize);

            for (var x = minCell.X; x <= maxCell.X; x++)
            {
                for (var z = minCell.Z; z <= maxCell.Z; z++)
                    cells.Add(new GridCell(x, z));
            }
        }

        return cells;
    }

    private static bool TriangleOverlapsAnyCell(
        ExtractedSceneTriangle triangle,
        IReadOnlySet<GridCell> cells,
        float cellSize)
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
                if (cells.Contains(new GridCell(x, z)))
                    return true;
            }
        }

        return false;
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

    private static IReadOnlyDictionary<ExtractedSceneTriangle, string> BuildFishableSurfaceGroupIds(
        uint territoryId,
        IReadOnlyList<ExtractedSceneTriangle> triangles)
    {
        if (triangles.Count == 0)
            return new Dictionary<ExtractedSceneTriangle, string>();

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
        var groups = new List<(List<int> Indexes, Vector3 Center, float Area)>();
        for (var start = 0; start < triangles.Count; start++)
        {
            if (visited[start])
                continue;

            var indexes = new List<int>();
            var weightedCenter = Vector3.Zero;
            var totalArea = 0f;
            var queue = new Queue<int>();
            visited[start] = true;
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                indexes.Add(current);
                var triangle = triangles[current];
                weightedCenter += triangle.Centroid * triangle.Area;
                totalArea += triangle.Area;

                foreach (var next in adjacency[current])
                {
                    if (visited[next])
                        continue;

                    visited[next] = true;
                    queue.Enqueue(next);
                }
            }

            var center = totalArea > 0.0001f
                ? weightedCenter / totalArea
                : indexes.Aggregate(Vector3.Zero, (current, index) => current + triangles[index].Centroid) / indexes.Count;
            groups.Add((indexes, center, totalArea));
        }

        var groupIds = new Dictionary<ExtractedSceneTriangle, string>();
        foreach (var item in groups
            .OrderBy(group => group.Center.X)
            .ThenBy(group => group.Center.Z)
            .ThenByDescending(group => group.Area)
            .Select((group, index) => new
            {
                group.Indexes,
                SurfaceGroupId = $"t{territoryId}_surface_{index + 1:D4}",
            }))
        {
            foreach (var triangleIndex in item.Indexes)
                groupIds[triangles[triangleIndex]] = item.SurfaceGroupId;
        }

        return groupIds;
    }

    private static IReadOnlyList<FishableSurfaceBlock> BuildFishableSurfaceBlocks(
        uint territoryId,
        IReadOnlyList<ExtractedSceneTriangle> triangles,
        TriangleIndex? walkableIndex = null)
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

        AddFishableProjectionAdjacency(triangles, walkableIndex, adjacency);

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

    private static void AddFishableProjectionAdjacency(
        IReadOnlyList<ExtractedSceneTriangle> triangles,
        TriangleIndex? walkableIndex,
        List<int>[] adjacency)
    {
        var cells = new Dictionary<GridCell, List<int>>();
        for (var index = 0; index < triangles.Count; index++)
            AddTriangleProjectionCells(cells, triangles[index], index);

        var testedPairs = new HashSet<TrianglePairKey>();
        foreach (var indexes in cells.Values)
        {
            for (var left = 0; left < indexes.Count; left++)
            {
                for (var right = left + 1; right < indexes.Count; right++)
                {
                    var leftIndex = indexes[left];
                    var rightIndex = indexes[right];
                    var pair = TrianglePairKey.From(leftIndex, rightIndex);
                    if (!testedPairs.Add(pair)
                        || !AreFishableProjectionsConnected(triangles[leftIndex], triangles[rightIndex], walkableIndex))
                        continue;

                    adjacency[leftIndex].Add(rightIndex);
                    adjacency[rightIndex].Add(leftIndex);
                }
            }
        }
    }

    private static void AddTriangleProjectionCells(
        Dictionary<GridCell, List<int>> cells,
        ExtractedSceneTriangle triangle,
        int triangleIndex)
    {
        var bounds = CalculateHorizontalBounds(triangle).Expand(FishableProjectionContactTolerance);
        var minCell = GridCell.From(bounds.MinX, bounds.MinZ, SurfaceIndexCellSize);
        var maxCell = GridCell.From(bounds.MaxX, bounds.MaxZ, SurfaceIndexCellSize);

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

                list.Add(triangleIndex);
            }
        }
    }

    private static bool AreFishableProjectionsConnected(
        ExtractedSceneTriangle left,
        ExtractedSceneTriangle right,
        TriangleIndex? walkableIndex)
    {
        if (!CalculateHorizontalBounds(left).Overlaps(CalculateHorizontalBounds(right), FishableProjectionContactTolerance))
            return false;

        if (!TryFindProjectionContactPoint(left, right, out var contactPoint, out var leftY, out var rightY))
            return false;

        return !IsFishableProjectionBlockedByWalkable(contactPoint, leftY, rightY, walkableIndex);
    }

    private static bool TryFindProjectionContactPoint(
        ExtractedSceneTriangle left,
        ExtractedSceneTriangle right,
        out Vector2 contactPoint,
        out float leftY,
        out float rightY)
    {
        foreach (var point in EnumerateTriangleProjectionSamples(left))
        {
            if (TryUseProjectionContactPoint(point, left, right, out leftY, out rightY))
            {
                contactPoint = point;
                return true;
            }
        }

        foreach (var point in EnumerateTriangleProjectionSamples(right))
        {
            if (TryUseProjectionContactPoint(point, left, right, out leftY, out rightY))
            {
                contactPoint = point;
                return true;
            }
        }

        var leftEdges = GetTriangleProjectionEdges(left);
        var rightEdges = GetTriangleProjectionEdges(right);
        foreach (var leftEdge in leftEdges)
        {
            foreach (var rightEdge in rightEdges)
            {
                if (!TryGetSegmentIntersection(leftEdge.Start, leftEdge.End, rightEdge.Start, rightEdge.End, out var intersection)
                    || !TryUseProjectionContactPoint(intersection, left, right, out leftY, out rightY))
                    continue;

                contactPoint = intersection;
                return true;
            }
        }

        contactPoint = default;
        leftY = 0f;
        rightY = 0f;
        return false;
    }

    private static IEnumerable<Vector2> EnumerateTriangleProjectionSamples(ExtractedSceneTriangle triangle)
    {
        yield return new Vector2(triangle.Centroid.X, triangle.Centroid.Z);
        yield return new Vector2(triangle.A.X, triangle.A.Z);
        yield return new Vector2(triangle.B.X, triangle.B.Z);
        yield return new Vector2(triangle.C.X, triangle.C.Z);
        yield return new Vector2((triangle.A.X + triangle.B.X) * 0.5f, (triangle.A.Z + triangle.B.Z) * 0.5f);
        yield return new Vector2((triangle.B.X + triangle.C.X) * 0.5f, (triangle.B.Z + triangle.C.Z) * 0.5f);
        yield return new Vector2((triangle.C.X + triangle.A.X) * 0.5f, (triangle.C.Z + triangle.A.Z) * 0.5f);
    }

    private static bool TryUseProjectionContactPoint(
        Vector2 point,
        ExtractedSceneTriangle left,
        ExtractedSceneTriangle right,
        out float leftY,
        out float rightY)
    {
        leftY = 0f;
        rightY = 0f;
        if (!TryProjectYOnTriangleXz(left, point.X, point.Y, out var projectedLeftY)
            || !TryProjectYOnTriangleXz(right, point.X, point.Y, out var projectedRightY))
            return false;

        leftY = projectedLeftY;
        rightY = projectedRightY;
        return true;
    }

    private static IReadOnlyList<ProjectionEdge> GetTriangleProjectionEdges(ExtractedSceneTriangle triangle)
    {
        var a = new Vector2(triangle.A.X, triangle.A.Z);
        var b = new Vector2(triangle.B.X, triangle.B.Z);
        var c = new Vector2(triangle.C.X, triangle.C.Z);
        return
        [
            new ProjectionEdge(a, b),
            new ProjectionEdge(b, c),
            new ProjectionEdge(c, a),
        ];
    }

    private static bool TryGetSegmentIntersection(
        Vector2 leftStart,
        Vector2 leftEnd,
        Vector2 rightStart,
        Vector2 rightEnd,
        out Vector2 intersection)
    {
        var left = leftEnd - leftStart;
        var right = rightEnd - rightStart;
        var denominator = Cross(left, right);
        if (MathF.Abs(denominator) <= 0.0001f)
        {
            intersection = default;
            return false;
        }

        var delta = rightStart - leftStart;
        var leftT = Cross(delta, right) / denominator;
        var rightT = Cross(delta, left) / denominator;
        if (leftT < -FishableProjectionContactTolerance
            || leftT > 1f + FishableProjectionContactTolerance
            || rightT < -FishableProjectionContactTolerance
            || rightT > 1f + FishableProjectionContactTolerance)
        {
            intersection = default;
            return false;
        }

        intersection = leftStart + (left * Math.Clamp(leftT, 0f, 1f));
        return true;
    }

    private static bool IsFishableProjectionBlockedByWalkable(
        Vector2 contactPoint,
        float leftY,
        float rightY,
        TriangleIndex? walkableIndex)
    {
        if (walkableIndex is null)
            return false;

        var lowerY = MathF.Min(leftY, rightY);
        var upperY = MathF.Max(leftY, rightY);
        if (upperY - lowerY <= FishableProjectionBlockerVerticalEpsilon * 2f)
            return false;

        foreach (var walkablePoint in walkableIndex.FindContainingPoints(new Vector3(contactPoint.X, lowerY, contactPoint.Y)))
        {
            if (walkablePoint.Y > lowerY + FishableProjectionBlockerVerticalEpsilon
                && walkablePoint.Y < upperY - FishableProjectionBlockerVerticalEpsilon)
                return true;
        }

        return false;
    }

    private static bool IsFishablePointBlockedByWalkable(Vector3 fishablePoint, TriangleIndex walkableIndex)
    {
        return walkableIndex.HasContainingPointAboveWithinVerticalRange(
            fishablePoint,
            WalkableFishableMinimumVerticalDelta,
            OpenFishableClearanceMeters);
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
        var bounds = CalculateHorizontalBounds(triangles);
        var boundarySegments = BuildFishableBoundarySegments(triangles);
        return new FishableSurfaceBlock(
            string.Empty,
            triangles.ToList(),
            boundarySegments,
            center,
            new HorizontalBounds(bounds.MinX, bounds.MaxX, bounds.MinZ, bounds.MaxZ),
            totalArea,
            new TriangleIndex(triangles, SurfaceIndexCellSize));
    }

    private static IReadOnlyList<FishableBoundarySegment> BuildFishableBoundarySegments(IReadOnlyList<ExtractedSceneTriangle> triangles)
    {
        return BuildOuterEdges(triangles)
            .Select(edge => CreateFishableBoundarySegment(edge))
            .Where(segment => segment.Length >= MinimumBoundaryEdgeLength)
            .ToList();
    }

    private static FishableBoundarySegment CreateFishableBoundarySegment(BoundaryEdge edge)
    {
        var midpoint = edge.Midpoint;
        var edgeVector = edge.End - edge.Start;
        var edgeDirection = new Vector2(edgeVector.X, edgeVector.Z);
        if (edgeDirection.LengthSquared() > 0.0001f)
        {
            edgeDirection = Vector2.Normalize(edgeDirection);
            var inwardDirection = new Vector2(-edgeDirection.Y, edgeDirection.X);
            if (TryGetHorizontalDirection(edge.Triangle.Centroid - midpoint, out var centroidDirection)
                && Vector2.Dot(inwardDirection, centroidDirection) < 0f)
                inwardDirection = -inwardDirection;

            return new FishableBoundarySegment(
                edge.Start,
                edge.End,
                inwardDirection,
                edge.HorizontalLength,
                edge.Triangle);
        }

        if (!TryGetHorizontalDirection(edge.Triangle.Centroid - midpoint, out var fallbackDirection))
            fallbackDirection = Vector2.UnitY;

        return new FishableBoundarySegment(
            edge.Start,
            edge.End,
            fallbackDirection,
            edge.HorizontalLength,
            edge.Triangle);
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

    private static void AddRayDropWalkableCandidates(
        uint territoryId,
        TriangleIndex fishableIndex,
        IReadOnlyDictionary<ExtractedSceneTriangle, string> fishableSurfaceGroupIds,
        IReadOnlyList<ExtractedSceneTriangle> walkableSurfaces,
        TriangleIndex walkableIndex,
        TriangleIndex collisionBlockerIndex,
        CandidateDedupeIndex candidateDedupe,
        List<CandidateScratch> candidates,
        IProgress<TerritoryScanProgress>? progress,
        CancellationToken cancellationToken,
        CandidateGenerationDiagnostics? diagnostics)
    {
        if (walkableSurfaces.Count == 0)
            return;

        var queryCache = new ScanQueryCache(walkableIndex, collisionBlockerIndex, diagnostics);
        var facingHints = new RayDropFacingHintIndex();
        var sampledPoints = new HashSet<WalkableCandidateStartKey>();
        var progressTotal = Math.Max(1, walkableSurfaces.Count);
        for (var surfaceIndex = 0; surfaceIndex < walkableSurfaces.Count; surfaceIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (candidates.Count >= MaxCandidates)
                return;

            if (surfaceIndex % 16 == 0)
            {
                progress?.Report(new TerritoryScanProgress(
                    "生成候选",
                    surfaceIndex,
                    progressTotal,
                    $"可走面射线下投：{surfaceIndex}/{walkableSurfaces.Count}，已生成 {candidates.Count} 个。"));
            }

            foreach (var position in EnumerateTriangleProximitySamples(walkableSurfaces[surfaceIndex], WalkableBlockCandidateSampleSpacing))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (candidates.Count >= MaxCandidates)
                    return;

                if (!sampledPoints.Add(WalkableCandidateStartKey.From(position)))
                    continue;

                if (!fishableIndex.TryFindNearest(
                        position,
                        MaximumCandidateDistanceFromFishableSurface,
                        out _,
                        out _,
                        out _))
                    continue;

                if (!queryCache.HasStandingClearance(position))
                {
                    if (diagnostics is not null)
                        diagnostics.WalkableRejects++;
                    continue;
                }

                if (diagnostics is not null)
                    diagnostics.CandidateCreateAttempts++;
                if (!TryCreateRayDropCandidateForWalkableSample(
                        territoryId,
                        fishableIndex,
                        fishableSurfaceGroupIds,
                        position,
                        queryCache,
                        facingHints,
                        out var candidate))
                {
                    if (diagnostics is not null)
                        diagnostics.CandidateCreateFailures++;
                    continue;
                }

                if (!candidateDedupe.TryAdd(candidate))
                {
                    if (diagnostics is not null)
                        diagnostics.CandidateDedupeRejects++;
                    continue;
                }

                candidates.Add(candidate);
                if (diagnostics is not null)
                    diagnostics.CandidateCreates++;
            }
        }
    }

    private static void AddCoverageDrivenCandidates(
        IReadOnlyList<FishableSurfaceBlock> fishableBlocks,
        IReadOnlyList<ExtractedSceneTriangle> walkableSurfaces,
        TriangleIndex walkableIndex,
        TriangleIndex collisionBlockerIndex,
        CandidateDedupeIndex candidateDedupe,
        List<CandidateScratch> candidates,
        IProgress<TerritoryScanProgress>? progress,
        CancellationToken cancellationToken,
        CandidateGenerationDiagnostics? diagnostics)
    {
        var queryCache = new ScanQueryCache(walkableIndex, collisionBlockerIndex, diagnostics);
        var coverageIndex = new CandidateCoverageIndex(candidates);
        var blockCandidateStopwatch = Stopwatch.StartNew();
        AddBlockWalkableSurfaceCandidates(
            fishableBlocks,
            walkableSurfaces,
            queryCache,
            coverageIndex,
            candidateDedupe,
            candidates,
            cancellationToken,
            diagnostics);
        diagnostics?.LogStep("block-walkable-candidates", blockCandidateStopwatch, $"candidates={candidates.Count}");

        for (var roundIndex = 0; roundIndex < FishableCoverageRounds.Length; roundIndex++)
        {
            var round = FishableCoverageRounds[roundIndex];
            var targetStopwatch = Stopwatch.StartNew();
            var targets = EnumerateFishableCoverageTargets(fishableBlocks, round.TargetSpacing, walkableIndex).ToList();
            diagnostics?.LogStep($"coverage-targets-{round.Name}", targetStopwatch, $"targets={targets.Count} spacing={round.TargetSpacing:F1} directions={round.DirectionCount}");
            var additionsBeforeRound = candidates.Count;
            var progressTotal = Math.Max(1, targets.Count);
            var roundStopwatch = Stopwatch.StartNew();
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
                if (diagnostics is not null)
                    diagnostics.CoverageTargets++;
                if (coverageIndex.IsCovered(target, queryCache))
                {
                    if (diagnostics is not null)
                        diagnostics.CoverageHits++;
                    continue;
                }

                if (diagnostics is not null)
                    diagnostics.CandidateCreateAttempts++;
                if (!TryCreateCandidateForFishableTarget(
                        target,
                        round,
                        queryCache,
                        candidateDedupe,
                        out var candidate,
                        diagnostics))
                {
                    if (diagnostics is not null)
                        diagnostics.CandidateCreateFailures++;
                    continue;
                }

                if (!candidateDedupe.TryAdd(candidate))
                {
                    if (diagnostics is not null)
                        diagnostics.CandidateDedupeRejects++;
                    continue;
                }

                candidates.Add(candidate);
                if (diagnostics is not null)
                    diagnostics.CandidateCreates++;
                coverageIndex.Add(candidate);
            }

            diagnostics?.LogStep(
                $"coverage-loop-{round.Name}",
                roundStopwatch,
                $"targets={targets.Count} added={candidates.Count - additionsBeforeRound} total={candidates.Count}");

            if (roundIndex > 0 && candidates.Count == additionsBeforeRound)
                break;
        }
    }

    private static void AddBlockWalkableSurfaceCandidates(
        IReadOnlyList<FishableSurfaceBlock> fishableBlocks,
        IReadOnlyList<ExtractedSceneTriangle> walkableSurfaces,
        ScanQueryCache queryCache,
        CandidateCoverageIndex coverageIndex,
        CandidateDedupeIndex candidateDedupe,
        List<CandidateScratch> candidates,
        CancellationToken cancellationToken,
        CandidateGenerationDiagnostics? diagnostics)
    {
        if (fishableBlocks.Count == 0 || walkableSurfaces.Count == 0)
            return;

        var blockIndex = new FishableBlockIndex(fishableBlocks);
        var facingHints = new RayDropFacingHintIndex();
        var sampledPoints = new HashSet<WalkableCandidateStartKey>();
        foreach (var walkableSurface in walkableSurfaces)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!blockIndex.MayOverlap(walkableSurface, MaximumCandidateDistanceFromFishableSurface))
                continue;

            foreach (var position in EnumerateTriangleSamples(walkableSurface, WalkableBlockCandidateSampleSpacing))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (candidates.Count >= MaxCandidates)
                    return;

                if (!sampledPoints.Add(WalkableCandidateStartKey.From(position)))
                    continue;

                var nearbyBlocks = blockIndex.FindNear(position);
                if (nearbyBlocks.Count == 0)
                    continue;

                if (!queryCache.HasStandingClearance(position))
                {
                    if (diagnostics is not null)
                        diagnostics.WalkableRejects++;
                    continue;
                }

                if (diagnostics is not null)
                    diagnostics.CandidateCreateAttempts++;
                if (!TryCreateRayDropCandidateForWalkableSample(position, nearbyBlocks, queryCache, facingHints, out var candidate))
                {
                    if (diagnostics is not null)
                        diagnostics.CandidateCreateFailures++;
                    continue;
                }

                if (!candidateDedupe.TryAdd(candidate))
                {
                    if (diagnostics is not null)
                        diagnostics.CandidateDedupeRejects++;
                    continue;
                }

                candidates.Add(candidate);
                coverageIndex.Add(candidate);
                if (diagnostics is not null)
                    diagnostics.CandidateCreates++;
            }
        }
    }

    private static IEnumerable<float> EnumerateCandidateFishableRayOffsets()
    {
        yield return CandidateFishableRayMaxDistance;
    }

    private static bool TryCreateRayDropCandidateForWalkableSample(
        uint territoryId,
        TriangleIndex fishableIndex,
        IReadOnlyDictionary<ExtractedSceneTriangle, string> fishableSurfaceGroupIds,
        Vector3 walkablePoint,
        ScanQueryCache queryCache,
        RayDropFacingHintIndex facingHints,
        out CandidateScratch candidate)
    {
        candidate = default;
        DirectionalRayDropHit? Probe(float angle)
        {
            var direction = DirectionFromAngle(angle);
            var rayPoint = CreateProbePoint(walkablePoint, direction, CandidateFishableRayMaxDistance) + new Vector3(0f, CandidateFishableRayHeight, 0f);
            var result = TryFindRayDropFishableTarget(
                territoryId,
                rayPoint,
                fishableIndex,
                fishableSurfaceGroupIds,
                queryCache,
                out var target);
            if (result == RayDropProbeResult.Miss)
                return null;

            return new DirectionalRayDropHit(
                NormalizeAngle(angle),
                direction,
                target.SurfaceGroupId,
                target.Point);
        }

        var facingHint = facingHints.TryFindBest(walkablePoint, out var resolvedHint)
            ? resolvedHint
            : (RayDropFacingHint?)null;
        if (!TryResolveBestRayDropFacing(
                Probe,
                hit => queryCache.HasClearFishableAccess(walkablePoint, hit.FishablePoint),
                facingHint,
                out var hit,
                out var sector))
            return false;

        if (sector is not null)
            facingHints.Add(walkablePoint, sector);
        candidate = new CandidateScratch(walkablePoint, AngleMath.NormalizeRotation(hit.Angle), hit.SurfaceGroupId);
        return true;
    }

    private static bool TryCreateRayDropCandidateForWalkableSample(
        Vector3 walkablePoint,
        IReadOnlyList<FishableSurfaceBlock> nearbyBlocks,
        ScanQueryCache queryCache,
        RayDropFacingHintIndex facingHints,
        out CandidateScratch candidate)
    {
        candidate = default;
        DirectionalRayDropHit? Probe(float angle)
        {
            var direction = DirectionFromAngle(angle);
            var rayPoint = CreateProbePoint(walkablePoint, direction, CandidateFishableRayMaxDistance) + new Vector3(0f, CandidateFishableRayHeight, 0f);
            var result = TryFindRayDropFishableTarget(rayPoint, walkablePoint, nearbyBlocks, queryCache, out var target);
            if (result == RayDropProbeResult.Miss)
                return null;

            return new DirectionalRayDropHit(
                NormalizeAngle(angle),
                direction,
                target.Block.SurfaceGroupId,
                target.Point);
        }

        var facingHint = facingHints.TryFindBest(walkablePoint, out var resolvedHint)
            ? resolvedHint
            : (RayDropFacingHint?)null;
        if (!TryResolveBestRayDropFacing(
                Probe,
                hit => queryCache.HasClearFishableAccess(walkablePoint, hit.FishablePoint),
                facingHint,
                out var hit,
                out var sector))
            return false;

        if (sector is not null)
            facingHints.Add(walkablePoint, sector);
        candidate = new CandidateScratch(walkablePoint, AngleMath.NormalizeRotation(hit.Angle), hit.SurfaceGroupId);
        return true;
    }

    private static bool TryResolveBestRayDropFacing(
        Func<float, DirectionalRayDropHit?> probe,
        Func<DirectionalRayDropHit, bool> isUsable,
        RayDropFacingHint? facingHint,
        out DirectionalRayDropHit selected,
        out DirectionalRayDropSector? selectedSector)
    {
        selected = default;
        selectedSector = null;
        if (facingHint is { } hint)
        {
            var hintHits = new List<DirectionalRayDropHit>(8);
            var hintTestedAngles = new HashSet<int>();
            ProbeAngles(probe, hintHits, hintTestedAngles, GetRayDropFacingHintProbeAngles(hint));
            if (hintHits.Count > 0
                && TryResolveBestRayDropSector(probe, isUsable, hintHits, hintTestedAngles, out selected, out selectedSector))
                return true;
        }

        var hits = new List<DirectionalRayDropHit>(12);
        var testedAngles = new HashSet<int>();

        ProbeAngles(probe, hits, testedAngles, [0f, MathF.PI / 2f, MathF.PI, MathF.PI * 3f / 2f]);
        ProbeAngles(probe, hits, testedAngles, [MathF.PI / 4f, MathF.PI * 3f / 4f, MathF.PI * 5f / 4f, MathF.PI * 7f / 4f]);
        if (hits.Count == 0)
            return false;

        return TryResolveBestRayDropSector(probe, isUsable, hits, testedAngles, out selected, out selectedSector);
    }

    private static bool TryResolveBestRayDropSector(
        Func<float, DirectionalRayDropHit?> probe,
        Func<DirectionalRayDropHit, bool> isUsable,
        List<DirectionalRayDropHit> hits,
        HashSet<int> testedAngles,
        out DirectionalRayDropHit selected,
        out DirectionalRayDropSector? selectedSector)
    {
        selected = default;
        selectedSector = null;
        var coarseSector = BuildDirectionalRayDropSectors(hits)
            .OrderByDescending(sector => sector.Hits.Count)
            .ThenByDescending(sector => sector.Width)
            .ThenBy(sector => sector.CenterAngle)
            .FirstOrDefault();
        if (coarseSector is null)
            return false;

        ProbeAngles(probe, hits, testedAngles, GetDirectionalRayDropSectorRefineAngles(coarseSector));

        var sector = BuildDirectionalRayDropSectors(hits)
            .Where(sector => string.Equals(sector.SurfaceGroupId, coarseSector.SurfaceGroupId, StringComparison.Ordinal)
                && sector.Hits.Any(hit => coarseSector.Hits.Any(coarseHit => AngularDistance(hit.Angle, coarseHit.Angle) <= FacingDirectionDedupeRadians)))
            .OrderByDescending(sector => sector.Hits.Count)
            .ThenByDescending(sector => sector.Width)
            .ThenBy(sector => sector.CenterAngle)
            .FirstOrDefault();
        if (sector is null)
            return false;
        selectedSector = sector;

        var sampledCenter = sector.Hits
            .OrderBy(hit => AngularDistance(hit.Angle, sector.CenterAngle))
            .ThenBy(hit => hit.Angle)
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(sampledCenter.SurfaceGroupId)
            && AngularDistance(sampledCenter.Angle, sector.CenterAngle) <= FacingDirectionDedupeRadians
            && isUsable(sampledCenter))
        {
            selected = sampledCenter;
            return true;
        }

        var centerHit = probe(sector.CenterAngle);
        if (centerHit is { } resolvedCenter
            && string.Equals(resolvedCenter.SurfaceGroupId, sector.SurfaceGroupId, StringComparison.Ordinal)
            && isUsable(resolvedCenter))
        {
            selected = resolvedCenter;
            return true;
        }

        foreach (var fallback in sector.Hits
            .OrderBy(hit => AngularDistance(hit.Angle, sector.CenterAngle))
            .ThenBy(hit => hit.Angle))
        {
            if (!isUsable(fallback))
                continue;

            selected = fallback;
            return true;
        }

        return false;
    }

    private static IReadOnlyList<float> GetRayDropFacingHintProbeAngles(RayDropFacingHint hint) =>
    [
        hint.EndAngle + CandidateFacingLocalRefineOffsetRadians,
        hint.EndAngle,
        hint.CenterAngle,
        hint.StartAngle,
        hint.StartAngle - CandidateFacingLocalRefineOffsetRadians,
    ];

    private static IReadOnlyList<float> GetDirectionalRayDropSectorRefineAngles(DirectionalRayDropSector sector) =>
    [
        sector.StartAngle - CandidateFacingLocalRefineOffsetRadians,
        sector.EndAngle + CandidateFacingLocalRefineOffsetRadians,
    ];

    private static void ProbeAngles(
        Func<float, DirectionalRayDropHit?> probe,
        List<DirectionalRayDropHit> hits,
        HashSet<int> testedAngles,
        IReadOnlyList<float> angles)
    {
        foreach (var angle in angles)
        {
            var normalized = NormalizeAngle(angle);
            if (!testedAngles.Add(QuantizeFacingAngle(normalized)))
                continue;

            var hit = probe(normalized);
            if (hit is { } resolvedHit)
                hits.Add(resolvedHit);
        }
    }

    private static IReadOnlyList<DirectionalRayDropSector> BuildDirectionalRayDropSectors(IReadOnlyList<DirectionalRayDropHit> hits)
    {
        if (hits.Count == 0)
            return [];

        var orderedHits = hits
            .OrderBy(hit => hit.Angle)
            .ToList();
        var sectorHits = new List<List<DirectionalRayDropHit>>();
        foreach (var hit in orderedHits)
        {
            if (sectorHits.Count == 0)
            {
                sectorHits.Add([hit]);
                continue;
            }

            var current = sectorHits[^1];
            var previous = current[^1];
            if (string.Equals(previous.SurfaceGroupId, hit.SurfaceGroupId, StringComparison.Ordinal)
                && AngularForwardDistance(previous.Angle, hit.Angle) <= CandidateFacingSectorMergeGapRadians)
                current.Add(hit);
            else
                sectorHits.Add([hit]);
        }

        if (sectorHits.Count > 1)
        {
            var first = sectorHits[0];
            var last = sectorHits[^1];
            if (string.Equals(first[0].SurfaceGroupId, last[^1].SurfaceGroupId, StringComparison.Ordinal)
                && AngularForwardDistance(last[^1].Angle, first[0].Angle) <= CandidateFacingSectorMergeGapRadians)
            {
                last.AddRange(first);
                sectorHits.RemoveAt(0);
            }
        }

        return sectorHits
            .Select(CreateDirectionalRayDropSector)
            .ToList();
    }

    private static DirectionalRayDropSector CreateDirectionalRayDropSector(IReadOnlyList<DirectionalRayDropHit> hits)
    {
        var ordered = hits
            .OrderBy(hit => hit.Angle)
            .ToList();
        if (ordered.Count == 1)
            return new DirectionalRayDropSector(
                ordered[0].SurfaceGroupId,
                ordered,
                ordered[0].Angle,
                ordered[0].Angle,
                ordered[0].Angle,
                0f);

        var largestGap = -1f;
        var gapStartIndex = 0;
        for (var index = 0; index < ordered.Count; index++)
        {
            var next = (index + 1) % ordered.Count;
            var gap = AngularForwardDistance(ordered[index].Angle, ordered[next].Angle);
            if (gap <= largestGap)
                continue;

            largestGap = gap;
            gapStartIndex = index;
        }

        var start = ordered[(gapStartIndex + 1) % ordered.Count].Angle;
        var end = ordered[gapStartIndex].Angle;
        if (end < start)
            end += MathF.Tau;
        var width = end - start;
        var center = NormalizeAngle(start + (width * 0.5f));
        return new DirectionalRayDropSector(
            ordered[0].SurfaceGroupId,
            ordered,
            NormalizeAngle(start),
            NormalizeAngle(end),
            center,
            width);
    }

    private static Vector2 DirectionFromAngle(float angle) => new(MathF.Sin(angle), MathF.Cos(angle));

    private static float NormalizeAngle(float angle)
    {
        angle %= MathF.Tau;
        return angle < 0f ? angle + MathF.Tau : angle;
    }

    private static float AngularForwardDistance(float from, float to)
    {
        var distance = NormalizeAngle(to) - NormalizeAngle(from);
        return distance < 0f ? distance + MathF.Tau : distance;
    }

    private static float AngularDistance(float left, float right)
    {
        var distance = MathF.Abs(NormalizeAngle(left) - NormalizeAngle(right));
        return MathF.Min(distance, MathF.Tau - distance);
    }

    private static int QuantizeFacingAngle(float angle) =>
        (int)MathF.Round(NormalizeAngle(angle) / FacingDirectionDedupeRadians, MidpointRounding.AwayFromZero);

    private static RayDropProbeResult TryFindRayDropFishableTarget(
        uint territoryId,
        Vector3 rayPoint,
        TriangleIndex fishableIndex,
        IReadOnlyDictionary<ExtractedSceneTriangle, string> fishableSurfaceGroupIds,
        ScanQueryCache queryCache,
        out RayDropFishableTarget target)
    {
        target = default;
        var hitFishable = fishableIndex.TryFindHighestContainingPointBelow(
            rayPoint,
            WalkableFishableMinimumVerticalDelta,
            out var fishableTriangle,
            out var fishablePoint);

        if (queryCache.TryFindHighestCollisionBelow(rayPoint, out var collisionPoint)
            && (!hitFishable || collisionPoint.Y >= fishablePoint.Y - SightLineIntersectionEpsilon))
            return RayDropProbeResult.Miss;

        if (!hitFishable)
            return RayDropProbeResult.Miss;

        var surfaceGroupId = fishableSurfaceGroupIds.TryGetValue(fishableTriangle, out var resolvedSurfaceGroupId)
            ? resolvedSurfaceGroupId
            : CreateFallbackFishableSurfaceGroupId(territoryId, fishablePoint);
        target = new RayDropFishableTarget(fishablePoint, fishableTriangle, surfaceGroupId);
        return RayDropProbeResult.HitFishable;
    }

    private static string CreateFallbackFishableSurfaceGroupId(uint territoryId, Vector3 point) =>
        $"t{territoryId}_surface_{QuantizeFallbackSurfaceGroup(point.X)}_{QuantizeFallbackSurfaceGroup(point.Y)}_{QuantizeFallbackSurfaceGroup(point.Z)}";

    private static int QuantizeFallbackSurfaceGroup(float value) =>
        (int)MathF.Floor(value / CandidateCoverageCellSize);

    private static RayDropProbeResult TryFindRayDropFishableTarget(
        Vector3 rayPoint,
        Vector3 walkablePoint,
        IReadOnlyList<FishableSurfaceBlock> nearbyBlocks,
        ScanQueryCache queryCache,
        out FishableCoverageTarget target)
    {
        target = default;
        ExtractedSceneTriangle bestTriangle = default;
        FishableSurfaceBlock? bestBlock = null;
        var bestFishablePoint = default(Vector3);
        var bestVerticalDistance = float.MaxValue;
        foreach (var block in nearbyBlocks)
        {
            if (!block.Bounds.Contains(rayPoint))
                continue;

            foreach (var triangle in block.Triangles)
            {
                if (!TryProjectYOnTriangleXz(triangle, rayPoint.X, rayPoint.Z, out var y))
                    continue;

                var fishablePoint = new Vector3(rayPoint.X, y, rayPoint.Z);
                var verticalDistance = rayPoint.Y - fishablePoint.Y;
                if (verticalDistance < WalkableFishableMinimumVerticalDelta
                    || verticalDistance >= bestVerticalDistance)
                    continue;

                bestVerticalDistance = verticalDistance;
                bestFishablePoint = fishablePoint;
                bestTriangle = triangle;
                bestBlock = block;
            }
        }

        if (queryCache.TryFindHighestCollisionBelow(rayPoint, out var collisionPoint)
            && (bestBlock is null || collisionPoint.Y >= bestFishablePoint.Y - SightLineIntersectionEpsilon))
            return RayDropProbeResult.Miss;

        if (bestBlock is not { } resultBlock)
            return RayDropProbeResult.Miss;

        if (!TryGetNearestFishableBoundaryDirection(bestFishablePoint, resultBlock, out var preferredDirection))
            TryGetHorizontalDirection(bestFishablePoint - walkablePoint, out preferredDirection);

        target = new FishableCoverageTarget(
            bestFishablePoint,
            bestTriangle,
            resultBlock,
            preferredDirection,
            preferredDirection.LengthSquared() > 0.0001f,
            FishableCoverageTargetKind.WalkableSample);
        return RayDropProbeResult.HitFishable;
    }

    private static IEnumerable<FishableCoverageTarget> EnumerateFishableCoverageTargets(
        IReadOnlyList<FishableSurfaceBlock> fishableBlocks,
        float spacing,
        TriangleIndex walkableIndex)
    {
        var keys = new HashSet<FishableCoverageTargetKey>();
        foreach (var block in fishableBlocks)
        {
            if (block.BoundarySegments.Count > 0)
            {
                foreach (var target in EnumerateFishableBoundaryTargets(block, spacing, walkableIndex))
                {
                    var key = FishableCoverageTargetKey.From(block.SurfaceGroupId, target.Point);
                    if (keys.Add(key))
                        yield return target;
                }
            }

            if (keys.Any(key => key.SurfaceGroupId == block.SurfaceGroupId))
                continue;

            foreach (var triangle in block.Triangles)
            {
                var yielded = false;
                foreach (var point in EnumerateTriangleSamples(triangle, spacing))
                {
                    var key = FishableCoverageTargetKey.From(block.SurfaceGroupId, point);
                    if (IsFishablePointBlockedByWalkable(point, walkableIndex)
                        || !keys.Add(key))
                        continue;

                    yielded = true;
                    yield return new FishableCoverageTarget(point, triangle, block, default, false, FishableCoverageTargetKind.InteriorFallback);
                }

                if (yielded)
                    continue;

                var fallbackKey = FishableCoverageTargetKey.From(block.SurfaceGroupId, triangle.Centroid);
                if (!IsFishablePointBlockedByWalkable(triangle.Centroid, walkableIndex)
                    && keys.Add(fallbackKey))
                    yield return new FishableCoverageTarget(triangle.Centroid, triangle, block, default, false, FishableCoverageTargetKind.InteriorFallback);
            }
        }
    }

    private static IEnumerable<FishableCoverageTarget> EnumerateFishableBoundaryTargets(
        FishableSurfaceBlock block,
        float spacing,
        TriangleIndex walkableIndex)
    {
        foreach (var segment in block.BoundarySegments)
        {
            var stepCount = Math.Max(1, (int)MathF.Ceiling(segment.Length / spacing));
            for (var index = 0; index <= stepCount; index++)
            {
                var t = stepCount == 0 ? 0f : index / (float)stepCount;
                var point = Vector3.Lerp(segment.Start, segment.End, t);
                if (IsFishablePointBlockedByWalkable(point, walkableIndex))
                    continue;

                var inwardDirection = CalculateSmoothedFishableBoundaryDirection(point, segment.InwardDirection, block.BoundarySegments);
                yield return new FishableCoverageTarget(point, segment.Triangle, block, inwardDirection, true, FishableCoverageTargetKind.Boundary);
            }
        }
    }

    private static bool TryGetNearestFishableBoundaryDirection(
        Vector3 point,
        FishableSurfaceBlock block,
        out Vector2 direction)
    {
        direction = default;
        if (block.BoundarySegments.Count == 0)
            return false;

        var point2 = new Vector2(point.X, point.Z);
        FishableBoundarySegment? nearest = null;
        var nearestDistance = float.MaxValue;
        foreach (var segment in block.BoundarySegments)
        {
            var distance = DistanceToSegment(
                point2,
                new Vector2(segment.Start.X, segment.Start.Z),
                new Vector2(segment.End.X, segment.End.Z));
            if (distance >= nearestDistance)
                continue;

            nearestDistance = distance;
            nearest = segment;
        }

        if (nearest is not { } nearestSegment)
            return false;

        var nearestPoint = ClosestPointOnSegment(
            point2,
            new Vector2(nearestSegment.Start.X, nearestSegment.Start.Z),
            new Vector2(nearestSegment.End.X, nearestSegment.End.Z));
        direction = CalculateSmoothedFishableBoundaryDirection(
            new Vector3(nearestPoint.X, point.Y, nearestPoint.Y),
            nearestSegment.InwardDirection,
            block.BoundarySegments);
        return direction.LengthSquared() > 0.0001f;
    }

    private static Vector2 CalculateSmoothedFishableBoundaryDirection(
        Vector3 point,
        Vector2 fallbackDirection,
        IReadOnlyList<FishableBoundarySegment> segments)
    {
        if (TryFitFishableSideDirection(point, fallbackDirection, segments, out var fittedDirection))
            return fittedDirection;

        var point2 = new Vector2(point.X, point.Z);
        var sum = Vector2.Zero;
        foreach (var segment in segments)
        {
            var distance = DistanceToSegment(
                point2,
                new Vector2(segment.Start.X, segment.Start.Z),
                new Vector2(segment.End.X, segment.End.Z));
            if (distance > FishableBoundaryNormalSmoothRadius)
                continue;

            var direction = segment.InwardDirection;
            if (sum.LengthSquared() > 0.0001f && Vector2.Dot(sum, direction) < 0f)
                direction = -direction;

            var weight = segment.Length / MathF.Max(0.25f, distance + 0.25f);
            sum += direction * weight;
        }

        return sum.LengthSquared() > 0.0001f
            ? Vector2.Normalize(sum)
            : fallbackDirection;
    }

    private static bool TryFitFishableSideDirection(
        Vector3 point,
        Vector2 fallbackDirection,
        IReadOnlyList<FishableBoundarySegment> segments,
        out Vector2 direction)
    {
        direction = default;
        var point2 = new Vector2(point.X, point.Z);
        var totalWeight = 0f;
        var mean = Vector2.Zero;
        foreach (var segment in segments)
        {
            var start = new Vector2(segment.Start.X, segment.Start.Z);
            var end = new Vector2(segment.End.X, segment.End.Z);
            var distance = DistanceToSegment(point2, start, end);
            if (distance > FishableBoundaryNormalSmoothRadius)
                continue;

            var weight = segment.Length / MathF.Max(0.25f, distance + 0.25f);
            var midpoint = (start + end) * 0.5f;
            mean += midpoint * weight;
            totalWeight += weight;
        }

        if (totalWeight <= 0.0001f)
            return false;

        mean /= totalWeight;
        var covarianceXx = 0f;
        var covarianceXz = 0f;
        var covarianceZz = 0f;
        foreach (var segment in segments)
        {
            var start = new Vector2(segment.Start.X, segment.Start.Z);
            var end = new Vector2(segment.End.X, segment.End.Z);
            var distance = DistanceToSegment(point2, start, end);
            if (distance > FishableBoundaryNormalSmoothRadius)
                continue;

            var weight = segment.Length / MathF.Max(0.25f, distance + 0.25f);
            AddWeightedBoundaryFitPoint(start, mean, weight, ref covarianceXx, ref covarianceXz, ref covarianceZz);
            AddWeightedBoundaryFitPoint(end, mean, weight, ref covarianceXx, ref covarianceXz, ref covarianceZz);
        }

        var halfDifference = (covarianceXx - covarianceZz) * 0.5f;
        var radius = MathF.Sqrt((halfDifference * halfDifference) + (covarianceXz * covarianceXz));
        var lambda = ((covarianceXx + covarianceZz) * 0.5f) + radius;
        var tangent = MathF.Abs(covarianceXz) > 0.0001f
            ? new Vector2(covarianceXz, lambda - covarianceXx)
            : covarianceXx >= covarianceZz
                ? Vector2.UnitX
                : Vector2.UnitY;
        if (tangent.LengthSquared() <= 0.0001f)
            return false;

        tangent = Vector2.Normalize(tangent);
        direction = new Vector2(-tangent.Y, tangent.X);
        if (direction.LengthSquared() <= 0.0001f)
            return false;

        direction = Vector2.Normalize(direction);
        if (fallbackDirection.LengthSquared() > 0.0001f
            && Vector2.Dot(direction, fallbackDirection) < 0f)
            direction = -direction;

        return true;
    }

    private static void AddWeightedBoundaryFitPoint(
        Vector2 point,
        Vector2 mean,
        float weight,
        ref float covarianceXx,
        ref float covarianceXz,
        ref float covarianceZz)
    {
        var delta = point - mean;
        covarianceXx += delta.X * delta.X * weight;
        covarianceXz += delta.X * delta.Y * weight;
        covarianceZz += delta.Y * delta.Y * weight;
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
        ScanQueryCache queryCache,
        CandidateDedupeIndex candidateDedupe,
        out CandidateScratch candidate,
        CandidateGenerationDiagnostics? diagnostics)
    {
        candidate = default;
        foreach (var probe in EnumerateCandidateSearchProbes(target.Point, round.DirectionCount))
        {
            if (diagnostics is not null)
                diagnostics.ProbeAttempts++;

            var walkablePoints = queryCache.GetCandidateWalkablePoints(probe);
            if (walkablePoints.Count == 0)
            {
                if (diagnostics is not null)
                    diagnostics.WalkableMisses++;
                continue;
            }

            foreach (var position in OrderCandidateWalkablePoints(target.Point, walkablePoints))
            {
                if (diagnostics is not null)
                    diagnostics.WalkableLayerAttempts++;
                if (!IsCandidateWalkablePoint(target, position, queryCache))
                {
                    if (diagnostics is not null)
                        diagnostics.WalkableRejects++;
                    continue;
                }

                if (!TryResolveLegalFacingRotation(
                        position,
                        target,
                        queryCache,
                        out var rotation))
                {
                    if (diagnostics is not null)
                        diagnostics.FacingRejects++;
                    continue;
                }

                var candidateScratch = new CandidateScratch(position, rotation, target.Block.SurfaceGroupId);
                if (candidateDedupe.ContainsNear(candidateScratch.Position))
                {
                    if (diagnostics is not null)
                        diagnostics.CandidateDedupeRejects++;
                    continue;
                }

                candidate = candidateScratch;
                return true;
            }
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

    private static IEnumerable<Vector3> OrderCandidateWalkablePoints(
        Vector3 targetFishablePoint,
        IReadOnlyList<Vector3> walkablePoints)
    {
        return walkablePoints
            .OrderBy(point => MathF.Abs(point.Y - targetFishablePoint.Y))
            .ThenByDescending(point => point.Y);
    }

    private static bool IsCandidateWalkablePoint(
        FishableCoverageTarget target,
        Vector3 candidatePoint,
        ScanQueryCache queryCache)
    {
        return queryCache.HasStandingClearance(candidatePoint)
            && IsFishablePointUsableForCandidate(target.Block, candidatePoint, target.Point, queryCache);
    }

    private static bool TryResolveLegalFacingRotation(
        Vector3 position,
        FishableCoverageTarget target,
        ScanQueryCache queryCache,
        out float rotation)
    {
        rotation = 0f;
        var baseDirections = new List<Vector2>();
        if (TryGetPreferredFishableWallDirection(position, target, out var preferredDirection))
            baseDirections.Add(preferredDirection);
        TryAddBaseDirection(baseDirections, target.Point - position);
        TryAddBaseDirection(baseDirections, target.Triangle.Centroid - position);
        if (TryFindLocalFishableCenter(position, target.Triangle, target.Point, target.Block, out var localFishableCenter))
            TryAddBaseDirection(baseDirections, localFishableCenter - position);
        TryAddBaseDirection(baseDirections, target.Block.Center - position);

        var testedDirections = new HashSet<int>();
        if (baseDirections.Count > 0
            && TryResolveFacingRotation(
                position,
                target,
                queryCache,
                [baseDirections[0]],
                CandidateFacingPrimaryAngleOffsets,
                testedDirections,
                out rotation))
            return true;

        if (TryResolveFacingRotation(
                position,
                target,
                queryCache,
                baseDirections,
                CandidateFacingPrimaryAngleOffsets,
                testedDirections,
                out rotation))
            return true;

        if (TryResolveFacingRotation(
                position,
                target,
                queryCache,
                baseDirections,
                CandidateFacingAngleOffsets,
                testedDirections,
                out rotation))
            return true;

        const int fullSweepSteps = 16;
        for (var index = 0; index < fullSweepSteps; index++)
        {
            var angle = MathF.Tau * index / fullSweepSteps;
            var direction = new Vector2(MathF.Sin(angle), MathF.Cos(angle));
            if (!TryAddTestedDirection(testedDirections, direction)
                || !IsLegalFacingDirection(position, direction, target, queryCache))
                continue;

            rotation = AngleMath.NormalizeRotation(angle);
            return true;
        }

        return false;
    }

    private static bool TryGetPreferredFishableWallDirection(
        Vector3 position,
        FishableCoverageTarget target,
        out Vector2 direction)
    {
        direction = default;
        if (!target.HasPreferredFacingDirection
            || target.PreferredFacingDirection.LengthSquared() <= 0.0001f)
            return false;

        direction = Vector2.Normalize(target.PreferredFacingDirection);
        if (target.Kind != FishableCoverageTargetKind.Boundary)
            return true;

        var candidateOffset = new Vector2(position.X - target.Point.X, position.Z - target.Point.Z);
        if (candidateOffset.LengthSquared() > 0.0001f
            && Vector2.Dot(candidateOffset, direction) > 0f)
            direction = -direction;

        return true;
    }

    private static bool TryResolveFacingRotation(
        Vector3 position,
        FishableCoverageTarget target,
        ScanQueryCache queryCache,
        IReadOnlyList<Vector2> baseDirections,
        IReadOnlyList<float> angleOffsets,
        HashSet<int> testedDirections,
        out float rotation)
    {
        foreach (var baseDirection in baseDirections)
        {
            foreach (var offset in angleOffsets)
            {
                var direction = RotateDirection(baseDirection, offset);
                if (!TryAddTestedDirection(testedDirections, direction)
                    || !IsLegalFacingDirection(position, direction, target, queryCache))
                    continue;

                rotation = AngleMath.NormalizeRotation(MathF.Atan2(direction.X, direction.Y));
                return true;
            }
        }

        rotation = 0f;
        return false;
    }

    private static bool IsLegalFacingDirection(
        Vector3 position,
        Vector2 direction,
        FishableCoverageTarget target,
        ScanQueryCache queryCache)
    {
        if (TryGetHorizontalDirection(target.Point - position, out var targetDirection)
            && Vector2.Dot(targetDirection, direction) <= 0f)
            return false;

        foreach (var offset in EnumerateCandidateFishableRayOffsets())
        {
            var probe = CreateProbePoint(position, direction, offset);
            if (!queryCache.TryFindFacingFishable(position, target, probe, out var fishablePoint, out var blocked))
            {
                if (blocked)
                    return false;
                continue;
            }

            if (GetValidFacingFishableRejectReason(position, direction, fishablePoint, target, queryCache) != ValidFacingFishableRejectReason.None)
                continue;

            return true;
        }

        return false;
    }

    private static ValidFacingFishableRejectReason GetValidFacingFishableRejectReason(
        Vector3 position,
        Vector2 direction,
        Vector3 fishablePoint,
        FishableCoverageTarget target,
        ScanQueryCache queryCache)
    {
        if (!TryGetHorizontalDirection(fishablePoint - position, out var fishableDirection)
            || Vector2.Dot(fishableDirection, direction) <= 0f)
            return ValidFacingFishableRejectReason.Behind;

        if (!IsCandidateFishableForWalkablePoint(position, fishablePoint))
            return ValidFacingFishableRejectReason.Distance;

        if (queryCache.IsFishableCoveredByWalkable(fishablePoint))
            return ValidFacingFishableRejectReason.Covered;

        if (!IsFishablePointSupportedByCandidate(fishablePoint, position, target.Block))
            return ValidFacingFishableRejectReason.Unsupported;

        return queryCache.HasClearFishableAccess(position, fishablePoint)
            ? ValidFacingFishableRejectReason.None
            : ValidFacingFishableRejectReason.AccessBlocked;
    }

    private static bool IsFishablePointUsableForCandidate(
        FishableSurfaceBlock block,
        Vector3 candidatePoint,
        Vector3 fishablePoint,
        ScanQueryCache queryCache)
    {
        return IsCandidateFishableForWalkablePoint(candidatePoint, fishablePoint)
            && !queryCache.IsFishableCoveredByWalkable(fishablePoint)
            && IsFishablePointSupportedByCandidate(fishablePoint, candidatePoint, block);
    }

    private static bool IsFishablePointSupportedByCandidate(
        Vector3 fishablePoint,
        Vector3 candidatePoint,
        FishableSurfaceBlock block)
    {
        return IsFishablePointNearBlockBoundary(fishablePoint, block)
            || IsFishablePointAboveWalkableProjection(candidatePoint, fishablePoint)
            || block.Index.ContainsPoint(candidatePoint);
    }

    private static bool IsFishablePointNearBlockBoundary(
        Vector3 fishablePoint,
        FishableSurfaceBlock block)
    {
        if (block.BoundarySegments.Count == 0)
            return true;

        var point = new Vector2(fishablePoint.X, fishablePoint.Z);
        foreach (var segment in block.BoundarySegments)
        {
            var distance = DistanceToSegment(
                point,
                new Vector2(segment.Start.X, segment.Start.Z),
                new Vector2(segment.End.X, segment.End.Z));
            if (distance <= MaximumCandidateDistanceFromFishableSurface)
                return true;
        }

        return false;
    }

    private static bool IsCandidateFishableForWalkablePoint(Vector3 walkablePoint, Vector3 fishablePoint)
    {
        return IsWalkablePointNearFishableBlock(walkablePoint, fishablePoint);
    }

    private static bool IsWalkablePointNearFishableBlock(Vector3 walkablePoint, Vector3 fishablePoint)
    {
        var horizontalDistance = HorizontalDistance(walkablePoint, fishablePoint);
        if (horizontalDistance > 0f)
            return horizontalDistance <= MaximumCandidateDistanceFromFishableSurface;

        return IsFishablePointAboveWalkableProjection(walkablePoint, fishablePoint);
    }

    private static bool IsFishablePointAboveWalkableProjection(Vector3 walkablePoint, Vector3 fishablePoint)
    {
        if (HorizontalDistance(walkablePoint, fishablePoint) > 0f)
            return false;

        var verticalDelta = fishablePoint.Y - walkablePoint.Y;
        return verticalDelta > WalkableFishableMinimumVerticalDelta
            && verticalDelta <= OpenFishableClearanceMeters;
    }

    private static bool IsCollisionBlocker(ExtractedSceneTriangle triangle)
    {
        return !triangle.IsFishable
            && !triangle.Flags.HasFlag(ScenePrimitiveFlags.FlyThrough);
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

    private static bool SegmentAabbMayIntersectTriangle(
        Vector3 start,
        Vector3 end,
        ExtractedSceneTriangle triangle)
    {
        var segmentMinX = MathF.Min(start.X, end.X) - SightLineIntersectionEpsilon;
        var segmentMaxX = MathF.Max(start.X, end.X) + SightLineIntersectionEpsilon;
        var segmentMinY = MathF.Min(start.Y, end.Y) - SightLineIntersectionEpsilon;
        var segmentMaxY = MathF.Max(start.Y, end.Y) + SightLineIntersectionEpsilon;
        var segmentMinZ = MathF.Min(start.Z, end.Z) - SightLineIntersectionEpsilon;
        var segmentMaxZ = MathF.Max(start.Z, end.Z) + SightLineIntersectionEpsilon;

        var triangleMinX = MathF.Min(triangle.A.X, MathF.Min(triangle.B.X, triangle.C.X));
        var triangleMaxX = MathF.Max(triangle.A.X, MathF.Max(triangle.B.X, triangle.C.X));
        var triangleMinZ = MathF.Min(triangle.A.Z, MathF.Min(triangle.B.Z, triangle.C.Z));
        var triangleMaxZ = MathF.Max(triangle.A.Z, MathF.Max(triangle.B.Z, triangle.C.Z));

        return segmentMaxX >= triangleMinX
            && segmentMinX <= triangleMaxX
            && segmentMaxY >= TriangleMinY(triangle)
            && segmentMinY <= TriangleMaxY(triangle)
            && segmentMaxZ >= triangleMinZ
            && segmentMinZ <= triangleMaxZ;
    }

    private static bool SegmentIntersectsTriangle(
        Vector3 start,
        Vector3 end,
        ExtractedSceneTriangle triangle)
    {
        var direction = end - start;
        var edge1 = triangle.B - triangle.A;
        var edge2 = triangle.C - triangle.A;
        var pVector = Vector3.Cross(direction, edge2);
        var determinant = Vector3.Dot(edge1, pVector);
        if (MathF.Abs(determinant) <= SightLineIntersectionEpsilon)
            return false;

        var inverseDeterminant = 1f / determinant;
        var tVector = start - triangle.A;
        var u = Vector3.Dot(tVector, pVector) * inverseDeterminant;
        if (u < -SightLineIntersectionEpsilon || u > 1f + SightLineIntersectionEpsilon)
            return false;

        var qVector = Vector3.Cross(tVector, edge1);
        var v = Vector3.Dot(direction, qVector) * inverseDeterminant;
        if (v < -SightLineIntersectionEpsilon || u + v > 1f + SightLineIntersectionEpsilon)
            return false;

        var t = Vector3.Dot(edge2, qVector) * inverseDeterminant;
        return t > SightLineIntersectionEpsilon && t < 1f - SightLineIntersectionEpsilon;
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

    private void LogDebugCandidateGeneration(
        uint territoryId,
        Vector3 playerPosition,
        IReadOnlyList<ExtractedSceneTriangle> fishableTriangles,
        IReadOnlyList<ExtractedSceneTriangle> walkableTriangles,
        IReadOnlyList<ExtractedSceneTriangle> collisionBlockerTriangles)
    {
        if (fishableTriangles.Count == 0 || walkableTriangles.Count == 0)
        {
            pluginLog.Debug(
                "FPG nearby candidate diagnostics: skipped fishableTriangles={FishableTriangles} walkableTriangles={WalkableTriangles}",
                fishableTriangles.Count,
                walkableTriangles.Count);
            return;
        }

        var fishableIndex = new TriangleIndex(fishableTriangles, SurfaceIndexCellSize);
        var walkableSurfaces = SelectPotentialWalkableSurfaces(walkableTriangles, fishableIndex);
        if (walkableSurfaces.Count == 0)
        {
            pluginLog.Debug(
                "FPG nearby candidate diagnostics: no walkable surfaces within fishable horizontal range input={WalkableTriangles} maxHorizontalDistance={Distance:F1}",
                walkableTriangles.Count,
                MaximumCandidateDistanceFromFishableSurface);
            return;
        }

        var walkableIndex = new TriangleIndex(walkableSurfaces, SurfaceIndexCellSize);
        var fishableSurfaceGroupIds = BuildFishableSurfaceGroupIds(territoryId, fishableTriangles);
        var surfaceGroupCount = fishableSurfaceGroupIds.Values.Distinct(StringComparer.Ordinal).Count();
        var relevantCollisionBlockers = FilterRelevantCollisionBlockers(fishableTriangles, collisionBlockerTriangles);
        var collisionBlockerIndex = new TriangleIndex(relevantCollisionBlockers, SurfaceIndexCellSize);
        var nearestWalkableDistance = walkableSurfaces
            .Select(triangle => HorizontalDistanceToTriangle(playerPosition, triangle))
            .DefaultIfEmpty(float.MaxValue)
            .Min();

        pluginLog.Debug(
            "FPG nearby candidate diagnostics: territory={TerritoryId} fishableSurfaceGroups={FishableSurfaceGroups} walkableSurfaces={WalkableSurfaces} nearestWalkableDistance={NearestWalkableDistance:F1} fishableIndexCells={FishableIndexCells} walkableIndexCells={WalkableIndexCells} collisionBlockers={CollisionBlockers} relevantCollisionBlockers={RelevantCollisionBlockers} collisionIndexCells={CollisionIndexCells} rayHeight={RayHeight:F1} rayDistance={RayDistance:F1} directionPlan=\"xz hint + 8-way sector + local\" standingClearance={StandingClearance:F1}",
            territoryId,
            surfaceGroupCount,
            walkableSurfaces.Count,
            nearestWalkableDistance,
            fishableIndex.CellCount,
            walkableIndex.CellCount,
            collisionBlockerTriangles.Count,
            relevantCollisionBlockers.Count,
            collisionBlockerIndex.CellCount,
            CandidateFishableRayHeight,
            CandidateFishableRayMaxDistance,
            WalkableStandingClearanceMeters);
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

    private static CandidateTargetDebugSummary AnalyzeCandidateTarget(
        FishableCoverageTarget target,
        FishableCoverageRound round,
        ScanQueryCache queryCache)
    {
        var summary = new CandidateTargetDebugSummary
        {
            FishableCovered = queryCache.IsFishableCoveredByWalkable(target.Point),
        };

        foreach (var probe in EnumerateCandidateSearchProbes(target.Point, round.DirectionCount))
        {
            summary.Probes++;
            var walkablePoints = queryCache.GetCandidateWalkablePoints(probe);
            if (walkablePoints.Count == 0)
            {
                summary.NoWalkableProbes++;
                continue;
            }

            summary.MaxStackDepth = Math.Max(summary.MaxStackDepth, walkablePoints.Count);
            if (summary.FirstStackYValues.Count == 0)
            {
                summary.FirstStackProbe = probe;
                summary.FirstStackYValues = walkablePoints
                    .Take(MaxDebugWalkableLayerValues)
                    .Select(point => point.Y)
                    .ToList();
            }

            foreach (var position in OrderCandidateWalkablePoints(target.Point, walkablePoints))
            {
                summary.LayerAttempts++;
                if (!queryCache.HasStandingClearance(position))
                {
                    summary.StandingRejects++;
                    continue;
                }

                if (!IsFishablePointUsableForCandidate(target.Block, position, target.Point, queryCache))
                {
                    summary.DistanceRejects++;
                    continue;
                }

                if (TryResolveLegalFacingRotation(position, target, queryCache, out var rotation))
                {
                    summary.Successes++;
                    summary.FirstSuccessPosition ??= position;
                    summary.FirstSuccessRotation ??= rotation;
                    continue;
                }

                summary.FacingRejects++;
                if (summary.FirstFacingSummary is not null)
                    continue;

                summary.FirstFacingRejectPosition = position;
                summary.FirstFacingSummary = AnalyzeFacingFailure(position, target, queryCache);
            }
        }

        return summary;
    }

    private static FacingDebugSummary AnalyzeFacingFailure(
        Vector3 position,
        FishableCoverageTarget target,
        ScanQueryCache queryCache)
    {
        var summary = new FacingDebugSummary();
        foreach (var direction in EnumerateDebugFacingDirections(position, target))
        {
            summary.Directions++;
            if (TryGetHorizontalDirection(target.Point - position, out var targetDirection)
                && Vector2.Dot(targetDirection, direction) <= 0f)
            {
                summary.TargetBehindDirections++;
                continue;
            }

            foreach (var offset in EnumerateCandidateFishableRayOffsets())
            {
                summary.ProbeAttempts++;
                var probe = CreateProbePoint(position, direction, offset);
                if (!queryCache.TryFindFacingFishable(position, target, probe, out var fishablePoint, out var blocked))
                {
                    if (blocked)
                    {
                        summary.AccessBlocked++;
                        break;
                    }

                    summary.NoFishable++;
                    continue;
                }

                summary.FirstFishable ??= fishablePoint;
                switch (GetValidFacingFishableRejectReason(position, direction, fishablePoint, target, queryCache))
                {
                    case ValidFacingFishableRejectReason.None:
                        summary.Successes++;
                        break;
                    case ValidFacingFishableRejectReason.Behind:
                        summary.FishableBehind++;
                        break;
                    case ValidFacingFishableRejectReason.Distance:
                        summary.DistanceRejects++;
                        break;
                    case ValidFacingFishableRejectReason.Unsupported:
                        summary.UnsupportedRejects++;
                        break;
                    case ValidFacingFishableRejectReason.Covered:
                        summary.CoveredRejects++;
                        summary.FirstCoveredFishable ??= fishablePoint;
                        break;
                    case ValidFacingFishableRejectReason.AccessBlocked:
                        summary.AccessBlocked++;
                        summary.FirstBlockedFishable ??= fishablePoint;
                        break;
                }
            }
        }

        return summary;
    }

    private static IReadOnlyList<Vector2> EnumerateDebugFacingDirections(
        Vector3 position,
        FishableCoverageTarget target)
    {
        var baseDirections = new List<Vector2>();
        if (TryGetPreferredFishableWallDirection(position, target, out var preferredDirection))
            baseDirections.Add(preferredDirection);
        TryAddBaseDirection(baseDirections, target.Point - position);
        TryAddBaseDirection(baseDirections, target.Triangle.Centroid - position);
        if (TryFindLocalFishableCenter(position, target.Triangle, target.Point, target.Block, out var localFishableCenter))
            TryAddBaseDirection(baseDirections, localFishableCenter - position);
        TryAddBaseDirection(baseDirections, target.Block.Center - position);

        var directions = new List<Vector2>();
        var testedDirections = new HashSet<int>();
        if (baseDirections.Count > 0)
            AddDebugFacingDirections(directions, testedDirections, [baseDirections[0]], CandidateFacingPrimaryAngleOffsets);
        AddDebugFacingDirections(directions, testedDirections, baseDirections, CandidateFacingPrimaryAngleOffsets);
        AddDebugFacingDirections(directions, testedDirections, baseDirections, CandidateFacingAngleOffsets);

        const int fullSweepSteps = 16;
        for (var index = 0; index < fullSweepSteps; index++)
        {
            var angle = MathF.Tau * index / fullSweepSteps;
            var direction = new Vector2(MathF.Sin(angle), MathF.Cos(angle));
            if (TryAddTestedDirection(testedDirections, direction))
                directions.Add(direction);
        }

        return directions;
    }

    private static void AddDebugFacingDirections(
        List<Vector2> directions,
        HashSet<int> testedDirections,
        IReadOnlyList<Vector2> baseDirections,
        IReadOnlyList<float> angleOffsets)
    {
        foreach (var baseDirection in baseDirections)
        {
            foreach (var offset in angleOffsets)
            {
                var direction = RotateDirection(baseDirection, offset);
                if (TryAddTestedDirection(testedDirections, direction))
                    directions.Add(direction);
            }
        }
    }

    private static string FormatDebugPoint(Vector3? point) =>
        point.HasValue
            ? $"({point.Value.X:F2},{point.Value.Y:F2},{point.Value.Z:F2})"
            : "-";

    private static string FormatDebugYList(IReadOnlyList<float> values) =>
        values.Count == 0
            ? "-"
            : string.Join(",", values.Select(value => value.ToString("F2")));

    private sealed class CandidateTargetDebugSummary
    {
        public bool FishableCovered { get; set; }
        public int Probes { get; set; }
        public int NoWalkableProbes { get; set; }
        public int MaxStackDepth { get; set; }
        public int LayerAttempts { get; set; }
        public int StandingRejects { get; set; }
        public int DistanceRejects { get; set; }
        public int FacingRejects { get; set; }
        public int Successes { get; set; }
        public Vector3? FirstStackProbe { get; set; }
        public List<float> FirstStackYValues { get; set; } = [];
        public Vector3? FirstSuccessPosition { get; set; }
        public float? FirstSuccessRotation { get; set; }
        public Vector3? FirstFacingRejectPosition { get; set; }
        public FacingDebugSummary? FirstFacingSummary { get; set; }
    }

    private sealed class FacingDebugSummary
    {
        public int Directions { get; set; }
        public int TargetBehindDirections { get; set; }
        public int ProbeAttempts { get; set; }
        public int NoFishable { get; set; }
        public int FishableBehind { get; set; }
        public int DistanceRejects { get; set; }
        public int UnsupportedRejects { get; set; }
        public int CoveredRejects { get; set; }
        public int AccessBlocked { get; set; }
        public int Successes { get; set; }
        public Vector3? FirstFishable { get; set; }
        public Vector3? FirstCoveredFishable { get; set; }
        public Vector3? FirstBlockedFishable { get; set; }
    }

    private enum ValidFacingFishableRejectReason
    {
        None,
        Behind,
        Distance,
        Unsupported,
        Covered,
        AccessBlocked,
    }

    private sealed class CandidateGenerationDiagnostics
    {
        private readonly IPluginLog log;
        private readonly uint territoryId;
        private readonly int fishableTriangles;
        private readonly int walkableTriangles;
        private readonly int collisionBlockerTriangles;

        public CandidateGenerationDiagnostics(
            IPluginLog log,
            uint territoryId,
            int fishableTriangles,
            int walkableTriangles,
            int collisionBlockerTriangles)
        {
            this.log = log;
            this.territoryId = territoryId;
            this.fishableTriangles = fishableTriangles;
            this.walkableTriangles = walkableTriangles;
            this.collisionBlockerTriangles = collisionBlockerTriangles;
        }

        public long CoverageTargets { get; set; }
        public long CoverageHits { get; set; }
        public long CandidateCreateAttempts { get; set; }
        public long CandidateCreates { get; set; }
        public long CandidateCreateFailures { get; set; }
        public long CandidateDedupeRejects { get; set; }
        public long ProbeAttempts { get; set; }
        public long WalkableLayerAttempts { get; set; }
        public long WalkableMisses { get; set; }
        public long WalkableRejects { get; set; }
        public long FacingRejects { get; set; }
        public long WalkableStackQueries { get; set; }
        public long WalkableStackCacheHits { get; set; }
        public long StandingClearanceQueries { get; set; }
        public long StandingClearanceCacheHits { get; set; }
        public long FacingFishableQueries { get; set; }
        public long FacingFishableCacheHits { get; set; }
        public long FacingFishableHits { get; set; }
        public long FishableCoveredQueries { get; set; }
        public long FishableCoveredCacheHits { get; set; }
        public long FishableCoveredHits { get; set; }
        public long AccessQueries { get; set; }
        public long AccessCacheHits { get; set; }
        public long AccessBlocked { get; set; }

        public void LogStep(string step, Stopwatch stopwatch, string details)
        {
            stopwatch.Stop();
            log.Information(
                "FPG candidate timing: territory={TerritoryId} step={Step} elapsedMs={ElapsedMs:F1} {Details}",
                territoryId,
                step,
                stopwatch.Elapsed.TotalMilliseconds,
                details);
        }

        public void LogSummary(Stopwatch stopwatch, int candidateCount)
        {
            stopwatch.Stop();
            log.Information(
                "FPG candidate timing summary: territory={TerritoryId} totalMs={TotalMs:F1} fishableTriangles={FishableTriangles} walkableTriangles={WalkableTriangles} collisionBlockers={CollisionBlockers} candidates={Candidates} coverageTargets={CoverageTargets} coverageHits={CoverageHits} createAttempts={CreateAttempts} created={Created} createFailures={CreateFailures} dedupeRejects={DedupeRejects} probes={Probes} walkableLayers={WalkableLayers} walkableMisses={WalkableMisses} walkableRejects={WalkableRejects} facingRejects={FacingRejects} walkableStackQueries={WalkableStackQueries} walkableStackCacheHits={WalkableStackCacheHits} standingClearanceQueries={StandingClearanceQueries} standingClearanceCacheHits={StandingClearanceCacheHits} facingFishableQueries={FacingFishableQueries} facingFishableCacheHits={FacingFishableCacheHits} facingFishableHits={FacingFishableHits} fishableCoveredQueries={FishableCoveredQueries} fishableCoveredCacheHits={FishableCoveredCacheHits} fishableCoveredHits={FishableCoveredHits} accessQueries={AccessQueries} accessCacheHits={AccessCacheHits} accessBlocked={AccessBlocked}",
                territoryId,
                stopwatch.Elapsed.TotalMilliseconds,
                fishableTriangles,
                walkableTriangles,
                collisionBlockerTriangles,
                candidateCount,
                CoverageTargets,
                CoverageHits,
                CandidateCreateAttempts,
                CandidateCreates,
                CandidateCreateFailures,
                CandidateDedupeRejects,
                ProbeAttempts,
                WalkableLayerAttempts,
                WalkableMisses,
                WalkableRejects,
                FacingRejects,
                WalkableStackQueries,
                WalkableStackCacheHits,
                StandingClearanceQueries,
                StandingClearanceCacheHits,
                FacingFishableQueries,
                FacingFishableCacheHits,
                FacingFishableHits,
                FishableCoveredQueries,
                FishableCoveredCacheHits,
                FishableCoveredHits,
                AccessQueries,
                AccessCacheHits,
                AccessBlocked);
        }
    }

    private sealed class FishableBlockIndex
    {
        private readonly Dictionary<GridCell, List<FishableSurfaceBlock>> cells = [];

        public FishableBlockIndex(IReadOnlyList<FishableSurfaceBlock> blocks)
        {
            foreach (var block in blocks)
                Add(block);
        }

        public bool MayOverlap(ExtractedSceneTriangle triangle, float buffer)
        {
            var bounds = CalculateHorizontalBounds(triangle).Expand(buffer);
            var minCell = GridCell.From(bounds.MinX, bounds.MinZ, SurfaceIndexCellSize);
            var maxCell = GridCell.From(bounds.MaxX, bounds.MaxZ, SurfaceIndexCellSize);
            for (var x = minCell.X; x <= maxCell.X; x++)
            {
                for (var z = minCell.Z; z <= maxCell.Z; z++)
                {
                    if (!cells.TryGetValue(new GridCell(x, z), out var blocks))
                        continue;

                    foreach (var block in blocks)
                    {
                        if (block.Bounds.Overlaps(bounds))
                            return true;
                    }
                }
            }

            return false;
        }

        public IReadOnlyList<FishableSurfaceBlock> FindNear(Vector3 point)
        {
            var cell = GridCell.From(point.X, point.Z, SurfaceIndexCellSize);
            if (!cells.TryGetValue(cell, out var blocks))
                return [];

            return blocks
                .Where(block => block.Bounds.Contains(point, MaximumCandidateDistanceFromFishableSurface))
                .ToList();
        }

        private void Add(FishableSurfaceBlock block)
        {
            var bounds = block.Bounds.Expand(MaximumCandidateDistanceFromFishableSurface);
            var minCell = GridCell.From(bounds.MinX, bounds.MinZ, SurfaceIndexCellSize);
            var maxCell = GridCell.From(bounds.MaxX, bounds.MaxZ, SurfaceIndexCellSize);
            for (var x = minCell.X; x <= maxCell.X; x++)
            {
                for (var z = minCell.Z; z <= maxCell.Z; z++)
                {
                    var cell = new GridCell(x, z);
                    if (!cells.TryGetValue(cell, out var blocks))
                    {
                        blocks = [];
                        cells[cell] = blocks;
                    }

                    blocks.Add(block);
                }
            }
        }
    }

    private sealed class CandidateDedupeIndex
    {
        private readonly Dictionary<CandidateDedupeCellKey, List<CandidateScratch>> cells = [];

        public bool TryAdd(CandidateScratch candidate)
        {
            if (ContainsNear(candidate.Position))
                return false;

            AddUnchecked(candidate);
            return true;
        }

        public bool ContainsNear(Vector3 position)
        {
            var center = CandidateDedupeCellKey.From(position);
            for (var x = center.X - 1; x <= center.X + 1; x++)
            {
                for (var y = center.Y - 1; y <= center.Y + 1; y++)
                {
                    for (var z = center.Z - 1; z <= center.Z + 1; z++)
                    {
                        if (!cells.TryGetValue(new CandidateDedupeCellKey(x, y, z), out var candidates))
                            continue;

                        foreach (var candidate in candidates)
                        {
                            if (AreCandidatesTooClose(position, candidate.Position))
                                return true;
                        }
                    }
                }
            }

            return false;
        }

        private void AddUnchecked(CandidateScratch candidate)
        {
            var key = CandidateDedupeCellKey.From(candidate.Position);
            if (!cells.TryGetValue(key, out var candidates))
            {
                candidates = [];
                cells[key] = candidates;
            }

            candidates.Add(candidate);
        }

        private static bool AreCandidatesTooClose(Vector3 left, Vector3 right)
        {
            return MathF.Abs(left.Y - right.Y) <= CandidateDedupeVerticalCellSize
                && HorizontalDistance(left, right) < CandidateDedupeCellSize;
        }
    }

    private sealed class CandidateCoverageIndex
    {
        private readonly Dictionary<CandidateCoverageCellKey, List<CandidateScratch>> cells = [];

        public CandidateCoverageIndex(IEnumerable<CandidateScratch> candidates)
        {
            foreach (var candidate in candidates)
                Add(candidate);
        }

        public void Add(CandidateScratch candidate)
        {
            var key = CandidateCoverageCellKey.From(candidate.SurfaceGroupId, candidate.Position);
            if (!cells.TryGetValue(key, out var list))
            {
                list = [];
                cells[key] = list;
            }

            list.Add(candidate);
        }

        public bool IsCovered(FishableCoverageTarget target, ScanQueryCache queryCache)
        {
            var center = CandidateCoverageCellKey.From(target.Block.SurfaceGroupId, target.Point);
            var range = (int)MathF.Ceiling(FishableCoverageSatisfiedDistance / CandidateCoverageCellSize) + 1;
            for (var x = center.X - range; x <= center.X + range; x++)
            {
                for (var z = center.Z - range; z <= center.Z + range; z++)
                {
                    if (!cells.TryGetValue(new CandidateCoverageCellKey(center.SurfaceGroupId, x, z), out var candidates))
                        continue;

                    foreach (var candidate in candidates)
                    {
                        if (HorizontalDistance(candidate.Position, target.Point) > FishableCoverageSatisfiedDistance
                            || !IsFishablePointUsableForCandidate(target.Block, candidate.Position, target.Point, queryCache))
                            continue;

                        var forward = new Vector2(MathF.Sin(candidate.Rotation), MathF.Cos(candidate.Rotation));
                        if (TryGetHorizontalDirection(target.Point - candidate.Position, out var targetDirection)
                            && Vector2.Dot(targetDirection, forward) <= 0f)
                            continue;

                        if (!HasValidFacingFishableAhead(candidate.Position, forward, target, queryCache))
                            continue;

                        return true;
                    }
                }
            }

            return false;
        }

        private static bool HasValidFacingFishableAhead(
            Vector3 position,
            Vector2 direction,
            FishableCoverageTarget target,
            ScanQueryCache queryCache)
        {
            foreach (var offset in EnumerateCandidateFishableRayOffsets())
            {
                var probe = CreateProbePoint(position, direction, offset);
                if (!queryCache.TryFindFacingFishable(position, target, probe, out var fishablePoint, out var blocked))
                {
                    if (blocked)
                        return false;
                    continue;
                }

                if (GetValidFacingFishableRejectReason(position, direction, fishablePoint, target, queryCache) != ValidFacingFishableRejectReason.None)
                    continue;

                return true;
            }

            return false;
        }
    }

    private sealed class ScanQueryCache
    {
        private readonly TriangleIndex walkableIndex;
        private readonly TriangleIndex collisionBlockerIndex;
        private readonly CandidateGenerationDiagnostics? diagnostics;
        private readonly Dictionary<HorizontalProbeKey, IReadOnlyList<Vector3>> walkableStacks = [];
        private readonly Dictionary<FacingFishableQueryKey, CachedVector3Result> facingFishable = [];
        private readonly Dictionary<ProbePointKey, bool> standingClearance = [];
        private readonly Dictionary<ProbePointKey, bool> fishableCovered = [];
        private readonly Dictionary<SightLineKey, bool> clearFacingCorridor = [];

        public ScanQueryCache(
            TriangleIndex walkableIndex,
            TriangleIndex collisionBlockerIndex,
            CandidateGenerationDiagnostics? diagnostics)
        {
            this.walkableIndex = walkableIndex;
            this.collisionBlockerIndex = collisionBlockerIndex;
            this.diagnostics = diagnostics;
        }

        public IReadOnlyList<Vector3> GetCandidateWalkablePoints(Vector3 probe)
        {
            var key = HorizontalProbeKey.From(probe);
            if (!walkableStacks.TryGetValue(key, out var points))
            {
                if (diagnostics is not null)
                    diagnostics.WalkableStackQueries++;
                points = walkableIndex.FindContainingPoints(probe);
                walkableStacks[key] = points;
            }
            else
            {
                if (diagnostics is not null)
                    diagnostics.WalkableStackCacheHits++;
            }

            return points;
        }

        public bool HasStandingClearance(Vector3 walkablePoint)
        {
            var key = ProbePointKey.From(walkablePoint);
            if (!standingClearance.TryGetValue(key, out var result))
            {
                if (diagnostics is not null)
                    diagnostics.StandingClearanceQueries++;
                result = !collisionBlockerIndex.HasContainingPointAboveWithinVerticalRange(
                    walkablePoint,
                    WalkableFishableMinimumVerticalDelta,
                    WalkableStandingClearanceMeters);
                standingClearance[key] = result;
            }
            else
            {
                if (diagnostics is not null)
                    diagnostics.StandingClearanceCacheHits++;
            }

            return result;
        }

        public bool TryFindHighestCollisionBelow(Vector3 rayPoint, out Vector3 collisionPoint)
        {
            collisionPoint = default;
            foreach (var point in collisionBlockerIndex.FindContainingPoints(rayPoint))
            {
                if (rayPoint.Y - point.Y <= WalkableFishableMinimumVerticalDelta)
                    continue;

                collisionPoint = point;
                return true;
            }

            return false;
        }

        public bool TryFindFacingFishable(
            Vector3 candidatePosition,
            FishableCoverageTarget target,
            Vector3 probe,
            out Vector3 fishablePoint,
            out bool blocked)
        {
            if (diagnostics is not null)
                diagnostics.FacingFishableQueries++;
            var key = FacingFishableQueryKey.From(target.Block.SurfaceGroupId, candidatePosition, probe);
            if (facingFishable.TryGetValue(key, out var cached))
            {
                if (diagnostics is not null)
                    diagnostics.FacingFishableCacheHits++;
                fishablePoint = cached.Point;
                blocked = cached.Blocked;
                return cached.Found;
            }

            var result = TryFindRayDropFishableTarget(
                probe + new Vector3(0f, CandidateFishableRayHeight, 0f),
                candidatePosition,
                [target.Block],
                this,
                out var rayDropTarget);
            var found = result == RayDropProbeResult.HitFishable;
            blocked = false;
            fishablePoint = found ? rayDropTarget.Point : default;
            facingFishable[key] = new CachedVector3Result(found, blocked, fishablePoint);
            if (found && diagnostics is not null)
                diagnostics.FacingFishableHits++;
            return found;
        }

        public bool HasClearFishableAccess(Vector3 walkablePoint, Vector3 fishablePoint)
        {
            if (diagnostics is not null)
                diagnostics.AccessQueries++;
            var result = HasClearFacingCorridor(walkablePoint, fishablePoint);
            if (!result && diagnostics is not null)
                diagnostics.AccessBlocked++;

            return result;
        }

        private bool HasClearFacingCorridor(Vector3 walkablePoint, Vector3 fishablePoint)
        {
            var end = new Vector3(fishablePoint.X, walkablePoint.Y, fishablePoint.Z);
            if (HorizontalDistance(walkablePoint, end) <= 0f)
                return true;

            var key = SightLineKey.From(walkablePoint, end);
            if (!clearFacingCorridor.TryGetValue(key, out var result))
            {
                result = !HasCollisionBlockerAboveFacingCorridor(walkablePoint, fishablePoint);
                clearFacingCorridor[key] = result;
            }

            return result;
        }

        private bool HasCollisionBlockerAboveFacingCorridor(Vector3 walkablePoint, Vector3 fishablePoint)
        {
            if (!TryGetHorizontalDirection(fishablePoint - walkablePoint, out var direction))
                return false;

            var maxDistance = MathF.Min(CandidateFishableRayMaxDistance, HorizontalDistance(walkablePoint, fishablePoint));
            for (var offset = FacingCorridorProbeStep;
                 offset <= maxDistance + 0.001f;
                 offset += FacingCorridorProbeStep)
            {
                var probe = CreateProbePoint(walkablePoint, direction, offset);
                if (collisionBlockerIndex.HasContainingPointAboveWithinVerticalRange(
                        probe,
                        FacingCorridorBlockerMinimumHeight,
                        OpenFishableClearanceMeters))
                    return true;
            }

            return false;
        }

        public bool IsFishableCoveredByWalkable(Vector3 fishablePoint)
        {
            var key = ProbePointKey.From(fishablePoint);
            if (!fishableCovered.TryGetValue(key, out var result))
            {
                if (diagnostics is not null)
                    diagnostics.FishableCoveredQueries++;
                result = IsFishablePointBlockedByWalkable(fishablePoint, walkableIndex);
                fishableCovered[key] = result;
            }
            else
            {
                if (diagnostics is not null)
                    diagnostics.FishableCoveredCacheHits++;
            }

            if (result && diagnostics is not null)
                diagnostics.FishableCoveredHits++;

            return result;
        }
    }

    private sealed class RayDropFacingHintIndex
    {
        private readonly Dictionary<RayDropFacingHintKey, List<RayDropFacingHint>> hintsByCell = [];

        public void Add(Vector3 position, DirectionalRayDropSector sector)
        {
            var key = RayDropFacingHintKey.From(position);
            if (!hintsByCell.TryGetValue(key, out var hints))
            {
                hints = [];
                hintsByCell[key] = hints;
            }

            hints.Add(new RayDropFacingHint(
                position,
                sector.SurfaceGroupId,
                sector.StartAngle,
                sector.EndAngle,
                sector.CenterAngle,
                sector.Width));
            hints.Sort(static (left, right) =>
            {
                var widthComparison = right.Width.CompareTo(left.Width);
                if (widthComparison != 0)
                    return widthComparison;

                return string.Compare(left.SurfaceGroupId, right.SurfaceGroupId, StringComparison.Ordinal);
            });
            if (hints.Count > MaxCandidateFacingHintsPerCell)
                hints.RemoveRange(MaxCandidateFacingHintsPerCell, hints.Count - MaxCandidateFacingHintsPerCell);
        }

        public bool TryFindBest(Vector3 position, out RayDropFacingHint hint)
        {
            hint = default;
            var found = false;
            var bestDistance = float.MaxValue;
            var key = RayDropFacingHintKey.From(position);
            for (var dx = -CandidateFacingHintSearchRadiusCells; dx <= CandidateFacingHintSearchRadiusCells; dx++)
            {
                for (var dz = -CandidateFacingHintSearchRadiusCells; dz <= CandidateFacingHintSearchRadiusCells; dz++)
                {
                    if (!hintsByCell.TryGetValue(key.Offset(dx, dz), out var hints))
                        continue;

                    foreach (var candidate in hints)
                    {
                        var distance = HorizontalDistance(position, candidate.Position);
                        if (found
                            && (candidate.Width < hint.Width
                                || (candidate.Width <= hint.Width + 0.001f && distance >= bestDistance)))
                            continue;

                        hint = candidate;
                        bestDistance = distance;
                        found = true;
                    }
                }
            }

            return found;
        }
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

        public int CellCount => cells.Count;
        public int EntryCount { get; private set; }

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

        public IReadOnlyList<Vector3> FindContainingPoints(Vector3 point)
        {
            var cell = GridCell.From(point.X, point.Z, cellSize);
            if (!cells.TryGetValue(cell, out var triangles))
                return [];

            var point2 = new Vector2(point.X, point.Z);
            var points = new List<Vector3>();
            var seenY = new HashSet<int>();
            foreach (var candidate in triangles)
            {
                if (!PointInTriangle(
                        point2,
                        new Vector2(candidate.A.X, candidate.A.Z),
                        new Vector2(candidate.B.X, candidate.B.Z),
                        new Vector2(candidate.C.X, candidate.C.Z))
                    || !TryProjectYOnTriangleXz(candidate, point.X, point.Z, out var candidateY)
                    || !seenY.Add(QuantizeStackY(candidateY)))
                    continue;

                points.Add(new Vector3(point.X, candidateY, point.Z));
            }

            points.Sort((left, right) => right.Y.CompareTo(left.Y));
            return points;
        }

        public bool TryFindHighestContainingPointBelow(
            Vector3 point,
            float minimumDropDistance,
            out ExtractedSceneTriangle triangle,
            out Vector3 containingPoint)
        {
            triangle = default;
            containingPoint = default;
            var cell = GridCell.From(point.X, point.Z, cellSize);
            if (!cells.TryGetValue(cell, out var triangles))
                return false;

            var point2 = new Vector2(point.X, point.Z);
            var highestY = float.MinValue;
            var found = false;
            foreach (var candidate in triangles)
            {
                if (!PointInTriangle(
                        point2,
                        new Vector2(candidate.A.X, candidate.A.Z),
                        new Vector2(candidate.B.X, candidate.B.Z),
                        new Vector2(candidate.C.X, candidate.C.Z))
                    || !TryProjectYOnTriangleXz(candidate, point.X, point.Z, out var candidateY))
                    continue;

                var dropDistance = point.Y - candidateY;
                if (dropDistance < minimumDropDistance
                    || candidateY <= highestY)
                    continue;

                highestY = candidateY;
                triangle = candidate;
                containingPoint = new Vector3(point.X, candidateY, point.Z);
                found = true;
            }

            return found;
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
                if (deltaY > minimumDeltaY && deltaY <= maximumDeltaY)
                    return true;
            }

            return false;
        }

        public bool IntersectsSegment(Vector3 start, Vector3 end)
        {
            var minX = MathF.Min(start.X, end.X);
            var maxX = MathF.Max(start.X, end.X);
            var minZ = MathF.Min(start.Z, end.Z);
            var maxZ = MathF.Max(start.Z, end.Z);
            var minCell = GridCell.From(minX, minZ, cellSize);
            var maxCell = GridCell.From(maxX, maxZ, cellSize);

            for (var x = minCell.X; x <= maxCell.X; x++)
            {
                for (var z = minCell.Z; z <= maxCell.Z; z++)
                {
                    if (!cells.TryGetValue(new GridCell(x, z), out var triangles))
                        continue;

                    foreach (var candidate in triangles)
                    {
                        if (SegmentAabbMayIntersectTriangle(start, end, candidate)
                            && SegmentIntersectsTriangle(start, end, candidate))
                            return true;
                    }
                }
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
                    EntryCount++;
                }
            }
        }

        private static int QuantizeStackY(float value) =>
            (int)MathF.Round(value / ProbeCacheCellSize, MidpointRounding.AwayFromZero);
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

    private readonly record struct HorizontalBounds(float MinX, float MaxX, float MinZ, float MaxZ)
    {
        public HorizontalBounds Expand(float buffer) => new(
            MinX - buffer,
            MaxX + buffer,
            MinZ - buffer,
            MaxZ + buffer);

        public bool Contains(Vector3 point, float buffer = 0f)
        {
            return point.X >= MinX - buffer
                && point.X <= MaxX + buffer
                && point.Z >= MinZ - buffer
                && point.Z <= MaxZ + buffer;
        }

        public bool Overlaps(HorizontalBounds other, float buffer = 0f)
        {
            return MaxX >= other.MinX - buffer
                && MinX <= other.MaxX + buffer
                && MaxZ >= other.MinZ - buffer
                && MinZ <= other.MaxZ + buffer;
        }
    }

    private sealed record FishableSurfaceBlock(
        string SurfaceGroupId,
        IReadOnlyList<ExtractedSceneTriangle> Triangles,
        IReadOnlyList<FishableBoundarySegment> BoundarySegments,
        Vector3 Center,
        HorizontalBounds Bounds,
        float Area,
        TriangleIndex Index);

    private readonly record struct FishableBoundarySegment(
        Vector3 Start,
        Vector3 End,
        Vector2 InwardDirection,
        float Length,
        ExtractedSceneTriangle Triangle);

    private readonly record struct FishableCoverageRound(
        string Name,
        float TargetSpacing,
        int DirectionCount);

    private readonly record struct FishableCoverageTarget(
        Vector3 Point,
        ExtractedSceneTriangle Triangle,
        FishableSurfaceBlock Block,
        Vector2 PreferredFacingDirection,
        bool HasPreferredFacingDirection,
        FishableCoverageTargetKind Kind);

    private readonly record struct RayDropFishableTarget(
        Vector3 Point,
        ExtractedSceneTriangle Triangle,
        string SurfaceGroupId);

    private readonly record struct DirectionalRayDropHit(
        float Angle,
        Vector2 Direction,
        string SurfaceGroupId,
        Vector3 FishablePoint);

    private sealed record DirectionalRayDropSector(
        string SurfaceGroupId,
        IReadOnlyList<DirectionalRayDropHit> Hits,
        float StartAngle,
        float EndAngle,
        float CenterAngle,
        float Width);

    private readonly record struct RayDropFacingHint(
        Vector3 Position,
        string SurfaceGroupId,
        float StartAngle,
        float EndAngle,
        float CenterAngle,
        float Width);

    private enum FishableCoverageTargetKind
    {
        Boundary,
        InteriorFallback,
        WalkableSample,
    }

    private enum RayDropProbeResult
    {
        Miss,
        HitFishable,
    }

    private readonly record struct FishableCoverageTargetKey(string SurfaceGroupId, int X, int Y, int Z)
    {
        public static FishableCoverageTargetKey From(string surfaceGroupId, Vector3 point) => new(
            surfaceGroupId,
            Quantize(point.X),
            Quantize(point.Y),
            Quantize(point.Z));

        private static int Quantize(float value) => (int)MathF.Floor(value / FishableCoverageTargetKeyCellSize);
    }

    private readonly record struct CandidateCoverageCellKey(string SurfaceGroupId, int X, int Z)
    {
        public static CandidateCoverageCellKey From(string surfaceGroupId, Vector3 point) => new(
            surfaceGroupId,
            Quantize(point.X),
            Quantize(point.Z));

        private static int Quantize(float value) => (int)MathF.Floor(value / CandidateCoverageCellSize);
    }

    private readonly record struct HorizontalProbeKey(int X, int Z)
    {
        public static HorizontalProbeKey From(Vector3 point) => new(
            Quantize(point.X),
            Quantize(point.Z));

        private static int Quantize(float value) => (int)MathF.Floor(value / ProbeCacheCellSize);
    }

    private readonly record struct ProbePointKey(int X, int Y, int Z)
    {
        public static ProbePointKey From(Vector3 point) => new(
            Quantize(point.X),
            Quantize(point.Y),
            Quantize(point.Z));

        private static int Quantize(float value) => (int)MathF.Floor(value / ProbeCacheCellSize);
    }

    private readonly record struct WalkableCandidateStartKey(int X, int Y, int Z)
    {
        public static WalkableCandidateStartKey From(Vector3 point) => new(
            QuantizeHorizontal(point.X),
            QuantizeVertical(point.Y),
            QuantizeHorizontal(point.Z));

        private static int QuantizeHorizontal(float value) => (int)MathF.Floor(value / CandidateDedupeCellSize);

        private static int QuantizeVertical(float value) => (int)MathF.Floor(value / CandidateStartPointYCellSize);
    }

    private readonly record struct RayDropFacingHintKey(int X, int Z)
    {
        public static RayDropFacingHintKey From(Vector3 point) => new(
            Quantize(point.X),
            Quantize(point.Z));

        public RayDropFacingHintKey Offset(int dx, int dz) => new(X + dx, Z + dz);

        private static int Quantize(float value) => (int)MathF.Floor(value / CandidateFacingHintCellSize);
    }

    private readonly record struct FacingFishableQueryKey(string SurfaceGroupId, ProbePointKey Candidate, HorizontalProbeKey Probe)
    {
        public static FacingFishableQueryKey From(string surfaceGroupId, Vector3 candidate, Vector3 probe) => new(
            surfaceGroupId,
            ProbePointKey.From(candidate),
            HorizontalProbeKey.From(probe));
    }

    private readonly record struct SightLineKey(int StartX, int StartY, int StartZ, int EndX, int EndY, int EndZ)
    {
        public static SightLineKey From(Vector3 start, Vector3 end) => new(
            Quantize(start.X),
            Quantize(start.Y),
            Quantize(start.Z),
            Quantize(end.X),
            Quantize(end.Y),
            Quantize(end.Z));

        private static int Quantize(float value) => (int)MathF.Floor(value / SightLineCacheCellSize);
    }

    private readonly record struct CachedVector3Result(bool Found, bool Blocked, Vector3 Point);

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

    private readonly record struct ProjectionEdge(Vector2 Start, Vector2 End);

    private readonly record struct TrianglePairKey(int Left, int Right)
    {
        public static TrianglePairKey From(int left, int right) =>
            left <= right ? new TrianglePairKey(left, right) : new TrianglePairKey(right, left);
    }

    private readonly record struct CandidateDedupeCellKey(int X, int Y, int Z)
    {
        public static CandidateDedupeCellKey From(Vector3 position) => new(
            (int)MathF.Floor(position.X / CandidateDedupeCellSize),
            (int)MathF.Floor(position.Y / CandidateDedupeVerticalCellSize),
            (int)MathF.Floor(position.Z / CandidateDedupeCellSize));
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

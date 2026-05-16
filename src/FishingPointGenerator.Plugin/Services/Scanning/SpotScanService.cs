using FishingPointGenerator.Core;
using FishingPointGenerator.Core.Models;

namespace FishingPointGenerator.Plugin.Services.Scanning;

internal sealed class SpotScanService
{
    private const float MinimumSearchRadiusMeters = 35f;
    private const float MaximumSearchRadiusMeters = 140f;
    private const float SearchRadiusPaddingMeters = 24f;

    private readonly TerritoryGeometryCache geometryCache;

    public SpotScanService(TerritoryGeometryCache geometryCache)
    {
        this.geometryCache = geometryCache;
    }

    public TerritorySurveyDocument ScanCurrentTerritory(bool forceTerritoryRescan)
    {
        return geometryCache.ScanCurrentTerritory(forceTerritoryRescan);
    }

    public TerritoryScanCapture CaptureCurrentTerritory()
    {
        return geometryCache.CaptureCurrentTerritory();
    }

    public TerritorySurveyDocument ScanCapturedTerritory(
        TerritoryScanCapture capture,
        IProgress<TerritoryScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        return geometryCache.ScanCapturedTerritory(capture, progress, cancellationToken);
    }

    public NearbyScanDebugResult DebugScanNearby(float radiusMeters)
    {
        return geometryCache.DebugScanNearby(radiusMeters);
    }

    public SpotScanDocument CreateSpotScan(
        FishingSpotTarget target,
        IReadOnlyList<FishingSpotTarget> catalogTargets,
        TerritorySurveyDocument survey)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(catalogTargets);
        ArgumentNullException.ThrowIfNull(survey);

        var warnings = new List<string>();
        if (survey.TerritoryId != target.TerritoryId)
        {
            warnings.Add($"当前区域 {survey.TerritoryId} 与目标区域 {target.TerritoryId} 不一致。");
            return CreateDocument(target.Key, survey, [], warnings);
        }

        var nearbyTargets = CreateNearbyTargetIndex(target.TerritoryId, catalogTargets);
        var targetCenter = new Point3(target.WorldX, 0f, target.WorldZ);
        var targetSearchRadius = GetSearchRadius(target);
        var candidates = survey.Candidates
            .Select(candidate => CreateSpotCandidate(
                target,
                targetCenter,
                targetSearchRadius,
                nearbyTargets,
                candidate))
            .GroupBy(candidate => candidate.CandidateFingerprint, StringComparer.Ordinal)
            .Select(group => group
                .OrderBy(candidate => candidate.SourceCandidateId, StringComparer.Ordinal)
                .First())
            .OrderBy(candidate => candidate.CandidateFingerprint, StringComparer.Ordinal)
            .ToList();

        if (candidates.Count == 0)
            warnings.Add("区域扫描已完成，但当前 Territory 内存候选为空。");
        else
            warnings.Add("点缓存未按 FishingSpot 半径裁剪；实际钓场归属以抛竿日志确认为准。");

        return CreateDocument(target.Key, survey, candidates, warnings);
    }

    private SpotScanDocument CreateDocument(
        SpotKey key,
        TerritorySurveyDocument survey,
        List<SpotCandidate> candidates,
        List<string> warnings)
    {
        return new SpotScanDocument
        {
            Key = key,
            ScanId = CreateStableScanId(key, survey),
            ScannerName = geometryCache.ScannerName,
            ScannerVersion = geometryCache.ScannerVersion,
            GeneratedAt = survey.GeneratedAt,
            Candidates = candidates,
            Warnings = warnings,
        };
    }

    private static string CreateStableScanId(SpotKey key, TerritorySurveyDocument survey)
    {
        var ticks = survey.GeneratedAt.UtcDateTime.Ticks;
        return $"territory-{survey.TerritoryId:x8}-spot-{key.FishingSpotId:x8}-{ticks:x16}-{survey.Candidates.Count:x8}";
    }

    private static SpotCandidate CreateSpotCandidate(
        FishingSpotTarget target,
        Point3 targetCenter,
        float targetSearchRadius,
        IReadOnlyList<NearbyTargetIndexEntry> nearbyTargets,
        ApproachCandidate candidate)
    {
        var key = target.Key;
        var targetDistance = candidate.Position.HorizontalDistanceTo(new Point3(targetCenter.X, candidate.Position.Y, targetCenter.Z));
        var territoryId = candidate.TerritoryId != 0 ? candidate.TerritoryId : target.TerritoryId;
        return new SpotCandidate
        {
            Key = key,
            CandidateFingerprint = SpotFingerprint.CreateTerritoryCandidateFingerprint(
                territoryId,
                candidate.Position,
                candidate.Rotation),
            RegionId = candidate.RegionId,
            BlockId = candidate.BlockId,
            SurfaceGroupId = candidate.SurfaceGroupId,
            Position = candidate.Position,
            Rotation = candidate.Rotation,
            Status = candidate.Status,
            Reachability = candidate.Reachability,
            PathLengthMeters = candidate.PathLengthMeters,
            SourceCandidateId = candidate.CandidateId,
            DistanceToTargetCenterMeters = targetDistance,
            IsWithinTargetSearchRadius = targetDistance <= targetSearchRadius,
            NearbyFishingSpotIds = FindNearbyFishingSpotIds(targetSearchRadius, targetDistance, nearbyTargets, candidate.Position),
            CreatedAt = candidate.CreatedAt,
        };
    }

    private static IReadOnlyList<NearbyTargetIndexEntry> CreateNearbyTargetIndex(
        uint territoryId,
        IReadOnlyList<FishingSpotTarget> catalogTargets)
    {
        return catalogTargets
            .Where(target => target.TerritoryId == territoryId)
            .Select(target => new NearbyTargetIndexEntry(
                target.FishingSpotId,
                target.WorldX,
                target.WorldZ,
                GetSearchRadius(target)))
            .OrderBy(target => target.FishingSpotId)
            .ToList();
    }

    private static List<uint> FindNearbyFishingSpotIds(
        float targetSearchRadius,
        float targetDistance,
        IReadOnlyList<NearbyTargetIndexEntry> nearbyTargets,
        Point3 position)
    {
        if (targetDistance > targetSearchRadius)
            return [];

        return nearbyTargets
            .Where(other =>
            {
                var center = new Point3(other.WorldX, position.Y, other.WorldZ);
                return position.HorizontalDistanceTo(center) <= other.SearchRadius;
            })
            .Select(other => other.FishingSpotId)
            .Distinct()
            .OrderBy(id => id)
            .ToList();
    }

    private static float GetSearchRadius(FishingSpotTarget target)
    {
        return Math.Clamp(target.Radius + SearchRadiusPaddingMeters, MinimumSearchRadiusMeters, MaximumSearchRadiusMeters);
    }

    private sealed record NearbyTargetIndexEntry(
        uint FishingSpotId,
        float WorldX,
        float WorldZ,
        float SearchRadius);
}

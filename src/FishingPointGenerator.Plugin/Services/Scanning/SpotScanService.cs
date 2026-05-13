using FishingPointGenerator.Core;
using FishingPointGenerator.Core.Models;

namespace FishingPointGenerator.Plugin.Services.Scanning;

internal sealed class SpotScanService
{
    private const float MinimumSearchRadiusMeters = 35f;
    private const float MaximumSearchRadiusMeters = 140f;
    private const float SearchRadiusPaddingMeters = 24f;
    private const float TargetPreferenceDistanceMeters = 180f;

    private readonly TerritoryGeometryCache geometryCache;

    public SpotScanService(TerritoryGeometryCache geometryCache)
    {
        this.geometryCache = geometryCache;
    }

    public SpotScanDocument ScanSpot(
        FishingSpotTarget target,
        IReadOnlyList<FishingSpotTarget> catalogTargets,
        bool forceTerritoryRescan)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(catalogTargets);

        var survey = geometryCache.ScanCurrentTerritory(forceTerritoryRescan);
        return CreateSpotScan(target, catalogTargets, survey);
    }

    public TerritorySurveyDocument ScanCurrentTerritory(bool forceTerritoryRescan)
    {
        return geometryCache.ScanCurrentTerritory(forceTerritoryRescan);
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
            return CreateDocument(target.Key, [], warnings);
        }

        var candidates = survey.Candidates
            .Select(candidate => CreateSpotCandidate(target, candidate))
            .GroupBy(candidate => candidate.CandidateFingerprint, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(candidate => candidate.Score).First())
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.CandidateFingerprint, StringComparer.Ordinal)
            .ToList();

        if (candidates.Count == 0)
            warnings.Add("区域扫描已完成，但当前 Territory 全图缓存没有候选点。");
        else
            warnings.Add("点缓存未按 FishingSpot 半径裁剪；实际钓场归属以抛竿日志确认为准。");

        return CreateDocument(target.Key, candidates, warnings);
    }

    private SpotScanDocument CreateDocument(SpotKey key, List<SpotCandidate> candidates, List<string> warnings)
    {
        return new SpotScanDocument
        {
            Key = key,
            ScannerName = geometryCache.ScannerName,
            ScannerVersion = geometryCache.ScannerVersion,
            Candidates = candidates,
            Warnings = warnings,
        };
    }

    private static SpotCandidate CreateSpotCandidate(FishingSpotTarget target, ApproachCandidate candidate)
    {
        var key = target.Key;
        return new SpotCandidate
        {
            Key = key,
            CandidateFingerprint = SpotFingerprint.CreateCandidateFingerprint(key, candidate.Position, candidate.Rotation),
            RegionId = candidate.RegionId,
            BlockId = candidate.BlockId,
            Position = candidate.Position,
            Rotation = candidate.Rotation,
            Score = CalculateTargetPreferenceScore(target, candidate),
            Status = candidate.Status,
            SourceCandidateId = candidate.CandidateId,
            NearbyFishingSpotIds = [],
            CreatedAt = candidate.CreatedAt,
        };
    }

    private static float CalculateTargetPreferenceScore(FishingSpotTarget target, ApproachCandidate candidate)
    {
        var targetCenter = new Point3(target.WorldX, candidate.Position.Y, target.WorldZ);
        var distance = candidate.Position.HorizontalDistanceTo(targetCenter);
        var softRadius = MathF.Max(GetSearchRadius(target), TargetPreferenceDistanceMeters);
        var targetPreference = 1f - Math.Clamp(distance / softRadius, 0f, 1f);
        return (candidate.Score * 0.75f) + (targetPreference * 0.25f);
    }

    private static float GetSearchRadius(FishingSpotTarget target)
    {
        return Math.Clamp(target.Radius + SearchRadiusPaddingMeters, MinimumSearchRadiusMeters, MaximumSearchRadiusMeters);
    }
}

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

    public SpotScanDocument ScanSpot(
        FishingSpotTarget target,
        IReadOnlyList<FishingSpotTarget> catalogTargets,
        bool forceTerritoryRescan)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(catalogTargets);

        var warnings = new List<string>();
        var survey = geometryCache.ScanCurrentTerritory(forceTerritoryRescan);
        if (survey.TerritoryId != target.TerritoryId)
        {
            warnings.Add($"当前区域 {survey.TerritoryId} 与目标区域 {target.TerritoryId} 不一致。");
            return CreateDocument(target.Key, [], warnings);
        }

        var nearbyTargets = catalogTargets
            .Where(other => other.TerritoryId == target.TerritoryId)
            .Where(other => other.FishingSpotId != target.FishingSpotId)
            .ToList();
        var searchRadius = GetSearchRadius(target);
        var candidates = survey.Candidates
            .Where(candidate => IsCandidateNearTarget(candidate, target, searchRadius))
            .Select(candidate => CreateSpotCandidate(target, candidate, nearbyTargets))
            .GroupBy(candidate => candidate.CandidateFingerprint, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(candidate => candidate.Score).First())
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.CandidateFingerprint, StringComparer.Ordinal)
            .ToList();

        if (candidates.Count == 0)
            warnings.Add("区域扫描已完成，但没有候选点落在此 FishingSpot 搜索半径内。");

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

    private static SpotCandidate CreateSpotCandidate(
        FishingSpotTarget target,
        ApproachCandidate candidate,
        IReadOnlyList<FishingSpotTarget> nearbyTargets)
    {
        var key = target.Key;
        return new SpotCandidate
        {
            Key = key,
            CandidateFingerprint = SpotFingerprint.CreateCandidateFingerprint(key, candidate.Position, candidate.TargetPoint),
            Position = candidate.Position,
            Rotation = candidate.Rotation,
            TargetPoint = candidate.TargetPoint,
            Score = candidate.Score,
            Status = candidate.Status,
            SourceCandidateId = candidate.CandidateId,
            NearbyFishingSpotIds = nearbyTargets
                .Where(other => IsCandidateNearTarget(candidate, other, GetSearchRadius(other)))
                .Select(other => other.FishingSpotId)
                .Distinct()
                .OrderBy(id => id)
                .ToList(),
            CreatedAt = candidate.CreatedAt,
        };
    }

    private static bool IsCandidateNearTarget(
        ApproachCandidate candidate,
        FishingSpotTarget target,
        float searchRadius)
    {
        var targetCenter = new Point3(target.WorldX, candidate.TargetPoint.Y, target.WorldZ);
        return candidate.TargetPoint.HorizontalDistanceTo(targetCenter) <= searchRadius;
    }

    private static float GetSearchRadius(FishingSpotTarget target)
    {
        return Math.Clamp(target.Radius + SearchRadiusPaddingMeters, MinimumSearchRadiusMeters, MaximumSearchRadiusMeters);
    }
}

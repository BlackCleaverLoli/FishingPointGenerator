using System.Numerics;
using Dalamud.Plugin.Services;
using FishingPointGenerator.Core.Geometry;
using FishingPointGenerator.Core.Models;
using FishingPointGenerator.Plugin.Services.Scanning;
using OmenTools;

namespace FishingPointGenerator.Plugin.Services;

internal sealed class PlaceholderScanner : ICurrentTerritoryScanner
{
    private readonly IPluginLog pluginLog;

    public PlaceholderScanner(IPluginLog pluginLog)
    {
        this.pluginLog = pluginLog;
    }

    public string Name => "占位扫描器";
    public bool IsPlaceholder => true;

    public NearbyScanDebugResult DebugScanNearby(float radiusMeters)
    {
        return new NearbyScanDebugResult
        {
            Message = "占位扫描器不支持附近碰撞面调试。",
        };
    }

    public TerritorySurveyDocument ScanCurrentTerritory()
    {
        var service = DService.Instance();
        var territoryId = service.ClientState.TerritoryType;
        var player = service.ObjectTable.LocalPlayer;
        if (territoryId == 0 || player is null)
        {
            pluginLog.Warning("占位扫描已跳过：没有可用区域或本地玩家。");
            return new TerritorySurveyDocument
            {
                TerritoryId = territoryId,
                TerritoryName = string.Empty,
            };
        }

        var position = player.Position;
        var rotation = AngleMath.NormalizeRotation(player.Rotation);
        var forward = AngleMath.RotationToDirection(rotation);
        var right = new Vector3(forward.Z, 0f, -forward.X);

        var candidates = new List<ApproachCandidate>();
        var offsets = new[]
        {
            new Vector2(0f, 0f),
            new Vector2(-2.5f, 0f),
            new Vector2(2.5f, 0f),
            new Vector2(0f, 2.5f),
            new Vector2(0f, -2.5f),
            new Vector2(-2.5f, 2.5f),
            new Vector2(2.5f, 2.5f),
            new Vector2(-2.5f, -2.5f),
            new Vector2(2.5f, -2.5f),
        };

        for (var index = 0; index < offsets.Length; index++)
        {
            var offset = offsets[index];
            var standingPosition = position + (right * offset.X) + (forward * offset.Y);
            candidates.Add(new ApproachCandidate
            {
                CandidateId = $"placeholder_{territoryId}_{DateTimeOffset.UtcNow:yyyyMMddHHmmss}_{index:D2}",
                TerritoryId = territoryId,
                SurfaceGroupId = $"placeholder_{territoryId}_surface_0001",
                Position = Point3.From(standingPosition),
                Rotation = rotation,
                Status = CandidateStatus.Unlabeled,
            });
        }

        return new TerritorySurveyDocument
        {
            TerritoryId = territoryId,
            TerritoryName = string.Empty,
            Candidates = candidates,
        };
    }
}

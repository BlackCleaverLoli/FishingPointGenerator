using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FishingPointGenerator.Core.Models;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;
using OmenTools;
using OmenTools.Interop.Game.Helpers;

namespace FishingPointGenerator.Plugin.Services.GameInteraction;

internal static unsafe class AetheryteTeleporter
{
    public static bool TryTeleportToNearestAetheryte(
        FishingSpotTarget target,
        out AetheryteTeleportDestination destination,
        out string error)
    {
        destination = default;
        error = string.Empty;

        if (target.TerritoryId == 0)
        {
            error = "已选钓场没有有效领地。";
            return false;
        }

        if (DService.Instance().ObjectTable.LocalPlayer is null)
        {
            error = "无法传送：没有可用的玩家角色。";
            return false;
        }

        var telepo = Telepo.Instance();
        if (telepo == null)
        {
            error = "无法传送：Telepo 不可用。";
            return false;
        }

        var candidates = CollectAetherytes(target).ToList();
        if (candidates.Count == 0)
        {
            error = $"FishingSpot {target.FishingSpotId} 所在领地没有可用以太之光数据。";
            return false;
        }

        var attunedCandidates = candidates
            .Where(candidate => IsAttuned(candidate.AetheryteId))
            .OrderBy(candidate => candidate.MapDistance)
            .ThenBy(candidate => candidate.AetheryteId)
            .ToList();
        if (attunedCandidates.Count == 0)
        {
            error = $"FishingSpot {target.FishingSpotId} 所在领地没有已共鸣的以太之光。";
            return false;
        }

        destination = attunedCandidates[0];
        if (!telepo->Teleport(destination.AetheryteId, 0))
        {
            error = $"无法向以太之光 {destination.AetheryteId} {destination.Name} 发起传送。";
            return false;
        }

        return true;
    }

    private static IEnumerable<AetheryteTeleportDestination> CollectAetherytes(FishingSpotTarget target)
    {
        var data = DService.Instance().Data;
        var mapMarkers = new Dictionary<(uint MarkerRange, uint DataKey), MapMarker>();
        foreach (var marker in data.GetSubrowExcelSheet<MapMarker>().SelectMany(marker => marker))
        {
            if (marker.DataType != 3 || !TryGetMarkerDataKey(marker, out var rowId))
                continue;

            mapMarkers[(marker.RowId, rowId)] = marker;
        }

        foreach (var aetheryte in data.GetExcelSheet<Aetheryte>())
        {
            if (!aetheryte.IsAetheryte || aetheryte.RowId <= 1 || aetheryte.PlaceName.RowId == 0)
                continue;
            if (aetheryte.Territory.RowId != target.TerritoryId)
                continue;

            var map = aetheryte.Territory.ValueNullable?.Map.ValueNullable;
            if (map is null)
                continue;
            if (!mapMarkers.TryGetValue((map.Value.MapMarkerRange, aetheryte.RowId), out var marker))
                continue;

            var mapPoint = PositionHelper.TextureToMap(marker.X, marker.Y, map.Value.SizeFactor);
            var dx = mapPoint.X - target.MapX;
            var dy = mapPoint.Y - target.MapY;
            var distance = MathF.Sqrt((dx * dx) + (dy * dy));

            yield return new AetheryteTeleportDestination(
                aetheryte.RowId,
                GetText(aetheryte.PlaceName.ValueNullable?.Name),
                mapPoint.X,
                mapPoint.Y,
                distance);
        }
    }

    private static bool IsAttuned(uint aetheryteId)
    {
        try
        {
            return DService.Instance().AetheryteList.Any(entry => entry.AetheryteID == aetheryteId);
        }
        catch (Exception ex)
        {
            DService.Instance().Log.Warning(ex, "FPG 检查以太之光共鸣状态失败");
            return false;
        }
    }

    private static bool TryGetMarkerDataKey(MapMarker marker, out uint rowId)
    {
        try
        {
            rowId = marker.DataKey.RowId;
            return rowId != 0;
        }
        catch
        {
            rowId = 0;
            return false;
        }
    }

    private static string GetText(ReadOnlySeString? text)
    {
        return text?.ToDalamudString().TextValue ?? string.Empty;
    }
}

internal readonly record struct AetheryteTeleportDestination(
    uint AetheryteId,
    string Name,
    float MapX,
    float MapY,
    float MapDistance);

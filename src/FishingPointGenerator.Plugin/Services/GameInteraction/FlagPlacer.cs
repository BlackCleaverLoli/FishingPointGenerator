using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using OmenTools;
using OmenTools.Interop.Game.Helpers;
using MapType = FFXIVClientStructs.FFXIV.Client.UI.Agent.MapType;

namespace FishingPointGenerator.Plugin.Services.GameInteraction;

internal static unsafe class FlagPlacer
{
    private const uint DefaultFlagIconId = 60561U;
    private const uint FishingIconId = 60465U;
    private const uint TempMarkerStyle = 4U;

    public static bool SetFlagFromWorld(
        uint territoryId,
        uint mapId,
        float worldX,
        float worldZ,
        string label,
        bool openMap = true,
        uint iconId = DefaultFlagIconId,
        ushort tempMarkerRadius = 0)
    {
        if (territoryId == 0 || mapId == 0)
            return false;

        try
        {
            var map = DService.Instance().Data.GetExcelSheet<Map>().GetRowOrDefault(mapId);
            if (map is null)
                return false;

            var agentMap = AgentMap.Instance();
            if (agentMap == null)
                return false;

            var (internalX, internalY) = WorldToInternalCoordinates(worldX, worldZ, map.Value);
            agentMap->FlagMarkerCount = 0;
            agentMap->SetFlagMapMarker(territoryId, mapId, internalX, internalY, iconId);
            agentMap->TempMapMarkerCount = 0;
            agentMap->AddGatheringTempMarker(
                internalX,
                internalY,
                tempMarkerRadius,
                FishingIconId,
                TempMarkerStyle,
                label);

            if (openMap)
                agentMap->OpenMap(mapId, territoryId, label, MapType.GatheringLog);

            return true;
        }
        catch (Exception ex)
        {
            DService.Instance().Log.Error(ex, "FPG 插旗失败");
            return false;
        }
    }

    private static (int X, int Y) WorldToInternalCoordinates(float worldX, float worldZ, Map map)
    {
        var mapCoords = PositionHelper.WorldToMap(new Vector2(worldX, worldZ), map);
        return (
            IntegerToInternal((int)(mapCoords.X * 100f), map.SizeFactor / 100f) - map.OffsetX,
            IntegerToInternal((int)(mapCoords.Y * 100f), map.SizeFactor / 100f) - map.OffsetY);
    }

    private static int IntegerToInternal(int coord, double scale)
    {
        return (int)(coord - 100 - 2048 / scale) / 2;
    }
}

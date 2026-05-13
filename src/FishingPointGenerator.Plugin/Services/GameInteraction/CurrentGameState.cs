using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;
using OmenTools;

namespace FishingPointGenerator.Plugin.Services.GameInteraction;

internal static unsafe class CurrentGameState
{
    public static bool IsCurrentTerritoryFlyable()
    {
        try
        {
            var territoryId = DService.Instance().ClientState.TerritoryType;
            if (territoryId == 0)
                return false;

            var territory = DService.Instance().Data.GetExcelSheet<TerritoryType>().GetRowOrDefault(territoryId);
            if (territory is null)
                return false;

            var aetherCurrentComp = territory.Value.AetherCurrentCompFlgSet.RowId;
            if (aetherCurrentComp == 0)
                return false;

            var playerState = PlayerState.Instance();
            return playerState != null && playerState->IsAetherCurrentZoneComplete(aetherCurrentComp);
        }
        catch (Exception ex)
        {
            DService.Instance().Log.Warning(ex, "FPG 检查当前区域飞行状态失败");
            return false;
        }
    }
}

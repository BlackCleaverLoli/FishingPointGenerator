using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using OmenTools;
using OmenTools.Interop.Game.ExecuteCommand.Implementations;

namespace FishingPointGenerator.Plugin.Services.GameInteraction;

internal sealed unsafe class PlayerFishingActionService
{
    private const uint MountRouletteGeneralActionId = 9;

    public bool MountIfNeeded()
    {
        var condition = DService.Instance().Condition;
        if (condition[ConditionFlag.Mounted] || condition[ConditionFlag.RidingPillion])
            return true;

        if (condition[ConditionFlag.Mounting]
            || condition[ConditionFlag.Mounting71]
            || condition[ConditionFlag.Casting]
            || condition[ConditionFlag.Casting87])
            return false;

        var actionManager = ActionManager.Instance();
        if (actionManager == null)
            return false;

        if (actionManager->GetActionStatus(ActionType.GeneralAction, MountRouletteGeneralActionId) != 0)
            return false;

        actionManager->UseAction(ActionType.GeneralAction, MountRouletteGeneralActionId);
        return false;
    }

    public bool DismountIfNeeded()
    {
        var condition = DService.Instance().Condition;
        if (!condition[ConditionFlag.Mounted]
            && !condition[ConditionFlag.RidingPillion]
            && !condition[ConditionFlag.Mounting]
            && !condition[ConditionFlag.Mounting71])
            return true;

        if (condition[ConditionFlag.Mounting] || condition[ConditionFlag.Mounting71])
            return false;

        MountCommand.Dismount();
        return false;
    }

    public bool Face(float rotation)
    {
        var player = DService.Instance().ObjectTable.LocalPlayer;
        if (player is null)
            return false;

        player.ToStruct()->SetRotation(rotation);
        return true;
    }

    public void Cast()
    {
        FishingCommand.Cast();
    }

    public void QuitFishing()
    {
        FishingCommand.Quit();
    }
}

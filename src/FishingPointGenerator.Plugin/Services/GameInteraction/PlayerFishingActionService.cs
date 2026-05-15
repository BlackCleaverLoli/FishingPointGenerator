using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using OmenTools;
using OmenTools.Interop.Game.ExecuteCommand.Implementations;
using OmenTools.Interop.Game.Models.Native;

namespace FishingPointGenerator.Plugin.Services.GameInteraction;

internal sealed unsafe class PlayerFishingActionService
{
    private const uint FishingCastActionId = 289;
    private const uint MountRouletteGeneralActionId = 9;
    private const uint FishingInterruptActionId = 299;
    private static readonly TimeSpan MountRetryDelay = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan DismountRetryDelay = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan CastRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan InterruptRetryDelay = TimeSpan.FromSeconds(1);

    private DateTimeOffset lastMountAttemptAt = DateTimeOffset.MinValue;
    private DateTimeOffset lastDismountAttemptAt = DateTimeOffset.MinValue;
    private DateTimeOffset lastCastAttemptAt = DateTimeOffset.MinValue;
    private DateTimeOffset lastInterruptAttemptAt = DateTimeOffset.MinValue;

    public bool MountIfNeeded()
    {
        var condition = DService.Instance().Condition;
        if (condition[ConditionFlag.Mounted] || condition[ConditionFlag.RidingPillion] || IsPlayerFlying())
        {
            lastMountAttemptAt = DateTimeOffset.MinValue;
            return true;
        }

        if (condition[ConditionFlag.Mounting]
            || condition[ConditionFlag.Mounting71]
            || condition[ConditionFlag.Casting]
            || condition[ConditionFlag.Casting87]
            || condition[ConditionFlag.Fishing])
            return false;

        if (DateTimeOffset.UtcNow - lastMountAttemptAt < MountRetryDelay)
            return false;

        var actionManager = ActionManager.Instance();
        if (actionManager == null)
            return false;

        if (actionManager->GetActionStatus(ActionType.GeneralAction, MountRouletteGeneralActionId) != 0)
            return false;

        lastMountAttemptAt = DateTimeOffset.UtcNow;
        actionManager->UseAction(ActionType.GeneralAction, MountRouletteGeneralActionId);
        return false;
    }

    public bool DismountIfNeeded()
    {
        var condition = DService.Instance().Condition;
        var isMounted = condition[ConditionFlag.Mounted]
            || condition[ConditionFlag.RidingPillion]
            || IsPlayerFlying();
        if (!isMounted
            && !condition[ConditionFlag.Mounting]
            && !condition[ConditionFlag.Mounting71])
        {
            lastDismountAttemptAt = DateTimeOffset.MinValue;
            return true;
        }

        if (condition[ConditionFlag.Mounting] || condition[ConditionFlag.Mounting71])
            return false;

        if (DateTimeOffset.UtcNow - lastDismountAttemptAt < DismountRetryDelay)
            return false;

        var actionManager = ActionManager.Instance();
        if (actionManager == null)
            return false;

        lastDismountAttemptAt = DateTimeOffset.UtcNow;
        actionManager->UseAction(ActionType.GeneralAction, MountRouletteGeneralActionId, 0);
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

    public bool IsFreeForAutoSurvey()
    {
        var condition = DService.Instance().Condition;
        return DService.Instance().ObjectTable.LocalPlayer is not null
            && !condition[ConditionFlag.Fishing]
            && !condition[ConditionFlag.Casting]
            && !condition[ConditionFlag.Casting87]
            && !condition[ConditionFlag.Mounting]
            && !condition[ConditionFlag.Mounting71]
            && !condition[ConditionFlag.Mounted]
            && !condition[ConditionFlag.RidingPillion]
            && !IsPlayerFlying();
    }

    public bool IsFishingActive()
    {
        var condition = DService.Instance().Condition;
        return condition[ConditionFlag.Fishing];
    }

    public bool IsMountedOrFlying()
    {
        var condition = DService.Instance().Condition;
        return condition[ConditionFlag.Mounted]
            || condition[ConditionFlag.RidingPillion]
            || IsPlayerFlying();
    }

    public FishingCastAttempt TryCast(float rotation)
    {
        if (!IsFreeForAutoSurvey())
            return FishingCastAttempt.NotFree;

        if (!Face(rotation))
            return FishingCastAttempt.PlayerUnavailable;

        if (DateTimeOffset.UtcNow - lastCastAttemptAt < CastRetryDelay)
            return FishingCastAttempt.Throttled;

        var actionManager = ActionManager.Instance();
        if (actionManager == null)
            return FishingCastAttempt.ActionManagerUnavailable;

        if (actionManager->GetActionStatus(ActionType.Action, FishingCastActionId) != 0)
            return FishingCastAttempt.ActionUnavailable;

        lastCastAttemptAt = DateTimeOffset.UtcNow;
        return actionManager->UseAction(ActionType.Action, FishingCastActionId, 0)
            ? FishingCastAttempt.Issued
            : FishingCastAttempt.ActionRejected;
    }

    public FishingInterruptAttempt InterruptFishingIfNeeded()
    {
        if (IsFreeForAutoSurvey())
        {
            lastInterruptAttemptAt = DateTimeOffset.MinValue;
            return FishingInterruptAttempt.Idle;
        }

        var condition = DService.Instance().Condition;
        if (condition[ConditionFlag.Casting]
            || condition[ConditionFlag.Casting87]
            || condition[ConditionFlag.Mounting]
            || condition[ConditionFlag.Mounting71])
            return FishingInterruptAttempt.Waiting;

        if (DateTimeOffset.UtcNow - lastInterruptAttemptAt < InterruptRetryDelay)
            return FishingInterruptAttempt.Waiting;

        var actionManager = ActionManager.Instance();
        if (actionManager == null)
            return FishingInterruptAttempt.Waiting;

        if (actionManager->GetActionStatus(ActionType.Action, FishingInterruptActionId) != 0)
            return FishingInterruptAttempt.Waiting;

        lastInterruptAttemptAt = DateTimeOffset.UtcNow;
        return actionManager->UseAction(ActionType.Action, FishingInterruptActionId, 0)
            ? FishingInterruptAttempt.Issued
            : FishingInterruptAttempt.Waiting;
    }

    public bool QuitFishingCommandIfNeeded()
    {
        if (IsFreeForAutoSurvey())
            return true;

        if (DateTimeOffset.UtcNow - lastInterruptAttemptAt < InterruptRetryDelay)
            return false;

        lastInterruptAttemptAt = DateTimeOffset.UtcNow;
        FishingCommand.Quit();
        return false;
    }

    private static bool IsPlayerFlying()
    {
        try
        {
            var controller = PlayerController.Instance();
            return controller != null && controller->MoveControllerFly.IsFlying != 0;
        }
        catch
        {
            return false;
        }
    }
}

internal enum FishingInterruptAttempt
{
    Idle,
    Issued,
    Waiting,
}

internal enum FishingCastAttempt
{
    Issued,
    NotFree,
    PlayerUnavailable,
    Throttled,
    ActionManagerUnavailable,
    ActionUnavailable,
    ActionRejected,
}

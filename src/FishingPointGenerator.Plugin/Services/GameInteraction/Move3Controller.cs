using System.Numerics;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using OmenTools;
using OmenTools.Interop.Game;
using OmenTools.Interop.Game.Models;

namespace FishingPointGenerator.Plugin.Services.GameInteraction;

internal sealed class Move3Controller : IDisposable
{
    private MovementInputController? movement;
    private Vector3 targetPosition;
    private float stopDistance;
    private float? lockedFaceRotation;
    private bool disposed;
    private readonly CompSig gameObjectSetRotationSig = new(
        "40 53 48 83 EC ?? F3 0F 10 81 ?? ?? ?? ?? 48 8B D9 0F 2E C1");
    private Hook<GameObjectSetRotationDelegate>? gameObjectSetRotationHook;

    private delegate void GameObjectSetRotationDelegate(nint obj, float value);

    public Move3Controller()
    {
        movement = new MovementInputController { Precision = 0.15f };
        gameObjectSetRotationHook = gameObjectSetRotationSig.GetHook<GameObjectSetRotationDelegate>(
            GameObjectSetRotationDetour);
        gameObjectSetRotationHook.Enable();
        DService.Instance().Framework.Update += OnFrameworkUpdate;
        DService.Instance().ClientState.TerritoryChanged += OnTerritoryChanged;
    }

    public bool IsMoving { get; private set; }

    public bool TryMoveTo(Vector3 target, float arrivalDistance, float? faceRotation, out string failureMessage)
    {
        failureMessage = string.Empty;
        if (disposed)
        {
            failureMessage = "Move3 已释放";
            return false;
        }

        if (DService.Instance().ObjectTable.LocalPlayer is null)
        {
            failureMessage = "没有可用玩家对象";
            return false;
        }

        if (movement is null)
        {
            failureMessage = "Move3 控制器不可用";
            return false;
        }

        targetPosition = target;
        stopDistance = Math.Max(0.05f, arrivalDistance);
        lockedFaceRotation = faceRotation is { } rotation && float.IsFinite(rotation)
            ? rotation
            : null;
        IsMoving = true;
        movement.DesiredPosition = target;
        movement.Enabled = true;
        return true;
    }

    public void Stop()
    {
        IsMoving = false;
        lockedFaceRotation = null;
        targetPosition = default;
        if (movement is not null)
            movement.Enabled = false;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!IsMoving || targetPosition == default)
            return;

        var player = DService.Instance().ObjectTable.LocalPlayer;
        if (player is null)
        {
            Stop();
            return;
        }

        if (Vector3.Distance(player.Position, targetPosition) <= stopDistance)
            Stop();
    }

    private void OnTerritoryChanged(uint territoryId) => Stop();

    private void GameObjectSetRotationDetour(nint obj, float value)
    {
        var player = DService.Instance().ObjectTable.LocalPlayer;
        if (player is not null
            && obj == player.Address
            && IsMoving
            && lockedFaceRotation is { } rotation)
        {
            gameObjectSetRotationHook?.Original(obj, rotation);
            return;
        }

        gameObjectSetRotationHook?.Original(obj, value);
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        DService.Instance().Framework.Update -= OnFrameworkUpdate;
        DService.Instance().ClientState.TerritoryChanged -= OnTerritoryChanged;
        Stop();
        movement?.Dispose();
        movement = null;
        gameObjectSetRotationHook?.Dispose();
        gameObjectSetRotationHook = null;
    }
}

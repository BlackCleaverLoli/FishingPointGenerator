using System.Numerics;
using FishingPointGenerator.Core.Geometry;
using FishingPointGenerator.Core.Models;
using FishingPointGenerator.Plugin.Services;
using FishingPointGenerator.Plugin.Services.GameInteraction;
using OmenTools;

namespace FishingPointGenerator.Plugin.Services.AutoSurvey;

internal sealed class AutoSurveyRunner : IDisposable
{
    private const float MoveCloseRangeMeters = 0.8f;
    private const float ArrivedDistanceMeters = 1.2f;
    private const float DefaultLandingBackoffMeters = 0.5f;
    private const float MaximumLandingBackoffMeters = 3f;
    private const float CastAdjustStepMeters = 0.25f;
    private const float CastAdjustMoveCloseRangeMeters = 0.12f;
    private const float CastAdjustArrivedDistanceMeters = 0.15f;
    private const float CastAdjustMeshProbeHalfExtentXZ = 0.75f;
    private const float CastAdjustMeshProbeHalfExtentY = 1.5f;
    private const float CastAdjustMaximumHorizontalDistanceFromCandidateMeters = 3.25f;
    private const float CastAdjustMaximumVerticalSnapMeters = 1f;
    private const float FinalSettleTargetDistanceMeters = 1.3f;
    private const float FinalSettleKeepFacingToleranceRadians = 0.20f;
    private const float InitialLandingFloorProbeHeight = 5f;
    private const float InitialLandingMeshProbeHalfExtentXZ = 3f;
    private const float InitialLandingMeshProbeHalfExtentY = 3f;
    private const float InitialLandingSupportProbeRadius = 0.9f;
    private const float InitialLandingSupportProbeHalfRadius = 0.45f;
    private const float InitialLandingSupportHorizontalTolerance = 0.85f;
    private const float InitialLandingSupportHeightTolerance = 0.75f;
    private const float InitialLandingMaximumVerticalSnapMeters = 1.5f;
    private const int InitialLandingMinimumSupportScore = 7;
    private const float DismountRelocateMoveCloseRangeMeters = 0.5f;
    private const float DismountRelocateArrivedDistanceMeters = 0.75f;
    private const float DismountRelocateMeshProbeHalfExtentXZ = 1f;
    private const float DismountRelocateMeshProbeHalfExtentY = 2f;
    private const float DismountRelocateMaximumVerticalSnapMeters = 1f;
    private const int MaximumMoveResendAttempts = 3;
    private static readonly TimeSpan ShortDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan LoopDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan CastRecordTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan MoveResendDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PostMoveMountedTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CastReadyStableDuration = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan CastReadyFallbackDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DismountRelocateRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan InterruptFallbackDelay = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan InterruptGiveUpDelay = TimeSpan.FromSeconds(12);
    private static readonly float[] InitialLandingBackoffDistances = [1.5f, 2f, 2.5f, 3f];
    private static readonly LandingRelocateOffset[] DismountRelocateOffsets =
    [
        new(0.75f, 0f),
        new(-0.75f, 0f),
        new(0f, DefaultLandingBackoffMeters),
        new(0f, -0.75f),
        new(0.75f, -0.75f),
        new(-0.75f, -0.75f),
        new(0.75f, DefaultLandingBackoffMeters),
        new(-0.75f, DefaultLandingBackoffMeters),
        new(1.25f, 0f),
        new(-1.25f, 0f),
    ];

    private readonly SpotWorkflowSession session;
    private readonly VnavmeshQueryService navmesh;
    private readonly PlayerFishingActionService playerActions;

    private AutoSurveyMode mode;
    private AutoSurveyStep step;
    private DateTimeOffset waitUntil;
    private DateTimeOffset castStartedAt;
    private DateTimeOffset interruptStartedAt;
    private AutoSurveyStep stepAfterInterrupt;
    private bool completeRoundAfterInterrupt;
    private SpotCandidate? currentCandidate;
    private int observedCastRecordVersion;
    private int castRecordTimeoutRetryCount;
    private float currentLandingBackoffMeters = DefaultLandingBackoffMeters;
    private float currentLandingSideOffsetMeters;
    private float currentLandingForwardOffsetMeters;
    private Point3? currentMoveDestinationOverride;
    private int castAdjustMoveCount;
    private DateTimeOffset dismountStartedAt = DateTimeOffset.MinValue;
    private DateTimeOffset lastDismountRelocateAttemptAt = DateTimeOffset.MinValue;
    private int dismountRelocateOffsetIndex;
    private DateTimeOffset castReadyWaitStartedAt = DateTimeOffset.MinValue;
    private DateTimeOffset castReadySince = DateTimeOffset.MinValue;
    private DateTimeOffset lastMoveResendAttemptAt = DateTimeOffset.MinValue;
    private int moveResendAttemptCount;
    private Move3Controller? castAdjustMove3;
    private bool currentCandidateMountCompleted;
    private bool disposed;

    public AutoSurveyRunner(
        SpotWorkflowSession session,
        VnavmeshQueryService navmesh,
        PlayerFishingActionService playerActions)
    {
        this.session = session;
        this.navmesh = navmesh;
        this.playerActions = playerActions;
    }

    public bool IsRunning => mode != AutoSurveyMode.None;
    public string StatusText { get; private set; } = "未运行";
    public int CompletedRounds { get; private set; }
    public string CurrentCandidateText => currentCandidate is null
        ? "-"
        : $"{currentCandidate.SourceCandidateId} {FormatPoint(currentCandidate.Position)}";

    public void StartOnce()
    {
        Start(AutoSurveyMode.Once, "自动点亮一次：准备。");
    }

    public void StartLoop()
    {
        Start(AutoSurveyMode.Loop, "循环自动点亮：准备。");
    }

    public void Stop(string message = "自动点亮已停止。")
    {
        if (IsRunning)
            navmesh.StopMovement();
        StopMove3();

        mode = AutoSurveyMode.None;
        step = AutoSurveyStep.Idle;
        currentCandidate = null;
        ResetCandidateApproach();
        StatusText = message;
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        Stop("自动点亮已释放。");
        castAdjustMove3?.Dispose();
        castAdjustMove3 = null;
    }

    public void Poll()
    {
        if (!IsRunning)
            return;

        if (DateTimeOffset.UtcNow < waitUntil)
            return;

        try
        {
            PollCore();
        }
        catch (Exception ex)
        {
            Stop($"自动点亮异常：{ex.Message}");
        }
    }

    private void Start(AutoSurveyMode nextMode, string message)
    {
        navmesh.StopMovement();
        mode = nextMode;
        step = AutoSurveyStep.EnsureTarget;
        waitUntil = default;
        currentCandidate = null;
        observedCastRecordVersion = 0;
        castRecordTimeoutRetryCount = 0;
        castStartedAt = DateTimeOffset.MinValue;
        interruptStartedAt = DateTimeOffset.MinValue;
        stepAfterInterrupt = AutoSurveyStep.Idle;
        completeRoundAfterInterrupt = false;
        ResetCandidateApproach();
        CompletedRounds = 0;
        StatusText = message;
    }

    private void PollCore()
    {
        switch (step)
        {
            case AutoSurveyStep.EnsureTarget:
                EnsureTarget();
                break;
            case AutoSurveyStep.EnsureTerritoryScan:
                EnsureTerritoryScan();
                break;
            case AutoSurveyStep.WaitTerritoryScan:
                WaitTerritoryScan();
                break;
            case AutoSurveyStep.EnsureSpotScan:
                EnsureSpotScan();
                break;
            case AutoSurveyStep.RefreshCandidate:
                RefreshCandidate();
                break;
            case AutoSurveyStep.Mount:
                Mount();
                break;
            case AutoSurveyStep.StartMove:
                StartMove();
                break;
            case AutoSurveyStep.Move:
                Move();
                break;
            case AutoSurveyStep.PostMove:
                PostMove();
                break;
            case AutoSurveyStep.DismountRelocateMove:
                DismountRelocateMove();
                break;
            case AutoSurveyStep.Cast:
                Cast();
                break;
            case AutoSurveyStep.CastAdjustMove:
                CastAdjustMove();
                break;
            case AutoSurveyStep.WaitCastRecord:
                WaitCastRecord();
                break;
            case AutoSurveyStep.InterruptFishing:
                InterruptFishing();
                break;
            case AutoSurveyStep.LoopDelay:
                step = AutoSurveyStep.EnsureSpotScan;
                break;
        }
    }

    private void EnsureTarget()
    {
        if (session.CurrentTerritoryId == 0)
        {
            Stop("自动点亮停止：当前没有可用区域。");
            return;
        }

        if (!session.SelectedTerritoryIsCurrent)
            session.RefreshCurrentTerritory(selectNext: true);

        if (session.CurrentTarget is null)
            session.SelectNextTarget(setMessage: false);

        if (session.CurrentTarget is null)
        {
            Stop("自动点亮停止：当前区域没有可用钓场。");
            return;
        }

        StatusText = $"目标 {session.CurrentTargetDisplayName}：检查区域候选。";
        step = AutoSurveyStep.EnsureTerritoryScan;
    }

    private void EnsureTerritoryScan()
    {
        if (session.TerritoryScanInProgress)
        {
            StatusText = $"等待区域扫描：{session.TerritoryScanProgress?.Message ?? "扫描中"}";
            step = AutoSurveyStep.WaitTerritoryScan;
            return;
        }

        if (HasCurrentTerritorySurvey())
        {
            step = AutoSurveyStep.EnsureSpotScan;
            return;
        }

        session.ScanCurrentTerritory();
        if (session.TerritoryScanInProgress)
        {
            StatusText = "已启动区域扫描，等待完成。";
            step = AutoSurveyStep.WaitTerritoryScan;
            return;
        }

        if (!HasCurrentTerritorySurvey())
            Stop($"自动点亮停止：区域扫描不可用。{session.LastMessage}");
    }

    private void WaitTerritoryScan()
    {
        if (session.TerritoryScanInProgress)
        {
            StatusText = $"等待区域扫描：{session.TerritoryScanProgress?.Message ?? "扫描中"}";
            return;
        }

        if (!HasCurrentTerritorySurvey())
        {
            Stop($"自动点亮停止：区域扫描没有可用候选。{session.LastMessage}");
            return;
        }

        StatusText = $"区域扫描完成：{session.TerritoryCandidateCount} 个候选。";
        step = AutoSurveyStep.EnsureSpotScan;
    }

    private void EnsureSpotScan()
    {
        if (session.CurrentTarget is null)
        {
            step = AutoSurveyStep.EnsureTarget;
            return;
        }

        if (!HasCurrentTargetScan())
            session.ScanCurrentTarget();

        if (!HasCurrentTargetScan())
        {
            Stop($"自动点亮停止：无法派生维护目标候选。{session.LastMessage}");
            return;
        }

        StatusText = $"目标 {session.CurrentTargetDisplayName}：刷新当前候选。";
        step = AutoSurveyStep.RefreshCandidate;
    }

    private void RefreshCandidate()
    {
        session.RefreshAutoSurveyCandidateSelection();
        var selection = session.CurrentCandidateSelection;
        if (selection is null)
        {
            if (!TryAdvanceToNextTarget("维护目标没有可用候选。"))
                Stop($"自动点亮停止：维护目标没有可用候选。{session.LastMessage}");
            return;
        }

        currentCandidate = selection.Candidate;
        ResetCandidateApproach();
        var landingNote = TryPrepareInitialLandingApproach(out var landingMessage)
            ? $"，初始落点 {landingMessage}"
            : string.IsNullOrWhiteSpace(landingMessage)
                ? string.Empty
                : $"，初始落点回退：{landingMessage}";
        StatusText = $"当前候选：{CurrentCandidateText}{landingNote}，准备上坐骑。";
        step = AutoSurveyStep.Mount;
    }

    private void Mount()
    {
        if (playerActions.IsFishingActive())
        {
            BeginInterrupt(
                completeRound: false,
                nextStep: AutoSurveyStep.Mount,
                $"检测到钓鱼状态：准备中断后上坐骑 {CurrentCandidateText}");
            return;
        }

        if (playerActions.MountIfNeeded())
        {
            currentCandidateMountCompleted = true;
            StatusText = $"移动到候选：{CurrentCandidateText}";
            step = AutoSurveyStep.StartMove;
            return;
        }

        StatusText = "等待上坐骑。";
    }

    private void StartMove()
    {
        if (currentCandidate is null)
        {
            step = AutoSurveyStep.RefreshCandidate;
            return;
        }

        var selection = session.CurrentCandidateSelection;
        ResetMoveResend();
        var fly = selection?.CanFly ?? currentCandidate.Reachability == CandidateReachability.Flyable;
        if (!TryStartCurrentMove(fly, MoveCloseRangeMeters, $"vnavmesh 移动中：{CurrentCandidateText}", out var failureMessage))
        {
            lastMoveResendAttemptAt = DateTimeOffset.UtcNow;
            StatusText = $"vnavmesh 移动启动失败，准备重发：{failureMessage}。目标 {CurrentCandidateText}";
            Delay(ShortDelay);
        }

        step = AutoSurveyStep.Move;
    }

    private void Move()
    {
        if (currentCandidate is null)
        {
            step = AutoSurveyStep.RefreshCandidate;
            return;
        }

        if (TryBeginImmediateCastIfAvailable("移动中检测到抛竿可用"))
            return;

        if (IsNearCurrentCandidate())
        {
            navmesh.StopMovement();
            ResetMoveResend();
            BeginPostMove($"已到达候选：{CurrentCandidateText}，准备落地后处理。");
            return;
        }

        if (navmesh.IsPathRunning || navmesh.IsPathfindInProgress)
        {
            var leftDistance = navmesh.PathLeftDistance;
            StatusText = leftDistance > 0f
                ? $"vnavmesh 移动中：剩余 {leftDistance:F1}m，目标 {CurrentCandidateText}"
                : $"vnavmesh 移动中：目标 {CurrentCandidateText}";
            return;
        }

        var selection = session.CurrentCandidateSelection;
        var fly = selection?.CanFly ?? currentCandidate.Reachability == CandidateReachability.Flyable;
        if (TryResendCurrentMove(fly, MoveCloseRangeMeters, "vnavmesh 移动", out var giveUp, out var resendFailure))
            return;

        if (giveUp)
        {
            ResetMoveResend();
            StatusText = $"vnavmesh 未运行且尚未到点，重发仍失败：{resendFailure}。等待人工调整后重选候选。";
            Delay(LoopDelay);
            step = AutoSurveyStep.RefreshCandidate;
            return;
        }
    }

    private void PostMove()
    {
        if (currentCandidate is null)
        {
            step = AutoSurveyStep.RefreshCandidate;
            return;
        }

        if (navmesh.IsPathRunning || navmesh.IsPathfindInProgress)
            navmesh.StopMovement();

        var now = DateTimeOffset.UtcNow;
        var relocateFailure = string.Empty;
        if (playerActions.IsMountedOrFlying())
        {
            if (dismountStartedAt == DateTimeOffset.MinValue)
                dismountStartedAt = now;

            if (now - dismountStartedAt >= PostMoveMountedTimeout
                && TryStartDismountRelocateMove(out relocateFailure))
                return;
        }

        if (!playerActions.DismountIfNeeded())
        {
            ResetCastReadyWait();
            StatusText = !string.IsNullOrWhiteSpace(relocateFailure)
                ? $"等待下坐骑，备用落点暂不可用：{relocateFailure}。目标 {CurrentCandidateText}"
                : playerActions.IsMountedOrFlying()
                    ? $"等待下坐骑：{CurrentCandidateText}"
                    : $"等待下坐骑动作完成：{CurrentCandidateText}";
            Delay(ShortDelay);
            return;
        }

        dismountStartedAt = DateTimeOffset.MinValue;
        lastDismountRelocateAttemptAt = DateTimeOffset.MinValue;

        if (TryBeginImmediateCastIfAvailable("已落地且抛竿可用"))
            return;

        var finalSettleFailure = string.Empty;
        if (TryStartFinalSettleMove(out finalSettleFailure))
            return;

        if (IsCastReadyStable())
        {
            ResetDismountWait();
            ResetCastReadyWait();
            currentMoveDestinationOverride = null;
            StatusText = $"抛竿动作已稳定：{CurrentCandidateText}";
            Delay(ShortDelay);
            step = AutoSurveyStep.Cast;
            return;
        }

        if (castReadyWaitStartedAt == DateTimeOffset.MinValue)
            castReadyWaitStartedAt = now;

        if (now - castReadyWaitStartedAt >= CastReadyFallbackDelay)
        {
            ResetDismountWait();
            ResetCastReadyWait();
            currentMoveDestinationOverride = null;
            StatusText = $"抛竿动作长时间未稳定：回退到靠近/重试流程 {CurrentCandidateText}";
            Delay(ShortDelay);
            step = AutoSurveyStep.Cast;
            return;
        }

        StatusText = string.IsNullOrWhiteSpace(finalSettleFailure)
            ? $"等待抛竿动作稳定：{CurrentCandidateText}"
            : $"Move3 落地直走暂不可用：{finalSettleFailure}。等待抛竿动作稳定 {CurrentCandidateText}";
        Delay(ShortDelay);
    }

    private bool TryStartDismountRelocateMove(out string failureMessage)
    {
        failureMessage = string.Empty;
        if (currentCandidate is null)
            return false;

        if (DateTimeOffset.UtcNow - lastDismountRelocateAttemptAt < DismountRelocateRetryDelay)
            return false;

        if (dismountRelocateOffsetIndex >= DismountRelocateOffsets.Length)
        {
            failureMessage = "附近同高落点已试完";
            return false;
        }

        lastDismountRelocateAttemptAt = DateTimeOffset.UtcNow;
        var offset = DismountRelocateOffsets[dismountRelocateOffsetIndex];
        var requestedDestination = GetMoveDestination(
            currentCandidate,
            currentLandingBackoffMeters,
            offset.SideMeters,
            offset.ForwardMeters);
        dismountRelocateOffsetIndex++;
        var meshResult = navmesh.QueryNearestReachablePoint(
            requestedDestination.ToVector3(),
            DismountRelocateMeshProbeHalfExtentXZ,
            DismountRelocateMeshProbeHalfExtentY);
        if (!meshResult.IsReachable)
        {
            failureMessage = $"备用落点没有可达 mesh：{meshResult.Message}";
            return false;
        }

        var destination = Point3.From(meshResult.Point);
        var verticalDelta = MathF.Abs(destination.Y - currentCandidate.Position.Y);
        if (verticalDelta > DismountRelocateMaximumVerticalSnapMeters)
        {
            failureMessage = $"备用落点高度偏差过大：请求 {FormatPoint(requestedDestination)}，实际 {FormatPoint(destination)}，dy={verticalDelta:F2}m";
            return false;
        }

        var selection = session.CurrentCandidateSelection;
        StopMove3();
        var result = navmesh.MoveCloseTo(
            destination.ToVector3(),
            selection?.CanFly ?? currentCandidate.Reachability == CandidateReachability.Flyable,
            DismountRelocateMoveCloseRangeMeters);

        if (!result.IsStarted)
        {
            failureMessage = result.Message;
            return false;
        }

        ResetMoveResend();
        currentLandingSideOffsetMeters = offset.SideMeters;
        currentLandingForwardOffsetMeters = offset.ForwardMeters;
        currentMoveDestinationOverride = destination;
        StatusText = $"长时间无法下坐骑：尝试附近同高落点 {dismountRelocateOffsetIndex}/{DismountRelocateOffsets.Length} {FormatPoint(destination)}";
        Delay(ShortDelay);
        step = AutoSurveyStep.DismountRelocateMove;
        return true;
    }

    private void DismountRelocateMove()
    {
        if (currentCandidate is null)
        {
            step = AutoSurveyStep.RefreshCandidate;
            return;
        }

        if (IsNearCurrentMoveDestination(DismountRelocateArrivedDistanceMeters))
        {
            navmesh.StopMovement();
            ResetMoveResend();
            BeginPostMove($"已到达备用落点：{FormatPoint(GetCurrentMoveDestination())}，准备落地后处理。");
            return;
        }

        if (navmesh.IsPathRunning || navmesh.IsPathfindInProgress)
        {
            var leftDistance = navmesh.PathLeftDistance;
            StatusText = leftDistance > 0f
                ? $"前往备用落点：剩余 {leftDistance:F1}m，目标 {CurrentCandidateText}"
                : $"前往备用落点：目标 {CurrentCandidateText}";
            return;
        }

        var selection = session.CurrentCandidateSelection;
        var fly = selection?.CanFly ?? currentCandidate.Reachability == CandidateReachability.Flyable;
        if (TryResendCurrentMove(fly, DismountRelocateMoveCloseRangeMeters, "备用落点移动", out var giveUp, out var resendFailure))
            return;

        if (giveUp)
        {
            ResetMoveResend();
            BeginPostMove($"备用落点移动多次停止或重发失败：{resendFailure}。继续落地后处理 {CurrentCandidateText}");
        }
    }

    private void Cast()
    {
        if (TryCompleteDelayedCastRecord())
            return;

        if (currentCandidate is null)
        {
            step = AutoSurveyStep.RefreshCandidate;
            return;
        }

        if (!playerActions.IsFreeForAutoSurvey())
        {
            StatusText = $"等待自由态后抛竿：{CurrentCandidateText}";
            Delay(ShortDelay);
            return;
        }

        observedCastRecordVersion = session.CastRecordVersion;
        var castAttempt = playerActions.TryCast();
        if (castAttempt == FishingCastAttempt.Issued)
        {
            castStartedAt = DateTimeOffset.UtcNow;
            StatusText = castRecordTimeoutRetryCount > 0
                ? $"已重新发送抛竿 {castRecordTimeoutRetryCount}：等待日志记录 {CurrentCandidateText}"
                : $"已发送抛竿：等待日志记录 {CurrentCandidateText}";
            Delay(ShortDelay);
            step = AutoSurveyStep.WaitCastRecord;
            return;
        }

        var adjustFailure = string.Empty;
        if (castAttempt == FishingCastAttempt.ActionUnavailable && TryStartCastAdjustMove(out adjustFailure))
            return;

        if (castAttempt == FishingCastAttempt.PlayerUnavailable)
        {
            StatusText = $"没有可用玩家对象，等待人工回到可操作状态后继续抛竿：{CurrentCandidateText}";
            Delay(LoopDelay);
            return;
        }

        StatusText = castAttempt switch
        {
            FishingCastAttempt.NotFree => $"等待自由态后抛竿：{CurrentCandidateText}",
            FishingCastAttempt.Throttled => $"等待抛竿节流：{CurrentCandidateText}",
            FishingCastAttempt.ActionManagerUnavailable => $"等待动作系统可用：{CurrentCandidateText}",
            FishingCastAttempt.ActionUnavailable when !string.IsNullOrWhiteSpace(adjustFailure) =>
                $"抛竿不可用，靠近移动失败：{adjustFailure}。目标 {CurrentCandidateText}",
            FishingCastAttempt.ActionUnavailable => $"等待抛竿动作可用：{CurrentCandidateText}",
            FishingCastAttempt.ActionRejected => $"抛竿动作被拒绝，等待重试：{CurrentCandidateText}",
            _ => $"等待抛竿：{CurrentCandidateText}",
        };

        Delay(ShortDelay);
    }

    private bool TryStartCastAdjustMove(out string failureMessage)
    {
        failureMessage = string.Empty;
        if (currentCandidate is null)
            return false;

        if (TryStartFinalSettleMove(out failureMessage))
            return true;

        if (currentLandingBackoffMeters <= 0f)
            return false;

        var nextBackoff = MathF.Max(0f, currentLandingBackoffMeters - CastAdjustStepMeters);
        if (MathF.Abs(nextBackoff - currentLandingBackoffMeters) <= 0.001f)
            return false;

        var requestedDestination = GetMoveDestination(
            currentCandidate,
            nextBackoff,
            currentLandingSideOffsetMeters,
            currentLandingForwardOffsetMeters);
        if (!TryResolveCastAdjustDestination(requestedDestination, out var destination, out failureMessage))
            return false;

        var usedMove3 = false;
        if (TryStartMove3To(
            destination,
            CastAdjustArrivedDistanceMeters,
            keepFacing: false,
            out var move3Failure))
        {
            usedMove3 = true;
        }
        else
        {
            StopMove3();
            var result = navmesh.MoveCloseTo(
                destination.ToVector3(),
                fly: false,
                range: CastAdjustMoveCloseRangeMeters);
            if (!result.IsStarted)
            {
                failureMessage = string.IsNullOrWhiteSpace(move3Failure)
                    ? result.Message
                    : $"Move3 不可用：{move3Failure}；vnavmesh：{result.Message}";
                return false;
            }
        }

        ResetMoveResend();
        currentLandingBackoffMeters = nextBackoff;
        currentMoveDestinationOverride = destination;
        castAdjustMoveCount++;
        StatusText = usedMove3
            ? $"抛竿不可用：Move3 直走调面向 {castAdjustMoveCount}，目标 {CurrentCandidateText}"
            : $"抛竿不可用：vnavmesh 小步靠近 {castAdjustMoveCount}，目标 {CurrentCandidateText}";
        Delay(ShortDelay);
        step = AutoSurveyStep.CastAdjustMove;
        return true;
    }

    private bool TryStartFinalSettleMove(out string failureMessage)
    {
        failureMessage = string.Empty;
        if (!TryBuildFinalSettleDestination(out var destination, out var keepFacing, out failureMessage))
            return false;

        if (!TryStartMove3To(
            destination,
            CastAdjustArrivedDistanceMeters,
            keepFacing,
            out failureMessage))
            return false;

        ResetMoveResend();
        if (currentCandidate is not null)
        {
            var remainingDistance = destination.HorizontalDistanceTo(currentCandidate.Position);
            currentLandingBackoffMeters = MathF.Min(currentLandingBackoffMeters, MathF.Max(0f, remainingDistance));
        }

        currentMoveDestinationOverride = destination;
        castAdjustMoveCount++;
        StatusText = keepFacing
            ? $"抛竿不可用：Move3 固定面向直走 {castAdjustMoveCount}，目标 {CurrentCandidateText}"
            : $"抛竿不可用：Move3 最终直走调面向 {castAdjustMoveCount}，目标 {CurrentCandidateText}";
        Delay(ShortDelay);
        step = AutoSurveyStep.CastAdjustMove;
        return true;
    }

    private bool TryBuildFinalSettleDestination(
        out Point3 destination,
        out bool keepFacing,
        out string failureMessage)
    {
        destination = default;
        keepFacing = false;
        failureMessage = string.Empty;
        if (currentCandidate is null)
        {
            failureMessage = "没有当前候选";
            return false;
        }

        var player = DService.Instance().ObjectTable.LocalPlayer;
        if (player is null)
        {
            failureMessage = "没有可用玩家对象";
            return false;
        }

        var forward = GetForwardVector(currentCandidate.Rotation);
        destination = new Point3(
            player.Position.X + (forward.X * FinalSettleTargetDistanceMeters),
            player.Position.Y,
            player.Position.Z + (forward.Z * FinalSettleTargetDistanceMeters));

        var targetDistance = destination.HorizontalDistanceTo(currentCandidate.Position);
        if (targetDistance > CastAdjustMaximumHorizontalDistanceFromCandidateMeters)
        {
            failureMessage = $"最终直走目标离原候选过远：{targetDistance:F1}m";
            return false;
        }

        keepFacing = AngleMath.AngularDistance(player.Rotation, currentCandidate.Rotation)
            <= FinalSettleKeepFacingToleranceRadians;
        return true;
    }

    private void CastAdjustMove()
    {
        if (currentCandidate is null)
        {
            step = AutoSurveyStep.RefreshCandidate;
            return;
        }

        if (TryBeginImmediateCastIfAvailable("小步靠近后抛竿可用"))
            return;

        if (TryGetCurrentCandidateDistance(out var currentDistance)
            && currentDistance > CastAdjustMaximumHorizontalDistanceFromCandidateMeters)
        {
            navmesh.StopMovement();
            StopMove3();
            ResetMoveResend();
            currentMoveDestinationOverride = null;
            StatusText = $"小步靠近偏离原候选过远：{currentDistance:F1}m，重新选择候选。";
            Delay(ShortDelay);
            step = AutoSurveyStep.RefreshCandidate;
            return;
        }

        if (IsNearCurrentMoveDestination(CastAdjustArrivedDistanceMeters))
        {
            navmesh.StopMovement();
            StopMove3();
            ResetMoveResend();
            StatusText = $"已小步靠近：{CurrentCandidateText}，重新尝试抛竿。";
            Delay(ShortDelay);
            step = AutoSurveyStep.Cast;
            return;
        }

        if (castAdjustMove3?.IsMoving == true)
        {
            StatusText = $"Move3 小步靠近中：目标 {CurrentCandidateText}";
            return;
        }

        if (navmesh.IsPathRunning || navmesh.IsPathfindInProgress)
        {
            var leftDistance = navmesh.PathLeftDistance;
            StatusText = leftDistance > 0f
                ? $"小步靠近中：剩余 {leftDistance:F1}m，目标 {CurrentCandidateText}"
                : $"小步靠近中：目标 {CurrentCandidateText}";
            return;
        }

        if (TryResendCurrentMove(fly: false, CastAdjustMoveCloseRangeMeters, "小步靠近", out var giveUp, out var resendFailure))
            return;

        if (giveUp)
        {
            ResetMoveResend();
            StatusText = $"小步靠近多次停止或重发失败：{resendFailure}。重新尝试抛竿 {CurrentCandidateText}";
            Delay(ShortDelay);
            step = AutoSurveyStep.Cast;
        }
    }

    private bool TryResolveCastAdjustDestination(
        Point3 requestedDestination,
        out Point3 destination,
        out string failureMessage)
    {
        destination = default;
        failureMessage = string.Empty;
        if (currentCandidate is null)
        {
            failureMessage = "没有当前候选";
            return false;
        }

        var requestedDistance = requestedDestination.HorizontalDistanceTo(currentCandidate.Position);
        if (requestedDistance > CastAdjustMaximumHorizontalDistanceFromCandidateMeters)
        {
            failureMessage = $"小步目标离原候选过远：{requestedDistance:F1}m";
            return false;
        }

        var meshResult = navmesh.QueryNearestReachablePoint(
            requestedDestination.ToVector3(),
            CastAdjustMeshProbeHalfExtentXZ,
            CastAdjustMeshProbeHalfExtentY);
        if (!meshResult.IsReachable)
        {
            failureMessage = $"小步目标没有可达 mesh：{meshResult.Message}";
            return false;
        }

        destination = Point3.From(meshResult.Point);
        var actualDistance = destination.HorizontalDistanceTo(currentCandidate.Position);
        if (actualDistance > CastAdjustMaximumHorizontalDistanceFromCandidateMeters)
        {
            failureMessage = $"小步实际落点离原候选过远：请求 {FormatPoint(requestedDestination)}，实际 {FormatPoint(destination)}，dist={actualDistance:F1}m";
            return false;
        }

        var verticalDelta = MathF.Abs(destination.Y - currentCandidate.Position.Y);
        if (verticalDelta > CastAdjustMaximumVerticalSnapMeters)
        {
            failureMessage = $"小步实际落点高度偏差过大：请求 {FormatPoint(requestedDestination)}，实际 {FormatPoint(destination)}，dy={verticalDelta:F2}m";
            return false;
        }

        return true;
    }

    private void WaitCastRecord()
    {
        if (session.CastRecordVersion == observedCastRecordVersion)
        {
            if (DateTimeOffset.UtcNow - castStartedAt >= CastRecordTimeout)
            {
                castRecordTimeoutRetryCount++;
                castStartedAt = DateTimeOffset.MinValue;
                if (playerActions.IsFreeForAutoSurvey())
                {
                    StatusText = $"等待抛竿日志超时：第 {castRecordTimeoutRetryCount} 次，继续重试抛竿 {CurrentCandidateText}";
                    Delay(ShortDelay);
                    step = AutoSurveyStep.Cast;
                    return;
                }

                BeginInterrupt(
                    completeRound: false,
                    nextStep: AutoSurveyStep.Cast,
                    $"等待抛竿日志超时：第 {castRecordTimeoutRetryCount} 次，准备中断后重试抛竿 {CurrentCandidateText}");
                return;
            }

            StatusText = $"等待抛竿日志：{CurrentCandidateText}";
            return;
        }

        castStartedAt = DateTimeOffset.MinValue;
        castRecordTimeoutRetryCount = 0;
        BeginInterrupt(
            completeRound: true,
            nextStep: AutoSurveyStep.LoopDelay,
            $"已记录抛竿：准备中断 {CurrentCandidateText}");
    }

    private void BeginInterrupt(bool completeRound, AutoSurveyStep nextStep, string statusText)
    {
        interruptStartedAt = DateTimeOffset.UtcNow;
        completeRoundAfterInterrupt = completeRound;
        stepAfterInterrupt = nextStep;
        StatusText = statusText;
        step = AutoSurveyStep.InterruptFishing;
        Delay(ShortDelay);
    }

    private void InterruptFishing()
    {
        if (!completeRoundAfterInterrupt
            && castRecordTimeoutRetryCount > 0
            && session.CastRecordVersion != observedCastRecordVersion)
        {
            castStartedAt = DateTimeOffset.MinValue;
            castRecordTimeoutRetryCount = 0;
            completeRoundAfterInterrupt = true;
            stepAfterInterrupt = AutoSurveyStep.LoopDelay;
            StatusText = $"延迟收到抛竿日志：准备完成本轮 {CurrentCandidateText}";
        }

        var interruptResult = playerActions.InterruptFishingIfNeeded();
        if (interruptResult == FishingInterruptAttempt.Idle)
        {
            FinishInterrupt();
            return;
        }

        var elapsed = DateTimeOffset.UtcNow - interruptStartedAt;
        if (elapsed >= InterruptGiveUpDelay)
        {
            StatusText = $"中断后仍未回到自由态，等待人工解除后继续：{CurrentCandidateText}";
            Delay(LoopDelay);
            return;
        }

        if (elapsed >= InterruptFallbackDelay)
        {
            StatusText = $"等待收竿回到自由态：{CurrentCandidateText}";
            Delay(ShortDelay);
            return;
        }

        StatusText = interruptResult == FishingInterruptAttempt.Issued
            ? $"已发送中断技能：等待自由态 {CurrentCandidateText}"
            : $"等待可中断状态：{CurrentCandidateText}";
        Delay(ShortDelay);
    }

    private bool TryCompleteDelayedCastRecord()
    {
        if (castRecordTimeoutRetryCount <= 0 || session.CastRecordVersion == observedCastRecordVersion)
            return false;

        castStartedAt = DateTimeOffset.MinValue;
        castRecordTimeoutRetryCount = 0;
        BeginInterrupt(
            completeRound: true,
            nextStep: AutoSurveyStep.LoopDelay,
            $"延迟收到抛竿日志：准备完成本轮 {CurrentCandidateText}");
        return true;
    }

    private void FinishInterrupt()
    {
        interruptStartedAt = DateTimeOffset.MinValue;
        if (completeRoundAfterInterrupt)
        {
            FinishRound();
            return;
        }

        completeRoundAfterInterrupt = false;
        StatusText = $"已回到自由态：{CurrentCandidateText}";
        step = stepAfterInterrupt;
        stepAfterInterrupt = AutoSurveyStep.Idle;
        Delay(ShortDelay);
    }

    private void FinishRound()
    {
        CompletedRounds++;
        currentCandidate = null;
        castStartedAt = DateTimeOffset.MinValue;
        castRecordTimeoutRetryCount = 0;
        interruptStartedAt = DateTimeOffset.MinValue;
        completeRoundAfterInterrupt = false;
        stepAfterInterrupt = AutoSurveyStep.Idle;
        ResetCandidateApproach();

        if (mode == AutoSurveyMode.Once)
        {
            Stop($"自动点亮一次完成：已处理 {CompletedRounds} 次抛竿。");
            return;
        }

        StatusText = $"本轮完成：已处理 {CompletedRounds} 次抛竿，准备刷新下一个候选。";
        Delay(LoopDelay);
        step = AutoSurveyStep.LoopDelay;
    }

    private bool TryAdvanceToNextTarget(string reason)
    {
        if (mode != AutoSurveyMode.Loop)
            return false;

        var previous = session.CurrentTarget?.FishingSpotId;
        session.SelectNextTarget(setMessage: false);
        if (session.CurrentTarget is null || session.CurrentTarget.FishingSpotId == previous)
            return false;

        StatusText = $"{reason}已切换到 {session.CurrentTargetDisplayName}。";
        Delay(ShortDelay);
        step = AutoSurveyStep.EnsureSpotScan;
        return true;
    }

    private bool HasCurrentTerritorySurvey()
    {
        return session.CurrentTerritorySurvey is { } survey
            && survey.TerritoryId == session.CurrentTerritoryId
            && survey.Candidates.Count > 0;
    }

    private bool HasCurrentTargetScan()
    {
        return session.CurrentTarget is { } target
            && session.CurrentScan is { } scan
            && scan.Key == target.Key
            && scan.Candidates.Count > 0;
    }

    private bool IsNearCurrentCandidate()
    {
        return IsNearCurrentMoveDestination(ArrivedDistanceMeters);
    }

    private bool IsNearCurrentMoveDestination(float distanceMeters)
    {
        var player = DService.Instance().ObjectTable.LocalPlayer;
        if (player is null || currentCandidate is null)
            return false;

        return GetCurrentMoveDestination()
            .HorizontalDistanceTo(Point3.From(player.Position)) <= distanceMeters;
    }

    private bool TryGetCurrentCandidateDistance(out float distance)
    {
        distance = 0f;
        var player = DService.Instance().ObjectTable.LocalPlayer;
        if (player is null || currentCandidate is null)
            return false;

        distance = Point3.From(player.Position).HorizontalDistanceTo(currentCandidate.Position);
        return true;
    }

    private bool TryBeginImmediateCastIfAvailable(string reason)
    {
        if (!currentCandidateMountCompleted || !playerActions.IsCastActionAvailable())
            return false;

        navmesh.StopMovement();
        StopMove3();
        ResetMoveResend();
        ResetDismountWait();
        ResetCastReadyWait();
        currentMoveDestinationOverride = null;
        StatusText = $"{reason}：准备抛竿 {CurrentCandidateText}";
        Delay(ShortDelay);
        step = AutoSurveyStep.Cast;
        return true;
    }

    private Point3 GetCurrentMoveDestination()
    {
        if (currentCandidate is null)
            return default;
        if (currentMoveDestinationOverride is { } destination)
            return destination;

        return GetMoveDestination(
            currentCandidate,
            currentLandingBackoffMeters,
            currentLandingSideOffsetMeters,
            currentLandingForwardOffsetMeters);
    }

    private bool TryPrepareInitialLandingApproach(out string message)
    {
        message = string.Empty;
        if (currentCandidate is null)
            return false;

        if (!navmesh.IsReady)
        {
            message = "vnavmesh 未就绪，使用 0.5m 回退点";
            return false;
        }

        var forward = GetForwardVector(currentCandidate.Rotation);
        var left = GetLeftVector(forward);
        var found = false;
        var bestScore = int.MinValue;
        var bestBackoff = DefaultLandingBackoffMeters;
        var bestDestination = default(Point3);
        var lastFailure = string.Empty;

        foreach (var backoff in InitialLandingBackoffDistances)
        {
            var requested = GetMoveDestination(currentCandidate, backoff, 0f, 0f);
            if (!TryResolveInitialLandingPoint(requested, out var destination, out lastFailure))
                continue;

            var verticalDelta = MathF.Abs(destination.Y - currentCandidate.Position.Y);
            if (verticalDelta > InitialLandingMaximumVerticalSnapMeters)
            {
                lastFailure = $"落点高度偏差 {verticalDelta:F2}m";
                continue;
            }

            var score = ScoreInitialLandingPoint(destination, forward, left);
            if (score < InitialLandingMinimumSupportScore)
            {
                lastFailure = $"支撑评分 {score}/{InitialLandingMinimumSupportScore}";
                continue;
            }

            if (found && (score < bestScore || (score == bestScore && backoff >= bestBackoff)))
                continue;

            found = true;
            bestScore = score;
            bestBackoff = backoff;
            bestDestination = destination;
        }

        if (!found)
        {
            currentLandingBackoffMeters = DefaultLandingBackoffMeters;
            currentMoveDestinationOverride = null;
            message = string.IsNullOrWhiteSpace(lastFailure)
                ? "没有找到可用 staging 落点，使用 0.5m 回退点"
                : $"{lastFailure}，使用 0.5m 回退点";
            return false;
        }

        currentLandingBackoffMeters = bestBackoff;
        currentMoveDestinationOverride = bestDestination;
        message = $"{bestBackoff:F1}m，支撑 {bestScore}";
        return true;
    }

    private bool TryResolveInitialLandingPoint(
        Point3 requested,
        out Point3 destination,
        out string failureMessage)
    {
        destination = default;
        failureMessage = string.Empty;

        var landing = navmesh.QueryLandingPoint(
            requested.ToVector3(),
            InitialLandingFloorProbeHeight,
            InitialLandingMeshProbeHalfExtentXZ,
            InitialLandingMeshProbeHalfExtentY);
        if (!landing.IsReachable)
        {
            failureMessage = $"找不到地面：{landing.Message}";
            return false;
        }

        var reachable = navmesh.QueryNearestReachablePoint(
            landing.Point,
            InitialLandingMeshProbeHalfExtentXZ,
            InitialLandingMeshProbeHalfExtentY);
        if (!reachable.IsReachable)
        {
            failureMessage = $"找不到可达 mesh：{reachable.Message}";
            return false;
        }

        destination = Point3.From(reachable.Point);
        var horizontalSnap = destination.HorizontalDistanceTo(requested);
        if (horizontalSnap > InitialLandingSupportHorizontalTolerance)
        {
            failureMessage = $"mesh 吸附过远 {horizontalSnap:F2}m";
            return false;
        }

        return true;
    }

    private int ScoreInitialLandingPoint(Point3 landingPoint, Vector3 forward, Vector3 left)
    {
        var score = 0;
        foreach (var offset in EnumerateInitialLandingSupportOffsets(forward, left))
        {
            var probeTarget = landingPoint.Add(offset);
            var probe = navmesh.QueryLandingPoint(
                probeTarget.ToVector3(),
                InitialLandingFloorProbeHeight,
                InitialLandingMeshProbeHalfExtentXZ,
                InitialLandingMeshProbeHalfExtentY);
            if (!probe.IsReachable)
                continue;

            var snappedProbe = Point3.From(probe.Point);
            if (snappedProbe.HorizontalDistanceTo(probeTarget) > InitialLandingSupportHorizontalTolerance)
                continue;

            if (MathF.Abs(snappedProbe.Y - landingPoint.Y) > InitialLandingSupportHeightTolerance)
                continue;

            score++;
        }

        return score;
    }

    private static IEnumerable<Vector3> EnumerateInitialLandingSupportOffsets(Vector3 forward, Vector3 left)
    {
        yield return Vector3.Zero;
        yield return forward * InitialLandingSupportProbeHalfRadius;
        yield return -forward * InitialLandingSupportProbeRadius;
        yield return left * InitialLandingSupportProbeRadius;
        yield return -left * InitialLandingSupportProbeRadius;
        yield return (left - forward) * InitialLandingSupportProbeHalfRadius;
        yield return (-left - forward) * InitialLandingSupportProbeHalfRadius;
        yield return (left + forward) * InitialLandingSupportProbeHalfRadius;
        yield return (-left + forward) * InitialLandingSupportProbeHalfRadius;
    }

    private static Point3 GetMoveDestination(
        SpotCandidate candidate,
        float backoffMeters,
        float sideOffsetMeters,
        float forwardOffsetMeters)
    {
        var clampedBackoff = Math.Clamp(backoffMeters, 0f, MaximumLandingBackoffMeters);
        var forward = GetForwardVector(candidate.Rotation);
        var rightX = forward.Z;
        var rightZ = -forward.X;
        var x = candidate.Position.X
            - (forward.X * clampedBackoff)
            + (rightX * sideOffsetMeters)
            + (forward.X * forwardOffsetMeters);
        var z = candidate.Position.Z
            - (forward.Z * clampedBackoff)
            + (rightZ * sideOffsetMeters)
            + (forward.Z * forwardOffsetMeters);
        return new Point3(x, candidate.Position.Y, z);
    }

    private static Vector3 GetForwardVector(float rotation) =>
        Vector3.Normalize(new Vector3(MathF.Sin(rotation), 0f, MathF.Cos(rotation)));

    private static Vector3 GetLeftVector(Vector3 forward) =>
        Vector3.Normalize(new Vector3(-forward.Z, 0f, forward.X));

    private void ResetCandidateApproach()
    {
        currentLandingBackoffMeters = DefaultLandingBackoffMeters;
        currentLandingSideOffsetMeters = 0f;
        currentLandingForwardOffsetMeters = 0f;
        currentMoveDestinationOverride = null;
        castAdjustMoveCount = 0;
        castRecordTimeoutRetryCount = 0;
        currentCandidateMountCompleted = false;
        ResetMoveResend();
        ResetDismountWait();
        ResetCastReadyWait();
        StopMove3();
    }

    private bool TryStartMove3To(
        Point3 destination,
        float arrivalDistance,
        bool keepFacing,
        out string failureMessage)
    {
        failureMessage = string.Empty;
        if (navmesh.IsNavmeshBuildInProgress || navmesh.IsPathfindInProgress)
        {
            failureMessage = "vnavmesh 正在构建或寻路";
            StopMove3();
            return false;
        }

        if (navmesh.IsPathRunning)
            navmesh.StopMovement();

        try
        {
            castAdjustMove3 ??= new Move3Controller();
            return castAdjustMove3.TryMoveTo(
                destination.ToVector3(),
                arrivalDistance,
                keepFacing,
                out failureMessage);
        }
        catch (Exception ex)
        {
            castAdjustMove3 = null;
            failureMessage = ex.Message;
            return false;
        }
    }

    private void StopMove3()
    {
        if (castAdjustMove3?.IsMoving == true)
            castAdjustMove3.Stop();
    }

    private bool TryStartCurrentMove(
        bool fly,
        float range,
        string statusText,
        out string failureMessage)
    {
        failureMessage = string.Empty;
        if (currentCandidate is null)
        {
            failureMessage = "没有当前候选";
            return false;
        }

        StopMove3();
        var result = navmesh.MoveCloseTo(GetCurrentMoveDestination().ToVector3(), fly, range);
        if (!result.IsStarted)
        {
            failureMessage = result.Message;
            return false;
        }

        StatusText = statusText;
        Delay(ShortDelay);
        return true;
    }

    private bool TryResendCurrentMove(
        bool fly,
        float range,
        string label,
        out bool giveUp,
        out string failureMessage)
    {
        giveUp = false;
        failureMessage = string.Empty;
        if (currentCandidate is null)
        {
            giveUp = true;
            failureMessage = "没有当前候选";
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        var elapsed = now - lastMoveResendAttemptAt;
        if (elapsed < MoveResendDelay)
        {
            var remainingSeconds = Math.Max(0d, (MoveResendDelay - elapsed).TotalSeconds);
            StatusText = $"{label}未运行且尚未到点：等待重发 {moveResendAttemptCount}/{MaximumMoveResendAttempts}，{remainingSeconds:F1}s，目标 {CurrentCandidateText}";
            Delay(ShortDelay);
            return false;
        }

        if (moveResendAttemptCount >= MaximumMoveResendAttempts)
        {
            giveUp = true;
            failureMessage = $"{label}已重发 {moveResendAttemptCount}/{MaximumMoveResendAttempts} 次仍未到点";
            return false;
        }

        moveResendAttemptCount++;
        lastMoveResendAttemptAt = now;
        StopMove3();
        var result = navmesh.MoveCloseTo(GetCurrentMoveDestination().ToVector3(), fly, range);
        if (!result.IsStarted)
        {
            failureMessage = result.Message;
            if (moveResendAttemptCount >= MaximumMoveResendAttempts)
            {
                giveUp = true;
                failureMessage = $"{label}重发失败 {moveResendAttemptCount}/{MaximumMoveResendAttempts}：{result.Message}";
                return false;
            }

            StatusText = $"{label}重发失败 {moveResendAttemptCount}/{MaximumMoveResendAttempts}：{result.Message}，目标 {CurrentCandidateText}";
            Delay(ShortDelay);
            return false;
        }

        StatusText = $"{label}已重发 {moveResendAttemptCount}/{MaximumMoveResendAttempts}：目标 {CurrentCandidateText}";
        Delay(ShortDelay);
        return true;
    }

    private void ResetMoveResend()
    {
        lastMoveResendAttemptAt = DateTimeOffset.MinValue;
        moveResendAttemptCount = 0;
    }

    private void BeginPostMove(string statusText)
    {
        dismountStartedAt = DateTimeOffset.UtcNow;
        lastDismountRelocateAttemptAt = DateTimeOffset.MinValue;
        ResetCastReadyWait();
        StatusText = statusText;
        Delay(ShortDelay);
        step = AutoSurveyStep.PostMove;
    }

    private void ResetDismountWait()
    {
        dismountStartedAt = DateTimeOffset.MinValue;
        lastDismountRelocateAttemptAt = DateTimeOffset.MinValue;
        dismountRelocateOffsetIndex = 0;
    }

    private void ResetCastReadyWait()
    {
        castReadyWaitStartedAt = DateTimeOffset.MinValue;
        castReadySince = DateTimeOffset.MinValue;
    }

    private bool IsCastReadyStable()
    {
        if (!playerActions.IsCastActionAvailable())
        {
            castReadySince = DateTimeOffset.MinValue;
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        if (castReadySince == DateTimeOffset.MinValue)
            castReadySince = now;

        return now - castReadySince >= CastReadyStableDuration;
    }

    private void Delay(TimeSpan delay)
    {
        waitUntil = DateTimeOffset.UtcNow + delay;
    }

    private static string FormatPoint(Point3 point)
    {
        return $"{point.X:F2},{point.Y:F2},{point.Z:F2}";
    }

    private readonly record struct LandingRelocateOffset(float SideMeters, float ForwardMeters);
}

internal enum AutoSurveyMode
{
    None,
    Once,
    Loop,
}

internal enum AutoSurveyStep
{
    Idle,
    EnsureTarget,
    EnsureTerritoryScan,
    WaitTerritoryScan,
    EnsureSpotScan,
    RefreshCandidate,
    Mount,
    StartMove,
    Move,
    PostMove,
    DismountRelocateMove,
    Cast,
    CastAdjustMove,
    WaitCastRecord,
    InterruptFishing,
    LoopDelay,
}

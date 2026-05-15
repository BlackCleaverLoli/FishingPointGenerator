using FishingPointGenerator.Core.Models;
using FishingPointGenerator.Plugin.Services;
using FishingPointGenerator.Plugin.Services.GameInteraction;
using OmenTools;

namespace FishingPointGenerator.Plugin.Services.AutoSurvey;

internal sealed class AutoSurveyRunner
{
    private const float MoveCloseRangeMeters = 0.8f;
    private const float ArrivedDistanceMeters = 1.2f;
    private static readonly TimeSpan ShortDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan LoopDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan CastRecordTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan InterruptFallbackDelay = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan InterruptGiveUpDelay = TimeSpan.FromSeconds(12);

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

        mode = AutoSurveyMode.None;
        step = AutoSurveyStep.Idle;
        currentCandidate = null;
        StatusText = message;
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
        castStartedAt = DateTimeOffset.MinValue;
        interruptStartedAt = DateTimeOffset.MinValue;
        stepAfterInterrupt = AutoSurveyStep.Idle;
        completeRoundAfterInterrupt = false;
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
            case AutoSurveyStep.Dismount:
                Dismount();
                break;
            case AutoSurveyStep.Face:
                Face();
                break;
            case AutoSurveyStep.Cast:
                Cast();
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
            Stop($"自动点亮停止：无法派生当前钓场候选。{session.LastMessage}");
            return;
        }

        StatusText = $"目标 {session.CurrentTargetDisplayName}：刷新当前候选。";
        step = AutoSurveyStep.RefreshCandidate;
    }

    private void RefreshCandidate()
    {
        session.RefreshCandidateSelection();
        var selection = session.CurrentCandidateSelection;
        if (selection is null)
        {
            if (!TryAdvanceToNextTarget("当前目标没有可用候选。"))
                Stop($"自动点亮停止：当前目标没有可用候选。{session.LastMessage}");
            return;
        }

        currentCandidate = selection.Candidate;
        StatusText = $"当前候选：{CurrentCandidateText}，准备上坐骑。";
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
        var result = navmesh.MoveCloseTo(
            currentCandidate.Position.ToVector3(),
            selection?.CanFly ?? currentCandidate.Reachability == CandidateReachability.Flyable,
            MoveCloseRangeMeters);
        if (!result.IsStarted)
        {
            Stop($"自动点亮停止：vnavmesh 移动失败。{result.Message}");
            return;
        }

        StatusText = $"vnavmesh 移动中：{CurrentCandidateText}";
        step = AutoSurveyStep.Move;
    }

    private void Move()
    {
        if (currentCandidate is null)
        {
            step = AutoSurveyStep.RefreshCandidate;
            return;
        }

        if (IsNearCurrentCandidate())
        {
            navmesh.StopMovement();
            StatusText = $"已到达候选：{CurrentCandidateText}，准备下坐骑。";
            Delay(ShortDelay);
            step = AutoSurveyStep.Dismount;
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

        StatusText = $"vnavmesh 未运行且尚未到点：目标 {CurrentCandidateText}。可手动处理或停止自动点亮。";
    }

    private void Dismount()
    {
        if (playerActions.DismountIfNeeded())
        {
            StatusText = $"设置朝向：{CurrentCandidateText}";
            step = AutoSurveyStep.Face;
            return;
        }

        StatusText = "等待下坐骑。";
    }

    private void Face()
    {
        if (currentCandidate is null)
        {
            step = AutoSurveyStep.RefreshCandidate;
            return;
        }

        if (!playerActions.IsFreeForAutoSurvey())
        {
            StatusText = $"等待角色自由态：{CurrentCandidateText}";
            Delay(ShortDelay);
            return;
        }

        if (!playerActions.Face(currentCandidate.Rotation))
        {
            Stop("自动点亮停止：没有可用玩家对象，无法设置朝向。");
            return;
        }

        StatusText = $"已设置朝向：{currentCandidate.Rotation:F3}，准备抛竿。";
        Delay(ShortDelay);
        step = AutoSurveyStep.Cast;
    }

    private void Cast()
    {
        if (!playerActions.IsFreeForAutoSurvey())
        {
            StatusText = $"等待自由态后抛竿：{CurrentCandidateText}";
            Delay(ShortDelay);
            return;
        }

        observedCastRecordVersion = session.CastRecordVersion;
        if (!playerActions.CastIfReady())
        {
            StatusText = $"等待抛竿节流：{CurrentCandidateText}";
            Delay(ShortDelay);
            return;
        }

        castStartedAt = DateTimeOffset.UtcNow;
        StatusText = $"已发送抛竿：等待日志记录 {CurrentCandidateText}";
        Delay(ShortDelay);
        step = AutoSurveyStep.WaitCastRecord;
    }

    private void WaitCastRecord()
    {
        if (session.CastRecordVersion == observedCastRecordVersion)
        {
            if (DateTimeOffset.UtcNow - castStartedAt >= CastRecordTimeout)
            {
                BeginInterrupt(
                    completeRound: false,
                    nextStep: AutoSurveyStep.Cast,
                    $"等待抛竿日志超时：准备中断后重试 {CurrentCandidateText}");
                return;
            }

            StatusText = $"等待抛竿日志：{CurrentCandidateText}";
            return;
        }

        castStartedAt = DateTimeOffset.MinValue;
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
        var interruptResult = playerActions.InterruptFishingIfNeeded();
        if (interruptResult == FishingInterruptAttempt.Idle)
        {
            FinishInterrupt();
            return;
        }

        var elapsed = DateTimeOffset.UtcNow - interruptStartedAt;
        if (elapsed >= InterruptGiveUpDelay)
        {
            Stop($"自动点亮停止：中断后未能回到自由态。目标 {CurrentCandidateText}");
            return;
        }

        if (elapsed >= InterruptFallbackDelay)
        {
            playerActions.QuitFishingCommandIfNeeded();
            StatusText = $"等待收竿回到自由态：{CurrentCandidateText}";
            Delay(ShortDelay);
            return;
        }

        StatusText = interruptResult == FishingInterruptAttempt.Issued
            ? $"已发送中断技能：等待自由态 {CurrentCandidateText}"
            : $"等待可中断状态：{CurrentCandidateText}";
        Delay(ShortDelay);
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
        interruptStartedAt = DateTimeOffset.MinValue;
        completeRoundAfterInterrupt = false;
        stepAfterInterrupt = AutoSurveyStep.Idle;

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
        var player = DService.Instance().ObjectTable.LocalPlayer;
        if (player is null || currentCandidate is null)
            return false;

        return currentCandidate.Position.HorizontalDistanceTo(Point3.From(player.Position)) <= ArrivedDistanceMeters;
    }

    private void Delay(TimeSpan delay)
    {
        waitUntil = DateTimeOffset.UtcNow + delay;
    }

    private static string FormatPoint(Point3 point)
    {
        return $"{point.X:F2},{point.Y:F2},{point.Z:F2}";
    }
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
    Dismount,
    Face,
    Cast,
    WaitCastRecord,
    InterruptFishing,
    LoopDelay,
}

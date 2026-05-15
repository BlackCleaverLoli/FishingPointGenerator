using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FishingPointGenerator.Core;
using FishingPointGenerator.Core.Models;
using FishingPointGenerator.Plugin.Services;
using FishingPointGenerator.Plugin.Services.Scanning;
using OmenTools;

namespace FishingPointGenerator.Plugin.Services.Overlay;

internal sealed unsafe class WorldOverlayRenderer
{
    private const uint TargetColor = 0xff45b7ff;
    private const uint TerritoryCandidateColor = 0x66808080;
    private const uint CandidateColor = 0xffd0d0d0;
    private const uint ConfirmedColor = 0xff55d779;
    private const uint DisabledColor = 0xff8080ff;
    private const uint BlockLabelColor = 0xffc0a060;
    private const uint WarningColor = 0xff4080ff;
    private const uint RiskColor = 0xff38e6ff;
    private const uint FishableDebugFillColor = 0x3345a0ff;
    private const uint FishableDebugEdgeColor = 0xff45a0ff;
    private const uint FishableDebugTextColor = 0xffd0f0ff;
    private const uint WalkableDebugEdgeColor = 0xffff9a2f;
    private const uint WalkableDebugHatchColor = 0xbbff9a2f;
    private const uint WalkableDebugTextColor = 0xffffddb0;
    private const int CircleSegments = 48;
    private const float FacingGuideLengthMeters = 5f;
    private const float DebugSurfaceHatchSpacingMeters = 4f;
    private const float CandidateClickRadiusPixels = 22f;
    private const float CandidateSelectionDragThresholdPixels = 8f;
    private const int VirtualKeyXButton1 = 0x05;
    private const int KeyDownMask = 0x8000;
    private const uint CandidateSelectionFillColor = 0x334080ff;
    private const uint CandidateSelectionEdgeColor = 0xff4080ff;
    private const int MaxSurfaceDebugLabels = 4;
    private const int MaxCandidateLabels = 18;
    private const double OverlaySlowFrameLogThresholdMs = 12d;
    private static readonly TimeSpan OverlayPerfFrameLogInterval = TimeSpan.FromSeconds(1);

    private Matrix4x4 viewProj;
    private Vector4 nearPlane;
    private Vector2 viewportSize;
    private bool previousSelectionMouseDown;
    private bool candidateSelectionActive;
    private Vector2 candidateSelectionStart;
    private Vector2 candidateSelectionEnd;
    private TerritoryMaintenanceDocument? cachedMaintenance;
    private OverlayOwnerIndex cachedOwnerIndex = OverlayOwnerIndex.Empty;
    private TerritorySurveyDocument? cachedStatusSurvey;
    private OverlayOwnerIndex? cachedStatusOwnerIndex;
    private uint cachedStatusTerritoryId;
    private OverlayCandidateStatusIndex cachedStatusIndex = OverlayCandidateStatusIndex.Empty;
    private DateTimeOffset lastOverlayPerfFrameLogAt = DateTimeOffset.MinValue;
    private OverlayPerfFrameSample maxOverlayPerfFrameSample = OverlayPerfFrameSample.Empty;
    private int overlayPerfFrameCount;
    private int overlayPerfSlowFrameCount;

    public void Draw(SpotWorkflowSession session)
    {
        if (!session.OverlayEnabled && !session.OverlayPointDisableMode)
            return;

        var player = DService.Instance().ObjectTable.LocalPlayer;
        if (player is null || !TryUpdateCamera())
            return;

        var drawDistance = Math.Clamp(session.OverlayMaxDistanceMeters, 10f, 1000f);
        var candidateLimit = Math.Clamp(session.OverlayCandidateLimit, 10, 5000);
        session.OverlayMaxDistanceMeters = drawDistance;
        session.OverlayCandidateLimit = candidateLimit;

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGuiHelpers.ForceNextWindowMainViewport();
        ImGuiHelpers.SetNextWindowPosRelativeMainViewport(Vector2.Zero);
        var windowFlags = ImGuiWindowFlags.NoNav
            | ImGuiWindowFlags.NoTitleBar
            | ImGuiWindowFlags.NoScrollbar
            | ImGuiWindowFlags.NoBackground
            | ImGuiWindowFlags.NoInputs;
        ImGui.Begin(
            "fpg_world_overlay",
            windowFlags);
        ImGui.SetWindowSize(ImGui.GetIO().DisplaySize);

        var collectPerfStats = session.OverlayPerformanceDebugEnabled;
        if (!collectPerfStats)
        {
            lastOverlayPerfFrameLogAt = DateTimeOffset.MinValue;
            maxOverlayPerfFrameSample = OverlayPerfFrameSample.Empty;
            overlayPerfFrameCount = 0;
            overlayPerfSlowFrameCount = 0;
        }

        var frameTiming = StartTiming(collectPerfStats);
        var drawList = ImGui.GetWindowDrawList();
        var canDrawCurrentTerritory = session.SelectedTerritoryIsCurrent;
        var showCandidates = session.OverlayShowCandidates || session.OverlayPointDisableMode;
        var targetMs = 0d;
        var surfaceDebugMs = 0d;
        var surfaceDebugStats = OverlaySurfaceDrawStats.Empty;
        var territoryCacheStats = OverlayCandidateDrawStats.Empty;
        var candidateStats = OverlayCandidateDrawStats.Empty;
        if (session.CurrentTarget is not null && canDrawCurrentTerritory)
        {
            var step = StartTiming(collectPerfStats);
            DrawTarget(drawList, session, player.Position);
            targetMs = StopTiming(step);
        }

        if (session.OverlayShowFishableDebug || session.OverlayShowWalkableDebug)
        {
            var step = StartTiming(collectPerfStats);
            surfaceDebugStats = DrawSurfaceDebug(
                drawList,
                session,
                player.Position,
                drawDistance,
                Math.Min(candidateLimit, 512),
                collectPerfStats);
            surfaceDebugMs = StopTiming(step);
        }

        if (session.OverlayShowTerritoryCache && !showCandidates && canDrawCurrentTerritory)
            territoryCacheStats = DrawTerritoryCache(
                drawList,
                session,
                player.Position,
                drawDistance,
                candidateLimit,
                collectPerfStats);

        if (showCandidates && canDrawCurrentTerritory)
            candidateStats = DrawCandidates(
                drawList,
                session,
                player.Position,
                drawDistance,
                candidateLimit,
                collectPerfStats);

        LogOverlayPerfFrame(
            session,
            StopTiming(frameTiming),
            targetMs,
            surfaceDebugMs,
            surfaceDebugStats,
            territoryCacheStats,
            candidateStats,
            drawDistance,
            candidateLimit,
            canDrawCurrentTerritory,
            showCandidates);

        ImGui.End();
        ImGui.PopStyleVar();
    }

    private bool TryUpdateCamera()
    {
        var controlCamera = CameraManager.Instance()->GetActiveCamera();
        var renderCamera = controlCamera != null ? controlCamera->SceneCamera.RenderCamera : null;
        if (renderCamera == null)
            return false;

        var view = renderCamera->ViewMatrix;
        view.M44 = 1f;
        viewProj = view * renderCamera->ProjectionMatrix;
        nearPlane = new Vector4(view.M13, view.M23, view.M33, view.M43 + renderCamera->NearPlane);

        var device = Device.Instance();
        viewportSize = device != null
            ? new Vector2(device->Width, device->Height)
            : ImGui.GetIO().DisplaySize;
        return viewportSize.X > 0f && viewportSize.Y > 0f;
    }

    private void LogOverlayPerfFrame(
        SpotWorkflowSession session,
        double totalMs,
        double targetMs,
        double surfaceDebugMs,
        OverlaySurfaceDrawStats surfaceDebugStats,
        OverlayCandidateDrawStats territoryCacheStats,
        OverlayCandidateDrawStats candidateStats,
        float drawDistance,
        int candidateLimit,
        bool canDrawCurrentTerritory,
        bool showCandidates)
    {
        if (!session.OverlayPerformanceDebugEnabled)
            return;

        var now = DateTimeOffset.UtcNow;
        var currentSample = new OverlayPerfFrameSample(
            totalMs,
            targetMs,
            surfaceDebugMs,
            surfaceDebugStats,
            territoryCacheStats,
            candidateStats,
            drawDistance,
            candidateLimit,
            canDrawCurrentTerritory,
            showCandidates);
        overlayPerfFrameCount++;
        if (totalMs >= OverlaySlowFrameLogThresholdMs)
            overlayPerfSlowFrameCount++;
        if (!maxOverlayPerfFrameSample.HasData || totalMs > maxOverlayPerfFrameSample.TotalMs)
            maxOverlayPerfFrameSample = currentSample;

        if (lastOverlayPerfFrameLogAt != DateTimeOffset.MinValue
            && now - lastOverlayPerfFrameLogAt < OverlayPerfFrameLogInterval)
            return;

        lastOverlayPerfFrameLogAt = now;
        var logSample = maxOverlayPerfFrameSample.HasData ? maxOverlayPerfFrameSample : currentSample;
        var frameCount = overlayPerfFrameCount;
        var slowFrameCount = overlayPerfSlowFrameCount;
        maxOverlayPerfFrameSample = OverlayPerfFrameSample.Empty;
        overlayPerfFrameCount = 0;
        overlayPerfSlowFrameCount = 0;
        var remainingSeconds = Math.Max(0d, (session.OverlayPerformanceDebugUntil - now).TotalSeconds);
        DService.Instance().Log.Information(
            "FPG overlay perf sample: maxTotal={TotalMs:F2}ms maxSlow={IsSlow} frames={FrameCount} slowFrames={SlowFrameCount} remain={RemainingSeconds:F1}s territory={TerritoryId} selected={SelectedTerritoryId} canDraw={CanDrawCurrentTerritory} showCandidates={ShowCandidates} pointDisable={PointDisable} showCache={ShowCache} surfaces={ShowFishable}/{ShowWalkable} target={HasTarget} distance={DrawDistance:F1} limit={CandidateLimit}; maxStages target={TargetMs:F2}ms surface={SurfaceDebugMs:F2}ms territoryCache={TerritoryCacheMs:F2}ms candidates={CandidateMs:F2}ms; {CandidateStats}; {TerritoryCacheStats}; {SurfaceStats}",
            logSample.TotalMs,
            logSample.TotalMs >= OverlaySlowFrameLogThresholdMs,
            frameCount,
            slowFrameCount,
            remainingSeconds,
            session.CurrentTerritoryId,
            session.SelectedTerritoryId,
            logSample.CanDrawCurrentTerritory,
            logSample.ShowCandidates,
            session.OverlayPointDisableMode,
            session.OverlayShowTerritoryCache,
            session.OverlayShowFishableDebug,
            session.OverlayShowWalkableDebug,
            session.CurrentTarget is not null,
            logSample.DrawDistance,
            logSample.CandidateLimit,
            logSample.TargetMs,
            logSample.SurfaceDebugMs,
            logSample.TerritoryCacheStats.TotalMs,
            logSample.CandidateStats.TotalMs,
            FormatCandidateDrawStats(logSample.CandidateStats, "candidates"),
            FormatCandidateDrawStats(logSample.TerritoryCacheStats, "territoryCache"),
            FormatSurfaceDrawStats(logSample.SurfaceDebugStats));
    }

    private static long StartTiming(bool enabled)
    {
        return enabled ? Stopwatch.GetTimestamp() : 0L;
    }

    private static double StopTiming(long timestamp)
    {
        return timestamp == 0L ? 0d : Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds;
    }

    private static string FormatCandidateDrawStats(OverlayCandidateDrawStats stats, string fallbackSource)
    {
        var source = stats.Source == "none" ? fallbackSource : stats.Source;
        if (!stats.HasData)
            return $"{source}: none";

        return FormattableString.Invariant(
            $"{source}: src={stats.SourceCount} visible={stats.VisibleCount} drawn={stats.DrawnCount} lines={stats.LineCount} points={stats.PointCount} labels={stats.LabelCount}+{stats.StatusTextCount} selectable={stats.SelectableCount} blocks={stats.BlockLabelsDrawn}/{stats.BlockLabelsVisible}/{stats.BlockLabelsSource} blockChecks={stats.BlockLabelCandidateChecks} status={stats.StatusRecordedTotal}/{stats.StatusRiskTotal}/{stats.StatusDisabledTotal} cache(owner={(stats.OwnerIndexCacheHit ? "hit" : "miss")},status={(stats.StatusIndexCacheHit ? "hit" : "miss")}) ms(owner={stats.OwnerIndexMs:F2},status={stats.StatusIndexMs:F2},collect={stats.CollectMs:F2},draw={stats.DrawLoopMs:F2},hud={stats.HudTextMs:F2},blocks={stats.BlockLabelsMs:F2},select={stats.SelectionMs:F2},total={stats.TotalMs:F2})");
    }

    private static string FormatSurfaceDrawStats(OverlaySurfaceDrawStats stats)
    {
        if (!stats.HasData)
            return "surface: none";

        return FormattableString.Invariant(
            $"surface: water={stats.Fishable.DrawnCount}/{stats.Fishable.VisibleCount}/{stats.Fishable.SourceCount} walk={stats.Walkable.DrawnCount}/{stats.Walkable.VisibleCount}/{stats.Walkable.SourceCount} near={stats.NearbyCandidates.DrawnCount}/{stats.NearbyCandidates.VisibleCount}/{stats.NearbyCandidates.SourceCount} lines={stats.LineCount} points={stats.PointCount} fills={stats.FilledTriangleCount} labels={stats.LabelCount}+{stats.HudTextCount} ms(water={stats.Fishable.TotalMs:F2},walk={stats.Walkable.TotalMs:F2},near={stats.NearbyCandidates.TotalMs:F2},hud={stats.HudMs:F2},total={stats.TotalMs:F2})");
    }

    private void DrawTarget(ImDrawListPtr drawList, SpotWorkflowSession session, Vector3 playerPosition)
    {
        var target = session.CurrentTarget!;
        var baseY = session.CurrentCandidateSelection?.Candidate.Position.Y ?? playerPosition.Y;
        var center = new Vector3(target.WorldX, baseY, target.WorldZ);

        DrawWorldPoint(drawList, center, 5f, TargetColor, true);
        DrawWorldText(drawList, center + new Vector3(0f, 2f, 0f), $"{target.FishingSpotId} {target.Name}", TargetColor);

        if (session.OverlayShowTargetRadius && target.Radius > 0f)
            DrawWorldCircle(drawList, center, Math.Clamp(target.Radius, 3f, 140f), TargetColor);
    }

    private OverlayCandidateDrawStats DrawCandidates(
        ImDrawListPtr drawList,
        SpotWorkflowSession session,
        Vector3 playerPosition,
        float drawDistance,
        int candidateLimit,
        bool collectPerfStats)
    {
        var survey = session.CurrentTerritorySurvey;
        if (survey is null || survey.Candidates.Count == 0)
            return OverlayCandidateDrawStats.Empty;

        var territoryId = survey.TerritoryId != 0 ? survey.TerritoryId : session.SelectedTerritoryId;
        var ownerIndexCacheHit = ReferenceEquals(cachedMaintenance, session.CurrentTerritoryMaintenance);
        var ownerIndexTiming = StartTiming(collectPerfStats);
        var ownerIndex = GetOwnerIndex(session.CurrentTerritoryMaintenance);
        var ownerIndexMs = StopTiming(ownerIndexTiming);
        var recordedOwnersByKey = ownerIndex.RecordedOwnersByKey;
        var riskOwnersByKey = ownerIndex.RiskOwnersByKey;
        var disabledOwnersByKey = ownerIndex.DisabledOwnersByKey;
        var statusIndexCacheHit = IsCandidateStatusIndexCached(survey, territoryId, ownerIndex);
        var statusIndexTiming = StartTiming(collectPerfStats);
        var statusIndex = GetCandidateStatusIndex(survey, territoryId, ownerIndex);
        var statusIndexMs = StopTiming(statusIndexTiming);

        var playerPoint = Point3.From(playerPosition);
        var collectTiming = StartTiming(collectPerfStats);
        var visibleCandidates = CollectVisibleCandidates(
            survey.Candidates,
            survey.Candidates.Count,
            playerPoint,
            drawDistance,
            candidateLimit);
        var collectMs = StopTiming(collectTiming);
        var clippedCandidateCount = visibleCandidates.ClippedCount;
        var candidates = visibleCandidates.Candidates;

        var mousePosition = ImGui.GetIO().MousePos;
        var selectionMouseDown = IsOverlaySelectionMouseDown();
        var viewportMin = ImGuiHelpers.MainViewport.Pos;
        var viewportMax = viewportMin + viewportSize;
        var mouseInViewport = mousePosition.X >= viewportMin.X
            && mousePosition.Y >= viewportMin.Y
            && mousePosition.X <= viewportMax.X
            && mousePosition.Y <= viewportMax.Y;
        var canStartPointDisableSelection = session.OverlayPointDisableMode
            && selectionMouseDown
            && !previousSelectionMouseDown
            && !session.IsOverlayPointDisableMouseBlockedByUi(mousePosition)
            && mouseInViewport;
        var finishPointDisableSelection = session.OverlayPointDisableMode
            && !selectionMouseDown
            && previousSelectionMouseDown
            && candidateSelectionActive;
        if (!session.OverlayPointDisableMode)
            candidateSelectionActive = false;
        if (canStartPointDisableSelection)
        {
            candidateSelectionActive = true;
            candidateSelectionStart = mousePosition;
            candidateSelectionEnd = mousePosition;
        }
        else if (candidateSelectionActive && selectionMouseDown)
        {
            candidateSelectionEnd = mousePosition;
        }

        List<OverlayCandidateScreenPoint> selectableCandidates = session.OverlayPointDisableMode
            ? new List<OverlayCandidateScreenPoint>(candidates.Count)
            : [];
        var labelCount = 0;
        var lineCount = 0;
        var pointCount = 0;
        var drawLoopTiming = StartTiming(collectPerfStats);
        foreach (var item in candidates)
        {
            var candidate = item.Candidate;
            var standing = ToVector3(candidate.Position);
            var status = GetCandidateStatus(candidate, territoryId, ownerIndex, statusIndex);
            var isConfirmed = status.IsConfirmed;
            var isRisk = status.IsRisk;
            var isDisabled = status.IsDisabled;
            var color = isDisabled ? DisabledColor : isRisk ? RiskColor : isConfirmed ? ConfirmedColor : CandidateColor;
            var pointRadius = isDisabled ? 4f : isRisk ? 3.5f : isConfirmed ? 4f : 3f;
            var shouldLabel = isDisabled || isConfirmed || isRisk || labelCount < MaxCandidateLabels;

            if (session.OverlayPointDisableMode && TryWorldToScreen(standing, out var candidateScreen))
                selectableCandidates.Add(new OverlayCandidateScreenPoint(candidate, candidateScreen));

            DrawFacingGuide(drawList, standing, candidate.Rotation, color);
            lineCount++;
            DrawWorldPoint(drawList, standing, pointRadius, color, true);
            pointCount++;

            if (shouldLabel)
            {
                var recordedOwners = GetRecordedOwners(candidate, territoryId, recordedOwnersByKey);
                var riskOwners = GetRiskOwners(candidate, territoryId, riskOwnersByKey);
                var disabledOwners = GetDisabledOwners(candidate, territoryId, disabledOwnersByKey);
                DrawWorldText(
                    drawList,
                    standing + new Vector3(0f, 1.6f, 0f),
                    BuildCandidateLabel(candidate, item.Distance, recordedOwners, riskOwners, disabledOwners),
                    color);
                labelCount++;
            }
        }
        var drawLoopMs = StopTiming(drawLoopTiming);

        var selectionTiming = StartTiming(collectPerfStats);
        if (candidateSelectionActive && selectionMouseDown)
            DrawCandidateSelectionRect(drawList, candidateSelectionStart, candidateSelectionEnd);
        var selectionMs = StopTiming(selectionTiming);

        var statusTextCount = 0;
        var hudTextTiming = StartTiming(collectPerfStats);
        if (TryWorldToScreen(playerPosition + new Vector3(0f, 2.5f, 0f), out var screen))
        {
            var clippedText = clippedCandidateCount > candidates.Count
                ? $" 显示截断 {candidates.Count}/{clippedCandidateCount}"
                : string.Empty;
            var pointDisableText = session.OverlayPointDisableMode ? " 左键/Mouse4点选/框选禁用/恢复" : string.Empty;
            drawList.AddText(
                screen,
                statusIndex.RiskTotal > 0 ? RiskColor : WarningColor,
                $"FPG overlay 已记录 {statusIndex.RecordedTotal}/{survey.Candidates.Count} 风险 {statusIndex.RiskTotal} 屏蔽 {statusIndex.DisabledTotal}{pointDisableText}{clippedText}");
            statusTextCount = 1;
        }
        var hudTextMs = StopTiming(hudTextTiming);

        var blockLabelStats = DrawTerritoryBlockLabels(
            drawList,
            session,
            playerPosition,
            drawDistance,
            territoryId,
            ownerIndex,
            statusIndex,
            collectPerfStats);
        if (finishPointDisableSelection)
        {
            var selectionApplyTiming = StartTiming(collectPerfStats);
            var selectedCandidates = SelectOverlayCandidates(selectableCandidates, candidateSelectionStart, candidateSelectionEnd, mousePosition);
            candidateSelectionActive = false;
            if (selectedCandidates.Count > 0)
                session.ToggleOverlayCandidatesDisabled(selectedCandidates);
            selectionMs += StopTiming(selectionApplyTiming);
        }

        previousSelectionMouseDown = selectionMouseDown;
        return new OverlayCandidateDrawStats(
            "candidates",
            survey.Candidates.Count,
            clippedCandidateCount,
            candidates.Count,
            lineCount,
            pointCount,
            labelCount,
            statusTextCount,
            selectableCandidates.Count,
            blockLabelStats.SourceBlockCount,
            blockLabelStats.VisibleBlockCount,
            blockLabelStats.DrawnLabelCount,
            blockLabelStats.CandidateCheckCount,
            statusIndex.RecordedTotal,
            statusIndex.RiskTotal,
            statusIndex.DisabledTotal,
            ownerIndexCacheHit,
            statusIndexCacheHit,
            ownerIndexMs,
            statusIndexMs,
            collectMs,
            drawLoopMs,
            hudTextMs,
            blockLabelStats.TotalMs,
            selectionMs);
    }

    private static IReadOnlyList<ApproachCandidate> SelectOverlayCandidates(
        IReadOnlyList<OverlayCandidateScreenPoint> candidates,
        Vector2 selectionStart,
        Vector2 selectionEnd,
        Vector2 mousePosition)
    {
        if (Vector2.DistanceSquared(selectionStart, selectionEnd) < CandidateSelectionDragThresholdPixels * CandidateSelectionDragThresholdPixels)
        {
            ApproachCandidate? clickedCandidate = null;
            var clickedCandidateDistanceSq = CandidateClickRadiusPixels * CandidateClickRadiusPixels;
            foreach (var item in candidates)
            {
                var distanceSq = Vector2.DistanceSquared(mousePosition, item.ScreenPosition);
                if (distanceSq > clickedCandidateDistanceSq)
                    continue;

                clickedCandidate = item.Candidate;
                clickedCandidateDistanceSq = distanceSq;
            }

            return clickedCandidate is null ? [] : [clickedCandidate];
        }

        var min = new Vector2(
            MathF.Min(selectionStart.X, selectionEnd.X),
            MathF.Min(selectionStart.Y, selectionEnd.Y));
        var max = new Vector2(
            MathF.Max(selectionStart.X, selectionEnd.X),
            MathF.Max(selectionStart.Y, selectionEnd.Y));
        return candidates
            .Where(item =>
                item.ScreenPosition.X >= min.X
                && item.ScreenPosition.Y >= min.Y
                && item.ScreenPosition.X <= max.X
                && item.ScreenPosition.Y <= max.Y)
            .Select(item => item.Candidate)
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.CandidateId))
            .GroupBy(candidate => candidate.CandidateId, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
    }

    private static void DrawCandidateSelectionRect(ImDrawListPtr drawList, Vector2 start, Vector2 end)
    {
        var min = new Vector2(MathF.Min(start.X, end.X), MathF.Min(start.Y, end.Y));
        var max = new Vector2(MathF.Max(start.X, end.X), MathF.Max(start.Y, end.Y));
        drawList.AddRectFilled(min, max, CandidateSelectionFillColor);
        drawList.AddRect(min, max, CandidateSelectionEdgeColor, 0f, ImDrawFlags.None, 1.5f);
    }

    private static VisibleCandidateCollection CollectVisibleCandidates(
        IEnumerable<ApproachCandidate> source,
        int sourceCount,
        Point3 playerPoint,
        float drawDistance,
        int candidateLimit)
    {
        var candidates = new List<VisibleCandidate>(Math.Min(sourceCount, candidateLimit));
        var clippedCount = 0;
        foreach (var candidate in source)
        {
            var distance = candidate.Position.HorizontalDistanceTo(playerPoint);
            if (distance > drawDistance)
                continue;

            clippedCount++;
            candidates.Add(new VisibleCandidate(candidate, distance));
        }

        candidates.Sort(static (left, right) =>
        {
            var distanceCompare = left.Distance.CompareTo(right.Distance);
            return distanceCompare != 0
                ? distanceCompare
                : string.Compare(left.Candidate.CandidateId, right.Candidate.CandidateId, StringComparison.Ordinal);
        });
        if (candidates.Count > candidateLimit)
            candidates.RemoveRange(candidateLimit, candidates.Count - candidateLimit);

        return new VisibleCandidateCollection(candidates, clippedCount);
    }

    private static string BuildCandidateLabel(
        ApproachCandidate candidate,
        float distanceToPlayer,
        IReadOnlyList<CandidateRecordOwner> recordedOwners,
        IReadOnlyList<CandidateRecordOwner> riskOwners,
        IReadOnlyList<CandidateRecordOwner> disabledOwners)
    {
        var status = "未记录";
        if (disabledOwners.Count > 0)
            status = $"屏蔽:{FormatRecordedOwners(disabledOwners)}";
        else if (recordedOwners.Count > 1)
            status = $"风险已记录:{FormatRecordedOwners(recordedOwners)}";
        else if (riskOwners.Count > 0)
            status = $"风险:{FormatRecordedOwners(riskOwners)}";
        else if (recordedOwners.Count > 0)
            status = $"已记录:{FormatRecordedOwners(recordedOwners)}";
        if (disabledOwners.Count > 0 && recordedOwners.Count > 0)
            status += $" 已记录:{FormatRecordedOwners(recordedOwners)}";
        if (recordedOwners.Count > 0 && riskOwners.Count > 0)
            status += $" 风险:{FormatRecordedOwners(riskOwners)}";
        var reachability = candidate.Reachability switch
        {
            CandidateReachability.Flyable => "可飞",
            CandidateReachability.WalkReachable => "可走",
            _ => "未知",
        };
        var path = candidate.PathLengthMeters is { } pathLength ? $" path={pathLength:F1}m" : string.Empty;
        return $"候选 {status}/{reachability} p={distanceToPlayer:F1}m{path} b={ShortBlockId(candidate.BlockId)}";
    }

    private static bool HasRecordedOwner(
        ApproachCandidate candidate,
        uint territoryId,
        IReadOnlyDictionary<string, List<CandidateRecordOwner>> recordedOwnersByKey)
    {
        return HasOwner(candidate, territoryId, recordedOwnersByKey, includeBlock: false);
    }

    private static bool HasRiskOwner(
        ApproachCandidate candidate,
        uint territoryId,
        IReadOnlyDictionary<string, List<CandidateRecordOwner>> riskOwnersByKey)
    {
        return HasOwner(candidate, territoryId, riskOwnersByKey, includeBlock: true);
    }

    private static bool HasVisibleRiskOwner(
        ApproachCandidate candidate,
        uint territoryId,
        IReadOnlyDictionary<string, List<CandidateRecordOwner>> recordedOwnersByKey,
        IReadOnlyDictionary<string, List<CandidateRecordOwner>> riskOwnersByKey)
    {
        return HasRiskOwner(candidate, territoryId, riskOwnersByKey)
            || HasMultipleRecordedOwners(candidate, territoryId, recordedOwnersByKey);
    }

    private static bool HasDisabledOwner(
        ApproachCandidate candidate,
        uint territoryId,
        IReadOnlyDictionary<string, List<CandidateRecordOwner>> disabledOwnersByKey)
    {
        return HasOwner(candidate, territoryId, disabledOwnersByKey, includeBlock: false);
    }

    private static bool HasOwner(
        ApproachCandidate candidate,
        uint territoryId,
        IReadOnlyDictionary<string, List<CandidateRecordOwner>> ownersByKey,
        bool includeBlock)
    {
        if (ownersByKey.Count == 0)
            return false;

        return HasOwnerKey(ownersByKey, candidate.CandidateId)
            || (includeBlock && HasOwnerKey(ownersByKey, candidate.BlockId))
            || HasOwnerKey(ownersByKey, GetTerritoryCandidateFingerprint(candidate, territoryId));
    }

    private static bool HasOwnerKey(
        IReadOnlyDictionary<string, List<CandidateRecordOwner>> ownersByKey,
        string key)
    {
        return !string.IsNullOrWhiteSpace(key) && ownersByKey.ContainsKey(key);
    }

    private static bool HasMultipleRecordedOwners(
        ApproachCandidate candidate,
        uint territoryId,
        IReadOnlyDictionary<string, List<CandidateRecordOwner>> recordedOwnersByKey)
    {
        if (recordedOwnersByKey.Count == 0)
            return false;

        uint firstOwnerId = 0;
        var hasOwner = false;
        return HasDifferentOwner(recordedOwnersByKey, candidate.CandidateId, ref firstOwnerId, ref hasOwner)
            || HasDifferentOwner(
                recordedOwnersByKey,
                GetTerritoryCandidateFingerprint(candidate, territoryId),
                ref firstOwnerId,
                ref hasOwner);
    }

    private static bool HasDifferentOwner(
        IReadOnlyDictionary<string, List<CandidateRecordOwner>> ownersByKey,
        string key,
        ref uint firstOwnerId,
        ref bool hasOwner)
    {
        if (string.IsNullOrWhiteSpace(key) || !ownersByKey.TryGetValue(key, out var owners))
            return false;

        foreach (var owner in owners)
        {
            if (!hasOwner)
            {
                firstOwnerId = owner.FishingSpotId;
                hasOwner = true;
                continue;
            }

            if (owner.FishingSpotId != firstOwnerId)
                return true;
        }

        return false;
    }

    private static IReadOnlyList<CandidateRecordOwner> GetRecordedOwners(
        ApproachCandidate candidate,
        uint territoryId,
        IReadOnlyDictionary<string, List<CandidateRecordOwner>> recordedOwnersByKey)
    {
        if (recordedOwnersByKey.Count == 0)
            return [];

        var owners = new Dictionary<uint, CandidateRecordOwner>();
        AddRecordedOwners(owners, recordedOwnersByKey, candidate.CandidateId);
        AddRecordedOwners(owners, recordedOwnersByKey, GetTerritoryCandidateFingerprint(candidate, territoryId));
        return owners.Values
            .OrderBy(owner => owner.FishingSpotId)
            .ToList();
    }

    private static IReadOnlyList<CandidateRecordOwner> GetRiskOwners(
        ApproachCandidate candidate,
        uint territoryId,
        IReadOnlyDictionary<string, List<CandidateRecordOwner>> riskOwnersByKey)
    {
        if (riskOwnersByKey.Count == 0)
            return [];

        var owners = new Dictionary<uint, CandidateRecordOwner>();
        AddRecordedOwners(owners, riskOwnersByKey, candidate.CandidateId);
        AddRecordedOwners(owners, riskOwnersByKey, candidate.BlockId);
        AddRecordedOwners(owners, riskOwnersByKey, GetTerritoryCandidateFingerprint(candidate, territoryId));
        return owners.Values
            .OrderBy(owner => owner.FishingSpotId)
            .ToList();
    }

    private static IReadOnlyList<CandidateRecordOwner> GetDisabledOwners(
        ApproachCandidate candidate,
        uint territoryId,
        IReadOnlyDictionary<string, List<CandidateRecordOwner>> disabledOwnersByKey)
    {
        if (disabledOwnersByKey.Count == 0)
            return [];

        var owners = new Dictionary<uint, CandidateRecordOwner>();
        AddRecordedOwners(owners, disabledOwnersByKey, candidate.CandidateId);
        AddRecordedOwners(owners, disabledOwnersByKey, candidate.BlockId);
        AddRecordedOwners(owners, disabledOwnersByKey, GetTerritoryCandidateFingerprint(candidate, territoryId));
        return owners.Values
            .OrderBy(owner => owner.FishingSpotId)
            .ToList();
    }

    private static int CountConfirmedPoints(TerritoryMaintenanceDocument? maintenance)
    {
        return maintenance?.Spots.Sum(spot =>
            spot.ApproachPoints.Count(point => point.Status == ApproachPointStatus.Confirmed)) ?? 0;
    }

    private static int CountDisabledPoints(TerritoryMaintenanceDocument? maintenance)
    {
        return maintenance?.Spots.Sum(spot =>
            spot.ApproachPoints.Count(IsEffectiveDisabledApproachPoint)) ?? 0;
    }

    private static int CountMixedRiskCandidates(TerritoryMaintenanceDocument? maintenance)
    {
        if (maintenance is null)
            return 0;

        return maintenance.Spots
            .SelectMany(spot => spot.MixedRiskBlocks)
            .Sum(record =>
            {
                var candidateCount = record.CandidateIds
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.Ordinal)
                    .Count();
                return candidateCount > 0 || string.IsNullOrWhiteSpace(record.BlockId) ? candidateCount : 1;
            });
    }

    private static int CountRecordedRiskKeys(IReadOnlyDictionary<string, List<CandidateRecordOwner>> recordedOwnersByKey)
    {
        return recordedOwnersByKey.Values.Count(owners =>
            owners
                .Select(owner => owner.FishingSpotId)
                .Distinct()
                .Skip(1)
                .Any());
    }

    private static Dictionary<string, List<CandidateRecordOwner>> BuildRecordedCandidateOwnerIndex(
        TerritoryMaintenanceDocument? maintenance)
    {
        var ownersByKey = new Dictionary<string, List<CandidateRecordOwner>>(StringComparer.Ordinal);
        if (maintenance is null)
            return ownersByKey;

        foreach (var spot in maintenance.Spots)
        {
            var owner = new CandidateRecordOwner(spot.FishingSpotId, spot.Name);
            foreach (var point in spot.ApproachPoints.Where(IsTrustedConfirmedApproachPoint))
            {
                AddRecordedOwner(ownersByKey, point.SourceCandidateId, owner);
                AddRecordedOwner(ownersByKey, point.SourceCandidateFingerprint, owner);
                if (maintenance.TerritoryId != 0)
                {
                    AddRecordedOwner(
                        ownersByKey,
                        SpotFingerprint.CreateTerritoryCandidateFingerprint(
                            maintenance.TerritoryId,
                            point.Position,
                            point.Rotation),
                        owner);
                }
            }
        }

        return ownersByKey;
    }

    private static Dictionary<string, List<CandidateRecordOwner>> BuildMixedRiskCandidateOwnerIndex(
        TerritoryMaintenanceDocument? maintenance)
    {
        var ownersByKey = new Dictionary<string, List<CandidateRecordOwner>>(StringComparer.Ordinal);
        if (maintenance is null)
            return ownersByKey;

        foreach (var spot in maintenance.Spots)
        {
            var owner = new CandidateRecordOwner(spot.FishingSpotId, spot.Name);
            foreach (var record in spot.MixedRiskBlocks)
            {
                AddRecordedOwner(ownersByKey, record.BlockId, owner);
                foreach (var candidateId in record.CandidateIds.Where(id => !string.IsNullOrWhiteSpace(id)))
                    AddRecordedOwner(ownersByKey, candidateId, owner);
            }
        }

        return ownersByKey;
    }

    private static Dictionary<string, List<CandidateRecordOwner>> BuildDisabledCandidateOwnerIndex(
        TerritoryMaintenanceDocument? maintenance)
    {
        var ownersByKey = new Dictionary<string, List<CandidateRecordOwner>>(StringComparer.Ordinal);
        if (maintenance is null)
            return ownersByKey;

        foreach (var spot in maintenance.Spots)
        {
            var owner = new CandidateRecordOwner(spot.FishingSpotId, spot.Name);
            foreach (var point in spot.ApproachPoints.Where(IsEffectiveDisabledApproachPoint))
            {
                AddRecordedOwner(ownersByKey, point.SourceCandidateId, owner);
                AddRecordedOwner(ownersByKey, point.SourceCandidateFingerprint, owner);
                if (maintenance.TerritoryId != 0)
                {
                    AddRecordedOwner(
                        ownersByKey,
                        SpotFingerprint.CreateTerritoryCandidateFingerprint(
                            maintenance.TerritoryId,
                            point.Position,
                            point.Rotation),
                        owner);
                }
            }
        }

        return ownersByKey;
    }

    private static void AddRecordedOwners(
        Dictionary<uint, CandidateRecordOwner> owners,
        IReadOnlyDictionary<string, List<CandidateRecordOwner>> ownersByKey,
        string key)
    {
        if (string.IsNullOrWhiteSpace(key) || !ownersByKey.TryGetValue(key, out var matchedOwners))
            return;

        foreach (var owner in matchedOwners)
            owners.TryAdd(owner.FishingSpotId, owner);
    }

    private static void AddRecordedOwner(
        Dictionary<string, List<CandidateRecordOwner>> ownersByKey,
        string key,
        CandidateRecordOwner owner)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        if (!ownersByKey.TryGetValue(key, out var owners))
        {
            owners = [];
            ownersByKey.Add(key, owners);
        }

        if (!owners.Any(existing => existing.FishingSpotId == owner.FishingSpotId))
            owners.Add(owner);
    }

    private static bool IsEffectiveDisabledApproachPoint(ApproachPoint point)
    {
        return point.Status == ApproachPointStatus.Disabled
            && point.SourceKind != ApproachPointSourceKind.AutoCastFill;
    }

    private static bool IsTrustedConfirmedApproachPoint(ApproachPoint point)
    {
        return point.Status == ApproachPointStatus.Confirmed
            && (point.SourceKind == ApproachPointSourceKind.Candidate
                || point.SourceKind == ApproachPointSourceKind.AutoCastFill);
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    private static bool IsOverlaySelectionMouseDown()
    {
        try
        {
            return InputManager.IsLeftMouseDown()
                || (GetAsyncKeyState(VirtualKeyXButton1) & KeyDownMask) != 0;
        }
        catch
        {
            return false;
        }
    }

    private static string GetTerritoryCandidateFingerprint(ApproachCandidate candidate, uint territoryId)
    {
        var effectiveTerritoryId = candidate.TerritoryId != 0 ? candidate.TerritoryId : territoryId;
        return effectiveTerritoryId == 0
            ? string.Empty
            : SpotFingerprint.CreateTerritoryCandidateFingerprint(
                effectiveTerritoryId,
                candidate.Position,
                candidate.Rotation);
    }

    private static string FormatRecordedOwners(IReadOnlyList<CandidateRecordOwner> owners)
    {
        return string.Join(
            ",",
            owners.Take(4).Select(owner => string.IsNullOrWhiteSpace(owner.Name)
                ? owner.FishingSpotId.ToString()
                : $"{owner.Name}#{owner.FishingSpotId}"));
    }

    private OverlaySurfaceDrawStats DrawSurfaceDebug(
        ImDrawListPtr drawList,
        SpotWorkflowSession session,
        Vector3 playerPosition,
        float drawDistance,
        int triangleLimit,
        bool collectPerfStats)
    {
        var debug = session.NearbyDebugOverlay;
        if (debug is null)
            return OverlaySurfaceDrawStats.Empty;

        if (debug.TerritoryId != 0 && debug.TerritoryId != session.CurrentTerritoryId)
            return OverlaySurfaceDrawStats.Empty;

        var limit = Math.Clamp(triangleLimit, 1, 512);
        var fishableStats = OverlaySurfaceSetDrawStats.Empty;
        var walkableStats = OverlaySurfaceSetDrawStats.Empty;
        if (session.OverlayShowFishableDebug)
            fishableStats = DrawSurfaceDebugSet(
                drawList,
                debug.FishableTriangles,
                playerPosition,
                drawDistance,
                limit,
                "water",
                FishableDebugEdgeColor,
                FishableDebugFillColor,
                FishableDebugTextColor,
                collectPerfStats);

        if (session.OverlayShowWalkableDebug)
            walkableStats = DrawSurfaceDebugSet(
                drawList,
                debug.WalkableTriangles,
                playerPosition,
                drawDistance,
                limit,
                "walk",
                WalkableDebugEdgeColor,
                WalkableDebugHatchColor,
                WalkableDebugTextColor,
                collectPerfStats);

        var nearbyCandidateStats = DrawNearbyDebugCandidates(
            drawList,
            debug.Candidates,
            playerPosition,
            drawDistance,
            limit,
            collectPerfStats);
        var hudTiming = StartTiming(collectPerfStats);
        var hudTextCount = 0;
        if (TryWorldToScreen(debug.PlayerPosition + new Vector3(0f, 3f, 0f), out var screen))
        {
            drawList.AddText(
                screen,
                FishableDebugTextColor,
                $"FPG surfaces water {fishableStats.DrawnCount}/{debug.FishableTriangles.Count} walk {walkableStats.DrawnCount}/{debug.WalkableTriangles.Count} cand {nearbyCandidateStats.DrawnCount}/{debug.Candidates.Count} r={debug.RadiusMeters:F0}m");
            hudTextCount = 1;
        }

        var hudMs = StopTiming(hudTiming);
        return new OverlaySurfaceDrawStats(
            fishableStats,
            walkableStats,
            nearbyCandidateStats,
            hudTextCount,
            hudMs);
    }

    private OverlaySurfaceSetDrawStats DrawSurfaceDebugSet(
        ImDrawListPtr drawList,
        IReadOnlyList<DebugOverlayTriangle> source,
        Vector3 playerPosition,
        float drawDistance,
        int triangleLimit,
        string labelPrefix,
        uint edgeColor,
        uint surfaceColor,
        uint textColor,
        bool collectPerfStats)
    {
        if (source.Count == 0)
            return OverlaySurfaceSetDrawStats.Empty;

        var collectTiming = StartTiming(collectPerfStats);
        var triangles = source
            .Select(triangle => new
            {
                Triangle = triangle,
                Distance = HorizontalDistance(triangle.Centroid, playerPosition),
            })
            .Where(item => item.Distance <= drawDistance)
            .OrderBy(item => item.Distance)
            .ToList();
        var visibleCount = triangles.Count;
        if (triangles.Count > triangleLimit)
            triangles.RemoveRange(triangleLimit, triangles.Count - triangleLimit);
        var collectMs = StopTiming(collectTiming);

        var drawTiming = StartTiming(collectPerfStats);
        var lineCount = 0;
        var pointCount = 0;
        var filledTriangleCount = 0;
        foreach (var item in triangles)
        {
            var triangle = item.Triangle;
            if (labelPrefix == "water")
            {
                DrawWorldTriangleFilled(drawList, triangle.A, triangle.B, triangle.C, surfaceColor);
                filledTriangleCount++;
            }
            else
            {
                DrawWorldTriangleHatch(drawList, triangle, surfaceColor);
                lineCount += CountTriangleHatchLines(triangle);
            }

            DrawWorldLine(drawList, triangle.A, triangle.B, edgeColor);
            DrawWorldLine(drawList, triangle.B, triangle.C, edgeColor);
            DrawWorldLine(drawList, triangle.C, triangle.A, edgeColor);
            lineCount += 3;
            DrawWorldPoint(drawList, triangle.Centroid, 2f, edgeColor, false);
            pointCount++;
        }
        var drawMs = StopTiming(drawTiming);

        var labelTiming = StartTiming(collectPerfStats);
        var labelIndex = 0;
        foreach (var item in triangles.Take(MaxSurfaceDebugLabels))
        {
            labelIndex++;
            DrawWorldText(
                drawList,
                item.Triangle.Centroid + new Vector3(0f, 0.5f, 0f),
                $"{labelPrefix}{labelIndex} {FormatMaterial(item.Triangle.Material)} {item.Distance:F1}m",
                textColor);
        }
        var labelMs = StopTiming(labelTiming);

        return new OverlaySurfaceSetDrawStats(
            source.Count,
            visibleCount,
            triangles.Count,
            lineCount,
            pointCount,
            filledTriangleCount,
            labelIndex,
            collectMs,
            drawMs,
            labelMs);
    }

    private OverlayNearbyDebugCandidateStats DrawNearbyDebugCandidates(
        ImDrawListPtr drawList,
        IReadOnlyList<ApproachCandidate> source,
        Vector3 playerPosition,
        float drawDistance,
        int candidateLimit,
        bool collectPerfStats)
    {
        if (source.Count == 0)
            return OverlayNearbyDebugCandidateStats.Empty;

        var playerPoint = Point3.From(playerPosition);
        var collectTiming = StartTiming(collectPerfStats);
        var candidates = source
            .Select(candidate => new
            {
                Candidate = candidate,
                Distance = candidate.Position.HorizontalDistanceTo(playerPoint),
            })
            .Where(item => item.Distance <= drawDistance)
            .OrderBy(item => item.Distance)
            .ThenBy(item => item.Candidate.CandidateId, StringComparer.Ordinal)
            .ToList();
        var visibleCount = candidates.Count;
        if (candidates.Count > candidateLimit)
            candidates.RemoveRange(candidateLimit, candidates.Count - candidateLimit);
        var collectMs = StopTiming(collectTiming);

        var labelCount = 0;
        var lineCount = 0;
        var pointCount = 0;
        var drawTiming = StartTiming(collectPerfStats);
        foreach (var item in candidates)
        {
            var candidate = item.Candidate;
            var standing = ToVector3(candidate.Position);
            DrawFacingGuide(drawList, standing, candidate.Rotation, WarningColor);
            lineCount++;
            DrawWorldPoint(drawList, standing, 3f, WarningColor, true);
            pointCount++;

            if (labelCount < MaxCandidateLabels)
            {
                DrawWorldText(
                    drawList,
                    standing + new Vector3(0f, 1.6f, 0f),
                    $"near {ShortId(candidate.CandidateId)} {ShortBlockId(candidate.BlockId)} {candidate.Status}",
                    WarningColor);
                labelCount++;
            }
        }
        var drawMs = StopTiming(drawTiming);

        return new OverlayNearbyDebugCandidateStats(
            source.Count,
            visibleCount,
            candidates.Count,
            lineCount,
            pointCount,
            labelCount,
            collectMs,
            drawMs);
    }

    private OverlayCandidateDrawStats DrawTerritoryCache(
        ImDrawListPtr drawList,
        SpotWorkflowSession session,
        Vector3 playerPosition,
        float drawDistance,
        int candidateLimit,
        bool collectPerfStats)
    {
        var blocks = session.CurrentTerritoryBlocks;
        if (blocks.Count == 0)
            return OverlayCandidateDrawStats.Empty;

        var territoryId = session.CurrentTerritorySurvey?.TerritoryId ?? session.SelectedTerritoryId;
        var ownerIndexCacheHit = ReferenceEquals(cachedMaintenance, session.CurrentTerritoryMaintenance);
        var ownerIndexTiming = StartTiming(collectPerfStats);
        var ownerIndex = GetOwnerIndex(session.CurrentTerritoryMaintenance);
        var ownerIndexMs = StopTiming(ownerIndexTiming);
        var statusIndexCacheHit = session.CurrentTerritorySurvey is { } survey
            && IsCandidateStatusIndexCached(survey, territoryId, ownerIndex);
        var statusIndexTiming = StartTiming(collectPerfStats);
        var statusIndex = session.CurrentTerritorySurvey is { } currentSurvey
            ? GetCandidateStatusIndex(currentSurvey, territoryId, ownerIndex)
            : OverlayCandidateStatusIndex.Empty;
        var statusIndexMs = StopTiming(statusIndexTiming);
        var playerPoint = Point3.From(playerPosition);
        var totalCandidateCount = blocks.Sum(block => block.Candidates.Count);
        var collectTiming = StartTiming(collectPerfStats);
        var visibleCandidates = CollectVisibleCandidates(
                blocks.SelectMany(block => block.Candidates),
                totalCandidateCount,
                playerPoint,
                drawDistance,
                candidateLimit);
        var collectMs = StopTiming(collectTiming);
        var candidates = visibleCandidates.Candidates;

        var lineCount = 0;
        var pointCount = 0;
        var drawLoopTiming = StartTiming(collectPerfStats);
        foreach (var item in candidates)
        {
            var candidate = item.Candidate;
            var standing = ToVector3(candidate.Position);
            var status = GetCandidateStatus(candidate, territoryId, ownerIndex, statusIndex);
            var isConfirmed = status.IsConfirmed;
            var isRisk = status.IsRisk;
            var isDisabled = status.IsDisabled;
            var color = isDisabled ? DisabledColor : isRisk ? RiskColor : isConfirmed ? ConfirmedColor : TerritoryCandidateColor;
            DrawFacingGuide(drawList, standing, candidate.Rotation, color);
            lineCount++;
            DrawWorldPoint(drawList, standing, isDisabled ? 3f : isRisk ? 2.5f : isConfirmed ? 3f : 2f, color, true);
            pointCount++;
        }
        var drawLoopMs = StopTiming(drawLoopTiming);

        return new OverlayCandidateDrawStats(
            "territoryCache",
            totalCandidateCount,
            visibleCandidates.ClippedCount,
            candidates.Count,
            lineCount,
            pointCount,
            0,
            0,
            0,
            blocks.Count,
            0,
            0,
            0,
            statusIndex.RecordedTotal,
            statusIndex.RiskTotal,
            statusIndex.DisabledTotal,
            ownerIndexCacheHit,
            statusIndexCacheHit,
            ownerIndexMs,
            statusIndexMs,
            collectMs,
            drawLoopMs,
            0d,
            0d,
            0d);
    }

    private OverlayOwnerIndex GetOwnerIndex(TerritoryMaintenanceDocument? maintenance)
    {
        if (ReferenceEquals(cachedMaintenance, maintenance))
            return cachedOwnerIndex;

        cachedMaintenance = maintenance;
        var recordedOwnersByKey = BuildRecordedCandidateOwnerIndex(maintenance);
        var riskOwnersByKey = BuildMixedRiskCandidateOwnerIndex(maintenance);
        cachedOwnerIndex = new OverlayOwnerIndex(
            recordedOwnersByKey,
            riskOwnersByKey,
            BuildDisabledCandidateOwnerIndex(maintenance),
            CountConfirmedPoints(maintenance),
            CountMixedRiskCandidates(maintenance),
            CountDisabledPoints(maintenance),
            CountRecordedRiskKeys(recordedOwnersByKey));
        return cachedOwnerIndex;
    }

    private OverlayCandidateStatusIndex GetCandidateStatusIndex(
        TerritorySurveyDocument survey,
        uint territoryId,
        OverlayOwnerIndex ownerIndex)
    {
        if (IsCandidateStatusIndexCached(survey, territoryId, ownerIndex))
            return cachedStatusIndex;

        cachedStatusSurvey = survey;
        cachedStatusOwnerIndex = ownerIndex;
        cachedStatusTerritoryId = territoryId;
        cachedStatusIndex = new OverlayCandidateStatusIndex(
            new Dictionary<string, OverlayCandidateStatus>(
                Math.Min(Math.Max(survey.Candidates.Count / 8, 256), 4096),
                StringComparer.Ordinal),
            ownerIndex.RecordedPointCount,
            ownerIndex.RiskCandidateCount + ownerIndex.RecordedRiskKeyCount,
            ownerIndex.DisabledPointCount);
        return cachedStatusIndex;
    }

    private bool IsCandidateStatusIndexCached(
        TerritorySurveyDocument survey,
        uint territoryId,
        OverlayOwnerIndex ownerIndex)
    {
        return ReferenceEquals(cachedStatusSurvey, survey)
            && ReferenceEquals(cachedStatusOwnerIndex, ownerIndex)
            && cachedStatusTerritoryId == territoryId;
    }

    private static OverlayCandidateStatus GetCandidateStatus(
        ApproachCandidate candidate,
        uint territoryId,
        OverlayOwnerIndex ownerIndex,
        OverlayCandidateStatusIndex statusIndex)
    {
        var key = GetCandidateStatusKey(candidate, territoryId);
        if (!string.IsNullOrWhiteSpace(key)
            && statusIndex.StatusByCandidateKey.TryGetValue(key, out var cachedStatus))
            return cachedStatus;

        var status = CreateCandidateStatus(candidate, territoryId, ownerIndex);
        if (!string.IsNullOrWhiteSpace(key))
            statusIndex.StatusByCandidateKey[key] = status;
        return status;
    }

    private static OverlayCandidateStatus CreateCandidateStatus(
        ApproachCandidate candidate,
        uint territoryId,
        OverlayOwnerIndex ownerIndex)
    {
        var isConfirmed = HasRecordedOwner(candidate, territoryId, ownerIndex.RecordedOwnersByKey);
        var isRisk = HasVisibleRiskOwner(
            candidate,
            territoryId,
            ownerIndex.RecordedOwnersByKey,
            ownerIndex.RiskOwnersByKey);
        var isDisabled = HasDisabledOwner(candidate, territoryId, ownerIndex.DisabledOwnersByKey);
        return new OverlayCandidateStatus(isConfirmed, isRisk, isDisabled);
    }

    private static string GetCandidateStatusKey(ApproachCandidate candidate, uint territoryId)
    {
        return !string.IsNullOrWhiteSpace(candidate.CandidateId)
            ? candidate.CandidateId
            : GetTerritoryCandidateFingerprint(candidate, territoryId);
    }

    private OverlayBlockLabelDrawStats DrawTerritoryBlockLabels(
        ImDrawListPtr drawList,
        SpotWorkflowSession session,
        Vector3 playerPosition,
        float drawDistance,
        uint territoryId,
        OverlayOwnerIndex ownerIndex,
        OverlayCandidateStatusIndex statusIndex,
        bool collectPerfStats)
    {
        if (session.CurrentTerritoryBlocks.Count == 0)
            return OverlayBlockLabelDrawStats.Empty;

        var playerPoint = Point3.From(playerPosition);
        var collectTiming = StartTiming(collectPerfStats);
        var visibleBlocks = new List<OverlayBlockDistance>(Math.Min(session.CurrentTerritoryBlocks.Count, 32));
        var visibleBlockCount = 0;
        foreach (var block in session.CurrentTerritoryBlocks)
        {
            var distance = block.Center.HorizontalDistanceTo(playerPoint);
            if (distance > drawDistance)
                continue;

            visibleBlockCount++;
            visibleBlocks.Add(new OverlayBlockDistance(block, distance));
        }

        visibleBlocks.Sort(static (left, right) =>
        {
            var distanceCompare = left.Distance.CompareTo(right.Distance);
            return distanceCompare != 0
                ? distanceCompare
                : string.Compare(left.Block.BlockId, right.Block.BlockId, StringComparison.Ordinal);
        });
        if (visibleBlocks.Count > 32)
            visibleBlocks.RemoveRange(32, visibleBlocks.Count - 32);
        var collectMs = StopTiming(collectTiming);

        var candidateCheckCount = 0;
        var drawTiming = StartTiming(collectPerfStats);
        foreach (var item in visibleBlocks)
        {
            var confirmedCount = 0;
            var riskCount = 0;
            var disabledCount = 0;
            foreach (var candidate in item.Block.Candidates)
            {
                candidateCheckCount++;
                var status = GetCandidateStatus(candidate, territoryId, ownerIndex, statusIndex);
                if (status.IsConfirmed)
                    confirmedCount++;
                if (status.IsRisk)
                    riskCount++;
                if (status.IsDisabled)
                    disabledCount++;
            }

            var label = $"{ShortBlockId(item.Block.BlockId)} {confirmedCount}/{item.Block.Candidates.Count}";
            if (riskCount > 0)
                label += $" r{riskCount}";
            if (disabledCount > 0)
                label += $" x{disabledCount}";
            DrawWorldText(drawList, ToVector3(item.Block.Center) + new Vector3(0f, 2f, 0f), label, BlockLabelColor);
        }
        var drawMs = StopTiming(drawTiming);

        return new OverlayBlockLabelDrawStats(
            session.CurrentTerritoryBlocks.Count,
            visibleBlockCount,
            visibleBlocks.Count,
            candidateCheckCount,
            collectMs,
            drawMs);
    }

    private void DrawWorldCircle(ImDrawListPtr drawList, Vector3 center, float radius, uint color)
    {
        var previous = center + new Vector3(radius, 0f, 0f);
        for (var index = 1; index <= CircleSegments; index++)
        {
            var angle = MathF.Tau * index / CircleSegments;
            var current = center + new Vector3(MathF.Cos(angle) * radius, 0f, MathF.Sin(angle) * radius);
            DrawWorldLine(drawList, previous, current, color);
            previous = current;
        }
    }

    private void DrawWorldLine(ImDrawListPtr drawList, Vector3 start, Vector3 end, uint color, int thickness = 1)
    {
        if (!ClipLineToNearPlane(ref start, ref end))
            return;

        if (TryWorldToScreen(start, out var screenStart) && TryWorldToScreen(end, out var screenEnd))
            drawList.AddLine(screenStart, screenEnd, color, thickness);
    }

    private void DrawWorldTriangleHatch(ImDrawListPtr drawList, DebugOverlayTriangle triangle, uint color)
    {
        var stripeCount = CountTriangleHatchLines(triangle);

        for (var index = 1; index <= stripeCount; index++)
        {
            var t = index / (stripeCount + 1f);
            var start = Vector3.Lerp(triangle.A, triangle.B, t);
            var end = Vector3.Lerp(triangle.A, triangle.C, t);
            DrawWorldLine(drawList, start, end, color);
        }
    }

    private static int CountTriangleHatchLines(DebugOverlayTriangle triangle)
    {
        var maxLength = MathF.Max(
            HorizontalDistance(triangle.A, triangle.B),
            MathF.Max(
                HorizontalDistance(triangle.B, triangle.C),
                HorizontalDistance(triangle.C, triangle.A)));
        return Math.Clamp(
            (int)MathF.Ceiling(maxLength / DebugSurfaceHatchSpacingMeters),
            2,
            24);
    }

    private void DrawWorldTriangleFilled(ImDrawListPtr drawList, Vector3 a, Vector3 b, Vector3 c, uint color)
    {
        if (TryWorldToScreen(a, out var screenA)
            && TryWorldToScreen(b, out var screenB)
            && TryWorldToScreen(c, out var screenC))
            drawList.AddTriangleFilled(screenA, screenB, screenC, color);
    }

    private void DrawFacingGuide(ImDrawListPtr drawList, Vector3 standing, float rotation, uint color, int thickness = 1)
    {
        var direction = new Vector3(MathF.Sin(rotation), 0f, MathF.Cos(rotation));
        DrawWorldLine(drawList, standing, standing + (direction * FacingGuideLengthMeters), color, thickness);
    }

    private void DrawWorldPoint(ImDrawListPtr drawList, Vector3 point, float radius, uint color, bool filled)
    {
        if (!TryWorldToScreen(point, out var screen))
            return;

        if (filled)
            drawList.AddCircleFilled(screen, radius, color);
        else
            drawList.AddCircle(screen, radius, color);
    }

    private void DrawWorldText(ImDrawListPtr drawList, Vector3 point, string text, uint color)
    {
        if (string.IsNullOrWhiteSpace(text) || !TryWorldToScreen(point, out var screen))
            return;

        drawList.AddText(screen, color, text);
    }

    private bool TryWorldToScreen(Vector3 world, out Vector2 screen)
    {
        screen = default;
        if (Vector4.Dot(new Vector4(world, 1f), nearPlane) >= 0f)
            return false;

        var projected = Vector4.Transform(world, viewProj);
        if (MathF.Abs(projected.W) <= 0.0001f)
            return false;

        var inverseW = 1f / projected.W;
        screen = new Vector2(
            0.5f * viewportSize.X * (1f + projected.X * inverseW),
            0.5f * viewportSize.Y * (1f - projected.Y * inverseW))
            + ImGuiHelpers.MainViewport.Pos;
        return true;
    }

    private bool ClipLineToNearPlane(ref Vector3 start, ref Vector3 end)
    {
        var startDistance = Vector4.Dot(new Vector4(start, 1f), nearPlane);
        var endDistance = Vector4.Dot(new Vector4(end, 1f), nearPlane);
        if (startDistance >= 0f && endDistance >= 0f)
            return false;

        if (startDistance > 0f || endDistance > 0f)
        {
            var delta = end - start;
            var normal = new Vector3(nearPlane.X, nearPlane.Y, nearPlane.Z);
            var denominator = Vector3.Dot(delta, normal);
            if (MathF.Abs(denominator) <= 0.0001f)
                return false;

            var ratio = -startDistance / denominator;
            var clipped = start + (ratio * delta);
            if (startDistance > 0f)
                start = clipped;
            else
                end = clipped;
        }

        return true;
    }

    private static Vector3 ToVector3(Point3 point) => new(point.X, point.Y, point.Z);

    private static float HorizontalDistance(Vector3 left, Vector3 right)
    {
        var dx = left.X - right.X;
        var dz = left.Z - right.Z;
        return MathF.Sqrt((dx * dx) + (dz * dz));
    }

    private static string FormatMaterial(ulong material)
    {
        return "0x" + material.ToString("X");
    }

    private static string ShortBlockId(string blockId)
    {
        if (string.IsNullOrWhiteSpace(blockId))
            return "block";

        var index = blockId.LastIndexOf("_block_", StringComparison.Ordinal);
        return index >= 0 ? "b" + blockId[(index + "_block_".Length)..] : blockId;
    }

    private static string ShortId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "-";

        return value.Length <= 10 ? value : value[..10];
    }

    private readonly record struct OverlayPerfFrameSample(
        double TotalMs,
        double TargetMs,
        double SurfaceDebugMs,
        OverlaySurfaceDrawStats SurfaceDebugStats,
        OverlayCandidateDrawStats TerritoryCacheStats,
        OverlayCandidateDrawStats CandidateStats,
        float DrawDistance,
        int CandidateLimit,
        bool CanDrawCurrentTerritory,
        bool ShowCandidates)
    {
        public static OverlayPerfFrameSample Empty { get; } = new(
            0d,
            0d,
            0d,
            OverlaySurfaceDrawStats.Empty,
            OverlayCandidateDrawStats.Empty,
            OverlayCandidateDrawStats.Empty,
            0f,
            0,
            false,
            false);

        public bool HasData => TotalMs > 0d;
    }

    private readonly record struct OverlayCandidateDrawStats(
        string Source,
        int SourceCount,
        int VisibleCount,
        int DrawnCount,
        int LineCount,
        int PointCount,
        int LabelCount,
        int StatusTextCount,
        int SelectableCount,
        int BlockLabelsSource,
        int BlockLabelsVisible,
        int BlockLabelsDrawn,
        int BlockLabelCandidateChecks,
        int StatusRecordedTotal,
        int StatusRiskTotal,
        int StatusDisabledTotal,
        bool OwnerIndexCacheHit,
        bool StatusIndexCacheHit,
        double OwnerIndexMs,
        double StatusIndexMs,
        double CollectMs,
        double DrawLoopMs,
        double HudTextMs,
        double BlockLabelsMs,
        double SelectionMs)
    {
        public static OverlayCandidateDrawStats Empty { get; } = new(
            "none",
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            true,
            true,
            0d,
            0d,
            0d,
            0d,
            0d,
            0d,
            0d);

        public bool HasData => SourceCount > 0
            || VisibleCount > 0
            || DrawnCount > 0
            || BlockLabelsSource > 0
            || BlockLabelsVisible > 0
            || BlockLabelsDrawn > 0;

        public double TotalMs => OwnerIndexMs
            + StatusIndexMs
            + CollectMs
            + DrawLoopMs
            + HudTextMs
            + BlockLabelsMs
            + SelectionMs;
    }

    private readonly record struct OverlaySurfaceDrawStats(
        OverlaySurfaceSetDrawStats Fishable,
        OverlaySurfaceSetDrawStats Walkable,
        OverlayNearbyDebugCandidateStats NearbyCandidates,
        int HudTextCount,
        double HudMs)
    {
        public static OverlaySurfaceDrawStats Empty { get; } = new(
            OverlaySurfaceSetDrawStats.Empty,
            OverlaySurfaceSetDrawStats.Empty,
            OverlayNearbyDebugCandidateStats.Empty,
            0,
            0d);

        public bool HasData => Fishable.HasData || Walkable.HasData || NearbyCandidates.HasData || HudTextCount > 0;
        public int LineCount => Fishable.LineCount + Walkable.LineCount + NearbyCandidates.LineCount;
        public int PointCount => Fishable.PointCount + Walkable.PointCount + NearbyCandidates.PointCount;
        public int FilledTriangleCount => Fishable.FilledTriangleCount + Walkable.FilledTriangleCount;
        public int LabelCount => Fishable.LabelCount + Walkable.LabelCount + NearbyCandidates.LabelCount;
        public double TotalMs => Fishable.TotalMs + Walkable.TotalMs + NearbyCandidates.TotalMs + HudMs;
    }

    private readonly record struct OverlaySurfaceSetDrawStats(
        int SourceCount,
        int VisibleCount,
        int DrawnCount,
        int LineCount,
        int PointCount,
        int FilledTriangleCount,
        int LabelCount,
        double CollectMs,
        double DrawMs,
        double LabelMs)
    {
        public static OverlaySurfaceSetDrawStats Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0d, 0d, 0d);
        public bool HasData => SourceCount > 0 || VisibleCount > 0 || DrawnCount > 0;
        public double TotalMs => CollectMs + DrawMs + LabelMs;
    }

    private readonly record struct OverlayNearbyDebugCandidateStats(
        int SourceCount,
        int VisibleCount,
        int DrawnCount,
        int LineCount,
        int PointCount,
        int LabelCount,
        double CollectMs,
        double DrawMs)
    {
        public static OverlayNearbyDebugCandidateStats Empty { get; } = new(0, 0, 0, 0, 0, 0, 0d, 0d);
        public bool HasData => SourceCount > 0 || VisibleCount > 0 || DrawnCount > 0;
        public double TotalMs => CollectMs + DrawMs;
    }

    private readonly record struct OverlayBlockLabelDrawStats(
        int SourceBlockCount,
        int VisibleBlockCount,
        int DrawnLabelCount,
        int CandidateCheckCount,
        double CollectMs,
        double DrawMs)
    {
        public static OverlayBlockLabelDrawStats Empty { get; } = new(0, 0, 0, 0, 0d, 0d);
        public double TotalMs => CollectMs + DrawMs;
    }

    private readonly record struct OverlayBlockDistance(SurveyBlock Block, float Distance);

    private readonly record struct VisibleCandidate(ApproachCandidate Candidate, float Distance);

    private sealed record VisibleCandidateCollection(IReadOnlyList<VisibleCandidate> Candidates, int ClippedCount);

    private readonly record struct OverlayCandidateStatus(bool IsConfirmed, bool IsRisk, bool IsDisabled);

    private sealed record CandidateRecordOwner(uint FishingSpotId, string Name);
    private sealed record OverlayCandidateScreenPoint(ApproachCandidate Candidate, Vector2 ScreenPosition);

    private sealed record OverlayCandidateStatusIndex(
        Dictionary<string, OverlayCandidateStatus> StatusByCandidateKey,
        int RecordedTotal,
        int RiskTotal,
        int DisabledTotal)
    {
        public static OverlayCandidateStatusIndex Empty { get; } = new(
            new Dictionary<string, OverlayCandidateStatus>(StringComparer.Ordinal),
            0,
            0,
            0);
    }

    private sealed record OverlayOwnerIndex(
        IReadOnlyDictionary<string, List<CandidateRecordOwner>> RecordedOwnersByKey,
        IReadOnlyDictionary<string, List<CandidateRecordOwner>> RiskOwnersByKey,
        IReadOnlyDictionary<string, List<CandidateRecordOwner>> DisabledOwnersByKey,
        int RecordedPointCount,
        int RiskCandidateCount,
        int DisabledPointCount,
        int RecordedRiskKeyCount)
    {
        public static OverlayOwnerIndex Empty { get; } = new(
            new Dictionary<string, List<CandidateRecordOwner>>(StringComparer.Ordinal),
            new Dictionary<string, List<CandidateRecordOwner>>(StringComparer.Ordinal),
            new Dictionary<string, List<CandidateRecordOwner>>(StringComparer.Ordinal),
            0,
            0,
            0,
            0);
    }
}

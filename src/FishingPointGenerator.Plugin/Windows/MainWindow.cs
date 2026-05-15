using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using FishingPointGenerator.Core.Models;
using FishingPointGenerator.Plugin.Services;

namespace FishingPointGenerator.Plugin.Windows;

internal sealed class MainWindow : Window, IDisposable
{
    private const float MinimumCastBlockSnapDistance = 1f;
    private const float MaximumCastBlockSnapDistance = 50f;
    private const float MinimumCastBlockFillRange = 1f;
    private const float MaximumCastBlockFillRange = 1000f;
    private const float MinimumOverlayDistance = 10f;
    private const float MaximumOverlayDistance = 1000f;
    private const int MinimumOverlayCandidateLimit = 10;
    private const int MaximumOverlayCandidateLimit = 5000;
    private const float CompactListBreakpoint = 560f;

    private static readonly Vector4 MutedText = new(0.72f, 0.72f, 0.72f, 1f);
    private static readonly Vector4 GoodText = new(0.35f, 0.86f, 0.52f, 1f);
    private static readonly Vector4 WarnText = new(1f, 0.68f, 0.24f, 1f);
    private static readonly Vector4 ErrorText = new(1f, 0.35f, 0.32f, 1f);
    private static readonly Vector4 AccentText = new(0.42f, 0.72f, 1f, 1f);

    private readonly SpotWorkflowSession session;
    private string territoryFilterText = string.Empty;
    private string targetFilterText = string.Empty;
    private string targetIdText = string.Empty;
    private MainTab activeTab = MainTab.Territories;

    private enum MainTab
    {
        Territories,
        Spots,
        Maintenance,
        Tools,
    }

    public MainWindow(SpotWorkflowSession session)
        : base("FishingPointGenerator###FishingPointGeneratorMainWindow")
    {
        this.session = session;
    }

    public void Dispose() { }

    public override void PreDraw()
    {
        ImGui.SetNextWindowSize(new Vector2(920f, 760f), ImGuiCond.FirstUseEver);
        base.PreDraw();
    }

    public override void Draw()
    {
        UpdateOverlayPointDisableUiBounds();

        DrawHeader();
        ImGui.Separator();

        DrawFieldWorkflowPanel();
        ImGui.Separator();

        DrawMainTabs();
        ImGui.Separator();

        switch (activeTab)
        {
            case MainTab.Territories:
                DrawTerritoryDrawer();
                break;
            case MainTab.Spots:
                DrawSpotList();
                break;
            case MainTab.Maintenance:
                DrawSpotDetail();
                break;
            case MainTab.Tools:
                DrawOutputTools();
                DrawDataCleanup();
                DrawDebugOptions();
                DrawPaths();
                break;
        }
    }

    private void UpdateOverlayPointDisableUiBounds()
    {
        var min = ImGui.GetWindowPos();
        var max = min + ImGui.GetWindowSize();
        session.SetOverlayPointDisableUiWindowBounds(min, max);
    }

    private void DrawMainTabs()
    {
        var tabLine = 0f;
        DrawMainTabButton("领地", MainTab.Territories, ref tabLine);
        DrawMainTabButton("钓场", MainTab.Spots, ref tabLine);
        DrawMainTabButton("维护", MainTab.Maintenance, ref tabLine);
        DrawMainTabButton("工具", MainTab.Tools, ref tabLine);
    }

    private void DrawMainTabButton(string label, MainTab tab, ref float tabLine)
    {
        var tabLabel = activeTab == tab ? $"[{label}]" : label;
        if (FlowActionButton(tabLabel, false, ref tabLine))
            activeTab = tab;
    }

    private void DrawHeader()
    {
        ImGui.TextColored(AccentText, "FishingPointGenerator");
        ImGui.SameLine();
        DrawStatusBadge();

        ImGui.PushTextWrapPos();
        ImGui.TextColored(GetMessageColor(), session.LastMessage);
        ImGui.PopTextWrapPos();

        if (session.TerritoryScanInProgress)
        {
            var progress = session.TerritoryScanProgress;
            var text = progress is null ? "扫描进行中" : $"{progress.Stage}: {progress.Message}";
            ImGui.ProgressBar(session.TerritoryScanProgressFraction, new Vector2(-1f, 0f), text);
        }
    }

    private void DrawFieldWorkflowPanel()
    {
        var hasTarget = session.CurrentTarget is not null;
        var sameTerritory = session.SelectedTerritoryIsCurrent;
        var hasCurrentTerritory = session.CurrentTerritoryId != 0;
        var statusText = hasTarget
            ? $"{session.CurrentTargetDisplayName} / {FormatStatus(session.CurrentAnalysis?.Status)}"
            : "-";

        DrawSectionTitle("现场操作");
        DrawSummaryRow(
            "区域",
            $"{session.CurrentTerritoryId} / {FormatTerritoryTitle(session.SelectedTerritoryId, session.SelectedTerritoryName)}");
        DrawSummaryRow("目标", statusText, GetStatusColor(session.CurrentAnalysis?.Status));
        DrawSummaryRow("自动", session.AutoSurveyStatusText, session.AutoSurveyRunning ? AccentText : MutedText);

        var actionLine = 0f;
        if (FlowActionButton("自动一次", session.AutoSurveyRunning || !hasCurrentTerritory, ref actionLine))
            session.StartAutoSurveyOnce();
        if (FlowActionButton("循环点亮", session.AutoSurveyRunning || !hasCurrentTerritory, ref actionLine))
            session.StartAutoSurveyLoop();
        if (FlowActionButton("停止自动", !session.AutoSurveyRunning, ref actionLine))
            session.StopAutoSurvey();
        if (FlowActionButton("传送水晶", !hasTarget, ref actionLine))
            session.TeleportToCurrentTargetAetheryte();
        if (FlowActionButton("钓场插旗", !hasTarget || !sameTerritory, ref actionLine))
            session.PlaceCurrentTargetFlag();
        if (FlowActionButton(
                "推荐插旗",
                !hasTarget || !sameTerritory || !session.CurrentCandidateSelectionIsActionable,
                ref actionLine))
            session.PlaceSelectedCandidateFlag();

        var pointDisableMode = session.OverlayPointDisableMode;
        if (FlowCheckbox("点选/框选禁用/恢复", ref pointDisableMode, ref actionLine))
            session.OverlayPointDisableMode = pointDisableMode;
    }

    private void DrawTerritoryDrawer()
    {
        DrawSectionTitle("领地");

        var actionLine = 0f;
        if (FlowActionButton("刷新目录", false, ref actionLine))
            session.RefreshCatalog();
        if (FlowActionButton("当前区域", false, ref actionLine))
            session.RefreshCurrentTerritory(selectNext: false);
        if (FlowActionButton("扫描当前", session.TerritoryScanInProgress, ref actionLine))
            session.ScanCurrentTerritory();
        if (FlowActionButton("取消扫描", !session.TerritoryScanInProgress, ref actionLine))
            session.CancelTerritoryScan();

        DrawFilterInput("过滤", "##territory_filter", ref territoryFilterText);

        var visible = GetVisibleTerritories().ToList();
        DrawListCount(visible.Count, session.TerritorySummaries.Count);
        var height = Math.Max(320f, ImGui.GetContentRegionAvail().Y - 4f);
        if (ImGui.BeginChild("##fpg_territory_list", new Vector2(0f, height), true, ImGuiWindowFlags.None))
        {
            var width = ImGui.GetContentRegionAvail().X;
            if (width < CompactListBreakpoint)
                DrawTerritoryRowsCompact(visible);
            else
                DrawTerritoryRowsWide(visible, width);
        }

        ImGui.EndChild();
    }

    private void DrawTerritoryRowsWide(IReadOnlyList<TerritoryMaintenanceSummary> territories, float width)
    {
        const float spotWidth = 44f;
        const float confirmedWidth = 44f;
        const float maintenanceWidth = 58f;
        const float riskWidth = 44f;
        const float gap = 10f;
        var titleWidth = Math.Max(180f, width - spotWidth - confirmedWidth - maintenanceWidth - riskWidth - (gap * 4f));
        var x = ImGui.GetCursorPosX();

        DrawPseudoHeader("领地", titleWidth);
        DrawPseudoHeaderCell(x, titleWidth + gap, spotWidth, "钓场");
        DrawPseudoHeaderCell(x, titleWidth + spotWidth + (gap * 2f), confirmedWidth, "确认");
        DrawPseudoHeaderCell(x, titleWidth + spotWidth + confirmedWidth + (gap * 3f), maintenanceWidth, "需维护");
        DrawPseudoHeaderCell(x, titleWidth + spotWidth + confirmedWidth + maintenanceWidth + (gap * 4f), riskWidth, "风险");
        ImGui.Separator();

        foreach (var summary in territories)
        {
            var title = FormatTerritoryTitle(summary.TerritoryId, summary.TerritoryName);
            if (summary.IsCurrentTerritory)
                title += "  当前";

            if (ImGui.Selectable(
                    $"{FitTextToWidth(title, titleWidth)}##territory_{summary.TerritoryId}",
                    summary.IsSelected,
                    ImGuiSelectableFlags.None,
                    new Vector2(titleWidth, 0f)))
            {
                session.SelectTerritory(summary.TerritoryId, selectNext: false);
                activeTab = MainTab.Spots;
            }

            var riskCount = summary.RiskCount + summary.WeakCoverageCount;
            DrawPseudoCell(x, titleWidth + gap, spotWidth, summary.SpotCount.ToString());
            DrawPseudoCell(x, titleWidth + spotWidth + (gap * 2f), confirmedWidth, summary.ConfirmedCount.ToString(), summary.ConfirmedCount > 0 ? GoodText : MutedText);
            DrawPseudoCell(
                x,
                titleWidth + spotWidth + confirmedWidth + (gap * 3f),
                maintenanceWidth,
                summary.MaintenanceNeededCount.ToString(),
                summary.MaintenanceNeededCount > 0 ? WarnText : MutedText);
            DrawPseudoCell(x, titleWidth + spotWidth + confirmedWidth + maintenanceWidth + (gap * 4f), riskWidth, riskCount.ToString(), riskCount > 0 ? ErrorText : MutedText);
        }
    }

    private void DrawTerritoryRowsCompact(IReadOnlyList<TerritoryMaintenanceSummary> territories)
    {
        foreach (var summary in territories)
        {
            var title = FormatTerritoryTitle(summary.TerritoryId, summary.TerritoryName);
            if (summary.IsCurrentTerritory)
                title += "  当前";

            if (ImGui.Selectable($"{FitTextToWidth(title, ImGui.GetContentRegionAvail().X)}##territory_{summary.TerritoryId}", summary.IsSelected))
            {
                session.SelectTerritory(summary.TerritoryId, selectNext: false);
                activeTab = MainTab.Spots;
            }

            var riskCount = summary.RiskCount + summary.WeakCoverageCount;
            var metricLine = 0f;
            DrawInlineMetric("钓场", summary.SpotCount.ToString(), MutedText, ref metricLine);
            DrawInlineMetric("确认", summary.ConfirmedCount.ToString(), summary.ConfirmedCount > 0 ? GoodText : MutedText, ref metricLine);
            DrawInlineMetric("需维护", summary.MaintenanceNeededCount.ToString(), summary.MaintenanceNeededCount > 0 ? WarnText : MutedText, ref metricLine);
            DrawInlineMetric("风险", riskCount.ToString(), riskCount > 0 ? ErrorText : MutedText, ref metricLine);
        }
    }

    private void DrawSpotList()
    {
        DrawSectionTitle("钓场");

        ImGui.TextColored(MutedText, "RowId");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(110f);
        ImGui.InputText("##target_id", ref targetIdText, 16);
        ImGui.SameLine();
        if (ActionButton("打开"))
        {
            if (uint.TryParse(targetIdText.Trim(), out var targetId))
            {
                session.SelectTarget(targetId);
                activeTab = MainTab.Maintenance;
            }
        }

        SameLineIfFits("下一个");
        if (ActionButton("下一个", session.TargetCount == 0))
        {
            session.SelectNextTarget();
            activeTab = MainTab.Maintenance;
        }

        DrawFilterInput("过滤", "##spot_filter", ref targetFilterText);

        DrawTerritoryOverview();

        if (session.CurrentTerritoryTargets.Count == 0)
        {
            ImGui.TextColored(MutedText, "未加载领地钓场。");
            return;
        }

        var visible = GetVisibleTargets().Take(300).ToList();
        DrawListCount(visible.Count, session.CurrentTerritoryTargets.Count);
        var height = Math.Max(360f, ImGui.GetContentRegionAvail().Y - 4f);
        if (ImGui.BeginChild("##fpg_spot_list", new Vector2(0f, height), true, ImGuiWindowFlags.None))
        {
            var width = ImGui.GetContentRegionAvail().X;
            if (width < CompactListBreakpoint)
                DrawSpotRowsCompact(visible);
            else
                DrawSpotRowsWide(visible, width);
        }

        ImGui.EndChild();
    }

    private void DrawSpotRowsWide(IReadOnlyList<FishingSpotTarget> targets, float width)
    {
        const float statusWidth = 68f;
        const float pointsWidth = 34f;
        const float candidateWidth = 44f;
        const float mapWidth = 82f;
        const float gap = 10f;
        var nameWidth = Math.Max(190f, width - statusWidth - pointsWidth - candidateWidth - mapWidth - (gap * 4f));
        var x = ImGui.GetCursorPosX();

        DrawPseudoHeader("钓场", nameWidth);
        DrawPseudoHeaderCell(x, nameWidth + gap, statusWidth, "状态");
        DrawPseudoHeaderCell(x, nameWidth + statusWidth + (gap * 2f), pointsWidth, "点");
        DrawPseudoHeaderCell(x, nameWidth + statusWidth + pointsWidth + (gap * 3f), candidateWidth, "候选");
        DrawPseudoHeaderCell(x, nameWidth + statusWidth + pointsWidth + candidateWidth + (gap * 4f), mapWidth, "地图");
        ImGui.Separator();

        foreach (var target in targets)
        {
            var analysis = session.Analyses.FirstOrDefault(analysis => analysis.Key == target.Key);
            var selected = session.CurrentTarget?.Key == target.Key;
            var title = $"{target.FishingSpotId} {target.Name}";

            if (ImGui.Selectable($"{FitTextToWidth(title, nameWidth)}##spot_{target.FishingSpotId}", selected, ImGuiSelectableFlags.None, new Vector2(nameWidth, 0f)))
            {
                session.SelectTarget(target.FishingSpotId);
                activeTab = MainTab.Maintenance;
            }

            DrawPseudoCell(x, nameWidth + gap, statusWidth, FormatStatus(analysis?.Status), GetStatusColor(analysis?.Status));
            DrawPseudoCell(x, nameWidth + statusWidth + (gap * 2f), pointsWidth, (analysis?.ConfirmedApproachPointCount ?? 0).ToString());
            DrawPseudoCell(x, nameWidth + statusWidth + pointsWidth + (gap * 3f), candidateWidth, (analysis?.CandidateCount ?? 0).ToString());
            DrawPseudoCell(x, nameWidth + statusWidth + pointsWidth + candidateWidth + (gap * 4f), mapWidth, $"{target.MapX:F1}, {target.MapY:F1}");
        }
    }

    private void DrawSpotRowsCompact(IReadOnlyList<FishingSpotTarget> targets)
    {
        foreach (var target in targets)
        {
            var analysis = session.Analyses.FirstOrDefault(analysis => analysis.Key == target.Key);
            var selected = session.CurrentTarget?.Key == target.Key;
            var title = $"{target.FishingSpotId} {target.Name}";
            if (ImGui.Selectable($"{FitTextToWidth(title, ImGui.GetContentRegionAvail().X)}##spot_{target.FishingSpotId}", selected))
            {
                session.SelectTarget(target.FishingSpotId);
                activeTab = MainTab.Maintenance;
            }

            var metricLine = 0f;
            DrawInlineMetric("状态", FormatStatus(analysis?.Status), GetStatusColor(analysis?.Status), ref metricLine);
            DrawInlineMetric("点", (analysis?.ConfirmedApproachPointCount ?? 0).ToString(), MutedText, ref metricLine);
            DrawInlineMetric("候选", (analysis?.CandidateCount ?? 0).ToString(), MutedText, ref metricLine);
            DrawInlineMetric("地图", $"{target.MapX:F1}, {target.MapY:F1}", MutedText, ref metricLine);
        }
    }

    private void DrawSpotDetail()
    {
        DrawSectionTitle("维护目标");

        var target = session.CurrentTarget;
        if (target is null)
        {
            ImGui.TextColored(MutedText, "未打开维护目标。");
            return;
        }

        DrawSelectedSpotSummary(target);
        DrawTargetActions(session.CurrentAnalysis);
        DrawPointDebugDetails();
    }

    private void DrawSelectedSpotSummary(FishingSpotTarget target)
    {
        var analysis = session.CurrentAnalysis;
        DrawSummaryRow("RowId", target.FishingSpotId.ToString());
        DrawSummaryRow("名称", target.Name);
        DrawSummaryRow("领地", FormatTerritoryTitle(target.TerritoryId, target.TerritoryName));
        DrawSummaryRow("状态", FormatStatus(analysis?.Status), GetStatusColor(analysis?.Status));
        DrawSummaryRow("复核", FormatReviewDecision(session.CurrentReviewDecision));
        DrawSummaryRow("真实点", session.CurrentApproachPoints.Count(point => point.Status == ApproachPointStatus.Confirmed).ToString());
        DrawSummaryRow("候选点", (analysis?.CandidateCount ?? 0).ToString());
        DrawSummaryRow("推荐候选", session.CurrentCandidateSelection?.ModeText ?? "-");
        if (session.CurrentCandidateSelection is not null)
        {
            DrawSummaryRow(
                "候选状态",
                session.CurrentCandidateSelectionIsActionable ? "可用" : "仅参考",
                session.CurrentCandidateSelectionIsActionable ? GoodText : WarnText);
        }

        DrawSummaryRow("鱼类物品", target.ItemIds.Count == 0 ? "-" : string.Join(", ", target.ItemIds));
        DrawSummaryRow("半径", target.Radius.ToString("F1"));
    }

    private void DrawApproachPoints()
    {
        ImGui.Spacing();
        ImGui.TextColored(AccentText, "真实可钓点");
        if (session.CurrentApproachPoints.Count == 0)
        {
            ImGui.TextColored(MutedText, "尚未记录真实点位。");
            return;
        }

        if (ImGui.BeginChild("##fpg_approach_points", new Vector2(0f, 150f), true, ImGuiWindowFlags.None))
        {
            var width = ImGui.GetContentRegionAvail().X;
            if (width < CompactListBreakpoint)
                DrawApproachPointRowsCompact();
            else
                DrawApproachPointRowsWide(width);
        }

        ImGui.EndChild();
    }

    private void DrawApproachPointRowsWide(float width)
    {
        const float statusWidth = 50f;
        const float pointWidth = 150f;
        const float rotationWidth = 62f;
        const float sourceWidth = 78f;
        const float evidenceWidth = 36f;
        const float gap = 10f;
        var surfaceWidth = Math.Max(70f, width - statusWidth - pointWidth - rotationWidth - sourceWidth - evidenceWidth - (gap * 5f));
        var x = ImGui.GetCursorPosX();

        DrawPseudoHeader("状态", statusWidth);
        DrawPseudoHeaderCell(x, statusWidth + gap, pointWidth, "点位");
        DrawPseudoHeaderCell(x, statusWidth + pointWidth + (gap * 2f), rotationWidth, "朝向");
        DrawPseudoHeaderCell(x, statusWidth + pointWidth + rotationWidth + (gap * 3f), sourceWidth, "来源");
        DrawPseudoHeaderCell(x, statusWidth + pointWidth + rotationWidth + sourceWidth + (gap * 4f), surfaceWidth, "水系");
        DrawPseudoHeaderCell(x, statusWidth + pointWidth + rotationWidth + sourceWidth + surfaceWidth + (gap * 5f), evidenceWidth, "证据");
        ImGui.Separator();

        foreach (var point in session.CurrentApproachPoints.OrderBy(point => point.PointId, StringComparer.Ordinal))
        {
            ImGui.TextColored(point.Status == ApproachPointStatus.Confirmed ? GoodText : MutedText, FormatApproachPointStatus(point.Status));
            DrawPseudoCell(x, statusWidth + gap, pointWidth, FormatPoint(point.Position));
            DrawPseudoCell(x, statusWidth + pointWidth + (gap * 2f), rotationWidth, point.Rotation.ToString("F3"));
            DrawPseudoCell(x, statusWidth + pointWidth + rotationWidth + (gap * 3f), sourceWidth, FormatSource(point.SourceKind));
            DrawPseudoCell(
                x,
                statusWidth + pointWidth + rotationWidth + sourceWidth + (gap * 4f),
                surfaceWidth,
                string.IsNullOrWhiteSpace(point.SourceSurfaceGroupId) ? "-" : point.SourceSurfaceGroupId);
            DrawPseudoCell(x, statusWidth + pointWidth + rotationWidth + sourceWidth + surfaceWidth + (gap * 5f), evidenceWidth, point.EvidenceIds.Count.ToString());
        }
    }

    private void DrawApproachPointRowsCompact()
    {
        foreach (var point in session.CurrentApproachPoints.OrderBy(point => point.PointId, StringComparer.Ordinal))
        {
            ImGui.TextColored(point.Status == ApproachPointStatus.Confirmed ? GoodText : MutedText, FormatApproachPointStatus(point.Status));
            ImGui.SameLine();
            ImGui.TextUnformatted(FitTextToWidth(FormatPoint(point.Position), ImGui.GetContentRegionAvail().X));
            var metricLine = 0f;
            DrawInlineMetric("朝向", point.Rotation.ToString("F3"), MutedText, ref metricLine);
            DrawInlineMetric("来源", FormatSource(point.SourceKind), MutedText, ref metricLine);
            DrawInlineMetric("水系", string.IsNullOrWhiteSpace(point.SourceSurfaceGroupId) ? "-" : point.SourceSurfaceGroupId, MutedText, ref metricLine);
            DrawInlineMetric("证据", point.EvidenceIds.Count.ToString(), MutedText, ref metricLine);
        }
    }

    private void DrawCandidateSelection()
    {
        var selection = session.CurrentCandidateSelection;
        if (selection is null)
            return;

        var candidate = selection.Candidate;
        ImGui.Spacing();
        ImGui.TextColored(AccentText, "当前候选");

        DrawSummaryRow("状态", selection.ModeText);
        DrawSummaryRow(
            "可用",
            session.CurrentCandidateSelectionIsActionable ? "可插旗" : "仅参考",
            session.CurrentCandidateSelectionIsActionable ? GoodText : WarnText);
        DrawSummaryRow("点位", FormatPoint(candidate.Position));
        DrawSummaryRow("朝向", candidate.Rotation.ToString("F3"));
        DrawSummaryRow("距中心", candidate.DistanceToTargetCenterMeters.ToString("F1"));
        DrawSummaryRow("距角色", FormatNullableDistance(selection.DistanceToPlayerMeters));
        DrawSummaryRow("路径", FormatNullableDistance(selection.PathLengthMeters));
        DrawSummaryRow("检查候选", selection.CheckedCandidateCount == 0 ? "-" : selection.CheckedCandidateCount.ToString());
        DrawSummaryRow("可飞", selection.CanFly ? "是" : "否");
        DrawSummaryRow("Surface", string.IsNullOrWhiteSpace(candidate.SurfaceGroupId) ? "-" : candidate.SurfaceGroupId);
        DrawSummaryRow("Block", string.IsNullOrWhiteSpace(candidate.BlockId) ? "-" : candidate.BlockId);
        DrawSummaryRow("Fingerprint", candidate.CandidateFingerprint);
        DrawSummaryRow("说明", string.IsNullOrWhiteSpace(selection.Note) ? "-" : selection.Note);
    }

    private void DrawTargetActions(SpotAnalysis? analysis)
    {
        var hasTarget = session.CurrentTarget is not null;
        var sameTerritory = session.SelectedTerritoryIsCurrent;

        ImGui.Spacing();
        ImGui.TextColored(AccentText, "派生与确认");
        var actionLine = 0f;
        if (FlowActionButton("派生候选", !hasTarget || session.TerritoryCandidateCount == 0, ref actionLine))
            session.ScanCurrentTarget();
        if (FlowActionButton("刷新推荐候选", !hasTarget || !sameTerritory || session.CurrentScan is null, ref actionLine))
            session.RefreshCandidateSelection();
        if (FlowActionButton("确认当前站位", !hasTarget || !sameTerritory, ref actionLine))
            session.ConfirmCurrentStanding();

        ImGui.Spacing();
        ImGui.TextColored(AccentText, "复核");
        actionLine = 0f;
        if (FlowActionButton("允许弱覆盖导出", analysis?.Status != SpotAnalysisStatus.WeakCoverage, ref actionLine))
            session.AllowWeakCoverageExport();
        if (FlowActionButton("允许风险导出", analysis?.Status != SpotAnalysisStatus.MixedRisk, ref actionLine))
            session.AllowRiskExport();
        if (FlowActionButton("忽略钓场", !hasTarget, ref actionLine))
            session.IgnoreCurrentTarget();

        ImGui.Spacing();
        ImGui.TextColored(AccentText, "报告");
        actionLine = 0f;
        if (FlowActionButton("生成维护目标报告", !hasTarget, ref actionLine))
            session.GenerateCurrentReport();
    }

    private void DrawPointDebugDetails()
    {
        if (!ImGui.CollapsingHeader("点位调试明细"))
            return;

        DrawApproachPoints();
        DrawCandidateSelection();
        DrawPointDebugActions();
    }

    private void DrawPointDebugActions()
    {
        var hasTarget = session.CurrentTarget is not null;
        var hasCandidateSelection = session.CurrentCandidateSelection?.Candidate is not null;
        var sameTerritory = session.SelectedTerritoryIsCurrent;

        ImGui.Spacing();
        ImGui.TextColored(AccentText, "点位操作");
        var actionLine = 0f;
        if (FlowActionButton("插旗未记录候选", !hasTarget || !sameTerritory || session.CurrentScan is null, ref actionLine))
            session.PlaceNearestUnrecordedCandidateFlag();
        if (FlowActionButton("排除推荐候选", !hasCandidateSelection || !sameTerritory, ref actionLine))
            session.RejectSelectedCandidate();
    }

    private void DrawTerritoryOverview()
    {
        var riskCount = session.MixedRiskCount + session.WeakCoverageCount;
        var metricLine = 0f;
        DrawInlineMetric("钓场", session.TargetCount.ToString(), MutedText, ref metricLine);
        DrawInlineMetric("确认", session.ConfirmedCount.ToString(), session.ConfirmedCount > 0 ? GoodText : MutedText, ref metricLine);
        DrawInlineMetric("需维护", session.MaintenanceNeededCount.ToString(), session.MaintenanceNeededCount > 0 ? WarnText : MutedText, ref metricLine);
        DrawInlineMetric("风险", riskCount.ToString(), riskCount > 0 ? ErrorText : MutedText, ref metricLine);
    }

    private void DrawDebugOptions()
    {
        if (!ImGui.CollapsingHeader("调试与显示"))
            return;

        var autoRecord = session.AutoRecordCastsEnabled;
        if (ImGui.Checkbox("自动记录抛竿", ref autoRecord))
            session.AutoRecordCastsEnabled = autoRecord;

        var overlayEnabled = session.OverlayEnabled;
        if (ImGui.Checkbox("显示 overlay", ref overlayEnabled))
            session.OverlayEnabled = overlayEnabled;

        var showCandidates = session.OverlayShowCandidates;
        if (ImGui.Checkbox("显示候选点", ref showCandidates))
            session.OverlayShowCandidates = showCandidates;

        var showTerritoryCache = session.OverlayShowTerritoryCache;
        if (ImGui.Checkbox("显示领地内存候选", ref showTerritoryCache))
            session.OverlayShowTerritoryCache = showTerritoryCache;

        var showRadius = session.OverlayShowTargetRadius;
        if (ImGui.Checkbox("显示钓场半径", ref showRadius))
            session.OverlayShowTargetRadius = showRadius;

        var showFishableDebug = session.OverlayShowFishableDebug;
        if (ImGui.Checkbox("显示 Fishable 水面", ref showFishableDebug))
            session.OverlayShowFishableDebug = showFishableDebug;

        var showWalkableDebug = session.OverlayShowWalkableDebug;
        if (ImGui.Checkbox("显示 Walkable 可走面", ref showWalkableDebug))
            session.OverlayShowWalkableDebug = showWalkableDebug;

        var actionLine = 0f;
        if (FlowActionButton("清除调试层显示", session.NearbyDebugOverlay is null, ref actionLine))
            session.ClearNearbyDebugOverlay();

        DrawFloatInput(
            "抛竿块选择距离(m)",
            session.CastBlockSnapDistanceMeters,
            MinimumCastBlockSnapDistance,
            MaximumCastBlockSnapDistance,
            value => session.CastBlockSnapDistanceMeters = value);
        DrawFloatInput("一次点亮水系范围(m)", session.CastBlockFillRangeMeters, MinimumCastBlockFillRange, MaximumCastBlockFillRange, value => session.CastBlockFillRangeMeters = value);

        DrawFloatInput(
            "overlay 距离(m)",
            session.OverlayMaxDistanceMeters,
            MinimumOverlayDistance,
            MaximumOverlayDistance,
            value => session.OverlayMaxDistanceMeters = value,
            10f,
            50f,
            "%.0f");
        DrawIntInput("overlay 点数上限", session.OverlayCandidateLimit, MinimumOverlayCandidateLimit, MaximumOverlayCandidateLimit, value => session.OverlayCandidateLimit = value);
    }

    private void DrawDataCleanup()
    {
        if (!ImGui.CollapsingHeader("数据清理"))
            return;

        var ctrlDown = ImGui.GetIO().KeyCtrl;
        var hasTarget = session.CurrentTarget is not null;
        var hasTerritory = session.SelectedTerritoryId != 0;

        ImGui.TextColored(WarnText, "按住 Ctrl 启用清理按钮。");
        var actionLine = 0f;
        if (FlowActionButton("清维护目标数据", !ctrlDown || !hasTarget, ref actionLine))
            session.ClearCurrentSpotMaintenance();
        if (FlowActionButton("清当前领地维护", !ctrlDown || !hasTerritory, ref actionLine))
            session.ClearCurrentTerritoryMaintenance();
        if (FlowActionButton("清当前领地内存候选", !ctrlDown || !hasTerritory, ref actionLine))
            session.ClearCurrentTerritoryCandidates();
    }

    private void DrawOutputTools()
    {
        if (!ImGui.CollapsingHeader("导出"))
            return;

        var actionLine = 0f;
        if (FlowActionButton("导出全部已确认", false, ref actionLine))
            session.ExportConfirmed();
    }

    private void DrawPaths()
    {
        if (!ImGui.CollapsingHeader("文件"))
            return;

        DrawSummaryRow("数据", session.DataRoot);
        DrawSummaryRow("目录", session.CatalogPath);
        DrawSummaryRow("维护", string.IsNullOrWhiteSpace(session.MaintenancePath) ? "-" : session.MaintenancePath);
        DrawSummaryRow("内存候选", session.TerritoryCandidateCount.ToString());
        DrawSummaryRow("导出", session.ExportPath);
    }

    private IEnumerable<TerritoryMaintenanceSummary> GetVisibleTerritories()
    {
        var filter = territoryFilterText.Trim();
        if (string.IsNullOrWhiteSpace(filter))
            return session.TerritorySummaries;

        return session.TerritorySummaries.Where(summary =>
            summary.TerritoryId.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase)
            || summary.TerritoryName.Contains(filter, StringComparison.OrdinalIgnoreCase));
    }

    private IEnumerable<FishingSpotTarget> GetVisibleTargets()
    {
        var filter = targetFilterText.Trim();
        if (string.IsNullOrWhiteSpace(filter))
            return session.CurrentTerritoryTargets;

        return session.CurrentTerritoryTargets.Where(target =>
            target.FishingSpotId.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase)
            || target.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));
    }

    private void DrawStatusBadge()
    {
        var (text, color) = GetStatus();
        ImGui.TextColored(color, text);
    }

    private (string Text, Vector4 Color) GetStatus()
    {
        if (session.TerritoryScanInProgress)
            return ("扫描中", AccentText);
        if (IsErrorMessage(session.LastMessage))
            return ("错误", ErrorText);
        if (session.TargetCount == 0)
            return ("无目录", MutedText);
        if (session.CurrentTarget is null)
            return ("无维护目标", WarnText);
        if (session.CurrentApproachPoints.Any(point => point.Status == ApproachPointStatus.Confirmed))
            return ("维护中", GoodText);
        return ("待维护", WarnText);
    }

    private Vector4 GetMessageColor()
    {
        if (IsErrorMessage(session.LastMessage))
            return ErrorText;
        if (session.LastMessage.Contains("没有", StringComparison.Ordinal)
            || session.LastMessage.Contains("未", StringComparison.Ordinal)
            || session.LastMessage.Contains("无", StringComparison.Ordinal)
            || session.LastMessage.Contains("不可用", StringComparison.Ordinal))
            return WarnText;
        return MutedText;
    }

    private static bool IsErrorMessage(string message)
    {
        return message.Contains("failed", StringComparison.OrdinalIgnoreCase)
            || message.Contains("失败", StringComparison.Ordinal)
            || message.Contains("错误", StringComparison.Ordinal);
    }

    private static Vector4 GetStatusColor(SpotAnalysisStatus? status)
    {
        return status switch
        {
            SpotAnalysisStatus.Confirmed => GoodText,
            SpotAnalysisStatus.WeakCoverage => WarnText,
            SpotAnalysisStatus.MixedRisk => ErrorText,
            SpotAnalysisStatus.NoCandidate => ErrorText,
            SpotAnalysisStatus.Ignored => MutedText,
            SpotAnalysisStatus.NeedsVisit => WarnText,
            SpotAnalysisStatus.NeedsScan => WarnText,
            _ => MutedText,
        };
    }

    private static string FormatStatus(SpotAnalysisStatus? status)
    {
        return status switch
        {
            SpotAnalysisStatus.NotStarted => "未开始",
            SpotAnalysisStatus.NeedsScan => "需扫描",
            SpotAnalysisStatus.NeedsVisit => "需到访",
            SpotAnalysisStatus.Confirmed => "已确认",
            SpotAnalysisStatus.WeakCoverage => "弱覆盖",
            SpotAnalysisStatus.MixedRisk => "混合风险",
            SpotAnalysisStatus.NoCandidate => "无候选点",
            SpotAnalysisStatus.Ignored => "已忽略",
            SpotAnalysisStatus.Stale => "已过期",
            _ => "未开始",
        };
    }

    private static string FormatReviewDecision(SpotReviewDecision decision)
    {
        if (decision == SpotReviewDecision.None)
            return "-";

        var values = new List<string>();
        if ((decision & SpotReviewDecision.IgnoreSpot) == SpotReviewDecision.IgnoreSpot)
            values.Add("忽略");
        if ((decision & SpotReviewDecision.AllowWeakCoverageExport) == SpotReviewDecision.AllowWeakCoverageExport)
            values.Add("弱覆盖");
        if ((decision & SpotReviewDecision.AllowRiskExport) == SpotReviewDecision.AllowRiskExport)
            values.Add("风险");
        if ((decision & SpotReviewDecision.NeedsManualReview) == SpotReviewDecision.NeedsManualReview)
            values.Add("需复核");

        return values.Count == 0 ? "-" : string.Join("+", values);
    }

    private static string FormatApproachPointStatus(ApproachPointStatus status)
    {
        return status switch
        {
            ApproachPointStatus.Confirmed => "确认",
            ApproachPointStatus.Rejected => "拒绝",
            ApproachPointStatus.Disabled => "禁用",
            _ => status.ToString(),
        };
    }

    private static string FormatSource(ApproachPointSourceKind source)
    {
        return source switch
        {
            ApproachPointSourceKind.Manual => "手动",
            ApproachPointSourceKind.Candidate => "候选",
            ApproachPointSourceKind.AutoCastFill => "抛竿连锁",
            ApproachPointSourceKind.Imported => "导入",
            _ => source.ToString(),
        };
    }

    private static string FormatTerritoryTitle(uint territoryId, string territoryName)
    {
        return string.IsNullOrWhiteSpace(territoryName)
            ? territoryId.ToString()
            : $"{territoryId} {territoryName}";
    }

    private static string FormatPoint(Point3 point)
    {
        return $"{point.X:F2}, {point.Y:F2}, {point.Z:F2}";
    }

    private static string FormatNullableDistance(float? distance)
    {
        return distance is null
            ? "-"
            : distance.Value == float.MaxValue
                ? "inf"
                : $"{distance.Value:F1}m";
    }

    private static void DrawSummaryRow(string label, string value)
    {
        DrawSummaryRow(label, value, null);
    }

    private static void DrawSummaryRow(string label, string value, Vector4? valueColor)
    {
        var rowStart = ImGui.GetCursorPosX();
        var labelWidth = Math.Clamp(ImGui.GetContentRegionAvail().X * 0.28f, 72f, 132f);
        ImGui.TextColored(MutedText, label);
        ImGui.SameLine(rowStart + labelWidth);
        ImGui.PushTextWrapPos();
        if (valueColor is { } color)
            ImGui.TextColored(color, value);
        else
            ImGui.TextUnformatted(value);
        ImGui.PopTextWrapPos();
    }

    private static void DrawInlineMetric(string label, string value, Vector4 color, ref float lineWidth)
    {
        var width = ImGui.CalcTextSize($"{label} {value}").X + 14f;
        if (lineWidth > 0f && lineWidth + width <= ImGui.GetContentRegionAvail().X)
            ImGui.SameLine();
        else if (lineWidth > 0f)
            lineWidth = 0f;

        ImGui.TextColored(MutedText, label);
        ImGui.SameLine();
        ImGui.TextColored(color, value);
        lineWidth += width;
    }

    private static void DrawSectionTitle(string title)
    {
        ImGui.TextColored(AccentText, title);
    }

    private static bool ActionButton(string label, bool disabled = false)
    {
        ImGui.BeginDisabled(disabled);
        var clicked = ImGui.Button(label);
        ImGui.EndDisabled();
        return clicked && !disabled;
    }

    private static bool FlowActionButton(string label, bool disabled, ref float lineWidth)
    {
        var width = ImGui.CalcTextSize(label).X + 24f;
        if (lineWidth > 0f && lineWidth + width <= ImGui.GetContentRegionAvail().X)
            ImGui.SameLine();
        else if (lineWidth > 0f)
            lineWidth = 0f;

        var clicked = ActionButton(label, disabled);
        lineWidth += width;
        return clicked;
    }

    private static bool FlowCheckbox(string label, ref bool value, ref float lineWidth)
    {
        var width = ImGui.CalcTextSize(label).X + ImGui.GetFrameHeight() + 14f;
        if (lineWidth > 0f && lineWidth + width <= ImGui.GetContentRegionAvail().X)
            ImGui.SameLine();
        else if (lineWidth > 0f)
            lineWidth = 0f;

        var changed = ImGui.Checkbox(label, ref value);
        lineWidth += width;
        return changed;
    }

    private static void DrawFilterInput(string label, string id, ref string text)
    {
        ImGui.TextColored(MutedText, label);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputText(id, ref text, 64);
    }

    private static void DrawListCount(int visibleCount, int totalCount)
    {
        if (visibleCount == totalCount)
            ImGui.TextColored(MutedText, $"{totalCount} 项");
        else
            ImGui.TextColored(MutedText, $"{visibleCount} / {totalCount} 项");
    }

    private static void DrawPseudoHeader(string label, float width)
    {
        ImGui.TextColored(MutedText, FitTextToWidth(label, width));
    }

    private static void DrawPseudoHeaderCell(float rowStartX, float offsetX, float width, string label)
    {
        ImGui.SameLine(rowStartX + offsetX);
        ImGui.TextColored(MutedText, FitTextToWidth(label, width));
    }

    private static void DrawPseudoCell(float rowStartX, float offsetX, float width, string value)
    {
        DrawPseudoCell(rowStartX, offsetX, width, value, null);
    }

    private static void DrawPseudoCell(float rowStartX, float offsetX, float width, string value, Vector4? color)
    {
        ImGui.SameLine(rowStartX + offsetX);
        var text = FitTextToWidth(value, width);
        if (color is { } textColor)
            ImGui.TextColored(textColor, text);
        else
            ImGui.TextUnformatted(text);
    }

    private static void SameLineIfFits(string nextLabel)
    {
        var width = ImGui.CalcTextSize(nextLabel).X + 24f;
        if (width <= ImGui.GetContentRegionAvail().X)
            ImGui.SameLine();
    }

    private static string FitTextToWidth(string text, float width)
    {
        if (width <= 16f || ImGui.CalcTextSize(text).X <= width)
            return text;

        const string ellipsis = "...";
        var usableWidth = Math.Max(0f, width - ImGui.CalcTextSize(ellipsis).X);
        var length = text.Length;
        while (length > 0 && ImGui.CalcTextSize(text[..length]).X > usableWidth)
            length--;

        return length <= 0 ? ellipsis : text[..length] + ellipsis;
    }

    private static void DrawFloatInput(
        string label,
        float value,
        float minimum,
        float maximum,
        Action<float> setter,
        float step = 1f,
        float stepFast = 10f,
        string format = "%.1f")
    {
        ImGui.SetNextItemWidth(180f);
        if (!ImGui.InputFloat(label, ref value, step, stepFast, format))
            return;

        setter(Math.Clamp(value, minimum, maximum));
    }

    private static void DrawIntInput(
        string label,
        int value,
        int minimum,
        int maximum,
        Action<int> setter,
        int step = 10,
        int stepFast = 100)
    {
        ImGui.SetNextItemWidth(180f);
        if (!ImGui.InputInt(label, ref value, step, stepFast))
            return;

        setter(Math.Clamp(value, minimum, maximum));
    }
}

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

    private static readonly Vector4 MutedText = new(0.72f, 0.72f, 0.72f, 1f);
    private static readonly Vector4 GoodText = new(0.35f, 0.86f, 0.52f, 1f);
    private static readonly Vector4 WarnText = new(1f, 0.68f, 0.24f, 1f);
    private static readonly Vector4 ErrorText = new(1f, 0.35f, 0.32f, 1f);
    private static readonly Vector4 AccentText = new(0.42f, 0.72f, 1f, 1f);

    private readonly SpotWorkflowSession session;
    private string territoryFilterText = string.Empty;
    private string targetFilterText = string.Empty;
    private string targetIdText = string.Empty;

    public MainWindow(SpotWorkflowSession session)
        : base("FishingPointGenerator###FishingPointGeneratorMainWindow")
    {
        this.session = session;
    }

    public void Dispose() { }

    public override void PreDraw()
    {
        ImGui.SetNextWindowSize(new Vector2(1160f, 760f), ImGuiCond.FirstUseEver);
        base.PreDraw();
    }

    public override void Draw()
    {
        DrawHeader();
        ImGui.Separator();

        if (ImGui.BeginTable("##fpg_main_layout", 3, ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersInnerV))
        {
            ImGui.TableSetupColumn("Territories", ImGuiTableColumnFlags.WidthFixed, 280f);
            ImGui.TableSetupColumn("Spots", ImGuiTableColumnFlags.WidthFixed, 390f);
            ImGui.TableSetupColumn("Maintenance", ImGuiTableColumnFlags.WidthStretch);

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            DrawTerritoryDrawer();
            ImGui.TableNextColumn();
            DrawSpotList();
            ImGui.TableNextColumn();
            DrawSpotDetail();

            ImGui.EndTable();
        }

        ImGui.Separator();
        DrawDebugOptions();
        DrawPaths();
    }

    private void DrawHeader()
    {
        ImGui.TextColored(AccentText, "FishingPointGenerator");
        ImGui.SameLine();
        DrawStatusBadge();
        ImGui.SameLine();
        ImGui.TextColored(MutedText, $"游戏区域: {session.CurrentTerritoryId}");
        ImGui.SameLine();
        ImGui.TextColored(MutedText, $"已选领地: {FormatTerritoryTitle(session.SelectedTerritoryId, session.SelectedTerritoryName)}");

        ImGui.PushTextWrapPos();
        ImGui.TextColored(GetMessageColor(), session.LastMessage);
        ImGui.PopTextWrapPos();
    }

    private void DrawTerritoryDrawer()
    {
        DrawSectionTitle("领地");

        if (ActionButton("刷新目录"))
            session.RefreshCatalog();
        ImGui.SameLine();
        if (ActionButton("当前区域"))
            session.RefreshCurrentTerritory(selectNext: false);
        ImGui.SameLine();
        if (ActionButton("扫描当前"))
            session.ScanCurrentTerritory();

        ImGui.SetNextItemWidth(-1f);
        ImGui.InputText("##territory_filter", ref territoryFilterText, 64);

        if (!ImGui.BeginTable("##fpg_territory_list", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Borders, new Vector2(0f, 560f)))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("领地");
        ImGui.TableSetupColumn("钓场");
        ImGui.TableSetupColumn("确认");
        ImGui.TableSetupColumn("需维护");
        ImGui.TableSetupColumn("风险");
        ImGui.TableHeadersRow();

        foreach (var summary in GetVisibleTerritories())
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            var title = FormatTerritoryTitle(summary.TerritoryId, summary.TerritoryName);
            if (ImGui.Selectable($"{title}##territory_{summary.TerritoryId}", summary.IsSelected, ImGuiSelectableFlags.SpanAllColumns))
                session.SelectTerritory(summary.TerritoryId, selectNext: false);
            if (summary.IsCurrentTerritory)
            {
                ImGui.SameLine();
                ImGui.TextColored(AccentText, "当前");
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(summary.SpotCount.ToString());
            ImGui.TableNextColumn();
            ImGui.TextColored(summary.ConfirmedCount > 0 ? GoodText : MutedText, summary.ConfirmedCount.ToString());
            ImGui.TableNextColumn();
            ImGui.TextColored(summary.MaintenanceNeededCount > 0 ? WarnText : MutedText, summary.MaintenanceNeededCount.ToString());
            ImGui.TableNextColumn();
            var riskCount = summary.RiskCount + summary.WeakCoverageCount;
            ImGui.TextColored(riskCount > 0 ? ErrorText : MutedText, riskCount.ToString());
        }

        ImGui.EndTable();
    }

    private void DrawSpotList()
    {
        DrawSectionTitle("钓场");

        ImGui.SetNextItemWidth(110f);
        ImGui.InputText("RowId", ref targetIdText, 16);
        ImGui.SameLine();
        if (ActionButton("选择"))
        {
            if (uint.TryParse(targetIdText.Trim(), out var targetId))
                session.SelectTarget(targetId);
        }
        ImGui.SameLine();
        if (ActionButton("下一个", session.TargetCount == 0))
            session.SelectNextTarget();

        ImGui.SetNextItemWidth(-1f);
        ImGui.InputText("##spot_filter", ref targetFilterText, 64);

        DrawTerritoryOverview();

        if (session.CurrentTerritoryTargets.Count == 0)
        {
            ImGui.TextColored(MutedText, "未加载领地钓场。");
            return;
        }

        if (!ImGui.BeginTable("##fpg_spot_list", 7, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Borders, new Vector2(0f, 440f)))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("RowId");
        ImGui.TableSetupColumn("状态");
        ImGui.TableSetupColumn("名称");
        ImGui.TableSetupColumn("点");
        ImGui.TableSetupColumn("候选");
        ImGui.TableSetupColumn("地图");
        ImGui.TableSetupColumn("旧缓存");
        ImGui.TableHeadersRow();

        var ctrlDown = ImGui.GetIO().KeyCtrl;
        foreach (var target in GetVisibleTargets().Take(300))
        {
            var analysis = session.Analyses.FirstOrDefault(analysis => analysis.Key == target.Key);
            var selected = session.CurrentTarget?.Key == target.Key;

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            if (ImGui.Selectable($"{target.FishingSpotId}##spot_{target.FishingSpotId}", selected, ImGuiSelectableFlags.SpanAllColumns))
                session.SelectTarget(target.FishingSpotId);

            ImGui.TableNextColumn();
            ImGui.TextColored(GetStatusColor(analysis?.Status), FormatStatus(analysis?.Status));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(target.Name);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted((analysis?.ConfirmedApproachPointCount ?? 0).ToString());
            ImGui.TableNextColumn();
            ImGui.TextUnformatted((analysis?.CandidateCount ?? 0).ToString());
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{target.MapX:F1}, {target.MapY:F1}");
            ImGui.TableNextColumn();
            if (SmallActionButton($"清除##clear_spot_{target.FishingSpotId}", !ctrlDown))
                session.ClearSpotPointCache(target.FishingSpotId);
        }

        ImGui.EndTable();
    }

    private void DrawSpotDetail()
    {
        DrawSectionTitle("维护");

        var target = session.CurrentTarget;
        if (target is null)
        {
            ImGui.TextColored(MutedText, "未选择钓场。");
            return;
        }

        DrawSelectedSpotSummary(target);
        DrawApproachPoints();
        DrawCandidateSelection();
        DrawTargetActions(session.CurrentAnalysis);
    }

    private void DrawSelectedSpotSummary(FishingSpotTarget target)
    {
        var analysis = session.CurrentAnalysis;
        if (!ImGui.BeginTable("##fpg_spot_summary", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders))
            return;

        DrawSummaryRow("RowId", target.FishingSpotId.ToString());
        DrawSummaryRow("名称", target.Name);
        DrawSummaryRow("领地", FormatTerritoryTitle(target.TerritoryId, target.TerritoryName));
        DrawSummaryRow("状态", FormatStatus(analysis?.Status), GetStatusColor(analysis?.Status));
        DrawSummaryRow("复核", FormatReviewDecision(session.CurrentReviewDecision));
        DrawSummaryRow("真实点", session.CurrentApproachPoints.Count(point => point.Status == ApproachPointStatus.Confirmed).ToString());
        DrawSummaryRow("候选点", (analysis?.CandidateCount ?? 0).ToString());
        DrawSummaryRow("鱼类物品", target.ItemIds.Count == 0 ? "-" : string.Join(", ", target.ItemIds));
        DrawSummaryRow("半径", target.Radius.ToString("F1"));

        ImGui.EndTable();
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

        if (!ImGui.BeginTable("##fpg_approach_points", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY, new Vector2(0f, 150f)))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("状态");
        ImGui.TableSetupColumn("点位");
        ImGui.TableSetupColumn("朝向");
        ImGui.TableSetupColumn("来源");
        ImGui.TableSetupColumn("证据");
        ImGui.TableHeadersRow();

        foreach (var point in session.CurrentApproachPoints.OrderBy(point => point.PointId, StringComparer.Ordinal))
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextColored(point.Status == ApproachPointStatus.Confirmed ? GoodText : MutedText, FormatApproachPointStatus(point.Status));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(FormatPoint(point.Position));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(point.Rotation.ToString("F3"));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(FormatSource(point.SourceKind));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(point.EvidenceIds.Count.ToString());
        }

        ImGui.EndTable();
    }

    private void DrawCandidateSelection()
    {
        var selection = session.CurrentCandidateSelection;
        if (selection is null)
            return;

        var candidate = selection.Candidate;
        ImGui.Spacing();
        ImGui.TextColored(AccentText, "当前候选");
        if (!ImGui.BeginTable("##fpg_candidate_selection", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders))
            return;

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

        ImGui.EndTable();
    }

    private void DrawTargetActions(SpotAnalysis? analysis)
    {
        var hasTarget = session.CurrentTarget is not null;
        var hasCandidateSelection = session.CurrentCandidateSelection?.Candidate is not null;
        var actionableCandidateSelection = session.CurrentCandidateSelectionIsActionable;
        var sameTerritory = session.SelectedTerritoryIsCurrent;

        ImGui.Spacing();
        if (ActionButton("派生候选", !hasTarget || session.TerritoryCandidateCount == 0))
            session.ScanCurrentTarget();

        ImGui.SameLine();
        if (ActionButton("刷新候选", !hasTarget || !sameTerritory || session.CurrentScan is null))
            session.RefreshCandidateSelection();

        ImGui.SameLine();
        if (ActionButton("插旗钓场", !hasTarget || !sameTerritory))
            session.PlaceCurrentTargetFlag();

        ImGui.SameLine();
        if (ActionButton("插旗候选", !actionableCandidateSelection || !sameTerritory))
            session.PlaceSelectedCandidateFlag();

        ImGui.SameLine();
        if (ActionButton("插旗未记录", !hasTarget || !sameTerritory || session.CurrentScan is null))
            session.PlaceNearestUnrecordedCandidateFlag();

        if (ActionButton("确认当前站位", !hasTarget || !sameTerritory))
            session.ConfirmCurrentStanding();

        ImGui.SameLine();
        if (ActionButton("排除此候选", !hasCandidateSelection || !sameTerritory))
            session.RejectSelectedCandidate();

        ImGui.SameLine();
        if (ActionButton("允许弱覆盖导出", analysis?.Status != SpotAnalysisStatus.WeakCoverage))
            session.AllowWeakCoverageExport();

        ImGui.SameLine();
        if (ActionButton("允许风险导出", analysis?.Status != SpotAnalysisStatus.MixedRisk))
            session.AllowRiskExport();

        ImGui.SameLine();
        if (ActionButton("忽略钓场", !hasTarget))
            session.IgnoreCurrentTarget();

        if (ActionButton("生成报告", !hasTarget))
            session.GenerateCurrentReport();

        ImGui.SameLine();
        if (ActionButton("导出已确认"))
            session.ExportConfirmed();
    }

    private void DrawTerritoryOverview()
    {
        if (!ImGui.BeginTable("##fpg_territory_overview", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders))
            return;

        ImGui.TableNextRow();
        DrawCompactSummary("钓场", session.TargetCount.ToString(), MutedText);
        DrawCompactSummary("确认", session.ConfirmedCount.ToString(), session.ConfirmedCount > 0 ? GoodText : MutedText);
        DrawCompactSummary("需维护", session.MaintenanceNeededCount.ToString(), session.MaintenanceNeededCount > 0 ? WarnText : MutedText);
        DrawCompactSummary("风险", (session.MixedRiskCount + session.WeakCoverageCount).ToString(),
            session.MixedRiskCount + session.WeakCoverageCount > 0 ? ErrorText : MutedText);

        ImGui.EndTable();
    }

    private void DrawDebugOptions()
    {
        if (!ImGui.CollapsingHeader("调试与显示"))
            return;

        var autoRecord = session.AutoRecordCastsEnabled;
        if (ImGui.Checkbox("自动记录抛竿", ref autoRecord))
            session.AutoRecordCastsEnabled = autoRecord;

        ImGui.SameLine();
        var overlayEnabled = session.OverlayEnabled;
        if (ImGui.Checkbox("显示 overlay", ref overlayEnabled))
            session.OverlayEnabled = overlayEnabled;

        ImGui.SameLine();
        var showCandidates = session.OverlayShowCandidates;
        if (ImGui.Checkbox("显示候选点", ref showCandidates))
            session.OverlayShowCandidates = showCandidates;

        ImGui.SameLine();
        var showTerritoryCache = session.OverlayShowTerritoryCache;
        if (ImGui.Checkbox("显示全图缓存", ref showTerritoryCache))
            session.OverlayShowTerritoryCache = showTerritoryCache;

        var showRadius = session.OverlayShowTargetRadius;
        if (ImGui.Checkbox("显示钓场半径", ref showRadius))
            session.OverlayShowTargetRadius = showRadius;

        ImGui.SameLine();
        var showFishableDebug = session.OverlayShowFishableDebug;
        if (ImGui.Checkbox("显示 Fishable 水面", ref showFishableDebug))
            session.OverlayShowFishableDebug = showFishableDebug;

        ImGui.SameLine();
        var showWalkableDebug = session.OverlayShowWalkableDebug;
        if (ImGui.Checkbox("显示 Walkable 可走面", ref showWalkableDebug))
            session.OverlayShowWalkableDebug = showWalkableDebug;

        DrawFloatInput("抛竿块选择距离(m)", session.CastBlockSnapDistanceMeters, MinimumCastBlockSnapDistance, MaximumCastBlockSnapDistance, value => session.CastBlockSnapDistanceMeters = value);
        ImGui.SameLine();
        DrawFloatInput("一次点亮块内范围(m)", session.CastBlockFillRangeMeters, MinimumCastBlockFillRange, MaximumCastBlockFillRange, value => session.CastBlockFillRangeMeters = value);

        DrawFloatInput("overlay 距离(m)", session.OverlayMaxDistanceMeters, MinimumOverlayDistance, MaximumOverlayDistance, value => session.OverlayMaxDistanceMeters = value, 10f, 50f, "%.0f");
        ImGui.SameLine();
        DrawIntInput("overlay 点数上限", session.OverlayCandidateLimit, MinimumOverlayCandidateLimit, MaximumOverlayCandidateLimit, value => session.OverlayCandidateLimit = value);
    }

    private void DrawPaths()
    {
        if (!ImGui.CollapsingHeader("文件"))
            return;

        if (!ImGui.BeginTable("##fpg_paths", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders))
            return;

        DrawSummaryRow("数据", session.DataRoot);
        DrawSummaryRow("目录", session.CatalogPath);
        DrawSummaryRow("维护", string.IsNullOrWhiteSpace(session.MaintenancePath) ? "-" : session.MaintenancePath);
        DrawSummaryRow("全图缓存", session.GeneratedSurveyPath);
        DrawSummaryRow("导出", session.ExportPath);

        ImGui.EndTable();
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
        if (IsErrorMessage(session.LastMessage))
            return ("错误", ErrorText);
        if (session.TargetCount == 0)
            return ("无目录", MutedText);
        if (session.CurrentTarget is null)
            return ("未选钓场", WarnText);
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
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextColored(MutedText, label);
        ImGui.TableNextColumn();
        if (valueColor is { } color)
            ImGui.TextColored(color, value);
        else
            ImGui.TextUnformatted(value);
    }

    private static void DrawCompactSummary(string label, string value, Vector4 color)
    {
        ImGui.TableNextColumn();
        ImGui.TextColored(MutedText, label);
        ImGui.SameLine();
        ImGui.TextColored(color, value);
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

    private static bool SmallActionButton(string label, bool disabled = false)
    {
        ImGui.BeginDisabled(disabled);
        var clicked = ImGui.SmallButton(label);
        ImGui.EndDisabled();
        return clicked && !disabled;
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

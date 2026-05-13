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
    private string targetIdText = string.Empty;
    private string targetFilterText = string.Empty;

    public MainWindow(SpotWorkflowSession session)
        : base("FishingPointGenerator###FishingPointGeneratorMainWindow")
    {
        this.session = session;
    }

    public void Dispose() { }

    public override void PreDraw()
    {
        ImGui.SetNextWindowSize(new Vector2(860f, 720f), ImGuiCond.FirstUseEver);
        base.PreDraw();
    }

    public override void Draw()
    {
        DrawHeader();
        DrawSectionSeparator();
        DrawTerritoryWorkflow();
        DrawSectionSeparator();
        DrawTargetSelector();
        ImGui.Spacing();
        DrawTargetList();
        DrawSectionSeparator();
        DrawTargetDetails();
        DrawSectionSeparator();
        DrawDebugOptions();
        DrawSectionSeparator();
        DrawPaths();
    }

    private void DrawHeader()
    {
        ImGui.TextColored(AccentText, "FishingPointGenerator");
        ImGui.SameLine();
        DrawStatusBadge();

        DrawKeyValueInline("当前区域", session.CurrentTerritoryId.ToString());
        DrawKeyValueInline("扫描器", session.ScannerName);

        ImGui.PushTextWrapPos();
        ImGui.TextColored(GetMessageColor(), session.LastMessage);
        ImGui.PopTextWrapPos();
    }

    private void DrawTerritoryWorkflow()
    {
        DrawSectionTitle("区域准备");

        if (ActionButton("刷新目录"))
            session.RefreshCatalog();

        ImGui.SameLine();
        if (ActionButton("刷新当前区域"))
            session.RefreshCurrentTerritory(selectNext: false);

        ImGui.SameLine();
        if (ActionButton("扫描全图"))
            session.ScanCurrentTerritory();

        ImGui.SameLine();
        if (ActionButton("生成已选缓存", session.CurrentTarget is null || session.TerritoryCandidateCount == 0))
            session.ScanCurrentTarget();

        ImGui.Spacing();
        DrawTerritoryOverview();
    }

    private void DrawTargetSelector()
    {
        DrawSectionTitle("目标");

        ImGui.SetNextItemWidth(150f);
        ImGui.InputText("指定 FishingSpot.RowId", ref targetIdText, 16);
        ImGui.SameLine();
        if (ActionButton("选择"))
        {
            if (uint.TryParse(targetIdText.Trim(), out var targetId))
                session.SelectTarget(targetId);
        }

        ImGui.SameLine();
        if (ActionButton("下一个目标", session.TargetCount == 0))
            session.SelectNextTarget();

        ImGui.SetNextItemWidth(240f);
        ImGui.InputText("过滤当前 Territory 目标", ref targetFilterText, 64);
        ImGui.SameLine();
        if (ImGui.SmallButton("清除过滤"))
            targetFilterText = string.Empty;

        var current = session.CurrentTarget is { } currentTarget
            ? $"{currentTarget.FishingSpotId} {currentTarget.Name}"
            : "未选择";
        DrawKeyValueInline("当前目标", current);
    }

    private void DrawDebugOptions()
    {
        if (!ImGui.CollapsingHeader("调试与显示", ImGuiTreeNodeFlags.DefaultOpen))
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

        DrawFloatInput(
            "抛竿块选择距离(m)",
            session.CastBlockSnapDistanceMeters,
            MinimumCastBlockSnapDistance,
            MaximumCastBlockSnapDistance,
            value => session.CastBlockSnapDistanceMeters = value);

        ImGui.SameLine();
        DrawFloatInput(
            "一次点亮块内范围(m)",
            session.CastBlockFillRangeMeters,
            MinimumCastBlockFillRange,
            MaximumCastBlockFillRange,
            value => session.CastBlockFillRangeMeters = value);

        DrawFloatInput(
            "overlay 距离(m)",
            session.OverlayMaxDistanceMeters,
            MinimumOverlayDistance,
            MaximumOverlayDistance,
            value => session.OverlayMaxDistanceMeters = value,
            step: 10f,
            stepFast: 50f,
            format: "%.0f");

        ImGui.SameLine();
        DrawIntInput(
            "overlay 点数上限",
            session.OverlayCandidateLimit,
            MinimumOverlayCandidateLimit,
            MaximumOverlayCandidateLimit,
            value => session.OverlayCandidateLimit = value);

        if (session.LastCastPlaceNameId != 0)
        {
            var resolved = session.LastCastFishingSpotId != 0
                ? session.LastCastFishingSpotId.ToString()
                : "-";
            ImGui.TextColored(MutedText, $"最后抛竿 PlaceName: {session.LastCastPlaceNameId}，FishingSpot: {resolved}，新增: {session.LastCastRecordedCount}");
        }

        if (session.NearbyDebugOverlay is { } debug)
        {
            ImGui.TextColored(
                MutedText,
                $"附近调试面：Fishable {debug.FishableTriangles.Count}，Walkable {debug.WalkableTriangles.Count}，半径 {debug.RadiusMeters:F1}m，区域 {debug.TerritoryId}");
            ImGui.SameLine();
            if (SmallActionButton("清除调试面"))
                session.ClearNearbyDebugOverlay();
        }
    }

    private void DrawTerritoryOverview()
    {
        if (!ImGui.BeginTable("##fpg_territory_overview", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
            return;

        DrawSummaryRow("目标数", session.TargetCount.ToString());
        DrawSummaryRow("全图缓存点", session.TerritoryCandidateCount.ToString(), session.TerritoryCandidateCount > 0 ? GoodText : WarnText);
        DrawSummaryRow("全图缓存块", session.TerritoryBlockCount.ToString(), session.TerritoryBlockCount > 0 ? GoodText : WarnText);
        DrawSummaryRow("已确认", session.ConfirmedCount.ToString(), session.ConfirmedCount > 0 ? GoodText : MutedText);
        DrawSummaryRow("需到访", session.NeedsVisitCount.ToString(), session.NeedsVisitCount > 0 ? WarnText : MutedText);
        DrawSummaryRow("弱覆盖", session.WeakCoverageCount.ToString(), session.WeakCoverageCount > 0 ? WarnText : MutedText);
        DrawSummaryRow("无候选点", session.NoCandidateCount.ToString(), session.NoCandidateCount > 0 ? ErrorText : MutedText);
        DrawSummaryRow("混合风险", session.MixedRiskCount.ToString(), session.MixedRiskCount > 0 ? WarnText : MutedText);
        DrawSummaryRow("孤立标记", session.OrphanedLabelCount.ToString(), session.OrphanedLabelCount > 0 ? ErrorText : MutedText);
        DrawSummaryRow("已忽略", session.IgnoredCount.ToString(), session.IgnoredCount > 0 ? MutedText : MutedText);

        ImGui.EndTable();
    }

    private void DrawTargetDetails()
    {
        DrawSectionTitle("已选目标维护");

        var target = session.CurrentTarget;
        if (target is null)
        {
            ImGui.TextColored(MutedText, "未选择 FishingSpot 目标。");
            return;
        }

        var analysis = session.CurrentAnalysis;
        if (!ImGui.BeginTable("##fpg_target_detail", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
            return;

        DrawSummaryRow("RowId", target.FishingSpotId.ToString());
        DrawSummaryRow("PlaceName", target.PlaceNameId.ToString());
        DrawSummaryRow("名称", target.Name);
        DrawSummaryRow("状态", FormatStatus(analysis?.Status), GetStatusColor(analysis?.Status));
        DrawSummaryRow("地图坐标", $"{target.MapX:F2}, {target.MapY:F2}");
        DrawSummaryRow("世界坐标", $"{target.WorldX:F2}, {target.WorldZ:F2}");
        DrawSummaryRow("半径", target.Radius.ToString("F1"));
        DrawSummaryRow("鱼类物品", target.ItemIds.Count == 0 ? "-" : string.Join(", ", target.ItemIds));
        DrawSummaryRow("候选点", (analysis?.CandidateCount ?? 0).ToString());
        DrawSummaryRow("已确认标记", (analysis?.ConfirmedLabelCount ?? 0).ToString());
        DrawSummaryRow("当前 scan 点数", (session.CurrentScan?.Candidates.Count ?? 0).ToString());
        DrawSummaryRow("当前块数", session.CurrentTargetBlocks.Count.ToString());

        var candidate = analysis?.RecommendedCandidate;
        if (candidate is not null)
        {
            DrawSummaryRow("推荐原因", FormatRecommendationReason(analysis?.RecommendationReason));
            DrawSummaryRow("点位", FormatPoint(candidate.Position));
            DrawSummaryRow("朝向", candidate.Rotation.ToString("F3"));
            DrawSummaryRow("Fingerprint", candidate.CandidateFingerprint);
        }

        ImGui.EndTable();
        ImGui.Spacing();
        DrawTargetActions(analysis);
    }

    private void DrawTargetActions(SpotAnalysis? analysis)
    {
        var hasTarget = session.CurrentTarget is not null;
        var hasRecommendation = analysis?.RecommendedCandidate is not null;

        if (ActionButton("插旗钓场", !hasTarget))
            session.PlaceCurrentTargetFlag();

        ImGui.SameLine();
        if (ActionButton("插旗点位", !hasRecommendation))
            session.PlaceRecommendedStandingFlag();

        ImGui.SameLine();
        if (ActionButton("确认推荐", !hasRecommendation))
            session.ConfirmRecommendation();

        ImGui.SameLine();
        if (ActionButton("记录不匹配", !hasRecommendation))
            session.RecordMismatch();

        ImGui.Spacing();
        if (ActionButton("允许弱覆盖导出", analysis?.Status != SpotAnalysisStatus.WeakCoverage))
            session.AllowWeakCoverageExport();

        ImGui.SameLine();
        if (ActionButton("忽略目标", !hasTarget))
            session.IgnoreCurrentTarget();

        ImGui.SameLine();
        if (ActionButton("生成报告", !hasTarget))
            session.GenerateCurrentReport();

        ImGui.SameLine();
        if (ActionButton("导出已确认"))
            session.ExportConfirmed();
    }

    private void DrawTargetList()
    {
        DrawSectionTitle("当前 Territory 钓场目标");

        if (session.CurrentTerritoryTargets.Count == 0)
        {
            ImGui.TextColored(MutedText, "当前区域未加载目录目标。");
            return;
        }

        if (!ImGui.BeginTable("##fpg_spot_list", 8, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(0f, 300f)))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("RowId");
        ImGui.TableSetupColumn("状态");
        ImGui.TableSetupColumn("名称");
        ImGui.TableSetupColumn("地图坐标");
        ImGui.TableSetupColumn("候选点");
        ImGui.TableSetupColumn("标记");
        ImGui.TableSetupColumn("块");
        ImGui.TableSetupColumn("Ctrl清除");
        ImGui.TableHeadersRow();

        var visibleTargets = GetVisibleTargets().Take(200).ToList();
        var ctrlDown = ImGui.GetIO().KeyCtrl;
        foreach (var target in visibleTargets)
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
            ImGui.TextUnformatted($"{target.MapX:F1}, {target.MapY:F1}");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted((analysis?.CandidateCount ?? 0).ToString());
            ImGui.TableNextColumn();
            ImGui.TextUnformatted((analysis?.ConfirmedLabelCount ?? 0).ToString());
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(selected ? session.CurrentTargetBlocks.Count.ToString() : "-");
            ImGui.TableNextColumn();
            if (SmallActionButton($"清除##clear_spot_{target.FishingSpotId}", !ctrlDown))
                session.ClearSpotPointCache(target.FishingSpotId);
        }

        ImGui.EndTable();

        var visibleCount = GetVisibleTargets().Count();
        if (visibleCount > visibleTargets.Count)
            ImGui.TextColored(MutedText, $"仅显示前 {visibleTargets.Count} / {visibleCount} 个目标。");
    }

    private void DrawPaths()
    {
        if (!ImGui.CollapsingHeader("文件"))
            return;

        if (!ImGui.BeginTable("##fpg_paths", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
            return;

        DrawSummaryRow("数据", session.DataRoot);
        DrawSummaryRow("目录", session.CatalogPath);
        DrawSummaryRow("全图缓存", session.GeneratedSurveyPath);
        DrawSummaryRow("导出", session.ExportPath);

        ImGui.EndTable();
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

        if (session.TerritoryCandidateCount == 0)
            return ("需扫描", WarnText);

        if (session.CurrentTarget is null)
            return ("未选目标", WarnText);

        if (session.CurrentAnalysis?.RecommendedCandidate is not null)
            return ("维护中", GoodText);

        return ("已加载", GoodText);
    }

    private Vector4 GetMessageColor()
    {
        if (IsErrorMessage(session.LastMessage))
            return ErrorText;

        if (session.LastMessage.Contains("empty", StringComparison.OrdinalIgnoreCase)
            || session.LastMessage.Contains("No ", StringComparison.Ordinal))
            return WarnText;
        if (session.LastMessage.Contains("为空", StringComparison.Ordinal)
            || session.LastMessage.Contains("没有", StringComparison.Ordinal)
            || session.LastMessage.Contains("未", StringComparison.Ordinal)
            || session.LastMessage.Contains("无", StringComparison.Ordinal)
            || session.LastMessage.Contains("不在", StringComparison.Ordinal)
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
            SpotAnalysisStatus.MixedRisk => WarnText,
            SpotAnalysisStatus.NoCandidate => ErrorText,
            SpotAnalysisStatus.OrphanedLabels => ErrorText,
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
            SpotAnalysisStatus.OrphanedLabels => "孤立标记",
            _ => "未开始",
        };
    }

    private static string FormatRecommendationReason(SpotRecommendationReason? reason)
    {
        return reason switch
        {
            SpotRecommendationReason.NeedsVisit => "需到访",
            SpotRecommendationReason.WeakCoverage => "弱覆盖",
            SpotRecommendationReason.OrphanedLabelReview => "检查孤立标记",
            SpotRecommendationReason.MixedRiskReview => "检查混合风险",
            _ => "-",
        };
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

    private static void DrawSectionTitle(string title)
    {
        ImGui.TextColored(AccentText, title);
    }

    private static void DrawSectionSeparator()
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
    }

    private static void DrawKeyValueInline(string label, string value)
    {
        ImGui.TextColored(MutedText, $"{label}:");
        ImGui.SameLine();
        ImGui.TextUnformatted(value);
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

    private IEnumerable<FishingSpotTarget> GetVisibleTargets()
    {
        var filter = targetFilterText.Trim();
        if (string.IsNullOrWhiteSpace(filter))
            return session.CurrentTerritoryTargets;

        return session.CurrentTerritoryTargets.Where(target =>
            target.FishingSpotId.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase)
            || target.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));
    }

    private static string FormatPoint(Point3 point)
    {
        return $"{point.X:F2}, {point.Y:F2}, {point.Z:F2}";
    }
}

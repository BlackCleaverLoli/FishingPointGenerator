using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using FishingPointGenerator.Core.Models;
using FishingPointGenerator.Plugin.Services;

namespace FishingPointGenerator.Plugin.Windows;

internal sealed class MainWindow : Window, IDisposable
{
    private static readonly Vector4 MutedText = new(0.72f, 0.72f, 0.72f, 1f);
    private static readonly Vector4 GoodText = new(0.35f, 0.86f, 0.52f, 1f);
    private static readonly Vector4 WarnText = new(1f, 0.68f, 0.24f, 1f);
    private static readonly Vector4 ErrorText = new(1f, 0.35f, 0.32f, 1f);
    private static readonly Vector4 AccentText = new(0.42f, 0.72f, 1f, 1f);

    private readonly SpotWorkflowSession session;
    private string targetIdText = string.Empty;

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
        DrawActions();
        DrawSectionSeparator();
        DrawTerritoryOverview();
        DrawSectionSeparator();
        DrawTargetDetails();
        DrawSectionSeparator();
        DrawTargetList();
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

    private void DrawActions()
    {
        DrawSectionTitle("操作");

        if (ImGui.Button("刷新目录"))
            session.RefreshCatalog();

        ImGui.SameLine();
        if (ImGui.Button("刷新当前区域"))
            session.RefreshCurrentTerritory(selectNext: false);

        ImGui.SameLine();
        if (ImGui.Button("下一个目标"))
            session.SelectNextTarget();

        ImGui.SameLine();
        if (ImGui.Button("扫描目标"))
            session.ScanCurrentTarget();

        if (ImGui.Button("确认推荐"))
            session.ConfirmRecommendation();

        ImGui.SameLine();
        if (ImGui.Button("记录不匹配"))
            session.RecordMismatch();

        ImGui.SameLine();
        if (ImGui.Button("允许弱覆盖导出"))
            session.AllowWeakCoverageExport();

        ImGui.SameLine();
        if (ImGui.Button("忽略目标"))
            session.IgnoreCurrentTarget();

        ImGui.SameLine();
        if (ImGui.Button("生成报告"))
            session.GenerateCurrentReport();

        ImGui.SameLine();
        if (ImGui.Button("导出已确认"))
            session.ExportConfirmed();

        ImGui.Spacing();
        ImGui.SetNextItemWidth(150f);
        ImGui.InputText("指定 FishingSpot.RowId", ref targetIdText, 16);
        ImGui.SameLine();
        if (ImGui.Button("选择"))
        {
            if (uint.TryParse(targetIdText.Trim(), out var targetId))
                session.SelectTarget(targetId);
        }
    }

    private void DrawTerritoryOverview()
    {
        DrawSectionTitle("当前区域");

        if (!ImGui.BeginTable("##fpg_territory_overview", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
            return;

        DrawSummaryRow("目标数", session.TargetCount.ToString());
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
        DrawSectionTitle("已选目标");

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
        DrawSummaryRow("名称", target.Name);
        DrawSummaryRow("状态", FormatStatus(analysis?.Status), GetStatusColor(analysis?.Status));
        DrawSummaryRow("地图坐标", $"{target.MapX:F2}, {target.MapY:F2}");
        DrawSummaryRow("世界坐标", $"{target.WorldX:F2}, {target.WorldZ:F2}");
        DrawSummaryRow("半径", target.Radius.ToString("F1"));
        DrawSummaryRow("鱼类物品", target.ItemIds.Count == 0 ? "-" : string.Join(", ", target.ItemIds));
        DrawSummaryRow("候选点", (analysis?.CandidateCount ?? 0).ToString());
        DrawSummaryRow("已确认标记", (analysis?.ConfirmedLabelCount ?? 0).ToString());

        var candidate = analysis?.RecommendedCandidate;
        if (candidate is not null)
        {
            DrawSummaryRow("推荐原因", FormatRecommendationReason(analysis?.RecommendationReason));
            DrawSummaryRow("站位", FormatPoint(candidate.Position));
            DrawSummaryRow("朝向", candidate.Rotation.ToString("F3"));
            DrawSummaryRow("目标点", FormatPoint(candidate.TargetPoint));
            DrawSummaryRow("Fingerprint", candidate.CandidateFingerprint);
        }

        ImGui.EndTable();
    }

    private void DrawTargetList()
    {
        DrawSectionTitle("FishingSpot 列表");

        if (session.CurrentTerritoryTargets.Count == 0)
        {
            ImGui.TextColored(MutedText, "当前区域未加载目录目标。");
            return;
        }

        if (!ImGui.BeginTable("##fpg_spot_list", 7, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY))
            return;

        ImGui.TableSetupColumn("选择");
        ImGui.TableSetupColumn("状态");
        ImGui.TableSetupColumn("RowId");
        ImGui.TableSetupColumn("名称");
        ImGui.TableSetupColumn("地图坐标");
        ImGui.TableSetupColumn("候选点");
        ImGui.TableSetupColumn("标记");
        ImGui.TableHeadersRow();

        foreach (var target in session.CurrentTerritoryTargets.Take(120))
        {
            var analysis = session.Analyses.FirstOrDefault(analysis => analysis.Key == target.Key);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            if (ImGui.SmallButton($"选择##spot_{target.FishingSpotId}"))
                session.SelectTarget(target.FishingSpotId);

            ImGui.TableNextColumn();
            ImGui.TextColored(GetStatusColor(analysis?.Status), FormatStatus(analysis?.Status));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(target.FishingSpotId.ToString());
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(target.Name);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{target.MapX:F1}, {target.MapY:F1}");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted((analysis?.CandidateCount ?? 0).ToString());
            ImGui.TableNextColumn();
            ImGui.TextUnformatted((analysis?.ConfirmedLabelCount ?? 0).ToString());
        }

        ImGui.EndTable();

        if (session.CurrentTerritoryTargets.Count > 120)
            ImGui.TextColored(MutedText, $"仅显示前 120 / {session.CurrentTerritoryTargets.Count} 个目标。");
    }

    private void DrawPaths()
    {
        DrawSectionTitle("文件");

        if (!ImGui.BeginTable("##fpg_paths", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
            return;

        DrawSummaryRow("数据", session.DataRoot);
        DrawSummaryRow("目录", session.CatalogPath);
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

        if (session.CurrentAnalysis?.RecommendedCandidate is not null)
            return ("就绪", GoodText);

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

    private static string FormatPoint(Point3 point)
    {
        return $"{point.X:F2}, {point.Y:F2}, {point.Z:F2}";
    }
}

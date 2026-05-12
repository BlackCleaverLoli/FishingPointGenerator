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

        DrawKeyValueInline("Territory", session.CurrentTerritoryId.ToString());
        DrawKeyValueInline("Scanner", session.ScannerName);

        ImGui.PushTextWrapPos();
        ImGui.TextColored(GetMessageColor(), session.LastMessage);
        ImGui.PopTextWrapPos();
    }

    private void DrawActions()
    {
        DrawSectionTitle("Actions");

        if (ImGui.Button("Refresh catalog"))
            session.RefreshCatalog();

        ImGui.SameLine();
        if (ImGui.Button("Refresh territory"))
            session.RefreshCurrentTerritory(selectNext: false);

        ImGui.SameLine();
        if (ImGui.Button("Next target"))
            session.SelectNextTarget();

        ImGui.SameLine();
        if (ImGui.Button("Scan target"))
            session.ScanCurrentTarget();

        if (ImGui.Button("Confirm recommendation"))
            session.ConfirmRecommendation();

        ImGui.SameLine();
        if (ImGui.Button("Record mismatch"))
            session.RecordMismatch();

        ImGui.SameLine();
        if (ImGui.Button("Allow weak export"))
            session.AllowWeakCoverageExport();

        ImGui.SameLine();
        if (ImGui.Button("Ignore target"))
            session.IgnoreCurrentTarget();

        ImGui.SameLine();
        if (ImGui.Button("Generate report"))
            session.GenerateCurrentReport();

        ImGui.SameLine();
        if (ImGui.Button("Export confirmed"))
            session.ExportConfirmed();

        ImGui.Spacing();
        ImGui.SetNextItemWidth(150f);
        ImGui.InputText("FishingSpot.RowId override", ref targetIdText, 16);
        ImGui.SameLine();
        if (ImGui.Button("Select"))
        {
            if (uint.TryParse(targetIdText.Trim(), out var targetId))
                session.SelectTarget(targetId);
        }
    }

    private void DrawTerritoryOverview()
    {
        DrawSectionTitle("Current Territory");

        if (!ImGui.BeginTable("##fpg_territory_overview", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
            return;

        DrawSummaryRow("Targets", session.TargetCount.ToString());
        DrawSummaryRow("Confirmed", session.ConfirmedCount.ToString(), session.ConfirmedCount > 0 ? GoodText : MutedText);
        DrawSummaryRow("Needs visit", session.NeedsVisitCount.ToString(), session.NeedsVisitCount > 0 ? WarnText : MutedText);
        DrawSummaryRow("Weak coverage", session.WeakCoverageCount.ToString(), session.WeakCoverageCount > 0 ? WarnText : MutedText);
        DrawSummaryRow("No candidate", session.NoCandidateCount.ToString(), session.NoCandidateCount > 0 ? ErrorText : MutedText);
        DrawSummaryRow("Mixed risk", session.MixedRiskCount.ToString(), session.MixedRiskCount > 0 ? WarnText : MutedText);
        DrawSummaryRow("Orphaned labels", session.OrphanedLabelCount.ToString(), session.OrphanedLabelCount > 0 ? ErrorText : MutedText);
        DrawSummaryRow("Ignored", session.IgnoredCount.ToString(), session.IgnoredCount > 0 ? MutedText : MutedText);

        ImGui.EndTable();
    }

    private void DrawTargetDetails()
    {
        DrawSectionTitle("Selected Target");

        var target = session.CurrentTarget;
        if (target is null)
        {
            ImGui.TextColored(MutedText, "No FishingSpot target is selected.");
            return;
        }

        var analysis = session.CurrentAnalysis;
        if (!ImGui.BeginTable("##fpg_target_detail", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
            return;

        DrawSummaryRow("RowId", target.FishingSpotId.ToString());
        DrawSummaryRow("Name", target.Name);
        DrawSummaryRow("Status", analysis?.Status.ToString() ?? "NotStarted", GetStatusColor(analysis?.Status));
        DrawSummaryRow("Map", $"{target.MapX:F2}, {target.MapY:F2}");
        DrawSummaryRow("World", $"{target.WorldX:F2}, {target.WorldZ:F2}");
        DrawSummaryRow("Radius", target.Radius.ToString("F1"));
        DrawSummaryRow("Fish items", target.ItemIds.Count == 0 ? "-" : string.Join(", ", target.ItemIds));
        DrawSummaryRow("Candidates", (analysis?.CandidateCount ?? 0).ToString());
        DrawSummaryRow("Confirmed labels", (analysis?.ConfirmedLabelCount ?? 0).ToString());

        var candidate = analysis?.RecommendedCandidate;
        if (candidate is not null)
        {
            DrawSummaryRow("Recommendation", analysis?.RecommendationReason?.ToString() ?? "-");
            DrawSummaryRow("Standing", FormatPoint(candidate.Position));
            DrawSummaryRow("Rotation", candidate.Rotation.ToString("F3"));
            DrawSummaryRow("Target point", FormatPoint(candidate.TargetPoint));
            DrawSummaryRow("Fingerprint", candidate.CandidateFingerprint);
        }

        ImGui.EndTable();
    }

    private void DrawTargetList()
    {
        DrawSectionTitle("Fishing Spots");

        if (session.CurrentTerritoryTargets.Count == 0)
        {
            ImGui.TextColored(MutedText, "No catalog targets are loaded for this territory.");
            return;
        }

        if (!ImGui.BeginTable("##fpg_spot_list", 7, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY))
            return;

        ImGui.TableSetupColumn("Pick");
        ImGui.TableSetupColumn("Status");
        ImGui.TableSetupColumn("RowId");
        ImGui.TableSetupColumn("Name");
        ImGui.TableSetupColumn("Map");
        ImGui.TableSetupColumn("Candidates");
        ImGui.TableSetupColumn("Labels");
        ImGui.TableHeadersRow();

        foreach (var target in session.CurrentTerritoryTargets.Take(120))
        {
            var analysis = session.Analyses.FirstOrDefault(analysis => analysis.Key == target.Key);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            if (ImGui.SmallButton($"Select##spot_{target.FishingSpotId}"))
                session.SelectTarget(target.FishingSpotId);

            ImGui.TableNextColumn();
            ImGui.TextColored(GetStatusColor(analysis?.Status), analysis?.Status.ToString() ?? "NotStarted");
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
            ImGui.TextColored(MutedText, $"Showing first 120 of {session.CurrentTerritoryTargets.Count} targets.");
    }

    private void DrawPaths()
    {
        DrawSectionTitle("Files");

        if (!ImGui.BeginTable("##fpg_paths", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
            return;

        DrawSummaryRow("Data", session.DataRoot);
        DrawSummaryRow("Catalog", session.CatalogPath);
        DrawSummaryRow("Export", session.ExportPath);

        ImGui.EndTable();
    }

    private void DrawStatusBadge()
    {
        var (text, color) = GetStatus();
        ImGui.TextColored(color, text);
    }

    private (string Text, Vector4 Color) GetStatus()
    {
        if (session.LastMessage.Contains("failed", StringComparison.OrdinalIgnoreCase))
            return ("Error", ErrorText);

        if (session.TargetCount == 0)
            return ("No catalog", MutedText);

        if (session.CurrentAnalysis?.RecommendedCandidate is not null)
            return ("Ready", GoodText);

        return ("Loaded", GoodText);
    }

    private Vector4 GetMessageColor()
    {
        if (session.LastMessage.Contains("failed", StringComparison.OrdinalIgnoreCase))
            return ErrorText;

        if (session.LastMessage.Contains("empty", StringComparison.OrdinalIgnoreCase)
            || session.LastMessage.Contains("No ", StringComparison.Ordinal))
            return WarnText;

        return MutedText;
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

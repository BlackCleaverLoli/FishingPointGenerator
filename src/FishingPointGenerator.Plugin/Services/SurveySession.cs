using FishingPointGenerator.Core;
using FishingPointGenerator.Core.Models;
using OmenTools;

namespace FishingPointGenerator.Plugin.Services;

internal sealed class SurveySession
{
    private readonly ICurrentTerritoryScanner scanner;
    private readonly SurveyJsonStore store;
    private readonly SurveyBlockBuilder blockBuilder = new();
    private readonly SurveyAnalyzer analyzer = new();
    private readonly ExportBuilder exportBuilder = new();

    public SurveySession(PluginPaths paths, ICurrentTerritoryScanner scanner)
    {
        this.scanner = scanner;
        store = new SurveyJsonStore(paths.RootDirectory);
        DataRoot = paths.DataDirectory;
    }

    public string DataRoot { get; }
    public string ScannerName => scanner.Name;
    public bool IsPlaceholderScanner => scanner.IsPlaceholder;
    public TerritorySurveyDocument? CurrentSurvey { get; private set; }
    public TerritoryLabelsDocument? CurrentLabels { get; private set; }
    public IReadOnlyList<SurveyBlock> Blocks { get; private set; } = [];
    public IReadOnlyList<SurveyBlockState> States { get; private set; } = [];
    public SurveyRecommendation? Recommendation { get; private set; }
    public string LastMessage { get; private set; } = "Ready.";

    public uint CurrentTerritoryId => DService.Instance().ClientState.TerritoryType;
    public int CandidateCount => CurrentSurvey?.Candidates.Count ?? 0;
    public int BlockCount => Blocks.Count;
    public int LabelCount => CurrentLabels?.Labels.Count ?? 0;
    public int MixedBlockCount => States.Count(state => state.Status == SurveyBlockStatus.Mixed);
    public int QuarantinedBlockCount => States.Count(state => state.Status == SurveyBlockStatus.Quarantined);
    public int ExportableBlockCount => States.Count(state => state.Exportable);
    public string ExportPath => store.GetExportPath();

    public void RefreshCurrentTerritory()
    {
        var territoryId = CurrentTerritoryId;
        CurrentSurvey = store.LoadGeneratedSurvey(territoryId);
        CurrentLabels = store.LoadLabels(territoryId);
        RebuildState();
        LastMessage = $"Loaded territory {territoryId}.";
    }

    public void ScanCurrentTerritory()
    {
        TerritorySurveyDocument scannedSurvey;
        try
        {
            scannedSurvey = scanner.ScanCurrentTerritory();
        }
        catch (Exception ex)
        {
            CurrentSurvey = null;
            CurrentLabels = null;
            Blocks = [];
            States = [];
            Recommendation = null;
            LastMessage = $"Scan failed: {ex.Message}";
            return;
        }

        if (scannedSurvey.TerritoryId == 0 || scannedSurvey.Candidates.Count == 0)
        {
            CurrentSurvey = scannedSurvey;
            CurrentLabels = store.LoadLabels(scannedSurvey.TerritoryId);
            Blocks = [];
            States = [];
            Recommendation = null;
            LastMessage = "Scanner returned no candidates.";
            return;
        }

        var blocks = blockBuilder.BuildBlocks(scannedSurvey.Candidates);
        CurrentSurvey = scannedSurvey with
        {
            Candidates = blocks.SelectMany(block => block.Candidates).ToList(),
            Labels = [],
        };
        store.SaveGeneratedSurvey(CurrentSurvey);

        CurrentLabels = store.LoadLabels(CurrentSurvey.TerritoryId);
        RebuildStateFromBlocks(blocks);
        LastMessage = $"Scanned territory {CurrentSurvey.TerritoryId}: {CandidateCount} candidates, {BlockCount} blocks.";
    }

    public void LabelRecommendation(uint fishingSpotId)
    {
        if (fishingSpotId == 0)
        {
            LastMessage = "FishingSpot.RowId must be greater than zero.";
            return;
        }

        var recommendation = Recommendation;
        if (CurrentSurvey is null || recommendation?.Candidate is null)
        {
            LastMessage = "No recommended block is available to label.";
            return;
        }

        var playerSnapshot = GetPlayerSnapshot();
        var candidate = recommendation.Candidate;
        var label = new FishingSpotLabel
        {
            TerritoryId = CurrentSurvey.TerritoryId,
            BlockId = recommendation.BlockId,
            CandidateId = candidate.CandidateId,
            FishingSpotId = fishingSpotId,
            Status = LabelStatus.Accepted,
            ConfirmedPosition = playerSnapshot?.Position ?? candidate.Position,
            ConfirmedRotation = playerSnapshot?.Rotation ?? candidate.Rotation,
        };

        AppendLabel(label);
        LastMessage = $"Labeled {recommendation.BlockId} as FishingSpot {fishingSpotId}.";
    }

    public void IgnoreRecommendation()
    {
        var recommendation = Recommendation;
        if (CurrentSurvey is null || recommendation?.Candidate is null)
        {
            LastMessage = "No recommended block is available to ignore.";
            return;
        }

        var playerSnapshot = GetPlayerSnapshot();
        var candidate = recommendation.Candidate;
        AppendLabel(new FishingSpotLabel
        {
            TerritoryId = CurrentSurvey.TerritoryId,
            BlockId = recommendation.BlockId,
            CandidateId = candidate.CandidateId,
            FishingSpotId = 0,
            Status = LabelStatus.Ignored,
            ConfirmedPosition = playerSnapshot?.Position ?? candidate.Position,
            ConfirmedRotation = playerSnapshot?.Rotation ?? candidate.Rotation,
        });

        LastMessage = $"Ignored {recommendation.BlockId}.";
    }

    public void Export()
    {
        if (States.Count == 0)
        {
            LastMessage = "No analyzed blocks are available to export.";
            return;
        }

        var export = exportBuilder.Build(States);
        store.SaveExport(export);
        LastMessage = $"Exported {export.FishingSpots.Sum(spot => spot.Points.Count)} points to {ExportPath}.";
    }

    private void AppendLabel(FishingSpotLabel label)
    {
        CurrentLabels ??= new TerritoryLabelsDocument { TerritoryId = label.TerritoryId };
        CurrentLabels = CurrentLabels with
        {
            Labels = CurrentLabels.Labels.Append(label).ToList(),
        };
        store.SaveLabels(CurrentLabels);
        RebuildState();
    }

    private void RebuildState()
    {
        if (CurrentSurvey is null)
        {
            Blocks = [];
            States = [];
            Recommendation = null;
            return;
        }

        var blocks = BuildBlocksFromCandidates(CurrentSurvey.Candidates);
        RebuildStateFromBlocks(blocks);
    }

    private void RebuildStateFromBlocks(IReadOnlyList<SurveyBlock> blocks)
    {
        Blocks = blocks;
        IReadOnlyList<FishingSpotLabel> labels = CurrentLabels?.Labels is { } currentLabels
            ? currentLabels
            : Array.Empty<FishingSpotLabel>();
        States = analyzer.Analyze(Blocks, labels);
        Recommendation = analyzer.RecommendNext(States, GetPlayerSnapshot()?.Position);
    }

    private IReadOnlyList<SurveyBlock> BuildBlocksFromCandidates(IReadOnlyList<ApproachCandidate> candidates)
    {
        if (candidates.Count == 0)
            return [];

        if (candidates.All(candidate => !string.IsNullOrWhiteSpace(candidate.RegionId) && !string.IsNullOrWhiteSpace(candidate.BlockId)))
        {
            return candidates
                .GroupBy(candidate => new { candidate.TerritoryId, candidate.RegionId, candidate.BlockId })
                .OrderBy(group => group.Key.TerritoryId)
                .ThenBy(group => group.Key.RegionId, StringComparer.Ordinal)
                .ThenBy(group => group.Key.BlockId, StringComparer.Ordinal)
                .Select(group => new SurveyBlock
                {
                    TerritoryId = group.Key.TerritoryId,
                    RegionId = group.Key.RegionId,
                    BlockId = group.Key.BlockId,
                    Candidates = group.OrderBy(candidate => candidate.CandidateId, StringComparer.Ordinal).ToList(),
                })
                .ToList();
        }

        return blockBuilder.BuildBlocks(candidates);
    }

    private static PlayerSnapshot? GetPlayerSnapshot()
    {
        var player = DService.Instance().ObjectTable.LocalPlayer;
        if (player is null)
            return null;

        return new PlayerSnapshot(Point3.From(player.Position), player.Rotation);
    }

    private sealed record PlayerSnapshot(Point3 Position, float Rotation);
}

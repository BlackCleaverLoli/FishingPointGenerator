using FishingPointGenerator.Core;
using FishingPointGenerator.Core.Models;
using FishingPointGenerator.Plugin.Services.Catalog;
using FishingPointGenerator.Plugin.Services.Scanning;
using OmenTools;

namespace FishingPointGenerator.Plugin.Services;

internal sealed class SpotWorkflowSession
{
    private readonly SpotJsonStore store;
    private readonly LuminaFishingSpotCatalogBuilder catalogBuilder = new();
    private readonly SpotAnalysisBuilder analysisBuilder = new();
    private readonly SpotRecommendationEngine recommendationEngine = new();
    private readonly SpotExportBuilder exportBuilder = new();
    private readonly SpotScanService scanService;

    public SpotWorkflowSession(PluginPaths paths, ICurrentTerritoryScanner scanner)
    {
        store = new SpotJsonStore(paths.RootDirectory);
        var geometryCache = new TerritoryGeometryCache(scanner);
        scanService = new SpotScanService(geometryCache);
        DataRoot = paths.DataDirectory;
        ScannerName = geometryCache.ScannerName;
    }

    public string DataRoot { get; }
    public string ScannerName { get; }
    public FishingSpotCatalogDocument Catalog { get; private set; } = new();
    public IReadOnlyList<FishingSpotTarget> CurrentTerritoryTargets { get; private set; } = [];
    public IReadOnlyList<SpotAnalysis> Analyses { get; private set; } = [];
    public FishingSpotTarget? CurrentTarget { get; private set; }
    public SpotAnalysis? CurrentAnalysis { get; private set; }
    public string LastMessage { get; private set; } = "Ready.";

    public uint CurrentTerritoryId => DService.Instance().ClientState.TerritoryType;
    public string CatalogPath => store.GetCatalogPath();
    public string ExportPath => store.GetExportPath();
    public int TargetCount => CurrentTerritoryTargets.Count;
    public int ConfirmedCount => CountStatus(SpotAnalysisStatus.Confirmed);
    public int NeedsVisitCount => CountStatus(SpotAnalysisStatus.NeedsVisit);
    public int NoCandidateCount => CountStatus(SpotAnalysisStatus.NoCandidate);
    public int MixedRiskCount => CountStatus(SpotAnalysisStatus.MixedRisk);
    public int IgnoredCount => CountStatus(SpotAnalysisStatus.Ignored);
    public int WeakCoverageCount => CountStatus(SpotAnalysisStatus.WeakCoverage);
    public int OrphanedLabelCount => CountStatus(SpotAnalysisStatus.OrphanedLabels);

    public void RefreshCatalog()
    {
        Catalog = catalogBuilder.Build();
        store.SaveCatalog(Catalog);
        RefreshCurrentTerritory(selectNext: true);
        LastMessage = $"Catalog refreshed: {Catalog.Spots.Count} fishing spots.";
    }

    public void RefreshCurrentTerritory(bool selectNext = false)
    {
        Catalog = store.LoadCatalog();
        if (Catalog.Spots.Count == 0)
        {
            LastMessage = "FishingSpot catalog is empty. Refresh catalog first.";
            CurrentTerritoryTargets = [];
            Analyses = [];
            CurrentTarget = null;
            CurrentAnalysis = null;
            return;
        }

        var territoryId = CurrentTerritoryId;
        CurrentTerritoryTargets = Catalog.Spots
            .Where(target => target.TerritoryId == territoryId)
            .OrderBy(target => target.FishingSpotId)
            .ToList();

        RebuildAnalyses();
        if (selectNext || CurrentTarget is null || CurrentTarget.TerritoryId != territoryId)
            SelectNextTarget(setMessage: false);
        else
            SyncCurrentAnalysis();

        LastMessage = $"Loaded territory {territoryId}: {CurrentTerritoryTargets.Count} catalog targets.";
    }

    public bool SelectTarget(uint fishingSpotId)
    {
        var target = CurrentTerritoryTargets.FirstOrDefault(target => target.FishingSpotId == fishingSpotId);
        if (target is null)
        {
            LastMessage = $"FishingSpot {fishingSpotId} is not in current territory {CurrentTerritoryId}.";
            return false;
        }

        CurrentTarget = target;
        SyncCurrentAnalysis();
        LastMessage = $"Selected FishingSpot {target.FishingSpotId}: {target.Name}.";
        return true;
    }

    public void SelectNextTarget(bool setMessage = true)
    {
        var next = recommendationEngine.PickNext(Analyses);
        if (next is null)
        {
            CurrentTarget = CurrentTerritoryTargets.FirstOrDefault();
            SyncCurrentAnalysis();
            if (setMessage)
                LastMessage = CurrentTarget is null ? "No target is available in this territory." : "No pending target; selected first catalog row.";
            return;
        }

        CurrentTarget = CurrentTerritoryTargets.FirstOrDefault(target => target.Key == next.Key);
        CurrentAnalysis = next;
        if (setMessage && CurrentTarget is not null)
            LastMessage = $"Selected next target {CurrentTarget.FishingSpotId}: {CurrentTarget.Name}.";
    }

    public void ScanCurrentTarget()
    {
        if (!EnsureCurrentTarget())
            return;

        var scan = scanService.ScanSpot(CurrentTarget!, Catalog.Spots, forceTerritoryRescan: true);
        store.SaveScan(scan);
        RebuildAnalyses();
        SyncCurrentAnalysis();
        LastMessage = $"Scanned FishingSpot {CurrentTarget!.FishingSpotId}: {scan.Candidates.Count} candidates.";
    }

    public void ConfirmRecommendation()
    {
        if (!EnsureRecommendation(out var scan, out var candidate))
            return;

        var playerSnapshot = GetPlayerSnapshot();
        AppendLedgerEvent(new SpotLabelEvent
        {
            EventType = SpotLabelEventType.Confirm,
            TerritoryId = CurrentTarget!.TerritoryId,
            FishingSpotId = CurrentTarget.FishingSpotId,
            CandidateFingerprint = candidate.CandidateFingerprint,
            ConfirmedPosition = playerSnapshot?.Position ?? candidate.Position,
            ConfirmedTargetPoint = candidate.TargetPoint,
            ConfirmedRotation = playerSnapshot?.Rotation ?? candidate.Rotation,
            SourceScanId = scan.ScanId,
            SourceScannerVersion = scan.ScannerVersion,
        });

        LastMessage = $"Confirmed recommendation for FishingSpot {CurrentTarget.FishingSpotId}.";
    }

    public void RecordMismatch()
    {
        if (!EnsureRecommendation(out var scan, out var candidate))
            return;

        var playerSnapshot = GetPlayerSnapshot();
        AppendLedgerEvent(new SpotLabelEvent
        {
            EventType = SpotLabelEventType.Mismatch,
            TerritoryId = CurrentTarget!.TerritoryId,
            FishingSpotId = CurrentTarget.FishingSpotId,
            CandidateFingerprint = candidate.CandidateFingerprint,
            ConfirmedPosition = playerSnapshot?.Position ?? candidate.Position,
            ConfirmedTargetPoint = candidate.TargetPoint,
            ConfirmedRotation = playerSnapshot?.Rotation ?? candidate.Rotation,
            SourceScanId = scan.ScanId,
            SourceScannerVersion = scan.ScannerVersion,
        });

        LastMessage = $"Recorded mismatch for FishingSpot {CurrentTarget.FishingSpotId}.";
    }

    public void IgnoreCurrentTarget()
    {
        if (!EnsureCurrentTarget())
            return;

        store.SaveReview(new SpotReviewDocument
        {
            Key = CurrentTarget!.Key,
            Decision = SpotReviewDecision.IgnoreSpot,
        });
        RebuildAnalyses();
        SyncCurrentAnalysis();
        LastMessage = $"Ignored FishingSpot {CurrentTarget.FishingSpotId}.";
    }

    public void AllowWeakCoverageExport()
    {
        if (!EnsureCurrentTarget())
            return;

        store.SaveReview(new SpotReviewDocument
        {
            Key = CurrentTarget!.Key,
            Decision = SpotReviewDecision.AllowWeakCoverageExport,
        });
        RebuildAnalyses();
        SyncCurrentAnalysis();
        LastMessage = $"Allowed weak coverage export for FishingSpot {CurrentTarget.FishingSpotId}.";
    }

    public void GenerateCurrentReport()
    {
        if (!EnsureCurrentTarget())
            return;

        var scan = TryLoadScan(CurrentTarget!.Key);
        var ledger = TryLoadLedger(CurrentTarget.Key);
        var report = analysisBuilder.BuildValidationReport(CurrentAnalysis ?? BuildAnalysis(CurrentTarget!), ledger, scan);
        store.SaveReport(report);
        LastMessage = $"Generated validation report for FishingSpot {CurrentTarget.FishingSpotId}.";
    }

    public void ExportConfirmed()
    {
        if (Catalog.Spots.Count == 0)
            Catalog = store.LoadCatalog();

        var analyses = new List<SpotAnalysis>();
        var scans = new List<SpotScanDocument>();
        var ledgers = new List<SpotLabelLedger>();
        foreach (var target in Catalog.Spots)
        {
            var scan = TryLoadScan(target.Key);
            var ledger = TryLoadLedger(target.Key);
            var review = TryLoadReview(target.Key);
            var analysis = analysisBuilder.Analyze(target, scan, ledger, review);
            analyses.Add(analysis);
            if (scan is not null)
                scans.Add(scan);
            if (ledger is not null)
                ledgers.Add(ledger);
        }

        var export = exportBuilder.Build(analyses, scans, ledgers);
        store.SaveExport(export);
        LastMessage = $"Exported {export.FishingSpots.Sum(spot => spot.Points.Count)} confirmed approach points.";
    }

    private void AppendLedgerEvent(SpotLabelEvent labelEvent)
    {
        var ledger = store.LoadLedger(labelEvent.Key);
        store.SaveLedger(ledger with
        {
            Events = ledger.Events.Append(labelEvent).ToList(),
        });
        RebuildAnalyses();
        SyncCurrentAnalysis();
    }

    private bool EnsureRecommendation(out SpotScanDocument scan, out SpotCandidate candidate)
    {
        scan = new SpotScanDocument();
        candidate = new SpotCandidate();
        if (!EnsureCurrentTarget())
            return false;

        scan = TryLoadScan(CurrentTarget!.Key) ?? new SpotScanDocument { Key = CurrentTarget.Key };
        var selectedCandidate = CurrentAnalysis?.RecommendedCandidate ?? scan.Candidates.FirstOrDefault();
        if (selectedCandidate is null || string.IsNullOrWhiteSpace(selectedCandidate.CandidateFingerprint))
        {
            LastMessage = "No recommendation is available for the selected target.";
            return false;
        }

        candidate = selectedCandidate;
        return true;
    }

    private bool EnsureCurrentTarget()
    {
        if (CurrentTarget is not null)
            return true;

        SelectNextTarget(setMessage: false);
        if (CurrentTarget is not null)
            return true;

        LastMessage = "No FishingSpot target is selected.";
        return false;
    }

    private void RebuildAnalyses()
    {
        Analyses = CurrentTerritoryTargets
            .Select(BuildAnalysis)
            .OrderBy(analysis => analysis.Key.FishingSpotId)
            .ToList();
    }

    private SpotAnalysis BuildAnalysis(FishingSpotTarget target)
    {
        return analysisBuilder.Analyze(
            target,
            TryLoadScan(target.Key),
            TryLoadLedger(target.Key),
            TryLoadReview(target.Key));
    }

    private void SyncCurrentAnalysis()
    {
        CurrentAnalysis = CurrentTarget is null
            ? null
            : Analyses.FirstOrDefault(analysis => analysis.Key == CurrentTarget.Key);
    }

    private SpotScanDocument? TryLoadScan(SpotKey key)
    {
        return File.Exists(store.GetScanPath(key)) ? store.LoadScan(key) : null;
    }

    private SpotLabelLedger? TryLoadLedger(SpotKey key)
    {
        return File.Exists(store.GetLedgerPath(key)) ? store.LoadLedger(key) : null;
    }

    private SpotReviewDocument? TryLoadReview(SpotKey key)
    {
        return File.Exists(store.GetReviewPath(key)) ? store.LoadReview(key) : null;
    }

    private int CountStatus(SpotAnalysisStatus status)
    {
        return Analyses.Count(analysis => analysis.Status == status);
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

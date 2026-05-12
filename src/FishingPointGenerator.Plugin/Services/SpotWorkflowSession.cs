using FishingPointGenerator.Core;
using FishingPointGenerator.Core.Geometry;
using FishingPointGenerator.Core.Models;
using FishingPointGenerator.Plugin.Services.GameInteraction;
using FishingPointGenerator.Plugin.Services.Catalog;
using FishingPointGenerator.Plugin.Services.Scanning;
using OmenTools;

namespace FishingPointGenerator.Plugin.Services;

internal sealed class SpotWorkflowSession
{
    private const float MinimumCastBlockSnapDistance = 1f;
    private const float MaximumCastBlockSnapDistance = 50f;
    private const float MinimumCastBlockFillRange = 1f;
    private const float MaximumCastBlockFillRange = 1000f;

    private readonly SpotJsonStore store;
    private readonly SurveyJsonStore surveyStore;
    private readonly LuminaFishingSpotCatalogBuilder catalogBuilder = new();
    private readonly SpotAnalysisBuilder analysisBuilder = new();
    private readonly SpotRecommendationEngine recommendationEngine = new();
    private readonly SpotExportBuilder exportBuilder = new();
    private readonly SurveyBlockOptions blockOptions = new();
    private readonly SurveyBlockBuilder blockBuilder;
    private readonly SpotScanService scanService;

    public SpotWorkflowSession(PluginPaths paths, ICurrentTerritoryScanner scanner)
    {
        store = new SpotJsonStore(paths.RootDirectory);
        surveyStore = new SurveyJsonStore(paths.RootDirectory);
        blockBuilder = new SurveyBlockBuilder(blockOptions);
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
    public TerritorySurveyDocument? CurrentTerritorySurvey { get; private set; }
    public IReadOnlyList<SurveyBlock> CurrentTerritoryBlocks { get; private set; } = [];
    public IReadOnlyList<SurveyBlock> CurrentTargetBlocks { get; private set; } = [];
    public SpotScanDocument? CurrentScan { get; private set; }
    public SpotLabelLedger? CurrentLedger { get; private set; }
    public string LastMessage { get; private set; } = "就绪。";
    public bool AutoRecordCastsEnabled { get; set; } = true;
    public bool OverlayEnabled { get; set; } = true;
    public bool OverlayShowCandidates { get; set; } = true;
    public bool OverlayShowTerritoryCache { get; set; } = true;
    public bool OverlayShowTargetRadius { get; set; } = true;
    public float CastBlockSnapDistanceMeters { get; set; } = 6f;
    public float CastBlockFillRangeMeters { get; set; } = 30f;
    public float OverlayMaxDistanceMeters { get; set; } = 90f;
    public int OverlayCandidateLimit { get; set; } = 160;
    public uint LastCastFishingSpotId { get; private set; }
    public int LastCastRecordedCount { get; private set; }

    public uint CurrentTerritoryId => DService.Instance().ClientState.TerritoryType;
    public string CatalogPath => store.GetCatalogPath();
    public string GeneratedSurveyPath => surveyStore.GetGeneratedSurveyPath(CurrentTerritoryId);
    public string ExportPath => store.GetExportPath();
    public int TargetCount => CurrentTerritoryTargets.Count;
    public int TerritoryCandidateCount => CurrentTerritorySurvey?.Candidates.Count ?? 0;
    public int TerritoryBlockCount => CurrentTerritoryBlocks.Count;
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
        LastMessage = $"目录已刷新：{Catalog.Spots.Count} 个 FishingSpot。";
    }

    public void RefreshCurrentTerritory(bool selectNext = false)
    {
        var territoryId = CurrentTerritoryId;
        LoadCurrentTerritorySurvey(territoryId);
        Catalog = store.LoadCatalog();
        if (Catalog.Spots.Count == 0)
        {
            LastMessage = "FishingSpot 目录为空。请先刷新目录。";
            CurrentTerritoryTargets = [];
            Analyses = [];
            CurrentTarget = null;
            CurrentAnalysis = null;
            CurrentScan = null;
            CurrentLedger = null;
            CurrentTargetBlocks = [];
            return;
        }

        CurrentTerritoryTargets = Catalog.Spots
            .Where(target => target.TerritoryId == territoryId)
            .OrderBy(target => target.FishingSpotId)
            .ToList();

        RebuildAnalyses();
        if (selectNext || CurrentTarget is null || CurrentTarget.TerritoryId != territoryId)
            SelectNextTarget(setMessage: false);
        else
            SyncCurrentAnalysis();

        LastMessage = $"已加载区域 {territoryId}：{CurrentTerritoryTargets.Count} 个目录目标。";
    }

    public bool SelectTarget(uint fishingSpotId)
    {
        var target = CurrentTerritoryTargets.FirstOrDefault(target => target.FishingSpotId == fishingSpotId);
        if (target is null)
        {
            LastMessage = $"FishingSpot {fishingSpotId} 不在当前区域 {CurrentTerritoryId}。";
            return false;
        }

        CurrentTarget = target;
        SyncCurrentAnalysis();
        LastMessage = $"已选择 FishingSpot {target.FishingSpotId}：{target.Name}。";
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
                LastMessage = CurrentTarget is null ? "当前区域没有可用目标。" : "没有待处理目标；已选择目录第一行。";
            return;
        }

        CurrentTarget = CurrentTerritoryTargets.FirstOrDefault(target => target.Key == next.Key);
        SyncCurrentAnalysis();
        if (setMessage && CurrentTarget is not null)
            LastMessage = $"已选择下一个目标 {CurrentTarget.FishingSpotId}：{CurrentTarget.Name}。";
    }

    public void ScanCurrentTerritory()
    {
        TerritorySurveyDocument scannedSurvey;
        try
        {
            scannedSurvey = scanService.ScanCurrentTerritory(forceTerritoryRescan: true);
        }
        catch (Exception ex)
        {
            CurrentTerritorySurvey = null;
            CurrentTerritoryBlocks = [];
            CurrentScan = null;
            CurrentTargetBlocks = [];
            LastMessage = $"扫描当前区域失败：{ex.Message}";
            return;
        }

        var blocks = blockBuilder.BuildBlocks(scannedSurvey.Candidates);
        CurrentTerritorySurvey = scannedSurvey with
        {
            Candidates = blocks.SelectMany(block => block.Candidates).ToList(),
            Labels = [],
        };
        CurrentTerritoryBlocks = blocks;
        surveyStore.SaveGeneratedSurvey(CurrentTerritorySurvey);

        if (CurrentTarget is not null && CurrentTarget.TerritoryId == CurrentTerritorySurvey.TerritoryId)
            TryCreateAndSaveScanFromTerritory(CurrentTarget);

        RebuildAnalyses();
        SyncCurrentAnalysis();
        LastMessage = $"已扫描区域 {CurrentTerritorySurvey.TerritoryId}：{TerritoryCandidateCount} 个候选点，{TerritoryBlockCount} 个块。";
    }

    public void ScanCurrentTarget()
    {
        if (!EnsureCurrentTarget())
            return;

        var scan = TryCreateAndSaveScanFromTerritory(CurrentTarget!);
        if (scan is null)
        {
            LastMessage = "没有当前区域全图缓存。请先扫描当前区域，再生成已选钓场点缓存。";
            return;
        }

        RebuildAnalyses();
        SyncCurrentAnalysis();
        LastMessage = $"已从全图缓存生成 FishingSpot {CurrentTarget!.FishingSpotId} 点缓存：{scan.Candidates.Count} 个候选点，{CurrentTargetBlocks.Count} 个块。";
    }

    public void PlaceCurrentTargetFlag()
    {
        if (!EnsureCurrentTarget())
            return;

        var target = CurrentTarget!;
        if (!FlagPlacer.SetFlagFromWorld(
                target.TerritoryId,
                target.MapId,
                target.WorldX,
                target.WorldZ,
                target.Name,
                tempMarkerRadius: GetMapMarkerRadius(target)))
        {
            LastMessage = $"无法为 FishingSpot {target.FishingSpotId} 放置地图标记。";
            return;
        }

        LastMessage = $"已为 FishingSpot {target.FishingSpotId} 的钓场中心插旗。";
    }

    public void PlaceRecommendedStandingFlag()
    {
        if (!EnsureRecommendation(out _, out var candidate))
            return;

        if (!FlagPlacer.SetFlagFromWorld(
                CurrentTarget!.TerritoryId,
                CurrentTarget.MapId,
                candidate.Position.X,
                candidate.Position.Z,
                $"{CurrentTarget.Name} 点位"))
        {
            LastMessage = $"无法为 FishingSpot {CurrentTarget.FishingSpotId} 的推荐点位插旗。";
            return;
        }

        LastMessage = $"已为 FishingSpot {CurrentTarget.FishingSpotId} 的推荐点位插旗。";
    }

    public bool RecordCastFill(uint fishingSpotId)
    {
        LastCastFishingSpotId = fishingSpotId;
        LastCastRecordedCount = 0;

        if (!AutoRecordCastsEnabled)
            return false;

        if (fishingSpotId == 0)
        {
            LastMessage = "检测到抛竿，但日志中没有有效 FishingSpot.RowId。";
            return true;
        }

        if (CurrentTarget is null)
        {
            LastMessage = $"检测到 FishingSpot {fishingSpotId} 抛竿，但尚未选择要填色的目标。";
            return true;
        }

        if (CurrentTarget.TerritoryId != CurrentTerritoryId)
        {
            LastMessage = $"检测到抛竿 FishingSpot {fishingSpotId}，但当前目标不在当前区域。";
            return true;
        }

        if (CurrentTarget.FishingSpotId != fishingSpotId)
        {
            LastMessage = $"抛竿命中 FishingSpot {fishingSpotId}，与当前目标 {CurrentTarget.FishingSpotId} 不一致，未记录。";
            return true;
        }

        var playerSnapshot = GetPlayerSnapshot();
        if (playerSnapshot is null)
        {
            LastMessage = "检测到抛竿，但没有可用的玩家位置。";
            return true;
        }

        var scan = EnsureScanForCurrentTarget();
        if (scan is null)
        {
            LastMessage = $"检测到 FishingSpot {fishingSpotId} 抛竿，但没有当前区域全图缓存。请先扫描当前区域。";
            return true;
        }

        if (scan.Candidates.Count == 0)
        {
            RebuildAnalyses();
            SyncCurrentAnalysis();
            LastMessage = $"检测到 FishingSpot {fishingSpotId} 抛竿，但当前扫描没有候选点。";
            return true;
        }

        var snapDistance = Math.Clamp(
            CastBlockSnapDistanceMeters,
            MinimumCastBlockSnapDistance,
            MaximumCastBlockSnapDistance);
        CastBlockSnapDistanceMeters = snapDistance;
        var fillRange = Math.Clamp(
            CastBlockFillRangeMeters,
            MinimumCastBlockFillRange,
            MaximumCastBlockFillRange);
        CastBlockFillRangeMeters = fillRange;
        var selection = FindCastFillBlock(scan.Candidates, playerSnapshot.Position, snapDistance);
        if (selection is null)
        {
            LastMessage = $"检测到 FishingSpot {fishingSpotId} 抛竿，但 {snapDistance:F1}m 内没有可填色候选块。";
            return true;
        }

        var candidatesByFingerprint = scan.Candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.CandidateFingerprint))
            .GroupBy(candidate => candidate.CandidateFingerprint, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var fillCandidateIds = SelectCastFillCandidateIds(selection.Block, selection.SeedCandidate, fillRange);
        var candidates = fillCandidateIds
            .Select(candidateId => candidatesByFingerprint.TryGetValue(candidateId, out var spotCandidate)
                ? spotCandidate
                : null)
            .Where(candidate => candidate is not null)
            .Select(candidate => candidate!)
            .OrderBy(candidate => candidate.CandidateFingerprint, StringComparer.Ordinal)
            .ToList();

        var ledger = store.LoadLedger(CurrentTarget.Key);
        var existingFingerprints = ledger.Events
            .Where(label => label.EventType is SpotLabelEventType.Confirm or SpotLabelEventType.Override)
            .Select(label => label.CandidateFingerprint)
            .Where(fingerprint => !string.IsNullOrWhiteSpace(fingerprint))
            .ToHashSet(StringComparer.Ordinal);
        var events = candidates
            .Where(candidate => existingFingerprints.Add(candidate.CandidateFingerprint))
            .Select(candidate => new SpotLabelEvent
            {
                EventType = SpotLabelEventType.Confirm,
                TerritoryId = CurrentTarget.TerritoryId,
                FishingSpotId = CurrentTarget.FishingSpotId,
                CandidateFingerprint = candidate.CandidateFingerprint,
                ConfirmedPosition = candidate.Position,
                ConfirmedRotation = candidate.Rotation,
                SourceScanId = scan.ScanId,
                SourceScannerVersion = scan.ScannerVersion,
                Note = $"autoFillFromCast player={FormatPoint(playerSnapshot.Position)} block={selection.Block.BlockId} snap={snapDistance:F1} fillRange={fillRange:F1}",
            })
            .ToList();

        if (events.Count == 0)
        {
            LastMessage = $"FishingSpot {fishingSpotId} 抛竿块 {selection.Block.BlockId} 的本次局部范围已覆盖 {candidates.Count}/{selection.Block.Candidates.Count} 个候选点，无新增记录。";
            return true;
        }

        store.SaveLedger(ledger with
        {
            Events = ledger.Events.Concat(events).ToList(),
        });
        LastCastRecordedCount = events.Count;
        RebuildAnalyses();
        SyncCurrentAnalysis();
        LastMessage = $"FishingSpot {fishingSpotId} 抛竿填色：块 {selection.Block.BlockId} 局部新增 {events.Count}/{candidates.Count} 个候选点（全块 {selection.Block.Candidates.Count}）。";
        return true;
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
            ConfirmedRotation = playerSnapshot?.Rotation ?? candidate.Rotation,
            SourceScanId = scan.ScanId,
            SourceScannerVersion = scan.ScannerVersion,
        });

        LastMessage = $"已确认 FishingSpot {CurrentTarget.FishingSpotId} 的推荐。";
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
            ConfirmedRotation = playerSnapshot?.Rotation ?? candidate.Rotation,
            SourceScanId = scan.ScanId,
            SourceScannerVersion = scan.ScannerVersion,
        });

        LastMessage = $"已记录 FishingSpot {CurrentTarget.FishingSpotId} 的不匹配。";
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
        LastMessage = $"已忽略 FishingSpot {CurrentTarget.FishingSpotId}。";
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
        LastMessage = $"已允许 FishingSpot {CurrentTarget.FishingSpotId} 以弱覆盖状态导出。";
    }

    public void GenerateCurrentReport()
    {
        if (!EnsureCurrentTarget())
            return;

        var scan = TryLoadScan(CurrentTarget!.Key);
        var ledger = TryLoadLedger(CurrentTarget.Key);
        var report = analysisBuilder.BuildValidationReport(CurrentAnalysis ?? BuildAnalysis(CurrentTarget!), ledger, scan);
        store.SaveReport(report);
        LastMessage = $"已生成 FishingSpot {CurrentTarget.FishingSpotId} 的验证报告。";
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
        LastMessage = $"已导出 {export.FishingSpots.Sum(spot => spot.Points.Count)} 个已确认点位。";
    }

    public void ClearSpotPointCache(uint fishingSpotId)
    {
        var target = CurrentTerritoryTargets.FirstOrDefault(target => target.FishingSpotId == fishingSpotId);
        if (target is null)
        {
            LastMessage = $"FishingSpot {fishingSpotId} 不在当前区域 {CurrentTerritoryId}。";
            return;
        }

        var removedScan = store.DeleteScan(target.Key);
        var removedLedger = store.DeleteLedger(target.Key);
        RebuildAnalyses();
        if (CurrentTarget?.Key == target.Key)
        {
            CurrentScan = null;
            CurrentLedger = null;
            CurrentTargetBlocks = [];
            CurrentAnalysis = BuildAnalysis(target);
            ReplaceAnalysis(CurrentAnalysis);
        }

        LastMessage = $"已清除 FishingSpot {fishingSpotId} 的点缓存和点亮记录（scan={FormatRemoved(removedScan)}，ledger={FormatRemoved(removedLedger)}）。";
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

        var ensuredScan = EnsureScanForCurrentTarget();
        if (ensuredScan is null)
        {
            LastMessage = "已选目标没有点缓存。请先扫描当前区域，再生成当前目标缓存。";
            return false;
        }

        scan = ensuredScan;
        var selectedCandidate = CurrentAnalysis?.RecommendedCandidate ?? scan.Candidates.FirstOrDefault();
        if (selectedCandidate is null || string.IsNullOrWhiteSpace(selectedCandidate.CandidateFingerprint))
        {
            LastMessage = "已选目标没有可用推荐。";
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

        LastMessage = "未选择 FishingSpot 目标。";
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
        if (CurrentTarget is null)
        {
            CurrentAnalysis = null;
            CurrentScan = null;
            CurrentLedger = null;
            CurrentTargetBlocks = [];
            return;
        }

        CurrentAnalysis = Analyses.FirstOrDefault(analysis => analysis.Key == CurrentTarget.Key);
        CurrentScan = TryLoadScan(CurrentTarget.Key);
        if (CurrentScan is null && HasCurrentTerritorySurveyFor(CurrentTarget))
            CurrentScan = TryCreateAndSaveScanFromTerritory(CurrentTarget);

        CurrentLedger = TryLoadLedger(CurrentTarget.Key);
        CurrentTargetBlocks = CurrentScan is null ? [] : BuildBlocksFromSpotCandidates(CurrentScan.Candidates);
        if (CurrentScan is not null)
        {
            CurrentAnalysis = analysisBuilder.Analyze(CurrentTarget, CurrentScan, CurrentLedger, TryLoadReview(CurrentTarget.Key));
            ReplaceAnalysis(CurrentAnalysis);
        }
    }

    private void ReplaceAnalysis(SpotAnalysis analysis)
    {
        var replaced = false;
        var analyses = Analyses
            .Select(existing =>
            {
                if (existing.Key != analysis.Key)
                    return existing;

                replaced = true;
                return analysis;
            })
            .ToList();
        if (!replaced)
            analyses.Add(analysis);

        Analyses = analyses
            .OrderBy(existing => existing.Key.FishingSpotId)
            .ToList();
    }

    private SpotScanDocument? EnsureScanForCurrentTarget()
    {
        var existing = TryLoadScan(CurrentTarget!.Key);
        if (existing is not null)
        {
            CurrentScan = existing;
            CurrentTargetBlocks = BuildBlocksFromSpotCandidates(existing.Candidates);
            return existing;
        }

        return TryCreateAndSaveScanFromTerritory(CurrentTarget);
    }

    private FillBlockSelection? FindCastFillBlock(
        IReadOnlyList<SpotCandidate> candidates,
        Point3 playerPosition,
        float snapDistance)
    {
        var blocks = CurrentTargetBlocks.Count > 0
            ? CurrentTargetBlocks
            : BuildBlocksFromSpotCandidates(candidates);

        return blocks
            .Select(block => new
            {
                Block = block,
                Nearest = block.Candidates
                    .Select(candidate => new
                    {
                        Candidate = candidate,
                        Distance = candidate.Position.HorizontalDistanceTo(playerPosition),
                    })
                    .OrderBy(item => item.Distance)
                    .ThenBy(item => item.Candidate.CandidateId, StringComparer.Ordinal)
                    .FirstOrDefault(),
            })
            .Where(item => item.Nearest is not null && item.Nearest.Distance <= snapDistance)
            .OrderBy(item => item.Nearest!.Distance)
            .ThenByDescending(item => item.Block.Candidates.Count)
            .ThenBy(item => item.Block.BlockId, StringComparer.Ordinal)
            .Select(item => new FillBlockSelection(item.Block, item.Nearest!.Candidate, item.Nearest.Distance))
            .FirstOrDefault();
    }

    private IReadOnlySet<string> SelectCastFillCandidateIds(
        SurveyBlock block,
        ApproachCandidate seedCandidate,
        float fillRange)
    {
        var candidates = block.Candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.CandidateId))
            .OrderBy(candidate => candidate.CandidateId, StringComparer.Ordinal)
            .ToList();
        if (candidates.Count == 0)
            return new HashSet<string>(StringComparer.Ordinal);

        var seed = candidates.FirstOrDefault(candidate => string.Equals(candidate.CandidateId, seedCandidate.CandidateId, StringComparison.Ordinal))
            ?? candidates
                .OrderBy(candidate => candidate.Position.HorizontalDistanceTo(seedCandidate.Position))
                .ThenBy(candidate => candidate.CandidateId, StringComparer.Ordinal)
                .First();
        var distances = candidates.ToDictionary(candidate => candidate.CandidateId, _ => float.MaxValue, StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var selected = new HashSet<string>(StringComparer.Ordinal);
        distances[seed.CandidateId] = 0f;

        while (visited.Count < candidates.Count)
        {
            var current = candidates
                .Where(candidate => !visited.Contains(candidate.CandidateId))
                .OrderBy(candidate => distances[candidate.CandidateId])
                .ThenBy(candidate => candidate.CandidateId, StringComparer.Ordinal)
                .FirstOrDefault();
            if (current is null)
                break;

            var currentDistance = distances[current.CandidateId];
            if (currentDistance > fillRange)
                break;

            visited.Add(current.CandidateId);
            selected.Add(current.CandidateId);

            foreach (var next in candidates)
            {
                if (visited.Contains(next.CandidateId) || !ShouldLinkBlockPortion(current, next))
                    continue;

                var edgeDistance = Math.Max(0.1f, current.Position.HorizontalDistanceTo(next.Position));
                var nextDistance = currentDistance + edgeDistance;
                if (nextDistance < distances[next.CandidateId])
                    distances[next.CandidateId] = nextDistance;
            }
        }

        if (selected.Count == 0)
            selected.Add(seed.CandidateId);

        return selected;
    }

    private bool ShouldLinkBlockPortion(ApproachCandidate left, ApproachCandidate right)
    {
        return MathF.Abs(left.Position.Y - right.Position.Y) <= blockOptions.BlockHeightToleranceMeters
            && left.Position.HorizontalDistanceTo(right.Position) <= blockOptions.BlockLinkDistanceMeters
            && AngleMath.AngularDistance(left.Rotation, right.Rotation) <= blockOptions.BlockRotationToleranceRadians;
    }

    private void LoadCurrentTerritorySurvey(uint territoryId)
    {
        SetCurrentTerritorySurvey(surveyStore.LoadGeneratedSurvey(territoryId));
    }

    private void SetCurrentTerritorySurvey(TerritorySurveyDocument? survey)
    {
        CurrentTerritoryBlocks = BuildBlocksFromApproachCandidates(survey?.Candidates ?? []);
        CurrentTerritorySurvey = survey is null
            ? null
            : survey with
            {
                Candidates = CurrentTerritoryBlocks.SelectMany(block => block.Candidates).ToList(),
            };
    }

    private SpotScanDocument? TryCreateAndSaveScanFromTerritory(FishingSpotTarget target)
    {
        if (!HasCurrentTerritorySurveyFor(target))
            return null;

        var scan = scanService.CreateSpotScan(target, Catalog.Spots, CurrentTerritorySurvey!);
        store.SaveScan(scan);
        CurrentScan = scan;
        CurrentTargetBlocks = BuildBlocksFromSpotCandidates(scan.Candidates);
        return scan;
    }

    private bool HasCurrentTerritorySurveyFor(FishingSpotTarget target)
    {
        return CurrentTerritorySurvey is { } survey
            && survey.TerritoryId == target.TerritoryId
            && survey.Candidates.Count > 0;
    }

    private IReadOnlyList<SurveyBlock> BuildBlocksFromSpotCandidates(IReadOnlyList<SpotCandidate> candidates)
    {
        return BuildBlocksFromApproachCandidates(candidates.Select(ToApproachCandidate).ToList());
    }

    private IReadOnlyList<SurveyBlock> BuildBlocksFromApproachCandidates(IReadOnlyList<ApproachCandidate> candidates)
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

    private static ApproachCandidate ToApproachCandidate(SpotCandidate candidate)
    {
        return new ApproachCandidate
        {
            CandidateId = candidate.CandidateFingerprint,
            TerritoryId = candidate.Key.TerritoryId,
            RegionId = candidate.RegionId,
            BlockId = candidate.BlockId,
            Position = candidate.Position,
            Rotation = candidate.Rotation,
            Score = candidate.Score,
            Status = candidate.Status,
            CreatedAt = candidate.CreatedAt,
        };
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

    private static string FormatPoint(Point3 point)
    {
        return $"{point.X:F2},{point.Y:F2},{point.Z:F2}";
    }

    private static string FormatRemoved(bool removed)
    {
        return removed ? "deleted" : "missing";
    }

    private static ushort GetMapMarkerRadius(FishingSpotTarget target)
    {
        if (target.Radius <= 0f)
            return 0;

        return (ushort)Math.Clamp(MathF.Round(target.Radius / 7f), 1f, ushort.MaxValue);
    }

    private static PlayerSnapshot? GetPlayerSnapshot()
    {
        var player = DService.Instance().ObjectTable.LocalPlayer;
        if (player is null)
            return null;

        return new PlayerSnapshot(Point3.From(player.Position), player.Rotation);
    }

    private sealed record PlayerSnapshot(Point3 Position, float Rotation);

    private sealed record FillBlockSelection(SurveyBlock Block, ApproachCandidate SeedCandidate, float Distance);
}

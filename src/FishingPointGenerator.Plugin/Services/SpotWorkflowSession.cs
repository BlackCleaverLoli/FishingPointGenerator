using FishingPointGenerator.Core;
using FishingPointGenerator.Core.Models;
using FishingPointGenerator.Plugin.Services.GameInteraction;
using FishingPointGenerator.Plugin.Services.Catalog;
using FishingPointGenerator.Plugin.Services.Scanning;
using Lumina.Excel.Sheets;
using OmenTools;
using System.Threading;

namespace FishingPointGenerator.Plugin.Services;

internal sealed class SpotWorkflowSession
{
    private const float MinimumCastBlockSnapDistance = 1f;
    private const float MaximumCastBlockSnapDistance = 50f;
    private const float MinimumCastBlockFillRange = 1f;
    private const float MaximumCastBlockFillRange = 1000f;

    private readonly SpotJsonStore store;
    private readonly TerritoryMaintenanceStore maintenanceStore;
    private readonly LuminaFishingSpotCatalogBuilder catalogBuilder = new();
    private readonly MaintenanceAnalysisBuilder maintenanceAnalysisBuilder = new();
    private readonly SpotTargetSelectionEngine targetSelectionEngine = new();
    private readonly MaintenanceExportBuilder maintenanceExportBuilder = new();
    private readonly SurveyBlockOptions blockOptions = new();
    private readonly SurveyBlockBuilder blockBuilder;
    private readonly SpotScanService scanService;
    private readonly VnavmeshQueryService navmeshQuery = new();
    private Task<TerritoryScanWorkResult>? territoryScanTask;
    private CancellationTokenSource? territoryScanCancellation;
    private bool territoryScanCancelMessageRequested;
    private int territoryScanGeneration;

    public SpotWorkflowSession(PluginPaths paths, ICurrentTerritoryScanner scanner)
    {
        store = new SpotJsonStore(paths.RootDirectory);
        maintenanceStore = new TerritoryMaintenanceStore(paths.RootDirectory);
        blockBuilder = new SurveyBlockBuilder(blockOptions);
        var geometryCache = new TerritoryGeometryCache(scanner);
        scanService = new SpotScanService(geometryCache);
        DataRoot = paths.DataDirectory;
        ScannerName = geometryCache.ScannerName;
    }

    public string DataRoot { get; }
    public string ScannerName { get; }
    public FishingSpotCatalogDocument Catalog { get; private set; } = new();
    public IReadOnlyList<TerritoryMaintenanceSummary> TerritorySummaries { get; private set; } = [];
    public uint SelectedTerritoryId { get; private set; }
    public string SelectedTerritoryName { get; private set; } = string.Empty;
    public TerritoryMaintenanceDocument? CurrentTerritoryMaintenance { get; private set; }
    public IReadOnlyList<FishingSpotTarget> CurrentTerritoryTargets { get; private set; } = [];
    public IReadOnlyList<SpotAnalysis> Analyses { get; private set; } = [];
    public FishingSpotTarget? CurrentTarget { get; private set; }
    public SpotAnalysis? CurrentAnalysis { get; private set; }
    public TerritorySurveyDocument? CurrentTerritorySurvey { get; private set; }
    public IReadOnlyList<SurveyBlock> CurrentTerritoryBlocks { get; private set; } = [];
    public IReadOnlyList<SurveyBlock> CurrentTargetBlocks { get; private set; } = [];
    public SpotScanDocument? CurrentScan { get; private set; }
    public CandidateSelection? CurrentCandidateSelection { get; private set; }
    public string LastMessage { get; private set; } = "就绪。";
    public bool AutoRecordCastsEnabled { get; set; } = true;
    public bool OverlayEnabled { get; set; } = true;
    public bool OverlayShowCandidates { get; set; } = true;
    public bool OverlayShowTerritoryCache { get; set; } = true;
    public bool OverlayShowTargetRadius { get; set; } = true;
    public bool OverlayShowFishableDebug { get; set; } = true;
    public bool OverlayShowWalkableDebug { get; set; } = true;
    public float CastBlockSnapDistanceMeters { get; set; } = 6f;
    public float CastBlockFillRangeMeters { get; set; } = 30f;
    public float OverlayMaxDistanceMeters { get; set; } = 90f;
    public int OverlayCandidateLimit { get; set; } = 160;
    public uint LastCastPlaceNameId { get; private set; }
    public uint LastCastFishingSpotId { get; private set; }
    public int LastCastRecordedCount { get; private set; }
    public NearbyScanDebugResult? NearbyDebugOverlay { get; private set; }
    public TerritoryScanProgress? TerritoryScanProgress { get; private set; }

    public uint CurrentTerritoryId => DService.Instance().ClientState.TerritoryType;
    public bool SelectedTerritoryIsCurrent => SelectedTerritoryId == CurrentTerritoryId;
    public bool TerritoryScanInProgress => territoryScanTask is { IsCompleted: false };
    public float TerritoryScanProgressFraction => TerritoryScanProgress?.Fraction ?? 0f;
    public string CatalogPath => store.GetCatalogPath();
    public string MaintenancePath => SelectedTerritoryId == 0 ? string.Empty : maintenanceStore.GetTerritoryMaintenancePath(SelectedTerritoryId);
    public string ExportPath => store.GetExportPath();
    public int TargetCount => CurrentTerritoryTargets.Count;
    public int TerritoryCandidateCount => CurrentTerritorySurvey?.Candidates.Count ?? 0;
    public int TerritoryBlockCount => CurrentTerritoryBlocks.Count;
    public int ConfirmedCount => CountStatus(SpotAnalysisStatus.Confirmed);
    public int MaintenanceNeededCount => Analyses.Count(IsMaintenanceStatus);
    public int NoCandidateCount => CountStatus(SpotAnalysisStatus.NoCandidate);
    public int MixedRiskCount => CountStatus(SpotAnalysisStatus.MixedRisk);
    public int IgnoredCount => CountStatus(SpotAnalysisStatus.Ignored);
    public int WeakCoverageCount => CountStatus(SpotAnalysisStatus.WeakCoverage);
    public bool CurrentCandidateSelectionIsActionable => CurrentCandidateSelection is not null;
    public SpotReviewDecision CurrentReviewDecision => GetCurrentMaintenanceRecord()?.ReviewDecision ?? SpotReviewDecision.None;
    public string CurrentReviewNote => GetCurrentMaintenanceRecord()?.ReviewNote ?? string.Empty;
    public IReadOnlyList<ApproachPoint> CurrentApproachPoints => GetCurrentMaintenanceRecord()?.ApproachPoints ?? [];
    public IReadOnlyList<SpotEvidenceEvent> CurrentEvidence => GetCurrentMaintenanceRecord()?.Evidence ?? [];
    public string CurrentTargetDisplayName => CurrentTarget is null
        ? string.Empty
        : $"{CurrentTarget.FishingSpotId} {CurrentTarget.Name}";

    public void RefreshCatalog()
    {
        Catalog = catalogBuilder.Build();
        store.SaveCatalog(Catalog);
        RebuildTerritorySummaries();
        RefreshCurrentTerritory(selectNext: true);
        LastMessage = $"目录已刷新：{Catalog.Spots.Count} 个 FishingSpot。";
    }

    public void RefreshCurrentTerritory(bool selectNext = false)
    {
        var territoryId = CurrentTerritoryId;
        Catalog = store.LoadCatalog();
        if (Catalog.Spots.Count == 0)
        {
            LastMessage = "FishingSpot 目录为空。请先刷新目录。";
            TerritorySummaries = [];
            SelectedTerritoryId = territoryId;
            SelectedTerritoryName = string.Empty;
            CurrentTerritoryMaintenance = null;
            CurrentTerritoryTargets = [];
            CurrentTerritorySurvey = null;
            CurrentTerritoryBlocks = [];
            Analyses = [];
            CurrentTarget = null;
            CurrentAnalysis = null;
            CurrentScan = null;
            CurrentCandidateSelection = null;
            CurrentTargetBlocks = [];
            return;
        }

        RebuildTerritorySummaries();
        if (!SelectTerritory(territoryId, selectNext, setMessage: false))
        {
            SelectedTerritoryId = territoryId;
            SelectedTerritoryName = string.Empty;
            CurrentTerritoryMaintenance = null;
            CurrentTerritoryTargets = [];
            CurrentTerritorySurvey = null;
            CurrentTerritoryBlocks = [];
            Analyses = [];
            CurrentTarget = null;
            CurrentAnalysis = null;
            CurrentScan = null;
            CurrentCandidateSelection = null;
            CurrentTargetBlocks = [];
            LastMessage = $"当前区域 {territoryId} 不在 FishingSpot 目录中。";
            return;
        }

        LastMessage = $"已加载当前区域 {territoryId}：{CurrentTerritoryTargets.Count} 个目录目标。";
    }

    public bool SelectTerritory(uint territoryId, bool selectNext = false, bool setMessage = true)
    {
        if (Catalog.Spots.Count == 0)
            Catalog = store.LoadCatalog();

        var targets = Catalog.Spots
            .Where(target => target.TerritoryId == territoryId)
            .OrderBy(target => target.FishingSpotId)
            .ToList();

        if (targets.Count == 0)
        {
            if (setMessage)
                LastMessage = $"目录中没有 Territory {territoryId} 的钓场。";
            return false;
        }

        SelectedTerritoryId = territoryId;
        SelectedTerritoryName = targets.FirstOrDefault(target => !string.IsNullOrWhiteSpace(target.TerritoryName))?.TerritoryName ?? string.Empty;
        CurrentTerritoryTargets = targets;
        CurrentTerritoryMaintenance = EnsureMaintenanceSpots(
            maintenanceStore.LoadTerritory(territoryId, SelectedTerritoryName),
            targets);
        maintenanceStore.SaveTerritory(CurrentTerritoryMaintenance);
        SetCurrentTerritorySurvey(null);

        RebuildAnalyses();
        if (selectNext || CurrentTarget is null || CurrentTarget.TerritoryId != territoryId)
            SelectNextTarget(setMessage: false);
        else
            SyncCurrentAnalysis();

        RebuildTerritorySummaries();
        if (setMessage)
            LastMessage = $"已选择领地 {territoryId} {SelectedTerritoryName}：{CurrentTerritoryTargets.Count} 个钓场。";

        return true;
    }

    public void HandleTerritoryChanged(uint territoryId)
    {
        ClearCurrentTerritoryRuntimeState();
        RefreshCurrentTerritory(selectNext: true);
        LastMessage = $"已切换区域 {territoryId}：已清空上一张图的内存扫描状态。{LastMessage}";
    }

    public bool SelectTarget(uint fishingSpotId)
    {
        var target = CurrentTerritoryTargets.FirstOrDefault(target => target.FishingSpotId == fishingSpotId);
        if (target is null)
        {
            LastMessage = $"FishingSpot {fishingSpotId} 不在已选择领地 {SelectedTerritoryId}。";
            return false;
        }

        CurrentTarget = target;
        SyncCurrentAnalysis();
        LastMessage = $"已选择 FishingSpot {target.FishingSpotId}：{target.Name}。";
        return true;
    }

    public void SelectNextTarget(bool setMessage = true)
    {
        var next = targetSelectionEngine.PickNext(Analyses);
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
        if (TerritoryScanInProgress)
        {
            LastMessage = TerritoryScanProgress is { } currentProgress
                ? $"扫描正在后台进行：{currentProgress.Message}"
                : "扫描正在后台进行。";
            return;
        }

        TerritoryScanCapture capture;
        bool currentTerritoryFlyable;
        bool navmeshReady;
        PlayerSnapshot? playerSnapshot;
        try
        {
            capture = scanService.CaptureCurrentTerritory();
            currentTerritoryFlyable = CurrentGameState.IsCurrentTerritoryFlyable();
            navmeshReady = navmeshQuery.IsReady;
            playerSnapshot = GetPlayerSnapshot();
        }
        catch (Exception ex)
        {
            CurrentTerritorySurvey = null;
            CurrentTerritoryBlocks = [];
            CurrentScan = null;
            CurrentCandidateSelection = null;
            CurrentTargetBlocks = [];
            LastMessage = $"扫描当前区域失败：{ex.Message}";
            return;
        }

        if (capture.TerritoryId == 0)
        {
            LastMessage = "扫描当前区域失败：没有可用区域。";
            return;
        }

        territoryScanCancellation?.Dispose();
        territoryScanCancellation = new CancellationTokenSource();
        territoryScanCancelMessageRequested = false;
        var cancellationToken = territoryScanCancellation.Token;
        var scanGeneration = Interlocked.Increment(ref territoryScanGeneration);
        var scanProgress = new Progress<TerritoryScanProgress>(value =>
        {
            if (Volatile.Read(ref territoryScanGeneration) == scanGeneration)
                TerritoryScanProgress = value;
        });
        TerritoryScanProgress = new TerritoryScanProgress("准备", 0, 1, $"正在启动区域 {capture.TerritoryId} 后台扫描。");
        territoryScanTask = Task.Run(
            async () => await ExecuteTerritoryScanAsync(
                    capture,
                    currentTerritoryFlyable,
                    navmeshReady,
                    playerSnapshot,
                    scanProgress,
                    cancellationToken)
                .ConfigureAwait(false),
            cancellationToken);
        LastMessage = $"已开始后台扫描区域 {capture.TerritoryId}。扫描期间可以继续操作客户端。";
    }

    public void CancelTerritoryScan()
    {
        CancelTerritoryScan(setMessage: true);
    }

    public void PollBackgroundOperations()
    {
        var task = territoryScanTask;
        if (task is null || !task.IsCompleted)
            return;

        territoryScanTask = null;
        Interlocked.Increment(ref territoryScanGeneration);
        var showCancellationMessage = territoryScanCancelMessageRequested;
        territoryScanCancelMessageRequested = false;
        territoryScanCancellation?.Dispose();
        territoryScanCancellation = null;
        TerritoryScanProgress = null;

        TerritoryScanWorkResult result;
        try
        {
            result = task.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            if (showCancellationMessage)
                LastMessage = "扫描已取消。";
            return;
        }
        catch (Exception ex)
        {
            ClearCurrentTerritoryCandidateState();
            LastMessage = $"扫描当前区域失败：{ex.Message}";
            return;
        }

        if (!result.Success || result.Survey is null)
        {
            ClearCurrentTerritoryCandidateState();
            LastMessage = result.Message;
            return;
        }

        if (result.Survey.TerritoryId != CurrentTerritoryId)
        {
            LastMessage = $"扫描区域 {result.Survey.TerritoryId} 已完成，但当前已切换到区域 {CurrentTerritoryId}；结果已丢弃。";
            return;
        }

        if (SelectedTerritoryId != result.Survey.TerritoryId)
            SelectTerritory(result.Survey.TerritoryId, selectNext: false, setMessage: false);

        CurrentTerritorySurvey = result.Survey;
        CurrentTerritoryBlocks = result.Blocks;
        RebuildTerritorySummaries();
        RebuildAnalyses();
        SyncCurrentAnalysis();
        LastMessage = result.Message;
    }

    public void ScanCurrentTarget()
    {
        if (!EnsureCurrentTarget())
            return;

        var target = CurrentTarget!;
        var scan = CreateScanFromTerritory(target);
        if (scan is null)
        {
            LastMessage = "当前区域没有内存候选。请先扫描当前区域，再为已选钓场派生候选。";
            return;
        }

        CurrentScan = scan;
        CurrentTargetBlocks = BuildBlocksFromSpotCandidates(scan.Candidates);
        RebuildAnalyses();
        SyncCurrentAnalysis();
        LastMessage = $"已从当前领地内存候选为 FishingSpot {target.FishingSpotId} 派生候选：{scan.Candidates.Count} 个候选点，{CurrentTargetBlocks.Count} 个块。";
    }

    public void RefreshCandidateSelection()
    {
        if (!EnsureCurrentTarget())
            return;

        var target = CurrentTarget!;
        var scan = EnsureScanForCurrentTarget();
        if (scan is null)
        {
            CurrentCandidateSelection = null;
            LastMessage = "已选目标没有可派生候选。请先扫描当前区域。";
            return;
        }

        CurrentCandidateSelection = BuildCandidateSelection(target, scan, forceProbe: true);
        CurrentAnalysis ??= BuildAnalysis(target);
        ReplaceAnalysis(CurrentAnalysis);
        LastMessage = CurrentCandidateSelection is null
            ? $"FishingSpot {target.FishingSpotId} 没有可用候选。"
            : $"已刷新 FishingSpot {target.FishingSpotId} 的候选选择：{CurrentCandidateSelection.ModeText}。";
    }

    public void DebugScanNearby(float radiusMeters)
    {
        try
        {
            NearbyDebugOverlay = scanService.DebugScanNearby(radiusMeters);
            OverlayEnabled = true;
            OverlayShowFishableDebug = true;
            OverlayShowWalkableDebug = true;
            LastMessage = NearbyDebugOverlay.Message;
        }
        catch (Exception ex)
        {
            NearbyDebugOverlay = null;
            LastMessage = $"附近碰撞面调试失败：{ex.Message}";
        }
    }

    public IReadOnlyList<string> BuildNearbyCandidateDebugLines(float radiusMeters, int limit)
    {
        radiusMeters = Math.Clamp(radiusMeters, 1f, 1000f);
        limit = Math.Clamp(limit, 1, 500);

        if (!EnsureCurrentTarget())
            return [LastMessage];

        var target = CurrentTarget!;
        var playerSnapshot = GetPlayerSnapshot();
        if (playerSnapshot is null)
        {
            LastMessage = "无法输出候选点调试：没有可用的玩家位置。";
            return [LastMessage];
        }

        var scanSource = "territorySurvey";
        var scan = GetSpotScanForTarget(target);
        if (scan is not null && CurrentScan?.Key == target.Key)
            scanSource = "current";

        if (scan is null)
        {
            LastMessage = "无法输出候选点调试：已选目标没有可派生候选。请先扫描当前区域。";
            return [LastMessage];
        }

        var blocks = BuildBlocksFromSpotCandidates(scan.Candidates);
        var confirmedFingerprints = GetMaintenanceRecord(target.Key)?.ApproachPoints
            .Where(point => point.Status == ApproachPointStatus.Confirmed)
            .Select(point => point.SourceCandidateFingerprint)
            .Where(fingerprint => !string.IsNullOrWhiteSpace(fingerprint))
            .ToHashSet(StringComparer.Ordinal)
            ?? [];
        var blockByCandidateId = blocks
            .SelectMany(block => block.Candidates.Select(candidate => new { candidate.CandidateId, Block = block }))
            .Where(item => !string.IsNullOrWhiteSpace(item.CandidateId))
            .GroupBy(item => item.CandidateId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Block, StringComparer.Ordinal);
        var blockCandidateById = blocks
            .SelectMany(block => block.Candidates)
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.CandidateId))
            .GroupBy(candidate => candidate.CandidateId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var snapDistance = Math.Clamp(
            CastBlockSnapDistanceMeters,
            MinimumCastBlockSnapDistance,
            MaximumCastBlockSnapDistance);
        var fillRange = Math.Clamp(
            CastBlockFillRangeMeters,
            MinimumCastBlockFillRange,
            MaximumCastBlockFillRange);
        var selection = FindCastFillBlock(blocks, playerSnapshot.Position, snapDistance);
        var fillDistances = selection is null
            ? new Dictionary<string, float>(StringComparer.Ordinal)
            : CalculateCastFillDistances(blocks, selection.Block, selection.SeedCandidate);
        var fillCandidateIds = SelectCastFillCandidateIds(fillDistances, fillRange);

        var nearby = scan.Candidates
            .Select(candidate => new
            {
                Candidate = candidate,
                Distance = candidate.Position.HorizontalDistanceTo(playerSnapshot.Position),
            })
            .Where(item => item.Distance <= radiusMeters)
            .OrderBy(item => item.Distance)
            .ThenBy(item => item.Candidate.CandidateFingerprint, StringComparer.Ordinal)
            .Take(limit)
            .ToList();

        var lines = new List<string>
        {
            $"FPG candidate debug: territory={target.TerritoryId} spot={target.FishingSpotId} name=\"{target.Name}\" player=({FormatPoint(playerSnapshot.Position)}) radius={radiusMeters:F1} limit={limit} scanSource={scanSource} candidates={scan.Candidates.Count} targetRange={scan.Candidates.Count(candidate => candidate.IsWithinTargetSearchRadius)} blocks={blocks.Count} nearby={nearby.Count} confirmed={confirmedFingerprints.Count} snap={snapDistance:F1} fillRange={fillRange:F1}",
        };

        if (selection is null)
        {
            var nearestDistance = scan.Candidates.Count == 0
                ? (float?)null
                : scan.Candidates.Min(candidate => candidate.Position.HorizontalDistanceTo(playerSnapshot.Position));
            lines.Add($"FPG candidate debug selection: none within snap={snapDistance:F1} nearestCandidate={FormatNullableDistance(nearestDistance)}");
        }
        else
        {
            var confirmedInBlock = selection.Block.Candidates.Count(candidate => confirmedFingerprints.Contains(candidate.CandidateId));
            var selectedBlockCount = blocks.Count(block => block.Candidates.Any(candidate => fillCandidateIds.Contains(candidate.CandidateId)));
            lines.Add($"FPG candidate debug selection: surface={FormatSurfaceGroup(selection.SeedCandidate.SurfaceGroupId)} block={selection.Block.BlockId} seed={ShortId(selection.SeedCandidate.CandidateId)} seedDistance={selection.Distance:F2} blockCandidates={selection.Block.Candidates.Count} graphCandidates={fillDistances.Count} fillCandidates={fillCandidateIds.Count} fillBlocks={selectedBlockCount} confirmedInBlock={confirmedInBlock}");
        }

        var index = 0;
        foreach (var item in nearby)
        {
            index++;
            var candidate = item.Candidate;
            var fingerprint = candidate.CandidateFingerprint;
            blockByCandidateId.TryGetValue(fingerprint, out var block);
            blockCandidateById.TryGetValue(fingerprint, out var blockCandidate);
            var linkCount = block is not null && blockCandidate is not null
                ? block.Candidates.Count(next => !string.Equals(next.CandidateId, fingerprint, StringComparison.Ordinal)
                    && ShouldLinkBlockPortion(blockCandidate, next))
                : 0;
            var pathDistance = fillDistances.TryGetValue(fingerprint, out var distance)
                ? distance
                : (float?)null;

            lines.Add(
                "FPG candidate debug item: "
                + $"#{index} dist={item.Distance:F2} block={block?.BlockId ?? "-"} "
                + $"surface={FormatSurfaceGroup(candidate.SurfaceGroupId)} "
                + $"fill={(fillCandidateIds.Contains(fingerprint) ? "yes" : "no")} "
                + $"path={FormatNullableDistance(pathDistance)} "
                + $"confirmed={(confirmedFingerprints.Contains(fingerprint) ? "yes" : "no")} "
                + $"targetRange={(candidate.IsWithinTargetSearchRadius ? "yes" : "no")} "
                + $"targetDistance={candidate.DistanceToTargetCenterMeters:F1} "
                + $"links={linkCount} status={candidate.Status} "
                + $"pos=({FormatPoint(candidate.Position)}) rot={candidate.Rotation:F3} "
                + $"fp={ShortId(fingerprint)} source={ShortId(candidate.SourceCandidateId)}");
        }

        LastMessage = $"已输出 FishingSpot {target.FishingSpotId} 附近候选点调试：{nearby.Count}/{scan.Candidates.Count}，详细见 Dalamud log。";
        return lines;
    }

    public void ClearNearbyDebugOverlay()
    {
        NearbyDebugOverlay = null;
        LastMessage = "已清除附近碰撞面调试 overlay。";
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

    public void PlaceSelectedCandidateFlag()
    {
        if (!EnsureCandidateSelection(out _, out var candidate, requireActionable: true))
            return;

        if (!CanUseCandidateSelection(CurrentCandidateSelection))
        {
            LastMessage = "当前没有可用候选。请先扫描当前区域。";
            return;
        }

        var target = CurrentTarget!;
        if (!FlagPlacer.SetFlagFromWorld(
                target.TerritoryId,
                target.MapId,
                candidate.Position.X,
                candidate.Position.Z,
                $"{target.Name} 点位"))
        {
            LastMessage = $"无法为 FishingSpot {target.FishingSpotId} 的候选点插旗。";
            return;
        }

        LastMessage = $"已为 FishingSpot {target.FishingSpotId} 的候选点插旗。";
    }

    public void PlaceNearestUnrecordedCandidateFlag()
    {
        if (!EnsureCurrentTarget())
            return;
        if (!EnsureSelectedTargetIsCurrentTerritory())
            return;

        var target = CurrentTarget!;
        var scan = EnsureScanForCurrentTarget();
        if (scan is null)
        {
            LastMessage = "已选目标没有可派生候选。请先扫描当前区域。";
            return;
        }

        var playerSnapshot = GetPlayerSnapshot();
        if (playerSnapshot is null)
        {
            LastMessage = "无法选择未记录候选：没有可用的玩家位置。";
            return;
        }

        var candidate = GetSelectableCandidatePool(scan, GetMaintenanceRecord(target.Key))
            .OrderBy(candidate => candidate.Position.HorizontalDistanceTo(playerSnapshot.Position))
            .ThenByDescending(candidate => candidate.IsWithinTargetSearchRadius)
            .ThenBy(candidate => candidate.DistanceToTargetCenterMeters)
            .ThenBy(candidate => candidate.CandidateFingerprint, StringComparer.Ordinal)
            .FirstOrDefault();
        if (candidate is null)
        {
            LastMessage = $"FishingSpot {target.FishingSpotId} 没有未记录候选可插旗。";
            return;
        }

        if (!FlagPlacer.SetFlagFromWorld(
                target.TerritoryId,
                target.MapId,
                candidate.Position.X,
                candidate.Position.Z,
                $"{target.Name} 未记录点"))
        {
            LastMessage = $"无法为 FishingSpot {target.FishingSpotId} 的未记录候选插旗。";
            return;
        }

        LastMessage = $"已为 FishingSpot {target.FishingSpotId} 的未记录候选插旗：距角色 {candidate.Position.HorizontalDistanceTo(playerSnapshot.Position):F1}m，距中心 {candidate.DistanceToTargetCenterMeters:F1}m。";
    }

    public bool RecordCastFill(uint castPlaceNameId)
    {
        LastCastPlaceNameId = castPlaceNameId;
        LastCastFishingSpotId = 0;
        LastCastRecordedCount = 0;

        if (!AutoRecordCastsEnabled)
            return false;

        if (castPlaceNameId == 0)
        {
            LastMessage = "检测到抛竿，但日志中没有有效 PlaceName.RowId。";
            return true;
        }

        var playerSnapshot = GetPlayerSnapshot();
        if (playerSnapshot is null)
        {
            LastMessage = "检测到抛竿，但没有可用的玩家位置。";
            return true;
        }

        var target = ResolveCastTarget(castPlaceNameId, playerSnapshot.Position, out var resolutionNote);
        if (target is null)
            return true;

        CurrentTarget = target;
        SyncCurrentAnalysis();

        var fishingSpotId = target.FishingSpotId;
        LastCastFishingSpotId = fishingSpotId;

        var scan = EnsureScanForCurrentTarget();
        if (scan is null)
        {
            LastMessage = $"{resolutionNote}检测到 FishingSpot {fishingSpotId} 抛竿，但没有当前区域内存候选。请先扫描当前区域。";
            return true;
        }

        if (scan.Candidates.Count == 0)
        {
            RebuildAnalyses();
            SyncCurrentAnalysis();
            LastMessage = $"{resolutionNote}检测到 FishingSpot {fishingSpotId} 抛竿，但当前扫描没有候选点。";
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
            LastMessage = $"{resolutionNote}检测到 FishingSpot {fishingSpotId} 抛竿，但 {snapDistance:F1}m 内没有可填色候选块。";
            return true;
        }

        var candidatesByFingerprint = scan.Candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.CandidateFingerprint))
            .GroupBy(candidate => candidate.CandidateFingerprint, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var fillDistances = CalculateCastFillDistances(CurrentTargetBlocks, selection.Block, selection.SeedCandidate);
        var fillCandidateIds = SelectCastFillCandidateIds(fillDistances, fillRange);
        var candidates = fillCandidateIds
            .Select(candidateId => candidatesByFingerprint.TryGetValue(candidateId, out var spotCandidate)
                ? spotCandidate
                : null)
            .Where(candidate => candidate is not null)
            .Select(candidate => candidate!)
            .OrderBy(candidate => candidate.CandidateFingerprint, StringComparer.Ordinal)
            .ToList();

        var existingFingerprints = GetMaintenanceRecord(target.Key)?.ApproachPoints
            .Where(point => point.Status == ApproachPointStatus.Confirmed)
            .Select(point => point.SourceCandidateFingerprint)
            .Where(fingerprint => !string.IsNullOrWhiteSpace(fingerprint))
            .ToHashSet(StringComparer.Ordinal)
            ?? [];
        var note = $"autoFillFromCast placeName={castPlaceNameId} player={FormatPoint(playerSnapshot.Position)} surface={FormatSurfaceGroup(selection.SeedCandidate.SurfaceGroupId)} block={selection.Block.BlockId} snap={snapDistance:F1} fillRange={fillRange:F1}";
        var newCandidates = candidates
            .Where(candidate => existingFingerprints.Add(candidate.CandidateFingerprint))
            .ToList();

        if (newCandidates.Count == 0)
        {
            LastMessage = $"{resolutionNote}FishingSpot {fishingSpotId} 抛竿 Surface {FormatSurfaceGroup(selection.SeedCandidate.SurfaceGroupId)} 的本次连锁范围已覆盖 {candidates.Count}/{fillDistances.Count} 个候选点，无新增记录。";
            return true;
        }

        UpsertAutoCastFillApproachPoints(target, scan, newCandidates, note);
        LastCastRecordedCount = newCandidates.Count;
        RebuildAnalyses();
        SyncCurrentAnalysis();
        LastMessage = $"{resolutionNote}FishingSpot {fishingSpotId} 抛竿连锁点亮：Surface {FormatSurfaceGroup(selection.SeedCandidate.SurfaceGroupId)} 新增 {newCandidates.Count}/{candidates.Count} 个候选点（连锁图 {fillDistances.Count}，seed 块 {selection.Block.BlockId}）。";
        return true;
    }

    public void ConfirmCurrentStanding()
    {
        if (!EnsureCurrentTarget())
            return;
        if (!EnsureSelectedTargetIsCurrentTerritory())
            return;

        var target = CurrentTarget!;
        var playerSnapshot = GetPlayerSnapshot();
        var evidenceId = Guid.NewGuid().ToString("N");
        var scan = EnsureScanForCurrentTarget();
        if (scan is not null)
        {
            CurrentAnalysis = maintenanceAnalysisBuilder.Analyze(
                target,
                scan,
                GetMaintenanceRecord(target.Key),
                TryLoadLegacyReview(target.Key));
            CurrentCandidateSelection = GetOrBuildCandidateSelection(target, scan);
            ReplaceAnalysis(CurrentAnalysis);
        }

        if (playerSnapshot is null)
        {
            LastMessage = "无法读取玩家当前位置，不能记录真实可钓点。";
            return;
        }

        UpsertManualApproachPoint(
            target,
            evidenceId,
            playerSnapshot.Position,
            playerSnapshot.Rotation,
            "manualCurrentStanding");
        LastMessage = $"已用当前站位确认 FishingSpot {target.FishingSpotId}。";
    }

    public void RejectSelectedCandidate()
    {
        if (!EnsureCandidateSelection(out var scan, out var candidate, requireActionable: false))
            return;
        if (!EnsureSelectedTargetIsCurrentTerritory())
            return;

        var target = CurrentTarget!;
        var playerSnapshot = GetPlayerSnapshot();
        AppendMaintenanceEvidence(
            target,
            SpotEvidenceEventType.Reject,
            playerSnapshot?.Position ?? candidate.Position,
            playerSnapshot?.Rotation ?? candidate.Rotation,
            candidate.CandidateFingerprint,
            scan.ScanId,
            scan.ScannerVersion,
            "rejectCandidate");

        LastMessage = $"已排除 FishingSpot {target.FishingSpotId} 的当前候选。";
    }

    public void IgnoreCurrentTarget()
    {
        if (!EnsureCurrentTarget())
            return;

        var target = CurrentTarget!;
        store.SaveLegacyReview(new SpotReviewDocument
        {
            Key = target.Key,
            Decision = SpotReviewDecision.IgnoreSpot,
        });
        SetMaintenanceReview(target, SpotReviewDecision.IgnoreSpot, "ignore", merge: false);
        RebuildAnalyses();
        SyncCurrentAnalysis();
        LastMessage = $"已忽略 FishingSpot {target.FishingSpotId}。";
    }

    public void AllowWeakCoverageExport()
    {
        if (!EnsureCurrentTarget())
            return;

        var target = CurrentTarget!;
        var reviewDecision = MergeReviewDecision(CurrentReviewDecision, SpotReviewDecision.AllowWeakCoverageExport);
        store.SaveLegacyReview(new SpotReviewDocument
        {
            Key = target.Key,
            Decision = reviewDecision,
        });
        SetMaintenanceReview(target, SpotReviewDecision.AllowWeakCoverageExport, "allowWeakCoverageExport", merge: true);
        RebuildAnalyses();
        SyncCurrentAnalysis();
        LastMessage = $"已允许 FishingSpot {target.FishingSpotId} 以弱覆盖状态导出。";
    }

    public void AllowRiskExport()
    {
        if (!EnsureCurrentTarget())
            return;

        var target = CurrentTarget!;
        var reviewDecision = MergeReviewDecision(CurrentReviewDecision, SpotReviewDecision.AllowRiskExport);
        store.SaveLegacyReview(new SpotReviewDocument
        {
            Key = target.Key,
            Decision = reviewDecision,
        });
        SetMaintenanceReview(target, SpotReviewDecision.AllowRiskExport, "allowRiskExport", merge: true);
        RebuildAnalyses();
        SyncCurrentAnalysis();
        LastMessage = $"已允许 FishingSpot {target.FishingSpotId} 在风险复核后导出。";
    }

    public void GenerateCurrentReport()
    {
        if (!EnsureCurrentTarget())
            return;

        var target = CurrentTarget!;
        var analysis = CurrentAnalysis ?? BuildAnalysis(target);
        var maintenance = GetMaintenanceRecord(target.Key);
        var approachPoints = maintenance?.ApproachPoints
            .OrderBy(point => point.Status)
            .ThenBy(point => point.PointId, StringComparer.Ordinal)
            .ToList()
            ?? [];
        var evidence = maintenance?.Evidence
            .OrderByDescending(item => item.CreatedAt)
            .ThenBy(item => item.EventId, StringComparer.Ordinal)
            .ToList()
            ?? [];
        var findings = analysis.Messages
            .Select(message => new SpotValidationFinding
            {
                Severity = analysis.Status == SpotAnalysisStatus.Confirmed
                    ? SpotValidationSeverity.Info
                    : SpotValidationSeverity.Warning,
                Code = analysis.Status.ToString(),
                Message = message,
            })
            .ToList();
        findings.Add(new SpotValidationFinding
        {
            Severity = SpotValidationSeverity.Info,
            Code = "ApproachPoints",
            Message = $"真实可钓点 {approachPoints.Count(point => point.Status == ApproachPointStatus.Confirmed)} 个，证据 {evidence.Count} 条。",
        });
        if (CurrentScan is not null)
        {
            findings.Add(new SpotValidationFinding
            {
                Severity = SpotValidationSeverity.Info,
                Code = "CandidateCache",
                Message = $"当前内存候选 {CurrentScan.Candidates.Count} 个，来源 {CurrentScan.ScannerName}。",
            });
        }

        var report = new SpotValidationReport
        {
            Key = analysis.Key,
            TerritoryName = SelectedTerritoryName,
            FishingSpotName = target.Name,
            Status = analysis.Status,
            CandidateCount = analysis.CandidateCount,
            ConfirmedApproachPointCount = approachPoints.Count(point => point.Status == ApproachPointStatus.Confirmed),
            Findings = findings,
            ApproachPoints = approachPoints,
            Evidence = evidence,
        };
        store.SaveReport(report);
        LastMessage = $"已生成 FishingSpot {target.FishingSpotId} 的验证报告。";
    }

    public void ExportConfirmed()
    {
        if (Catalog.Spots.Count == 0)
            Catalog = store.LoadCatalog();

        var maintenanceDocuments = LoadAllCatalogMaintenanceDocuments();
        var analyses = BuildExportAnalysesFromMaintenance(maintenanceDocuments);

        var export = maintenanceExportBuilder.Build(analyses, maintenanceDocuments);
        store.SaveExport(export);
        LastMessage = $"已导出 {export.FishingSpots.Sum(spot => spot.Points.Count)} 个已确认点位。";
    }

    public void ClearSpotPointCache(uint fishingSpotId)
    {
        var target = CurrentTerritoryTargets.FirstOrDefault(target => target.FishingSpotId == fishingSpotId);
        if (target is null)
        {
            LastMessage = $"FishingSpot {fishingSpotId} 不在已选择领地 {SelectedTerritoryId}。";
            return;
        }

        var removedScan = store.DeleteLegacySpotScan(target.Key);
        RebuildAnalyses();
        if (CurrentTarget?.Key == target.Key)
        {
            CurrentScan = null;
            CurrentCandidateSelection = null;
            CurrentTargetBlocks = [];
            CurrentAnalysis = BuildAnalysis(target);
            ReplaceAnalysis(CurrentAnalysis);
        }

        LastMessage = $"已清除 FishingSpot {fishingSpotId} 的旧版 spot 扫描文件（scan={FormatRemoved(removedScan)}）。维护记录、ledger 和 review 已保留。";
    }

    public void ClearCurrentSpotMaintenance()
    {
        if (!EnsureCurrentTarget())
            return;

        var target = CurrentTarget!;
        var removedLedger = store.DeleteLegacyLedger(target.Key);
        var removedReview = store.DeleteLegacyReview(target.Key);
        UpdateMaintenanceSpot(target, _ => CreateMaintenanceSpot(target));
        CurrentCandidateSelection = CurrentScan is null
            ? null
            : GetOrBuildCandidateSelection(target, CurrentScan);
        LastMessage = $"已清除 FishingSpot {target.FishingSpotId} 的维护数据：真实点、证据和复核状态已重置（ledger={FormatRemoved(removedLedger)}, review={FormatRemoved(removedReview)}）。";
    }

    public void ClearCurrentTerritoryMaintenance()
    {
        if (SelectedTerritoryId == 0)
        {
            LastMessage = "未选择领地，不能清除维护数据。";
            return;
        }

        var targets = CurrentTerritoryTargets;
        if (targets.Count == 0)
        {
            LastMessage = $"已选领地 {SelectedTerritoryId} 没有钓场目录，不能清除维护数据。";
            return;
        }

        var removedLedgers = 0;
        var removedReviews = 0;
        foreach (var target in targets)
        {
            if (store.DeleteLegacyLedger(target.Key))
                removedLedgers++;
            if (store.DeleteLegacyReview(target.Key))
                removedReviews++;
        }

        var document = new TerritoryMaintenanceDocument
        {
            TerritoryId = SelectedTerritoryId,
            TerritoryName = SelectedTerritoryName,
            Spots = targets
                .OrderBy(target => target.FishingSpotId)
                .Select(CreateMaintenanceSpot)
                .ToList(),
        };
        maintenanceStore.SaveTerritory(document);
        CurrentTerritoryMaintenance = document;
        CurrentCandidateSelection = CurrentScan is null || CurrentTarget is null
            ? null
            : GetOrBuildCandidateSelection(CurrentTarget, CurrentScan);
        RebuildTerritorySummaries();
        RebuildAnalyses();
        SyncCurrentAnalysis();
        LastMessage = $"已清除领地 {SelectedTerritoryId} {SelectedTerritoryName} 的维护数据：{targets.Count} 个钓场已重置（ledger={removedLedgers}, review={removedReviews}）。";
    }

    public void ClearCurrentTerritoryCandidates()
    {
        var territoryId = SelectedTerritoryId != 0 ? SelectedTerritoryId : CurrentTerritoryId;
        if (territoryId == 0)
        {
            LastMessage = "未选择领地，不能清除内存候选。";
            return;
        }

        if (SelectedTerritoryId == territoryId)
        {
            SetCurrentTerritorySurvey(null);
            CurrentScan = null;
            CurrentCandidateSelection = null;
            CurrentTargetBlocks = [];
            RebuildAnalyses();
            SyncCurrentAnalysis();
        }

        RebuildTerritorySummaries();
        LastMessage = $"已清除领地 {territoryId} 的内存候选。";
    }

    private void UpsertAutoCastFillApproachPoints(
        FishingSpotTarget target,
        SpotScanDocument scan,
        IReadOnlyList<SpotCandidate> candidates,
        string note)
    {
        UpdateMaintenanceSpot(target, spot =>
        {
            var points = spot.ApproachPoints.ToList();
            var evidence = spot.Evidence.ToList();
            foreach (var candidate in candidates)
            {
                var eventId = Guid.NewGuid().ToString("N");
                evidence.Add(new SpotEvidenceEvent
                {
                    EventId = eventId,
                    EventType = SpotEvidenceEventType.AutoCastFill,
                    Position = candidate.Position,
                    Rotation = candidate.Rotation,
                    CandidateFingerprint = candidate.CandidateFingerprint,
                    SourceScanId = scan.ScanId,
                    SourceScannerVersion = scan.ScannerVersion,
                    Note = note,
                });
                UpsertApproachPoint(
                    target,
                    points,
                    candidate,
                    scan,
                    eventId,
                    ApproachPointSourceKind.AutoCastFill,
                    candidate.Position,
                    candidate.Rotation,
                    note);
            }

            return spot with
            {
                ApproachPoints = points
                    .OrderBy(point => point.PointId, StringComparer.Ordinal)
                    .ToList(),
                Evidence = evidence
                    .OrderBy(item => item.CreatedAt)
                    .ThenBy(item => item.EventId, StringComparer.Ordinal)
                    .ToList(),
            };
        });
    }

    private void UpsertApproachPointFromCandidate(
        FishingSpotTarget target,
        SpotScanDocument scan,
        SpotCandidate candidate,
        string evidenceId,
        ApproachPointSourceKind sourceKind,
        Point3 position,
        float rotation,
        string note)
    {
        UpdateMaintenanceSpot(target, spot =>
        {
            var points = spot.ApproachPoints.ToList();
            var evidence = spot.Evidence.ToList();
            evidence.Add(new SpotEvidenceEvent
            {
                EventId = evidenceId,
                EventType = sourceKind == ApproachPointSourceKind.AutoCastFill
                    ? SpotEvidenceEventType.AutoCastFill
                    : SpotEvidenceEventType.ManualConfirm,
                Position = position,
                Rotation = rotation,
                CandidateFingerprint = candidate.CandidateFingerprint,
                SourceScanId = scan.ScanId,
                SourceScannerVersion = scan.ScannerVersion,
                Note = note,
            });
            UpsertApproachPoint(target, points, candidate, scan, evidenceId, sourceKind, position, rotation, note);

            return spot with
            {
                ApproachPoints = points
                    .OrderBy(point => point.PointId, StringComparer.Ordinal)
                    .ToList(),
                Evidence = evidence
                    .OrderBy(item => item.CreatedAt)
                    .ThenBy(item => item.EventId, StringComparer.Ordinal)
                    .ToList(),
            };
        });
    }

    private void UpsertManualApproachPoint(
        FishingSpotTarget target,
        string evidenceId,
        Point3 position,
        float rotation,
        string note)
    {
        UpdateMaintenanceSpot(target, spot =>
        {
            var points = spot.ApproachPoints.ToList();
            var evidence = spot.Evidence.ToList();
            evidence.Add(new SpotEvidenceEvent
            {
                EventId = evidenceId,
                EventType = SpotEvidenceEventType.ManualConfirm,
                Position = position,
                Rotation = rotation,
                Note = note,
            });
            UpsertApproachPoint(target, points, evidenceId, position, rotation, note);

            return spot with
            {
                ApproachPoints = points
                    .OrderBy(point => point.PointId, StringComparer.Ordinal)
                    .ToList(),
                Evidence = evidence
                    .OrderBy(item => item.CreatedAt)
                    .ThenBy(item => item.EventId, StringComparer.Ordinal)
                    .ToList(),
            };
        });
    }

    private static void UpsertApproachPoint(
        FishingSpotTarget target,
        List<ApproachPoint> points,
        string evidenceId,
        Point3 position,
        float rotation,
        string note)
    {
        UpsertApproachPoint(
            target,
            points,
            evidenceId,
            position,
            rotation,
            note,
            new ApproachPoint { SourceKind = ApproachPointSourceKind.Manual });
    }

    private static void UpsertApproachPoint(
        FishingSpotTarget target,
        List<ApproachPoint> points,
        SpotCandidate candidate,
        SpotScanDocument scan,
        string evidenceId,
        ApproachPointSourceKind sourceKind,
        Point3 position,
        float rotation,
        string note)
    {
        UpsertApproachPoint(
            target,
            points,
            evidenceId,
            position,
            rotation,
            note,
            new ApproachPoint
            {
                SourceKind = sourceKind,
                SourceCandidateFingerprint = candidate.CandidateFingerprint,
                SourceCandidateId = candidate.SourceCandidateId,
                SourceBlockId = candidate.BlockId,
                SourceScanId = scan.ScanId,
                SourceScannerVersion = scan.ScannerVersion,
            });
    }

    private static void UpsertApproachPoint(
        FishingSpotTarget target,
        List<ApproachPoint> points,
        string evidenceId,
        Point3 position,
        float rotation,
        string note,
        ApproachPoint source)
    {
        var pointId = SpotFingerprint.CreateApproachPointId(target.Key, position, rotation);
        var existingIndex = points.FindIndex(point => string.Equals(point.PointId, pointId, StringComparison.Ordinal));
        if (existingIndex >= 0)
        {
            var existing = points[existingIndex];
            points[existingIndex] = existing with
            {
                Status = ApproachPointStatus.Confirmed,
                EvidenceIds = existing.EvidenceIds
                    .Append(evidenceId)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToList(),
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            return;
        }

        points.Add(new ApproachPoint
        {
            PointId = pointId,
            Position = position,
            Rotation = rotation,
            Status = ApproachPointStatus.Confirmed,
            SourceKind = source.SourceKind,
            SourceCandidateFingerprint = source.SourceCandidateFingerprint,
            SourceCandidateId = source.SourceCandidateId,
            SourceBlockId = source.SourceBlockId,
            SourceScanId = source.SourceScanId,
            SourceScannerVersion = source.SourceScannerVersion,
            EvidenceIds = string.IsNullOrWhiteSpace(evidenceId) ? [] : [evidenceId],
            Note = note,
        });
    }

    private void AppendMaintenanceEvidence(
        FishingSpotTarget target,
        SpotEvidenceEventType eventType,
        Point3? position,
        float? rotation,
        string candidateFingerprint,
        string sourceScanId,
        string sourceScannerVersion,
        string note)
    {
        UpdateMaintenanceSpot(target, spot => spot with
        {
            Evidence = spot.Evidence
                .Append(new SpotEvidenceEvent
                {
                    EventType = eventType,
                    Position = position,
                    Rotation = rotation,
                    CandidateFingerprint = candidateFingerprint,
                    SourceScanId = sourceScanId,
                    SourceScannerVersion = sourceScannerVersion,
                    Note = note,
                })
                .OrderBy(item => item.CreatedAt)
                .ThenBy(item => item.EventId, StringComparer.Ordinal)
                .ToList(),
        });
    }

    private void SetMaintenanceReview(FishingSpotTarget target, SpotReviewDecision decision, string note, bool merge)
    {
        UpdateMaintenanceSpot(target, spot => spot with
        {
            ReviewDecision = merge ? MergeReviewDecision(spot.ReviewDecision, decision) : decision,
            ReviewNote = note,
            Evidence = spot.Evidence
                .Append(new SpotEvidenceEvent
                {
                    EventType = SpotEvidenceEventType.Review,
                    Note = $"{decision}: {note}",
                })
                .OrderBy(item => item.CreatedAt)
                .ThenBy(item => item.EventId, StringComparer.Ordinal)
                .ToList(),
        });
    }

    private bool EnsureCandidateSelection(out SpotScanDocument scan, out SpotCandidate candidate, bool requireActionable)
    {
        scan = new SpotScanDocument();
        candidate = new SpotCandidate();
        if (!EnsureCurrentTarget())
            return false;

        var target = CurrentTarget!;
        var ensuredScan = EnsureScanForCurrentTarget();
        if (ensuredScan is null)
        {
            LastMessage = "已选目标没有可派生候选。请先扫描当前区域。";
            return false;
        }

        scan = ensuredScan;
        CurrentAnalysis = maintenanceAnalysisBuilder.Analyze(
            target,
            scan,
            GetMaintenanceRecord(target.Key),
            TryLoadLegacyReview(target.Key));
        CurrentCandidateSelection = GetOrBuildCandidateSelection(target, scan);
        ReplaceAnalysis(CurrentAnalysis);

        var selectedCandidate = CurrentCandidateSelection?.Candidate;
        if (selectedCandidate is null || string.IsNullOrWhiteSpace(selectedCandidate.CandidateFingerprint))
        {
            LastMessage = "已选目标没有可用候选。";
            return false;
        }

        if (requireActionable && !CanUseCandidateSelection(CurrentCandidateSelection))
        {
            LastMessage = "当前候选尚未通过可达性检查。";
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

    private bool EnsureSelectedTargetIsCurrentTerritory()
    {
        if (CurrentTarget?.TerritoryId == CurrentTerritoryId)
            return true;

        LastMessage = "已选钓场不在当前游戏区域，不能记录玩家当前位置。";
        return false;
    }

    private SpotMaintenanceRecord? GetCurrentMaintenanceRecord()
    {
        return CurrentTarget is { } target ? GetMaintenanceRecord(target.Key) : null;
    }

    private SpotMaintenanceRecord? GetMaintenanceRecord(SpotKey key)
    {
        if (!key.IsValid)
            return null;

        var document = CurrentTerritoryMaintenance is { } current && current.TerritoryId == key.TerritoryId
            ? current
            : maintenanceStore.LoadTerritory(key.TerritoryId);
        return document.Spots.FirstOrDefault(spot => spot.FishingSpotId == key.FishingSpotId);
    }

    private void UpdateMaintenanceSpot(
        FishingSpotTarget target,
        Func<SpotMaintenanceRecord, SpotMaintenanceRecord> update)
    {
        IReadOnlyList<FishingSpotTarget> territoryTargets = Catalog.Spots.Count == 0
            ? [target]
            : Catalog.Spots
                .Where(item => item.TerritoryId == target.TerritoryId)
                .OrderBy(item => item.FishingSpotId)
                .ToList();
        var document = EnsureMaintenanceSpots(
            maintenanceStore.LoadTerritory(target.TerritoryId, target.TerritoryName),
            territoryTargets);
        var spots = document.Spots.ToList();
        var index = spots.FindIndex(spot => spot.FishingSpotId == target.FishingSpotId);
        if (index < 0)
        {
            spots.Add(CreateMaintenanceSpot(target));
            index = spots.Count - 1;
        }

        spots[index] = update(spots[index]) with
        {
            FishingSpotId = target.FishingSpotId,
            Name = target.Name,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        document = document with
        {
            TerritoryName = string.IsNullOrWhiteSpace(document.TerritoryName) ? target.TerritoryName : document.TerritoryName,
            Spots = spots
                .OrderBy(spot => spot.FishingSpotId)
                .ToList(),
        };
        maintenanceStore.SaveTerritory(document);

        if (SelectedTerritoryId == target.TerritoryId)
            CurrentTerritoryMaintenance = document;

        RebuildTerritorySummaries();
        RebuildAnalyses();
        SyncCurrentAnalysis();
    }

    private TerritoryMaintenanceDocument EnsureMaintenanceSpots(
        TerritoryMaintenanceDocument document,
        IReadOnlyList<FishingSpotTarget> targets)
    {
        if (targets.Count == 0)
            return document;

        var existing = document.Spots
            .GroupBy(spot => spot.FishingSpotId)
            .ToDictionary(group => group.Key, group => group.First());
        var spots = new List<SpotMaintenanceRecord>();
        foreach (var target in targets.OrderBy(target => target.FishingSpotId))
        {
            var spot = existing.TryGetValue(target.FishingSpotId, out var saved)
                ? saved with { Name = string.IsNullOrWhiteSpace(saved.Name) ? target.Name : saved.Name }
                : CreateMaintenanceSpot(target);
            spot = MergeLegacyReviewIntoMaintenanceSpot(target, spot);
            spot = MergeLegacyLedgerIntoMaintenanceSpot(target, spot);
            spots.Add(spot);
        }

        foreach (var orphaned in document.Spots.Where(spot => !targets.Any(target => target.FishingSpotId == spot.FishingSpotId)))
            spots.Add(orphaned);

        var territoryName = document.TerritoryName;
        if (string.IsNullOrWhiteSpace(territoryName))
            territoryName = targets.FirstOrDefault(target => !string.IsNullOrWhiteSpace(target.TerritoryName))?.TerritoryName ?? string.Empty;

        return document with
        {
            TerritoryId = document.TerritoryId != 0 ? document.TerritoryId : targets[0].TerritoryId,
            TerritoryName = territoryName,
            Spots = spots
                .OrderBy(spot => spot.FishingSpotId)
                .ToList(),
        };
    }

    private static SpotMaintenanceRecord CreateMaintenanceSpot(FishingSpotTarget target)
    {
        return new SpotMaintenanceRecord
        {
            FishingSpotId = target.FishingSpotId,
            Name = target.Name,
        };
    }

    private SpotMaintenanceRecord MergeLegacyLedgerIntoMaintenanceSpot(
        FishingSpotTarget target,
        SpotMaintenanceRecord spot)
    {
        var ledgerPath = store.GetLegacyLedgerPath(target.Key);
        if (!File.Exists(ledgerPath))
            return spot;

        var ledger = store.LoadLegacyLedger(target.Key);
        var bindableEvents = ledger.Events
            .Where(label => label.EventType is SpotLabelEventType.Confirm or SpotLabelEventType.Override)
            .Where(label => label.ConfirmedPosition is not null && label.ConfirmedRotation is not null)
            .OrderBy(label => label.CreatedAt)
            .ThenBy(label => label.EventId, StringComparer.Ordinal)
            .ToList();
        if (bindableEvents.Count == 0)
            return spot;

        var points = spot.ApproachPoints.ToList();
        var evidence = spot.Evidence.ToList();
        var existingPointIds = points
            .Select(point => point.PointId)
            .ToHashSet(StringComparer.Ordinal);
        var existingEvidenceIds = evidence
            .Select(item => item.EventId)
            .ToHashSet(StringComparer.Ordinal);
        var legacyScan = TryLoadLegacySpotScan(target.Key);
        var candidatesByFingerprint = legacyScan?.Candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.CandidateFingerprint))
            .GroupBy(candidate => candidate.CandidateFingerprint, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal)
            ?? [];

        foreach (var label in bindableEvents)
        {
            var position = label.ConfirmedPosition!.Value;
            var rotation = label.ConfirmedRotation!.Value;
            var pointId = SpotFingerprint.CreateApproachPointId(target.Key, position, rotation);
            candidatesByFingerprint.TryGetValue(label.CandidateFingerprint, out var candidate);

            if (existingEvidenceIds.Add(label.EventId))
            {
                evidence.Add(new SpotEvidenceEvent
                {
                    EventId = label.EventId,
                    EventType = SpotEvidenceEventType.ManualConfirm,
                    Position = position,
                    Rotation = rotation,
                    CandidateFingerprint = label.CandidateFingerprint,
                    SourceScanId = label.SourceScanId,
                    SourceScannerVersion = label.SourceScannerVersion,
                    CreatedAt = label.CreatedAt,
                    Note = string.IsNullOrWhiteSpace(label.Note) ? "legacyLedgerImport" : $"legacyLedgerImport: {label.Note}",
                });
            }

            if (!existingPointIds.Add(pointId))
                continue;

            points.Add(new ApproachPoint
            {
                PointId = pointId,
                Position = position,
                Rotation = rotation,
                Status = ApproachPointStatus.Confirmed,
                SourceKind = ApproachPointSourceKind.Imported,
                SourceCandidateFingerprint = label.CandidateFingerprint,
                SourceCandidateId = candidate?.SourceCandidateId ?? string.Empty,
                SourceBlockId = candidate?.BlockId ?? string.Empty,
                SourceScanId = label.SourceScanId,
                SourceScannerVersion = label.SourceScannerVersion,
                EvidenceIds = [label.EventId],
                CreatedAt = label.CreatedAt,
                UpdatedAt = label.CreatedAt,
                Note = "legacyLedgerImport",
            });
        }

        return spot with
        {
            ApproachPoints = points
                .OrderBy(point => point.PointId, StringComparer.Ordinal)
                .ToList(),
            Evidence = evidence
                .OrderBy(item => item.CreatedAt)
                .ThenBy(item => item.EventId, StringComparer.Ordinal)
                .ToList(),
        };
    }

    private SpotMaintenanceRecord MergeLegacyReviewIntoMaintenanceSpot(
        FishingSpotTarget target,
        SpotMaintenanceRecord spot)
    {
        if (spot.ReviewDecision != SpotReviewDecision.None)
            return spot;

        var reviewPath = store.GetLegacyReviewPath(target.Key);
        if (!File.Exists(reviewPath))
            return spot;

        var review = store.LoadLegacyReview(target.Key);
        if (review.Decision == SpotReviewDecision.None)
            return spot;

        return spot with
        {
            ReviewDecision = review.Decision,
            ReviewNote = string.IsNullOrWhiteSpace(review.Note) ? "legacyReviewImport" : review.Note,
            UpdatedAt = review.UpdatedAt,
            Evidence = spot.Evidence
                .Append(new SpotEvidenceEvent
                {
                    EventType = SpotEvidenceEventType.Review,
                    CreatedAt = review.UpdatedAt,
                    Note = string.IsNullOrWhiteSpace(review.Note)
                        ? $"legacyReviewImport: {review.Decision}"
                        : $"legacyReviewImport: {review.Decision}: {review.Note}",
                })
                .OrderBy(item => item.CreatedAt)
                .ThenBy(item => item.EventId, StringComparer.Ordinal)
                .ToList(),
        };
    }

    private IReadOnlyList<TerritoryMaintenanceDocument> LoadAllCatalogMaintenanceDocuments()
    {
        if (Catalog.Spots.Count == 0)
            Catalog = store.LoadCatalog();

        return Catalog.Spots
            .GroupBy(target => target.TerritoryId)
            .OrderBy(group => group.Key)
            .Select(group =>
            {
                var targets = group
                    .OrderBy(target => target.FishingSpotId)
                    .ToList();
                var territoryName = targets.FirstOrDefault(target => !string.IsNullOrWhiteSpace(target.TerritoryName))?.TerritoryName ?? string.Empty;
                return EnsureMaintenanceSpots(maintenanceStore.LoadTerritory(group.Key, territoryName), targets);
            })
            .ToList();
    }

    private void RebuildTerritorySummaries()
    {
        if (Catalog.Spots.Count == 0)
        {
            TerritorySummaries = [];
            return;
        }

        var savedMaintenance = maintenanceStore.LoadAllTerritories()
            .ToDictionary(document => document.TerritoryId);
        TerritorySummaries = Catalog.Spots
            .GroupBy(target => target.TerritoryId)
            .Select(group =>
            {
                var targets = group
                    .OrderBy(target => target.FishingSpotId)
                    .ToList();
                var territoryName = targets.FirstOrDefault(target => !string.IsNullOrWhiteSpace(target.TerritoryName))?.TerritoryName ?? string.Empty;
                savedMaintenance.TryGetValue(group.Key, out var maintenance);
                var analyses = targets
                    .Select(target =>
                    {
                        var spot = maintenance?.Spots.FirstOrDefault(spot => spot.FishingSpotId == target.FishingSpotId);
                        var spotScan = CurrentTerritorySurvey?.TerritoryId == target.TerritoryId
                            ? scanService.CreateSpotScan(target, Catalog.Spots, CurrentTerritorySurvey)
                            : null;
                        return maintenanceAnalysisBuilder.Analyze(target, spotScan, spot, null);
                    })
                    .ToList();
                var confirmed = analyses.Count(analysis => analysis.Status == SpotAnalysisStatus.Confirmed);
                var weak = analyses.Count(analysis => analysis.Status == SpotAnalysisStatus.WeakCoverage);
                var ignored = analyses.Count(analysis => analysis.Status == SpotAnalysisStatus.Ignored);
                var risk = analyses.Count(analysis => analysis.Status == SpotAnalysisStatus.MixedRisk);
                var needsMaintenance = analyses.Count(IsMaintenanceStatus);

                return new TerritoryMaintenanceSummary(
                    group.Key,
                    territoryName,
                    targets.Count,
                    confirmed,
                    needsMaintenance,
                    weak,
                    risk,
                    ignored,
                    CurrentTerritorySurvey?.TerritoryId == group.Key && CurrentTerritorySurvey.Candidates.Count > 0,
                    group.Key == CurrentTerritoryId,
                    group.Key == SelectedTerritoryId);
            })
            .OrderByDescending(summary => summary.IsCurrentTerritory)
            .ThenByDescending(summary => summary.RiskCount)
            .ThenByDescending(summary => summary.MaintenanceNeededCount)
            .ThenBy(summary => summary.TerritoryId)
            .ToList();
    }

    private static bool IsMaintenanceStatus(SpotAnalysis analysis)
    {
        return analysis.Status is
            SpotAnalysisStatus.NeedsScan or
            SpotAnalysisStatus.NeedsVisit or
            SpotAnalysisStatus.NoCandidate or
            SpotAnalysisStatus.MixedRisk or
            SpotAnalysisStatus.WeakCoverage;
    }

    private async Task<TerritoryScanWorkResult> ExecuteTerritoryScanAsync(
        TerritoryScanCapture capture,
        bool currentTerritoryFlyable,
        bool navmeshReady,
        PlayerSnapshot? playerSnapshot,
        IProgress<TerritoryScanProgress> progress,
        CancellationToken cancellationToken)
    {
        try
        {
            progress.Report(new TerritoryScanProgress("扫描", 0, 1, $"正在扫描区域 {capture.TerritoryId}。"));
            var scannedSurvey = scanService.ScanCapturedTerritory(capture, progress, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var rawCandidates = scannedSurvey.Candidates.Count;
            progress.Report(new TerritoryScanProgress("分块", 0, 1, $"正在为 {rawCandidates} 个候选构建块。"));
            var blocks = blockBuilder.BuildBlocks(scannedSurvey.Candidates);
            var blockedSurvey = scannedSurvey with
            {
                Candidates = blocks.SelectMany(block => block.Candidates).ToList(),
            };

            var reachability = await FilterSurveyReachabilityAsync(
                    blockedSurvey,
                    rawCandidates,
                    currentTerritoryFlyable,
                    navmeshReady,
                    playerSnapshot,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!reachability.Success || reachability.Survey is null)
                return new TerritoryScanWorkResult(false, null, [], reachability.Message);

            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(new TerritoryScanProgress("分块", 0, 1, $"正在为 {reachability.Survey.Candidates.Count} 个可用候选重建块。"));
            var filteredBlocks = blockBuilder.BuildBlocks(reachability.Survey.Candidates);
            var finalSurvey = reachability.Survey with
            {
                Candidates = filteredBlocks.SelectMany(block => block.Candidates).ToList(),
                ReachableCandidateCount = filteredBlocks.Sum(block => block.Candidates.Count),
            };
            var message = $"已扫描区域 {finalSurvey.TerritoryId}：原始 {finalSurvey.RawCandidateCount} 个，可用 {finalSurvey.Candidates.Count} 个，丢弃不可达 {finalSurvey.UnreachableCandidateCount} 个，{filteredBlocks.Count} 个块。{finalSurvey.ReachabilityNote}";
            progress.Report(new TerritoryScanProgress("完成", 1, 1, message));
            return new TerritoryScanWorkResult(true, finalSurvey, filteredBlocks, message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new TerritoryScanWorkResult(false, null, [], $"扫描当前区域失败：{ex.Message}");
        }
    }

    private async Task<TerritoryReachabilityResult> FilterSurveyReachabilityAsync(
        TerritorySurveyDocument survey,
        int rawCandidateCount,
        bool currentTerritoryFlyable,
        bool navmeshReady,
        PlayerSnapshot? playerSnapshot,
        IProgress<TerritoryScanProgress> progress,
        CancellationToken cancellationToken)
    {
        if (survey.Candidates.Count == 0)
        {
            return new TerritoryReachabilityResult(
                true,
                survey with
                {
                    ReachabilityMode = SurveyReachabilityMode.NotChecked,
                    RawCandidateCount = rawCandidateCount,
                    ReachableCandidateCount = 0,
                    UnreachableCandidateCount = 0,
                    ReachabilityNote = "扫描未生成候选点。",
                },
                string.Empty);
        }

        if (currentTerritoryFlyable)
        {
            progress.Report(new TerritoryScanProgress("可达性", 1, 1, "当前区域已解锁飞行，候选全部保留。"));
            var candidates = survey.Candidates
                .Select(candidate => candidate with
                {
                    Reachability = CandidateReachability.Flyable,
                    PathLengthMeters = null,
                })
                .ToList();
            return new TerritoryReachabilityResult(
                true,
                survey with
                {
                    ReachabilityMode = SurveyReachabilityMode.Flyable,
                    RawCandidateCount = rawCandidateCount,
                    ReachableCandidateCount = candidates.Count,
                    UnreachableCandidateCount = 0,
                    ReachabilityNote = "当前区域已解锁飞行，扫描候选全部保留。",
                    Candidates = candidates,
                },
                string.Empty);
        }

        if (playerSnapshot is null)
            return new TerritoryReachabilityResult(false, null, "当前区域不能飞，但无法读取玩家位置；为避免保留不可达候选，本次扫描未更新内存候选。");

        if (!navmeshReady)
            return new TerritoryReachabilityResult(false, null, "当前区域不能飞，但 vnavmesh 未就绪；为避免保留不可达候选，本次扫描未更新内存候选。");

        var reachable = new List<ApproachCandidate>();
        var unreachable = 0;
        var unavailable = 0;
        for (var index = 0; index < survey.Candidates.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (index % 10 == 0)
            {
                progress.Report(new TerritoryScanProgress(
                    "可达性",
                    index,
                    survey.Candidates.Count,
                    $"正在检查步行可达性：{index}/{survey.Candidates.Count}，保留 {reachable.Count} 个。"));
            }

            var candidate = survey.Candidates[index];
            var result = await navmeshQuery
                .QueryPathAsync(playerSnapshot.Position.ToVector3(), candidate.Position.ToVector3(), fly: false, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.IsReachable)
            {
                reachable.Add(candidate with
                {
                    Reachability = CandidateReachability.WalkReachable,
                    PathLengthMeters = result.PathLengthMeters,
                });
                continue;
            }

            if (result.Status == PathQueryStatus.Unavailable)
                unavailable++;
            else
                unreachable++;
        }

        if (unavailable > 0)
            return new TerritoryReachabilityResult(false, null, $"当前区域不能飞，可达性检查中有 {unavailable} 个候选无法查询；为避免保留不完整候选，本次扫描未更新内存候选。");

        progress.Report(new TerritoryScanProgress("可达性", survey.Candidates.Count, survey.Candidates.Count, $"步行可达性检查完成，保留 {reachable.Count} 个候选。"));
        return new TerritoryReachabilityResult(
            true,
            survey with
            {
                ReachabilityMode = SurveyReachabilityMode.WalkPath,
                ReachabilityOrigin = playerSnapshot.Position,
                RawCandidateCount = rawCandidateCount,
                ReachableCandidateCount = reachable.Count,
                UnreachableCandidateCount = unreachable,
                ReachabilityNote = $"当前区域不能飞，已从角色位置检查步行路径，保留可达候选 {reachable.Count} 个。",
                Candidates = reachable,
            },
            string.Empty);
    }

    private IReadOnlyList<SpotAnalysis> BuildExportAnalysesFromMaintenance(
        IReadOnlyList<TerritoryMaintenanceDocument> maintenanceDocuments)
    {
        var maintenanceByTerritory = maintenanceDocuments.ToDictionary(document => document.TerritoryId);
        return Catalog.Spots
            .OrderBy(target => target.TerritoryId)
            .ThenBy(target => target.FishingSpotId)
            .Select(target =>
            {
                maintenanceByTerritory.TryGetValue(target.TerritoryId, out var document);
                var maintenance = document?.Spots.FirstOrDefault(spot => spot.FishingSpotId == target.FishingSpotId);
                var confirmedCount = maintenance?.ApproachPoints.Count(point => point.Status == ApproachPointStatus.Confirmed) ?? 0;
                var reviewDecision = maintenance?.ReviewDecision ?? SpotReviewDecision.None;
                var status = reviewDecision.HasFlag(SpotReviewDecision.IgnoreSpot)
                    ? SpotAnalysisStatus.Ignored
                    : confirmedCount == 0
                        ? SpotAnalysisStatus.NeedsVisit
                        : confirmedCount >= 2 || reviewDecision.HasFlag(SpotReviewDecision.AllowWeakCoverageExport)
                            ? SpotAnalysisStatus.Confirmed
                            : SpotAnalysisStatus.WeakCoverage;

                return new SpotAnalysis
                {
                    Key = target.Key,
                    Status = status,
                    ConfirmedApproachPointCount = confirmedCount,
                };
            })
            .ToList();
    }

    private static SpotReviewDecision MergeReviewDecision(SpotReviewDecision existing, SpotReviewDecision added)
    {
        return (existing & ~SpotReviewDecision.IgnoreSpot) | added;
    }

    private static bool HasReviewDecision(SpotReviewDecision decisions, SpotReviewDecision flag)
    {
        return (decisions & flag) == flag;
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
        return maintenanceAnalysisBuilder.Analyze(
            target,
            GetSpotScanForTarget(target),
            GetMaintenanceRecord(target.Key),
            TryLoadLegacyReview(target.Key));
    }

    private CandidateSelection? BuildCandidateSelection(
        FishingSpotTarget target,
        SpotScanDocument scan,
        bool forceProbe = false)
    {
        var candidates = GetSelectableCandidatePool(scan, GetMaintenanceRecord(target.Key));
        if (candidates.Count == 0)
            return null;

        var playerSnapshot = GetPlayerSnapshot();
        var candidate = PickDefaultCandidate(candidates);
        return new CandidateSelection(
            candidate,
            GetCandidateSelectionMode(candidate),
            candidate.Reachability == CandidateReachability.Flyable,
            candidate.PathLengthMeters,
            playerSnapshot is null ? null : candidate.Position.HorizontalDistanceTo(playerSnapshot.Position),
            candidate.DistanceToTargetCenterMeters,
            candidates.Count,
            "当前候选来自领地内存候选；优先钓场范围内点，范围外点作为回退。");
    }

    private CandidateSelection? GetOrBuildCandidateSelection(
        FishingSpotTarget target,
        SpotScanDocument scan)
    {
        if (CurrentCandidateSelection is { } current
            && current.Candidate.Key == target.Key
            && scan.Candidates.Any(candidate => string.Equals(
                candidate.CandidateFingerprint,
                current.Candidate.CandidateFingerprint,
                StringComparison.Ordinal)))
            return current;

        return BuildCandidateSelection(target, scan);
    }

    private static IReadOnlyList<SpotCandidate> GetSelectableCandidatePool(
        SpotScanDocument scan,
        SpotMaintenanceRecord? maintenance)
    {
        var excludedFingerprints = GetRecordedCandidateFingerprints(maintenance);
        return scan.Candidates
            .Where(IsSelectableCandidate)
            .Where(candidate => !excludedFingerprints.Contains(candidate.CandidateFingerprint))
            .ToList();
    }

    private static SpotCandidate PickDefaultCandidate(IReadOnlyList<SpotCandidate> candidates)
    {
        return candidates
            .OrderByDescending(candidate => candidate.IsWithinTargetSearchRadius)
            .ThenBy(candidate => candidate.DistanceToTargetCenterMeters)
            .ThenBy(candidate => candidate.CandidateFingerprint, StringComparer.Ordinal)
            .First();
    }

    private static CandidateSelectionMode GetCandidateSelectionMode(SpotCandidate candidate)
    {
        return candidate.Reachability switch
        {
            CandidateReachability.Flyable => CandidateSelectionMode.FlyableDistance,
            CandidateReachability.WalkReachable => CandidateSelectionMode.WalkReachable,
            _ => CandidateSelectionMode.Filtered,
        };
    }

    private static IReadOnlySet<string> GetRecordedCandidateFingerprints(SpotMaintenanceRecord? maintenance)
    {
        var excludedFingerprints = maintenance?.ApproachPoints
            .Where(point => point.Status == ApproachPointStatus.Confirmed)
            .Select(point => point.SourceCandidateFingerprint)
            .Where(fingerprint => !string.IsNullOrWhiteSpace(fingerprint))
            .ToHashSet(StringComparer.Ordinal)
            ?? [];

        foreach (var evidence in maintenance?.Evidence ?? [])
        {
            if (evidence.EventType == SpotEvidenceEventType.Reject
                && !string.IsNullOrWhiteSpace(evidence.CandidateFingerprint))
                excludedFingerprints.Add(evidence.CandidateFingerprint);
        }

        return excludedFingerprints;
    }

    private static bool IsSelectableCandidate(SpotCandidate candidate)
    {
        return candidate.Status is not CandidateStatus.Ignored and not CandidateStatus.Quarantined
            && !string.IsNullOrWhiteSpace(candidate.CandidateFingerprint);
    }

    private static bool CanUseCandidateSelection(CandidateSelection? selection)
    {
        return selection is not null;
    }

    private void SyncCurrentAnalysis()
    {
        if (CurrentTarget is null)
        {
            CurrentAnalysis = null;
            CurrentScan = null;
            CurrentCandidateSelection = null;
            CurrentTargetBlocks = [];
            return;
        }

        var target = CurrentTarget!;
        CurrentAnalysis = Analyses.FirstOrDefault(analysis => analysis.Key == target.Key);
        CurrentScan = GetSpotScanForTarget(target);
        CurrentTargetBlocks = CurrentScan is null ? [] : BuildBlocksFromSpotCandidates(CurrentScan.Candidates);
        CurrentAnalysis = maintenanceAnalysisBuilder.Analyze(
            target,
            CurrentScan,
            GetMaintenanceRecord(target.Key),
            TryLoadLegacyReview(target.Key));
        CurrentCandidateSelection = CurrentScan is null
            ? null
            : GetOrBuildCandidateSelection(target, CurrentScan);
        ReplaceAnalysis(CurrentAnalysis);
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
        if (CurrentTarget is null)
        {
            CurrentCandidateSelection = null;
            return null;
        }

        var target = CurrentTarget!;
        var scan = GetSpotScanForTarget(target);
        if (scan is not null)
        {
            CurrentScan = scan;
            CurrentTargetBlocks = BuildBlocksFromSpotCandidates(scan.Candidates);
            return scan;
        }

        CurrentCandidateSelection = null;
        return null;
    }

    private FillBlockSelection? FindCastFillBlock(
        IReadOnlyList<SpotCandidate> candidates,
        Point3 playerPosition,
        float snapDistance)
    {
        var blocks = CurrentTargetBlocks.Count > 0
            ? CurrentTargetBlocks
            : BuildBlocksFromSpotCandidates(candidates);

        return FindCastFillBlock(blocks, playerPosition, snapDistance);
    }

    private FillBlockSelection? FindCastFillBlock(
        IReadOnlyList<SurveyBlock> blocks,
        Point3 playerPosition,
        float snapDistance)
    {
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

    private static IReadOnlySet<string> SelectCastFillCandidateIds(
        IReadOnlyDictionary<string, float> distances,
        float fillRange)
    {
        return distances
            .Where(item => item.Value <= fillRange)
            .Select(item => item.Key)
            .ToHashSet(StringComparer.Ordinal);
    }

    private IReadOnlyDictionary<string, float> CalculateCastFillDistances(
        IReadOnlyList<SurveyBlock> blocks,
        SurveyBlock fallbackBlock,
        ApproachCandidate seedCandidate)
    {
        var candidates = SelectCastFillGraphCandidates(blocks, fallbackBlock, seedCandidate);
        return CalculateCandidateGraphDistances(candidates, seedCandidate);
    }

    private static IReadOnlyList<ApproachCandidate> SelectCastFillGraphCandidates(
        IReadOnlyList<SurveyBlock> blocks,
        SurveyBlock fallbackBlock,
        ApproachCandidate seedCandidate)
    {
        if (!string.IsNullOrWhiteSpace(seedCandidate.SurfaceGroupId))
        {
            var surfaceCandidates = blocks
                .SelectMany(block => block.Candidates)
                .Where(candidate => string.Equals(candidate.SurfaceGroupId, seedCandidate.SurfaceGroupId, StringComparison.Ordinal))
                .ToList();
            if (surfaceCandidates.Count > 0)
                return surfaceCandidates;
        }

        return fallbackBlock.Candidates.ToList();
    }

    private IReadOnlyDictionary<string, float> CalculateCandidateGraphDistances(
        IReadOnlyList<ApproachCandidate> sourceCandidates,
        ApproachCandidate seedCandidate)
    {
        var candidates = sourceCandidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.CandidateId))
            .OrderBy(candidate => candidate.CandidateId, StringComparer.Ordinal)
            .ToList();
        if (candidates.Count == 0)
            return new Dictionary<string, float>(StringComparer.Ordinal);

        var seed = candidates.FirstOrDefault(candidate => string.Equals(candidate.CandidateId, seedCandidate.CandidateId, StringComparison.Ordinal))
            ?? candidates
                .OrderBy(candidate => candidate.Position.HorizontalDistanceTo(seedCandidate.Position))
                .ThenBy(candidate => candidate.CandidateId, StringComparer.Ordinal)
                .First();
        var distances = candidates.ToDictionary(candidate => candidate.CandidateId, _ => float.MaxValue, StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
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
            if (currentDistance == float.MaxValue)
                break;

            visited.Add(current.CandidateId);

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

        return distances;
    }

    private bool ShouldLinkBlockPortion(ApproachCandidate left, ApproachCandidate right)
    {
        return MathF.Abs(left.Position.Y - right.Position.Y) <= blockOptions.BlockHeightToleranceMeters
            && left.Position.HorizontalDistanceTo(right.Position) <= blockOptions.BlockLinkDistanceMeters;
    }

    private void ClearCurrentTerritoryRuntimeState()
    {
        CancelTerritoryScan(setMessage: false);
        CurrentTerritorySurvey = null;
        CurrentTerritoryBlocks = [];
        CurrentTerritoryTargets = [];
        CurrentTerritoryMaintenance = null;
        SelectedTerritoryId = 0;
        SelectedTerritoryName = string.Empty;
        Analyses = [];
        CurrentTarget = null;
        CurrentAnalysis = null;
        CurrentScan = null;
        CurrentCandidateSelection = null;
        CurrentTargetBlocks = [];
        NearbyDebugOverlay = null;
        LastCastPlaceNameId = 0;
        LastCastFishingSpotId = 0;
        LastCastRecordedCount = 0;
    }

    private void ClearCurrentTerritoryCandidateState()
    {
        CurrentTerritorySurvey = null;
        CurrentTerritoryBlocks = [];
        CurrentScan = null;
        CurrentCandidateSelection = null;
        CurrentTargetBlocks = [];
    }

    private void CancelTerritoryScan(bool setMessage)
    {
        if (territoryScanTask is null || territoryScanTask.IsCompleted)
            return;

        territoryScanCancellation?.Cancel();
        territoryScanCancelMessageRequested = setMessage;
        TerritoryScanProgress = new TerritoryScanProgress("取消", 0, 1, "正在取消后台扫描。");
        if (setMessage)
            LastMessage = "正在取消后台扫描。";
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

    private SpotScanDocument? GetSpotScanForTarget(FishingSpotTarget target)
    {
        return CreateScanFromTerritory(target);
    }

    private SpotScanDocument? CreateScanFromTerritory(FishingSpotTarget target)
    {
        if (!HasCurrentTerritorySurveyFor(target))
            return null;

        var scan = scanService.CreateSpotScan(target, Catalog.Spots, CurrentTerritorySurvey!);
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
            SurfaceGroupId = candidate.SurfaceGroupId,
            Position = candidate.Position,
            Rotation = candidate.Rotation,
            Status = candidate.Status,
            Reachability = candidate.Reachability,
            PathLengthMeters = candidate.PathLengthMeters,
            CreatedAt = candidate.CreatedAt,
        };
    }

    private SpotScanDocument? TryLoadLegacySpotScan(SpotKey key)
    {
        return File.Exists(store.GetLegacySpotScanPath(key)) ? store.LoadLegacySpotScan(key) : null;
    }

    private SpotReviewDocument? TryLoadLegacyReview(SpotKey key)
    {
        return File.Exists(store.GetLegacyReviewPath(key)) ? store.LoadLegacyReview(key) : null;
    }

    private int CountStatus(SpotAnalysisStatus status)
    {
        return Analyses.Count(analysis => analysis.Status == status);
    }

    private static string FormatPoint(Point3 point)
    {
        return $"{point.X:F2},{point.Y:F2},{point.Z:F2}";
    }

    private static string FormatNullableDistance(float? distance)
    {
        return distance is null
            ? "-"
            : distance.Value == float.MaxValue
                ? "inf"
                : distance.Value.ToString("F2");
    }

    private static string FormatSurfaceGroup(string surfaceGroupId)
    {
        return string.IsNullOrWhiteSpace(surfaceGroupId) ? "-" : surfaceGroupId;
    }

    private static string ShortId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "-";

        return value.Length <= 14 ? value : value[..14];
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

    private static uint GetTargetPlaceNameId(FishingSpotTarget target)
    {
        if (target.PlaceNameId != 0)
            return target.PlaceNameId;

        var spot = DService.Instance().Data.GetExcelSheet<FishingSpot>().GetRowOrDefault(target.FishingSpotId);
        return spot?.PlaceName.RowId ?? 0;
    }

    private FishingSpotTarget? ResolveCastTarget(uint castPlaceNameId, Point3 playerPosition, out string resolutionNote)
    {
        resolutionNote = string.Empty;
        EnsureCurrentTerritoryTargetList();
        var matches = CurrentTerritoryTargets
            .Where(target => GetTargetPlaceNameId(target) == castPlaceNameId)
            .ToList();

        if (matches.Count == 0)
        {
            LastMessage = $"检测到 PlaceName {castPlaceNameId} 抛竿，但当前区域目录没有匹配的 FishingSpot。请刷新目录后重试。";
            return null;
        }

        if (matches.Count == 1)
        {
            var target = matches[0];
            if (CurrentTarget?.Key != target.Key)
                resolutionNote = $"已自动选择 FishingSpot {target.FishingSpotId}；";

            return target;
        }

        var selected = matches
            .OrderBy(target => GetTargetCenterDistance(target, playerPosition))
            .ThenBy(target => target.FishingSpotId)
            .First();
        resolutionNote = $"PlaceName {castPlaceNameId} 匹配多个目标，已按玩家位置自动选择 FishingSpot {selected.FishingSpotId}；";
        return selected;
    }

    private static float GetTargetCenterDistance(FishingSpotTarget target, Point3 position)
    {
        return position.HorizontalDistanceTo(new Point3(target.WorldX, position.Y, target.WorldZ));
    }

    private void EnsureCurrentTerritoryTargetList()
    {
        var territoryId = CurrentTerritoryId;
        if (CurrentTerritoryTargets.Count == 0
            || CurrentTerritoryTargets.Any(target => target.TerritoryId != territoryId))
            RefreshCurrentTerritory();
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

    private sealed record TerritoryReachabilityResult(
        bool Success,
        TerritorySurveyDocument? Survey,
        string Message);
}

internal sealed record TerritoryMaintenanceSummary(
    uint TerritoryId,
    string TerritoryName,
    int SpotCount,
    int ConfirmedCount,
    int MaintenanceNeededCount,
    int WeakCoverageCount,
    int RiskCount,
    int IgnoredCount,
    bool HasScanCache,
    bool IsCurrentTerritory,
    bool IsSelected);

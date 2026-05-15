using Dalamud.Plugin.Services;
using FishingPointGenerator.Core;
using FishingPointGenerator.Core.Models;
using FishingPointGenerator.Plugin.Services.AutoSurvey;
using FishingPointGenerator.Plugin.Services.GameInteraction;
using FishingPointGenerator.Plugin.Services.Catalog;
using FishingPointGenerator.Plugin.Services.Scanning;
using Lumina.Excel.Sheets;
using OmenTools;
using System.Numerics;
using System.Threading;
using Action = System.Action;

namespace FishingPointGenerator.Plugin.Services;

internal sealed class SpotWorkflowSession : IDisposable
{
    private const float MinimumCastBlockSnapDistance = 1f;
    private const float MaximumCastBlockSnapDistance = 50f;
    private const float MinimumCastBlockFillRange = 1f;
    private const float MaximumCastBlockFillRange = 1000f;
    private const float MixedRiskCastBlockFillRangeMeters = 8f;
    private const float MixedRiskBoundaryDisableWidthMeters = 10f;
    private const float MixedRiskSeedResolveHorizontalToleranceMeters = 6f;
    private const float MixedRiskSeedResolveVerticalToleranceMeters = 2f;
    private const float CastWaterSystemLinkDistanceMeters = 15f;
    private const float CastWaterSystemHeightToleranceMeters = 3f;
    private const int FinalCandidateSparsifyMinimumBlockSize = 5;
    private const float FinalCandidateSparsifySpacingMeters = 4f;
    private const float FlyableMeshProbeHalfExtentXZ = 1f;
    private const float FlyableMeshProbeHalfExtentY = 1.5f;
    private const float FlyableMeshFloorProbeHeight = 2f;
    private const float FlyableMeshMaximumHorizontalSnap = 0.75f;
    private const float FlyableMeshMaximumVerticalSnap = 1.25f;
    private const float FormalScanNearbySummaryRadiusMeters = 35f;
    private const string ManualOverlayDisableNote = "manualOverlayDisable";
    private const string ManualOverlayDisablePreviousConfirmedNote = "manualOverlayDisablePreviousConfirmed";
    private const string ManualOverlayUndisableNote = "manualOverlayUndisable";

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
    private readonly PluginConfiguration configuration;
    private readonly Action saveConfiguration;
    private readonly IPluginLog pluginLog;
    private readonly AutoSurveyRunner autoSurveyRunner;
    private Task<TerritoryScanWorkResult>? territoryScanTask;
    private CancellationTokenSource? territoryScanCancellation;
    private bool territoryScanCancelMessageRequested;
    private int territoryScanGeneration;
    private bool disposed;
    private bool autoRecordCastsEnabled = true;
    private bool overlayEnabled = true;
    private bool overlayShowCandidates = true;
    private bool overlayShowTerritoryCache = true;
    private bool overlayShowTargetRadius = true;
    private bool overlayShowFishableDebug = true;
    private bool overlayShowWalkableDebug = true;
    private bool overlayPointDisableMode;
    private bool overlayPointDisableUiWindowVisible;
    private Vector2 overlayPointDisableUiWindowMin;
    private Vector2 overlayPointDisableUiWindowMax;
    private float castBlockSnapDistanceMeters = 6f;
    private float castBlockFillRangeMeters = 120f;
    private float overlayMaxDistanceMeters = 90f;
    private int overlayCandidateLimit = 160;

    public SpotWorkflowSession(
        PluginPaths paths,
        ICurrentTerritoryScanner scanner,
        PluginConfiguration configuration,
        Action saveConfiguration,
        IPluginLog pluginLog)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(saveConfiguration);
        ArgumentNullException.ThrowIfNull(pluginLog);

        this.configuration = configuration;
        this.saveConfiguration = saveConfiguration;
        this.pluginLog = pluginLog;
        store = new SpotJsonStore(paths.RootDirectory);
        maintenanceStore = new TerritoryMaintenanceStore(paths.RootDirectory);
        blockBuilder = new SurveyBlockBuilder(blockOptions);
        var geometryCache = new TerritoryGeometryCache(scanner);
        scanService = new SpotScanService(geometryCache);
        autoSurveyRunner = new AutoSurveyRunner(this, navmeshQuery, new PlayerFishingActionService());
        DataRoot = paths.DataDirectory;
        ScannerName = geometryCache.ScannerName;
        ApplyUiSettings(configuration);
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
    public bool AutoRecordCastsEnabled
    {
        get => autoRecordCastsEnabled;
        set
        {
            if (autoRecordCastsEnabled == value)
                return;

            autoRecordCastsEnabled = value;
            PersistUiSettings();
        }
    }

    public bool OverlayEnabled
    {
        get => overlayEnabled;
        set
        {
            if (overlayEnabled == value)
                return;

            overlayEnabled = value;
            PersistUiSettings();
        }
    }

    public bool OverlayShowCandidates
    {
        get => overlayShowCandidates;
        set
        {
            if (overlayShowCandidates == value)
                return;

            overlayShowCandidates = value;
            PersistUiSettings();
        }
    }

    public bool OverlayShowTerritoryCache
    {
        get => overlayShowTerritoryCache;
        set
        {
            if (overlayShowTerritoryCache == value)
                return;

            overlayShowTerritoryCache = value;
            PersistUiSettings();
        }
    }

    public bool OverlayShowTargetRadius
    {
        get => overlayShowTargetRadius;
        set
        {
            if (overlayShowTargetRadius == value)
                return;

            overlayShowTargetRadius = value;
            PersistUiSettings();
        }
    }

    public bool OverlayShowFishableDebug
    {
        get => overlayShowFishableDebug;
        set
        {
            if (overlayShowFishableDebug == value)
                return;

            overlayShowFishableDebug = value;
            PersistUiSettings();
        }
    }

    public bool OverlayShowWalkableDebug
    {
        get => overlayShowWalkableDebug;
        set
        {
            if (overlayShowWalkableDebug == value)
                return;

            overlayShowWalkableDebug = value;
            PersistUiSettings();
        }
    }

    public bool OverlayPointDisableMode
    {
        get => overlayPointDisableMode;
        set => overlayPointDisableMode = value;
    }

    public void SetOverlayPointDisableUiWindowVisible(bool visible)
    {
        overlayPointDisableUiWindowVisible = visible;
    }

    public void SetOverlayPointDisableUiWindowBounds(Vector2 min, Vector2 max)
    {
        overlayPointDisableUiWindowVisible = true;
        overlayPointDisableUiWindowMin = min;
        overlayPointDisableUiWindowMax = max;
    }

    public bool IsOverlayPointDisableMouseBlockedByUi(Vector2 mousePosition)
    {
        return overlayPointDisableUiWindowVisible
            && mousePosition.X >= overlayPointDisableUiWindowMin.X
            && mousePosition.Y >= overlayPointDisableUiWindowMin.Y
            && mousePosition.X <= overlayPointDisableUiWindowMax.X
            && mousePosition.Y <= overlayPointDisableUiWindowMax.Y;
    }

    public float CastBlockSnapDistanceMeters
    {
        get => castBlockSnapDistanceMeters;
        set
        {
            if (castBlockSnapDistanceMeters == value)
                return;

            castBlockSnapDistanceMeters = value;
            PersistUiSettings();
        }
    }

    public float CastBlockFillRangeMeters
    {
        get => castBlockFillRangeMeters;
        set
        {
            if (castBlockFillRangeMeters == value)
                return;

            castBlockFillRangeMeters = value;
            PersistUiSettings();
        }
    }

    public float OverlayMaxDistanceMeters
    {
        get => overlayMaxDistanceMeters;
        set
        {
            if (overlayMaxDistanceMeters == value)
                return;

            overlayMaxDistanceMeters = value;
            PersistUiSettings();
        }
    }

    public int OverlayCandidateLimit
    {
        get => overlayCandidateLimit;
        set
        {
            if (overlayCandidateLimit == value)
                return;

            overlayCandidateLimit = value;
            PersistUiSettings();
        }
    }
    public uint LastCastPlaceNameId { get; private set; }
    public uint LastCastFishingSpotId { get; private set; }
    public int LastCastRecordedCount { get; private set; }
    public int CastRecordVersion { get; private set; }
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
    public IReadOnlySet<string> CurrentTerritoryRecordedCandidateIds => GetRecordedCandidateIds(CurrentTerritoryMaintenance);
    public IReadOnlySet<string> CurrentTerritoryRecordedCandidateFingerprints => GetRecordedCandidateFingerprints(CurrentTerritoryMaintenance);
    public IReadOnlyList<SpotEvidenceEvent> CurrentEvidence => GetCurrentMaintenanceRecord()?.Evidence ?? [];
    public string CurrentTargetDisplayName => CurrentTarget is null
        ? string.Empty
        : $"{CurrentTarget.FishingSpotId} {CurrentTarget.Name}";
    public bool AutoSurveyRunning => autoSurveyRunner.IsRunning;
    public string AutoSurveyStatusText => autoSurveyRunner.StatusText;
    public string AutoSurveyCandidateText => autoSurveyRunner.CurrentCandidateText;
    public int AutoSurveyCompletedRounds => autoSurveyRunner.CompletedRounds;

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        autoSurveyRunner.Stop("自动点亮已因插件卸载停止。");
        ClearCurrentTerritoryRuntimeState();
        ReleaseTerritoryScanTask();
        TerritorySummaries = [];
        Catalog = new();
    }

    private void ApplyUiSettings(PluginConfiguration settings)
    {
        autoRecordCastsEnabled = settings.AutoRecordCastsEnabled;
        overlayEnabled = settings.OverlayEnabled;
        overlayShowCandidates = settings.OverlayShowCandidates;
        overlayShowTerritoryCache = settings.OverlayShowTerritoryCache;
        overlayShowTargetRadius = settings.OverlayShowTargetRadius;
        overlayShowFishableDebug = settings.OverlayShowFishableDebug;
        overlayShowWalkableDebug = settings.OverlayShowWalkableDebug;
        castBlockSnapDistanceMeters = settings.CastBlockSnapDistanceMeters;
        castBlockFillRangeMeters = settings.CastBlockFillRangeMeters;
        overlayMaxDistanceMeters = settings.OverlayMaxDistanceMeters;
        overlayCandidateLimit = settings.OverlayCandidateLimit;
    }

    private void PersistUiSettings()
    {
        if (disposed)
            return;

        try
        {
            configuration.AutoRecordCastsEnabled = autoRecordCastsEnabled;
            configuration.OverlayEnabled = overlayEnabled;
            configuration.OverlayShowCandidates = overlayShowCandidates;
            configuration.OverlayShowTerritoryCache = overlayShowTerritoryCache;
            configuration.OverlayShowTargetRadius = overlayShowTargetRadius;
            configuration.OverlayShowFishableDebug = overlayShowFishableDebug;
            configuration.OverlayShowWalkableDebug = overlayShowWalkableDebug;
            configuration.CastBlockSnapDistanceMeters = castBlockSnapDistanceMeters;
            configuration.CastBlockFillRangeMeters = castBlockFillRangeMeters;
            configuration.OverlayMaxDistanceMeters = overlayMaxDistanceMeters;
            configuration.OverlayCandidateLimit = overlayCandidateLimit;
            saveConfiguration();
        }
        catch
        {
        }
    }

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
        if (disposed)
            return;

        autoSurveyRunner.Stop("自动点亮已因切图停止。");
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
        LastMessage = $"已打开维护目标 FishingSpot {target.FishingSpotId}：{target.Name}。";
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
                LastMessage = CurrentTarget is null ? "当前区域没有可用目标。" : "没有待处理目标；已打开目录第一行。";
            return;
        }

        CurrentTarget = CurrentTerritoryTargets.FirstOrDefault(target => target.Key == next.Key);
        SyncCurrentAnalysis();
        if (setMessage && CurrentTarget is not null)
            LastMessage = $"已打开下一个维护目标 {CurrentTarget.FishingSpotId}：{CurrentTarget.Name}。";
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
        if (disposed)
            return;

        var task = territoryScanTask;
        if (task is null || !task.IsCompleted)
        {
            autoSurveyRunner.Poll();
            return;
        }

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
        autoSurveyRunner.Poll();
    }

    public void ScanCurrentTarget()
    {
        if (!EnsureCurrentTarget())
            return;

        var target = CurrentTarget!;
        var scan = CreateScanFromTerritory(target);
        if (scan is null)
        {
            LastMessage = "当前区域没有内存候选。请先扫描当前区域，再为维护目标派生候选。";
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
        RefreshCandidateSelection(includeRecordedUnresolvedRiskCandidates: false);
    }

    public void RefreshAutoSurveyCandidateSelection()
    {
        RefreshCandidateSelection(includeRecordedUnresolvedRiskCandidates: true);
    }

    private void RefreshCandidateSelection(bool includeRecordedUnresolvedRiskCandidates)
    {
        if (!EnsureCurrentTarget())
            return;

        var target = CurrentTarget!;
        var scan = EnsureScanForCurrentTarget();
        if (scan is null)
        {
            CurrentCandidateSelection = null;
            LastMessage = "维护目标没有可派生候选。请先扫描当前区域。";
            return;
        }

        var convergedRiskBlocks = includeRecordedUnresolvedRiskCandidates
            ? ConvergeCurrentMixedRiskBlocks(target, "autoSurveyPreSelectionMixedRiskConverged")
            : 0;
        if (convergedRiskBlocks > 0)
            scan = EnsureScanForCurrentTarget() ?? scan;

        CurrentCandidateSelection = BuildCandidateSelection(
            target,
            scan,
            forceProbe: true,
            includeRecordedUnresolvedRiskCandidates: includeRecordedUnresolvedRiskCandidates);
        CurrentAnalysis ??= BuildAnalysis(target);
        ReplaceAnalysis(CurrentAnalysis);
        var convergenceNote = convergedRiskBlocks > 0
            ? $"，已收敛风险记录 {convergedRiskBlocks} 个"
            : string.Empty;
        LastMessage = CurrentCandidateSelection is null
            ? $"FishingSpot {target.FishingSpotId} 没有可用候选{convergenceNote}。"
            : $"已刷新 FishingSpot {target.FishingSpotId} 的候选选择：{CurrentCandidateSelection.ModeText}{convergenceNote}。";
    }

    public void StartAutoSurveyOnce()
    {
        AutoRecordCastsEnabled = true;
        autoSurveyRunner.StartOnce();
        LastMessage = "已启动自动点亮一次。";
    }

    public void StartAutoSurveyLoop()
    {
        AutoRecordCastsEnabled = true;
        autoSurveyRunner.StartLoop();
        LastMessage = "已启动循环自动点亮。";
    }

    public void StopAutoSurvey()
    {
        autoSurveyRunner.Stop();
        LastMessage = autoSurveyRunner.StatusText;
    }

    public void DebugScanNearby(float radiusMeters)
    {
        try
        {
            var debugOverlay = scanService.DebugScanNearby(radiusMeters);
            NearbyDebugOverlay = AlignNearbyDebugCandidates(debugOverlay);
            OverlayEnabled = true;
            OverlayShowFishableDebug = true;
            OverlayShowWalkableDebug = true;
            LastMessage = NearbyDebugOverlay.Message;
            pluginLog.Information(
                "FPG nearby debug overlay candidates: territory={TerritoryId} radius={Radius:F1} overlayCandidates={OverlayCandidates} message=\"{Message}\"",
                NearbyDebugOverlay.TerritoryId,
                NearbyDebugOverlay.RadiusMeters,
                NearbyDebugOverlay.Candidates.Count,
                NearbyDebugOverlay.Message);
        }
        catch (Exception ex)
        {
            NearbyDebugOverlay = null;
            LastMessage = $"附近碰撞面调试失败：{ex.Message}";
        }
    }

    private NearbyScanDebugResult AlignNearbyDebugCandidates(NearbyScanDebugResult debugOverlay)
    {
        var playerPosition = Point3.From(debugOverlay.PlayerPosition);
        if (CurrentTerritorySurvey is { } survey && survey.TerritoryId == debugOverlay.TerritoryId)
        {
            var finalCandidateIds = survey.Candidates
                .Select(candidate => candidate.CandidateId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.Ordinal);
            var rawDebugMatches = debugOverlay.Candidates.Count(candidate => finalCandidateIds.Contains(candidate.CandidateId));
            var alignedCandidates = survey.Candidates
                .Where(candidate => candidate.Position.HorizontalDistanceTo(playerPosition) <= debugOverlay.RadiusMeters)
                .OrderBy(candidate => candidate.Position.HorizontalDistanceTo(playerPosition))
                .ThenBy(candidate => candidate.CandidateId, StringComparer.Ordinal)
                .ToList();
            var nearestFinalText = TryFindNearestCandidate(survey.Candidates, playerPosition, out var nearestFinal, out var nearestFinalDistance)
                ? $"{nearestFinalDistance:F1}m@({nearestFinal.Position.X:F2},{nearestFinal.Position.Y:F2},{nearestFinal.Position.Z:F2})"
                : "-";
            return debugOverlay with
            {
                Candidates = alignedCandidates,
                Message = AppendNote(
                    debugOverlay.Message,
                    $"候选显示已对齐当前正式扫缓存：附近最终候选 {alignedCandidates.Count}/{survey.Candidates.Count} 个，附近原始候选保留 {rawDebugMatches}/{debugOverlay.Candidates.Count} 个，最近正式候选 {nearestFinalText}。"),
            };
        }

        var rawCandidateCount = debugOverlay.Candidates.Count;
        if (rawCandidateCount == 0)
        {
            return debugOverlay with
            {
                Message = AppendNote(
                    debugOverlay.Message,
                    "当前没有正式扫缓存；附近原始候选为空。"),
            };
        }

        var blocks = blockBuilder.BuildBlocks(debugOverlay.Candidates);
        var finalBlocks = SparsifyFinalCandidateBlocks(blocks);
        var candidates = finalBlocks
            .SelectMany(block => block.Candidates)
            .OrderBy(candidate => candidate.Position.HorizontalDistanceTo(playerPosition))
            .ThenBy(candidate => candidate.CandidateId, StringComparer.Ordinal)
            .ToList();
        var dropped = Math.Max(0, rawCandidateCount - candidates.Count);
        return debugOverlay with
        {
            Candidates = candidates,
            Message = AppendNote(
                debugOverlay.Message,
                $"当前没有正式扫缓存；候选显示使用附近原始候选经正式分块和 {FinalCandidateSparsifySpacingMeters:F1}m 最终稀疏化后的结果：{candidates.Count}/{rawCandidateCount} 个，稀疏丢弃 {dropped} 个，未执行正式扫的 vnavmesh 可达性过滤。"),
        };
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
            LastMessage = "无法输出候选点调试：维护目标没有可派生候选。请先扫描当前区域。";
            return [LastMessage];
        }

        var blocks = BuildBlocksFromSpotCandidates(scan.Candidates);
        var recordedCandidateIds = CurrentTerritoryRecordedCandidateIds;
        var recordedCandidateFingerprints = CurrentTerritoryRecordedCandidateFingerprints;
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
            $"FPG candidate debug: territory={target.TerritoryId} spot={target.FishingSpotId} name=\"{target.Name}\" player=({FormatPoint(playerSnapshot.Position)}) radius={radiusMeters:F1} limit={limit} scanSource={scanSource} candidates={scan.Candidates.Count} targetRange={scan.Candidates.Count(candidate => candidate.IsWithinTargetSearchRadius)} blocks={blocks.Count} nearby={nearby.Count} confirmed={recordedCandidateIds.Count} snap={snapDistance:F1} fillRange={fillRange:F1}",
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
            var confirmedInBlock = selection.Block.Candidates.Count(candidate => recordedCandidateIds.Contains(candidate.CandidateId));
            var selectedBlockCount = blocks.Count(block => block.Candidates.Any(candidate => fillCandidateIds.Contains(candidate.CandidateId)));
            lines.Add($"FPG candidate debug selection: surface={FormatSurfaceGroup(selection.SeedCandidate.SurfaceGroupId)} block={selection.Block.BlockId} seed={ShortId(selection.SeedCandidate.CandidateId)} seedDistance={selection.Distance:F2} blockCandidates={selection.Block.Candidates.Count} graphCandidates={fillDistances.Count} fillCandidates={fillCandidateIds.Count} fillBlocks={selectedBlockCount} confirmedInBlock={confirmedInBlock}");
        }

        var index = 0;
        foreach (var item in nearby)
        {
            index++;
            var candidate = item.Candidate;
            var candidateId = GetSpotCandidateGraphId(candidate);
            blockByCandidateId.TryGetValue(candidateId, out var block);
            blockCandidateById.TryGetValue(candidateId, out var blockCandidate);
            var linkCount = block is not null && blockCandidate is not null
                ? block.Candidates.Count(next => !string.Equals(next.CandidateId, candidateId, StringComparison.Ordinal)
                    && ShouldLinkCastWaterSystem(blockCandidate, next))
                : 0;
            var pathDistance = fillDistances.TryGetValue(candidateId, out var distance)
                ? distance
                : (float?)null;
            var recorded = IsRecordedCandidate(candidate, recordedCandidateIds, recordedCandidateFingerprints);

            lines.Add(
                "FPG candidate debug item: "
                + $"#{index} dist={item.Distance:F2} block={block?.BlockId ?? "-"} "
                + $"surface={FormatSurfaceGroup(candidate.SurfaceGroupId)} "
                + $"fill={(fillCandidateIds.Contains(candidateId) ? "yes" : "no")} "
                + $"path={FormatNullableDistance(pathDistance)} "
                + $"confirmed={(recorded ? "yes" : "no")} "
                + $"targetRange={(candidate.IsWithinTargetSearchRadius ? "yes" : "no")} "
                + $"targetDistance={candidate.DistanceToTargetCenterMeters:F1} "
                + $"links={linkCount} status={candidate.Status} "
                + $"pos=({FormatPoint(candidate.Position)}) rot={candidate.Rotation:F3} "
                + $"fp={ShortId(candidate.CandidateFingerprint)} source={ShortId(candidate.SourceCandidateId)}");
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

    public void TeleportToCurrentTargetAetheryte()
    {
        if (!EnsureCurrentTarget())
            return;

        var target = CurrentTarget!;
        if (!AetheryteTeleporter.TryTeleportToNearestAetheryte(target, out var destination, out var error))
        {
            LastMessage = error;
            return;
        }

        LastMessage =
            $"已发起传送到 FishingSpot {target.FishingSpotId} 附近以太之光："
            + $"{destination.AetheryteId} {destination.Name}，地图距离约 {destination.MapDistance:F1}。";
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
            LastMessage = "维护目标没有可派生候选。请先扫描当前区域。";
            return;
        }

        var playerSnapshot = GetPlayerSnapshot();
        if (playerSnapshot is null)
        {
            LastMessage = "无法选择未记录/风险候选：没有可用的玩家位置。";
            return;
        }

        var candidatePool = GetSelectableCandidatePool(scan);
        var candidate = candidatePool.Count == 0
            ? null
            : PickDefaultCandidate(
                candidatePool,
                playerSnapshot.Position);
        if (candidate is null)
        {
            LastMessage = $"FishingSpot {target.FishingSpotId} 没有未记录或风险候选可插旗。";
            return;
        }

        if (!FlagPlacer.SetFlagFromWorld(
                target.TerritoryId,
                target.MapId,
                candidate.Position.X,
                candidate.Position.Z,
                $"{target.Name} 未记录/风险点"))
        {
            LastMessage = $"无法为 FishingSpot {target.FishingSpotId} 的未记录/风险候选插旗。";
            return;
        }

        LastMessage = $"已为 FishingSpot {target.FishingSpotId} 的领地未记录/风险候选插旗：距角色 {candidate.Position.HorizontalDistanceTo(playerSnapshot.Position):F1}m，距中心 {candidate.DistanceToTargetCenterMeters:F1}m。";
    }

    public bool RecordCastFill(uint castPlaceNameId)
    {
        if (disposed)
            return false;

        LastCastPlaceNameId = castPlaceNameId;
        LastCastFishingSpotId = 0;
        LastCastRecordedCount = 0;

        if (!AutoRecordCastsEnabled)
            return false;

        CastRecordVersion++;
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

        var maintenance = GetMaintenanceRecord(target.Key);
        var hasExistingRiskBlock = IsMixedRiskBlock(maintenance, selection.Block);
        var conflictSpotIds = hasExistingRiskBlock
            ? Array.Empty<uint>()
            : FindCastFillBlockConflictSpotIds(target, selection.Block);
        var usesRiskRange = hasExistingRiskBlock || conflictSpotIds.Count > 0;
        var effectiveFillRange = usesRiskRange
            ? Math.Min(fillRange, MixedRiskCastBlockFillRangeMeters)
            : fillRange;
        var markResult = MixedRiskBlockMarkResult.Empty;
        if (conflictSpotIds.Count > 0)
        {
            markResult = MarkMixedRiskBlock(
                target,
                selection.Block,
                conflictSpotIds,
                $"mixedRiskBlockMark placeName={castPlaceNameId} player={FormatPoint(playerSnapshot.Position)} waterSystem={FormatSurfaceGroup(selection.SeedCandidate.SurfaceGroupId)} block={selection.Block.BlockId}");
            maintenance = GetMaintenanceRecord(target.Key);
        }

        var candidatesByGraphId = scan.Candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(GetSpotCandidateGraphId(candidate)))
            .GroupBy(GetSpotCandidateGraphId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var fillDistances = CalculateCastFillDistances(CurrentTargetBlocks, selection.Block, selection.SeedCandidate);
        var fillCandidateIds = SelectCastFillCandidateIds(fillDistances, effectiveFillRange);
        var candidates = fillCandidateIds
            .Select(candidateId => candidatesByGraphId.TryGetValue(candidateId, out var spotCandidate)
                ? spotCandidate
                : null)
            .Where(candidate => candidate is not null)
            .Select(candidate => candidate!)
            .OrderBy(candidate => candidate.CandidateFingerprint, StringComparer.Ordinal)
            .ToList();
        if (candidates.Count == 0)
        {
            LastMessage = $"{resolutionNote}FishingSpot {fishingSpotId} 抛竿 Surface {FormatSurfaceGroup(selection.SeedCandidate.SurfaceGroupId)} 的本次水系范围没有可写候选点。";
            return true;
        }

        var existingCandidateIds = GetRecordedCandidateIds(maintenance, target.TerritoryId);
        var existingCandidateFingerprints = GetRecordedCandidateFingerprints(maintenance, target.TerritoryId);
        var note = $"autoFillFromCast placeName={castPlaceNameId} player={FormatPoint(playerSnapshot.Position)} waterSystem={FormatSurfaceGroup(selection.SeedCandidate.SurfaceGroupId)} block={selection.Block.BlockId} snap={snapDistance:F1} fillRange={effectiveFillRange:F1} requestedFillRange={fillRange:F1} mixedRisk={(usesRiskRange ? "true" : "false")} repeatEvidence=true";
        var newCandidates = candidates
            .Where(candidate => !IsRecordedCandidate(candidate, existingCandidateIds, existingCandidateFingerprints))
            .ToList();
        var repeatedCandidates = candidates.Count - newCandidates.Count;

        UpsertAutoCastFillApproachPoints(target, scan, candidates, note);
        var disabledBoundaryCandidates = usesRiskRange
            ? DisableMixedRiskBoundaryCandidates(
                target,
                scan,
                selection.Block,
                $"{note} boundaryDisableWidth={MixedRiskBoundaryDisableWidthMeters:F1}")
            : 0;
        var convergedRiskBlocks = usesRiskRange
            ? ConvergeMixedRiskBlock(
                target,
                selection.Block,
                $"{note} mixedRiskConverged")
            : 0;
        LastCastRecordedCount = candidates.Count;
        RebuildAnalyses();
        SyncCurrentAnalysis();
        var mixedRiskNote = conflictSpotIds.Count > 0
            ? $"，混合风险块小范围 {effectiveFillRange:F1}m，标记风险候选 {markResult.MarkedCandidateCount} 个，关联 FishingSpot {FormatSpotIds(markResult.RelatedFishingSpotIds)}"
            : hasExistingRiskBlock
                ? $"，已有风险候选小范围 {effectiveFillRange:F1}m"
            : string.Empty;
        var boundaryNote = disabledBoundaryCandidates > 0
            ? $"，自动屏蔽中线风险候选 {disabledBoundaryCandidates} 个"
            : string.Empty;
        var convergenceNote = convergedRiskBlocks > 0
            ? $"，风险块收敛结案"
            : string.Empty;
        LastMessage = $"{resolutionNote}FishingSpot {fishingSpotId} 抛竿水系点亮：{FormatSurfaceGroup(selection.SeedCandidate.SurfaceGroupId)} 新增 {newCandidates.Count} 个点，重复证据 {repeatedCandidates} 个（本次 {candidates.Count}/{fillDistances.Count}，seed 块 {selection.Block.BlockId}{mixedRiskNote}{boundaryNote}{convergenceNote}）。";
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

    public bool ToggleOverlayCandidateDisabled(ApproachCandidate candidate)
    {
        if (!SelectedTerritoryIsCurrent)
        {
            LastMessage = "当前选择区域不在游戏区域，不能点选维护 overlay 点。";
            return false;
        }

        if (CurrentTerritorySurvey is null || CurrentTerritorySurvey.TerritoryId != CurrentTerritoryId)
        {
            LastMessage = "当前区域没有可用于维护的 overlay 候选缓存。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(candidate.CandidateId))
        {
            LastMessage = "点选的 overlay 候选缺少候选标识，不能维护。";
            return false;
        }

        var undisabledCount = RemoveOverlayCandidateDisabled(candidate);
        if (undisabledCount > 0)
        {
            LastMessage = $"已取消禁用 overlay 候选 {ShortId(candidate.CandidateId)}：{undisabledCount} 个维护点。";
            return true;
        }

        if (!EnsureCurrentTarget())
            return false;
        if (!EnsureSelectedTargetIsCurrentTerritory())
            return false;

        var target = CurrentTarget!;
        var scan = EnsureScanForCurrentTarget();
        if (scan is null)
        {
            LastMessage = "当前维护目标没有可派生候选，不能禁用 overlay 点。";
            return false;
        }

        var eventId = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        var changed = false;
        UpdateMaintenanceSpot(target, spot =>
        {
            var points = spot.ApproachPoints.ToList();
            var pointId = SpotFingerprint.CreateApproachPointId(target.Key, candidate.Position, candidate.Rotation);
            var hadConfirmedPoint = points.Any(point =>
                string.Equals(point.PointId, pointId, StringComparison.Ordinal)
                && point.Status == ApproachPointStatus.Confirmed);
            var note = hadConfirmedPoint
                ? $"{ManualOverlayDisableNote}; {ManualOverlayDisablePreviousConfirmedNote}"
                : ManualOverlayDisableNote;
            if (!UpsertDisabledApproachPoint(target, points, candidate, scan, eventId, note, now))
                return spot;

            changed = true;
            var evidence = spot.Evidence.ToList();
            evidence.Add(new SpotEvidenceEvent
            {
                EventId = eventId,
                EventType = SpotEvidenceEventType.Review,
                Position = candidate.Position,
                Rotation = candidate.Rotation,
                CandidateFingerprint = candidate.CandidateId,
                SourceSurfaceGroupId = candidate.SurfaceGroupId,
                SourceScanId = scan.ScanId,
                SourceScannerVersion = scan.ScannerVersion,
                Note = ManualOverlayDisableNote,
                CreatedAt = now,
            });

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

        LastMessage = changed
            ? $"已禁用 FishingSpot {target.FishingSpotId} 的 overlay 候选 {ShortId(candidate.CandidateId)}。"
            : $"overlay 候选 {ShortId(candidate.CandidateId)} 已是禁用状态。";
        return changed;
    }

    public bool ToggleOverlayCandidatesDisabled(IReadOnlyList<ApproachCandidate> candidates)
    {
        var selected = candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.CandidateId))
            .GroupBy(candidate => candidate.CandidateId, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        if (selected.Count == 0)
        {
            LastMessage = "框选区域内没有可维护的 overlay 候选。";
            return false;
        }

        if (selected.Count == 1)
            return ToggleOverlayCandidateDisabled(selected[0]);

        if (!SelectedTerritoryIsCurrent)
        {
            LastMessage = "当前选择区域不在游戏区域，不能框选维护 overlay 点。";
            return false;
        }

        if (CurrentTerritorySurvey is null || CurrentTerritorySurvey.TerritoryId != CurrentTerritoryId)
        {
            LastMessage = "当前区域没有可用于维护的 overlay 候选缓存。";
            return false;
        }

        var territoryId = CurrentTerritorySurvey.TerritoryId != 0
            ? CurrentTerritorySurvey.TerritoryId
            : CurrentTerritoryId;
        var document = CurrentTerritoryMaintenance is { } current && current.TerritoryId == territoryId
            ? current
            : maintenanceStore.LoadTerritory(territoryId);
        var alreadyDisabled = selected
            .Where(candidate => IsOverlayCandidateDisabled(document, territoryId, candidate))
            .Select(candidate => candidate.CandidateId)
            .ToHashSet(StringComparer.Ordinal);
        if (alreadyDisabled.Count == selected.Count)
        {
            var restored = RemoveOverlayCandidatesDisabled(selected);
            LastMessage = restored > 0
                ? $"已框选取消禁用 overlay 候选：恢复 {restored} 个维护点（候选 {selected.Count} 个）。"
                : $"框选的 {selected.Count} 个 overlay 候选没有可恢复的禁用记录。";
            return restored > 0;
        }

        if (!EnsureCurrentTarget())
            return false;
        if (!EnsureSelectedTargetIsCurrentTerritory())
            return false;

        var target = CurrentTarget!;
        var scan = EnsureScanForCurrentTarget();
        if (scan is null)
        {
            LastMessage = "当前维护目标没有可派生候选，不能框选禁用 overlay 点。";
            return false;
        }

        var toDisable = selected
            .Where(candidate => !alreadyDisabled.Contains(candidate.CandidateId))
            .ToList();
        if (toDisable.Count == 0)
        {
            LastMessage = $"框选的 {selected.Count} 个 overlay 候选都已是禁用状态。";
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        var changedCount = 0;
        UpdateMaintenanceSpot(target, spot =>
        {
            var points = spot.ApproachPoints.ToList();
            var evidence = spot.Evidence.ToList();
            foreach (var candidate in toDisable)
            {
                var eventId = Guid.NewGuid().ToString("N");
                var pointId = SpotFingerprint.CreateApproachPointId(target.Key, candidate.Position, candidate.Rotation);
                var hadConfirmedPoint = points.Any(point =>
                    string.Equals(point.PointId, pointId, StringComparison.Ordinal)
                    && point.Status == ApproachPointStatus.Confirmed);
                var note = hadConfirmedPoint
                    ? $"{ManualOverlayDisableNote}; {ManualOverlayDisablePreviousConfirmedNote}"
                    : ManualOverlayDisableNote;
                if (!UpsertDisabledApproachPoint(target, points, candidate, scan, eventId, note, now))
                    continue;

                changedCount++;
                evidence.Add(new SpotEvidenceEvent
                {
                    EventId = eventId,
                    EventType = SpotEvidenceEventType.Review,
                    Position = candidate.Position,
                    Rotation = candidate.Rotation,
                    CandidateFingerprint = candidate.CandidateId,
                    SourceSurfaceGroupId = candidate.SurfaceGroupId,
                    SourceScanId = scan.ScanId,
                    SourceScannerVersion = scan.ScannerVersion,
                    Note = ManualOverlayDisableNote,
                    CreatedAt = now,
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
        });

        LastMessage = changedCount > 0
            ? $"已框选禁用 FishingSpot {target.FishingSpotId} 的 overlay 候选：新增屏蔽 {changedCount} 个（选择 {selected.Count}，跳过已屏蔽 {alreadyDisabled.Count}）。"
            : $"框选的 {selected.Count} 个 overlay 候选没有新增禁用记录。";
        return changedCount > 0;
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
        LastMessage = $"已导出 {export.Count} 个已确认点位。";
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

        ClearSpotMaintenance(CurrentTarget!.FishingSpotId);
    }

    public void ClearSpotMaintenance(uint fishingSpotId)
    {
        var target = CurrentTerritoryTargets.FirstOrDefault(target => target.FishingSpotId == fishingSpotId);
        if (target is null)
        {
            LastMessage = $"FishingSpot {fishingSpotId} 不在已选择领地 {SelectedTerritoryId}。";
            return;
        }

        var removedLedger = store.DeleteLegacyLedger(target.Key);
        var removedReview = store.DeleteLegacyReview(target.Key);
        UpdateMaintenanceSpot(target, _ => CreateMaintenanceSpot(target));
        if (CurrentTarget?.Key == target.Key)
        {
            CurrentCandidateSelection = CurrentScan is null
                ? null
                : GetOrBuildCandidateSelection(target, CurrentScan);
        }

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
                    SourceSurfaceGroupId = candidate.SurfaceGroupId,
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

    private IReadOnlyList<uint> FindCastFillBlockConflictSpotIds(
        FishingSpotTarget target,
        SurveyBlock block)
    {
        var territoryTargets = GetCatalogTerritoryTargets(target);
        var document = EnsureMaintenanceSpots(
            maintenanceStore.LoadTerritory(target.TerritoryId, target.TerritoryName),
            territoryTargets);
        var blockCandidateIds = GetBlockCandidateIds(block);
        var conflicts = new HashSet<uint>();
        var current = document.Spots.FirstOrDefault(spot => spot.FishingSpotId == target.FishingSpotId);
        if (current is not null)
            AddMixedRiskBlockConflictIds(conflicts, current, block, blockCandidateIds);

        foreach (var spot in document.Spots)
        {
            if (spot.FishingSpotId == target.FishingSpotId || HasReviewDecision(spot.ReviewDecision, SpotReviewDecision.IgnoreSpot))
                continue;

            if (HasConfirmedApproachPointInBlock(spot, target.TerritoryId, block, blockCandidateIds)
                || IsMixedRiskBlock(spot, block, blockCandidateIds))
            {
                conflicts.Add(spot.FishingSpotId);
                AddMixedRiskBlockConflictIds(conflicts, spot, block, blockCandidateIds);
            }
        }

        return conflicts
            .Where(id => id != 0 && id != target.FishingSpotId)
            .OrderBy(id => id)
            .ToList();
    }

    private MixedRiskBlockMarkResult MarkMixedRiskBlock(
        FishingSpotTarget target,
        SurveyBlock block,
        IReadOnlyList<uint> conflictSpotIds,
        string note)
    {
        var territoryTargets = GetCatalogTerritoryTargets(target);
        var document = EnsureMaintenanceSpots(
            maintenanceStore.LoadTerritory(target.TerritoryId, target.TerritoryName),
            territoryTargets);
        var blockCandidateIds = GetBlockCandidateIds(block);
        var involvedSpotIds = conflictSpotIds
            .Append(target.FishingSpotId)
            .Where(id => id != 0)
            .Distinct()
            .OrderBy(id => id)
            .ToList();
        if (involvedSpotIds.Count == 0)
            return MixedRiskBlockMarkResult.Empty;

        var now = DateTimeOffset.UtcNow;
        var markedCandidateCount = blockCandidateIds.Count;
        var touchedSpotIds = new List<uint>();
        var spots = document.Spots.ToList();
        for (var index = 0; index < spots.Count; index++)
        {
            var spot = spots[index];
            if (!involvedSpotIds.Contains(spot.FishingSpotId) || HasReviewDecision(spot.ReviewDecision, SpotReviewDecision.IgnoreSpot))
                continue;

            var conflictsForSpot = involvedSpotIds
                .Where(id => id != spot.FishingSpotId)
                .OrderBy(id => id)
                .ToList();

            var upsert = UpsertMixedRiskBlockRecord(
                spot.MixedRiskBlocks,
                block,
                blockCandidateIds,
                conflictsForSpot,
                0,
                now,
                note);
            if (!upsert.Changed)
                continue;

            touchedSpotIds.Add(spot.FishingSpotId);
            var evidence = spot.Evidence
                .Append(new SpotEvidenceEvent
                {
                    EventType = SpotEvidenceEventType.Review,
                    Position = block.Center,
                    SourceSurfaceGroupId = GetBlockSurfaceGroupId(block),
                    Note = $"{note} riskCandidates={blockCandidateIds.Count} conflicts={FormatSpotIds(conflictsForSpot)}",
                    CreatedAt = now,
                })
                .OrderBy(item => item.CreatedAt)
                .ThenBy(item => item.EventId, StringComparer.Ordinal)
                .ToList();

            spots[index] = spot with
            {
                ReviewDecision = spot.ReviewDecision | SpotReviewDecision.NeedsManualReview,
                ReviewNote = string.IsNullOrWhiteSpace(spot.ReviewNote) ? "mixedRiskBlock" : spot.ReviewNote,
                MixedRiskBlocks = upsert.Records,
                Evidence = evidence,
                UpdatedAt = now,
            };
        }

        if (touchedSpotIds.Count == 0)
            return new MixedRiskBlockMarkResult(0, involvedSpotIds);

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
        return new MixedRiskBlockMarkResult(markedCandidateCount, involvedSpotIds);
    }

    private int DisableMixedRiskBoundaryCandidates(
        FishingSpotTarget target,
        SpotScanDocument scan,
        SurveyBlock block,
        string note)
    {
        var territoryTargets = GetCatalogTerritoryTargets(target);
        var document = EnsureMaintenanceSpots(
            maintenanceStore.LoadTerritory(target.TerritoryId, target.TerritoryName),
            territoryTargets);
        var blockCandidateIds = GetBlockCandidateIds(block);
        var involvedSpotIds = FindMixedRiskBlockSpotIds(document, target.FishingSpotId, block, blockCandidateIds);
        if (involvedSpotIds.Count < 2)
            return 0;

        var blockCandidates = block.Candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.CandidateId))
            .OrderBy(candidate => candidate.CandidateId, StringComparer.Ordinal)
            .ToList();
        if (blockCandidates.Count == 0)
            return 0;

        var seedsBySpot = BuildMixedRiskSeedCandidatesBySpot(
            document,
            involvedSpotIds,
            target.TerritoryId,
            block,
            blockCandidateIds,
            blockCandidates);
        if (seedsBySpot.Count < 2)
            return 0;

        var distancesBySpot = seedsBySpot
            .ToDictionary(
                item => item.Key,
                item => CalculateCandidateGraphDistances(blockCandidates, item.Value));
        var candidatesToDisable = blockCandidates
            .Where(candidate => IsMixedRiskBoundaryCandidate(candidate, distancesBySpot))
            .OrderBy(candidate => candidate.CandidateId, StringComparer.Ordinal)
            .ToList();
        if (candidatesToDisable.Count == 0)
            return 0;

        var now = DateTimeOffset.UtcNow;
        var changedCandidateIds = new HashSet<string>(StringComparer.Ordinal);
        var spots = document.Spots.ToList();
        for (var index = 0; index < spots.Count; index++)
        {
            var spot = spots[index];
            if (!involvedSpotIds.Contains(spot.FishingSpotId) || HasReviewDecision(spot.ReviewDecision, SpotReviewDecision.IgnoreSpot))
                continue;

            var spotTarget = territoryTargets.FirstOrDefault(item => item.FishingSpotId == spot.FishingSpotId)
                ?? target with { FishingSpotId = spot.FishingSpotId, Name = spot.Name };
            var points = spot.ApproachPoints.ToList();
            var evidence = spot.Evidence.ToList();
            var changedForSpot = 0;
            foreach (var candidate in candidatesToDisable)
            {
                var eventId = Guid.NewGuid().ToString("N");
                if (!UpsertDisabledApproachPoint(spotTarget, points, candidate, scan, eventId, note, now))
                    continue;

                changedForSpot++;
                changedCandidateIds.Add(candidate.CandidateId);
                evidence.Add(new SpotEvidenceEvent
                {
                    EventId = eventId,
                    EventType = SpotEvidenceEventType.Review,
                    Position = candidate.Position,
                    Rotation = candidate.Rotation,
                    CandidateFingerprint = candidate.CandidateId,
                    SourceSurfaceGroupId = candidate.SurfaceGroupId,
                    SourceScanId = scan.ScanId,
                    SourceScannerVersion = scan.ScannerVersion,
                    Note = note,
                    CreatedAt = now,
                });
            }

            if (changedForSpot == 0)
                continue;

            spots[index] = spot with
            {
                ReviewDecision = spot.ReviewDecision | SpotReviewDecision.NeedsManualReview,
                ReviewNote = string.IsNullOrWhiteSpace(spot.ReviewNote) ? "mixedRiskBoundaryDisabled" : spot.ReviewNote,
                ApproachPoints = points
                    .OrderBy(point => point.PointId, StringComparer.Ordinal)
                    .ToList(),
                Evidence = evidence
                    .OrderBy(item => item.CreatedAt)
                    .ThenBy(item => item.EventId, StringComparer.Ordinal)
                    .ToList(),
                UpdatedAt = now,
            };
        }

        if (changedCandidateIds.Count == 0)
            return 0;

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

        return changedCandidateIds.Count;
    }

    private int ConvergeMixedRiskBlock(
        FishingSpotTarget target,
        SurveyBlock block,
        string note)
    {
        var territoryTargets = GetCatalogTerritoryTargets(target);
        var document = EnsureMaintenanceSpots(
            maintenanceStore.LoadTerritory(target.TerritoryId, target.TerritoryName),
            territoryTargets);
        var blockCandidateIds = GetBlockCandidateIds(block);
        var involvedSpotIds = FindMixedRiskBlockSpotIds(document, target.FishingSpotId, block, blockCandidateIds);
        if (involvedSpotIds.Count < 2)
            return 0;

        var blockCandidates = block.Candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.CandidateId))
            .OrderBy(candidate => candidate.CandidateId, StringComparer.Ordinal)
            .ToList();
        if (blockCandidates.Count == 0
            || !AreMixedRiskBlockCandidatesResolved(document, involvedSpotIds, target.TerritoryId, blockCandidates))
            return 0;

        var now = DateTimeOffset.UtcNow;
        var spots = document.Spots.ToList();
        var resolvedRecords = 0;
        for (var index = 0; index < spots.Count; index++)
        {
            var spot = spots[index];
            if (!involvedSpotIds.Contains(spot.FishingSpotId) || HasReviewDecision(spot.ReviewDecision, SpotReviewDecision.IgnoreSpot))
                continue;

            var remainingRecords = spot.MixedRiskBlocks
                .Where(record => !IsMixedRiskBlockRecordForBlock(record, block, blockCandidateIds))
                .ToList();
            if (remainingRecords.Count == spot.MixedRiskBlocks.Count)
                continue;

            resolvedRecords += spot.MixedRiskBlocks.Count - remainingRecords.Count;
            var evidence = spot.Evidence
                .Append(new SpotEvidenceEvent
                {
                    EventType = SpotEvidenceEventType.Review,
                    Position = block.Center,
                    SourceSurfaceGroupId = GetBlockSurfaceGroupId(block),
                    Note = $"{note} resolvedRiskCandidates={blockCandidates.Count}",
                    CreatedAt = now,
                })
                .OrderBy(item => item.CreatedAt)
                .ThenBy(item => item.EventId, StringComparer.Ordinal)
                .ToList();

            spots[index] = spot with
            {
                ReviewDecision = remainingRecords.Count == 0
                    ? spot.ReviewDecision & ~SpotReviewDecision.NeedsManualReview
                    : spot.ReviewDecision,
                MixedRiskBlocks = SortMixedRiskBlockRecords(remainingRecords),
                Evidence = evidence,
                UpdatedAt = now,
            };
        }

        if (resolvedRecords == 0)
            return 0;

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

        return resolvedRecords;
    }

    private int ConvergeCurrentMixedRiskBlocks(
        FishingSpotTarget target,
        string note)
    {
        var maintenance = GetMaintenanceRecord(target.Key);
        if (maintenance is null || maintenance.MixedRiskBlocks.Count == 0 || CurrentTargetBlocks.Count == 0)
            return 0;

        var resolvedRecords = 0;
        foreach (var block in CurrentTargetBlocks.Where(block => IsMixedRiskBlock(maintenance, block)).ToList())
            resolvedRecords += ConvergeMixedRiskBlock(target, block, note);

        if (resolvedRecords == 0)
            return 0;

        RebuildAnalyses();
        SyncCurrentAnalysis();
        return resolvedRecords;
    }

    private static bool AreMixedRiskBlockCandidatesResolved(
        TerritoryMaintenanceDocument document,
        IReadOnlyList<uint> involvedSpotIds,
        uint territoryId,
        IReadOnlyList<ApproachCandidate> blockCandidates)
    {
        return blockCandidates.All(candidate =>
            CountCandidateConfirmedSpots(document, involvedSpotIds, territoryId, candidate) == 1
            || IsCandidateDisabledByAnySpot(document, involvedSpotIds, territoryId, candidate));
    }

    private static int CountCandidateConfirmedSpots(
        TerritoryMaintenanceDocument document,
        IReadOnlyList<uint> involvedSpotIds,
        uint territoryId,
        ApproachCandidate candidate)
    {
        return document.Spots
            .Where(spot => involvedSpotIds.Contains(spot.FishingSpotId))
            .Count(spot => spot.ApproachPoints
                .Where(point => point.Status == ApproachPointStatus.Confirmed)
                .Any(point => IsApproachPointForCandidate(point, territoryId, candidate)));
    }

    private static bool IsCandidateDisabledByAnySpot(
        TerritoryMaintenanceDocument document,
        IReadOnlyList<uint> involvedSpotIds,
        uint territoryId,
        ApproachCandidate candidate)
    {
        return document.Spots
            .Where(spot => involvedSpotIds.Contains(spot.FishingSpotId))
            .SelectMany(spot => spot.ApproachPoints)
            .Where(IsEffectiveDisabledApproachPoint)
            .Any(point => IsApproachPointForCandidate(point, territoryId, candidate));
    }

    private static bool IsApproachPointForCandidate(
        ApproachPoint point,
        uint territoryId,
        ApproachCandidate candidate)
    {
        var candidateId = candidate.CandidateId;
        if (string.IsNullOrWhiteSpace(candidateId))
            return false;

        if (string.Equals(point.SourceCandidateId, candidateId, StringComparison.Ordinal)
            || string.Equals(point.SourceCandidateFingerprint, candidateId, StringComparison.Ordinal))
            return true;

        var effectiveTerritoryId = candidate.TerritoryId != 0 ? candidate.TerritoryId : territoryId;
        if (effectiveTerritoryId == 0)
            return false;

        var candidateFingerprint = SpotFingerprint.CreateTerritoryCandidateFingerprint(
            effectiveTerritoryId,
            candidate.Position,
            candidate.Rotation);
        if (string.Equals(point.SourceCandidateId, candidateFingerprint, StringComparison.Ordinal)
            || string.Equals(point.SourceCandidateFingerprint, candidateFingerprint, StringComparison.Ordinal))
            return true;

        var pointFingerprint = SpotFingerprint.CreateTerritoryCandidateFingerprint(
            effectiveTerritoryId,
            point.Position,
            point.Rotation);
        return string.Equals(pointFingerprint, candidateId, StringComparison.Ordinal)
            || string.Equals(pointFingerprint, candidateFingerprint, StringComparison.Ordinal);
    }

    private static IReadOnlyList<uint> FindMixedRiskBlockSpotIds(
        TerritoryMaintenanceDocument document,
        uint currentFishingSpotId,
        SurveyBlock block,
        IReadOnlySet<string> blockCandidateIds)
    {
        var involved = new HashSet<uint>();
        foreach (var spot in document.Spots)
        {
            if (!IsMixedRiskBlock(spot, block, blockCandidateIds))
                continue;

            if (spot.FishingSpotId != 0)
                involved.Add(spot.FishingSpotId);
            foreach (var record in spot.MixedRiskBlocks.Where(record => IsMixedRiskBlockRecordForBlock(record, block, blockCandidateIds)))
            {
                foreach (var fishingSpotId in record.ConflictingFishingSpotIds)
                {
                    if (fishingSpotId != 0)
                        involved.Add(fishingSpotId);
                }
            }
        }

        if (currentFishingSpotId != 0 && involved.Count > 0)
            involved.Add(currentFishingSpotId);

        return involved
            .OrderBy(id => id)
            .ToList();
    }

    private static Dictionary<uint, List<ApproachCandidate>> BuildMixedRiskSeedCandidatesBySpot(
        TerritoryMaintenanceDocument document,
        IReadOnlyList<uint> involvedSpotIds,
        uint territoryId,
        SurveyBlock block,
        IReadOnlySet<string> blockCandidateIds,
        IReadOnlyList<ApproachCandidate> blockCandidates)
    {
        var candidatesById = blockCandidates
            .GroupBy(candidate => candidate.CandidateId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var result = new Dictionary<uint, List<ApproachCandidate>>();
        foreach (var spot in document.Spots)
        {
            if (!involvedSpotIds.Contains(spot.FishingSpotId))
                continue;

            var seeds = new List<ApproachCandidate>();
            foreach (var point in spot.ApproachPoints.Where(point => point.Status == ApproachPointStatus.Confirmed))
            {
                if (!IsApproachPointInBlock(point, territoryId, block, blockCandidateIds))
                    continue;
                if (!TryResolveApproachPointCandidate(point, territoryId, candidatesById, blockCandidates, out var candidate))
                    continue;
                if (seeds.Any(existing => string.Equals(existing.CandidateId, candidate.CandidateId, StringComparison.Ordinal)))
                    continue;

                seeds.Add(candidate);
            }

            if (seeds.Count > 0)
                result[spot.FishingSpotId] = seeds;
        }

        return result;
    }

    private static bool TryResolveApproachPointCandidate(
        ApproachPoint point,
        uint territoryId,
        IReadOnlyDictionary<string, ApproachCandidate> candidatesById,
        IReadOnlyList<ApproachCandidate> blockCandidates,
        out ApproachCandidate candidate)
    {
        candidate = null!;
        if (!string.IsNullOrWhiteSpace(point.SourceCandidateId)
            && candidatesById.TryGetValue(point.SourceCandidateId, out var sourceCandidate))
        {
            candidate = sourceCandidate;
            return true;
        }
        if (!string.IsNullOrWhiteSpace(point.SourceCandidateFingerprint)
            && candidatesById.TryGetValue(point.SourceCandidateFingerprint, out var sourceFingerprintCandidate))
        {
            candidate = sourceFingerprintCandidate;
            return true;
        }
        if (territoryId != 0)
        {
            var territoryFingerprint = SpotFingerprint.CreateTerritoryCandidateFingerprint(
                territoryId,
                point.Position,
                point.Rotation);
            if (candidatesById.TryGetValue(territoryFingerprint, out var territoryCandidate))
            {
                candidate = territoryCandidate;
                return true;
            }
        }

        candidate = blockCandidates
            .Where(item => MathF.Abs(item.Position.Y - point.Position.Y) <= MixedRiskSeedResolveVerticalToleranceMeters)
            .Where(item => item.Position.HorizontalDistanceTo(point.Position) <= MixedRiskSeedResolveHorizontalToleranceMeters)
            .OrderBy(item => item.Position.HorizontalDistanceTo(point.Position))
            .ThenBy(item => item.CandidateId, StringComparer.Ordinal)
            .FirstOrDefault()!;
        return candidate is not null;
    }

    private static bool IsMixedRiskBoundaryCandidate(
        ApproachCandidate candidate,
        IReadOnlyDictionary<uint, IReadOnlyDictionary<string, float>> distancesBySpot)
    {
        var nearest = distancesBySpot
            .Select(item => item.Value.TryGetValue(candidate.CandidateId, out var distance)
                ? distance
                : float.MaxValue)
            .Where(distance => distance < float.MaxValue)
            .OrderBy(distance => distance)
            .Take(2)
            .ToList();
        return nearest.Count >= 2
            && MathF.Abs(nearest[0] - nearest[1]) <= MixedRiskBoundaryDisableWidthMeters;
    }

    private IReadOnlyList<FishingSpotTarget> GetCatalogTerritoryTargets(FishingSpotTarget target)
    {
        if (Catalog.Spots.Count == 0)
            return [target];

        return Catalog.Spots
            .Where(item => item.TerritoryId == target.TerritoryId)
            .OrderBy(item => item.FishingSpotId)
            .ToList();
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
                SourceSurfaceGroupId = candidate.SurfaceGroupId,
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

    private int RemoveOverlayCandidateDisabled(ApproachCandidate candidate)
    {
        var territoryId = candidate.TerritoryId != 0 ? candidate.TerritoryId : CurrentTerritoryId;
        if (territoryId == 0)
            return 0;

        var document = CurrentTerritoryMaintenance is { } current && current.TerritoryId == territoryId
            ? current
            : maintenanceStore.LoadTerritory(territoryId);
        if (document.Spots.Count == 0)
            return 0;

        var candidateIds = GetOverlayCandidateMatchIds(candidate, territoryId);
        if (candidateIds.Count == 0)
            return 0;

        var now = DateTimeOffset.UtcNow;
        var changedCount = 0;
        var spots = document.Spots.ToList();
        for (var index = 0; index < spots.Count; index++)
        {
            var spot = spots[index];
            var changedSpot = false;
            var points = new List<ApproachPoint>();
            foreach (var point in spot.ApproachPoints)
            {
                if (!IsEffectiveDisabledApproachPoint(point)
                    || !IsApproachPointForOverlayCandidate(point, territoryId, candidateIds))
                {
                    points.Add(point);
                    continue;
                }

                changedSpot = true;
                changedCount++;
                if (!string.IsNullOrWhiteSpace(point.Note)
                    && point.Note.Contains(ManualOverlayDisablePreviousConfirmedNote, StringComparison.Ordinal))
                {
                    points.Add(point with
                    {
                        Status = ApproachPointStatus.Confirmed,
                        UpdatedAt = now,
                        Note = AppendNote(point.Note, ManualOverlayUndisableNote),
                    });
                }
            }

            if (!changedSpot)
                continue;

            var evidence = spot.Evidence.ToList();
            evidence.Add(new SpotEvidenceEvent
            {
                EventType = SpotEvidenceEventType.Review,
                Position = candidate.Position,
                Rotation = candidate.Rotation,
                CandidateFingerprint = candidate.CandidateId,
                SourceSurfaceGroupId = candidate.SurfaceGroupId,
                Note = ManualOverlayUndisableNote,
                CreatedAt = now,
            });

            spots[index] = spot with
            {
                ApproachPoints = points
                    .OrderBy(point => point.PointId, StringComparer.Ordinal)
                    .ToList(),
                Evidence = evidence
                    .OrderBy(item => item.CreatedAt)
                    .ThenBy(item => item.EventId, StringComparer.Ordinal)
                    .ToList(),
                UpdatedAt = now,
            };
        }

        if (changedCount == 0)
            return 0;

        document = document with
        {
            UpdatedAt = now,
            Spots = spots
                .OrderBy(spot => spot.FishingSpotId)
                .ToList(),
        };
        maintenanceStore.SaveTerritory(document);
        if (SelectedTerritoryId == territoryId)
            CurrentTerritoryMaintenance = document;

        RebuildTerritorySummaries();
        RebuildAnalyses();
        SyncCurrentAnalysis();
        return changedCount;
    }

    private int RemoveOverlayCandidatesDisabled(IReadOnlyList<ApproachCandidate> candidates)
    {
        var territoryId = candidates
            .Select(candidate => candidate.TerritoryId)
            .FirstOrDefault(id => id != 0);
        if (territoryId == 0)
            territoryId = CurrentTerritoryId;
        if (territoryId == 0)
            return 0;

        var document = CurrentTerritoryMaintenance is { } current && current.TerritoryId == territoryId
            ? current
            : maintenanceStore.LoadTerritory(territoryId);
        if (document.Spots.Count == 0)
            return 0;

        var matches = candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.CandidateId))
            .GroupBy(candidate => candidate.CandidateId, StringComparer.Ordinal)
            .Select(group => group.First())
            .Select(candidate => new OverlayCandidateMatch(candidate, GetOverlayCandidateMatchIds(candidate, territoryId)))
            .Where(match => match.CandidateIds.Count > 0)
            .ToList();
        if (matches.Count == 0)
            return 0;

        var now = DateTimeOffset.UtcNow;
        var changedCount = 0;
        var spots = document.Spots.ToList();
        for (var index = 0; index < spots.Count; index++)
        {
            var spot = spots[index];
            var changedSpot = false;
            var points = new List<ApproachPoint>();
            var evidence = spot.Evidence.ToList();
            foreach (var point in spot.ApproachPoints)
            {
                var matchedCandidate = FindOverlayCandidateMatch(point, territoryId, matches);
                if (!IsEffectiveDisabledApproachPoint(point) || matchedCandidate is null)
                {
                    points.Add(point);
                    continue;
                }

                changedSpot = true;
                changedCount++;
                if (!string.IsNullOrWhiteSpace(point.Note)
                    && point.Note.Contains(ManualOverlayDisablePreviousConfirmedNote, StringComparison.Ordinal))
                {
                    points.Add(point with
                    {
                        Status = ApproachPointStatus.Confirmed,
                        UpdatedAt = now,
                        Note = AppendNote(point.Note, ManualOverlayUndisableNote),
                    });
                }

                evidence.Add(new SpotEvidenceEvent
                {
                    EventType = SpotEvidenceEventType.Review,
                    Position = matchedCandidate.Position,
                    Rotation = matchedCandidate.Rotation,
                    CandidateFingerprint = matchedCandidate.CandidateId,
                    SourceSurfaceGroupId = matchedCandidate.SurfaceGroupId,
                    Note = ManualOverlayUndisableNote,
                    CreatedAt = now,
                });
            }

            if (!changedSpot)
                continue;

            spots[index] = spot with
            {
                ApproachPoints = points
                    .OrderBy(point => point.PointId, StringComparer.Ordinal)
                    .ToList(),
                Evidence = evidence
                    .OrderBy(item => item.CreatedAt)
                    .ThenBy(item => item.EventId, StringComparer.Ordinal)
                    .ToList(),
                UpdatedAt = now,
            };
        }

        if (changedCount == 0)
            return 0;

        document = document with
        {
            UpdatedAt = now,
            Spots = spots
                .OrderBy(spot => spot.FishingSpotId)
                .ToList(),
        };
        maintenanceStore.SaveTerritory(document);
        if (SelectedTerritoryId == territoryId)
            CurrentTerritoryMaintenance = document;

        RebuildTerritorySummaries();
        RebuildAnalyses();
        SyncCurrentAnalysis();
        return changedCount;
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
                SourceSurfaceGroupId = candidate.SurfaceGroupId,
                SourceBlockId = candidate.BlockId,
                SourceScanId = scan.ScanId,
                SourceScannerVersion = scan.ScannerVersion,
            });
    }

    private static bool UpsertDisabledApproachPoint(
        FishingSpotTarget target,
        List<ApproachPoint> points,
        ApproachCandidate candidate,
        SpotScanDocument scan,
        string evidenceId,
        string note,
        DateTimeOffset now)
    {
        var pointId = SpotFingerprint.CreateApproachPointId(target.Key, candidate.Position, candidate.Rotation);
        var existingIndex = points.FindIndex(point => string.Equals(point.PointId, pointId, StringComparison.Ordinal));
        if (existingIndex >= 0)
        {
            var existing = points[existingIndex];
            if (existing.Status == ApproachPointStatus.Disabled
                && existing.SourceKind != ApproachPointSourceKind.AutoCastFill)
                return false;

            points[existingIndex] = existing with
            {
                Status = ApproachPointStatus.Disabled,
                SourceKind = existing.SourceKind == ApproachPointSourceKind.AutoCastFill
                    ? ApproachPointSourceKind.Candidate
                    : existing.SourceKind,
                SourceCandidateFingerprint = string.IsNullOrWhiteSpace(existing.SourceCandidateFingerprint)
                    ? candidate.CandidateId
                    : existing.SourceCandidateFingerprint,
                SourceCandidateId = string.IsNullOrWhiteSpace(existing.SourceCandidateId)
                    ? candidate.CandidateId
                    : existing.SourceCandidateId,
                SourceSurfaceGroupId = string.IsNullOrWhiteSpace(existing.SourceSurfaceGroupId)
                    ? candidate.SurfaceGroupId
                    : existing.SourceSurfaceGroupId,
                SourceBlockId = string.IsNullOrWhiteSpace(existing.SourceBlockId)
                    ? candidate.BlockId
                    : existing.SourceBlockId,
                SourceScanId = string.IsNullOrWhiteSpace(existing.SourceScanId)
                    ? scan.ScanId
                    : existing.SourceScanId,
                SourceScannerVersion = string.IsNullOrWhiteSpace(existing.SourceScannerVersion)
                    ? scan.ScannerVersion
                    : existing.SourceScannerVersion,
                EvidenceIds = existing.EvidenceIds
                    .Append(evidenceId)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToList(),
                UpdatedAt = now,
                Note = AppendNote(existing.Note, note),
            };
            return true;
        }

        points.Add(new ApproachPoint
        {
            PointId = pointId,
            Position = candidate.Position,
            Rotation = candidate.Rotation,
            Status = ApproachPointStatus.Disabled,
            SourceKind = ApproachPointSourceKind.Candidate,
            SourceCandidateFingerprint = candidate.CandidateId,
            SourceCandidateId = candidate.CandidateId,
            SourceSurfaceGroupId = candidate.SurfaceGroupId,
            SourceBlockId = candidate.BlockId,
            SourceScanId = scan.ScanId,
            SourceScannerVersion = scan.ScannerVersion,
            EvidenceIds = string.IsNullOrWhiteSpace(evidenceId) ? [] : [evidenceId],
            CreatedAt = now,
            UpdatedAt = now,
            Note = note,
        });
        return true;
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
                SourceKind = existing.SourceKind == ApproachPointSourceKind.Manual
                    && source.SourceKind != ApproachPointSourceKind.Manual
                        ? source.SourceKind
                        : existing.SourceKind,
                SourceCandidateFingerprint = string.IsNullOrWhiteSpace(existing.SourceCandidateFingerprint)
                    ? source.SourceCandidateFingerprint
                    : existing.SourceCandidateFingerprint,
                SourceCandidateId = string.IsNullOrWhiteSpace(existing.SourceCandidateId)
                    ? source.SourceCandidateId
                    : existing.SourceCandidateId,
                SourceSurfaceGroupId = string.IsNullOrWhiteSpace(existing.SourceSurfaceGroupId)
                    ? source.SourceSurfaceGroupId
                    : existing.SourceSurfaceGroupId,
                SourceBlockId = string.IsNullOrWhiteSpace(existing.SourceBlockId)
                    ? source.SourceBlockId
                    : existing.SourceBlockId,
                SourceScanId = string.IsNullOrWhiteSpace(existing.SourceScanId)
                    ? source.SourceScanId
                    : existing.SourceScanId,
                SourceScannerVersion = string.IsNullOrWhiteSpace(existing.SourceScannerVersion)
                    ? source.SourceScannerVersion
                    : existing.SourceScannerVersion,
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
            SourceSurfaceGroupId = source.SourceSurfaceGroupId,
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
            LastMessage = "维护目标没有可派生候选。请先扫描当前区域。";
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
            LastMessage = "维护目标没有可用候选。";
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

        LastMessage = "维护目标不在当前游戏区域，不能记录玩家当前位置。";
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
            spot = RemoveLegacyDisabledAutoCastFillPoints(spot);
            spots.Add(spot);
        }

        foreach (var orphaned in document.Spots.Where(spot => !targets.Any(target => target.FishingSpotId == spot.FishingSpotId)))
            spots.Add(RemoveLegacyDisabledAutoCastFillPoints(orphaned));

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

    private static SpotMaintenanceRecord RemoveLegacyDisabledAutoCastFillPoints(SpotMaintenanceRecord spot)
    {
        if (!spot.ApproachPoints.Any(point =>
                point.Status == ApproachPointStatus.Disabled
                && point.SourceKind == ApproachPointSourceKind.AutoCastFill))
            return spot;

        return spot with
        {
            ApproachPoints = spot.ApproachPoints
                .Where(point => point.Status != ApproachPointStatus.Disabled
                    || point.SourceKind != ApproachPointSourceKind.AutoCastFill)
                .OrderBy(point => point.PointId, StringComparer.Ordinal)
                .ToList(),
            UpdatedAt = DateTimeOffset.UtcNow,
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
                        return ApplyMaintenanceMixedRisk(
                            target,
                            maintenanceAnalysisBuilder.Analyze(target, spotScan, spot, null),
                            maintenance,
                            spot?.ReviewDecision ?? SpotReviewDecision.None);
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
            LogFormalScanCandidateStage(capture.TerritoryId, "raw", scannedSurvey.Candidates, playerSnapshot);

            var rawCandidates = scannedSurvey.Candidates.Count;
            progress.Report(new TerritoryScanProgress("分块", 0, 1, $"正在为 {rawCandidates} 个候选构建块。"));
            var blocks = blockBuilder.BuildBlocks(
                scannedSurvey.Candidates,
                CreateBlockBuildProgressAdapter(progress, "分块", "初次分块："),
                cancellationToken);
            var blockedSurvey = scannedSurvey with
            {
                Candidates = blocks.SelectMany(block => block.Candidates).ToList(),
            };
            LogFormalScanCandidateStage(capture.TerritoryId, "blocked", blockedSurvey.Candidates, playerSnapshot);

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
            LogFormalScanCandidateStage(capture.TerritoryId, "reachable", reachability.Survey.Candidates, playerSnapshot);
            var filteredCandidateLabel = reachability.Survey.ReachabilityMode == SurveyReachabilityMode.NotChecked
                ? "缓存候选"
                : "可用候选";
            progress.Report(new TerritoryScanProgress("分块", 0, 1, $"正在为 {reachability.Survey.Candidates.Count} 个{filteredCandidateLabel}重建块。"));
            var filteredBlocks = blockBuilder.BuildBlocks(
                reachability.Survey.Candidates,
                CreateBlockBuildProgressAdapter(progress, "分块", $"{filteredCandidateLabel}重分块："),
                cancellationToken);
            var filteredCandidateCount = filteredBlocks.Sum(block => block.Candidates.Count);
            var finalBlocks = SparsifyFinalCandidateBlocks(filteredBlocks);
            var finalCandidates = finalBlocks.SelectMany(block => block.Candidates).ToList();
            var finalCandidateCount = finalCandidates.Count;
            var finalReachableCandidateCount = reachability.Survey.ReachabilityMode == SurveyReachabilityMode.NotChecked
                ? reachability.Survey.ReachableCandidateCount
                : finalCandidates.Count(candidate => candidate.Reachability != CandidateReachability.Unknown);
            var sparseDropped = Math.Max(0, filteredCandidateCount - finalCandidateCount);
            var finalSurvey = reachability.Survey with
            {
                Candidates = finalCandidates,
                ReachableCandidateCount = finalReachableCandidateCount,
                ReachabilityNote = sparseDropped == 0
                    ? reachability.Survey.ReachabilityNote
                    : AppendNote(
                        reachability.Survey.ReachabilityNote,
                        $"最终稀疏化按块内 {FinalCandidateSparsifySpacingMeters:F1}m 间距丢弃过密候选 {sparseDropped} 个。"),
            };
            LogFormalScanCandidateStage(capture.TerritoryId, "final", finalSurvey.Candidates, playerSnapshot);
            var message = $"已扫描区域 {finalSurvey.TerritoryId}：原始 {finalSurvey.RawCandidateCount} 个，缓存 {finalSurvey.Candidates.Count} 个，丢弃不可达 {finalSurvey.UnreachableCandidateCount} 个，最终稀疏丢弃 {sparseDropped} 个，{finalBlocks.Count} 个块。{finalSurvey.ReachabilityNote}";
            progress.Report(new TerritoryScanProgress("完成", 1, 1, message));
            return new TerritoryScanWorkResult(true, finalSurvey, finalBlocks, message);
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

    private static IProgress<SurveyBlockBuildProgress> CreateBlockBuildProgressAdapter(
        IProgress<TerritoryScanProgress> progress,
        string stage,
        string prefix)
    {
        return new BlockBuildProgressAdapter(progress, stage, prefix);
    }

    private sealed class BlockBuildProgressAdapter(
        IProgress<TerritoryScanProgress> progress,
        string stage,
        string prefix) : IProgress<SurveyBlockBuildProgress>
    {
        public void Report(SurveyBlockBuildProgress value)
        {
            progress.Report(new TerritoryScanProgress(
                stage,
                value.Current,
                value.Total,
                $"{prefix}{value.Message}"));
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

        if (!navmeshReady)
        {
            var mode = currentTerritoryFlyable ? "可飞" : "不能飞";
            return new TerritoryReachabilityResult(false, null, $"当前区域{mode}，但 vnavmesh 未就绪；为避免保留不可达候选，本次扫描未更新内存候选。");
        }

        if (currentTerritoryFlyable)
        {
            var candidates1 = new List<ApproachCandidate>(survey.Candidates.Count);
            var flyable1 = 0;
            var meshMiss1 = 0;
            var snapRejected1 = 0;
            var unavailable1 = 0;
            for (var index = 0; index < survey.Candidates.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (index % 64 == 0)
                {
                    progress.Report(new TerritoryScanProgress(
                        "可达性",
                        index,
                        survey.Candidates.Count,
                        $"正在检查飞行落点 mesh：{index}/{survey.Candidates.Count}，标记可飞 {flyable1} 个，丢弃 {meshMiss1 + snapRejected1} 个。"));
                }

                var candidate = survey.Candidates[index];
                var result = navmeshQuery.QueryLandingPoint(
                    candidate.Position.ToVector3(),
                    FlyableMeshFloorProbeHeight,
                    FlyableMeshProbeHalfExtentXZ,
                    FlyableMeshProbeHalfExtentY);
                cancellationToken.ThrowIfCancellationRequested();
                if (result.IsReachable)
                {
                    if (IsCloseEnoughToCandidateMesh(candidate.Position.ToVector3(), result.Point))
                    {
                        candidates1.Add(candidate with
                        {
                            Reachability = CandidateReachability.Flyable,
                            PathLengthMeters = null,
                        });
                        flyable1++;
                        continue;
                    }

                    snapRejected1++;
                    continue;
                }

                if (result.Status == PathQueryStatus.Unavailable)
                {
                    unavailable1++;
                    continue;
                }

                meshMiss1++;
            }

            if (unavailable1 > 0)
                return new TerritoryReachabilityResult(false, null, $"当前区域可飞，飞行落点 mesh 检查中有 {unavailable1} 个候选无法查询；为避免保留不完整候选，本次扫描未更新内存候选。");

            if (flyable1 == 0)
            {
                progress.Report(new TerritoryScanProgress("可达性", survey.Candidates.Count, survey.Candidates.Count, $"飞行落点 mesh 检查没有标记可飞候选，已丢弃 {meshMiss1 + snapRejected1} 个候选。"));
                return new TerritoryReachabilityResult(
                    true,
                    survey with
                    {
                        ReachabilityMode = SurveyReachabilityMode.Flyable,
                        ReachabilityOrigin = playerSnapshot?.Position,
                        RawCandidateCount = rawCandidateCount,
                        ReachableCandidateCount = 0,
                        UnreachableCandidateCount = meshMiss1 + snapRejected1,
                        ReachabilityNote = $"当前区域已解锁飞行，但 vnavmesh landing mesh 检查没有标记任何候选可飞；已丢弃无落点 mesh {meshMiss1} 个、吸附偏移过大 {snapRejected1} 个。",
                        Candidates = [],
                    },
                    string.Empty);
            }

            progress.Report(new TerritoryScanProgress("可达性", survey.Candidates.Count, survey.Candidates.Count, $"飞行落点 mesh 检查完成，标记可飞 {flyable1} 个，丢弃 {meshMiss1 + snapRejected1} 个。"));
            return new TerritoryReachabilityResult(
                true,
                survey with
                {
                    ReachabilityMode = SurveyReachabilityMode.Flyable,
                    ReachabilityOrigin = playerSnapshot?.Position,
                    RawCandidateCount = rawCandidateCount,
                    ReachableCandidateCount = flyable1,
                    UnreachableCandidateCount = meshMiss1 + snapRejected1,
                    ReachabilityNote = $"当前区域已解锁飞行，已用 vnavmesh landing mesh 检查候选：标记可飞 {flyable1} 个，丢弃无落点 mesh {meshMiss1} 个、吸附偏移过大 {snapRejected1} 个。",
                    Candidates = candidates1,
                },
                string.Empty);
        }

        if (playerSnapshot is null)
        {
            return new TerritoryReachabilityResult(false, null, "当前区域不能飞，但无法读取玩家位置；为避免保留不可达候选，本次扫描未更新内存候选。");
        }

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

        if (reachable.Count == 0)
            return CreateUncheckedReachabilityFallback(
                survey,
                rawCandidateCount,
                $"当前区域不能飞，但从角色当前位置未找到可达候选；已保留几何扫描候选 {survey.Candidates.Count} 个，后续按人工抛竿确认。");

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

    private static TerritoryReachabilityResult CreateUncheckedReachabilityFallback(
        TerritorySurveyDocument survey,
        int rawCandidateCount,
        string note)
    {
        return new TerritoryReachabilityResult(
            true,
            survey with
            {
                ReachabilityMode = SurveyReachabilityMode.NotChecked,
                RawCandidateCount = rawCandidateCount,
                ReachableCandidateCount = 0,
                UnreachableCandidateCount = 0,
                ReachabilityNote = note,
                Candidates = survey.Candidates
                    .Select(candidate => candidate with
                    {
                        Reachability = CandidateReachability.Unknown,
                        PathLengthMeters = null,
                    })
                    .ToList(),
            },
            string.Empty);
    }

    private static IReadOnlyList<SurveyBlock> SparsifyFinalCandidateBlocks(IReadOnlyList<SurveyBlock> blocks)
    {
        if (blocks.Count == 0)
            return [];

        return blocks
            .Select(SparsifyFinalCandidateBlock)
            .ToList();
    }

    private static SurveyBlock SparsifyFinalCandidateBlock(SurveyBlock block)
    {
        if (block.Candidates.Count < FinalCandidateSparsifyMinimumBlockSize)
            return block;

        var candidates = block.Candidates
            .GroupBy(candidate => candidate.SurfaceGroupId, StringComparer.Ordinal)
            .SelectMany(group =>
            {
                var groupCandidates = group.ToList();
                return groupCandidates.Count < FinalCandidateSparsifyMinimumBlockSize
                    ? groupCandidates
                    : SparsifyFinalCandidates(
                        groupCandidates,
                        CalculateCandidateCenter(groupCandidates),
                        FinalCandidateSparsifySpacingMeters);
            })
            .OrderBy(candidate => candidate.CandidateId, StringComparer.Ordinal)
            .ToList();
        return candidates.Count == block.Candidates.Count
            ? block
            : block with { Candidates = candidates };
    }

    private static IReadOnlyList<ApproachCandidate> SparsifyFinalCandidates(
        IReadOnlyList<ApproachCandidate> candidates,
        Point3 center,
        float spacing)
    {
        var kept = new List<ApproachCandidate>();
        var cells = new Dictionary<CandidateSparseGridCell, List<ApproachCandidate>>();
        var spacingSquared = spacing * spacing;
        foreach (var candidate in candidates
            .OrderBy(candidate => HorizontalDistanceSquared(candidate.Position, center))
            .ThenBy(candidate => candidate.CandidateId, StringComparer.Ordinal))
        {
            if (HasKeptCandidateWithin(candidate.Position, cells, spacing, spacingSquared))
                continue;

            kept.Add(candidate);
            AddKeptCandidate(candidate, cells, spacing);
        }

        return kept
            .OrderBy(candidate => candidate.CandidateId, StringComparer.Ordinal)
            .ToList();
    }

    private static bool HasKeptCandidateWithin(
        Point3 position,
        IReadOnlyDictionary<CandidateSparseGridCell, List<ApproachCandidate>> cells,
        float spacing,
        float spacingSquared)
    {
        var cell = CandidateSparseGridCell.From(position, spacing);
        for (var x = cell.X - 1; x <= cell.X + 1; x++)
        {
            for (var z = cell.Z - 1; z <= cell.Z + 1; z++)
            {
                if (!cells.TryGetValue(new CandidateSparseGridCell(x, z), out var candidates))
                    continue;

                if (candidates.Any(candidate => HorizontalDistanceSquared(candidate.Position, position) < spacingSquared))
                    return true;
            }
        }

        return false;
    }

    private static void AddKeptCandidate(
        ApproachCandidate candidate,
        Dictionary<CandidateSparseGridCell, List<ApproachCandidate>> cells,
        float spacing)
    {
        var cell = CandidateSparseGridCell.From(candidate.Position, spacing);
        if (!cells.TryGetValue(cell, out var candidates))
        {
            candidates = [];
            cells[cell] = candidates;
        }

        candidates.Add(candidate);
    }

    private static Point3 CalculateCandidateCenter(IReadOnlyList<ApproachCandidate> candidates)
    {
        if (candidates.Count == 0)
            return default;

        var x = 0f;
        var y = 0f;
        var z = 0f;
        foreach (var candidate in candidates)
        {
            x += candidate.Position.X;
            y += candidate.Position.Y;
            z += candidate.Position.Z;
        }

        return new Point3(x / candidates.Count, y / candidates.Count, z / candidates.Count);
    }

    private static float HorizontalDistanceSquared(Point3 left, Point3 right)
    {
        var dx = left.X - right.X;
        var dz = left.Z - right.Z;
        return (dx * dx) + (dz * dz);
    }

    private void LogFormalScanCandidateStage(
        uint territoryId,
        string stage,
        IReadOnlyList<ApproachCandidate> candidates,
        PlayerSnapshot? playerSnapshot)
    {
        if (playerSnapshot is null)
        {
            pluginLog.Information(
                "FPG formal scan candidate stage: territory={TerritoryId} stage={Stage} candidates={Candidates} nearOrigin=- origin=- nearest=-",
                territoryId,
                stage,
                candidates.Count);
            return;
        }

        var origin = playerSnapshot.Position;
        var nearbyCount = candidates.Count(candidate =>
            candidate.Position.HorizontalDistanceTo(origin) <= FormalScanNearbySummaryRadiusMeters);
        if (!TryFindNearestCandidate(candidates, origin, out var nearest, out var nearestDistance))
        {
            pluginLog.Information(
                "FPG formal scan candidate stage: territory={TerritoryId} stage={Stage} candidates={Candidates} nearOrigin={NearbyCount}/{Radius:F1} origin=({OriginX:F2},{OriginY:F2},{OriginZ:F2}) nearest=-",
                territoryId,
                stage,
                candidates.Count,
                nearbyCount,
                FormalScanNearbySummaryRadiusMeters,
                origin.X,
                origin.Y,
                origin.Z);
            return;
        }

        pluginLog.Information(
            "FPG formal scan candidate stage: territory={TerritoryId} stage={Stage} candidates={Candidates} nearOrigin={NearbyCount}/{Radius:F1} origin=({OriginX:F2},{OriginY:F2},{OriginZ:F2}) nearest={NearestDistance:F1} nearestPos=({NearestX:F2},{NearestY:F2},{NearestZ:F2}) nearestReachability={NearestReachability}",
            territoryId,
            stage,
            candidates.Count,
            nearbyCount,
            FormalScanNearbySummaryRadiusMeters,
            origin.X,
            origin.Y,
            origin.Z,
            nearestDistance,
            nearest.Position.X,
            nearest.Position.Y,
            nearest.Position.Z,
            nearest.Reachability);
    }

    private static bool TryFindNearestCandidate(
        IReadOnlyList<ApproachCandidate> candidates,
        Point3 origin,
        out ApproachCandidate nearest,
        out float nearestDistance)
    {
        nearest = null!;
        nearestDistance = float.MaxValue;
        foreach (var candidate in candidates)
        {
            var distance = candidate.Position.HorizontalDistanceTo(origin);
            if (distance >= nearestDistance)
                continue;

            nearest = candidate;
            nearestDistance = distance;
        }

        return nearest is not null;
    }

    private static bool IsCloseEnoughToCandidateMesh(Vector3 candidate, Vector3 meshPoint)
    {
        var dx = candidate.X - meshPoint.X;
        var dz = candidate.Z - meshPoint.Z;
        var horizontalDistance = MathF.Sqrt((dx * dx) + (dz * dz));
        return horizontalDistance <= FlyableMeshMaximumHorizontalSnap
            && MathF.Abs(candidate.Y - meshPoint.Y) <= FlyableMeshMaximumVerticalSnap;
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

                var analysis = new SpotAnalysis
                {
                    Key = target.Key,
                    Status = status,
                    ConfirmedApproachPointCount = confirmedCount,
                };
                return ApplyMaintenanceMixedRisk(target, analysis, document, reviewDecision);
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
        var maintenance = GetMaintenanceRecord(target.Key);
        return ApplyMaintenanceMixedRisk(
            target,
            maintenanceAnalysisBuilder.Analyze(
                target,
                GetSpotScanForTarget(target),
                maintenance,
                TryLoadLegacyReview(target.Key)),
            CurrentTerritoryMaintenance,
            maintenance?.ReviewDecision ?? SpotReviewDecision.None);
    }

    private static SpotAnalysis ApplyMaintenanceMixedRisk(
        FishingSpotTarget target,
        SpotAnalysis analysis,
        TerritoryMaintenanceDocument? maintenanceDocument,
        SpotReviewDecision reviewDecision)
    {
        if (analysis.Status == SpotAnalysisStatus.Ignored)
            return analysis;

        var relatedSpotIds = FindMaintenanceMixedRiskSpotIds(target.Key, maintenanceDocument);
        if (relatedSpotIds.Count == 0)
            return analysis;

        var messages = analysis.Messages.ToList();
        messages.Add($"已确认候选或水系也被 FishingSpot {string.Join(", ", relatedSpotIds)} 记录。");
        return analysis with
        {
            Status = HasReviewDecision(reviewDecision, SpotReviewDecision.AllowRiskExport)
                ? analysis.Status
                : SpotAnalysisStatus.MixedRisk,
            HasMixedRisk = true,
            Messages = messages
                .Distinct(StringComparer.Ordinal)
                .ToList(),
        };
    }

    private static IReadOnlyList<uint> FindMaintenanceMixedRiskSpotIds(
        SpotKey key,
        TerritoryMaintenanceDocument? maintenanceDocument)
    {
        var current = maintenanceDocument?.Spots.FirstOrDefault(spot => spot.FishingSpotId == key.FishingSpotId);
        if (current is null)
            return [];

        var territoryId = maintenanceDocument!.TerritoryId != 0 ? maintenanceDocument.TerritoryId : key.TerritoryId;
        var sourceCandidateIds = GetRecordedCandidateIds(current, territoryId);
        var sourceMixedRiskCandidateIds = current.MixedRiskBlocks
            .SelectMany(record => record.CandidateIds)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        var sourceMixedRiskBlockIds = current.MixedRiskBlocks
            .Select(record => record.BlockId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        var explicitRelatedSpotIds = current.MixedRiskBlocks
            .SelectMany(record => record.ConflictingFishingSpotIds)
            .Where(id => id != 0 && id != key.FishingSpotId)
            .ToHashSet();
        if (sourceCandidateIds.Count == 0
            && sourceMixedRiskCandidateIds.Count == 0
            && sourceMixedRiskBlockIds.Count == 0
            && explicitRelatedSpotIds.Count == 0)
            return [];

        return maintenanceDocument.Spots
            .Where(spot => spot.FishingSpotId != key.FishingSpotId)
            .Where(spot =>
            {
                if (explicitRelatedSpotIds.Contains(spot.FishingSpotId))
                    return true;

                var otherCandidateIds = GetRecordedCandidateIds(spot, territoryId);
                var otherMixedRiskBlocks = spot.MixedRiskBlocks;
                return otherCandidateIds.Any(id => sourceCandidateIds.Contains(id))
                    || otherMixedRiskBlocks.Any(record =>
                        record.ConflictingFishingSpotIds.Contains(key.FishingSpotId)
                        || (!string.IsNullOrWhiteSpace(record.BlockId) && sourceMixedRiskBlockIds.Contains(record.BlockId))
                        || record.CandidateIds.Any(id => sourceCandidateIds.Contains(id) || sourceMixedRiskCandidateIds.Contains(id)));
            })
            .Select(spot => spot.FishingSpotId)
            .Distinct()
            .OrderBy(id => id)
            .ToList();
    }

    private CandidateSelection? BuildCandidateSelection(
        FishingSpotTarget target,
        SpotScanDocument scan,
        bool forceProbe = false,
        bool includeRecordedUnresolvedRiskCandidates = false)
    {
        var unresolvedRiskCandidateIds = includeRecordedUnresolvedRiskCandidates
            ? GetUnresolvedMixedRiskCandidateIds(scan)
            : new HashSet<string>(StringComparer.Ordinal);
        var candidates = GetSelectableCandidatePool(scan, unresolvedRiskCandidateIds);
        if (candidates.Count == 0)
            return null;

        var playerSnapshot = GetPlayerSnapshot();
        var candidate = PickDefaultCandidate(
            candidates,
            playerSnapshot?.Position,
            unresolvedRiskCandidateIds);
        var unresolvedRiskCandidateCount = unresolvedRiskCandidateIds.Count == 0
            ? 0
            : candidates.Count(item => IsCandidateKeyMatched(item, unresolvedRiskCandidateIds));
        var note = unresolvedRiskCandidateCount > 0
            ? $"自动点亮候选来自领地内存候选；优先未结案风险候选 {unresolvedRiskCandidateCount} 个，再按距玩家排序；仍排除禁用和拒绝点。"
            : "当前候选来自领地内存候选；排除当前钓场已记录、禁用和拒绝点，当前钓场风险候选允许重新点亮，再按距玩家排序。";
        return new CandidateSelection(
            candidate,
            GetCandidateSelectionMode(candidate),
            candidate.Reachability == CandidateReachability.Flyable,
            candidate.PathLengthMeters,
            playerSnapshot is null ? null : candidate.Position.HorizontalDistanceTo(playerSnapshot.Position),
            candidate.DistanceToTargetCenterMeters,
            candidates.Count,
            note);
    }

    private CandidateSelection? GetOrBuildCandidateSelection(
        FishingSpotTarget target,
        SpotScanDocument scan)
    {
        var selectableCandidates = GetSelectableCandidatePool(scan);
        if (CurrentCandidateSelection is { } current
            && current.Candidate.Key == target.Key
            && selectableCandidates.Any(candidate => string.Equals(
                candidate.CandidateFingerprint,
                current.Candidate.CandidateFingerprint,
                StringComparison.Ordinal)))
        {
            return current;
        }

        return BuildCandidateSelection(target, scan);
    }

    private IReadOnlyList<SpotCandidate> GetSelectableCandidatePool(SpotScanDocument scan)
    {
        return GetSelectableCandidatePool(scan, new HashSet<string>(StringComparer.Ordinal));
    }

    private IReadOnlyList<SpotCandidate> GetSelectableCandidatePool(
        SpotScanDocument scan,
        IReadOnlySet<string> unresolvedRiskCandidateIds)
    {
        var currentMaintenance = GetMaintenanceRecord(scan.Key);
        var currentRecordedCandidateIds = GetRecordedCandidateIds(currentMaintenance, scan.Key.TerritoryId);
        var currentRecordedCandidateFingerprints = GetRecordedCandidateFingerprints(currentMaintenance, scan.Key.TerritoryId);
        var currentRiskCandidateIds = GetMixedRiskCandidateIds(currentMaintenance);
        var territoryRecordedCandidateIds = CurrentTerritoryRecordedCandidateIds;
        var territoryRecordedCandidateFingerprints = CurrentTerritoryRecordedCandidateFingerprints;
        var disabledCandidateIds = GetDisabledCandidateIds(CurrentTerritoryMaintenance);
        var disabledCandidateFingerprints = GetDisabledCandidateFingerprints(CurrentTerritoryMaintenance);
        var rejectedFingerprints = GetRejectedCandidateFingerprints(CurrentTerritoryMaintenance);
        return scan.Candidates
            .Where(IsSelectableCandidate)
            .Where(candidate => IsCandidateKeyMatched(candidate, unresolvedRiskCandidateIds)
                || !IsRecordedCandidate(candidate, currentRecordedCandidateIds, currentRecordedCandidateFingerprints))
            .Where(candidate => IsCandidateKeyMatched(candidate, unresolvedRiskCandidateIds)
                || IsMixedRiskCandidate(candidate, currentRiskCandidateIds)
                || !IsRecordedCandidate(candidate, territoryRecordedCandidateIds, territoryRecordedCandidateFingerprints))
            .Where(candidate => !IsRecordedCandidate(candidate, disabledCandidateIds, disabledCandidateFingerprints))
            .Where(candidate => !rejectedFingerprints.Contains(candidate.CandidateFingerprint))
            .ToList();
    }

    private static SpotCandidate PickDefaultCandidate(
        IReadOnlyList<SpotCandidate> candidates,
        Point3? playerPosition,
        IReadOnlySet<string>? priorityCandidateIds = null)
    {
        if (playerPosition is { } position)
        {
            return candidates
                .OrderBy(candidate => IsCandidateKeyMatched(candidate, priorityCandidateIds) ? 0 : 1)
                .ThenBy(candidate => candidate.Position.HorizontalDistanceTo(position))
                .ThenBy(candidate => candidate.CandidateFingerprint, StringComparer.Ordinal)
                .First();
        }

        return candidates
            .OrderBy(candidate => IsCandidateKeyMatched(candidate, priorityCandidateIds) ? 0 : 1)
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

    private static IReadOnlySet<string> GetRecordedCandidateIds(TerritoryMaintenanceDocument? maintenance)
    {
        if (maintenance is null)
            return new HashSet<string>(StringComparer.Ordinal);

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var point in maintenance.Spots
            .SelectMany(spot => spot.ApproachPoints)
            .Where(point => point.Status == ApproachPointStatus.Confirmed))
            AddRecordedCandidateIds(ids, maintenance.TerritoryId, point);

        return ids;
    }

    private static IReadOnlySet<string> GetRecordedCandidateIds(SpotMaintenanceRecord? maintenance, uint territoryId = 0)
    {
        if (maintenance is null)
            return new HashSet<string>(StringComparer.Ordinal);

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var point in maintenance.ApproachPoints.Where(point => point.Status == ApproachPointStatus.Confirmed))
            AddRecordedCandidateIds(ids, territoryId, point);

        return ids;
    }

    private static IReadOnlySet<string> GetRecordedCandidateFingerprints(TerritoryMaintenanceDocument? maintenance)
    {
        if (maintenance is null)
            return new HashSet<string>(StringComparer.Ordinal);

        var fingerprints = new HashSet<string>(StringComparer.Ordinal);
        foreach (var point in maintenance.Spots
            .SelectMany(spot => spot.ApproachPoints)
            .Where(point => point.Status == ApproachPointStatus.Confirmed))
            AddRecordedCandidateFingerprints(fingerprints, maintenance.TerritoryId, point);

        return fingerprints;
    }

    private static IReadOnlySet<string> GetRecordedCandidateFingerprints(SpotMaintenanceRecord? maintenance, uint territoryId = 0)
    {
        if (maintenance is null)
            return new HashSet<string>(StringComparer.Ordinal);

        var fingerprints = new HashSet<string>(StringComparer.Ordinal);
        foreach (var point in maintenance.ApproachPoints.Where(point => point.Status == ApproachPointStatus.Confirmed))
            AddRecordedCandidateFingerprints(fingerprints, territoryId, point);

        return fingerprints;
    }

    private static IReadOnlySet<string> GetDisabledCandidateIds(SpotMaintenanceRecord? maintenance, uint territoryId = 0)
    {
        if (maintenance is null)
            return new HashSet<string>(StringComparer.Ordinal);

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var point in maintenance.ApproachPoints.Where(IsEffectiveDisabledApproachPoint))
            AddRecordedCandidateIds(ids, territoryId, point);

        return ids;
    }

    private static IReadOnlySet<string> GetDisabledCandidateIds(TerritoryMaintenanceDocument? maintenance)
    {
        if (maintenance is null)
            return new HashSet<string>(StringComparer.Ordinal);

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var point in maintenance.Spots
            .SelectMany(spot => spot.ApproachPoints)
            .Where(IsEffectiveDisabledApproachPoint))
            AddRecordedCandidateIds(ids, maintenance.TerritoryId, point);

        return ids;
    }

    private static IReadOnlySet<string> GetMixedRiskCandidateIds(SpotMaintenanceRecord? maintenance)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (maintenance is null)
            return ids;

        foreach (var record in maintenance.MixedRiskBlocks)
        {
            AddIfNotBlank(ids, record.BlockId);
            foreach (var id in record.CandidateIds.Where(id => !string.IsNullOrWhiteSpace(id)))
                ids.Add(id);
        }

        return ids;
    }

    private IReadOnlySet<string> GetUnresolvedMixedRiskCandidateIds(SpotScanDocument scan)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var currentMaintenance = GetMaintenanceRecord(scan.Key);
        if (currentMaintenance is null || currentMaintenance.MixedRiskBlocks.Count == 0)
            return ids;

        var maintenanceDocument = CurrentTerritoryMaintenance is { } current
            && current.TerritoryId == scan.Key.TerritoryId
                ? current
                : maintenanceStore.LoadTerritory(scan.Key.TerritoryId);
        if (maintenanceDocument.Spots.Count == 0)
            return ids;

        foreach (var candidate in scan.Candidates.Where(IsSelectableCandidate))
        {
            if (!IsUnresolvedMixedRiskCandidate(
                    candidate,
                    currentMaintenance,
                    maintenanceDocument,
                    scan.Key.TerritoryId))
            {
                continue;
            }

            AddIfNotBlank(ids, GetSpotCandidateGraphId(candidate));
            AddIfNotBlank(ids, candidate.CandidateFingerprint);
        }

        return ids;
    }

    private static bool IsUnresolvedMixedRiskCandidate(
        SpotCandidate candidate,
        SpotMaintenanceRecord currentMaintenance,
        TerritoryMaintenanceDocument maintenanceDocument,
        uint territoryId)
    {
        if (currentMaintenance.MixedRiskBlocks.Count == 0)
            return false;

        var approachCandidate = ToApproachCandidate(candidate);
        var matchingRecords = currentMaintenance.MixedRiskBlocks
            .Where(record => IsMixedRiskBlockRecordForCandidate(record, candidate))
            .ToList();
        if (matchingRecords.Count == 0)
            return false;

        var involvedSpotIds = matchingRecords
            .SelectMany(record => record.ConflictingFishingSpotIds)
            .Append(currentMaintenance.FishingSpotId)
            .Where(id => id != 0)
            .Distinct()
            .OrderBy(id => id)
            .ToList();
        if (involvedSpotIds.Count < 2)
            return false;

        if (IsCandidateDisabledByAnySpot(maintenanceDocument, involvedSpotIds, territoryId, approachCandidate))
            return false;

        return CountCandidateConfirmedSpots(maintenanceDocument, involvedSpotIds, territoryId, approachCandidate) != 1;
    }

    private static bool IsMixedRiskBlockRecordForCandidate(
        MixedRiskBlockRecord record,
        SpotCandidate candidate)
    {
        if (!string.IsNullOrWhiteSpace(record.BlockId)
            && !string.IsNullOrWhiteSpace(candidate.BlockId)
            && string.Equals(record.BlockId, candidate.BlockId, StringComparison.Ordinal))
            return true;

        var candidateId = GetSpotCandidateGraphId(candidate);
        return record.CandidateIds.Any(id =>
            string.Equals(id, candidateId, StringComparison.Ordinal)
            || string.Equals(id, candidate.CandidateFingerprint, StringComparison.Ordinal));
    }

    private static IReadOnlySet<string> GetDisabledCandidateFingerprints(SpotMaintenanceRecord? maintenance, uint territoryId = 0)
    {
        if (maintenance is null)
            return new HashSet<string>(StringComparer.Ordinal);

        var fingerprints = new HashSet<string>(StringComparer.Ordinal);
        foreach (var point in maintenance.ApproachPoints.Where(IsEffectiveDisabledApproachPoint))
            AddRecordedCandidateFingerprints(fingerprints, territoryId, point);

        return fingerprints;
    }

    private static IReadOnlySet<string> GetDisabledCandidateFingerprints(TerritoryMaintenanceDocument? maintenance)
    {
        if (maintenance is null)
            return new HashSet<string>(StringComparer.Ordinal);

        var fingerprints = new HashSet<string>(StringComparer.Ordinal);
        foreach (var point in maintenance.Spots
            .SelectMany(spot => spot.ApproachPoints)
            .Where(IsEffectiveDisabledApproachPoint))
            AddRecordedCandidateFingerprints(fingerprints, maintenance.TerritoryId, point);

        return fingerprints;
    }

    private static bool IsEffectiveDisabledApproachPoint(ApproachPoint point)
    {
        return point.Status == ApproachPointStatus.Disabled
            && point.SourceKind != ApproachPointSourceKind.AutoCastFill;
    }

    private static HashSet<string> GetOverlayCandidateMatchIds(ApproachCandidate candidate, uint territoryId)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        AddIfNotBlank(ids, candidate.CandidateId);
        if (territoryId != 0)
        {
            ids.Add(SpotFingerprint.CreateTerritoryCandidateFingerprint(
                territoryId,
                candidate.Position,
                candidate.Rotation));
        }

        return ids;
    }

    private static bool IsOverlayCandidateDisabled(
        TerritoryMaintenanceDocument document,
        uint territoryId,
        ApproachCandidate candidate)
    {
        var candidateIds = GetOverlayCandidateMatchIds(candidate, territoryId);
        if (candidateIds.Count == 0)
            return false;

        return document.Spots
            .SelectMany(spot => spot.ApproachPoints)
            .Where(IsEffectiveDisabledApproachPoint)
            .Any(point => IsApproachPointForOverlayCandidate(point, territoryId, candidateIds));
    }

    private static ApproachCandidate? FindOverlayCandidateMatch(
        ApproachPoint point,
        uint territoryId,
        IReadOnlyList<OverlayCandidateMatch> matches)
    {
        foreach (var match in matches)
        {
            if (IsApproachPointForOverlayCandidate(point, territoryId, match.CandidateIds))
                return match.Candidate;
        }

        return null;
    }

    private static bool IsApproachPointForOverlayCandidate(
        ApproachPoint point,
        uint territoryId,
        IReadOnlySet<string> candidateIds)
    {
        if (!string.IsNullOrWhiteSpace(point.SourceCandidateId) && candidateIds.Contains(point.SourceCandidateId))
            return true;
        if (!string.IsNullOrWhiteSpace(point.SourceCandidateFingerprint) && candidateIds.Contains(point.SourceCandidateFingerprint))
            return true;
        if (territoryId != 0
            && candidateIds.Contains(SpotFingerprint.CreateTerritoryCandidateFingerprint(
                territoryId,
                point.Position,
                point.Rotation)))
            return true;

        return false;
    }

    private static void AddRecordedCandidateIds(HashSet<string> ids, uint territoryId, ApproachPoint point)
    {
        AddIfNotBlank(ids, point.SourceCandidateId);
        AddIfNotBlank(ids, point.SourceCandidateFingerprint);
        AddTerritoryCandidateFingerprint(ids, territoryId, point);
    }

    private static void AddRecordedCandidateFingerprints(HashSet<string> fingerprints, uint territoryId, ApproachPoint point)
    {
        AddIfNotBlank(fingerprints, point.SourceCandidateFingerprint);
        AddIfNotBlank(fingerprints, point.SourceCandidateId);
        AddTerritoryCandidateFingerprint(fingerprints, territoryId, point);
    }

    private static void AddTerritoryCandidateFingerprint(HashSet<string> values, uint territoryId, ApproachPoint point)
    {
        if (territoryId == 0)
            return;

        values.Add(SpotFingerprint.CreateTerritoryCandidateFingerprint(
            territoryId,
            point.Position,
            point.Rotation));
    }

    private static void AddIfNotBlank(HashSet<string> values, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            values.Add(value);
    }

    private static IReadOnlySet<string> GetRejectedCandidateFingerprints(SpotMaintenanceRecord? maintenance)
    {
        return maintenance?.Evidence
            .Where(evidence => evidence.EventType == SpotEvidenceEventType.Reject)
            .Select(evidence => evidence.CandidateFingerprint)
            .Where(fingerprint => !string.IsNullOrWhiteSpace(fingerprint))
            .ToHashSet(StringComparer.Ordinal)
            ?? new HashSet<string>(StringComparer.Ordinal);
    }

    private static IReadOnlySet<string> GetRejectedCandidateFingerprints(TerritoryMaintenanceDocument? maintenance)
    {
        return maintenance?.Spots
            .SelectMany(spot => spot.Evidence)
            .Where(evidence => evidence.EventType == SpotEvidenceEventType.Reject)
            .Select(evidence => evidence.CandidateFingerprint)
            .Where(fingerprint => !string.IsNullOrWhiteSpace(fingerprint))
            .ToHashSet(StringComparer.Ordinal)
            ?? new HashSet<string>(StringComparer.Ordinal);
    }

    private static string GetSpotCandidateGraphId(SpotCandidate candidate)
    {
        return !string.IsNullOrWhiteSpace(candidate.SourceCandidateId)
            ? candidate.SourceCandidateId
            : candidate.CandidateFingerprint;
    }

    private static bool IsCandidateKeyMatched(
        SpotCandidate candidate,
        IReadOnlySet<string>? candidateIds)
    {
        if (candidateIds is null || candidateIds.Count == 0)
            return false;

        var candidateId = GetSpotCandidateGraphId(candidate);
        return (!string.IsNullOrWhiteSpace(candidateId) && candidateIds.Contains(candidateId))
            || (!string.IsNullOrWhiteSpace(candidate.CandidateFingerprint) && candidateIds.Contains(candidate.CandidateFingerprint));
    }

    private static bool IsRecordedCandidate(
        SpotCandidate candidate,
        IReadOnlySet<string> recordedCandidateIds,
        IReadOnlySet<string> recordedCandidateFingerprints)
    {
        var candidateId = GetSpotCandidateGraphId(candidate);
        return (!string.IsNullOrWhiteSpace(candidateId) && recordedCandidateIds.Contains(candidateId))
            || (!string.IsNullOrWhiteSpace(candidate.CandidateFingerprint)
                && recordedCandidateFingerprints.Contains(candidate.CandidateFingerprint));
    }

    private static bool IsMixedRiskCandidate(
        SpotCandidate candidate,
        IReadOnlySet<string> mixedRiskCandidateIds)
    {
        if (mixedRiskCandidateIds.Count == 0)
            return false;

        var candidateId = GetSpotCandidateGraphId(candidate);
        return (!string.IsNullOrWhiteSpace(candidateId) && mixedRiskCandidateIds.Contains(candidateId))
            || (!string.IsNullOrWhiteSpace(candidate.BlockId)
                && mixedRiskCandidateIds.Contains(candidate.BlockId))
            || (!string.IsNullOrWhiteSpace(candidate.CandidateFingerprint)
                && mixedRiskCandidateIds.Contains(candidate.CandidateFingerprint));
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

    private static IReadOnlySet<string> GetBlockCandidateIds(SurveyBlock block)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in block.Candidates)
            AddIfNotBlank(ids, candidate.CandidateId);

        return ids;
    }

    private static void AddMixedRiskBlockConflictIds(
        HashSet<uint> conflicts,
        SpotMaintenanceRecord spot,
        SurveyBlock block,
        IReadOnlySet<string> blockCandidateIds)
    {
        foreach (var record in spot.MixedRiskBlocks.Where(record => IsMixedRiskBlockRecordForBlock(record, block, blockCandidateIds)))
        {
            foreach (var fishingSpotId in record.ConflictingFishingSpotIds)
            {
                if (fishingSpotId != 0 && fishingSpotId != spot.FishingSpotId)
                    conflicts.Add(fishingSpotId);
            }
        }
    }

    private static bool HasConfirmedApproachPointInBlock(
        SpotMaintenanceRecord spot,
        uint territoryId,
        SurveyBlock block,
        IReadOnlySet<string> blockCandidateIds)
    {
        return spot.ApproachPoints
            .Where(point => point.Status == ApproachPointStatus.Confirmed)
            .Any(point => IsApproachPointInBlock(point, territoryId, block, blockCandidateIds));
    }

    private static bool IsMixedRiskBlock(SpotMaintenanceRecord? spot, SurveyBlock block)
    {
        return IsMixedRiskBlock(spot, block, GetBlockCandidateIds(block));
    }

    private static bool IsMixedRiskBlock(
        SpotMaintenanceRecord? spot,
        SurveyBlock block,
        IReadOnlySet<string> blockCandidateIds)
    {
        return spot?.MixedRiskBlocks.Any(record => IsMixedRiskBlockRecordForBlock(record, block, blockCandidateIds)) ?? false;
    }

    private static bool IsMixedRiskBlockRecordForBlock(
        MixedRiskBlockRecord record,
        SurveyBlock block,
        IReadOnlySet<string> blockCandidateIds)
    {
        if (!string.IsNullOrWhiteSpace(record.BlockId)
            && !string.IsNullOrWhiteSpace(block.BlockId)
            && string.Equals(record.BlockId, block.BlockId, StringComparison.Ordinal))
            return true;

        return record.CandidateIds.Any(id => blockCandidateIds.Contains(id));
    }

    private static bool IsApproachPointInBlock(
        ApproachPoint point,
        uint territoryId,
        SurveyBlock block,
        IReadOnlySet<string> blockCandidateIds)
    {
        if (!string.IsNullOrWhiteSpace(point.SourceCandidateId) && blockCandidateIds.Contains(point.SourceCandidateId))
            return true;
        if (!string.IsNullOrWhiteSpace(point.SourceCandidateFingerprint) && blockCandidateIds.Contains(point.SourceCandidateFingerprint))
            return true;
        if (territoryId != 0
            && blockCandidateIds.Contains(SpotFingerprint.CreateTerritoryCandidateFingerprint(territoryId, point.Position, point.Rotation)))
            return true;

        return !string.IsNullOrWhiteSpace(point.SourceBlockId)
            && !string.IsNullOrWhiteSpace(block.BlockId)
            && string.Equals(point.SourceBlockId, block.BlockId, StringComparison.Ordinal);
    }

    private static string GetBlockSurfaceGroupId(SurveyBlock block)
    {
        return block.Candidates
            .Select(candidate => candidate.SurfaceGroupId)
            .FirstOrDefault(surfaceGroupId => !string.IsNullOrWhiteSpace(surfaceGroupId))
            ?? string.Empty;
    }

    private static MixedRiskBlockUpsertResult UpsertMixedRiskBlockRecord(
        IReadOnlyList<MixedRiskBlockRecord> existing,
        SurveyBlock block,
        IReadOnlySet<string> blockCandidateIds,
        IReadOnlyList<uint> conflictSpotIds,
        int resetPointCount,
        DateTimeOffset now,
        string note)
    {
        var records = existing.ToList();
        var candidateIds = blockCandidateIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
        var conflicts = conflictSpotIds
            .Where(id => id != 0)
            .Distinct()
            .OrderBy(id => id)
            .ToList();
        var index = records.FindIndex(record => IsMixedRiskBlockRecordForBlock(record, block, blockCandidateIds));
        if (index < 0)
        {
            records.Add(new MixedRiskBlockRecord
            {
                BlockId = block.BlockId,
                SurfaceGroupId = GetBlockSurfaceGroupId(block),
                CandidateIds = candidateIds,
                ConflictingFishingSpotIds = conflicts,
                ResetPointCount = resetPointCount,
                CreatedAt = now,
                UpdatedAt = now,
                Note = note,
            });

            return new MixedRiskBlockUpsertResult(SortMixedRiskBlockRecords(records), true);
        }

        var current = records[index];
        var mergedCandidateIds = current.CandidateIds
            .Concat(candidateIds)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
        var mergedConflicts = current.ConflictingFishingSpotIds
            .Concat(conflicts)
            .Where(id => id != 0)
            .Distinct()
            .OrderBy(id => id)
            .ToList();
        var blockSurfaceGroupId = GetBlockSurfaceGroupId(block);
        var shouldSetBlockId = string.IsNullOrWhiteSpace(current.BlockId) && !string.IsNullOrWhiteSpace(block.BlockId);
        var shouldSetSurfaceGroupId = string.IsNullOrWhiteSpace(current.SurfaceGroupId) && !string.IsNullOrWhiteSpace(blockSurfaceGroupId);
        var changed = resetPointCount > 0
            || !current.CandidateIds.SequenceEqual(mergedCandidateIds)
            || !current.ConflictingFishingSpotIds.SequenceEqual(mergedConflicts)
            || shouldSetBlockId
            || shouldSetSurfaceGroupId;
        if (!changed)
            return new MixedRiskBlockUpsertResult(SortMixedRiskBlockRecords(records), false);

        records[index] = current with
        {
            BlockId = shouldSetBlockId ? block.BlockId : current.BlockId,
            SurfaceGroupId = shouldSetSurfaceGroupId ? blockSurfaceGroupId : current.SurfaceGroupId,
            CandidateIds = mergedCandidateIds,
            ConflictingFishingSpotIds = mergedConflicts,
            ResetPointCount = current.ResetPointCount + resetPointCount,
            UpdatedAt = now,
            Note = AppendNote(current.Note, note),
        };
        return new MixedRiskBlockUpsertResult(SortMixedRiskBlockRecords(records), true);
    }

    private static List<MixedRiskBlockRecord> SortMixedRiskBlockRecords(IEnumerable<MixedRiskBlockRecord> records)
    {
        return records
            .OrderBy(record => record.BlockId, StringComparer.Ordinal)
            .ThenBy(record => record.SurfaceGroupId, StringComparer.Ordinal)
            .ThenBy(record => record.CreatedAt)
            .ToList();
    }

    private static string AppendNote(string existing, string appended)
    {
        if (string.IsNullOrWhiteSpace(appended))
            return existing;
        if (string.IsNullOrWhiteSpace(existing))
            return appended;
        if (existing.Contains(appended, StringComparison.Ordinal))
            return existing;

        return $"{existing}; {appended}";
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
        var maintenance = GetMaintenanceRecord(target.Key);
        CurrentAnalysis = ApplyMaintenanceMixedRisk(
            target,
            maintenanceAnalysisBuilder.Analyze(
                target,
                CurrentScan,
                maintenance,
                TryLoadLegacyReview(target.Key)),
            CurrentTerritoryMaintenance,
            maintenance?.ReviewDecision ?? SpotReviewDecision.None);
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
        return CalculateCandidateGraphDistances(sourceCandidates, new[] { seedCandidate });
    }

    private IReadOnlyDictionary<string, float> CalculateCandidateGraphDistances(
        IReadOnlyList<ApproachCandidate> sourceCandidates,
        IReadOnlyList<ApproachCandidate> seedCandidates)
    {
        var candidates = sourceCandidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.CandidateId))
            .OrderBy(candidate => candidate.CandidateId, StringComparer.Ordinal)
            .ToList();
        if (candidates.Count == 0)
            return new Dictionary<string, float>(StringComparer.Ordinal);

        var distances = candidates.ToDictionary(candidate => candidate.CandidateId, _ => float.MaxValue, StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var seedCandidate in seedCandidates)
        {
            var seed = candidates.FirstOrDefault(candidate => string.Equals(candidate.CandidateId, seedCandidate.CandidateId, StringComparison.Ordinal))
                ?? candidates
                    .OrderBy(candidate => candidate.Position.HorizontalDistanceTo(seedCandidate.Position))
                    .ThenBy(candidate => candidate.CandidateId, StringComparer.Ordinal)
                    .First();
            distances[seed.CandidateId] = 0f;
        }

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
                if (visited.Contains(next.CandidateId) || !ShouldLinkCastWaterSystem(current, next))
                    continue;

                var edgeDistance = Math.Max(0.1f, current.Position.HorizontalDistanceTo(next.Position));
                var nextDistance = currentDistance + edgeDistance;
                if (nextDistance < distances[next.CandidateId])
                    distances[next.CandidateId] = nextDistance;
            }
        }

        return distances;
    }

    private bool ShouldLinkCastWaterSystem(ApproachCandidate left, ApproachCandidate right)
    {
        var sameWaterSystem = !string.IsNullOrWhiteSpace(left.SurfaceGroupId)
            && string.Equals(left.SurfaceGroupId, right.SurfaceGroupId, StringComparison.Ordinal);
        var linkDistance = sameWaterSystem
            ? CastWaterSystemLinkDistanceMeters
            : blockOptions.BlockLinkDistanceMeters;
        var heightTolerance = sameWaterSystem
            ? CastWaterSystemHeightToleranceMeters
            : blockOptions.BlockHeightToleranceMeters;
        return MathF.Abs(left.Position.Y - right.Position.Y) <= heightTolerance
            && left.Position.HorizontalDistanceTo(right.Position) <= linkDistance;
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

    private void ReleaseTerritoryScanTask()
    {
        var task = territoryScanTask;
        var cancellation = territoryScanCancellation;

        territoryScanTask = null;
        territoryScanCancellation = null;
        territoryScanCancelMessageRequested = false;
        TerritoryScanProgress = null;
        Interlocked.Increment(ref territoryScanGeneration);

        if (task is null)
        {
            cancellation?.Dispose();
            return;
        }

        if (task.IsCompleted)
        {
            if (task.IsFaulted)
                _ = task.Exception;

            cancellation?.Dispose();
            return;
        }

        _ = task.ContinueWith(
            completed =>
            {
                if (completed.IsFaulted)
                    _ = completed.Exception;

                cancellation?.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
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
            CandidateId = GetSpotCandidateGraphId(candidate),
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

    private static string FormatSpotIds(IReadOnlyList<uint> fishingSpotIds)
    {
        return fishingSpotIds.Count == 0 ? "-" : string.Join(", ", fishingSpotIds);
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

        if (CurrentTarget is { } current
            && matches.Any(target => target.Key == current.Key))
        {
            resolutionNote = $"PlaceName {castPlaceNameId} 匹配多个目标，已使用当前点亮目标 FishingSpot {current.FishingSpotId}；";
            return current;
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

    private sealed record OverlayCandidateMatch(ApproachCandidate Candidate, IReadOnlySet<string> CandidateIds);

    private sealed record FillBlockSelection(SurveyBlock Block, ApproachCandidate SeedCandidate, float Distance);

    private readonly record struct CandidateSparseGridCell(int X, int Z)
    {
        public static CandidateSparseGridCell From(Point3 point, float cellSize) => new(
            (int)MathF.Floor(point.X / cellSize),
            (int)MathF.Floor(point.Z / cellSize));
    }

    private sealed record MixedRiskBlockMarkResult(int MarkedCandidateCount, IReadOnlyList<uint> RelatedFishingSpotIds)
    {
        public static MixedRiskBlockMarkResult Empty { get; } = new(0, Array.Empty<uint>());
    }

    private sealed record MixedRiskBlockUpsertResult(List<MixedRiskBlockRecord> Records, bool Changed);

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

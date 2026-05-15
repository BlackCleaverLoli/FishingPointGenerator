using System.Globalization;
using System.IO;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FishingPointGenerator.Plugin.Services;
using FishingPointGenerator.Plugin.Services.GameInteraction;
using FishingPointGenerator.Plugin.Services.Overlay;
using FishingPointGenerator.Plugin.Services.Scanning;
using FishingPointGenerator.Plugin.Windows;
using OmenTools;
using System.Text.Json;

namespace FishingPointGenerator.Plugin;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/fpg";
    private const string CommandHelp =
        "/fpg - 打开/关闭窗口\n"
        + "/fpg catalog - 重建 FishingSpot 目录\n"
        + "/fpg refresh - 重新加载当前区域目标\n"
        + "/fpg next - 打开下一个需要处理的维护目标\n"
        + "/fpg target <fishingSpotId> - 打开已选领地内的维护目标\n"
        + "/fpg scan - 扫描当前区域全图并缓存候选点\n"
        + "/fpg scantarget - 从 Territory 缓存为维护目标派生候选\n"
        + "/fpg debugnear [radius] - 只分析角色附近碰撞面，输出调试日志并显示 Fishable/Walkable overlay\n"
        + "/fpg debugcandidates [radius] [limit] - 输出维护目标附近候选点、块和点亮范围调试日志\n"
        + "/fpg debugoverlayperf [seconds] - 固定采样 overlay 分段计时日志\n"
        + "/fpg debugclear - 清除附近碰撞面调试 overlay\n"
        + "/fpg flag - 为维护目标中心插旗\n"
        + "/fpg tp - 传送到维护目标附近已共鸣以太之光\n"
        + "/fpg flagcandidate - 为当前候选插旗\n"
        + "/fpg flagunrecorded - 为当前未记录候选点插旗\n"
        + "/fpg refreshcandidate - 刷新当前候选选择\n"
        + "/fpg autoonce - 自动移动到当前候选并抛竿一次\n"
        + "/fpg autoloop - 循环自动移动到当前候选并抛竿\n"
        + "/fpg autostop - 停止自动点亮\n"
        + "/fpg confirm - 已停用：模型只通过真实抛竿点亮确认\n"
        + "/fpg rejectcandidate - 排除当前候选\n"
        + "/fpg unrecordspot [fishingSpotId] - 将当前/指定钓场已记录点改为未记录，保留禁用点\n"
        + "/fpg clearspotmaintenance - 清除维护目标数据\n"
        + "/fpg clearterritorymaintenance - 清除当前领地维护数据\n"
        + "/fpg clearterritorycandidates - 清除当前领地内存候选\n"
        + "/fpg allowweak - 允许维护目标以弱覆盖状态导出\n"
        + "/fpg allowrisk - 允许维护目标在风险复核后导出\n"
        + "/fpg ignore - 忽略维护目标\n"
        + "/fpg report - 为维护目标生成验证报告\n"
        + "/fpg export - 导出已确认的 FishingSpot 点位";

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog pluginLog;
    private readonly WindowSystem windowSystem = new("FishingPointGenerator");
    private readonly WorldOverlayRenderer overlayRenderer = new();
    private readonly MainWindow mainWindow;
    private readonly SpotWorkflowSession session;
    private readonly FishingCastMonitor castMonitor;
    private readonly PluginConfiguration configuration;
    private bool disposed;

    public Plugin(IDalamudPluginInterface pluginInterface, IPluginLog pluginLog)
    {
        this.pluginInterface = pluginInterface;
        this.pluginLog = pluginLog;

        DService.Init(pluginInterface);

        var paths = new PluginPaths(pluginInterface);
        configuration = LoadConfiguration(paths);
        session = new SpotWorkflowSession(paths, new VnavmeshSceneScanner(pluginLog), configuration, SaveConfiguration, pluginLog);
        castMonitor = new FishingCastMonitor(session, pluginLog);
        mainWindow = new MainWindow(session);
        mainWindow.IsOpen = true;

        windowSystem.AddWindow(mainWindow);
        pluginInterface.UiBuilder.Draw += DrawUi;
        pluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        pluginInterface.UiBuilder.OpenConfigUi += ToggleMainUi;
        DService.Instance().ClientState.TerritoryChanged += OnTerritoryChanged;

        DService.Instance().Command.AddHandler(
            CommandName,
            new CommandInfo(OnCommand)
            {
                HelpMessage = CommandHelp,
            });

        session.RefreshCurrentTerritory();
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;

        DService.Instance().Command.RemoveHandler(CommandName);
        pluginInterface.UiBuilder.Draw -= DrawUi;
        pluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        pluginInterface.UiBuilder.OpenConfigUi -= ToggleMainUi;
        DService.Instance().ClientState.TerritoryChanged -= OnTerritoryChanged;

        session.Dispose();
        castMonitor.Dispose();
        windowSystem.RemoveAllWindows();
        mainWindow.Dispose();

        DService.Uninit();
    }

    private PluginConfiguration LoadConfiguration(PluginPaths paths)
    {
        var loaded = pluginInterface.GetPluginConfig() as PluginConfiguration;
        var legacyConfiguration = loaded is null ? LoadLegacyConfiguration(paths.RootDirectory) : null;
        var result = loaded ?? legacyConfiguration ?? new PluginConfiguration();
        if (result.Version < PluginConfiguration.CurrentVersion)
            result.Version = PluginConfiguration.CurrentVersion;

        if (loaded is null || result.Version != loaded.Version)
            pluginInterface.SavePluginConfig(result);

        if (loaded is not null || legacyConfiguration is not null)
            TryDeleteLegacyConfiguration(paths.RootDirectory);

        return result;
    }

    private static PluginConfiguration? LoadLegacyConfiguration(string rootDirectory)
    {
        var legacyPath = Path.Combine(rootDirectory, "ui_settings.json");
        if (!File.Exists(legacyPath))
            return null;

        try
        {
            return JsonSerializer.Deserialize<PluginConfiguration>(
                File.ReadAllText(legacyPath),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch
        {
            return null;
        }
    }

    private static void TryDeleteLegacyConfiguration(string rootDirectory)
    {
        var legacyPath = Path.Combine(rootDirectory, "ui_settings.json");
        if (!File.Exists(legacyPath))
            return;

        try
        {
            File.Delete(legacyPath);
        }
        catch
        {
        }
    }

    private void SaveConfiguration()
    {
        pluginInterface.SavePluginConfig(configuration);
    }

    private void DrawUi()
    {
        session.PollBackgroundOperations();
        session.SetOverlayPointDisableUiWindowVisible(mainWindow.IsOpen);
        overlayRenderer.Draw(session);
        windowSystem.Draw();
    }

    private void ToggleMainUi()
    {
        mainWindow.IsOpen = !mainWindow.IsOpen;
    }

    private void OnTerritoryChanged(uint territoryId)
    {
        try
        {
            session.HandleTerritoryChanged(territoryId);
            pluginLog.Debug("FishingPointGenerator 已处理切图：Territory={TerritoryId}", territoryId);
        }
        catch (Exception ex)
        {
            pluginLog.Error(ex, "FishingPointGenerator 切图处理失败");
        }
    }

    private void OnCommand(string command, string args)
    {
        try
        {
            HandleCommand(args);
        }
        catch (Exception ex)
        {
            pluginLog.Error(ex, "FishingPointGenerator 命令执行失败");
            Print($"命令执行失败：{ex.Message}");
        }
    }

    private void HandleCommand(string args)
    {
        var trimmed = args.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            ToggleMainUi();
            return;
        }

        var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        switch (parts[0].ToLowerInvariant())
        {
            case "scan":
                session.ScanCurrentTerritory();
                mainWindow.IsOpen = true;
                Print(session.LastMessage);
                break;

            case "scantarget":
                session.ScanCurrentTarget();
                mainWindow.IsOpen = true;
                Print(session.LastMessage);
                break;

            case "debugnear":
                var debugRadius = 35f;
                if (parts.Length >= 2
                    && (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out debugRadius)
                        || debugRadius <= 0f))
                {
                    Print("用法：/fpg debugnear [radius]");
                    return;
                }

                session.DebugScanNearby(debugRadius);
                mainWindow.IsOpen = true;
                Print(session.LastMessage);
                break;

            case "debugcandidates":
            case "debugcand":
                var candidateRadius = 35f;
                var candidateLimit = 80;
                if (parts.Length >= 2
                    && (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out candidateRadius)
                        || candidateRadius <= 0f))
                {
                    Print("用法：/fpg debugcandidates [radius] [limit]");
                    return;
                }

                if (parts.Length >= 3
                    && (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out candidateLimit)
                        || candidateLimit <= 0))
                {
                    Print("用法：/fpg debugcandidates [radius] [limit]");
                    return;
                }

                foreach (var line in session.BuildNearbyCandidateDebugLines(candidateRadius, candidateLimit))
                    pluginLog.Information("{Message}", line);

                mainWindow.IsOpen = true;
                Print(session.LastMessage);
                break;

            case "debugclear":
                session.ClearNearbyDebugOverlay();
                mainWindow.IsOpen = true;
                Print(session.LastMessage);
                break;

            case "debugoverlayperf":
            case "overlayperf":
                var overlayPerfSeconds = 5f;
                if (parts.Length >= 2
                    && string.Equals(parts[1], "off", StringComparison.OrdinalIgnoreCase))
                {
                    session.StopOverlayPerformanceDebug();
                    Print(session.LastMessage);
                    return;
                }

                if (parts.Length >= 2
                    && (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out overlayPerfSeconds)
                        || overlayPerfSeconds <= 0f))
                {
                    Print("用法：/fpg debugoverlayperf [seconds]");
                    return;
                }

                overlayPerfSeconds = Math.Clamp(overlayPerfSeconds, 1f, 30f);
                session.StartOverlayPerformanceDebug(TimeSpan.FromSeconds(overlayPerfSeconds));
                Print(session.LastMessage);
                break;

            case "flag":
                session.PlaceCurrentTargetFlag();
                mainWindow.IsOpen = true;
                Print(session.LastMessage);
                break;

            case "tp":
            case "teleport":
                session.TeleportToCurrentTargetAetheryte();
                mainWindow.IsOpen = true;
                Print(session.LastMessage);
                break;

            case "flagcandidate":
                session.PlaceSelectedCandidateFlag();
                mainWindow.IsOpen = true;
                Print(session.LastMessage);
                break;

            case "flagunrecorded":
                session.PlaceNearestUnrecordedCandidateFlag();
                mainWindow.IsOpen = true;
                Print(session.LastMessage);
                break;

            case "refreshcandidate":
                session.RefreshCandidateSelection();
                mainWindow.IsOpen = true;
                Print(session.LastMessage);
                break;

            case "autoonce":
                session.StartAutoSurveyOnce();
                mainWindow.IsOpen = true;
                Print(session.LastMessage);
                break;

            case "autoloop":
                session.StartAutoSurveyLoop();
                mainWindow.IsOpen = true;
                Print(session.LastMessage);
                break;

            case "autostop":
                session.StopAutoSurvey();
                mainWindow.IsOpen = true;
                Print(session.LastMessage);
                break;

            case "catalog":
                session.RefreshCatalog();
                mainWindow.IsOpen = true;
                Print(session.LastMessage);
                break;

            case "next":
                session.SelectNextTarget();
                mainWindow.IsOpen = true;
                Print(session.LastMessage);
                break;

            case "target":
                if (parts.Length < 2 || !uint.TryParse(parts[1], out var targetSpotId) || targetSpotId == 0)
                {
                    Print("用法：/fpg target <fishingSpotId>");
                    return;
                }

                session.SelectTarget(targetSpotId);
                mainWindow.IsOpen = true;
                Print(session.LastMessage);
                break;

            case "label":
                if (parts.Length < 2 || !uint.TryParse(parts[1], out var fishingSpotId) || fishingSpotId == 0)
                {
                    Print("用法：/fpg label <fishingSpotId>");
                    return;
                }

                if (session.SelectTarget(fishingSpotId))
                    session.ConfirmCurrentStanding();
                mainWindow.IsOpen = true;
                Print(session.LastMessage);
                break;

            case "confirm":
                session.ConfirmCurrentStanding();
                mainWindow.IsOpen = true;
                Print(session.LastMessage);
                break;

            case "rejectcandidate":
                session.RejectSelectedCandidate();
                mainWindow.IsOpen = true;
                Print(session.LastMessage);
                break;

            case "unrecordspot":
                if (parts.Length >= 2)
                {
                    if (!uint.TryParse(parts[1], out var unrecordSpotId) || unrecordSpotId == 0)
                    {
                        Print("用法：/fpg unrecordspot [fishingSpotId]");
                        return;
                    }

                    session.ClearSpotRecordedPoints(unrecordSpotId);
                }
                else
                {
                    session.ClearCurrentSpotRecordedPoints();
                }

                mainWindow.IsOpen = true;
                Print(session.LastMessage);
                break;

            case "clearspotmaintenance":
                session.ClearCurrentSpotMaintenance();
                mainWindow.IsOpen = true;
                Print(session.LastMessage);
                break;

            case "clearterritorymaintenance":
                session.ClearCurrentTerritoryMaintenance();
                mainWindow.IsOpen = true;
                Print(session.LastMessage);
                break;

            case "clearterritorycandidates":
                session.ClearCurrentTerritoryCandidates();
                mainWindow.IsOpen = true;
                Print(session.LastMessage);
                break;

            case "allowweak":
                session.AllowWeakCoverageExport();
                mainWindow.IsOpen = true;
                Print(session.LastMessage);
                break;

            case "allowrisk":
                session.AllowRiskExport();
                mainWindow.IsOpen = true;
                Print(session.LastMessage);
                break;

            case "ignore":
                session.IgnoreCurrentTarget();
                mainWindow.IsOpen = true;
                Print(session.LastMessage);
                break;

            case "report":
                session.GenerateCurrentReport();
                mainWindow.IsOpen = true;
                Print(session.LastMessage);
                break;

            case "export":
                session.ExportConfirmed();
                mainWindow.IsOpen = true;
                Print(session.LastMessage);
                break;

            case "refresh":
                session.RefreshCurrentTerritory();
                mainWindow.IsOpen = true;
                Print(session.LastMessage);
                break;

            case "help":
                Print(CommandHelp.Replace('\n', ' '));
                break;

            default:
                Print("未知命令。使用 /fpg help 查看帮助。");
                break;
        }
    }

    private static void Print(string message)
    {
        DService.Instance().Chat.Print($"[FPG] {message}");
    }
}

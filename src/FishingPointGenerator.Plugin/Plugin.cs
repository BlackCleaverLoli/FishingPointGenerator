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

namespace FishingPointGenerator.Plugin;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/fpg";
    private const string CommandHelp =
        "/fpg - 打开/关闭窗口\n"
        + "/fpg catalog - 重建 FishingSpot 目录\n"
        + "/fpg refresh - 重新加载当前区域目标\n"
        + "/fpg next - 选择下一个需要处理的目标\n"
        + "/fpg target <fishingSpotId> - 选择当前区域内的目标\n"
        + "/fpg scan - 扫描当前区域全图并缓存候选点\n"
        + "/fpg scantarget - 从全图缓存生成已选目标缓存\n"
        + "/fpg flag - 为已选钓场中心插旗\n"
        + "/fpg flagstand - 为推荐点位插旗\n"
        + "/fpg confirm - 确认已选目标的推荐\n"
        + "/fpg mismatch - 记录此目标与推荐不匹配\n"
        + "/fpg allowweak - 允许已选目标以弱覆盖状态导出\n"
        + "/fpg ignore - 忽略已选目标\n"
        + "/fpg report - 为已选目标生成验证报告\n"
        + "/fpg export - 导出已确认的 FishingSpot 点位";

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog pluginLog;
    private readonly WindowSystem windowSystem = new("FishingPointGenerator");
    private readonly WorldOverlayRenderer overlayRenderer = new();
    private readonly MainWindow mainWindow;
    private readonly SpotWorkflowSession session;
    private readonly FishingCastMonitor castMonitor;
    private bool disposed;

    public Plugin(IDalamudPluginInterface pluginInterface, IPluginLog pluginLog)
    {
        this.pluginInterface = pluginInterface;
        this.pluginLog = pluginLog;

        DService.Init(pluginInterface);

        var paths = new PluginPaths(pluginInterface);
        session = new SpotWorkflowSession(paths, new VnavmeshSceneScanner(pluginLog));
        castMonitor = new FishingCastMonitor(session, pluginLog);
        mainWindow = new MainWindow(session);
        mainWindow.IsOpen = true;

        windowSystem.AddWindow(mainWindow);
        pluginInterface.UiBuilder.Draw += DrawUi;
        pluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        pluginInterface.UiBuilder.OpenConfigUi += ToggleMainUi;

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

        castMonitor.Dispose();
        windowSystem.RemoveAllWindows();
        mainWindow.Dispose();

        DService.Uninit();
    }

    private void DrawUi()
    {
        overlayRenderer.Draw(session);
        windowSystem.Draw();
    }

    private void ToggleMainUi()
    {
        mainWindow.IsOpen = !mainWindow.IsOpen;
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

            case "flag":
                session.PlaceCurrentTargetFlag();
                mainWindow.IsOpen = true;
                Print(session.LastMessage);
                break;

            case "flagstand":
                session.PlaceRecommendedStandingFlag();
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
                    session.ConfirmRecommendation();
                mainWindow.IsOpen = true;
                Print(session.LastMessage);
                break;

            case "confirm":
                session.ConfirmRecommendation();
                mainWindow.IsOpen = true;
                Print(session.LastMessage);
                break;

            case "mismatch":
                session.RecordMismatch();
                mainWindow.IsOpen = true;
                Print(session.LastMessage);
                break;

            case "allowweak":
                session.AllowWeakCoverageExport();
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

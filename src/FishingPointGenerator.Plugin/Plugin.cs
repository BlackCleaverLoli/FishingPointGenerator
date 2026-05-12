using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FishingPointGenerator.Plugin.Services;
using FishingPointGenerator.Plugin.Services.Scanning;
using FishingPointGenerator.Plugin.Windows;
using OmenTools;

namespace FishingPointGenerator.Plugin;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/fpg";
    private const string CommandHelp =
        "/fpg - toggle window\n"
        + "/fpg catalog - rebuild the FishingSpot catalog\n"
        + "/fpg refresh - reload current territory targets\n"
        + "/fpg next - select the next target needing work\n"
        + "/fpg target <fishingSpotId> - select a target in the current territory\n"
        + "/fpg scan - rescan the selected target\n"
        + "/fpg confirm - confirm the selected target recommendation\n"
        + "/fpg mismatch - record that the recommendation did not match this target\n"
        + "/fpg allowweak - allow weak coverage export for selected target\n"
        + "/fpg ignore - ignore the selected target\n"
        + "/fpg report - generate validation report for selected target\n"
        + "/fpg export - export confirmed FishingSpot approach points";

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog pluginLog;
    private readonly WindowSystem windowSystem = new("FishingPointGenerator");
    private readonly MainWindow mainWindow;
    private readonly SpotWorkflowSession session;
    private bool disposed;

    public Plugin(IDalamudPluginInterface pluginInterface, IPluginLog pluginLog)
    {
        this.pluginInterface = pluginInterface;
        this.pluginLog = pluginLog;

        DService.Init(pluginInterface);

        var paths = new PluginPaths(pluginInterface);
        session = new SpotWorkflowSession(paths, new VnavmeshSceneScanner(pluginLog));
        mainWindow = new MainWindow(session);

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

        windowSystem.RemoveAllWindows();
        mainWindow.Dispose();

        DService.Uninit();
    }

    private void DrawUi()
    {
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
            pluginLog.Error(ex, "FishingPointGenerator command failed");
            Print($"Command failed: {ex.Message}");
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
                session.ScanCurrentTarget();
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
                    Print("Usage: /fpg target <fishingSpotId>");
                    return;
                }

                session.SelectTarget(targetSpotId);
                mainWindow.IsOpen = true;
                Print(session.LastMessage);
                break;

            case "label":
                if (parts.Length < 2 || !uint.TryParse(parts[1], out var fishingSpotId) || fishingSpotId == 0)
                {
                    Print("Usage: /fpg label <fishingSpotId>");
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
                Print("Unknown command. Use /fpg help.");
                break;
        }
    }

    private static void Print(string message)
    {
        DService.Instance().Chat.Print($"[FPG] {message}");
    }
}

using Dalamud.Plugin.Services;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.Text;
using OmenTools;
using OmenTools.OmenService;

namespace FishingPointGenerator.Plugin.Services.GameInteraction;

internal sealed class FishingCastMonitor : IDisposable
{
    private const uint CastLogMessageId = 0x456;

    private readonly SpotWorkflowSession session;
    private readonly IPluginLog pluginLog;
    private bool disposed;

    public FishingCastMonitor(SpotWorkflowSession session, IPluginLog pluginLog)
    {
        this.session = session;
        this.pluginLog = pluginLog;
        LogMessageManager.Instance().RegPost(OnPostLogMessage);
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        LogMessageManager.Instance().Unreg(OnPostLogMessage);
    }

    private void OnPostLogMessage(uint logMessageId, LogMessageQueueItem item)
    {
        if (logMessageId != CastLogMessageId)
            return;

        try
        {
            var param1 = GetLogMessageParamUInt(item, 0);
            var param2 = GetLogMessageParamUInt(item, 1);
            var param3 = GetLogMessageParamUInt(item, 2);

            pluginLog.Debug($"FPG cast log: {logMessageId}|{param1}|{param2}|{param3}");

            // LogMessage 1110/0x456 uses PlaceName,IntegerParameter(1), which is item parameter index 0.
            if (!session.RecordCastFill(param1))
                return;

            DService.Instance().Chat.Print($"[FPG] {session.LastMessage}");
        }
        catch (Exception ex)
        {
            pluginLog.Error(ex, "FPG 抛竿监听处理失败");
        }
    }

    private static uint GetLogMessageParamUInt(LogMessageQueueItem item, int index)
    {
        if (index < 0 || index >= item.Parameters.Count)
            return 0;

        var param = item.Parameters[index];
        switch (param.Type)
        {
            case TextParameterType.Uninitialized:
            case TextParameterType.ReferencedUtf8String:
                return 0;
            case TextParameterType.String:
                if (param.StringValue.HasValue)
                {
                    var text = param.StringValue.ExtractText();
                    if (uint.TryParse(text, out var value))
                        return value;
                }

                return 0;
            default:
                return unchecked((uint)param.IntValue);
        }
    }
}

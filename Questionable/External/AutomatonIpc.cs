using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using Microsoft.Extensions.Logging;
namespace Questionable.External;

internal sealed class AutomatonIpc
{
    private readonly ICallGateSubscriber<string, bool> _isTweakEnabled;
    private readonly ILogger<AutomatonIpc> _logger;
    private bool _loggedIpcError;

    public AutomatonIpc(IDalamudPluginInterface pluginInterface, ILogger<AutomatonIpc> logger)
    {
        _logger = logger;
        _isTweakEnabled = pluginInterface.GetIpcSubscriber<string, bool>("Automaton.IsTweakEnabled");
        logger.LogInformation("Automaton auto-snipe enabled: {IsTweakEnabled}", IsAutoSnipeEnabled);
    }

    public bool IsAutoSnipeEnabled
    {
        get
        {
            try
            {
                return _isTweakEnabled.InvokeFunc("AutoSnipeQuests");
            }
            catch(IpcNotReadyError)
            {
                // Automaton 沒註冊 IPC(未安裝、或還在啟動中)。這不是錯誤,
                // 安靜地當成停用即可 —— 別燒掉 _loggedIpcError 這個一次性旗標,
                // 否則之後真正的 IPC 錯誤就再也不會被記下來。
                return false;
            }
            catch(IpcError e)
            {
                if (!_loggedIpcError)
                {
                    _loggedIpcError = true;
                    _logger.LogWarning(e, "Could not query automaton for tweak status, probably not installed");
                }
                return false;
            }
        }
    }
}

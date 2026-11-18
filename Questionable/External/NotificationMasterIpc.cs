using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
namespace Questionable.External;

/// <summary>
/// 走到需要手動處理的步驟時，經 NotificationMaster 顯示系統匣通知／閃爍工作列。純通知，不影響任何流程。
/// </summary>
/// <remarks>
/// 🔴 端點名與型別逐字抄 NotificationMaster 的 <c>NMAPINames</c> 與 <c>IPC.cs</c>；不可同名改型別。
/// 刻意不走它的通知樞紐 <c>NotificationMaster.Notify</c>：樞紐預設也會叫 TataruPraise，和
/// <see cref="TataruPraiseIpc"/> 同一件事會念兩次。
/// 🔴 呼叫點只能在主執行緒的狀態邊緣（<c>SendNotification.Executor.Start</c>）。
/// </remarks>
internal sealed class NotificationMasterIpc(
    IDalamudPluginInterface pluginInterface,
    Configuration configuration,
    ILogger<NotificationMasterIpc> logger)
{
    public const string PluginName = "NotificationMaster";

    /// <summary><c>Func&lt;string requester, string title, string text, bool&gt;</c></summary>
    public const string DisplayToastNotificationIpcName = "NotificationMasterAPI.DisplayToastNotification";

    /// <summary><c>Func&lt;string requester, bool&gt;</c></summary>
    public const string FlashTaskbarIconIpcName = "NotificationMasterAPI.FlashTaskbarIcon";

    private const long LoadedRecheckIntervalMilliseconds = 2000;

    private readonly ICallGateSubscriber<string, string, string, bool> _displayToast =
        pluginInterface.GetIpcSubscriber<string, string, string, bool>(DisplayToastNotificationIpcName);

    private readonly ICallGateSubscriber<string, bool> _flashTaskbar =
        pluginInterface.GetIpcSubscriber<string, bool>(FlashTaskbarIconIpcName);

    private bool _loggedUnavailable;
    private bool _loggedFailure;
    private bool _cachedLoaded;
    private long _nextLoadedRecheckAt;

    public bool IsLoaded()
    {
        return pluginInterface.InstalledPlugins.Any(x => x.InternalName == PluginName && x.IsLoaded);
    }

    /// <summary>給設定視窗每幀用：每 2 秒才真的列舉一次已安裝插件。</summary>
    public bool IsLoadedCached()
    {
        long now = Environment.TickCount64;
        if (now >= _nextLoadedRecheckAt)
        {
            _nextLoadedRecheckAt = now + LoadedRecheckIntervalMilliseconds;
            _cachedLoaded = IsLoaded();
        }

        return _cachedLoaded;
    }

    public void Notify(string text)
    {
        var config = configuration.Notifications;
        if (!config.ShowTrayMessage && !config.FlashTaskbar)
        {
            return;
        }

        if (!IsLoaded())
        {
            if (!_loggedUnavailable)
            {
                _loggedUnavailable = true;
                logger.LogDebug("{PluginName} 沒有安裝或尚未載入，略過系統匣／工作列通知（只記這一次）。", PluginName);
            }

            return;
        }

        if (config.ShowTrayMessage)
        {
            TryInvoke(() => _displayToast.InvokeFunc(pluginInterface.InternalName, "Questionable", text));
        }

        if (config.FlashTaskbar)
        {
            TryInvoke(() => _flashTaskbar.InvokeFunc(pluginInterface.InternalName));
        }
    }

    // 各自包一層：系統匣那半邊失敗不該連工作列閃爍一起吃掉。
    private void TryInvoke(Func<bool> call)
    {
        try
        {
            call();
        }
        catch (Exception e)
        {
            if (!_loggedFailure)
            {
                _loggedFailure = true;
                logger.LogDebug(e, "呼叫 {PluginName} 的 IPC 失敗，略過這次系統匣／工作列通知（只記這一次）。", PluginName);
            }
        }
    }
}

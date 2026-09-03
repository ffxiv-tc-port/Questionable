using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using Microsoft.Extensions.Logging;
using System;
namespace Questionable.External;

/// <summary>
/// 自動任務因為卡住／不支援的步驟／錯誤而停下來，或是走到需要玩家親自處理的步驟時，
/// 請 TataruPraise 用語音喊一句「需要幫忙」。純通知，不影響 Questionable 的任何流程。
/// </summary>
/// <remarks>
/// 🔴 <b>契約名與情境鍵都是逐字常數，不要「順手改成好看一點」。</b>Dalamud 的 CallGate 是純字串比對，
/// 名字錯了不會有任何錯誤訊息，只會永遠拿到「沒有人註冊」——靜默斷線。
/// 權威定義在 <c>TataruPraise/TataruPraise/IpcContract.cs</c>（契約名）與
/// <c>TataruPraise/TataruPraise/Core/PraiseCategory.cs</c>（情境鍵 <c>NeedHelp</c>）。
/// <para>
/// 📌 <b>沒有需要 Dispose 的東西。</b><c>GetIpcSubscriber</c> 拿到的是訂閱端，不需要退訂，
/// 所以這個服務不會出現在任何 <c>Dispose()</c> 路徑上——也就沒有「Dispose 裡無防護的 IPC 呼叫」那個雷。
/// </para>
/// <para>
/// 🔴 <b>所有呼叫點都必須在主執行緒上。</b>目前的呼叫點分別在 framework update
/// （<see cref="Controller.QuestController"/>、<see cref="Controller.MiniTaskController{T}"/>、
/// <see cref="Controller.Utils.PartyWatchDog"/>）與任務執行器的 Start
/// （<c>SendNotification.Executor</c>）上，本來就跑在主執行緒。
/// </para>
/// </remarks>
internal sealed class TataruPraiseIpc(
    IDalamudPluginInterface pluginInterface,
    Configuration configuration,
    ILogger<TataruPraiseIpc> logger)
{
    /// <summary>對方外掛的內部名稱，只用在記錄檔的措辭上；判斷在不在一律靠 IPC 本身。</summary>
    public const string PluginName = "TataruPraise";

    /// <summary><c>Func&lt;bool&gt;</c>：現在有沒有辦法出聲（總開關開著、而且池裡有已合成的句子）。</summary>
    public const string IsAvailableIpcName = "TataruPraise.IsAvailable";

    /// <summary><c>Func&lt;string, bool&gt;</c>：從指定情境的誇獎池挑一句念。</summary>
    public const string PraiseIpcName = "TataruPraise.Praise";

    /// <summary>
    /// 送過去的情境鍵。<b>逐字對應 TataruPraise 內建情境 <c>PraiseCategory.NeedHelp</c>。</b>
    /// 池裡沒有這個情境時對方只會寫一行記錄並回 <see langword="false"/>，不會出錯。
    /// </summary>
    public const string NeedHelpCategory = "需要幫忙";

    private readonly ICallGateSubscriber<bool> _isAvailable =
        pluginInterface.GetIpcSubscriber<bool>(IsAvailableIpcName);

    private readonly ICallGateSubscriber<string, bool> _praise =
        pluginInterface.GetIpcSubscriber<string, bool>(PraiseIpcName);

    /// <summary>「對方沒安裝」只寫一次記錄，不要每次停下來都刷一行。</summary>
    private bool _loggedNotInstalled;

    /// <summary>
    /// 請塔塔露喊一句「需要幫忙」。
    /// </summary>
    /// <param name="reason">為什麼需要人工介入，只寫進記錄檔，不會送給對方。</param>
    /// <remarks>
    /// 🔴 <b>這個方法自己沒有去重。</b>呼叫端必須確定自己站在「狀態邊緣」上——
    /// 也就是「剛剛才從執行中變成停下來」，而不是每一幀都會走到的輪詢路徑。
    /// （<c>QuestController.Stop</c> 的 <c>IsRunning || AutomationType != Manual</c> 那個判斷本身就是邊緣：
    /// 第二幀進不去。）放到輪詢路徑上的話，失敗形式是「一直念」，不是報錯。
    /// </remarks>
    public void NotifyNeedHelp(string reason)
    {
        if (!configuration.Notifications.PraiseWithTataru)
        {
            return;
        }

        try
        {
            // 先問「現在出得了聲嗎」。對方沒安裝／沒載入的話這一行就會擲 IpcNotReadyError，
            // 下面的 Praise 根本不會被呼叫到。
            if (!_isAvailable.InvokeFunc())
            {
                logger.LogDebug("{PluginName} 目前不方便出聲（總開關關著，或誇獎池沒有已合成的句子），這次不喊：{Reason}",
                    PluginName, reason);
                return;
            }

            bool queued = _praise.InvokeFunc(NeedHelpCategory);
            _loggedNotInstalled = false;

            // 📌 使用者跑 LogLevel 1，盲區只有 Verbose,Debug 收得到但單檔數十萬行會淹沒 —— 這是「到底有沒有喊出去」唯一的線索。
            // ⚠️ 回傳 false 不是錯誤：可能還在冷卻，也可能「需要幫忙」這個情境在池裡一句都沒有。
            if (queued)
            {
                logger.LogInformation("已請 {PluginName} 喊一句「{Category}」（原因：{Reason}）。",
                    PluginName, NeedHelpCategory, reason);
            }
            else
            {
                logger.LogInformation(
                    "{PluginName} 收到了但沒有播出（冷卻未過，或誇獎池裡沒有「{Category}」這個情境的句子）。原因：{Reason}",
                    PluginName, NeedHelpCategory, reason);
            }
        }
        catch(IpcNotReadyError)
        {
            // 對方沒安裝或還沒載入。這是預期內的狀態，不是錯誤。
            if (!_loggedNotInstalled)
            {
                _loggedNotInstalled = true;
                logger.LogInformation(
                    "想請 {PluginName} 在卡住時喊一句，但它沒有安裝或尚未載入（IPC「{IpcName}」沒有人註冊）。" +
                    "這個功能會維持靜默，Questionable 其餘流程完全不受影響。",
                    PluginName, IsAvailableIpcName);
            }
        }
        catch(Exception e)
        {
            // 對方版本不合、簽名對不上之類。同樣不要影響 Questionable 的流程。
            logger.LogInformation(e, "呼叫 {PluginName} 的 IPC 失敗，這次不喊（原因：{Reason}）。", PluginName, reason);
        }
    }
}

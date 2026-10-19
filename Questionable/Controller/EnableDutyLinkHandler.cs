using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Logging;
using Questionable.Data;
using System;
using System.Collections.Generic;

namespace Questionable.Controller;

/// <summary>聊天訊息裡那顆「點一下就把這一場加進白名單」的連結。</summary>
/// <remarks>
/// 契約：<see cref="GetPayload"/> 由任務執行器呼叫、點擊回呼由 Dalamud 派送，兩者都在
/// 框架執行緒上 ⇒ <see cref="_payloads"/> 不另外加鎖。
/// 📌 連結本身帶不了資料（<c>DalamudLinkPayload.Extra1</c> 的 setter 是 internal），所以把
/// CFC id 編進 commandId；最高位元用來分「任務戰鬥」與「副本」兩張白名單。
/// </remarks>
internal sealed class EnableDutyLinkHandler : IDisposable
{
    private const uint SinglePlayerDutyFlag = 0x8000_0000;

    private readonly IChatGui _chatGui;
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly Configuration _configuration;
    private readonly TerritoryData _territoryData;
    private readonly ILogger<EnableDutyLinkHandler> _logger;

    private readonly Dictionary<uint, DalamudLinkPayload> _payloads = [];

    public EnableDutyLinkHandler(
        IChatGui chatGui,
        IDalamudPluginInterface pluginInterface,
        Configuration configuration,
        TerritoryData territoryData,
        ILogger<EnableDutyLinkHandler> logger)
    {
        _chatGui = chatGui;
        _pluginInterface = pluginInterface;
        _configuration = configuration;
        _territoryData = territoryData;
        _logger = logger;
    }

    /// <summary>
    /// 取得（必要時註冊）某一場的連結。
    /// ⚠️ 一定要走這份快取：同一個 commandId 重複註冊會讓 Dalamud 的字典擲例外。
    /// </summary>
    public DalamudLinkPayload GetPayload(uint contentFinderConditionId, bool singlePlayerDuty)
    {
        uint commandId = contentFinderConditionId | (singlePlayerDuty ? SinglePlayerDutyFlag : 0);
        if (!_payloads.TryGetValue(commandId, out DalamudLinkPayload? payload))
        {
            payload = _chatGui.AddChatLinkHandler(commandId, OnLinkClicked);
            _payloads[commandId] = payload;
        }

        return payload;
    }

    private void OnLinkClicked(uint commandId, SeString _)
    {
        bool singlePlayerDuty = (commandId & SinglePlayerDutyFlag) != 0;
        uint cfcId = commandId & ~SinglePlayerDutyFlag;
        string name = _territoryData.GetContentFinderCondition(cfcId)?.Name ?? $"CFC {cfcId}";

        HashSet<uint> whitelist = singlePlayerDuty
            ? _configuration.SinglePlayerDuties.WhitelistedSinglePlayerDutyCfcIds
            : _configuration.Duties.WhitelistedDutyCfcIds;
        HashSet<uint> blacklist = singlePlayerDuty
            ? _configuration.SinglePlayerDuties.BlacklistedSinglePlayerDutyCfcIds
            : _configuration.Duties.BlacklistedDutyCfcIds;

        // 黑名單在 IsConfiguredToRun* 裡比白名單先判 ⇒ 這裡加了白名單也不會生效。
        // 刻意不順手把它從黑名單拿掉：那是使用者自己設的。
        if (blacklist.Contains(cfcId))
        {
            _chatGui.Print($"「{name}」目前被你設成停用，請到 Questionable 設定裡改。",
                CommandHandler.MessageTag, CommandHandler.TagColor);
            return;
        }

        if (whitelist.Add(cfcId))
        {
            _pluginInterface.SavePluginConfig(_configuration);
            _logger.LogInformation("Whitelisted {Kind} {CfcId} ({Name}) from chat link",
                singlePlayerDuty ? "quest battle" : "duty", cfcId, name);
        }

        // 刻意不自動接回任務：使用者裁決過「登入後自動接回」不做，這裡是同一族的行為。
        _chatGui.Print($"已啟用「{name}」，重新開始任務即可。",
            CommandHandler.MessageTag, CommandHandler.TagColor);
    }

    public void Dispose()
    {
        foreach (uint commandId in _payloads.Keys)
            _chatGui.RemoveChatLinkHandler(commandId);

        _payloads.Clear();
    }
}

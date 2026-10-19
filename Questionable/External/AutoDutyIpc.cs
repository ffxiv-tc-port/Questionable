using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Logging;
using Questionable.Controller.Steps;
using Questionable.Data;
using Questionable.Model.Questing;
using System;
using System.Collections.Generic;
using System.Globalization;
namespace Questionable.External;

internal sealed class AutoDutyIpc
(
    IDalamudPluginInterface pluginInterface,
    IFramework framework,
    Configuration configuration,
    TerritoryData territoryData,
    ILogger<AutoDutyIpc> logger)
{
    public enum DutyMode
    {
        Support = 1,
        UnsyncRegular = 2
    }

    /// <summary>每次交給 AutoDuty 只跑一輪；輪數由 <c>Run</c> 的參數決定，不讀 AutoDuty 的 <c>LoopTimes</c>。</summary>
    private const int LoopsPerRun = 1;

    private readonly Configuration _configuration = configuration;
    private readonly ICallGateSubscriber<uint, bool> _contentHasPath = pluginInterface.GetIpcSubscriber<uint, bool>("AutoDuty.ContentHasPath");
    private readonly IFramework _framework = framework;
    private readonly ICallGateSubscriber<bool> _isStopped = pluginInterface.GetIpcSubscriber<bool>("AutoDuty.IsStopped");
    private readonly ILogger<AutoDutyIpc> _logger = logger;
    private readonly ICallGateSubscriber<bool> _popConfigOverrides = pluginInterface.GetIpcSubscriber<bool>("AutoDuty.PopConfigOverrides");
    private readonly ICallGateSubscriber<object, bool> _pushConfigOverrides = pluginInterface.GetIpcSubscriber<object, bool>("AutoDuty.PushConfigOverrides");
    private readonly ICallGateSubscriber<uint, int, bool, object> _run = pluginInterface.GetIpcSubscriber<uint, int, bool, object>("AutoDuty.Run");
    private readonly ICallGateSubscriber<string, string, object> _setConfig = pluginInterface.GetIpcSubscriber<string, string, object>("AutoDuty.SetConfig");
    private readonly ICallGateSubscriber<object> _stop = pluginInterface.GetIpcSubscriber<object>("AutoDuty.Stop");
    private readonly TerritoryData _territoryData = territoryData;

    public bool IsConfiguredToRunContent(DutyOptions? dutyOptions)
    {
        return IsConfiguredToRunContent(dutyOptions, out _);
    }

    /// <param name="reason">擋下來的是哪一條；回傳 <c>true</c> 時是 <see cref="EDutyBlockReason.None"/>。</param>
    public bool IsConfiguredToRunContent(DutyOptions? dutyOptions, out EDutyBlockReason reason)
    {
        if (dutyOptions == null || dutyOptions.ContentFinderConditionId == 0)
        {
            reason = EDutyBlockReason.NoDutyData;
            return false;
        }

        if (!_configuration.Duties.RunInstancedContentWithAutoDuty)
        {
            reason = EDutyBlockReason.DisabledInConfig;
            return false;
        }

        if (_configuration.Duties.BlacklistedDutyCfcIds.Contains(dutyOptions.ContentFinderConditionId))
        {
            reason = EDutyBlockReason.Blacklisted;
            return false;
        }

        if (_configuration.Duties.WhitelistedDutyCfcIds.Contains(dutyOptions.ContentFinderConditionId) &&
            _territoryData.TryGetContentFinderCondition(dutyOptions.ContentFinderConditionId, out TerritoryData.ContentFinderConditionData? _))
        {
            reason = EDutyBlockReason.None;
            return true;
        }

        // Enabled 先判：HasPath 那一次 IPC 只在 Enabled 時才打。
        if (!dutyOptions.Enabled)
        {
            reason = EDutyBlockReason.NotEnabledByDefault;
            return false;
        }

        if (!HasPath(dutyOptions.ContentFinderConditionId))
        {
            reason = EDutyBlockReason.NoPath;
            return false;
        }

        reason = EDutyBlockReason.None;
        return true;
    }

    public bool HasPath(uint cfcId)
    {
        if (!_territoryData.TryGetContentFinderCondition(cfcId, out TerritoryData.ContentFinderConditionData? cfcData))
        {
            return false;
        }

        try
        {
            return _contentHasPath.InvokeFunc(cfcData.TerritoryId);
        }
        catch(IpcError e)
        {
            _logger.LogWarning("Unable to query AutoDuty for path in territory {TerritoryType}: {Message}",
                cfcData.TerritoryId, e.Message);
            return false;
        }
    }

    /// <summary>
    /// 模式與 Unsynced 走 AutoDuty 的暫時覆寫層（不寫進存檔、AutoDuty 停止時自己還原）；
    /// 覆寫端點不存在或拒收時才退回 <c>SetConfig</c>，那條路會改掉使用者存檔裡的 AutoDuty 設定。
    /// </summary>
    public void StartInstance(uint cfcId, DutyMode dutyMode)
    {
        if (!_territoryData.TryGetContentFinderCondition(cfcId, out TerritoryData.ContentFinderConditionData? cfcData))
        {
            throw new TaskException($"Unknown ContentFinderConditionId {cfcId}");
        }

        string unsynced = $"{dutyMode == DutyMode.UnsyncRegular}";
        string dutyModeName = dutyMode switch
        {
            DutyMode.Support => "Support",
            DutyMode.UnsyncRegular => "Regular",
            var _ => throw new ArgumentOutOfRangeException(nameof(dutyMode), dutyMode, null)
        };

        bool overridden = TryPushConfigOverrides(unsynced, dutyModeName);
        bool runReturned = false;
        try
        {
            if (!overridden)
            {
                _setConfig.InvokeAction("Unsynced", unsynced);
                _setConfig.InvokeAction("dutyModeEnum", dutyModeName);
            }

            _run.InvokeAction(cfcData.TerritoryId, LoopsPerRun, !_configuration.Advanced.DisableAutoDutyBareMode);
            runReturned = true;
        }
        catch(IpcError e)
        {
            throw new TaskException($"Unable to run content with AutoDuty: {e.Message}", e);
        }
        finally
        {
            if (overridden)
            {
                PopConfigOverridesIfNotStarted(runReturned);
            }
        }
    }

    private bool TryPushConfigOverrides(string unsynced, string dutyModeName)
    {
        // LoopTimes 一起覆寫，只是讓 Run 自己寫回的「LoopTimes = 1」落在被覆寫的欄位上（存檔寫使用者的值、停止時還原）。
        Dictionary<string, string> overrides = new()
        {
            ["Unsynced"] = unsynced,
            ["dutyModeEnum"] = dutyModeName,
            ["LoopTimes"] = LoopsPerRun.ToString(CultureInfo.InvariantCulture),
        };

        bool? accepted = IpcInvoke.SafeFunc<bool?>(() => _pushConfigOverrides.InvokeFunc(overrides), fallback: null,
            _logger, "Unable to push temporary config overrides to AutoDuty");
        if (accepted == true)
        {
            return true;
        }

        _logger.LogInformation(
            "AutoDuty {Reason} the temporary config overrides, falling back to AutoDuty.SetConfig (this changes the saved AutoDuty settings)",
            accepted == null ? "cannot take" : "rejected");
        return false;
    }

    /// <param name="runReturned"><c>false</c> 表示呼叫 <c>Run</c> 本身就擲了例外。</param>
    private void PopConfigOverridesIfNotStarted(bool runReturned)
    {
        // framework 執行緒上 AutoDuty 是就地執行 Run 的：回來時仍是停止狀態＝沒開始跑，也就永遠等不到它停止時的自動還原。
        // 不在 framework 執行緒時 Run 只是排進它的佇列，此刻判不了，交給 AutoDuty 停止時還原。
        if (runReturned && !_framework.IsInFrameworkUpdateThread)
        {
            return;
        }

        if (!IsStopped())
        {
            return;
        }

        _logger.LogInformation("AutoDuty did not start the duty, releasing the temporary config overrides");
        IpcInvoke.SafeFunc(() => _popConfigOverrides.InvokeFunc(), fallback: false, _logger,
            "Unable to release the temporary config overrides in AutoDuty");
    }

    public bool IsStopped()
    {
        try
        {
            return _isStopped.InvokeFunc();
        }
        catch(IpcError)
        {
            return true;
        }
    }

    public void Stop()
    {
        try
        {
            _logger.LogInformation("Calling AutoDuty.Stop");
            _stop.InvokeAction();
        }
        catch(IpcError e)
        {
            throw new TaskException($"Unable to stop AutoDuty: {e.Message}", e);
        }
    }
}

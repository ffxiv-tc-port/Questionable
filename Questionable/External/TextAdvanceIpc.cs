using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Logging;
using Questionable.Controller;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
namespace Questionable.External;

internal sealed class TextAdvanceIpc : IDisposable
{
    private const string TextAdvanceInternalName = "TextAdvance";

    /// <summary>端點註冊著時，多久重讀一次 <c>IExposedPlugin.IsLoaded</c>（列舉已安裝插件每次都會配置）。</summary>
    private const long LoadedRecheckIntervalMilliseconds = 1000;

    private readonly Configuration _configuration;
    private readonly ICallGateSubscriber<string, bool> _disableExternalControl;
    private readonly ICallGateSubscriber<string, ExternalTerritoryConfig, bool> _enableExternalControl;
    private readonly IFramework _framework;
    private readonly ICallGateSubscriber<bool> _isInExternalControl;
    private readonly ILogger<TextAdvanceIpc> _logger;
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly string _pluginName;
    private readonly QuestController _questController;
    private readonly object _loggedFailuresGate = new();
    private readonly HashSet<string> _loggedFailures = [];
    private bool _isExternalControlActivated;
    private bool _gateRegisteredLastFrame;
    private bool _textAdvanceLoaded;
    private long _nextLoadedRecheckAt;

    public TextAdvanceIpc(IDalamudPluginInterface pluginInterface, IFramework framework,
        QuestController questController, Configuration configuration, ILogger<TextAdvanceIpc> logger)
    {
        _pluginInterface = pluginInterface;
        _logger = logger;
        _framework = framework;
        _questController = questController;
        _configuration = configuration;
        _isInExternalControl = pluginInterface.GetIpcSubscriber<bool>("TextAdvance.IsInExternalControl");
        _enableExternalControl =
            pluginInterface.GetIpcSubscriber<string, ExternalTerritoryConfig, bool>(
                "TextAdvance.EnableExternalControl");
        _disableExternalControl = pluginInterface.GetIpcSubscriber<string, bool>("TextAdvance.DisableExternalControl");
        _pluginName = pluginInterface.InternalName;
        _framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        _framework.Update -= OnUpdate;
        if (_isExternalControlActivated)
        {
            // Plugin unload order is not guaranteed, so TextAdvance may already be gone and
            // InvokeFunc then throws IpcNotReadyError. Questionable tears down via
            // ServiceProvider.Dispose(), which does NOT swallow exceptions: one throw aborts disposal
            // of every remaining service and skips ECommonsMain.Dispose(). If TextAdvance is already
            // unloaded, its external-control state died with it and there is nothing to release.
            try
            {
                _disableExternalControl.InvokeFunc(_pluginName);
            }
            catch (Exception e)
            {
                ECommons.DalamudServices.Svc.Log.Warning(e,
                    "Could not release TextAdvance external control during dispose "
                    + "(TextAdvance is most likely already unloaded)");
            }
        }
    }

    // 同插件的 Framework.Update 處理器共用一個 try/catch：這裡擲例外會讓排在後面的
    // YesAlreadyIpc.OnUpdate 那一幀整個被跳過 ⇒ 不在就不呼叫，呼叫失敗也就地接住。
    private void OnUpdate(IFramework framework)
    {
        bool hasActiveQuest = _questController.IsRunning ||
                              _questController.AutomationType != QuestController.EAutomationType.Manual;
        bool wantsControl = _configuration.General.ConfigureTextAdvance && hasActiveQuest;
        if (!wantsControl && !_isExternalControlActivated)
        {
            return;
        }

        if (!IsTextAdvanceLoaded())
        {
            // 外部控制狀態存在 TextAdvance 自己那邊，它不在了就跟著沒了，沒有東西要交回。
            _isExternalControlActivated = false;
            return;
        }

        try
        {
            if (wantsControl)
            {
                if (!_isInExternalControl.InvokeFunc())
                {
                    if (_enableExternalControl.InvokeFunc(
                        _pluginName, CreateExternalTerritoryConfig(_configuration.General.DontSkipCutscenes)))
                    {
                        _isExternalControlActivated = true;
                    }
                }
            }
            else
            {
                if (_disableExternalControl.InvokeFunc(_pluginName) || !_isInExternalControl.InvokeFunc())
                {
                    _isExternalControlActivated = false;
                }
            }
        }
        catch (Exception e)
        {
            LogFailureOnce(wantsControl ? "EnableExternalControl" : "DisableExternalControl", e);
        }
    }

    /// <summary>
    /// TextAdvance 有載入、而且端點註冊著。框架執行緒呼叫。端點沒註冊當幀就回 false；
    /// <c>IsLoaded</c> 在註冊狀態剛變化時與之後每 <see cref="LoadedRecheckIntervalMilliseconds"/> 毫秒重讀。
    /// </summary>
    private bool IsTextAdvanceLoaded()
    {
        if (!_isInExternalControl.HasFunction)
        {
            _gateRegisteredLastFrame = false;
            return false;
        }

        long now = Environment.TickCount64;
        if (!_gateRegisteredLastFrame || now >= _nextLoadedRecheckAt)
        {
            _gateRegisteredLastFrame = true;
            _nextLoadedRecheckAt = now + LoadedRecheckIntervalMilliseconds;
            _textAdvanceLoaded = _pluginInterface.InstalledPlugins
                .Any(x => x.InternalName == TextAdvanceInternalName && x.IsLoaded);
        }

        return _textAdvanceLoaded;
    }

    /// <summary>同一種失敗（階段＋例外型別）整個插件生命週期只記一次。</summary>
    private void LogFailureOnce(string stage, Exception e)
    {
        string key = $"{stage}|{e.GetType().FullName}";
        lock (_loggedFailuresGate)
        {
            if (!_loggedFailures.Add(key))
            {
                return;
            }
        }

        _logger.LogInformation(e, "呼叫 TextAdvance 的 {Stage} 失敗，略過這一幀（同一種失敗只記這一次）", stage);
    }

    private static ExternalTerritoryConfig CreateExternalTerritoryConfig(bool dontSkipCutscenes)
    {
        return new()
        {
            EnableQuestAccept = true,
            EnableQuestComplete = true,
            EnableRewardPick = true,
            EnableRequestHandin = true,
            EnableCutsceneEsc = !dontSkipCutscenes,
            EnableCutsceneSkipConfirm = !dontSkipCutscenes,
            EnableTalkSkip = !dontSkipCutscenes,
            EnableRequestFill = true,
            EnableAutoInteract = false
        };
    }

    [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
    public sealed class ExternalTerritoryConfig
    {
#pragma warning disable CS0414 // Field is assigned but its value is never used
        public bool? EnableQuestAccept;
        public bool? EnableQuestComplete;
        public bool? EnableRewardPick;
        public bool? EnableRequestHandin;
        public bool? EnableCutsceneEsc;
        public bool? EnableCutsceneSkipConfirm;
        public bool? EnableTalkSkip;
        public bool? EnableRequestFill;
        public bool? EnableAutoInteract;
#pragma warning restore CS0414 // Field is assigned but its value is never used
    }
}

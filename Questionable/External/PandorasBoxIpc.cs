using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Logging;
using Questionable.Controller;
using Questionable.Data;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
namespace Questionable.External;

internal sealed class PandorasBoxIpc : IDisposable
{
    private static readonly ImmutableHashSet<string> ConflictingFeatures = new HashSet<string>
    {
        // Actions
        "Auto-Meditation",
        "Auto-Motif (Out of Combat)",
        "Auto-Mount after Combat",
        "Auto-Mount after Gathering",
        "Auto-Peleton",
        "Auto-Sprint in Sanctuaries",
        "Auto-select Turn-ins",
        "Auto-Sync FATEs",

        // Targets
        "Auto-interact with Gathering Nodes",

        // Other
        "Pandora Quick Gather"
    }.ToImmutableHashSet();
    private readonly IClientState _clientState;

    private readonly IFramework _framework;

    private readonly ICallGateSubscriber<string, bool?> _getFeatureEnabled;
    private readonly ILogger<PandorasBoxIpc> _logger;
    private readonly QuestController _questController;
    private readonly ICallGateSubscriber<string, bool, object?> _setFeatureEnabled;
    private readonly TerritoryData _territoryData;

    private bool _loggedIpcError;
    private HashSet<string>? _pausedFeatures;
    private DateTime _nextPandoraRetryAt = DateTime.MinValue;

    public PandorasBoxIpc(IDalamudPluginInterface pluginInterface,
        IFramework framework,
        QuestController questController,
        TerritoryData territoryData,
        IClientState clientState,
        ILogger<PandorasBoxIpc> logger)
    {
        _framework = framework;
        _questController = questController;
        _territoryData = territoryData;
        _clientState = clientState;
        _logger = logger;
        _getFeatureEnabled = pluginInterface.GetIpcSubscriber<string, bool?>("PandorasBox.GetFeatureEnabled");
        _setFeatureEnabled = pluginInterface.GetIpcSubscriber<string, bool, object?>("PandorasBox.SetFeatureEnabled");
        logger.LogInformation("Pandora's Box auto active time maneuver enabled: {IsAtmEnabled}",
            IsAutoActiveTimeManeuverEnabled);

        _framework.Update += OnUpdate;
    }

    public bool IsAutoActiveTimeManeuverEnabled
    {
        get
        {
            try
            {
                return _getFeatureEnabled.InvokeFunc("Auto Active Time Maneuver") == true;
            }
            catch(IpcNotReadyError)
            {
                // 同 DisableConflictingFeatures():IPC 還沒註冊時安靜跳過,
                // 保留 _loggedIpcError 給真正的錯誤用。
                return false;
            }
            catch(IpcError e)
            {
                if (!_loggedIpcError)
                {
                    _loggedIpcError = true;
                    _logger.LogDebug(e, "Pandora's Box IPC is unavailable; the optional integration will be skipped");
                }

                return false;
            }
        }
    }

    public void Dispose()
    {
        _framework.Update -= OnUpdate;
        RestoreConflictingFeatures();
    }

    private void OnUpdate(IFramework framework)
    {
        bool hasActiveQuest = _questController.IsRunning ||
                              _questController.AutomationType != QuestController.EAutomationType.Manual;
        if (hasActiveQuest && !_territoryData.IsDutyInstance(_clientState.TerritoryType))
        {
            DisableConflictingFeatures();
        }
        else
        {
            RestoreConflictingFeatures();
        }
    }

    private void DisableConflictingFeatures()
    {
        if (_pausedFeatures != null || DateTime.UtcNow < _nextPandoraRetryAt)
        {
            return;
        }

        _pausedFeatures = [];

        foreach(string feature in ConflictingFeatures)
        {
            try
            {
                bool? isEnabled = _getFeatureEnabled.InvokeFunc(feature);
                if (isEnabled == true)
                {
                    _setFeatureEnabled.InvokeAction(feature, false);
                    _pausedFeatures.Add(feature);
                    _logger.LogInformation("Paused Pandora's Box feature: {Feature}", feature);
                }
            }
            catch(IpcNotReadyError)
            {
                // Pandora's Box 是選配整合且非同步初始化 IPC。
                // 不要每個 feature 各印一次警告；短暫延遲後重試，
                // 讓還在啟動中的提供者之後仍能被安全偵測到。
                _pausedFeatures = null;
                _nextPandoraRetryAt = DateTime.UtcNow.AddSeconds(1);
                if (!_loggedIpcError)
                {
                    _loggedIpcError = true;
                    _logger.LogDebug("Pandora's Box IPC is not registered; retrying the optional integration later");
                }

                return;
            }
            catch(IpcError e)
            {
                _logger.LogWarning(e, "Failed to pause Pandora's Box feature: {Feature}", feature);
            }
        }
    }

    private void RestoreConflictingFeatures()
    {
        if (_pausedFeatures == null)
        {
            return;
        }

        foreach(string feature in _pausedFeatures)
        {
            try
            {
                _setFeatureEnabled.InvokeAction(feature, true);
                _logger.LogInformation("Restored Pandora's Box feature: {Feature}", feature);
            }
            catch(IpcError e)
            {
                _logger.LogWarning(e, "Failed to restore Pandora's Box feature: {Feature}", feature);
            }
        }

        _pausedFeatures = null;
        _nextPandoraRetryAt = DateTime.MinValue;
    }
}

using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Logging;
using Questionable.Data;
using Questionable.Model.Questing;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
namespace Questionable.External;

internal sealed class BossModIpc
(
    IDalamudPluginInterface pluginInterface,
    Configuration configuration,
    ICommandManager commandManager,
    TerritoryData territoryData,
    ILogger<BossModIpc> logger) : IDisposable
{
    public enum EPreset
    {
        Overworld,
        QuestBattle,
        NormalMovement
    }

    private const string PluginName = "BossMod";

    private static readonly ReadOnlyDictionary<EPreset, PresetDefinition> PresetDefinitions = new Dictionary<EPreset, PresetDefinition>
    {
        { EPreset.Overworld, new("Questionable", "Overworld") },
        { EPreset.QuestBattle, new("Questionable - Quest Battles", "QuestBattle") },
        { EPreset.NormalMovement, new("Questionable - Normal Movement", "NormalMovement") }
    }.AsReadOnly();
    private readonly ICallGateSubscriber<bool> _clearPreset = pluginInterface.GetIpcSubscriber<bool>($"{PluginName}.Presets.ClearActive");
    private readonly ICommandManager _commandManager = commandManager;

    private readonly Configuration _configuration = configuration;
    private readonly ICallGateSubscriber<string, bool, bool> _createPreset = pluginInterface.GetIpcSubscriber<string, bool, bool>($"{PluginName}.Presets.Create");
    private readonly ICallGateSubscriber<string, string?> _getPreset = pluginInterface.GetIpcSubscriber<string, string?>($"{PluginName}.Presets.Get");
    private readonly ICallGateSubscriber<string?> _getActivePreset = pluginInterface.GetIpcSubscriber<string?>($"{PluginName}.Presets.GetActive");
    private readonly ICallGateSubscriber<string, bool> _setPreset = pluginInterface.GetIpcSubscriber<string, bool>($"{PluginName}.Presets.SetActive");
    private readonly TerritoryData _territoryData = territoryData;
    private readonly ILogger<BossModIpc> _logger = logger;

    private const string KeyEnableQuestBattles = "ZoneModuleConfig.EnableQuestBattles";
    private const string KeyClearPresetOnCombatEnd = "AutorotationConfig.ClearPresetOnCombatEnd";
    private readonly string _owner = pluginInterface.InternalName;
    private readonly ICallGateSubscriber<string, string, bool, bool> _setTransientBool =
        pluginInterface.GetIpcSubscriber<string, string, bool, bool>($"{PluginName}.Configuration.SetTransientBool");
    private readonly ICallGateSubscriber<string, int> _clearAllTransient =
        pluginInterface.GetIpcSubscriber<string, int>($"{PluginName}.Configuration.ClearAllTransient");

    /// <summary>經 BMR 的執行期覆寫端點設過值、還沒清掉；只是省掉不必要的 IPC，BMR 在本插件卸載時也會自己清。</summary>
    private volatile bool _hasTransientOverrides;

    /// <summary>EnableQuestBattles 是退回 <c>/bmr cfg</c>（會存檔）開的，關的時候也要用指令寫回 false。</summary>
    private volatile bool _questBattlesEnabledByCommand;

    /// <summary>
    /// Operations whose IPC failure has already been logged; keeps per-frame or repeated
    /// task paths from flooding the log with the same message.
    /// </summary>
    private readonly HashSet<string> _loggedIpcErrors = [];

    private void LogIpcFailure(IpcError e, string operation)
    {
        if (MarkLoggedOnce(operation))
        {
            _logger.LogWarning(e, "BossMod IPC call {Operation} failed, is BossMod installed and up to date?", operation);
        }
    }

    // 停止路徑可能從 UI 或 IPC 執行緒進來，HashSet 不是執行緒安全的。
    private bool MarkLoggedOnce(string operation)
    {
        lock (_loggedIpcErrors)
        {
            return _loggedIpcErrors.Add(operation);
        }
    }

    public bool IsSupported()
    {
        try
        {
            return _getPreset.HasFunction;
        }
        catch(IpcError)
        {
            return false;
        }
    }

    /// <returns><c>true</c> if the preset was applied; <c>false</c> if BossMod could not be reached.</returns>
    public bool SetPreset(EPreset preset)
    {
        PresetDefinition definition = PresetDefinitions[preset];
        try
        {
            if (_getPreset.InvokeFunc(definition.Name) == null)
            {
                _createPreset.InvokeFunc(definition.Content, true);
            }

            _setPreset.InvokeFunc(definition.Name);
            return true;
        }
        catch(IpcError e)
        {
            LogIpcFailure(e, $"SetPreset({definition.Name})");
            return false;
        }
    }

    /// <summary>只清 Questionable 自己的預設；BMR 的 ClearActive 不看名字，作用中的是別人的就不動。</summary>
    /// <returns><c>true</c> if no Questionable preset is active anymore; <c>false</c> if BossMod could not be reached.</returns>
    public bool ClearPreset()
    {
        try
        {
            string? active = _getActivePreset.InvokeFunc();
            if (active == null)
            {
                return true;
            }

            if (!IsOwnPreset(active))
            {
                _logger.LogInformation("Not clearing the active BossMod preset '{Preset}': it was not set by Questionable", active);
                return true;
            }

            _clearPreset.InvokeFunc();
            return true;
        }
        catch(IpcError e)
        {
            LogIpcFailure(e, "ClearPreset");
            return false;
        }
    }

    // BMR 的預設名比對不分大小寫（PresetDatabase.NameComparison）。
    private static bool IsOwnPreset(string name)
    {
        foreach (PresetDefinition definition in PresetDefinitions.Values)
        {
            if (string.Equals(definition.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // TODO this should use your actual rotation plugin, not always BossMod
    // 🔴 指令前綴是 /bmr 與 /bmrai(BossmodReborn 本 fork 的註冊名),不是上游的 /vbm 與 /vbmai。
    //    ICommandManager.ProcessCommand 對未註冊的指令只會回傳 false、不寫任何 log,
    //    所以前綴打錯時這幾行是完全靜默的空操作 —— 換 BossMod 分支時要一起改。
    /// <returns><c>true</c> if the AI was enabled; <c>false</c> if BossMod could not be reached.</returns>
    public bool EnableAi(bool passive)
    {
        //_commandManager.ProcessCommand("/bmrai on");
        if (!TrySetTransientBool(KeyEnableQuestBattles, true))
        {
            _questBattlesEnabledByCommand = true;
            _commandManager.ProcessCommand("/bmr cfg ZoneModuleConfig EnableQuestBattles true");
        }

        if (!TrySetTransientBool(KeyClearPresetOnCombatEnd, false))
        {
            _commandManager.ProcessCommand("/bmr cfg Autorotation ClearPresetOnCombatEnd false");
        }

        return SetPreset(passive ? EPreset.Overworld : EPreset.QuestBattle);
    }

    /// <returns><c>true</c> if the AI was disabled; <c>false</c> if BossMod could not be reached.</returns>
    public bool DisableAi()
    {
        // 刻意不送 /bmrai off：EnableAi 從不開 BMR AI，關掉的只會是使用者或別的插件開的那個。
        // 有覆寫端點時只撤掉自己的覆寫、BMR 回到使用者自己的設定；舊版 BMR 沒有端點才照舊寫 false。
        bool cleared = TryClearAllTransient();
        if (!cleared || _questBattlesEnabledByCommand)
        {
            _questBattlesEnabledByCommand = false;
            _commandManager.ProcessCommand("/bmr cfg ZoneModuleConfig EnableQuestBattles false");
        }

        return ClearPreset();
    }

    /// <summary>撤掉 Questionable 在 BMR 設的執行期覆寫；沒設過就不呼叫。任何執行緒皆可（BMR 端自帶鎖）。</summary>
    public void ClearTransientOverrides()
    {
        if (_hasTransientOverrides)
        {
            TryClearAllTransient();
        }
    }

    /// <returns><c>false</c>＝端點不存在、型別不符或 BMR 拒絕，呼叫端要退回 <c>/bmr cfg</c>。</returns>
    private bool TrySetTransientBool(string key, bool value)
    {
        try
        {
            if (!_setTransientBool.HasFunction)
            {
                LogFallbackOnce("SetTransientBool",
                    "BossMod has no runtime config override endpoint (older version), falling back to /bmr cfg (saved to its config file)");
                return false;
            }

            if (_setTransientBool.InvokeFunc(_owner, key, value))
            {
                _hasTransientOverrides = true;
                return true;
            }

            LogFallbackOnce($"SetTransientBool({key})",
                $"BossMod rejected the runtime override for {key}, falling back to /bmr cfg (saved to its config file)");
            return false;
        }
        catch(IpcError e)
        {
            LogIpcFailure(e, $"SetTransientBool({key})");
            return false;
        }
    }

    /// <returns><c>true</c>＝端點存在且呼叫成功（不論清掉幾筆）。</returns>
    private bool TryClearAllTransient()
    {
        try
        {
            if (!_clearAllTransient.HasFunction)
            {
                return false;
            }

            _clearAllTransient.InvokeFunc(_owner);
            _hasTransientOverrides = false;
            return true;
        }
        catch(IpcError e)
        {
            LogIpcFailure(e, "ClearAllTransient");
            return false;
        }
    }

    private void LogFallbackOnce(string operation, string message)
    {
        if (MarkLoggedOnce(operation))
        {
            _logger.LogInformation("{Message}", message);
        }
    }

    public void Dispose()
    {
        // 卸載路徑不可擲例外：會中斷 ServiceProvider 對其他服務的 Dispose。
        try
        {
            ClearTransientOverrides();
        }
        catch(Exception e)
        {
            _logger.LogWarning(e, "Failed to clear BossMod runtime config overrides on unload");
        }
    }

    public bool IsConfiguredToRunSoloInstance(ElementId questId, SinglePlayerDutyOptions? dutyOptions)
    {
        return IsConfiguredToRunSoloInstance(questId, dutyOptions, out _, out _);
    }

    /// <param name="reason">擋下來的是哪一條；回傳 <c>true</c> 時是 <see cref="EDutyBlockReason.None"/>。</param>
    /// <param name="contentFinderConditionId">這一場的 CFC id，查不到（或還沒查）時是 <c>0</c>。</param>
    public bool IsConfiguredToRunSoloInstance(ElementId questId, SinglePlayerDutyOptions? dutyOptions,
        out EDutyBlockReason reason, out uint contentFinderConditionId)
    {
        contentFinderConditionId = 0;

        if (!IsSupported())
        {
            reason = EDutyBlockReason.PluginUnavailable;
            return false;
        }

        if (!_configuration.SinglePlayerDuties.RunSoloInstancesWithBossMod)
        {
            reason = EDutyBlockReason.DisabledInConfig;
            return false;
        }

        if (questId.Value.Equals(5325)) // Valentiones 2026
        {
            reason = EDutyBlockReason.None;
            return true;
        }

        dutyOptions ??= new();
        if (!_territoryData.TryGetContentFinderConditionForSoloInstance(questId, dutyOptions.Index, out TerritoryData.ContentFinderConditionData? cfcData))
        {
            reason = EDutyBlockReason.NoDutyData;
            return false;
        }

        contentFinderConditionId = cfcData.ContentFinderConditionId;

        if (_configuration.SinglePlayerDuties.BlacklistedSinglePlayerDutyCfcIds.Contains(contentFinderConditionId))
        {
            reason = EDutyBlockReason.Blacklisted;
            return false;
        }

        if (_configuration.SinglePlayerDuties.WhitelistedSinglePlayerDutyCfcIds.Contains(contentFinderConditionId))
        {
            reason = EDutyBlockReason.None;
            return true;
        }

        reason = dutyOptions.Enabled ? EDutyBlockReason.None : EDutyBlockReason.NotEnabledByDefault;
        return dutyOptions.Enabled;
    }

    private sealed class PresetDefinition(string name, string fileName)
    {
        public string Name { get; } = name;
        public string Content { get; } = LoadPreset(fileName);

        private static string LoadPreset(string name)
        {
            Stream stream =
                typeof(BossModIpc).Assembly.GetManifestResourceStream(
                    $"Questionable.Controller.CombatModules.BossModPreset.{name}") ??
                throw new InvalidOperationException($"Preset {name} was not found");
            using StreamReader reader = new(stream);
            return reader.ReadToEnd();
        }
    }
}

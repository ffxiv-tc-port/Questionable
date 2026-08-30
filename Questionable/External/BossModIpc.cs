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
    ILogger<BossModIpc> logger)
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
    private readonly ICallGateSubscriber<string, bool> _setPreset = pluginInterface.GetIpcSubscriber<string, bool>($"{PluginName}.Presets.SetActive");
    private readonly TerritoryData _territoryData = territoryData;
    private readonly ILogger<BossModIpc> _logger = logger;

    /// <summary>
    /// Operations whose IPC failure has already been logged; keeps per-frame or repeated
    /// task paths from flooding the log with the same message.
    /// </summary>
    private readonly HashSet<string> _loggedIpcErrors = [];

    private void LogIpcFailure(IpcError e, string operation)
    {
        if (_loggedIpcErrors.Add(operation))
        {
            _logger.LogWarning(e, "BossMod IPC call {Operation} failed, is BossMod installed and up to date?", operation);
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

    /// <returns><c>true</c> if the preset was cleared; <c>false</c> if BossMod could not be reached.</returns>
    public bool ClearPreset()
    {
        try
        {
            _clearPreset.InvokeFunc();
            return true;
        }
        catch(IpcError e)
        {
            LogIpcFailure(e, "ClearPreset");
            return false;
        }
    }

    // TODO this should use your actual rotation plugin, not always BossMod
    // 🔴 指令前綴是 /bmr 與 /bmrai(BossmodReborn 本 fork 的註冊名),不是上游的 /vbm 與 /vbmai。
    //    ICommandManager.ProcessCommand 對未註冊的指令只會回傳 false、不寫任何 log,
    //    所以前綴打錯時這幾行是完全靜默的空操作 —— 換 BossMod 分支時要一起改。
    /// <returns><c>true</c> if the AI was enabled; <c>false</c> if BossMod could not be reached.</returns>
    public bool EnableAi(bool passive)
    {
        //_commandManager.ProcessCommand("/bmrai on");
        _commandManager.ProcessCommand("/bmr cfg ZoneModuleConfig EnableQuestBattles true");
        _commandManager.ProcessCommand("/bmr cfg Autorotation ClearPresetOnCombatEnd false");
        return SetPreset(passive ? EPreset.Overworld : EPreset.QuestBattle);
    }

    /// <returns><c>true</c> if the AI was disabled; <c>false</c> if BossMod could not be reached.</returns>
    public bool DisableAi()
    {
        _commandManager.ProcessCommand("/bmrai off");
        _commandManager.ProcessCommand("/bmr cfg ZoneModuleConfig EnableQuestBattles false");
        return ClearPreset();
    }

    public bool IsConfiguredToRunSoloInstance(ElementId questId, SinglePlayerDutyOptions? dutyOptions)
    {
        if (!IsSupported())
        {
            return false;
        }

        if (!_configuration.SinglePlayerDuties.RunSoloInstancesWithBossMod)
        {
            return false;
        }

        if (questId.Value.Equals(5325)) // Valentiones 2026
        {
            return true;
        }

        dutyOptions ??= new();
        if (!_territoryData.TryGetContentFinderConditionForSoloInstance(questId, dutyOptions.Index, out TerritoryData.ContentFinderConditionData? cfcData))
        {
            return false;
        }

        if (_configuration.SinglePlayerDuties.BlacklistedSinglePlayerDutyCfcIds.Contains(cfcData
            .ContentFinderConditionId))
        {
            return false;
        }

        if (_configuration.SinglePlayerDuties.WhitelistedSinglePlayerDutyCfcIds.Contains(cfcData
            .ContentFinderConditionId))
        {
            return true;
        }

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

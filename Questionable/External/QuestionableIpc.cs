using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using ECommons.ExcelServices;
using JetBrains.Annotations;
using Lumina.Text.ReadOnly;
using Microsoft.Extensions.Logging;
using Questionable.Controller;
using Questionable.Data;
using Questionable.Functions;
using Questionable.Model;
using Questionable.Model.Questing;
using Questionable.Windows;
using Questionable.Windows.QuestComponents;
using Questionable.Windows.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
namespace Questionable.External;

/// <remarks>
/// 🔴 <b>這裡的每一個端點都跑在呼叫端外掛的執行緒上</b>（CallGate＝直接方法呼叫），
/// 所以全部包上 <see cref="IpcFrameworkGate"/>：已經在主執行緒上呼叫時就地執行，
/// 從別的執行緒進來才會被交回主執行緒。端點同步可達的東西：<c>TaskQueue._tasks</c> 與
/// <c>ManualPriorityQuests</c>（裸 <c>List</c>，UI 每幀迭代）、<c>QuestManager.Instance()-&gt;</c> 等原生讀取、
/// Lumina 的 <c>ExcelSheet</c>（內部列快取不保證執行緒安全）。
/// <para>
/// 📌 閘門逾時時回的「不可用」值都是該端點失敗路徑上本來就會回的值（<c>IsQuestLocked</c> 回
/// <see langword="true"/>＝當作鎖著，其餘查詢回 <see langword="false"/>／<see langword="null"/>／空字串）。
/// ⚠️ 例外是「優先任務」端點：平常無論如何都回 <see langword="true"/>，逾時時回 <see langword="false"/>。
/// </para>
/// </remarks>
internal sealed class QuestionableIpc : IDisposable
{
    private const string IpcIsRunning = "Questionable.IsRunning";
    private const string IpcGetCurrentQuestId = "Questionable.GetCurrentQuestId";
    private const string IpcGetCurrentStepData = "Questionable.GetCurrentStepData";
    private const string IpcGetCurrentlyActiveEventQuests = "Questionable.GetCurrentlyActiveEventQuests";
    private const string IpcStartQuest = "Questionable.StartQuest";
    private const string IpcStartSingleQuest = "Questionable.StartSingleQuest";
    private const string IpcIsQuestLocked = "Questionable.IsQuestLocked";
    private const string IpcIsQuestComplete = "Questionable.IsQuestComplete";
    private const string IpcIsReadyToAcceptQuest = "Questionable.IsReadyToAcceptQuest";
    private const string IpcIsQuestAccepted = "Questionable.IsQuestAccepted";
    private const string IpcIsQuestUnobtainable = "Questionable.IsQuestUnobtainable";
    private const string IpcImportQuestPriority = "Questionable.ImportQuestPriority";
    private const string IpcClearQuestPriority = "Questionable.ClearQuestPriority";
    private const string IpcAddQuestPriority = "Questionable.AddQuestPriority";
    private const string IpcInsertQuestPriority = "Questionable.InsertQuestPriority";
    private const string IpcExportQuestPriority = "Questionable.ExportQuestPriority";
    private const string IpcStartGathering = "Questionable.StartGathering";
    private const string IpcStartGatheringComplex = "Questionable.StartGatheringComplex";
    private const string IpcStop = "Questionable.Stop";
    private const string IpcRedoLookup = "Questionable.RedoLookup";
    private const string IpcRedoLookupIndex = "Questionable.RedoLookupIndex";
    private const string IpcStartAlliedSocietyDailies = "Questionable.StartAlliedSocietyDailies";
    private readonly ICallGateProvider<string, bool> _addQuestPriority;
    private readonly ICallGateProvider<bool> _clearQuestPriority;
    private readonly ICallGateProvider<string> _exportQuestPriority;
    private readonly ICallGateProvider<List<string>> _getCurrentlyActiveEventQuests;
    private readonly ICallGateProvider<string?> _getCurrentQuestId;
    private readonly ICallGateProvider<StepData?> _getCurrentStepData;
    private readonly ICallGateProvider<string, bool> _importQuestPriority;
    private readonly ICallGateProvider<int, string, bool> _insertQuestPriority;
    private readonly ICallGateProvider<string, bool> _isQuestAccepted;
    private readonly ICallGateProvider<string, bool> _isQuestComplete;
    private readonly ICallGateProvider<string, bool> _isQuestLocked;
    private readonly ICallGateProvider<string, bool> _isQuestUnobtainable;
    private readonly ICallGateProvider<string, bool> _isReadyToAcceptQuest;

    private readonly IpcFrameworkGate _gate;

    private const int MaxReportedRejections = 128;
    private readonly HashSet<string> _reportedRejections = [];

    private readonly ICallGateProvider<bool> _isRunning;
    private readonly ILogger<QuestionableIpc> _logger;

    private readonly QuestController _questController;
    private readonly QuestData _questData;
    private readonly QuestFunctions _questFunctions;
    private readonly QuestRegistry _questRegistry;
    private readonly ICallGateProvider<uint, string> _redoLookup;
    private readonly ICallGateProvider<uint, Tuple<string, int>> _redoLookupIndex;
    private readonly RedoUtil _redoUtil;
    private readonly ICallGateProvider<uint, uint, byte, int, bool> _startGathering;
    private readonly ICallGateProvider<uint, uint, byte, int, ushort, bool> _startGatheringComplex;
    private readonly ICallGateProvider<string, bool> _startQuest;
    private readonly ICallGateProvider<string, bool> _startSingleQuest;
    private readonly ICallGateProvider<bool> _startAlliedSocietyDailies;
    private readonly ICallGateProvider<string, bool> _stop;

    public QuestionableIpc(
        QuestController questController,
        EventInfoComponent eventInfoComponent,
        QuestRegistry questRegistry,
        QuestFunctions questFunctions,
        PriorityWindow priorityWindow,
        ILogger<QuestionableIpc> logger,
        IDalamudPluginInterface pluginInterface,
        IpcFrameworkGate gate,
        QuestData questData)
    {
        _questController = questController;
        _questRegistry = questRegistry;
        _questFunctions = questFunctions;
        _questData = questData;
        _logger = logger;
        _gate = gate;

        _isRunning = pluginInterface.GetIpcProvider<bool>(IpcIsRunning);
        _isRunning.RegisterFunc(() => _gate.Get(IpcIsRunning,
            () => questController.AutomationType != QuestController.EAutomationType.Manual || questController.IsRunning,
            false));

        _getCurrentQuestId = pluginInterface.GetIpcProvider<string?>(IpcGetCurrentQuestId);
        _getCurrentQuestId.RegisterFunc(() => _gate.Get<string?>(IpcGetCurrentQuestId,
            () => questController.CurrentQuest?.Quest.Id.ToString(), null));

        _getCurrentStepData = pluginInterface.GetIpcProvider<StepData?>(IpcGetCurrentStepData);
        _getCurrentStepData.RegisterFunc(() => _gate.Get<StepData?>(IpcGetCurrentStepData, GetStepData, null));

        _getCurrentlyActiveEventQuests =
            pluginInterface.GetIpcProvider<List<string>>(IpcGetCurrentlyActiveEventQuests);
        _getCurrentlyActiveEventQuests.RegisterFunc(() => _gate.Get<List<string>>(IpcGetCurrentlyActiveEventQuests,
            () => [.. eventInfoComponent.GetCurrentlyActiveEventQuests().Select(q => q.ToString())], []));

        _startQuest = pluginInterface.GetIpcProvider<string, bool>(IpcStartQuest);
        _startQuest.RegisterFunc(questId => _gate.Get(IpcStartQuest, () => StartQuest(questId, false), false));

        _startSingleQuest = pluginInterface.GetIpcProvider<string, bool>(IpcStartSingleQuest);
        _startSingleQuest.RegisterFunc(questId =>
            _gate.Get(IpcStartSingleQuest, () => StartQuest(questId, true), false));

        // 沒有可做的部族日常時回 false、不開始（不像 /qst start 會退回一般選任務規則）。
        _startAlliedSocietyDailies = pluginInterface.GetIpcProvider<bool>(IpcStartAlliedSocietyDailies);
        _startAlliedSocietyDailies.RegisterFunc(() => _gate.Get(IpcStartAlliedSocietyDailies,
            () => questController.TryStartAlliedSocietyDailies("IPC allied dailies"), false));

        _isQuestLocked = pluginInterface.GetIpcProvider<string, bool>(IpcIsQuestLocked);
        // ⚠️ 不可用時回 true：與這一支自己「查不到任務就當作鎖著」的既有預設一致。
        _isQuestLocked.RegisterFunc(questId => _gate.Get(IpcIsQuestLocked, () => IsQuestLocked(questId), true));

        _isQuestComplete = pluginInterface.GetIpcProvider<string, bool>(IpcIsQuestComplete);
        _isQuestComplete.RegisterFunc(questId =>
            _gate.Get(IpcIsQuestComplete, () => IsQuestComplete(questId), false));

        _isReadyToAcceptQuest = pluginInterface.GetIpcProvider<string, bool>(IpcIsReadyToAcceptQuest);
        _isReadyToAcceptQuest.RegisterFunc(questId =>
            _gate.Get(IpcIsReadyToAcceptQuest, () => IsReadyToAcceptQuest(questId), false));

        _isQuestAccepted = pluginInterface.GetIpcProvider<string, bool>(IpcIsQuestAccepted);
        _isQuestAccepted.RegisterFunc(questId =>
            _gate.Get(IpcIsQuestAccepted, () => IsQuestAccepted(questId), false));

        _isQuestUnobtainable = pluginInterface.GetIpcProvider<string, bool>(IpcIsQuestUnobtainable);
        _isQuestUnobtainable.RegisterFunc(questId =>
            _gate.Get(IpcIsQuestUnobtainable, () => IsQuestUnobtainable(questId), false));

        _importQuestPriority = pluginInterface.GetIpcProvider<string, bool>(IpcImportQuestPriority);
        _importQuestPriority.RegisterFunc(encoded =>
            _gate.Get(IpcImportQuestPriority, () => ImportQuestPriority(encoded), false));

        _addQuestPriority = pluginInterface.GetIpcProvider<string, bool>(IpcAddQuestPriority);
        _addQuestPriority.RegisterFunc(questId =>
            _gate.Get(IpcAddQuestPriority, () => AddQuestPriority(questId), false));

        _clearQuestPriority = pluginInterface.GetIpcProvider<bool>(IpcClearQuestPriority);
        _clearQuestPriority.RegisterFunc(() => _gate.Get(IpcClearQuestPriority, ClearQuestPriority, false));

        _insertQuestPriority = pluginInterface.GetIpcProvider<int, string, bool>(IpcInsertQuestPriority);
        _insertQuestPriority.RegisterFunc((index, questId) =>
            _gate.Get(IpcInsertQuestPriority, () => InsertQuestPriority(index, questId), false));

        _exportQuestPriority = pluginInterface.GetIpcProvider<string>(IpcExportQuestPriority);
        _exportQuestPriority.RegisterFunc(() =>
            _gate.Get(IpcExportQuestPriority, priorityWindow.EncodeQuestPriority, string.Empty));

        _startGathering = pluginInterface.GetIpcProvider<uint, uint, byte, int, bool>(IpcStartGathering);
        _startGathering.RegisterFunc((npcId, itemId, classJob, quantity) =>
            _gate.Get(IpcStartGathering, () => StartGathering(npcId, itemId, classJob, quantity), false));

        _startGatheringComplex = pluginInterface.GetIpcProvider<uint, uint, byte, int, ushort, bool>(IpcStartGatheringComplex);
        _startGatheringComplex.RegisterFunc((npcId, itemId, classJob, quantity, collectability) =>
            _gate.Get(IpcStartGatheringComplex,
                () => StartGatheringComplex(npcId, itemId, classJob, quantity, collectability), false));

        _stop = pluginInterface.GetIpcProvider<string, bool>(IpcStop);
        _stop.RegisterFunc(label => _gate.Get(IpcStop, () => Stop(label), false));

        _redoUtil = new();

        _redoLookup = pluginInterface.GetIpcProvider<uint, string>(IpcRedoLookup);
        _redoLookup.RegisterFunc(questId =>
            _gate.Get(IpcRedoLookup, () => RedoLookup(questId), string.Empty));

        _redoLookupIndex = pluginInterface.GetIpcProvider<uint, Tuple<string, int>>(IpcRedoLookupIndex);
        _redoLookupIndex.RegisterFunc(questId =>
            _gate.Get(IpcRedoLookupIndex, () => RedoLookupIndex(questId), new Tuple<string, int>(string.Empty, -1)));
    }

    public void Dispose()
    {
        _isRunning.UnregisterFunc();
        _getCurrentQuestId.UnregisterFunc();
        _getCurrentStepData.UnregisterFunc();
        _getCurrentlyActiveEventQuests.UnregisterFunc();
        _startQuest.UnregisterFunc();
        _startSingleQuest.UnregisterFunc();
        _startAlliedSocietyDailies.UnregisterFunc();
        _isQuestLocked.UnregisterFunc();
        _isQuestComplete.UnregisterFunc();
        _isReadyToAcceptQuest.UnregisterFunc();
        _isQuestAccepted.UnregisterFunc();
        _isQuestUnobtainable.UnregisterFunc();
        _importQuestPriority.UnregisterFunc();
        _addQuestPriority.UnregisterFunc();
        _clearQuestPriority.UnregisterFunc();
        _insertQuestPriority.UnregisterFunc();
        _exportQuestPriority.UnregisterFunc();
        _startGathering.UnregisterFunc();
        _startGatheringComplex.UnregisterFunc();
        _stop.UnregisterFunc();
        _redoLookup.UnregisterFunc();
        _redoLookupIndex.UnregisterFunc();
    }

    private bool StartQuest(string questId, bool single)
    {
        _logger.LogDebug($"StartQuest({questId},{single})");
        if (ElementId.TryFromString(questId, out ElementId? elementId) && elementId != null &&
            _questRegistry.TryGetQuest(elementId, out Quest? quest))
        {
            _questController.SetNextQuest(quest);
            if (single)
            {
                _questController.StartSingleQuest("IPCQuestSelection");
            }
            else
            {
                _questController.Start("IPCQuestSelection");
            }
            return true;
        }

        return false;
    }

    private StepData? GetStepData()
    {
        _logger.LogDebug("GetStepData()");
        QuestController.QuestProgress? progress = _questController.CurrentQuest;
        if (progress == null)
        {
            return null;
        }

        string questId = progress.Quest.Id.ToString();
        if (string.IsNullOrEmpty(questId))
        {
            return null;
        }

        QuestStep? step = progress.Quest.FindSequence(progress.Sequence)?.FindStep(progress.Step);
        if (step == null)
        {
            return null;
        }

        return new()
        {
            QuestId = questId,
            Sequence = progress.Sequence,
            Step = progress.Step,
            InteractionType = step.InteractionType.ToString(),
            Position = step.Position,
            TerritoryId = step.TerritoryId
        };
    }

    private bool IsQuestLocked(string questId)
    {
        _logger.LogDebug($"IsQuestLocked({questId})");
        if (ElementId.TryFromString(questId, out ElementId? elementId) && elementId != null &&
            _questRegistry.TryGetQuest(elementId, out Quest? _))
        {
            return _questFunctions.IsQuestLocked(elementId);
        }

        return true;
    }

    private bool IsQuestComplete(string questId)
    {
        _logger.LogDebug($"IsQuestComplete({questId})");
        if (ElementId.TryFromString(questId, out ElementId? elementId) && elementId != null &&
            IsQuestKindOrLog(IpcIsQuestComplete, questId, elementId))
        {
            return _questFunctions.IsQuestComplete(elementId);
        }
        return false;
    }

    private bool IsReadyToAcceptQuest(string questId)
    {
        _logger.LogDebug($"IsReadyToAcceptQuest({questId})");
        if (ElementId.TryFromString(questId, out ElementId? elementId) && elementId != null &&
            !_questData.IsMissingFromSheet(elementId) &&
            IsQuestKindOrLog(IpcIsReadyToAcceptQuest, questId, elementId))
        {
            return _questFunctions.IsReadyToAcceptQuest(elementId);
        }
        return false;
    }

    private bool IsQuestAccepted(string questId)
    {
        _logger.LogDebug($"IsQuestAccepted({questId})");
        if (ElementId.TryFromString(questId, out ElementId? elementId) && elementId != null &&
            IsQuestKindOrLog(IpcIsQuestAccepted, questId, elementId))
        {
            return _questFunctions.IsQuestAccepted(elementId);
        }
        return false;
    }

    private bool IsQuestUnobtainable(string questId)
    {
        _logger.LogDebug($"IsQuestUnobtainable({questId})");
        if (ElementId.TryFromString(questId, out ElementId? elementId) && elementId != null &&
            !_questData.IsMissingFromSheet(elementId))
        {
            return _questFunctions.IsQuestUnobtainable(elementId);
        }
        return false;
    }

    private bool ImportQuestPriority(string encodedQuestPriority)
    {
        _logger.LogDebug($"ImportQuestPriority({encodedQuestPriority})");
        List<ElementId> questElements = PriorityWindow.DecodeQuestPriority(encodedQuestPriority);
        _questController.ImportQuestPriority(questElements);
        return true;
    }

    private bool ClearQuestPriority()
    {
        _logger.LogDebug("ClearQuestPriority()");
        _questController.ClearQuestPriority();
        return true;
    }

    private bool AddQuestPriority(string questId)
    {
        _logger.LogDebug($"AddQuestPriority({questId})");
        if (ElementId.TryFromString(questId, out ElementId? elementId) && elementId != null &&
            !IsUnknownAlliedSociety(elementId) &&
            _questRegistry.IsKnownQuest(elementId))
        {
            return _questController.AddQuestPriority(elementId);
        }

        ReportIgnoredPriority(IpcAddQuestPriority, questId, elementId);
        return true;
    }

    private bool InsertQuestPriority(int index, string questId)
    {
        _logger.LogDebug($"InsertQuestPriority({index},{questId})");
        if (ElementId.TryFromString(questId, out ElementId? elementId) && elementId != null &&
            !IsUnknownAlliedSociety(elementId) &&
            _questRegistry.IsKnownQuest(elementId))
        {
            return _questController.InsertQuestPriority(index, elementId);
        }

        ReportIgnoredPriority(IpcInsertQuestPriority, questId, elementId);
        return true;
    }

    /// <summary>
    /// 認不得的編號照舊回 <see langword="true"/>（上游語意，呼叫端可能依賴），只留一行記錄說明沒加入的原因。
    /// </summary>
    private void ReportIgnoredPriority(string endpoint, string? input, ElementId? parsed)
    {
        if (!ShouldReportRejection(endpoint, input))
        {
            return;
        }

        string reason = parsed == null ? "不是可辨識的任務編號"
            : IsUnknownAlliedSociety(parsed) ? UnknownAlliedSocietyReason
            : "Questionable 沒有這個項目的路線";
        _logger.LogInformation("[Questionable IPC] {Endpoint} 收到「{Input}」：{Reason}，沒有加入優先清單。",
            endpoint, input, reason);
    }

    /// <summary>
    /// N（都市傳送網）與 C（風脈泉）也解析得出 <see cref="ElementId"/>，但不是任務，
    /// <see cref="QuestFunctions"/> 的完成／接取查詢遇到會擲 <see cref="ArgumentOutOfRangeException"/>。
    /// 回 <see langword="false"/> 時呼叫端要回該端點既有的失敗值，這裡已留一行記錄。
    /// </summary>
    /// <remarks>友好部族日常（A）只收本客戶端表裡的部族，見 <see cref="IsUnknownAlliedSociety"/>。</remarks>
    private bool IsQuestKindOrLog(string endpoint, string? input, ElementId elementId)
    {
        if ((elementId is QuestId or SatisfactionSupplyNpcId or AlliedSocietyDailyId or UnlockLinkId) &&
            !IsUnknownAlliedSociety(elementId))
        {
            return true;
        }

        if (!ShouldReportRejection(endpoint, input))
        {
            return false;
        }

        if (elementId is AlliedSocietyDailyId)
        {
            _logger.LogInformation("[Questionable IPC] {Endpoint} 收到「{Input}」：{Reason}，回傳 false。",
                endpoint, input, UnknownAlliedSocietyReason);
            return false;
        }

        string kind = elementId switch
        {
            AethernetId => "都市傳送網",
            AetherCurrentId => "風脈泉",
            _ => elementId.GetType().Name,
        };
        _logger.LogInformation("[Questionable IPC] {Endpoint} 收到「{Input}」：這是{Kind}的編號、不是任務，回傳 false。",
            endpoint, input, kind);
        return false;
    }

    private const string UnknownAlliedSocietyReason = "本客戶端的友好部族表裡沒有這個部族編號";

    /// <summary>
    /// 鎖定判定會把部族編號直接交給原生 <c>PlayerState.GetBeastTribeRank</c>，
    /// 原生端對未知編號有沒有邊界檢查不得而知，所以 IPC 入口只收表裡有的部族。UI 內部路徑不經過這裡。
    /// </summary>
    private bool IsUnknownAlliedSociety(ElementId elementId) =>
        elementId is AlliedSocietyDailyId alliedSocietyDailyId &&
        !_questData.IsKnownAlliedSociety(alliedSocietyDailyId.AlliedSociety);

    /// <summary>
    /// 同一個端點＋同一個輸入只記一次（呼叫端可能每幀輪詢）；表滿就清空重來。
    /// 鎖只是保險：閘門平常把本體交回主執行緒，但卸載瞬間的旁路會就地跑在呼叫端執行緒。鎖內只碰集合。
    /// </summary>
    private bool ShouldReportRejection(string endpoint, string? input)
    {
        lock(_reportedRejections)
        {
            if (_reportedRejections.Count >= MaxReportedRejections)
            {
                _reportedRejections.Clear();
            }

            return _reportedRejections.Add(endpoint + "|" + input);
        }
    }

    private bool StartGathering(uint npcId, uint itemId, byte classJob, int quantity)
    {
        return StartGatheringComplex(npcId, itemId, classJob, quantity);
    }

    private bool StartGatheringComplex(uint npcId, uint itemId, byte classJob = ((byte)Job.MIN), int quantity = 1, ushort collectability = 0)
    {
        _logger.LogDebug($"StartGatheringComplex({npcId},{itemId},{classJob},{quantity},{collectability})");
        return _questController.StartGathering(npcId, itemId, (Job)classJob, quantity, collectability);
    }

    private bool Stop(string label)
    {
        _logger.LogDebug($"Stop({label})");
        _questController.StopAllDueToConditionFailed($"IPC: {label}");
        return true;
    }

    private string RedoLookup(uint questId)
    {
        if (questId >= 131072)
        {
            return "";
        }
        if (questId >= 65536)
        {
            questId -= 65536;
        }
        return _redoUtil.GetChapter(questId).Item1.ToString();
    }

    private Tuple<string, int> RedoLookupIndex(uint questId)
    {
        if (questId >= 131072)
        {
            return new("", -1);
        }
        if (questId >= 65536)
        {
            questId -= 65536;
        }
        Tuple<ReadOnlySeString, int> outp = _redoUtil.GetChapter(questId);
        return new(outp.Item1.ToString(), outp.Item2);
    }

    [UsedImplicitly(ImplicitUseKindFlags.Access, ImplicitUseTargetFlags.WithMembers)]
    public sealed class StepData
    {
        public required string QuestId { get; init; }
        public required byte Sequence { get; init; }
        public required int Step { get; init; }
        public required string InteractionType { get; init; }
        public required Vector3? Position { get; init; }
        public required uint TerritoryId { get; init; }
    }
}

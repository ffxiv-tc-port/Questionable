using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using ECommons.ExcelServices;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using Microsoft.Extensions.Logging;
using Questionable.Data;
using Questionable.External;
using Questionable.Functions;
using Questionable.Model;
using Questionable.Model.Common;
using Questionable.Model.Gathering;
using Questionable.Model.Questing;
using Questionable.Utils;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.RegularExpressions;
using Quest = Questionable.Model.Quest;

namespace Questionable.Controller.GameUi;

internal sealed class InteractionUiController : IDisposable
{
    private readonly IAddonLifecycle _addonLifecycle;
    private readonly AetheryteFunctions _aetheryteFunctions;
    private readonly BossModIpc _bossModIpc;
    private readonly IChatGui _chatGui;
    private readonly IClientState _clientState;
    private readonly Configuration _configuration;
    private readonly IDataManager _dataManager;
    private readonly ExcelFunctions _excelFunctions;
    private readonly IGameGuiAdapter _gameGui;
    private readonly GatheringPointRegistry _gatheringPointRegistry;
    private readonly Regex _hqTradeRegex;
    private readonly LifestreamIpc _lifestreamIpc;
    private readonly ILogger<InteractionUiController> _logger;
    private readonly Regex _purchaseItemRegex;
    private readonly QuestController _questController;
    private readonly QuestData _questData;
    private readonly QuestFunctions _questFunctions;
    private readonly QuestRegistry _questRegistry;
    private readonly Regex _returnRegex;
    //private readonly IPlayerState _playerState;
    private readonly ShopController _shopController;
    private readonly ITargetManager _targetManager;
    private readonly TerritoryData _territoryData;
    private readonly Regex _ticketRegex;

    private bool _isInitialCheck;
    private List<WarpQuestion>? _warpQuestions;
    private string? _lastWarpNotUsedPrompt;
    private long _lastWarpNotUsedAt;
    private readonly HashSet<string> _notifiedQuestBattlePrompts = [];

    public InteractionUiController(
        IAddonLifecycle addonLifecycle,
        IDataManager dataManager,
        QuestFunctions questFunctions,
        AetheryteFunctions aetheryteFunctions,
        ExcelFunctions excelFunctions,
        QuestController questController,
        GatheringPointRegistry gatheringPointRegistry,
        QuestRegistry questRegistry,
        QuestData questData,
        TerritoryData territoryData,
        IGameGuiAdapter gameGui,
        ITargetManager targetManager,
        IPluginLog pluginLog,
        IClientState clientState,
        IObjectTable objectTable,
        ShopController shopController,
        BossModIpc bossModIpc,
        LifestreamIpc lifestreamIpc,
        Configuration configuration,
        IChatGui chatGui,
        ILogger<InteractionUiController> logger)
    {
        _chatGui = chatGui;
        _addonLifecycle = addonLifecycle;
        _dataManager = dataManager;
        _questFunctions = questFunctions;
        _aetheryteFunctions = aetheryteFunctions;
        _excelFunctions = excelFunctions;
        _questController = questController;
        _gatheringPointRegistry = gatheringPointRegistry;
        _questRegistry = questRegistry;
        _questData = questData;
        _territoryData = territoryData;
        _gameGui = gameGui;
        _targetManager = targetManager;
        _clientState = clientState;
        //_playerState = playerState;
        _shopController = shopController;
        _bossModIpc = bossModIpc;
        _lifestreamIpc = lifestreamIpc;
        _configuration = configuration;
        _logger = logger;

        _returnRegex = DataManagerAdapter.GetRegex<Addon>(_dataManager, 196, addon => addon.Text, pluginLog)!;
        _purchaseItemRegex = DataManagerAdapter.GetRegex<Addon>(_dataManager, 3406, addon => addon.Text, pluginLog)!;
        _ticketRegex = DataManagerAdapter.GetRegex<Addon>(_dataManager, 102686, addon => addon.Text, pluginLog)!;
        _hqTradeRegex = DataManagerAdapter.GetRegex<Addon>(_dataManager, 102434, addon => addon.Text, pluginLog)!;

        _questController.AutomationTypeChanged += HandleCurrentDialogueChoices;
        _addonLifecycle.RegisterListener(AddonEvent.PostSetup, "SelectString", SelectStringPostSetup);
        _addonLifecycle.RegisterListener(AddonEvent.PostSetup, "CutSceneSelectString", CutsceneSelectStringPostSetup);
        _addonLifecycle.RegisterListener(AddonEvent.PostSetup, "SelectIconString", SelectIconStringPostSetup);
        _addonLifecycle.RegisterListener(AddonEvent.PostSetup, "SelectYesno", SelectYesnoPostSetup);
        _addonLifecycle.RegisterListener(AddonEvent.PostSetup, "DifficultySelectYesNo", DifficultySelectYesNoPostSetup);
        _addonLifecycle.RegisterListener(AddonEvent.PostSetup, "PointMenu", PointMenuPostSetup);
        _addonLifecycle.RegisterListener(AddonEvent.PostSetup, "HousingSelectBlock", HousingSelectBlockPostSetup);

        unsafe
        {
            if (_gameGui.TryGetAddonByName("RhythmAction", out AtkUnitBase* addon))
            {
                addon->Close(true);
            }
        }
    }

    private bool ShouldHandleUiInteractions => _isInitialCheck ||
                                               _questController.IsRunning ||
                                               _territoryData.IsQuestBattleInstance(_clientState.TerritoryType);

    public void Dispose()
    {
        _addonLifecycle.UnregisterListener(AddonEvent.PostSetup, "HousingSelectBlock", HousingSelectBlockPostSetup);
        _addonLifecycle.UnregisterListener(AddonEvent.PostSetup, "PointMenu", PointMenuPostSetup);
        _addonLifecycle.UnregisterListener(AddonEvent.PostSetup, "DifficultySelectYesNo", DifficultySelectYesNoPostSetup);
        _addonLifecycle.UnregisterListener(AddonEvent.PostSetup, "SelectYesno", SelectYesnoPostSetup);
        _addonLifecycle.UnregisterListener(AddonEvent.PostSetup, "SelectIconString", SelectIconStringPostSetup);
        _addonLifecycle.UnregisterListener(AddonEvent.PostSetup, "CutSceneSelectString", CutsceneSelectStringPostSetup);
        _addonLifecycle.UnregisterListener(AddonEvent.PostSetup, "SelectString", SelectStringPostSetup);
        _questController.AutomationTypeChanged -= HandleCurrentDialogueChoices;
    }

    private void HandleCurrentDialogueChoices(object sender, QuestController.EAutomationType automationType)
    {
        if (automationType != QuestController.EAutomationType.Manual)
        {
            HandleCurrentDialogueChoices();
        }
    }

    internal unsafe void HandleCurrentDialogueChoices()
    {
        try
        {
            _isInitialCheck = true;
            if (_gameGui.TryGetAddonByName("SelectString", out AddonSelectString* addonSelectString))
            {
                _logger.LogInformation("SelectString window is open");
                SelectStringPostSetup(addonSelectString, true);
            }

            if (_gameGui.TryGetAddonByName("CutSceneSelectString",
                out AddonCutSceneSelectString* addonCutSceneSelectString))
            {
                _logger.LogInformation("CutSceneSelectString window is open");
                CutsceneSelectStringPostSetup(addonCutSceneSelectString, true);
            }

            if (_gameGui.TryGetAddonByName("SelectIconString", out AddonSelectIconString* addonSelectIconString))
            {
                _logger.LogInformation("SelectIconString window is open");
                SelectIconStringPostSetup(addonSelectIconString, true);
            }

            if (_gameGui.TryGetAddonByName("SelectYesno", out AddonSelectYesno* addonSelectYesno))
            {
                _logger.LogInformation("SelectYesno window is open");
                SelectYesnoPostSetup(addonSelectYesno, true);
            }

            if (_gameGui.TryGetAddonByName("DifficultySelectYesNo", out AtkUnitBase* addonDifficultySelectYesNo))
            {
                _logger.LogInformation("DifficultySelectYesNo window is open");
                DifficultySelectYesNoPostSetup(addonDifficultySelectYesNo, true);
            }

            if (_gameGui.TryGetAddonByName("PointMenu", out AtkUnitBase* addonPointMenu))
            {
                _logger.LogInformation("PointMenu is open");
                PointMenuPostSetup(addonPointMenu);
            }
        }
        finally
        {
            _isInitialCheck = false;
        }
    }

    private unsafe void SelectStringPostSetup(AddonEvent type, AddonArgs args)
    {
        AddonSelectString* addonSelectString = (AddonSelectString*)args.Addon.Address;
        SelectStringPostSetup(addonSelectString, false);
    }

    private unsafe void SelectStringPostSetup(AddonSelectString* addonSelectString, bool checkAllSteps)
    {
        if (!ShouldHandleUiInteractions)
        {
            return;
        }

        // 🔴 AtkValues 是指標欄位,addon 剛 setup／正在拆解時為 null(長度另存 AtkValuesCount)。
        // 走 AtkValueAdapter 的邊界安全多載＝addon 判空＋陣列判空＋索引在界內三道一起。
        string? actualPrompt = AtkValueAdapter.ReadString(&addonSelectString->AtkUnitBase, 2);
        if (actualPrompt == null)
        {
            return;
        }

        List<string?> answers = [];
        for(ushort i = 7; i < addonSelectString->AtkUnitBase.AtkValuesCount; ++i)
        {
            if (AtkValueAdapter.ReadType(&addonSelectString->AtkUnitBase, i) == FFXIVClientStructs.FFXIV.Component.GUI.ValueType.String)
            {
                answers.Add(AtkValueAdapter.ReadString(&addonSelectString->AtkUnitBase, i));
            }
        }

        int? answer = HandleListChoice(actualPrompt, answers, checkAllSteps) ?? HandleInstanceListChoice(actualPrompt, answers);
        if (answer != null)
        {
            _logger.LogInformation("Using choice {Choice} for list prompt '{Prompt}'", answer, actualPrompt);
            AddonPressGuard.PressCallbackInt("SelectString", &addonSelectString->AtkUnitBase, answer.Value);
        }
    }

    private unsafe void CutsceneSelectStringPostSetup(AddonEvent type, AddonArgs args)
    {
        AddonCutSceneSelectString* addonCutSceneSelectString = (AddonCutSceneSelectString*)args.Addon.Address;
        CutsceneSelectStringPostSetup(addonCutSceneSelectString, false);
    }

    private unsafe void CutsceneSelectStringPostSetup(AddonCutSceneSelectString* addonCutSceneSelectString,
        bool checkAllSteps)
    {
        if (!ShouldHandleUiInteractions)
        {
            return;
        }

        string? actualPrompt = AtkValueAdapter.ReadString(&addonCutSceneSelectString->AtkUnitBase, 2);
        if (actualPrompt == null)
        {
            return;
        }

        List<string?> answers = [];
        for(int i = 5; i < addonCutSceneSelectString->AtkUnitBase.AtkValuesCount; ++i)
        {
            answers.Add(AtkValueAdapter.ReadString(&addonCutSceneSelectString->AtkUnitBase, i));
        }

        int? answer = HandleListChoice(actualPrompt, answers, checkAllSteps);
        if (answer != null)
        {
            AddonPressGuard.PressCallbackInt("CutSceneSelectString", &addonCutSceneSelectString->AtkUnitBase, answer.Value);
        }
    }

    private unsafe void SelectIconStringPostSetup(AddonEvent type, AddonArgs args)
    {
        AddonSelectIconString* addonSelectIconString = (AddonSelectIconString*)args.Addon.Address;
        SelectIconStringPostSetup(addonSelectIconString, false);
    }

    private unsafe void SelectIconStringPostSetup(AddonSelectIconString* addonSelectIconString, bool checkAllSteps)
    {
        if (!ShouldHandleUiInteractions)
        {
            return;
        }

        string? actualPrompt = AtkValueAdapter.ReadString(&addonSelectIconString->AtkUnitBase, 3);
        if (string.IsNullOrEmpty(actualPrompt))
        {
            actualPrompt = null;
        }

        List<string?> answers = GetChoices(addonSelectIconString);
        int? answer = HandleListChoice(actualPrompt, answers, checkAllSteps);
        if (answer != null)
        {
            _logger.LogInformation("Using choice {Choice} for list prompt '{Prompt}'", answer, actualPrompt);
            AddonPressGuard.PressCallbackInt("SelectIconString", &addonSelectIconString->AtkUnitBase, answer.Value);
            return;
        }

        // this is 'Daily Quests' for tribal quests, but not set for normal selections
        string? title = AtkValueAdapter.ReadString(&addonSelectIconString->AtkUnitBase, 0);

        QuestController.QuestProgress? currentQuest = _questController.StartedQuest;
        if (currentQuest != null && (actualPrompt == null || title != null))
        {
            _logger.LogInformation("Checking if current quest {Name} is on the list", currentQuest.Quest.Info.Name);
            if (CheckQuestSelection(addonSelectIconString, currentQuest.Quest, answers))
            {
                return;
            }

            QuestSequence? sequence = currentQuest.Quest.FindSequence(currentQuest.Sequence);
            QuestStep? step = sequence?.FindStep(currentQuest.Step);
            if (step is { InteractionType: EInteractionType.AcceptQuest, PickUpQuestId: not null } &&
                _questRegistry.TryGetQuest(step.PickUpQuestId, out Quest? pickupQuest))
            {
                _logger.LogInformation("Checking if current picked-up {Name} is on the list", pickupQuest.Info.Name);
                if (CheckQuestSelection(addonSelectIconString, pickupQuest, answers))
                {
                    return;
                }
            }
        }

        QuestController.QuestProgress? nextQuest = _questController.NextQuest;
        if (nextQuest != null && (actualPrompt == null || title != null))
        {
            _logger.LogInformation("Checking if next quest {Name} is on the list", nextQuest.Quest.Info.Name);
            if (CheckQuestSelection(addonSelectIconString, nextQuest.Quest, answers))
            {
            }
        }
    }

    private unsafe bool CheckQuestSelection(AddonSelectIconString* addonSelectIconString, Quest quest,
        List<string?> answers)
    {
        // it is possible for this to be a quest selection
        string questName = quest.Info.GameName;
        int questSelection = answers.FindIndex(x => GameFunctions.GameStringEquals(questName, x));
        if (questSelection >= 0)
        {
            _logger.LogInformation("Selecting quest {QuestName}", quest.Info.Name);
            AddonPressGuard.PressCallbackInt("SelectIconString", &addonSelectIconString->AtkUnitBase, questSelection);
            return true;
        }

        return false;
    }

    /// <remarks>
    /// 🔴 兩層都要驗:①迴圈次數是從 <c>AtkValues[5]</c> 讀出來的 ②元素索引 <c>i * 3 + 7</c> 要和
    /// <c>AtkValuesCount</c> 比。<c>AtkValues</c> 為 null 時解參考是攔不到的 AccessViolation;
    /// 長度不足時讀的是陣列後方的堆積垃圾,可能通過 <c>String.HasValue</c> 再拿垃圾當字串指標去讀。
    /// <para>失敗語意:界外就停止收集(回目前為止的清單)。呼叫端
    /// <c>HandleListChoice</c> 本來就用「答案文字比對」在挑,少收到幾筆等於這一幀沒配對到,
    /// 下一次 PostSetup／PostRefresh 還會再進來。</para>
    /// </remarks>
    public static unsafe List<string?> GetChoices(AddonSelectIconString* addonSelectIconString)
    {
        List<string?> answers = [];
        int valueCount = addonSelectIconString == null ? 0 : addonSelectIconString->AtkUnitBase.AtkValuesCount;
        int count = AtkValueAdapter.ReadInt(&addonSelectIconString->AtkUnitBase, 5);
        for(ushort i = 0; i < count; i++)
        {
            if (i * 3 + 7 >= valueCount)
            {
                break;
            }

            answers.Add(AtkValueAdapter.ReadString(&addonSelectIconString->AtkUnitBase, i * 3 + 7));
        }

        return answers;
    }

    private int? HandleListChoice(string? actualPrompt, List<string?> answers, bool checkAllSteps)
    {
        List<DialogueChoiceInfo> dialogueChoices = [];

        QuestController.QuestProgress? currentQuest = _questController.SimulatedQuest ??
                                                      _questController.GatheringQuest ??
                                                      _questController.StartedQuest;
        if (currentQuest != null)
        {
            Quest quest = currentQuest.Quest;
            bool isTaxiStandUnlock = false;
            List<EAetheryteLocation> freeOrFavoredAetheryteRegistrations = [];
            if (checkAllSteps)
            {
                QuestSequence? sequence = quest.FindSequence(currentQuest.Sequence);
                IEnumerable<DialogueChoice>? choices = sequence?.Steps.SelectMany(x => x.DialogueChoices);
                if (choices != null)
                {
                    dialogueChoices.AddRange(choices.Select(x => new DialogueChoiceInfo(quest, x)));
                }

                isTaxiStandUnlock = sequence?.Steps.Any(x => x.InteractionType == EInteractionType.UnlockTaxiStand) ??
                                    false;
                freeOrFavoredAetheryteRegistrations = sequence?.Steps
                                                          .Where(x => x is
                                                          {
                                                              InteractionType: EInteractionType
                                                                  .RegisterFreeOrFavoredAetheryte,
                                                              Aetheryte: not null
                                                          })
                                                          .Select(x => x.Aetheryte!.Value).ToList()
                                                      ?? [];
            }
            else
            {
                QuestStep? step = null;
                if (_territoryData.IsQuestBattleInstance(_clientState.TerritoryType))
                {
                    step = quest.FindSequence(currentQuest.Sequence)?.Steps
                        .FirstOrDefault(x => x.InteractionType == EInteractionType.SinglePlayerDuty);
                }

                step ??= quest.FindSequence(currentQuest.Sequence)?.FindStep(currentQuest.Step);

                if (step == null)
                {
                    _logger.LogDebug("Ignoring current quest dialogue choices, no active step");
                }
                else
                {
                    dialogueChoices.AddRange(step.DialogueChoices.Select(x => new DialogueChoiceInfo(quest, x)));
                    if (step.PurchaseMenu != null)
                    {
                        dialogueChoices.Add(new(quest, new()
                        {
                            Type = EDialogChoiceType.List,
                            ExcelSheet = step.PurchaseMenu.ExcelSheet,
                            Prompt = null,
                            Answer = step.PurchaseMenu.Key
                        }));
                    }

                    if (step is { InteractionType: EInteractionType.RegisterFreeOrFavoredAetheryte, Aetheryte: { } aetheryte })
                    {
                        freeOrFavoredAetheryteRegistrations = [aetheryte];
                    }

                    isTaxiStandUnlock = step.InteractionType == EInteractionType.UnlockTaxiStand;
                }
            }

            if (isTaxiStandUnlock && _configuration.Advanced.UnlockChocoboTaxiStands)
            {
                _logger.LogInformation("Adding chocobo taxi stand unlock dialogue choices");
                dialogueChoices.Add(new(quest, new()
                {
                    Type = EDialogChoiceType.List,
                    ExcelSheet = "transport/ChocoboTaxiStand",
                    Prompt = ExcelRef.FromKey("TEXT_CHOCOBOTAXISTAND_00000_Q1_000_1"),
                    Answer = ExcelRef.FromKey("TEXT_CHOCOBOTAXISTAND_00000_A1_000_3")
                }));
            }

            if (freeOrFavoredAetheryteRegistrations.Any(x =>
                _aetheryteFunctions.CanRegisterFreeOrFavoriteAetheryte(x) ==
                AetheryteRegistrationResult.SecurityTokenFreeDestinationAvailable))
            {
                _logger.LogInformation("Adding security token aetheryte unlock dialogue choice");
                dialogueChoices.Add(new(quest, new()
                {
                    Type = EDialogChoiceType.List,
                    ExcelSheet = "transport/Aetheryte",
                    Prompt = ExcelRef.FromKey("TEXT_AETHERYTE_MAINMENU_TITLE"),
                    PromptIsRegularExpression = true,
                    Answer = ExcelRef.FromKey("TEXT_AETHERYTE_REGISTER_TOKEN_FAVORITE"),
                    AnswerIsRegularExpression = true
                }));
            }
            else if (freeOrFavoredAetheryteRegistrations.Any(x =>
                _aetheryteFunctions.CanRegisterFreeOrFavoriteAetheryte(x) ==
                AetheryteRegistrationResult.FavoredDestinationAvailable))
            {
                _logger.LogInformation("Adding favored aetheryte unlock dialogue choice");
                dialogueChoices.Add(new(quest, new()
                {
                    Type = EDialogChoiceType.List,
                    ExcelSheet = "transport/Aetheryte",
                    Prompt = ExcelRef.FromKey("TEXT_AETHERYTE_MAINMENU_TITLE"),
                    PromptIsRegularExpression = true,
                    Answer = ExcelRef.FromKey("TEXT_AETHERYTE_REGISTER_FAVORITE"),
                    AnswerIsRegularExpression = true
                }));
            }

            // add all travel dialogue choices
            ushort? targetTerritoryId = FindTargetTerritoryFromQuestStep(currentQuest);
            if (targetTerritoryId != null)
            {
                foreach(string? answer in answers)
                {
                    if (answer == null)
                    {
                        continue;
                    }

                    if (TryFindWarp(targetTerritoryId.Value, answer, out uint? warpId, out string? warpText))
                    {
                        _logger.LogInformation("Adding warp {Id}, {Prompt}", warpId, warpText);
                        dialogueChoices.Add(new(quest, new()
                        {
                            Type = EDialogChoiceType.List,
                            ExcelSheet = null,
                            Prompt = null,
                            Answer = ExcelRef.FromSheetValue(warpText)
                        }));
                    }
                }
            }
        }
        else
        {
            _logger.LogDebug("Ignoring current quest dialogue choices, no active quest");
        }

        // add all quests that start with the targeted npc
        IGameObject? target = _targetManager.Target;
        if (target != null)
        {
            foreach(IQuestInfo questInfo in _questData.GetAllByIssuerDataId(GameFunctions.GetBaseID(target)).Where(x => x.QuestId is QuestId))
            {
                if (_questFunctions.IsReadyToAcceptQuest(questInfo.QuestId) &&
                    _questRegistry.TryGetQuest(questInfo.QuestId, out Quest? knownQuest))
                {
                    List<DialogueChoice>? questChoices = knownQuest.FindSequence(0)?.Steps
                        .SelectMany(x => x.DialogueChoices)
                        .ToList();
                    if (questChoices != null && questChoices.Count > 0)
                    {
                        _logger.LogInformation("Adding {Count} dialogue choices from not accepted quest {QuestName}",
                            questChoices.Count, questInfo.Name);
                        dialogueChoices.AddRange(questChoices.Select(x => new DialogueChoiceInfo(knownQuest, x)));
                    }
                }
            }
        }

        if (dialogueChoices.Count == 0)
        {
            _logger.LogDebug("No dialogue choices to check");
            return null;
        }

        foreach((Quest? quest, DialogueChoice dialogueChoice) in dialogueChoices)
        {
            if (dialogueChoice.Type != EDialogChoiceType.List)
            {
                continue;
            }

            if (dialogueChoice.SpecialCondition == "NoDutyActions")
            {
                try
                {
                    unsafe
                    {
                        // 🔴 RaptureHotbarModule.Instance() 是手寫包裝，UIModule 為 null 時合法回 null；對 null 解參考是
                        //    下面那個 catch 攔不到的 AccessViolation，所以必須顯式判空。
                        // fail-closed：讀不到就跳過這個對話選項，與下面 catch 區塊「檢查失敗就 continue」的處置一致。
                        RaptureHotbarModule* hotbarModule = RaptureHotbarModule.Instance();
                        if (hotbarModule == null)
                        {
                            _logger.LogInformation(
                                "NoDutyActions: RaptureHotbarModule 尚未就緒，無法確認任務技能狀態，跳過此對話選項");
                            continue;
                        }

                        if (hotbarModule->DutyActionsPresent)
                        {
                            _logger.LogInformation("NoDutyActions: actions present, skipping dialogue choice");
                            continue;
                        }
                    }
                }
                catch(Exception ex)
                {
                    _logger.LogError(ex, "Failed to check for duty actions");
                    continue;
                }
            }

            if (dialogueChoice.Answer == null)
            {
                _logger.LogDebug("Ignoring entry in DialogueChoices, no answer");
                continue;
            }

            if (dialogueChoice.DataId != null && dialogueChoice.DataId != GameFunctions.GetBaseID(_targetManager.Target))
            {
                _logger.LogDebug(
                    "Skipping entry in DialogueChoice expecting target dataId {ExpectedDataId}, actual target is {ActualTargetId}",
                    dialogueChoice.DataId, GameFunctions.GetBaseID(_targetManager.Target));
                continue;
            }

            StringOrRegex? excelPrompt = ResolveReference(quest, dialogueChoice.ExcelSheet, dialogueChoice.Prompt,
                dialogueChoice.PromptIsRegularExpression, isPrompt: true);
            StringOrRegex? excelAnswer = ResolveReference(quest, dialogueChoice.ExcelSheet, dialogueChoice.Answer,
                dialogueChoice.AnswerIsRegularExpression);

            if (actualPrompt == null && excelPrompt != null)
            {
                _logger.LogInformation("Unexpected excelPrompt: {ExcelPrompt}", excelPrompt);
                continue;
            }

            if (actualPrompt != null &&
                (excelPrompt == null || !IsMatch(actualPrompt, excelPrompt)))
            {
                _logger.LogInformation("Unexpected excelPrompt: {ExcelPrompt}, actualPrompt: {ActualPrompt}",
                    excelPrompt, actualPrompt);
                continue;
            }

            for(int i = 0; i < answers.Count; ++i)
            {
                _logger.LogTrace("Checking if {ActualAnswer} == {ExpectedAnswer}",
                    answers[i], excelAnswer);
                if (IsMatch(answers[i], excelAnswer))
                {
                    _logger.LogInformation("Returning {Index}: '{Answer}' for '{Prompt}'",
                        i, answers[i], actualPrompt);

                    // ensure we only open the dialog once
                    if (quest?.Id is SatisfactionSupplyNpcId)
                    {
                        if (_questController.GatheringQuest == null ||
                            _questController.GatheringQuest.Sequence == 255)
                        {
                            return null;
                        }

                        _questController.GatheringQuest.SetSequence(1);
                        _questController.StartGatheringQuest("SatisfactionSupply turn in");
                    }

                    return i;
                }
            }
        }

        _logger.LogInformation("No matching answer found for {Prompt}.", actualPrompt);
        return null;
    }

    private static bool IsMatch(string? actualAnswer, StringOrRegex? expectedAnswer)
    {
        if (actualAnswer == null && expectedAnswer == null)
        {
            return true;
        }

        if (actualAnswer == null || expectedAnswer == null)
        {
            return false;
        }

        return expectedAnswer.IsMatch(actualAnswer) ||
               expectedAnswer.IsMatch(actualAnswer.Replace("\n", string.Empty)) ||
               expectedAnswer.IsMatch(actualAnswer.Split('\n')[0]);
    }

    /// <summary>
    /// 副本區選單（Addon#2090「為了緩解伺服器壓力，「‹地名›」被分成了多個副本區…」）選「隨意（自動選擇）」
    /// （Addon#2091），由遊戲分配副本區。
    /// </summary>
    /// <remarks>
    /// 地名是巨集代入的，哪個地名都算；選項按文字找，不假設它在第一格，找不到就不按。
    /// Lifestream 的渴望灣傳送會自己按同一項、切換副本區則是使用者指定的區，它忙碌時一律讓給它。
    /// </remarks>
    private int? HandleInstanceListChoice(string actualPrompt, List<string?> answers)
    {
        if (!IsMatch(actualPrompt, _excelFunctions.GetDialogueTextByRowId("Addon", 2090, false, macroTemplate: true)))
        {
            return null;
        }

        StringOrRegex anyInstance = _excelFunctions.GetDialogueTextByRowId("Addon", 2091, false);
        int index = answers.FindIndex(x => IsMatch(x?.Trim(), anyInstance));
        if (index < 0)
        {
            _logger.LogInformation("Instance list without a '{AnyInstance}' entry, not choosing: {Answers}",
                anyInstance, string.Join(" / ", answers));
            return null;
        }

        if (_lifestreamIpc.IsBusy)
        {
            _logger.LogInformation("Instance list while Lifestream is busy, leaving '{Answer}' to Lifestream", answers[index]);
            return null;
        }

        _logger.LogInformation("Selecting no prefered instance, {Index}: '{Answer}' for '{Prompt}'", index, answers[index],
            ShowControlChars(actualPrompt));
        return index;
    }

    private unsafe void SelectYesnoPostSetup(AddonEvent type, AddonArgs args)
    {
        AddonSelectYesno* addonSelectYesno = (AddonSelectYesno*)args.Addon.Address;
        SelectYesnoPostSetup(addonSelectYesno, false);
    }

    private unsafe void SelectYesnoPostSetup(AddonSelectYesno* addonSelectYesno, bool checkAllSteps)
    {
        if (!ShouldHandleUiInteractions)
        {
            return;
        }

        string? actualPrompt = AtkValueAdapter.ReadString(&addonSelectYesno->AtkUnitBase, 0);
        if (actualPrompt == null)
        {
            return;
        }

        _logger.LogTrace("Prompt: '{Prompt}'", actualPrompt);
        if (_shopController.IsAwaitingYesNo && _purchaseItemRegex.IsMatch(actualPrompt))
        {
            AddonPressGuard.PressCallbackInt("SelectYesno", &addonSelectYesno->AtkUnitBase, 0);
            _shopController.IsAwaitingYesNo = false;
            return;
        }

        QuestController.QuestProgress? currentQuest = _questController.StartedQuest;
        if (currentQuest != null && CheckQuestYesNo(addonSelectYesno, currentQuest, actualPrompt, checkAllSteps))
        {
            return;
        }

        QuestController.QuestProgress? simulatedQuest = _questController.SimulatedQuest;
        if (simulatedQuest != null && HandleTravelYesNo(addonSelectYesno, simulatedQuest, actualPrompt))
        {
            return;
        }

        QuestController.QuestProgress? nextQuest = _questController.NextQuest;
        if (nextQuest != null && CheckQuestYesNo(addonSelectYesno, nextQuest, actualPrompt, checkAllSteps))
        {
            return;
        }

        NotifyUnansweredQuestBattlePrompt(actualPrompt);
    }

    /// <summary>
    /// 任務戰鬥裡 YesAlready 被我們壓著，沒登錄的確認框只能等人按；自動化在跑時（可能沒人看著）在聊天視窗提示，同一則文字只提示一次。
    /// </summary>
    private void NotifyUnansweredQuestBattlePrompt(string actualPrompt)
    {
        if (!_territoryData.IsQuestBattleInstance(_clientState.TerritoryType) ||
            (!_questController.IsRunning && _questController.AutomationType == QuestController.EAutomationType.Manual))
        {
            return;
        }

        string prompt = actualPrompt.ReplaceLineEndings(" ").Trim();
        if (!_notifiedQuestBattlePrompts.Add(prompt))
        {
            return;
        }

        _logger.LogInformation("Leaving unregistered quest battle prompt to the player: '{Prompt}'", ShowControlChars(actualPrompt));
        _chatGui.Print($"任務戰鬥中出現 Questionable 沒有登錄的確認框，請手動選擇：「{prompt}」",
            CommandHandler.MessageTag, CommandHandler.TagColor);
    }

    private unsafe bool CheckQuestYesNo(AddonSelectYesno* addonSelectYesno, QuestController.QuestProgress currentQuest,
        string actualPrompt, bool checkAllSteps)
    {
        Quest quest = currentQuest.Quest;
        if (checkAllSteps)
        {
            QuestSequence? sequence = quest.FindSequence(currentQuest.Sequence);
            if (sequence != null &&
                sequence.Steps.Any(step => HandleDefaultYesNo(addonSelectYesno, quest, step, step.DialogueChoices, actualPrompt)))
            {
                return true;
            }
        }
        else
        {
            QuestStep? step = quest.FindSequence(currentQuest.Sequence)?.FindStep(currentQuest.Step);
            if (step != null && HandleDefaultYesNo(addonSelectYesno, quest, step, step.DialogueChoices, actualPrompt))
            {
                return true;
            }
        }

        if (HandleTravelYesNo(addonSelectYesno, currentQuest, actualPrompt))
        {
            return true;
        }

        return false;
    }

    private unsafe bool HandleDefaultYesNo(AddonSelectYesno* addonSelectYesno, Quest quest,
        QuestStep? step, List<DialogueChoice> dialogueChoices, string actualPrompt)
    {
        if (step is { InteractionType: EInteractionType.RegisterFreeOrFavoredAetheryte, Aetheryte: { } aetheryteLocation })
        {
            AetheryteRegistrationResult registrationResult = _aetheryteFunctions.CanRegisterFreeOrFavoriteAetheryte(aetheryteLocation);
            if (registrationResult == AetheryteRegistrationResult.SecurityTokenFreeDestinationAvailable)
            {
                dialogueChoices =
                [
                    ..dialogueChoices,
                    new()
                    {
                        Type = EDialogChoiceType.YesNo,
                        ExcelSheet = "Addon",
                        Prompt = ExcelRef.FromRowId(102334),
                        Yes = true
                    }
                ];
            }
            else if (registrationResult == AetheryteRegistrationResult.FavoredDestinationAvailable)
            {
                dialogueChoices =
                [
                    ..dialogueChoices,
                    new()
                    {
                        Type = EDialogChoiceType.YesNo,
                        ExcelSheet = "Addon",
                        Prompt = ExcelRef.FromRowId(102306),
                        Yes = true
                    }
                ];
            }
        }

        _logger.LogTrace("DefaultYesNo: Choice count: {Count}", dialogueChoices.Count);
        foreach(DialogueChoice dialogueChoice in dialogueChoices)
        {
            if (dialogueChoice.Type != EDialogChoiceType.YesNo)
            {
                continue;
            }

            //force Yes if prompt is set to `null` while an answer is still given - basically means no key is available on sheets yet
            if (dialogueChoice.Prompt == null && dialogueChoice.Yes)
            {
                _logger.LogInformation("Forcing Yes because no key for this prompt is currently available.");
                AddonPressGuard.PressCallbackInt("SelectYesno", &addonSelectYesno->AtkUnitBase, 0);
                return true;
            }

            if (dialogueChoice.DataId != null && dialogueChoice.DataId != GameFunctions.GetBaseID(_targetManager.Target))
            {
                _logger.LogDebug(
                    "Skipping entry in DialogueChoice expecting target dataId {ExpectedDataId}, actual target is {ActualTargetId}",
                    dialogueChoice.DataId, GameFunctions.GetBaseID(_targetManager.Target));
                continue;
            }

            StringOrRegex? excelPrompt = ResolveReference(quest, dialogueChoice.ExcelSheet, dialogueChoice.Prompt,
                dialogueChoice.PromptIsRegularExpression, isPrompt: true);
            if (excelPrompt == null || !IsMatch(actualPrompt, excelPrompt))
            {
                _logger.LogInformation("Unexpected excelPrompt: {ExcelPrompt}, actualPrompt: {ActualPrompt}",
                    excelPrompt, actualPrompt);
                continue;
            }

            _logger.LogInformation("Returning {YesNo} for '{Prompt}'", dialogueChoice.Yes ? "Yes" : "No", actualPrompt);
            AddonPressGuard.PressCallbackInt("SelectYesno", &addonSelectYesno->AtkUnitBase, dialogueChoice.Yes ? 0 : 1);
            return true;
        }

        if (CheckSinglePlayerDutyYesNo(quest.Id, step) && IsSinglePlayerDutyEntryPrompt(actualPrompt))
        {
            AddonPressGuard.PressCallbackInt("SelectYesno", &addonSelectYesno->AtkUnitBase, 0);
            return true;
        }

        return false;
    }

    /// <summary>
    /// 任務戰鬥的進場確認框沒有表文字可比（各任務自己的對話），只在「為這一步跟 NPC 互動之後、還沒進副本」時當成自己的框。
    /// 副本裡只按 <see cref="QuestStep.DialogueChoices"/> 比對過文字的框（例如「要退出任務嗎？」不能盲按）。
    /// </summary>
    private bool IsSinglePlayerDutyEntryPrompt(string actualPrompt)
    {
        string? refusal = null;
        if (_territoryData.IsQuestBattleInstance(_clientState.TerritoryType))
        {
            refusal = "inside the quest battle";
        }
        else if (!_isInitialCheck &&
                 !(_questController.HasCurrentTaskMatching(out Steps.Interactions.Interact.Task? interact) &&
                   interact.EntersSoloDutyWithBossMod) &&
                 !_questController.HasCurrentTaskMatching(out Steps.Interactions.UseItem.IUseItemBase? _) &&
                 !_questController.HasCurrentTaskMatching(out Steps.Interactions.Emote.UseOnObject? _) &&
                 !_questController.HasCurrentTaskMatching(out Steps.Interactions.Emote.UseOnSelf? _) &&
                 !_questController.HasCurrentTaskMatching(out Steps.Interactions.SinglePlayerDuty.StartSinglePlayerDuty? _) &&
                 !_questController.HasCurrentTaskMatching(out Steps.Interactions.SinglePlayerDuty.Commence? _) &&
                 !_questController.HasCurrentTaskMatching(out Steps.Common.Mount.UnmountTask? _))
        {
            refusal = "not right after talking to the quest battle NPC";
        }

        if (refusal != null)
        {
            _logger.LogInformation("Not answering '{Prompt}' for the quest battle: {Reason}", actualPrompt, refusal);
            return false;
        }

        return true;
    }

    private bool CheckSinglePlayerDutyYesNo(ElementId questId, QuestStep? step)
    {
        if (step is { InteractionType: EInteractionType.SinglePlayerDuty } &&
            _bossModIpc.IsConfiguredToRunSoloInstance(questId, step.SinglePlayerDutyOptions))
        {
            // Most of these are yes/no dialogs "Duty calls, ...".
            //
            // For 'Vows of Virtue, Deeds of Cruelty', there's no such dialog, and it just puts you into the instance
            // after you confirm 'Wait for Krile?'. However, if you fail that duty, you'll get a DifficultySelectYesNo.

            // DifficultySelectYesNo → [0, 2] for very easy
            _logger.LogInformation("SinglePlayerDutyYesNo: probably Single Player Duty");
            return true;
        }

        return false;
    }

    private unsafe bool HandleTravelYesNo(AddonSelectYesno* addonSelectYesno,
        QuestController.QuestProgress currentQuest, string actualPrompt)
    {
        // 進來不代表會按，只記 Verbose；真的按下去的分支各自印 Information（新增分支照做）。
        _logger.LogTrace("TravelYesNo");
        if (_aetheryteFunctions.ReturnRequestedAt >= DateTime.Now.AddSeconds(-2) && _returnRegex.IsMatch(actualPrompt))
        {
            _logger.LogInformation("Automatically confirming return...");
            AddonPressGuard.PressCallbackInt("SelectYesno", &addonSelectYesno->AtkUnitBase, 0);
            return true;
        }
        if (_ticketRegex.IsMatch(actualPrompt))
        {
            _logger.LogInformation("Check UseTickets: {UseTickets}, answering ticket prompt with {YesNo}",
                _configuration.General.UseTickets, _configuration.General.UseTickets ? "Yes" : "No");
            AddonPressGuard.PressCallbackInt("SelectYesno", &addonSelectYesno->AtkUnitBase, _configuration.General.UseTickets ? 0 : 1);
            return true;
        }

        if (_questController.IsRunning && _hqTradeRegex != null && _hqTradeRegex.IsMatch(actualPrompt))
        {
//            if (IsHqTradeAllowedInSequence(currentQuest))
//            {
            _logger.LogInformation("Automatically confirming HQ item trade");
            AddonPressGuard.PressCallbackInt("SelectYesno", &addonSelectYesno->AtkUnitBase, 0);
            return true;
//            }
//            else
//            {
//                _logger.LogInformation("HQ item trade not allowed for current step, clicking No");
//                addonSelectYesno->AtkUnitBase.FireCallbackInt(1);
//                return true;
//            }
        }

        if (_questController.IsRunning && _gameGui.TryGetAddonByName("HousingSelectBlock", out AtkUnitBase* _))
        {
            _logger.LogInformation("Automatically confirming ward selection");
            AddonPressGuard.PressCallbackInt("SelectYesno", &addonSelectYesno->AtkUnitBase, 0);
            return true;
        }

        ushort? targetTerritoryId = FindTargetTerritoryFromQuestStep(currentQuest);
        if (targetTerritoryId != null &&
            TryFindWarp(targetTerritoryId.Value, actualPrompt, out uint? warpId, out string? warpText))
        {
            _logger.LogInformation("Using warp {Id}, {Prompt}", warpId, warpText);
            AddonPressGuard.PressCallbackInt("SelectYesno", &addonSelectYesno->AtkUnitBase, 0);
            return true;
        }

        return HandleStepNpcWarpYesNo(addonSelectYesno, currentQuest, actualPrompt, targetTerritoryId);
    }


    private unsafe void DifficultySelectYesNoPostSetup(AddonEvent type, AddonArgs args)
    {
        AtkUnitBase* addonDifficultySelectYesNo = (AtkUnitBase*)args.Addon.Address;
        DifficultySelectYesNoPostSetup(addonDifficultySelectYesNo, false);
    }

    private unsafe void DifficultySelectYesNoPostSetup(AtkUnitBase* addonDifficultySelectYesNo, bool checkAllSteps)
    {
        if (!_questController.IsRunning)
        {
            return;
        }

        QuestController.QuestProgress? currentQuest = _questController.StartedQuest;
        if (currentQuest == null)
        {
            return;
        }

        Quest quest = currentQuest.Quest;
        bool autoConfirm;
        if (checkAllSteps)
        {
            QuestSequence? sequence = quest.FindSequence(currentQuest.Sequence);
            autoConfirm = sequence != null && sequence.Steps.Any(step => CheckSinglePlayerDutyYesNo(quest.Id, step));
        }
        else
        {
            QuestStep? step = quest.FindSequence(currentQuest.Sequence)?.FindStep(currentQuest.Step);
            autoConfirm = step != null && CheckSinglePlayerDutyYesNo(quest.Id, step);
        }

        // 這扇框是戰敗後的重試框（重新挑戰／從中途繼續／選難度）。這一步的副本已經打過一次時，
        // 出現它就代表剛才輸了：不按（留給使用者決定），停下來求助。
        if (autoConfirm &&
            _configuration.SinglePlayerDuties.StopOnFailure &&
            _questController.IsAfterSinglePlayerDutyAttempt(out uint failedCfcId, out bool disableAiPending))
        {
            if (disableAiPending)
            {
                _bossModIpc.DisableAi();
            }

            string dutyName = _territoryData.GetContentFinderCondition(failedCfcId)?.Name ?? "?";
            _questController.StopAfterSinglePlayerDutyFailure(failedCfcId, dutyName, "離開副本後跳出重試難度選擇框");
            return;
        }

        if (autoConfirm)
        {
            _logger.LogInformation("Confirming difficulty ({Difficulty}) for quest battle", _configuration.SinglePlayerDuties.RetryDifficulty);
            AtkValue* selectChoice = stackalloc AtkValue[]
            {
                new() { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int, Int = 0 },
                new() { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int, Int = _configuration.SinglePlayerDuties.RetryDifficulty }
            };
            if (AddonPressGuard.TryBeginPress("DifficultySelectYesNo", addonDifficultySelectYesNo, "confirm"))
            {
                addonDifficultySelectYesNo->FireCallback(2, selectChoice);
            }
        }
    }

    private unsafe ushort? FindTargetTerritoryFromQuestStep(QuestController.QuestProgress currentQuest)
    {
        // this can be triggered either manually (in which case we should increase the step counter), or automatically
        // (in which case it is ~1 frame later, and the step counter has already been increased)
        QuestSequence? sequence = currentQuest.Quest.FindSequence(currentQuest.Sequence);
        if (sequence == null)
        {
            return null;
        }

        QuestStep? step = sequence.FindStep(currentQuest.Step);
        if (step != null)
        {
            _logger.LogTrace("FindTargetTerritoryFromQuestStep (current): {CurrentTerritory}, {TargetTerritory}",
                step.TerritoryId,
                step.TargetTerritoryId);
        }

        if (step != null && (step.TerritoryId != _clientState.TerritoryType || step.TargetTerritoryId == null) &&
            step.InteractionType == EInteractionType.Gather)
        {
            if (_gatheringPointRegistry.TryGetGatheringPointId(step.ItemsToGather[0].ItemId,
                    (Job?)PlayerState.Instance()->CurrentClassJobId ?? Job.ADV,
                    out GatheringPointId? gatheringPointId) &&
                _gatheringPointRegistry.TryGetGatheringPoint(gatheringPointId, out GatheringRoot? root))
            {
                foreach(QuestStep gatheringStep in root.Steps)
                {
                    if (gatheringStep.TerritoryId == _clientState.TerritoryType &&
                        gatheringStep.TargetTerritoryId != null)
                    {
                        _logger.LogTrace(
                            "FindTargetTerritoryFromQuestStep (gathering): {CurrentTerritory}, {TargetTerritory}",
                            gatheringStep.TerritoryId,
                            gatheringStep.TargetTerritoryId);
                        return gatheringStep.TargetTerritoryId;
                    }
                }
            }
        }

        if (step == null || step.TargetTerritoryId == null)
        {
            _logger.LogTrace("FindTargetTerritoryFromQuestStep: Checking previous step...");
            step = sequence.FindStep(currentQuest.Step == 255 ? (sequence.Steps.Count - 1) : (currentQuest.Step - 1));

            if (step != null)
            {
                _logger.LogTrace("FindTargetTerritoryFromQuestStep (previous): {CurrentTerritory}, {TargetTerritory}",
                    step.TerritoryId,
                    step.TargetTerritoryId);
            }
        }

        if (step == null || step.TargetTerritoryId == null)
        {
            _logger.LogTrace("FindTargetTerritoryFromQuestStep: Not found");
            return null;
        }

        _logger.LogDebug("Target territory for quest step: {TargetTerritory}", step.TargetTerritoryId);
        return step.TargetTerritoryId;
    }

    private static bool IsHqTradeAllowed(QuestController.QuestProgress currentQuest)
    {
        if (currentQuest?.Quest == null)
        {
            return false;
        }

        QuestSequence? sequence = currentQuest.Quest.FindSequence(currentQuest.Sequence);
        if (sequence == null)
        {
            return false;
        }

        QuestStep? step = sequence.FindStep(currentQuest.Step);
        if (step == null)
        {
            return false;
        }

        return step.AllowHighQuality == true;
    }

    private static bool IsHqTradeAllowedInSequence(QuestController.QuestProgress currentQuest)
    {
        if (currentQuest?.Quest == null)
        {
            return false;
        }

        QuestSequence? sequence = currentQuest.Quest.FindSequence(currentQuest.Sequence);
        if (sequence == null)
        {
            return false;
        }

        // Check all steps in the sequence for AllowHighQuality = true
        return sequence.Steps.Any(step => step.AllowHighQuality == true);
    }

    private bool TryFindWarp(ushort targetTerritoryId, string actualPrompt, [NotNullWhen(true)] out uint? warpId,
        [NotNullWhen(true)] out string? warpText)
    {
        IEnumerable<Warp> warps = _dataManager.GetExcelSheet<Warp>()
            .Where(x => x.RowId > 0 && x.TerritoryType.RowId == targetTerritoryId);
        string prompt = WithoutLineBreaks(actualPrompt);
        foreach(Warp entry in warps)
        {
            string excelName = entry.Name.WithCertainMacroCodeReplacements();
            string excelQuestion = entry.Question.WithCertainMacroCodeReplacements();

            if (!string.IsNullOrEmpty(excelQuestion) &&
                GameFunctions.GameStringEquals(WithoutLineBreaks(excelQuestion), prompt))
            {
                warpId = entry.RowId;
                warpText = excelQuestion;
                return true;
            }
            else if (!string.IsNullOrEmpty(excelName) && GameFunctions.GameStringEquals(WithoutLineBreaks(excelName), prompt))
            {
                warpId = entry.RowId;
                warpText = excelName;
                return true;
            }
            else
            {
                _logger.LogDebug("Ignoring prompt '{Prompt}'", excelQuestion);
            }
        }

        warpId = null;
        warpText = null;
        return false;
    }

    /// <summary>
    /// 表裡的 NewLine 巨集在確認框的畫面文字裡是字面換行字元（表那側剝成空字串），
    /// 所以比對 Warp 文字時兩邊都去掉換行，其餘照 <see cref="GameFunctions.GameStringEquals"/> 逐字比。
    /// </summary>
    private static string WithoutLineBreaks(string text) => text.ReplaceLineEndings(string.Empty);

    /// <summary>
    /// 目標區域那條沒認出來的 Warp 確認框。這一步沒寫目標區域時，改認「正在互動的就是這一步的 NPC，
    /// 而那個 NPC 身上只掛這一個 Warp」才按；其餘情況不按，並寫一行 Information 說明原因。
    /// </summary>
    private unsafe bool HandleStepNpcWarpYesNo(AddonSelectYesno* addonSelectYesno,
        QuestController.QuestProgress currentQuest, string actualPrompt, ushort? targetTerritoryId)
    {
        List<WarpQuestion> promptWarps = FindWarpsByQuestion(actualPrompt);
        if (!_questController.IsRunning)
        {
            if (promptWarps.Count > 0)
            {
                LogWarpNotUsed(promptWarps, actualPrompt, "Questionable 沒有在自動執行");
            }

            return false;
        }

        uint targetNpcId = GameFunctions.GetBaseID(_targetManager.Target);
        List<uint> stepNpcIds = FindStepNpcDataIds(currentQuest);
        if (promptWarps.Count == 0)
        {
            // 不是任何 Warp 的問句：只有這一步的 NPC 身上掛著 Warp 時才記（比對方式又對不上時靠這行看出來），其他確認框不記。
            List<uint> stepWarpIds = stepNpcIds.SelectMany(GetNpcWarpIds).Distinct().ToList();
            if (stepWarpIds.Count > 0)
            {
                LogWarpNotUsed(promptWarps, actualPrompt,
                    $"問句對不上這一步的 NPC（{string.Join("/", stepNpcIds)}）身上任何一個 Warp（{string.Join("/", stepWarpIds)}）的問句，目前的目標是 {targetNpcId}");
            }

            return false;
        }

        bool talkingToStepNpc = targetNpcId != 0 && stepNpcIds.Contains(targetNpcId);
        List<uint> npcWarpIds = talkingToStepNpc ? GetNpcWarpIds(targetNpcId) : [];
        string? reason;
        if (targetTerritoryId != null)
        {
            reason = $"這一步的目標區域是 {targetTerritoryId}，這趟去的是 " +
                     string.Join("/", promptWarps.Select(x => x.TerritoryId).Distinct());
        }
        else if (!talkingToStepNpc)
        {
            reason = $"這一步沒有目標區域，目前的目標 {targetNpcId} 也不是這一步的 NPC（{string.Join("/", stepNpcIds)}）";
        }
        else if (!promptWarps.Any(x => npcWarpIds.Contains(x.RowId)))
        {
            reason = $"這一步沒有目標區域，NPC {targetNpcId} 身上沒有這個 Warp（它掛的是 {string.Join("/", npcWarpIds)}）";
        }
        else if (npcWarpIds.Count != 1)
        {
            reason = $"這一步沒有目標區域，NPC {targetNpcId} 有 {npcWarpIds.Count} 個目的地，無法確定要搭哪一趟";
        }
        else
        {
            reason = null;
        }

        if (reason != null)
        {
            LogWarpNotUsed(promptWarps, actualPrompt, reason);
            return false;
        }

        WarpQuestion warp = promptWarps.First(x => x.RowId == npcWarpIds[0]);
        _logger.LogInformation("Using warp {Id}, {Prompt} (step NPC {NpcId})", warp.RowId, warp.Question, targetNpcId);
        AddonPressGuard.PressCallbackInt("SelectYesno", &addonSelectYesno->AtkUnitBase, 0);
        return true;
    }

    /// <summary>問句（忽略換行）等於 <paramref name="actualPrompt"/> 的所有 Warp；表只在第一次用到時讀一次。</summary>
    private List<WarpQuestion> FindWarpsByQuestion(string actualPrompt)
    {
        _warpQuestions ??= _dataManager.GetExcelSheet<Warp>()
            .Where(x => x.RowId > 0)
            .Select(x => new WarpQuestion(x.RowId, x.TerritoryType.RowId,
                x.Question.WithCertainMacroCodeReplacements()))
            .Where(x => !string.IsNullOrEmpty(x.Question))
            .ToList();

        string prompt = WithoutLineBreaks(actualPrompt);
        return _warpQuestions
            .Where(x => GameFunctions.GameStringEquals(WithoutLineBreaks(x.Question), prompt))
            .ToList();
    }

    /// <summary>
    /// 這一步會去互動的 NPC：步驟自己的 DataId；採集步驟另加採集路線裡位於目前區域的步驟
    /// （與 <see cref="FindTargetTerritoryFromQuestStep"/> 的採集分支同一套查法）。
    /// </summary>
    private unsafe List<uint> FindStepNpcDataIds(QuestController.QuestProgress currentQuest)
    {
        List<uint> dataIds = [];
        QuestStep? step = currentQuest.Quest.FindSequence(currentQuest.Sequence)?.FindStep(currentQuest.Step);
        if (step == null)
        {
            return dataIds;
        }

        if (step.DataId != null)
        {
            dataIds.Add(step.DataId.Value);
        }

        if (step.InteractionType == EInteractionType.Gather && step.ItemsToGather.Count > 0 &&
            _gatheringPointRegistry.TryGetGatheringPointId(step.ItemsToGather[0].ItemId,
                (Job?)PlayerState.Instance()->CurrentClassJobId ?? Job.ADV,
                out GatheringPointId? gatheringPointId) &&
            _gatheringPointRegistry.TryGetGatheringPoint(gatheringPointId, out GatheringRoot? root))
        {
            dataIds.AddRange(root.Steps
                .Where(x => x.TerritoryId == _clientState.TerritoryType && x.DataId != null)
                .Select(x => x.DataId!.Value));
        }

        return dataIds;
    }

    /// <summary>NPC 的事件處理器裡屬於 Warp 表的那幾個（處理器 id 就是 Warp 的列號）；不是 ENpc 就回空。</summary>
    private List<uint> GetNpcWarpIds(uint npcDataId)
    {
        ENpcBase? npc = _dataManager.GetExcelSheet<ENpcBase>().GetRowOrDefault(npcDataId);
        if (npc == null)
        {
            return [];
        }

        ExcelSheet<Warp> warpSheet = _dataManager.GetExcelSheet<Warp>();
        return npc.Value.ENpcData
            .Select(x => x.RowId)
            .Where(x => x != 0 && warpSheet.HasRow(x))
            .Distinct()
            .ToList();
    }

    /// <remarks>同一扇確認框會對目前任務、模擬任務、下一個任務各問一次；兩秒內同一句問句只記第一次。</remarks>
    private void LogWarpNotUsed(List<WarpQuestion> promptWarps, string actualPrompt, string reason)
    {
        long now = Environment.TickCount64;
        if (actualPrompt == _lastWarpNotUsedPrompt && now - _lastWarpNotUsedAt < 2000)
        {
            return;
        }

        _lastWarpNotUsedPrompt = actualPrompt;
        _lastWarpNotUsedAt = now;
        string ids = promptWarps.Count == 0 ? "-" : string.Join("/", promptWarps.Select(x => x.RowId));
        _logger.LogInformation("Not using warp {Ids}, {Prompt}: {Reason}", ids, ShowControlChars(actualPrompt), reason);
    }

    /// <summary>換行等控制字元寫成 &lt;0A&gt;，多行的畫面文字在 log 裡留在同一行。</summary>
    private static string ShowControlChars(string text) =>
        string.Concat(text.Select(c => char.IsControl(c) ? $"<{(int)c:X2}>" : c.ToString()));

    private unsafe void PointMenuPostSetup(AddonEvent type, AddonArgs args)
    {
        AtkUnitBase* addonPointMenu = (AtkUnitBase*)args.Addon.Address;
        PointMenuPostSetup(addonPointMenu);
    }

    private unsafe void PointMenuPostSetup(AtkUnitBase* addonPointMenu)
    {
        if (!ShouldHandleUiInteractions)
        {
            return;
        }

        QuestController.QuestProgress? currentQuest = _questController.StartedQuest;
        if (currentQuest == null)
        {
            _logger.LogInformation("Ignoring point menu, no active quest");
            return;
        }

        QuestSequence? sequence = currentQuest.Quest.FindSequence(currentQuest.Sequence);
        if (sequence == null)
        {
            return;
        }

        QuestStep? step = sequence.FindStep(currentQuest.Step);
        if (step == null)
        {
            return;
        }

        if (step.PointMenuChoices.Count == 0)
        {
            _logger.LogWarning("No point menu choices");
            return;
        }

        int counter = currentQuest.StepProgress.PointMenuCounter;
        if (counter >= step.PointMenuChoices.Count)
        {
            _logger.LogWarning("No remaining point menu choices");
            return;
        }

        uint choice = step.PointMenuChoices[counter];

        // 🔴 被守衛擋下＝這扇 PointMenu 剛剛已經回答過(正在關閉中)，counter 不可以前進，
        // 否則下一扇窗會少按一個選項。
        if (!AddonPressGuard.TryBeginPress("PointMenu", addonPointMenu, "choice"))
        {
            return;
        }

        _logger.LogInformation("Handling point menu, picking choice {Choice} (index = {Index})", choice, counter);
        AtkValue* selectChoice = stackalloc AtkValue[]
        {
            new() { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int, Int = 13 },
            new() { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.UInt, UInt = choice }
        };
        addonPointMenu->FireCallback(2, selectChoice);

        currentQuest.IncreasePointMenuCounter();
    }

    private unsafe void HousingSelectBlockPostSetup(AddonEvent type, AddonArgs args)
    {
        if (!ShouldHandleUiInteractions)
        {
            return;
        }

        _logger.LogInformation("Confirming selected housing ward");
        AtkUnitBase* addon = (AtkUnitBase*)args.Addon.Address;
        AddonPressGuard.PressCallbackInt("HousingSelectBlock", addon, 0);
    }

    /// <param name="isPrompt">
    /// 問句才用巨集樣板（<see cref="DataManagerExtensions.ToScreenTemplate"/>）。答案要在並排的選項裡挑一個，
    /// 萬用位置可能讓只差代入名稱的另一項先比中，所以答案維持逐字比。
    /// </param>
    private StringOrRegex? ResolveReference(Quest? quest, string? excelSheet, ExcelRef? excelRef, bool isRegExp,
        bool isPrompt = false)
    {
        if (excelRef == null)
        {
            return null;
        }

        if (excelRef.Type == ExcelRef.EType.Key)
        {
            return _excelFunctions.GetDialogueText(quest, excelSheet, excelRef.AsKey(), isRegExp, isPrompt);
        }
        else if (excelRef.Type == ExcelRef.EType.RowId)
        {
            return _excelFunctions.GetDialogueTextByRowId(excelSheet, excelRef.AsRowId(), isRegExp, isPrompt);
        }
        else if (excelRef.Type == ExcelRef.EType.RawString)
        {
            return new(excelRef.AsRawString());
        }

        return null;
    }

    private sealed record DialogueChoiceInfo(Quest? Quest, DialogueChoice DialogueChoice);

    /// <param name="Question">已剝巨集的問句（NewLine 巨集剝成空字串）。</param>
    private sealed record WarpQuestion(uint RowId, uint TerritoryId, string Question);
}

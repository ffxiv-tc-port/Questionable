using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using ECommons.ExcelServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Microsoft.Extensions.Logging;
using Questionable.Controller.Steps.Shared;
using Questionable.Controller.Utils;
using Questionable.Data;
using Questionable.External;
using Questionable.Functions;
using Questionable.Model;
using Questionable.Model.Questing;
using Questionable.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using ObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;

namespace Questionable.Controller.Steps.Interactions;

internal static class Interact
{
    internal sealed class Factory(AutomatonIpc automatonIpc, BossModIpc bossModIpc, Configuration configuration) : ITaskFactory
    {
        public IEnumerable<ITask> CreateAllTasks(Quest quest, QuestSequence sequence, QuestStep step)
        {
            if (step.InteractionType is EInteractionType.AcceptQuest or EInteractionType.CompleteQuest
                or EInteractionType.SinglePlayerDuty)
            {
                // 'PreventQuestCompletion' config check
                if (step.InteractionType is EInteractionType.CompleteQuest && configuration.Advanced.PreventQuestCompletion)
                {
                    yield break;
                }

                if (step.Emote != null)
                {
                    yield break;
                }

                if (step.ChatMessage != null)
                {
                    yield break;
                }

                if (step.ItemId != null)
                {
                    yield break;
                }

                if (step.DataId == null)
                {
                    yield break;
                }
            }
            else if (step.InteractionType == EInteractionType.PurchaseItem)
            {
                if (step.DataId == null)
                {
                    yield break;
                }
            }
            else if (step.InteractionType == EInteractionType.Snipe)
            {
                if (!automatonIpc.IsAutoSnipeEnabled)
                {
                    yield break;
                }
            }
            else if (step.InteractionType == EInteractionType.UnlockTaxiStand)
            {
                if (!configuration.Advanced.UnlockChocoboTaxiStands)
                {
                    yield break;
                }

                if (step.TaxiStandId == null)
                {
                    yield break;
                }

                // 10 級以下解不開陸行鳥計程車站，整步跳過。
                bool tooLowLevel;
                unsafe
                {
                    tooLowLevel = PlayerState.Instance()->CurrentLevel < 10;
                }

                if (tooLowLevel)
                {
                    yield break;
                }
            }
            else if (step.InteractionType != EInteractionType.Interact)
            {
                yield break;
            }

            ArgumentNullException.ThrowIfNull(step.DataId);

            // if we're fast enough, it is possible to get the smalltalk prompt
            if (sequence.Sequence == 0 && sequence.Steps.IndexOf(step) == 0)
            {
                yield return new WaitAtEnd.WaitDelay();
            }

            yield return new Task(
                step.DataId.Value,
                quest,
                step.InteractionType,
                step.TargetTerritoryId != null || quest.Id is SatisfactionSupplyNpcId ||
                step.SkipConditions is { StepIf.Never: true } || step.InteractionType == EInteractionType.PurchaseItem || step.DataId == 1052475,
                step.PickUpItemId,
                step.TaxiStandId,
                step.SkipConditions?.StepIf,
                step.CompletionQuestVariablesFlags,
                step.InteractionType == EInteractionType.SinglePlayerDuty &&
                bossModIpc.IsConfiguredToRunSoloInstance(quest.Id, step.SinglePlayerDutyOptions));
        }
    }

    internal sealed record Task
    (
        uint DataId,
        Quest? Quest,
        EInteractionType InteractionType,
        bool SkipMarkerCheck = false,
        uint? PickUpItemId = null,
        uint? TaxiStandId = null,
        SkipStepConditions? SkipConditions = null,
        List<QuestWorkValue?>? CompletionQuestVariablesFlags = null,
        bool EntersSoloDutyWithBossMod = false) : ITask
    {
        public List<QuestWorkValue?> CompletionQuestVariablesFlags { get; } = CompletionQuestVariablesFlags ?? [];

        public bool HasCompletionQuestVariablesFlags { get; } =
            Quest != null &&
            CompletionQuestVariablesFlags != null &&
            QuestWorkUtils.HasCompletionFlags(CompletionQuestVariablesFlags);

        public bool ShouldRedoOnInterrupt()
        {
            return true;
        }

        public override string ToString()
        {
            return $"Interact{(HasCompletionQuestVariablesFlags ? "*" : "")}({DataId})";
        }
    }

    internal sealed class DoInteract
    (
        GameFunctions gameFunctions,
        QuestFunctions questFunctions,
        CameraFunctions cameraFunctions,
        Configuration configuration,
        ICondition condition,
        IChatGui chatGui,
        IObjectTable objectTable,
        ClassJobUtils classJobUtils,
        ILogger<DoInteract> logger)
        : TaskExecutor<Task>, IConditionChangeAware
    {
        private DateTime _continueAt = DateTime.MinValue;
        private EInteractionState _interactionState = EInteractionState.None;
        private bool _needsFacing;
        private bool _needsUnmount;
        private bool _reportedGameObjNull;
        private bool _boundByDutyAtStart;
        private readonly InteractionRetry _retry = new();

        /// <summary>換職連續下達多久、幾次仍不在任務接受的職業上就判定為永久失敗（兩個條件都要滿足）。</summary>
        private static readonly TimeSpan JobSwitchGiveUpAfter = TimeSpan.FromSeconds(10);

        private const int JobSwitchGiveUpAttempts = 10;

        private int _jobSwitchAttempts;
        private DateTime _jobSwitchStartedAt;
        private Job _jobSwitchInitialJob;
        private int _lastEquipGearsetResult;

        /// <summary>
        ///     A slight delay when we think an interaction has ended, to make sure that we're processing "Action cancelled"
        ///     prior to the next step (in case we're attacked).
        /// </summary>
        private bool delayedFinalCheck;

        public Quest? Quest => Task.Quest;
        public EInteractionType InteractionType { get; set; }

        /// <summary>
        /// 這次互動是為了進 BossMod 代打的任務戰鬥，而人已經被送進副本了。
        /// </summary>
        /// <remarks>
        /// 進了副本就代表互動成功；此後不能再被「受到傷害」打斷，否則佇列會改由 Questionable
        /// 自己的戰鬥控制器接手，BossMod AI 根本不會被開啟。
        /// </remarks>
        private bool HasEnteredSoloDuty =>
            Task.EntersSoloDutyWithBossMod && !_boundByDutyAtStart && condition[ConditionFlag.BoundByDuty];

        public override bool WasInterrupted()
        {
            return !HasEnteredSoloDuty && base.WasInterrupted();
        }

        public override ETaskResult Update()
        {
            if (LogGate.TraceEnabled) logger.LogTrace("Entered Update, _continueAt: {ContinueAt}", _continueAt);
            if (HasEnteredSoloDuty)
            {
                logger.LogInformation("Entered the quest battle instance, interaction with {DataId} is done", Task.DataId);
                return ETaskResult.TaskComplete;
            }

            if (DateTime.Now <= _continueAt)
            {
                return ETaskResult.StillRunning;
            }

            if (_needsUnmount)
            {
                if (condition[ConditionFlag.Mounted])
                {
                    logger.LogDebug("Attempting unmount");
                    gameFunctions.Unmount();
                    _continueAt = DateTime.Now.AddSeconds(1);
                    return ETaskResult.StillRunning;
                }
                else
                {
                    _needsUnmount = false;
                }
            }
            else
            {
                if (LogGate.TraceEnabled) logger.LogTrace("Does not need unmount");
            }

            if (Task.PickUpItemId is { } pickUpItemId)
            {
                if (LogGate.TraceEnabled) logger.LogTrace("PickUpItemId {PickUpItemId}", pickUpItemId);
                unsafe
                {
                    InventoryManager* inventoryManager = InventoryManager.Instance();
                    if (inventoryManager->GetInventoryItemCount(pickUpItemId) > 0)
                    {
                        return ETaskResult.TaskComplete;
                    }
                }
            }
            else if (Task.TaxiStandId is { } taxiStandId)
            {
                if (LogGate.TraceEnabled) logger.LogTrace("TaxiStandId {TaxiStandId}", taxiStandId);
                unsafe
                {
                    UIState* uiState = UIState.Instance();
                    if (uiState->IsChocoboTaxiStandUnlocked(taxiStandId))
                    {
                        return ETaskResult.TaskComplete;
                    }
                }
            }
            else if (InteractionType == EInteractionType.Gather && condition[ConditionFlag.Gathering])
            {
                return ETaskResult.TaskComplete;
            }
            else if (Quest != null && Task.HasCompletionQuestVariablesFlags)
            {
                if (LogGate.TraceEnabled) logger.LogTrace("Checking QW");
                QuestProgressInfo? questWork = questFunctions.GetQuestProgressInfo(Quest.Id);

                if (questWork != null && QuestWorkUtils.MatchesQuestWork(Task.CompletionQuestVariablesFlags, questWork))
                {
                    return ETaskResult.TaskComplete;
                }
            }
            else if (ProgressContext != null)
            {
                if (LogGate.TraceEnabled) logger.LogTrace("Entered ProgressContext");
                if (ProgressContext.WasInterrupted())
                {
                    return ETaskResult.StillRunning;
                }
                // 沒觸發新施放（CheckSequence=false）時 WasSuccessful 只反映上一次施放，不能當這次互動的成敗。
                else if ((ProgressContext.CheckSequence && ProgressContext.WasSuccessful()) ||
                         _interactionState == EInteractionState.InteractionConfirmed)
                {
                    if (delayedFinalCheck)
                    {
                        return ETaskResult.TaskComplete;
                    }

                    _continueAt = DateTime.Now.AddSeconds(0.2);
                    delayedFinalCheck = true;
                    return ETaskResult.StillRunning;
                }
            }
            else
            {
                if (LogGate.TraceEnabled) logger.LogTrace("Conditions block passed");
            }

            IGameObject? gameObject = gameFunctions.FindObjectByDataId(Task.DataId);
            //if (gameObject == null || !gameObject.IsTargetable || !HasAnyMarker(gameObject))
            if (gameObject == null)
            {
                MarkTargetGoneAfterInteraction("left the object table");
                if (!_reportedGameObjNull)
                {
                    logger.LogDebug("gameObject is null");
                    _reportedGameObjNull = true;
                }
                return ETaskResult.StillRunning;
            }
            _reportedGameObjNull = false;
            if (LogGate.TraceEnabled) logger.LogTrace("gameObject != null");

            if (_needsFacing)
            {
                logger.LogInformation("Facing target");
                cameraFunctions.Face(gameObject.Position);
                _continueAt = DateTime.Now.AddSeconds(0.2);
                _needsFacing = false;
                return ETaskResult.StillRunning;
            }
            else
            {
                if (LogGate.TraceEnabled) logger.LogTrace("Does not need facing");
            }

            if (objectTable[0] is IPlayerCharacter player && Task.Quest != null && InteractionType == EInteractionType.AcceptQuest)
            {
                List<Job> acceptableJobs = [.. Task.Quest.Info.ClassJobs];
                Job playerJob = (Job)player.ClassJob.Value.RowId;
                if (acceptableJobs.Count >= 1 && !acceptableJobs.Contains(playerJob))
                {
                    // 只有一個可接受職業（職業任務）時不套用設定裡的職業偏好：
                    // 換過去一樣不合格，下一幀再換一次，任務永遠接不了。
                    bool singleJobQuest = acceptableJobs.Count == 1;
                    Job questJob = acceptableJobs[0];
                    string reason = singleJobQuest ? "quest accepts only this job" : "configured job preference";
                    if (!singleJobQuest)
                    {
                        // 設定值可能是 ADV（Auto），也可能是玩家沒有套組的職業，所以先解析成
                        // 實際職業；解不出或任務不收這個職業時沿用任務自己的第一個職業。
                        Job candidate = ResolveConfiguredJob(Task.Quest, questJob);
                        if (candidate != Job.ADV && acceptableJobs.Contains(candidate))
                        {
                            acceptableJobs = [.. acceptableJobs.Prepend(candidate)];
                        }
                        else
                        {
                            logger.LogInformation(
                                "Configured job {Candidate} is not usable for {QuestId}, using {QuestJob} instead",
                                candidate, Task.Quest.Id, questJob);
                            reason = "configured job unusable";
                        }
                    }

                    Job targetJob = acceptableJobs[0];
                    unsafe
                    {
                        RaptureGearsetModule* gearsetModule = RaptureGearsetModule.Instance();
                        List<Job> jobsWithGearset =
                            gearsetModule != null ? FindJobsWithGearset(gearsetModule, acceptableJobs) : [];
                        if (jobsWithGearset.Count == 0)
                        {
                            // 任務收的職業全都沒有套組，再等也不會好。拋 TaskException 交給控制器既有的失敗停止路徑：
                            // 清空佇列並切回 Manual（IsRunning 端點隨之轉 false），並請 TataruPraise 喊「需要幫忙」。
                            logger.LogInformation(
                                "Cannot switch job for {QuestId}: wanted {TargetJob} ({Reason}), {Failure}; accepted jobs: {AcceptedJobs}",
                                Task.Quest.Id, targetJob, reason,
                                gearsetModule == null ? "gearset module unavailable" : "no gearset for any accepted job",
                                string.Join(", ", acceptableJobs.Distinct()));
                            chatGui.PrintError($"Quest {Task.Quest.Info.Name} requires a job like {targetJob}, " +
                                               "but you do not have a valid job configured in QST Settings.");
                            throw new TaskException($"No usable gearset for any job accepted by quest {Task.Quest.Id}");
                        }

                        if (!jobsWithGearset.Contains(targetJob))
                        {
                            // 首選職業沒有套組、但任務收的其他職業有：不算確定失敗，改用其中依 Auto 規則排第一的職業。
                            Job fallbackJob = classJobUtils.PickPreferredJob(jobsWithGearset);
                            if (fallbackJob == Job.ADV)
                            {
                                fallbackJob = jobsWithGearset[0];
                            }

                            logger.LogInformation("No gearset for {TargetJob} ({QuestId}), using {FallbackJob} instead",
                                targetJob, Task.Quest.Id, fallbackJob);
                            targetJob = fallbackJob;
                            reason = "no gearset for preferred job";
                        }

                        if (_jobSwitchAttempts == 0)
                        {
                            _jobSwitchStartedAt = DateTime.Now;
                            _jobSwitchInitialJob = playerJob;
                            logger.LogInformation(
                                "Current ClassJob {PlayerJob} not valid for {QuestId}, switching to {TargetJob} (reason: {Reason})",
                                playerJob, Task.Quest.Id, targetJob, reason);
                        }
                        else if (_jobSwitchAttempts >= JobSwitchGiveUpAttempts &&
                                 DateTime.Now - _jobSwitchStartedAt >= JobSwitchGiveUpAfter)
                        {
                            // 套組已經換過這麼多次職業還是不對：主手不在身上、缺靈魂水晶變成基本職業之類，再換也一樣。
                            string failure = DescribeJobSwitchFailure(gearsetModule, targetJob, playerJob);
                            logger.LogInformation(
                                "Cannot switch job for {QuestId}: still {PlayerJob} after {Attempts} gearset changes to {TargetJob} over {Seconds:F0}s, {Failure}; accepted jobs: {AcceptedJobs}",
                                Task.Quest.Id, playerJob, _jobSwitchAttempts, targetJob,
                                (DateTime.Now - _jobSwitchStartedAt).TotalSeconds, failure,
                                string.Join(", ", acceptableJobs.Distinct()));
                            chatGui.PrintError($"Quest {Task.Quest.Info.Name} requires a job like {targetJob}, " +
                                               $"but changing to that gearset did not work: {failure}.");
                            throw new TaskException($"Gearset change to {targetJob} did not take effect for quest {Task.Quest.Id}");
                        }
                        else
                        {
                            logger.LogDebug("Still {PlayerJob} for {QuestId}, retrying gearset change to {TargetJob} (attempt {Attempt}, last result {Result})",
                                playerJob, Task.Quest.Id, targetJob, _jobSwitchAttempts + 1, _lastEquipGearsetResult);
                        }

                        for(int i = 0; i < 100; ++i)
                        {
                            RaptureGearsetModule.GearsetEntry* gearset = gearsetModule->GetGearset(i);
                            // 空槽的 ClassJob 是 0（＝ADV），少了 Exists 就會被當成有效套組命中。
                            if (gearset == null ||
                                !gearset->Flags.HasFlag(RaptureGearsetModule.GearsetFlag.Exists))
                            {
                                continue;
                            }

                            if (targetJob.Equals((Job)gearset->ClassJob))
                            {
                                _lastEquipGearsetResult = gearsetModule->EquipGearset(gearset->Id);
                            }
                        }

                        _jobSwitchAttempts++;
                    }

                    // 換裝中、動畫鎖、暫時不能換都是暫態：每 0.2 秒重判，直到上面的上限才判定為永久。
                    _continueAt = DateTime.Now.AddSeconds(0.2);
                    return ETaskResult.StillRunning;
                }
            }
            else
            {
                if (LogGate.TraceEnabled) logger.LogTrace("is not AcceptQuest");
            }

            if (!gameObject.IsTargetable || !HasAnyMarker(gameObject))
            {
                MarkTargetGoneAfterInteraction(gameObject.IsTargetable ? "lost its quest marker" : "became untargetable");
                return ETaskResult.StillRunning;
            }

            if (!_retry.ShouldSend(condition, logger, Task))
            {
                return ETaskResult.StillRunning;
            }

            TriggerInteraction(gameObject);
            return ETaskResult.StillRunning;
        }

        /// <summary>送出後目標消失／不能選／任務標記不見＝遊戲處理了這次互動（只送出前就不在的不算）。</summary>
        private void MarkTargetGoneAfterInteraction(string what)
        {
            if (_interactionState != EInteractionState.InteractionTriggered)
            {
                return;
            }

            logger.LogInformation("{DataId} {What} after the interaction, treating the interaction as accepted", Task.DataId, what);
            _interactionState = EInteractionState.InteractionConfirmed;
            _retry.OnReaction();
        }

        /// <summary>
        ///     把設定裡的職業偏好解析成「任務類型對得上、而且玩家真的有套組」的職業。
        ///     設定值是 ADV（Auto）時由 ClassJobUtils 依等級／裝備等級代為挑選。
        /// </summary>
        /// <returns>可用的職業；<see cref="Job.ADV"/> 表示解不出任何可用職業。</returns>
        private Job ResolveConfiguredJob(Quest quest, Job questJob)
        {
            EExtendedClassJob configured;
            if (quest.Info.AlliedSociety.Equals(EAlliedSociety.Namazu))
            {
                configured = configuration.Advanced.NamazuPreferCraft
                    ? EExtendedClassJob.ConfiguredCraftingJob
                    : EExtendedClassJob.ConfiguredGatheringJob;
            }
            else if (questJob.IsCrafter())
            {
                configured = EExtendedClassJob.ConfiguredCraftingJob;
            }
            else if (questJob.IsGatherer())
            {
                configured = EExtendedClassJob.ConfiguredGatheringJob;
            }
            else
            {
                configured = EExtendedClassJob.ConfiguredCombatJob;
            }

            return classJobUtils.AsIndividualJobs(configured, quest.Id).FirstOrDefault();
        }

        /// <summary>換職失敗的最可能原因，只用於記錄與聊天欄訊息；判斷依序是套組旗標、職業變化、換裝回傳值。</summary>
        private unsafe string DescribeJobSwitchFailure(RaptureGearsetModule* gearsetModule, Job targetJob, Job playerJob)
        {
            for(int i = 0; i < 100; ++i)
            {
                RaptureGearsetModule.GearsetEntry* gearset = gearsetModule->GetGearset(i);
                if (gearset != null &&
                    gearset->Flags.HasFlag(RaptureGearsetModule.GearsetFlag.Exists) &&
                    targetJob.Equals((Job)gearset->ClassJob) &&
                    gearset->Flags.HasFlag(RaptureGearsetModule.GearsetFlag.MainHandMissing))
                {
                    return $"the main arm of gearset {gearset->Id + 1} is missing from the Armoury Chest";
                }
            }

            if (playerJob != targetJob && playerJob == targetJob.GetDowngradedJob())
            {
                return $"the gearset makes you {playerJob} instead of {targetJob}, the soul crystal is probably missing";
            }

            if (_lastEquipGearsetResult != 0)
            {
                return $"the game refused the gearset change (result {_lastEquipGearsetResult})";
            }

            return playerJob == _jobSwitchInitialJob
                ? "the job never changed"
                : $"the job changed to {playerJob}, which this quest does not accept";
        }

        /// <summary>
        ///     <paramref name="acceptableJobs"/> 裡「至少有一個已建立套組」的職業，保持原順序、不重複。
        /// </summary>
        private static unsafe List<Job> FindJobsWithGearset(RaptureGearsetModule* gearsetModule, List<Job> acceptableJobs)
        {
            HashSet<Job> jobsWithGearset = [];
            for(int i = 0; i < 100; ++i)
            {
                RaptureGearsetModule.GearsetEntry* gearset = gearsetModule->GetGearset(i);
                if (gearset != null && gearset->Flags.HasFlag(RaptureGearsetModule.GearsetFlag.Exists))
                {
                    jobsWithGearset.Add((Job)gearset->ClassJob);
                }
            }

            return acceptableJobs.Where(jobsWithGearset.Contains).Distinct().ToList();
        }

        public void OnConditionChange(ConditionFlag flag, bool value)
        {
            if (ProgressContext is { CheckSequence: true } &&
                (ProgressContext.WasInterrupted() || ProgressContext.WasSuccessful()))
            {
                return;
            }

            logger.LogDebug("Condition change: {Flag} = {Value}", flag, value);
            // 詠唱不算：施放要等 CheckSequence 那條路判完成，不能一開始詠唱就當成功。
            if (_interactionState == EInteractionState.InteractionTriggered && value &&
                flag is ConditionFlag.OccupiedInQuestEvent or ConditionFlag.OccupiedInEvent
                    or ConditionFlag.OccupiedInCutSceneEvent or ConditionFlag.WatchingCutscene
                    or ConditionFlag.WatchingCutscene78 or ConditionFlag.BetweenAreas
                    or ConditionFlag.BetweenAreas51 or ConditionFlag.Gathering)
            {
                logger.LogInformation("Interaction was most likely triggered ({Flag})", flag);
                _interactionState = EInteractionState.InteractionConfirmed;
            }
        }

        public override bool ShouldInterruptOnDamage()
        {
            return !HasEnteredSoloDuty;
        }

        protected override bool Start()
        {
            InteractionType = Task.InteractionType;
            _boundByDutyAtStart = condition[ConditionFlag.BoundByDuty];

            _needsFacing = true;
            IGameObject? gameObject = gameFunctions.FindObjectByDataId(Task.DataId);
            if (gameObject == null)
            {
                logger.LogWarning("No game object with dataId {DataId}", Task.DataId);
                return false;
            }

            if (!gameObject.IsTargetable && Task.SkipConditions is { Never: false, NotTargetable: true })
            {
                logger.LogInformation("Not interacting with {DataId} because it is not targetable (but skippable)",
                    Task.DataId);
                return false;
            }

            // interacting always fails while airborne on a flying mount, regardless of the quest
            // step's own Fly/Land configuration; land (and if needed, dismount) before trying
            if (condition[ConditionFlag.InFlight])
            {
                logger.LogInformation("Preparing interaction for {DataId} by landing", Task.DataId);
                _needsUnmount = true;
                gameFunctions.Unmount();
                _continueAt = DateTime.Now.AddSeconds(1);
                return true;
            }

            // this is only relevant for followers on quests
            if (!gameObject.IsTargetable && condition[ConditionFlag.Mounted] &&
                gameObject.ObjectKind != ObjectKind.GatheringPoint)
            {
                logger.LogInformation("Preparing interaction for {DataId} by unmounting", Task.DataId);
                _needsUnmount = true;
                gameFunctions.Unmount();
                _continueAt = DateTime.Now.AddSeconds(1);
                return true;
            }

            return true;
        }

        private void TriggerInteraction(IGameObject gameObject)
        {
            ProgressContext =
                InteractionProgressContext.FromActionUseOrDefault(() =>
                {
                    if (gameFunctions.InteractWith(gameObject))
                    {
                        _interactionState = EInteractionState.InteractionTriggered;
                    }
                    else
                    {
                        _interactionState = EInteractionState.None;
                    }
                    return _interactionState != EInteractionState.None;
                });
            // 從沒送出過（距離外之類）維持原本每 0.5 秒再試；送出過之後的每一次都算進重試上限。
            if (_interactionState == EInteractionState.InteractionTriggered || _retry.Attempts > 0)
            {
                _retry.OnSent();
            }

            _continueAt = DateTime.Now.AddSeconds(0.5);
        }

        private unsafe bool HasAnyMarker(IGameObject gameObject)
        {
            if (Task.SkipMarkerCheck || gameObject.ObjectKind != ObjectKind.EventNpc)
            {
                return true;
            }

            GameObject* gameObjectStruct = (GameObject*)gameObject.Address;
            return gameObjectStruct->NamePlateIconId != 0;
        }

        private enum EInteractionState
        {
            None,
            InteractionTriggered,
            InteractionConfirmed
        }
    }
}

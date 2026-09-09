using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using ECommons.ExcelServices;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game;
using Microsoft.Extensions.Logging;
using Questionable.Controller.Steps.Common;
using Questionable.Controller.Steps.Fishing;
using Questionable.Controller.Utils;
using Questionable.Data;
using Questionable.External;
using Questionable.Functions;
using Questionable.Model;
using Questionable.Model.Questing;
using static Questionable.Controller.Steps.ITaskExecutor;

namespace Questionable.Controller.Steps.Shared;

internal static class Fish
{
    internal sealed class Factory(IAutoHookIpc autoHookIpc) : ITaskFactory
    {
        public IEnumerable<ITask> CreateAllTasks(Quest quest, QuestSequence sequence, QuestStep step)
        {
            if (step.InteractionType != EInteractionType.Fish)
                yield break;

            if (!autoHookIpc.IsAvailable())
                yield break;

            yield return new Mount.UnmountTask();
            yield return new SwitchClassJob.Task(Job.FSH);

            // Ensure we have at least one fish task. Quests that need key items (e.g. And Thanks for All the Fish) do not have itemIds and so will have no requested items.
            yield return new FishTask(quest, step.ItemsToGather.FirstOrDefault(), step.CompletionQuestVariablesFlags);

            // Create additional fish tasks for any additional items.
            foreach (GatheredItem item in step.ItemsToGather.Skip(1))
                yield return new FishTask(quest, item, step.CompletionQuestVariablesFlags);
        }
    }

    internal sealed record FishTask
    (
        Quest Quest,
        GatheredItem? GatheredItem,
        IList<QuestWorkValue?> CompletionQuestVariablesFlags) : ITask
    {
        public bool HasCompletionQuestVariablesFlags { get; } =
            QuestWorkUtils.HasCompletionFlags(CompletionQuestVariablesFlags);

        public override string ToString() =>
            $"Fish{(HasCompletionQuestVariablesFlags ? "*" : "")}{(GatheredItem != null ? $"({GatheredItem.ItemCount}x {GatheredItem.ItemId})" : "")}";
    }

    internal sealed class DoFish(
        IAutoHookIpc autoHookIpc,
        ICommandManager commandManager,
        ICondition condition,
        GameFunctions gameFunctions,
        IChatGui chatGui,
        SendNotification.Executor sendNotificationExecutor,
        IFishingPresetGenerator fishingPresetGenerator,
        QuestFunctions questFunctions,
        ILogger<DoFish> logger) : TaskExecutor<FishTask>, IStoppableTaskExecutor
    {
        /// <summary>
        /// 結束時要還原成什麼。
        /// 🔴 這裡<b>刻意</b>問的是使用者自己的值（<c>GetPluginState</c>）而不是實際生效的值：
        /// 拿疊加後的值當快照，會在別的外掛壓制期間拍到 <see langword="false"/>，
        /// 之後 <see cref="Cleanup"/> 就把那個 <see langword="false"/>「還」給使用者，
        /// 永久改掉他自己設定頁裡勾的東西。
        /// </summary>
        private readonly bool _wasAutoHookEnabled = autoHookIpc.IsPluginEnabled();

        private bool _started;
        private bool _cleanupDone;

        /// <summary>我們是否動過 AutoHook 的啟用開關（動過就一定要還原，即使還沒開始釣）。</summary>
        private bool _touchedAutoHook;

        /// <summary>「被別的外掛壓制中」這一行是否已經寫過（同一段壓制只寫一次，不要每秒洗版）。</summary>
        private bool _suppressionLogged;

        protected override bool Start()
        {
            if (HasRequestedItem(Task.GatheredItem))
            {
                logger.LogInformation($"Already have {Task.GatheredItem!.ItemCount}x {Task.GatheredItem.ItemId} in inventory", Task.GatheredItem.ItemCount, Task.GatheredItem.ItemId);
                return false;
            }

            if (HasMatchingCompletionQuestWork())
            {
                logger.LogInformation("Quest variables already match, skipping fish task.");
                return false;
            }

            // AutoHook is required for this task to work.
            // 🔴 回 false ＝這一輪先不要拋竿，但任務要留著（回 true 讓 MiniTaskController 保留執行器），
            //    Update() 每秒會再進來重試一次。回 false 會被當成「這個任務被跳過」而直接丟掉。
            if (!EnsureAutoHookRunning())
                return true;

            // Only create and select the anonymous preset if we haven't started yet. This prevents us from creating multiple presets.
            if (!_started)
            {
                logger.LogDebug("Starting fish task for quest {QuestId}.", Task.Quest.Id);

                if (!FishingData.FishingPresets.TryGetValue((QuestId)Task.Quest.Id, out string? presetExport))
                {
                    if (Task.GatheredItem?.FishingOptions?.Preset != null)
                        presetExport = Task.GatheredItem?.FishingOptions?.Preset!;
                    else
                    {
                        logger.LogDebug("No fishing preset found for quest {QuestId}. Autocreating from quest data.", Task.Quest.Id);

                        presetExport = fishingPresetGenerator.CreatePresetFromTask(Task);
                        logger.LogDebug(presetExport);
                    }
                }

                // Using an anonymouse preset allows us to easily remove it later.
                logger.LogInformation("Creating and selecting anonymous AutoHook preset for quest {QuestId}", Task.Quest.Id);
                autoHookIpc.CreateAndSelectAnonymousPreset(presetExport);
            }

            // Start fishing via command
            // Native command: gameFunctions.UseAction(EAction.FSHCast);
            logger.LogDebug("Starting fishing");
            commandManager.ProcessCommand("/ahstart");

            _started = true;
            return true;
        }

        public override ETaskResult Update()
        {
            if (HasRequestedItem(Task.GatheredItem))
            {
                logger.LogDebug("Requested item collected. Completing task.");
                Cleanup();
                return ETaskResult.TaskComplete;
            }

            if (HasMatchingCompletionQuestWork())
            {
                logger.LogDebug("Quest variables match. Completing task.");
                Cleanup();
                return ETaskResult.TaskComplete;
            }

            if (EzThrottler.Throttle("FishStart", 1000) && !condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Gathering])
            {
                logger.LogDebug("We don't seem to be fishing. Trying again.");
                Start();
                return ETaskResult.StillRunning;
            }
            return ETaskResult.StillRunning;
        }

        public void StopNow()
        {
            // 🔴 _touchedAutoHook 也要算：被別的外掛壓制而停在「已經幫使用者打開、但還沒拋竿」時
            //    _started 仍是 false，只看 _started 會讓那次打開永遠還不回去。
            if (_started || _touchedAutoHook)
                Cleanup();
        }

        // we're on a gathering class, so combat doesn't make much sense (we also can't change classes in combat...)
        public override bool ShouldInterruptOnDamage() => false;

        private bool HasMatchingCompletionQuestWork()
        {
            if (!Task.HasCompletionQuestVariablesFlags)
                return false;

            QuestProgressInfo? questWork = questFunctions.GetQuestProgressInfo(Task.Quest.Id);
            return questWork != null &&
                   QuestWorkUtils.MatchesQuestWork(Task.CompletionQuestVariablesFlags, questWork);
        }

        /// <summary>
        /// 確認 AutoHook 現在<b>真的會動</b>。回 <see langword="false"/> ⇒ 這一輪不要 <c>/ahstart</c>，
        /// 等下一次重試。
        /// </summary>
        /// <remarks>
        /// 🔴 <b>「現在該不該自己動手」問的是實際生效的狀態</b>（使用者的值疊上別的外掛的暫停租約），
        /// 不是建構時拍下的那張使用者快照 <see cref="_wasAutoHookEnabled"/>。
        /// 只問使用者的值時，別的外掛（例如 GatherBuddyReborn 自動採集期間持有的
        /// <c>AutoHook.AcquireSuppressionFor</c> 租約）壓著的期間會得到 <see langword="true"/>，
        /// 於是我們以為 AutoHook 會幫忙上鉤 —— 實際上 AutoHook 的 <c>OnFrameworkUpdate</c> 讀的是
        /// 疊加後的值並且直接早退，竿子拋出去沒有任何人上鉤，釣魚任務就這樣<b>完全靜默</b>地卡住。
        /// <para>
        /// 🔴 <b>被壓制時 <c>SetPluginState</c> 幫不上忙</b>：那支只寫使用者自己的值，壓不掉別人的租約
        /// （AutoHook 的租約是「只能往停手的方向壓」）。所以那種情況唯一正確的動作是<b>等</b>，
        /// 順便寫一行 <c>Information</c> 讓使用者知道是誰擋著。
        /// </para>
        /// </remarks>
        private bool EnsureAutoHookRunning()
        {
            if (autoHookIpc.IsEffectivePluginEnabled())
            {
                if (_suppressionLogged)
                {
                    logger.LogInformation("AutoHook 的暫停租約已經放開，繼續釣魚任務。");
                    _suppressionLogged = false;
                }

                return true;
            }

            // 使用者自己開著、卻沒有實際生效 ⇒ 一定是別的外掛持有暫停租約。只能等它放開或逾時。
            if (autoHookIpc.IsPluginEnabled())
                return LogSuppressedAndWait();

            // 使用者自己把 AutoHook 關著 —— 這是我們可以處理的，幫他打開（Cleanup 會還原成使用者的值）。
            if (!autoHookIpc.SetPluginEnabled(enabled: true))
            {
                const string errorText =
                    "AutoHook is required for fishing but could not be enabled. Please install or enable AutoHook.";
                logger.LogWarning("{ErrorText}", errorText);
                if (!sendNotificationExecutor.Start(new SendNotification.Task(EInteractionType.Fish, errorText)))
                    chatGui.PrintError(errorText, CommandHandler.MessageTag, CommandHandler.TagColor);
                throw new TaskException(errorText);
            }

            _touchedAutoHook = true;

            // 打開之後再問一次：若同時還有別人的租約壓著，打開使用者的值一樣不會生效。
            return autoHookIpc.IsEffectivePluginEnabled() || LogSuppressedAndWait();
        }

        /// <summary>寫一行「被壓制中」並回 <see langword="false"/>；同一段壓制只寫一次。</summary>
        /// <remarks>
        /// 🔴 等級是 <c>Information</c>：這條路徑原本<b>完全靜默</b>，使用者只會看到釣魚任務不動了，
        /// log 裡一個字都沒有。
        /// </remarks>
        private bool LogSuppressedAndWait()
        {
            if (!_suppressionLogged)
            {
                logger.LogInformation(
                    "AutoHook 目前被其他外掛的暫停租約壓制中，本次不接手釣魚；等租約放開或逾時後會自動繼續。");
                _suppressionLogged = true;
            }

            return false;
        }

        private void Cleanup()
        {
            if (_cleanupDone)
                return;

            logger.LogDebug("Cleaning up fish task.");

            // Make sure we're not fishing anymore.
            gameFunctions.UseAction(EAction.FSHQuit);

            // Clean up anonymous preset
            autoHookIpc.DeleteAllAnonymousPresets();

            // Respect player's current settings. Set plugin to the state it was in at the start.
            autoHookIpc.SetPluginEnabled(_wasAutoHookEnabled);

            _cleanupDone = true;
            _started = false;
            _touchedAutoHook = false;
        }
    }

    private static unsafe bool HasRequestedItem(GatheredItem? item)
    {
        if (item == null)
            return false;

        InventoryManager* inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
            return false;

        return inventoryManager->GetInventoryItemCount(item.ItemId,
            minCollectability: (short)item.Collectability) >= item.ItemCount;
    }
}

using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using ECommons.ExcelServices;
using ECommons.LanguageHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Questionable.Controller.Steps.Common;
using Questionable.Data;
using Questionable.Model;
using Questionable.Model.Gathering;
using Questionable.Model.Questing;
using System;
using System.Collections.Generic;
using System.Linq;
using Action = Questionable.Controller.Steps.Interactions.Action;

namespace Questionable.Controller.Steps.Shared;

internal static class Gather
{
    internal sealed class Factory : ITaskFactory
    {
        public IEnumerable<ITask> CreateAllTasks(Quest quest, QuestSequence sequence, QuestStep step)
        {
            if (step.InteractionType != EInteractionType.Gather)
            {
                yield break;
            }

            foreach(GatheredItem itemToGather in step.ItemsToGather)
            {
                yield return new DelayedGatheringTask(itemToGather, quest, sequence.Sequence, step);
            }
        }
    }

    internal sealed record DelayedGatheringTask(GatheredItem GatheredItem, Quest Quest, byte Sequence, QuestStep Step) : ITask
    {
        /// <summary>已經為這個道具自動換過職業；職業仍對不上就停下請玩家自己換，不再換第二次。</summary>
        public Job? SwitchedToClassJob { get; init; }

        public override string ToString()
        {
            return SwitchedToClassJob is { } job
                ? $"Gathering(pending for {GatheredItem.ItemId}, after switching to {job})"
                : $"Gathering(pending for {GatheredItem.ItemId})";
        }
    }

    internal sealed class DelayedGatheringExecutor
    (
        GatheringPointRegistry gatheringPointRegistry,
        TerritoryData territoryData,
        IClientState clientState,
        IObjectTable objectTable,
        IServiceProvider serviceProvider,
        IDataManager dataManager,
        ILogger<DelayedGatheringExecutor> logger) : TaskExecutor<DelayedGatheringTask>, IExtraTaskCreator
    {
        public override ETaskResult Update()
        {
            return ETaskResult.CreateNewTasks;
        }

        public IEnumerable<ITask> CreateExtraTasks()
        {
            Job currentClassJob = (Job)((IPlayerCharacter)objectTable[0]!).ClassJob.RowId;
            GatheringPointId? gatheringPointId;
            if (Task.Step.GatheringPoint is ushort gatheringPoint)
            {
                gatheringPointId = new(gatheringPoint);
            }
            else if (!gatheringPointRegistry.TryGetGatheringPointId(Task.GatheredItem.ItemId, currentClassJob,
                out gatheringPointId))
            {
                uint itemId = Task.GatheredItem.ItemId;
                List<Job> gatheringJobs = new[] { Job.MIN, Job.BTN }
                    .Where(x => gatheringPointRegistry.TryGetGatheringPointId(itemId, x, out _))
                    .ToList();
                if (gatheringJobs.Count > 0)
                {
                    // 背包已夠就不必換套組或停下（與下面採集前的同一個判斷）。
                    if (HasRequiredItems(Task.GatheredItem))
                    {
                        logger.LogInformation("Already have enough of item {ItemId} as {CurrentJob}, skipping the gatherer job switch", itemId, currentClassJob);
                        yield break;
                    }

                    string reason = $"No gathering point found for item {itemId} as {currentClassJob}, gatherable as {string.Join("/", gatheringJobs)}";
                    string chatMessage =
                        "Switch to ?? first: your current job (??) has no gathering point for ??. Automatic questing has been stopped; press Start again after switching.".Loc(
                            string.Join("/", gatheringJobs.Select(GetJobName)), GetJobName(currentClassJob), GetItemName(itemId));
                    if (Task.SwitchedToClassJob is { } attemptedJob)
                    {
                        throw new ManualActionRequiredException($"{reason}; switching to {attemptedJob} did not take effect", chatMessage);
                    }

                    // 照釣魚步驟的先例先下坐騎再換套組；沒有套組時由 SwitchClassJob 擲出同一句提示，
                    // 換了卻沒生效則由下面重排的同一個工作再判一次（只換一次，不會循環）。
                    Job targetJob = gatheringJobs[0];
                    logger.LogInformation("{Reason}, switching to {TargetJob} first", reason, targetJob);
                    yield return new Mount.UnmountTask();
                    yield return new SwitchClassJob.Task(targetJob) { ManualActionChatMessage = chatMessage };
                    yield return Task with { SwitchedToClassJob = targetJob };
                    yield break;
                }

                throw new TaskException($"No gathering point found for item {itemId}");
            }

            if (!gatheringPointRegistry.TryGetGatheringPoint(gatheringPointId, out GatheringRoot? gatheringRoot))
            {
                throw new TaskException($"No path found for gathering point {gatheringPointId.Value}");
            }

            if (HasRequiredItems(Task.GatheredItem))
            {
                yield break;
            }

            if (currentClassJob == Job.MIN)
            {
                yield return new Action.TriggerStatusIfMissing(EStatus.Prospect, EAction.Prospect);
            }
            else if (currentClassJob == Job.BTN)
            {
                yield return new Action.TriggerStatusIfMissing(EStatus.Triangulate, EAction.Triangulate);
            }

            using (IDisposable? _ = logger.BeginScope("Gathering(inner)"))
            {
                QuestSequence gatheringSequence = new()
                {
                    Sequence = 0,
                    Steps = gatheringRoot.Steps
                };
                foreach(QuestStep gatheringStep in gatheringSequence.Steps)
                {
                    foreach(ITask task in serviceProvider.GetRequiredService<TaskCreator>()
                        .CreateTasks(Task.Quest, Task.Sequence, gatheringSequence, gatheringStep))
                    {
                        if (task is WaitAtEnd.NextStep)
                        {
                            yield return new SkipMarker();
                        }
                        else
                        {
                            yield return task;
                        }
                    }
                }
            }

            uint territoryId = gatheringRoot.Steps.Last().TerritoryId;
            yield return new WaitCondition.Task(() => clientState.TerritoryType == territoryId,
                $"Wait(territory: {territoryData.GetNameAndId(territoryId)})");

            yield return new WaitNavmesh.Task();

            yield return new GatheringTask(gatheringPointId, Task.GatheredItem);
            yield return new WaitAtEnd.WaitDelay();
        }

        public override bool ShouldInterruptOnDamage()
        {
            return false;
        }
        protected override bool Start()
        {
            return true;
        }

        /// <summary>遊戲表裡的名稱（台服即繁中）；查不到時退回列舉名或編號。</summary>
        private string GetJobName(Job job)
        {
            string? name = dataManager.GetExcelSheet<Lumina.Excel.Sheets.ClassJob>()
                .GetRowOrDefault((uint)job)?.Name.ToString();
            return string.IsNullOrEmpty(name) ? job.ToString() : name;
        }

        private string GetItemName(uint itemId)
        {
            string? name = dataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>()
                .GetRowOrDefault(itemId)?.Name.ToString();
            return string.IsNullOrEmpty(name) ? itemId.ToString(System.Globalization.CultureInfo.InvariantCulture) : name;
        }

        private unsafe bool HasRequiredItems(GatheredItem itemToGather)
        {
            InventoryManager* inventoryManager = InventoryManager.Instance();
            return inventoryManager != null &&
                   inventoryManager->GetInventoryItemCount(itemToGather.ItemId,
                       minCollectability: (short)itemToGather.Collectability) >=
                   itemToGather.ItemCount;
        }
    }

    internal sealed record GatheringTask
    (
        GatheringPointId GatheringPointId,
        GatheredItem GatheredItem) : ITask
    {
        public override string ToString()
        {
            if (GatheredItem.Collectability == 0)
            {
                return $"Gather({GatheredItem.ItemCount}x {GatheredItem.ItemId})";
            }
            else
            {
                return
                    $"Gather({GatheredItem.ItemCount}x {GatheredItem.ItemId} {SeIconChar.Collectible.ToIconString()} {GatheredItem.Collectability})";
            }
        }
    }

    internal sealed class StartGathering(GatheringController gatheringController) : TaskExecutor<GatheringTask>,
        IToastAware
    {
        public override ETaskResult Update()
        {
            if (gatheringController.Update() == GatheringController.EStatus.Complete)
            {
                return ETaskResult.TaskComplete;
            }

            return ETaskResult.StillRunning;
        }

        public bool OnErrorToast(SeString message)
        {
            bool isHandled = false;
            gatheringController.OnErrorToast(ref message, ref isHandled);
            return isHandled;
        }

        // we're on a gathering class, so combat doesn't make much sense (we also can't change classes in combat...)
        public override bool ShouldInterruptOnDamage()
        {
            return false;
        }
        protected override bool Start()
        {
            return gatheringController.Start(new(Task.GatheringPointId,
                Task.GatheredItem.ItemId, Task.GatheredItem.AlternativeItemId, Task.GatheredItem.ItemCount,
                Task.GatheredItem.Collectability));
        }
    }

    /// <summary>
    ///     A task that does nothing, but if we're skipping a step, this will be the task next in queue to be executed (instead
    ///     of progressing to the next step) if gathering.
    /// </summary>
    internal sealed class SkipMarker : ITask
    {
        public override string ToString()
        {
            return "Gather/SkipMarker";
        }
    }

    internal sealed class DoSkip : TaskExecutor<SkipMarker>
    {
        protected override bool Start()
        {
            return true;
        }
        public override ETaskResult Update()
        {
            return ETaskResult.TaskComplete;
        }

        public override bool ShouldInterruptOnDamage()
        {
            return false;
        }
    }
}

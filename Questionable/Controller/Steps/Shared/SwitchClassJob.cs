using ECommons.ExcelServices;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Questionable.Controller.Steps.Common;
using Questionable.Data;
using Questionable.Model;
using Questionable.Model.Questing;
using System.Linq;
namespace Questionable.Controller.Steps.Shared;

internal static class SwitchClassJob
{
    internal sealed class Factory(ClassJobUtils classJobUtils) : SimpleTaskFactory
    {
        public override ITask? CreateTask(Quest quest, QuestSequence sequence, QuestStep step)
        {
            if (step.InteractionType != EInteractionType.SwitchClass)
            {
                return null;
            }

            Job classJob = classJobUtils.AsIndividualJobs(step.TargetClass, quest.Id).Single();
            return new Task(classJob);
        }
    }

    internal sealed record Task(Job ClassJob) : ITask
    {
        /// <summary>
        /// 不是 null 時，找不到該職業的套組改擲 <see cref="ManualActionRequiredException"/> 並把這句印到聊天視窗；
        /// null 維持原本的 <see cref="TaskException"/>。
        /// </summary>
        public string? ManualActionChatMessage { get; init; }

        public override string ToString()
        {
            return $"SwitchJob({ClassJob})";
        }
    }

    internal sealed class SwitchClassJobExecutor : AbstractDelayedTaskExecutor<Task>
    {
        protected override unsafe bool StartInternal()
        {
            if (PlayerState.Instance()->CurrentClassJobId == (uint)Task.ClassJob)
            {
                return false;
            }

            RaptureGearsetModule* gearsetModule = RaptureGearsetModule.Instance();
            if (gearsetModule != null)
            {
                for(int i = 0; i < 100; ++i)
                {
                    RaptureGearsetModule.GearsetEntry* gearset = gearsetModule->GetGearset(i);
                    if (gearset != null && gearset->ClassJob == (byte)Task.ClassJob)
                    {
                        gearsetModule->EquipGearset(gearset->Id);
                        return true;
                    }
                }
            }

            throw CreateNoGearsetException();
        }

        private TaskException CreateNoGearsetException()
        {
            string message = $"No gearset found for {Task.ClassJob}";
            return Task.ManualActionChatMessage is { } chatMessage
                ? new ManualActionRequiredException(message, chatMessage)
                : new TaskException(message);
        }

        protected override ETaskResult UpdateInternal()
        {
            return ETaskResult.TaskComplete;
        }

        // can we even take damage while switching jobs? we should be out of combat...
        public override bool ShouldInterruptOnDamage()
        {
            return false;
        }
    }
}

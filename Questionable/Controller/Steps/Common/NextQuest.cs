using Microsoft.Extensions.Logging;
using Questionable.Data;
using Questionable.Functions;
using Questionable.Model;
using Questionable.Model.Questing;
namespace Questionable.Controller.Steps.Common;

internal static class NextQuest
{
    internal sealed class Factory(QuestFunctions questFunctions) : SimpleTaskFactory
    {
        public override ITask? CreateTask(Quest quest, QuestSequence sequence, QuestStep step)
        {
            if (step.InteractionType != EInteractionType.CompleteQuest)
            {
                return null;
            }

            if (step.NextQuestId == null)
            {
                return null;
            }

            if (step.NextQuestId == quest.Id)
            {
                return null;
            }

            // probably irrelevant, since pick up is handled elsewhere (and, in particular, checks for aetherytes and stuff)
            if (questFunctions.GetPriorityQuests(onlyClassAndRoleQuests: true).Contains(step.NextQuestId))
            {
                return null;
            }

            return new SetQuestTask(step.NextQuestId, quest.Id);
        }
    }

    internal sealed record SetQuestTask(ElementId NextQuestId, ElementId CurrentQuestId) : ITask
    {
        public bool ShouldRedoOnInterrupt()
        {
            return true;
        }
        public override string ToString()
        {
            return $"SetNextQuest({NextQuestId})";
        }
    }

    internal sealed class NextQuestExecutor
    (
        QuestRegistry questRegistry,
        QuestController questController,
        QuestFunctions questFunctions,
        QuestData questData,
        ILogger<NextQuestExecutor> logger) : TaskExecutor<SetQuestTask>
    {
        protected override bool Start()
        {
            if (questController.AutomationType is QuestController.EAutomationType.SingleQuestA or QuestController.EAutomationType.SingleQuestB)
            {
                logger.LogInformation("Won't set next quest to {QuestId}, automation type is CurrentQuestOnly", Task.NextQuestId);
                questController.SetNextQuest(null);
            }
            else if (questData.IsMissingFromSheet(Task.NextQuestId))
            {
                // 與下面「找不到」同一個結果：不設下一個任務，這個工作照常完成、後面的交任務照跑。
                logger.LogInformation(
                    "下一個任務 {QuestId} 不在本客戶端的任務表裡（台服尚未開放的後續任務），任務 {CurrentQuestId} 交完後不接續",
                    Task.NextQuestId, Task.CurrentQuestId);
                questController.SetNextQuest(null);
            }
            else if (questFunctions.IsQuestLocked(Task.NextQuestId, Task.CurrentQuestId))
            {
                logger.LogInformation("Can't set next quest to {QuestId}, quest is locked", Task.NextQuestId);
                questController.SetNextQuest(null);
            }
            else if (questRegistry.TryGetQuest(Task.NextQuestId, out Quest? quest))
            {
                logger.LogInformation("Setting next quest to {QuestId}: '{QuestName}'", Task.NextQuestId, quest.Info.Name);
                questController.SetNextQuest(quest);
            }
            else
            {
                logger.LogInformation("Next quest with id {QuestId} not found", Task.NextQuestId);
                questController.SetNextQuest(null);
            }

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

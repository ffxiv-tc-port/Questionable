using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using Questionable.Data;
using Questionable.External;
using Questionable.Model;
using Questionable.Model.Questing;
namespace Questionable.Controller.Steps.Common;

internal static class SendNotification
{
    internal sealed class Factory
    (
        AutomatonIpc automatonIpc,
        AutoDutyIpc autoDutyIpc,
        BossModIpc bossModIpc,
        TerritoryData territoryData) : SimpleTaskFactory
    {
        public override ITask? CreateTask(Quest quest, QuestSequence sequence, QuestStep step)
        {
            return step.InteractionType switch
            {
                EInteractionType.Snipe when !automatonIpc.IsAutoSnipeEnabled =>
                    new(step.InteractionType, step.Comment),
                EInteractionType.Duty when !autoDutyIpc.IsConfiguredToRunContent(step.DutyOptions) =>
                    new(step.InteractionType, step.DutyOptions?.ContentFinderConditionId is { } contentFinderConditionId
                        ? territoryData.GetContentFinderCondition(contentFinderConditionId)?.Name
                        : step.Comment),
                EInteractionType.SinglePlayerDuty when !bossModIpc.IsConfiguredToRunSoloInstance(quest.Id, step.SinglePlayerDutyOptions) =>
                    new Task(step.InteractionType, quest.Info.Name),
                var _ => null
            };
        }
    }

    internal sealed record Task(EInteractionType InteractionType, string? Comment) : ITask
    {
        public override string ToString()
        {
            return "SendNotification";
        }
    }

    internal sealed class Executor
    (
        IChatGui chatGui,
        Configuration configuration,
        TataruPraiseIpc tataruPraiseIpc) : TaskExecutor<Task>
    {
        protected override bool Start()
        {
            // 🔴 塔塔露的語音提醒刻意放在 Notifications.Enabled 前面，跟聊天訊息各走各的開關：
            //    這個任務本身就是「流程走到需要玩家親自處理的步驟」，語音要不要出聲由
            //    Notifications.PraiseWithTataru 自己決定。
            // 📌 一個 SendNotification 任務只會 Start 一次，所以這裡天然就是狀態邊緣，不需要去重。
            tataruPraiseIpc.NotifyNeedHelp($"需要手動處理的步驟：{Task.InteractionType}");

            if (!configuration.Notifications.Enabled)
            {
                return false;
            }

            string text = Task.InteractionType switch
            {
                EInteractionType.Duty => "Duty",
                EInteractionType.SinglePlayerDuty => "Single player duty",
                EInteractionType.Instruction or EInteractionType.WaitForManualProgress or EInteractionType.Snipe =>
                    "Manual interaction required",
                var _ => $"{Task.InteractionType}"
            };

            if (!string.IsNullOrEmpty(Task.Comment))
            {
                text += $" - {Task.Comment}";
            }

            if (configuration.Notifications.ChatType != XivChatType.None)
            {
                XivChatEntry message = configuration.Notifications.ChatType switch
                {
                    XivChatType.Say
                        or XivChatType.Shout
                        or XivChatType.TellOutgoing
                        or XivChatType.TellIncoming
                        or XivChatType.Party
                        or XivChatType.Alliance
                        or >= XivChatType.Ls1 and <= XivChatType.Ls8
                        or XivChatType.FreeCompany
                        or XivChatType.NoviceNetwork
                        or XivChatType.Yell
                        or XivChatType.CrossParty
                        or XivChatType.PvPTeam
                        or XivChatType.CrossLinkShell1
                        or XivChatType.NPCDialogue
                        or XivChatType.NPCDialogueAnnouncements
                        or >= XivChatType.CrossLinkShell2 and <= XivChatType.CrossLinkShell8
                        => new()
                        {
                            Message = text,
                            Type = configuration.Notifications.ChatType,
                            Name = new SeStringBuilder()
                                .AddUiForeground(CommandHandler.MessageTag, CommandHandler.TagColor)
                                .Build()
                        },
                    var _ => new()
                    {
                        Message = new SeStringBuilder()
                            .AddUiForeground($"[{CommandHandler.MessageTag}] ", CommandHandler.TagColor)
                            .Append(text)
                            .Build(),
                        Type = configuration.Notifications.ChatType
                    }
                };
                chatGui.Print(message);
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

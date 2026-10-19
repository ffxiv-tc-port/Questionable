using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
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
            switch (step.InteractionType)
            {
                case EInteractionType.Snipe when !automatonIpc.IsAutoSnipeEnabled:
                    return new Task(step.InteractionType, step.Comment);

                case EInteractionType.Duty
                    when !autoDutyIpc.IsConfiguredToRunContent(step.DutyOptions, out EDutyBlockReason dutyReason):
                {
                    string? name = step.DutyOptions?.ContentFinderConditionId is { } contentFinderConditionId
                        ? territoryData.GetContentFinderCondition(contentFinderConditionId)?.Name
                        : step.Comment;

                    // 🔴 只有「預設沒開」才給一鍵啟用。白名單在 AutoDutyIpc 裡會蓋過 HasPath 檢查，
                    //    對一張 AutoDuty 沒有路徑的圖按下去，等於叫它去跑一張跑不了的副本。
                    uint enableableCfcId =
                        dutyReason == EDutyBlockReason.NotEnabledByDefault &&
                        step.DutyOptions is { } dutyOptions &&
                        autoDutyIpc.HasPath(dutyOptions.ContentFinderConditionId)
                            ? dutyOptions.ContentFinderConditionId
                            : 0;
                    return new Task(step.InteractionType, name, dutyReason, enableableCfcId);
                }

                case EInteractionType.SinglePlayerDuty
                    when !bossModIpc.IsConfiguredToRunSoloInstance(quest.Id, step.SinglePlayerDutyOptions,
                        out EDutyBlockReason soloReason, out uint soloCfcId):
                    return new Task(step.InteractionType, quest.Info.Name, soloReason,
                        soloReason == EDutyBlockReason.NotEnabledByDefault ? soloCfcId : 0);

                default:
                    return null;
            }
        }
    }

    /// <param name="BlockReason">被哪一條擋的；<see cref="EDutyBlockReason.None"/> ＝ 這一步本來就要手動。</param>
    /// <param name="EnableableCfcId">可以一鍵啟用的 CFC id；<c>0</c> ＝ 這個原因沒有一鍵可按。</param>
    internal sealed record Task(
        EInteractionType InteractionType,
        string? Comment,
        EDutyBlockReason BlockReason = EDutyBlockReason.None,
        uint EnableableCfcId = 0) : ITask
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
        TataruPraiseIpc tataruPraiseIpc,
        EnableDutyLinkHandler enableDutyLinkHandler) : TaskExecutor<Task>
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
                EInteractionType.Duty => "需要你自己跑的副本",
                EInteractionType.SinglePlayerDuty => "需要你自己打的任務戰鬥",
                EInteractionType.Instruction or EInteractionType.WaitForManualProgress or EInteractionType.Snipe =>
                    "需要手動操作",
                var _ => $"{Task.InteractionType}"
            };

            if (!string.IsNullOrEmpty(Task.Comment))
            {
                text += $"：{Task.Comment}";
            }

            if (DescribeBlockReason(Task.InteractionType, Task.BlockReason) is { } blockReason)
            {
                text += $"（{blockReason}）";
            }

            SeString BuildMessage(bool withTag)
            {
                SeStringBuilder builder = new();
                if (withTag)
                {
                    builder.AddUiForeground($"[{CommandHandler.MessageTag}] ", CommandHandler.TagColor);
                }

                builder.Append(text);

                if (Task.EnableableCfcId != 0)
                {
                    builder.AddUiForeground(500)
                        .AddText("  [ ")
                        .Add(enableDutyLinkHandler.GetPayload(Task.EnableableCfcId,
                            Task.InteractionType == EInteractionType.SinglePlayerDuty))
                        .AddText("點這裡啟用這一場")
                        .Add(RawPayload.LinkTerminator)
                        .AddText(" ]")
                        .AddUiForegroundOff();
                }

                return builder.Build();
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
                            Message = BuildMessage(false),
                            Type = configuration.Notifications.ChatType,
                            Name = new SeStringBuilder()
                                .AddUiForeground(CommandHandler.MessageTag, CommandHandler.TagColor)
                                .Build()
                        },
                    var _ => new()
                    {
                        Message = BuildMessage(true),
                        Type = configuration.Notifications.ChatType
                    }
                };
                chatGui.Print(message);
            }

            return true;
        }

        /// <summary>給使用者看的「為什麼沒有自動打」；<c>null</c> ＝ 這一步本來就要手動，沒有原因可說。</summary>
        private static string? DescribeBlockReason(EInteractionType interactionType, EDutyBlockReason reason)
        {
            bool singlePlayerDuty = interactionType == EInteractionType.SinglePlayerDuty;
            return reason switch
            {
                EDutyBlockReason.PluginUnavailable => singlePlayerDuty
                    ? "叫不到 BossMod，可能沒安裝或版本不合"
                    : "叫不到 AutoDuty，可能沒安裝或版本不合",
                EDutyBlockReason.DisabledInConfig => singlePlayerDuty
                    ? "設定裡的「使用 BossMod 執行任務戰鬥」沒有開"
                    : "設定裡的「使用 AutoDuty 與 BossMod 執行副本內容」沒有開",
                EDutyBlockReason.NoDutyData => "Questionable 沒有這一場的資料",
                EDutyBlockReason.Blacklisted => "你把這一場設成停用了",
                EDutyBlockReason.NotEnabledByDefault => "這一場預設沒有開啟",
                EDutyBlockReason.NoPath => "AutoDuty 沒有這張地圖的路徑",
                var _ => null
            };
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

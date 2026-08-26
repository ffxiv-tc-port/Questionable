using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using ECommons.LanguageHelpers;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Questionable.Controller;
using Questionable.Data;
using Questionable.Functions;
using Questionable.Model;
using Questionable.Model.Questing;
using System.Numerics;
namespace Questionable.Windows.QuestComponents;

internal sealed class QuestTooltipComponent
(
    QuestRegistry questRegistry,
    QuestData questData,
    TerritoryData territoryData,
    QuestFunctions questFunctions,
    UiUtils uiUtils,
    Configuration configuration)
{
    private readonly Configuration _configuration = configuration;
    private readonly QuestData _questData = questData;
    private readonly QuestFunctions _questFunctions = questFunctions;
    private readonly QuestRegistry _questRegistry = questRegistry;
    private readonly TerritoryData _territoryData = territoryData;
    private readonly UiUtils _uiUtils = uiUtils;

    public void Draw(IQuestInfo questInfo)
    {
        using var tooltip = ImRaii.Tooltip();
        DrawInner(questInfo, true);
    }

    public void DrawInner(IQuestInfo questInfo, bool showItemRewards)
    {
        ImGui.Text($"{SeIconChar.LevelEn.ToIconString()}{questInfo.Level}");
        ImGui.SameLine();

        (Vector4 color, FontAwesomeIcon _, string tooltipText) = _uiUtils.GetQuestStyle(questInfo.QuestId);
        ImGui.TextColored(color, tooltipText);

        if (questInfo is QuestInfo { IsSeasonalEvent: true })
        {
            ImGui.SameLine();
            ImGui.TextUnformatted("Event".Loc());
        }

        if (questInfo.IsRepeatable)
        {
            ImGui.SameLine();
            ImGui.TextUnformatted("Repeatable".Loc());
        }

        if (questInfo is QuestInfo { CompletesInstantly: true })
        {
            ImGui.SameLine();
            ImGui.TextUnformatted("Instant".Loc());
        }

        if (_questRegistry.TryGetQuest(questInfo.QuestId, out Quest? quest))
        {
            if (quest.Root.Disabled)
            {
                ImGui.SameLine();
                ImGui.TextColored(ImGuiColors.DalamudRed, "Disabled".Loc());
            }

            if (quest.Root.Author.Count == 1)
            {
                ImGui.Text($"{"Author".Loc()}: {quest.Root.Author[0]}");
            }
            else
            {
                ImGui.Text($"{"Authors".Loc()}: {string.Join(", ", quest.Root.Author)}");
            }

            if (quest.Root.Comment != null)
            {
                ImGui.Text($"{"Comment".Loc()}: {quest.Root.Comment.Split('\n', 2)[0]}");
            }

            if (quest.Root.LastChecked.Date != null)
            {
                ImGui.Text($"{"Last checked".Loc()}: {quest.Root.LastChecked.Date} {"by".Loc()} {quest.Root.LastChecked.Username}");
            }

            if (questInfo.AlliedSociety != EAlliedSociety.None)
            {
                ImGui.Text($"{"Society".Loc()}: {questInfo.AlliedSociety}");
            }
        }
        else
        {
            ImGui.SameLine();
            ImGui.TextColored(ImGuiColors.DalamudRed, "NoQuestPath".Loc());
        }

        DrawQuestUnlocks(questInfo, 0, showItemRewards);
    }

    private void DrawQuestUnlocks(IQuestInfo questInfo, int counter, bool showItemRewards)
    {
        if (counter >= 10)
        {
            return;
        }

        if (counter != 0 && questInfo.IsMainScenarioQuest)
        {
            return;
        }

        if (counter > 0)
        {
            ImGui.Indent();
        }

        if (questInfo.PreviousQuests.Count > 0)
        {
            if (counter == 0)
            {
                ImGui.Separator();
            }

            if (questInfo.PreviousQuests.Count > 1)
            {
                if (questInfo.PreviousQuestJoin == EQuestJoin.All)
                {
                    ImGui.Text("Requires all:".Loc());
                }
                else if (questInfo.PreviousQuestJoin == EQuestJoin.AtLeastOne)
                {
                    ImGui.Text("Requires one:".Loc());
                }
            }

            foreach(PreviousQuestInfo q in questInfo.PreviousQuests)
            {
                if (_questData.TryGetQuestInfo(q.QuestId, out IQuestInfo? qInfo))
                {
                    (Vector4 iconColor, FontAwesomeIcon icon, string _) = _uiUtils.GetQuestStyle(q.QuestId);
                    if (!_questRegistry.IsKnownQuest(qInfo.QuestId))
                    {
                        iconColor = ImGuiColors.DalamudGrey;
                    }

                    _uiUtils.ChecklistItem(
                        FormatQuestUnlockName(qInfo,
                            _questFunctions.IsQuestComplete(q.QuestId) ? byte.MinValue : q.Sequence), iconColor, icon);

                    if (qInfo is QuestInfo qstInfo && (counter <= 2 || icon != FontAwesomeIcon.Check))
                    {
                        DrawQuestUnlocks(qstInfo, counter + 1, false);
                    }
                }
                else
                {
                    using var _ = ImRaii.Disabled();
                    _uiUtils.ChecklistItem($"{"Unknown Quest".Loc()} ({q.QuestId})", ImGuiColors.DalamudGrey,
                        FontAwesomeIcon.Question);
                }
            }
        }

        if (questInfo is QuestInfo actualQuestInfo)
        {
            if (actualQuestInfo.MoogleDeliveryLevel > 0)
            {
                ImGui.Text($"{"Requires Carrier Level".Loc()} {actualQuestInfo.MoogleDeliveryLevel}");
            }


            if (counter == 0 && actualQuestInfo.QuestLocks.Count > 0)
            {
                ImGui.Separator();
                if (actualQuestInfo.QuestLocks.Count > 1)
                {
                    if (actualQuestInfo.QuestLockJoin == EQuestJoin.All)
                    {
                        ImGui.Text("Blocked by (if all completed):".Loc());
                    }
                    else if (actualQuestInfo.QuestLockJoin == EQuestJoin.AtLeastOne)
                    {
                        ImGui.Text("Blocked by (if at least completed):".Loc());
                    }
                }
                else
                {
                    ImGui.Text("Blocked by (if completed):".Loc());
                }

                foreach(QuestId q in actualQuestInfo.QuestLocks)
                {
                    IQuestInfo qInfo = _questData.GetQuestInfo(q);
                    (Vector4 iconColor, FontAwesomeIcon icon, string _) = _uiUtils.GetQuestStyle(q);
                    if (!_questRegistry.IsKnownQuest(qInfo.QuestId))
                    {
                        iconColor = ImGuiColors.DalamudGrey;
                    }

                    _uiUtils.ChecklistItem(FormatQuestUnlockName(qInfo), iconColor, icon);
                }
            }

            if (counter == 0 && actualQuestInfo.PreviousInstanceContent.Count > 0)
            {
                ImGui.Separator();
                if (actualQuestInfo.PreviousInstanceContent.Count > 1)
                {
                    if (questInfo.PreviousQuestJoin == EQuestJoin.All)
                    {
                        ImGui.Text("Requires all:".Loc());
                    }
                    else if (questInfo.PreviousQuestJoin == EQuestJoin.AtLeastOne)
                    {
                        ImGui.Text("Requires one:".Loc());
                    }
                }
                else
                {
                    ImGui.Text("Requires:".Loc());
                }

                foreach(ushort instanceId in actualQuestInfo.PreviousInstanceContent)
                {
                    string instanceName = _territoryData.GetInstanceName(instanceId) ?? "?";
                    (Vector4 iconColor, FontAwesomeIcon icon) = UiUtils.GetInstanceStyle(instanceId);
                    _uiUtils.ChecklistItem(instanceName, iconColor, icon);
                }
            }

            if (counter == 0 && actualQuestInfo.GrandCompany != GrandCompany.None)
            {
                ImGui.Separator();
                string gcName = actualQuestInfo.GrandCompany switch
                {
                    GrandCompany.Maelstrom => "Maelstrom".Loc(),
                    GrandCompany.TwinAdder => "Twin Adder".Loc(),
                    GrandCompany.ImmortalFlames => "Immortal Flames".Loc(),
                    var _ => "None".Loc()
                };

                GrandCompany currentGrandCompany = _questFunctions.GetGrandCompany();
                _uiUtils.ChecklistItem($"{"Grand Company".Loc()}: {gcName}", actualQuestInfo.GrandCompany == currentGrandCompany);
            }

            if (showItemRewards && actualQuestInfo.ItemRewards.Count > 0)
            {
                ImGui.Separator();
                ImGui.Text("Item Rewards:".Loc());
                foreach(ItemReward reward in actualQuestInfo.ItemRewards)
                {
                    ImGui.BulletText(reward.Name);
                }
            }
        }

        if (counter > 0)
        {
            ImGui.Unindent();
        }
    }

    private string FormatQuestUnlockName(IQuestInfo questInfo, byte sequence = 0)
    {
        string name = questInfo.Name;
        if (_configuration.Advanced.AdditionalStatusInformation && sequence != 0)
        {
            name += $" {SeIconChar.ItemLevel.ToIconString()}";
        }

        if (questInfo.IsMainScenarioQuest)
        {
            name += $" ({questInfo.QuestId}, {"MSQ".Loc()})";
        }
        else
        {
            name += $" ({questInfo.QuestId})";
        }

        return name;
    }
}

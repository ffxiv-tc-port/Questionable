using ImGuiNET;
using Dalamud.Game.Text;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using ECommons.LanguageHelpers;
using Questionable.Controller;
using Questionable.Data;
using Questionable.Model;
using Questionable.Windows.QuestComponents;
using System;
using System.Linq;
using System.Numerics;
namespace Questionable.Windows.JournalComponents;

internal sealed class QuestRewardComponent
(
    QuestRegistry questRegistry,
    QuestData questData,
    QuestTooltipComponent questTooltipComponent,
    UiUtils uiUtils)
{
    private readonly QuestData _questData = questData;
    private readonly QuestRegistry _questRegistry = questRegistry;
    private readonly QuestTooltipComponent _questTooltipComponent = questTooltipComponent;
    private readonly UiUtils _uiUtils = uiUtils;

    private bool _showEventRewards;

    public void DrawItemRewards()
    {
        using var tab = ImRaii.TabItem("Item Rewards".Loc());
        if (!tab)
        {
            return;
        }

        ImGui.Checkbox("Show rewards from seasonal event quests".Loc(), ref _showEventRewards);
        ImGui.Spacing();

        ImGui.BulletText(
            "Only untradeable items are listed (e.g. the Wind-up Airship can be sold on the market board).".Loc());

        DrawGroup("Mounts".Loc(), EItemRewardType.Mount);
        DrawGroup("Minions".Loc(), EItemRewardType.Minion);
        DrawGroup("Orchestrion Rolls".Loc(), EItemRewardType.OrchestrionRoll);
        DrawGroup("Triple Triad Cards".Loc(), EItemRewardType.TripleTriadCard);
        DrawGroup("Fashion Accessories".Loc(), EItemRewardType.FashionAccessory);
    }

    private void DrawGroup(string label, EItemRewardType type)
    {
        if (!ImGui.CollapsingHeader($"{label}###Reward{type}"))
        {
            return;
        }

        foreach(ItemReward item in _questData.RedeemableItems.Where(x => x.Type == type)
            .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            if (_questData.TryGetQuestInfo(item.ElementId, out IQuestInfo? questInfo))
            {
                bool isEventQuest = questInfo is QuestInfo { IsSeasonalEvent: true };
                if (!_showEventRewards && isEventQuest)
                {
                    continue;
                }

                string name = item.Name;
                if (isEventQuest)
                {
                    name += $" {SeIconChar.Clock.ToIconString()}";
                }

                bool complete = item.IsUnlocked();
                Vector4 color = !_questRegistry.IsKnownQuest(item.ElementId)
                    ? ImGuiColors.DalamudGrey
                    : complete
                        ? ImGuiColors.ParsedGreen
                        : ImGuiColors.DalamudRed;
                FontAwesomeIcon icon = complete ? FontAwesomeIcon.Check : FontAwesomeIcon.Times;
                if (_uiUtils.ChecklistItem(name, color, icon))
                {
                    using var tooltip = ImRaii.Tooltip();
                    ImGui.Text($"{"Obtained from".Loc()}: {questInfo.Name}");
                    using (ImRaii.PushIndent())
                    {
                        _questTooltipComponent.DrawInner(questInfo, false);
                    }
                }
            }
        }
    }
}

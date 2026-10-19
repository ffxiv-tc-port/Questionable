using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin;
using ECommons.LanguageHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using Dalamud.Plugin.Services;
using BeastTribe = Lumina.Excel.Sheets.BeastTribe;
using Questionable.Controller;
using Questionable.Data;
using Questionable.Functions;
using Questionable.Model;
using Questionable.Windows.QuestComponents;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
namespace Questionable.Windows.JournalComponents;

internal sealed class AlliedSocietyJournalComponent
(
    QuestFunctions questFunctions,
    QuestController questController,
    AlliedSocietyQuestFunctions alliedSocietyQuestFunctions,
    QuestData questData,
    QuestRegistry questRegistry,
    QuestJournalUtils questJournalUtils,
    QuestTooltipComponent questTooltipComponent,
    UiUtils uiUtils,
    Configuration configuration,
    IDalamudPluginInterface pluginInterface,
    IDataManager dataManager)
{
    private static readonly EAlliedSociety[] AlliedSocieties =
        Enum.GetValues<EAlliedSociety>().Where(x => x != EAlliedSociety.None).ToArray();
    private readonly SocietyRow[] _rows = AlliedSocieties.Select(x => new SocietyRow(x, GetTribeNames(dataManager, x))).ToArray();
    private static readonly string[] RankNames =
        ["Neutral", "Recognized", "Friendly", "Trusted", "Respected", "Honored", "Sworn", "Allied"];
    private readonly AlliedSocietyQuestFunctions _alliedSocietyQuestFunctions = alliedSocietyQuestFunctions;
    private readonly QuestController _questController = questController;
    private readonly QuestData _questData = questData;

    private readonly QuestFunctions _questFunctions = questFunctions;
    private readonly QuestJournalUtils _questJournalUtils = questJournalUtils;
    private readonly QuestRegistry _questRegistry = questRegistry;
    private readonly QuestTooltipComponent _questTooltipComponent = questTooltipComponent;
    private readonly UiUtils _uiUtils = uiUtils;
    private readonly Configuration _configuration = configuration;
    private readonly IDalamudPluginInterface _pluginInterface = pluginInterface;

    public void DrawAlliedSocietyQuests()
    {
        using var tab = ImRaii.TabItem("Allied Societies".Loc());
        if (!tab)
        {
            return;
        }
        bool addPending = false;
#if DEBUG
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Plus, "Add".Loc()))
        {
            addPending = true;
        }
        ImGui.SameLine();
#endif

        unsafe
        {
            uint allowances = QuestManager.Instance()->GetBeastTribeAllowance();
            ImGui.Text($"{"Remaining".Loc()}: {allowances}/12");
        }

        ImGui.SameLine();
        ImGuiComponents.HelpMarker(
            "Tick the box in front of an allied society: pressing Start will then accept that society's daily quests for today and complete them in order (within the remaining allowance).".Loc());

        bool optimizeRoute = _configuration.General.OptimizeAlliedSocietyDailyRoute;
        if (ImGui.Checkbox("Optimize route for multiple daily quests".Loc(), ref optimizeRoute))
        {
            _configuration.General.OptimizeAlliedSocietyDailyRoute = optimizeRoute;
            _pluginInterface.SavePluginConfig(_configuration);
        }

        ImGui.SameLine();
        ImGuiComponents.HelpMarker(
            "After accepting, go to the nearest objective first instead of following quest order; when only the turn-in is left and other quests for the same quest giver still have objectives, do those first and turn everything in together at the end. Untick to use the fixed quest order.".Loc());

        foreach(SocietyRow row in _rows)
        {
            EAlliedSociety alliedSociety = row.Society;
            int availabilityKey = _alliedSocietyQuestFunctions.GetAvailabilityKey(alliedSociety);
            if (availabilityKey != row.AvailabilityKey)
            {
                row.Quests = _alliedSocietyQuestFunctions.GetAvailableAlliedSocietyQuests(alliedSociety)
                    .Select(x => _questData.GetQuestInfo(x))
                    .ToList();
                row.AvailabilityKey = availabilityKey;
            }
            List<IQuestInfo> quests = row.Quests;
            //if (quests.Count == 0)
            //    continue;

            DrawAutoQueueCheckbox(row);

            string label = row.Label;
            bool isOpen;

            using (ImRaii.Disabled(quests.Count == 0))
            {
#if DEBUG
                if (quests.Any(x => !x.QuestId.Value.Equals(1569) && ( // Ixal "Deliverance"
                        !_questRegistry.TryGetQuest(x.QuestId, out Quest? quest) ||
                        (quest.Root.Disabled && !(quest.Root.Comment ?? "").Contains("FATE")) ||
                        (
                            (quest.Root.LastChecked.Date != null && quest.Root.LastChecked.Since(DateTime.Now)!.Value.TotalDays > 90) ||
                            (quest.Root.LastChecked.Date == null && !(quest.Root.Comment ?? "").Contains("FATE"))
                        )
                    )))
                {
                    using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudOrange))
                    {
                        isOpen = ImGui.CollapsingHeader(label);
                    }
                }
                else
#endif
                if (HasIncompleteQuest(quests))
                {
                    using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudYellow))
                    {
                        isOpen = ImGui.CollapsingHeader(label);
                    }
                }
                else
                {
                    isOpen = ImGui.CollapsingHeader(label);
                }
            }

            if (row.FullName != row.ShortName && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.SetTooltip(row.FullName);
            }

            _questJournalUtils.ShowQuestGroupContextMenu(row.MenuNote, quests);

            if (!isOpen)
            {
                continue;
            }

            if (alliedSociety <= EAlliedSociety.Ixal)
            {
                for(byte i = 1; i <= 8; ++i)
                {
                    List<IQuestInfo> questsByRank = quests.Where(quest => ((QuestInfo)quest).AlliedSocietyRank == i && !quest.QuestId.Value.Equals(1569)).ToList();
                    if (questsByRank.Count == 0)
                    {
                        continue;
                    }

                    ImGui.Text(RankNames[i - 1].Loc());
                    _questJournalUtils.ShowQuestGroupContextMenu($"DrawAlliedSocietyQuests{alliedSociety}/{RankNames[i - 1]}", questsByRank);
                    foreach(IQuestInfo quest in questsByRank)
                    {
                        DrawQuest((QuestInfo)quest, addPending);
                    }
                }
            }
            else
            {
                foreach(IQuestInfo quest in quests)
                {
                    DrawQuest((QuestInfo)quest, addPending);
                }
            }
        }
    }

    // 列舉值 1..19 與 BeastTribe 列號一致；表中沒有該列或名稱為空時退回列舉名。
    private static (string Short, string Full) GetTribeNames(IDataManager dataManager, EAlliedSociety society)
    {
        string fallback = society.ToString();
        try
        {
            if (dataManager.GetExcelSheet<BeastTribe>().TryGetRow((uint)society, out BeastTribe row))
            {
                string full = row.Name.ExtractText();
                if (!string.IsNullOrWhiteSpace(full))
                {
                    int sep = full.IndexOf('：');
                    string shortName = sep > 0 ? full[..sep] : full;
                    return (shortName, full);
                }
            }
        }
        catch (Exception)
        {
        }

        return (fallback, fallback);
    }

    private void DrawAutoQueueCheckbox(SocietyRow row)
    {
        HashSet<EAlliedSociety> autoQueue = _configuration.General.AutoQueueAlliedSocietyDailies;
        bool enabled = autoQueue.Contains(row.Society);
        if (ImGui.Checkbox(row.AutoQueueId, ref enabled))
        {
            if (enabled)
            {
                autoQueue.Add(row.Society);
            }
            else
            {
                autoQueue.Remove(row.Society);
            }

            _pluginInterface.SavePluginConfig(_configuration);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Automatically accept today's daily quests for this allied society when pressing Start".Loc());
        }

        ImGui.SameLine();
    }

    private bool HasIncompleteQuest(List<IQuestInfo> quests)
    {
        foreach(IQuestInfo quest in quests)
        {
            if (!_questFunctions.IsQuestComplete(quest.QuestId))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>一個部族的標題字串與「可接任務」快取；<see cref="Quests"/> 只在 <see cref="AvailabilityKey"/> 變了才重算。</summary>
    private sealed class SocietyRow(EAlliedSociety society, (string Short, string Full) names)
    {
        public EAlliedSociety Society { get; } = society;
        public string ShortName { get; } = names.Short;
        public string FullName { get; } = names.Full;
        public string Label { get; } = $"{names.Short}###AlliedSociety{(int)society}";
        public string MenuNote { get; } = $"DrawAlliedSocietyQuests{society}";
        public string AutoQueueId { get; } = $"##AutoQueueAlliedSociety{(int)society}";
        public int AvailabilityKey { get; set; } = -1;
        public List<IQuestInfo> Quests { get; set; } = [];
    }

    private void DrawQuest(QuestInfo questInfo, bool addPending = false)
    {
        (Vector4 color, FontAwesomeIcon icon, string tooltipText) = _uiUtils.GetQuestStyle(questInfo.QuestId);
        Quest? quest;
        bool fate = false;
        string lastChecked = "";
        if (!_questRegistry.TryGetQuest(questInfo.QuestId, out quest))
        {
            color = ImGuiColors.DalamudGrey;
        }
        else
        {
            if (quest.Root.LastChecked.Date != null)
            {
                lastChecked = $"({quest.Root.LastChecked.Date})";
#if DEBUG
                if (quest.Root.LastChecked.Since(DateTime.Now)!.Value.TotalDays > 90)
                {
                    color = ImGuiColors.DalamudRed;
                }
#endif
            }
#if DEBUG
            else
            {
                color = ImGuiColors.DPSRed;
            }
#endif
            if (quest.Root.Disabled && (quest.Root.Comment ?? "").Contains("FATE"))
            {
                color = ImGuiColors.DalamudOrange;
                fate = true;
            }
        }
        string checklistItem = $"{questInfo.Name} ({tooltipText}) {lastChecked}";
        if (fate)
        {
            checklistItem = "(FATE) " + checklistItem;
        }
        if (_uiUtils.ChecklistItem(checklistItem, color, icon))
        {
            _questTooltipComponent.Draw(questInfo);
        }
        if (addPending && (color.Equals(ImGuiColors.DalamudRed) || color.Equals(ImGuiColors.DPSRed)))
        {
            _questController.AddQuestPriority(questInfo.QuestId);
        }

        _questJournalUtils.ShowContextMenu(questInfo, quest, nameof(AlliedSocietyJournalComponent));
    }
}

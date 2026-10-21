using Dalamud.Game.Text;
using ECommons.ExcelServices;
using Questionable.Model.Questing;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
namespace Questionable.Model;

internal interface IQuestInfo
{
    public ElementId QuestId { get; }
    /// <summary>顯示用名稱（UI、log、任務檔名）；可能帶插件自加的區分後綴，不可拿去比對遊戲文字。</summary>
    public string Name { get; }

    /// <summary>遊戲畫面上的原文名稱，拿去跟遊戲選單文字比對時一律用這個。</summary>
    public string GameName { get; }

    public uint IssuerDataId { get; }
    public bool IsRepeatable { get; }
    public ImmutableList<PreviousQuestInfo> PreviousQuests { get; }
    public EQuestJoin PreviousQuestJoin { get; }
    public ushort Level { get; }
    public EAlliedSociety AlliedSociety { get; }
    public uint? JournalGenre { get; }
    public ushort SortKey { get; }
    public bool IsMainScenarioQuest { get; }
    public IReadOnlyList<Job> ClassJobs { get; }
    public EExpansionVersion Expansion { get; }

    public string SimplifiedName => Name
        .Replace(".", "", StringComparison.Ordinal)
        .Replace("*", "", StringComparison.Ordinal)
        .Replace("\"", "", StringComparison.Ordinal)
        .Replace("/", "", StringComparison.Ordinal)
        .Replace("\\", "", StringComparison.Ordinal)
        .Replace("<", "", StringComparison.Ordinal)
        .Replace(">", "", StringComparison.Ordinal)
        .Replace("|", "", StringComparison.Ordinal)
        .Replace(":", "", StringComparison.Ordinal)
        .Replace("?", "", StringComparison.Ordinal)
        .TrimStart(SeIconChar.QuestSync.ToIconChar(), SeIconChar.QuestRepeatable.ToIconChar(), ' ');
}

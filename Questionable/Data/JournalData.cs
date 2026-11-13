using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using Questionable.Model;
using Questionable.Model.Questing;
using System.Collections.Generic;
using System.Linq;
namespace Questionable.Data;

internal sealed class JournalData
{
    private readonly IPluginLog? _pluginLog;

    public JournalData(IDataManager dataManager, QuestData questData, IPluginLog? pluginLog = null)
    {
        _pluginLog = pluginLog;
        List<Genre> genres = dataManager.GetExcelSheet<JournalGenre>()
            .Where(x => x.RowId > 0 && x.Icon > 0)
            .Select(x => new Genre(x, questData.GetAllByJournalGenre(x.RowId)))
            .ToList();

        Genre genreLimsa = new(uint.MaxValue - 3, "Starting in Limsa Lominsa", 1,
            StartingCityQuests(dataManager, questData, 1, [108, 109]));
        Genre genreGridania = new(uint.MaxValue - 2, "Starting in Gridania", 1,
            StartingCityQuests(dataManager, questData, 2, [85, 123, 124]));
        Genre genreUldah = new(uint.MaxValue - 1, "Starting in Ul'dah", 1,
            StartingCityQuests(dataManager, questData, 3, [568, 569, 570]));
        genres.InsertRange(0, [genreLimsa, genreGridania, genreUldah]);
        if (genres.SingleOrDefault(x => x.Id == 1) is { } firstGenre)
        {
            firstGenre.Quests.RemoveAll(x =>
                genreLimsa.Quests.Contains(x) || genreGridania.Quests.Contains(x) || genreUldah.Quests.Contains(x));
        }
        else
        {
            _pluginLog?.Warning("任務日誌缺少 JournalGenre 第 1 列，起始任務不從原分類移除（遊戲資料可能已改版）");
        }

        Genres = genres.ToList();
        Categories = dataManager.GetExcelSheet<JournalCategory>()
            .Where(x => x.RowId > 0)
            .Select(x => new Category(x, Genres.Where(y => y.CategoryId == x.RowId).ToList()))
            .ToList();
        Sections = dataManager.GetExcelSheet<JournalSection>()
            .Select(x => new Section(x, Categories.Where(y => y.SectionId == x.RowId).ToList()))
            .ToList();
    }

    /// <summary>寫死的起始任務＋QuestRedo 該列列出的任務；缺列／不在任務資料裡的略過並記 Warning。</summary>
    private List<IQuestInfo> StartingCityQuests(IDataManager dataManager, QuestData questData, uint redoRow,
        uint[] fixedQuestIds)
    {
        IEnumerable<uint> rowIds = fixedQuestIds;
        if (dataManager.GetExcelSheet<QuestRedo>().GetRowOrDefault(redoRow) is { } questRedo)
        {
            rowIds = rowIds.Concat(questRedo.QuestRedoParam.Select(x => x.Quest.RowId));
        }
        else
        {
            _pluginLog?.Warning("任務日誌缺少 QuestRedo 第 {Row} 列，起始任務分類只列寫死的任務（遊戲資料可能已改版）", redoRow);
        }

        List<IQuestInfo> result = [];
        foreach(uint rowId in rowIds.Where(x => x != 0))
        {
            if (questData.TryGetQuestInfo(QuestId.FromRowId(rowId), out IQuestInfo? questInfo))
            {
                result.Add(questInfo);
            }
            else
            {
                _pluginLog?.Warning("任務日誌的起始任務 {RowId} 不在任務資料裡，略過（遊戲資料可能已改版）", rowId);
            }
        }

        return result;
    }

    public List<Genre> Genres { get; }
    public List<Category> Categories { get; }
    public List<Section> Sections { get; }

    internal sealed class Genre
    {
        public Genre(JournalGenre journalGenre, List<IQuestInfo> quests)
        {
            Id = journalGenre.RowId;
            Name = journalGenre.Name.ToString();
            CategoryId = journalGenre.JournalCategory.RowId;
            Quests = quests;
        }

        public Genre(uint id, string name, uint categoryId, List<IQuestInfo> quests)
        {
            Id = id;
            Name = name;
            CategoryId = categoryId;
            Quests = quests;
        }

        public uint Id { get; }
        public string Name { get; }
        public uint CategoryId { get; }
        public List<IQuestInfo> Quests { get; }
    }

    internal sealed class Category(JournalCategory journalCategory, IReadOnlyList<Genre> genres)
    {
        public uint Id { get; } = journalCategory.RowId;
        public string Name { get; } = journalCategory.Name.ToString();
        public uint SectionId { get; } = journalCategory.JournalSection.RowId;
        public IReadOnlyList<Genre> Genres { get; } = genres;
    }

    internal sealed class Section(JournalSection journalSection, IReadOnlyList<Category> categories)
    {
        public uint Id { get; } = journalSection.RowId;
        public string Name { get; } = journalSection.Name.ToString();
        public IReadOnlyList<Category> Categories { get; } = categories;
    }
}

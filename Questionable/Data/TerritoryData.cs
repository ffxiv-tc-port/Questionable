using Dalamud.Game;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using Lumina.Excel.Sheets;
using Questionable.Model.Questing;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
namespace Questionable.Data;

internal sealed class TerritoryData
{
    private readonly ImmutableDictionary<uint, ContentFinderConditionData> _contentFinderConditions;
    private readonly ImmutableDictionary<uint, uint> _dutyTerritories;
    private readonly ImmutableDictionary<uint, string> _instanceNames;
    private readonly ImmutableDictionary<(ElementId QuestId, byte Index), uint> _questBattlesToContentFinderCondition;
    private readonly ImmutableHashSet<uint> _territoriesWithMount;
    private readonly ImmutableDictionary<uint, string> _territoryNames;

    public TerritoryData(IDataManager dataManager)
    {
        _territoryNames = dataManager.GetExcelSheet<TerritoryType>()
            .Where(x => x.RowId > 0)
            .Select(x =>
                new
                {
                    x.RowId,
                    Name = x.PlaceName.ValueNullable?.Name.ToString() ?? x.PlaceNameZone.ValueNullable?.Name.ToString()
                })
            .Where(x => !string.IsNullOrEmpty(x.Name))
            .ToImmutableDictionary(x => x.RowId, x => x.Name!);

        _territoriesWithMount = dataManager.GetExcelSheet<TerritoryType>()
            .Where(x => x.RowId > 0 && x.Mount)
            .Select(x => x.RowId)
            .ToImmutableHashSet();

        _dutyTerritories = dataManager.GetExcelSheet<TerritoryType>()
            .Where(x => x.RowId > 0 && x.ContentFinderCondition.RowId != 0)
            .ToImmutableDictionary(x => x.RowId, x => x.ContentFinderCondition.Value.ContentType.RowId);

        _instanceNames = dataManager.GetExcelSheet<ContentFinderCondition>()
            .Where(x => x.RowId > 0 && x.Content.RowId != 0 && x.ContentLinkType == 1 && x.ContentType.RowId != 6)
            .ToImmutableDictionary(x => x.Content.RowId, x => x.Name.ToDalamudString().ToString());

        _contentFinderConditions = dataManager.GetExcelSheet<ContentFinderCondition>()
            .Where(x => x.RowId > 0 && x.Content.RowId != 0 && x.ContentLinkType is 1 or 5 && x.ContentType.RowId != 6)
            .Select(x => new ContentFinderConditionData(x, dataManager.Language))
            .ToImmutableDictionary(x => x.ContentFinderConditionId, x => x);

        // 查不到對應列的任務戰鬥直接略過(而不是讓建構式擲例外),詳見
        // LookupContentFinderConditionForQuestBattle 的說明。
        _questBattlesToContentFinderCondition = dataManager.GetExcelSheet<Quest>()
            .Where(x => x is { RowId: > 0, IssuerLocation.RowId: > 0 })
            .SelectMany(GetQuestBattles)
            .Select(x => (x.QuestId, x.Index,
                CfcId: LookupContentFinderConditionForQuestBattle(dataManager, x.QuestBattleId)))
            .Where(x => x.CfcId != null)
            .ToImmutableDictionary(x => (x.QuestId, x.Index), x => x.CfcId!.Value);
    }

    public string? GetName(uint territoryId)
    {
        return _territoryNames.GetValueOrDefault(territoryId);
    }

    public string GetNameAndId(uint territoryId)
    {
        string? territoryName = GetName(territoryId);
        if (territoryName != null)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{territoryName} ({territoryId})");
        }
        else
        {
            return territoryId.ToString(CultureInfo.InvariantCulture);
        }
    }

    public bool CanUseMount(uint territoryId)
    {
        return _territoriesWithMount.Contains(territoryId);
    }

    public bool IsDutyInstance(uint territoryId)
    {
        return _dutyTerritories.ContainsKey(territoryId);
    }

    public bool IsQuestBattleInstance(uint territoryId)
    {
        return _dutyTerritories.TryGetValue(territoryId, out uint contentType) && contentType == 7;
    }

    public string? GetInstanceName(ushort instanceId)
    {
        return _instanceNames.GetValueOrDefault(instanceId);
    }

    public ContentFinderConditionData? GetContentFinderCondition(uint cfcId)
    {
        return _contentFinderConditions.GetValueOrDefault(cfcId);
    }

    public bool TryGetContentFinderCondition(uint cfcId,
        [NotNullWhen(true)] out ContentFinderConditionData? contentFinderConditionData)
    {
        return _contentFinderConditions.TryGetValue(cfcId, out contentFinderConditionData);
    }

    public bool TryGetContentFinderConditionForSoloInstance(ElementId questId, byte index,
        [NotNullWhen(true)] out ContentFinderConditionData? contentFinderConditionData)
    {
        if (_questBattlesToContentFinderCondition.TryGetValue((questId, index), out uint cfcId))
        {
            return _contentFinderConditions.TryGetValue(cfcId, out contentFinderConditionData);
        }
        else
        {
            contentFinderConditionData = null;
            return false;
        }
    }

    /// <remarks>
    /// ⚠️ 原本這裡用 <c>_contentFinderConditions[x.Value]</c> 直接索引,對不在字典裡的 cfcId 會擲
    /// <see cref="KeyNotFoundException"/>。<see cref="TryGetContentFinderConditionForSoloInstance"/> 對同樣的
    /// 情況早就是回 <c>false</c>(走 <c>TryGetValue</c>),所以這裡改成略過才是兩邊一致的行為。
    /// 📌 2026-08-15 離線核對台服 7.20:277 筆全部命中,目前不會少列。
    /// </remarks>
    public IEnumerable<(ElementId QuestId, byte Index, ContentFinderConditionData Data)> GetAllQuestsWithQuestBattles()
    {
        foreach((var key, uint cfcId) in _questBattlesToContentFinderCondition)
        {
            if (_contentFinderConditions.TryGetValue(cfcId, out ContentFinderConditionData? data))
                yield return (key.QuestId, key.Index, data);
        }
    }

    private static string FixName(string name, ClientLanguage language)
    {
        if (string.IsNullOrEmpty(name) || language != ClientLanguage.English)
        {
            return name;
        }

        return string.Concat(name[0].ToString().ToUpper(CultureInfo.InvariantCulture), name.AsSpan(1));
    }

    private static IEnumerable<(ElementId QuestId, byte Index, uint QuestBattleId)> GetQuestBattles(Quest quest)
    {
        foreach(Quest.QuestParamsStruct t in quest.QuestParams)
        {
            if (t.ScriptInstruction == "QUESTBATTLE0" || (quest.RowId.Equals(5325) && t.ScriptInstruction == "INSTANCEDUNGEON0"))
            {
                yield return (QuestId.FromRowId(quest.RowId), 0, t.ScriptArg);
            }
            else if (t.ScriptInstruction == "QUESTBATTLE1")
            {
                yield return (QuestId.FromRowId(quest.RowId), 1, t.ScriptArg);
            }
            else if (t.ScriptInstruction.IsEmpty)
            {
                break;
            }
        }
    }

    /// <summary>
    /// 由任務戰鬥 id 反查對應的 ContentFinderCondition id。查不到時回 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// 🔴 這是建構式路徑,擲例外＝<see cref="TerritoryData"/> 這個 DI 單例建不起來＝整個外掛載入失敗,
    /// 不是「某個任務不能跑」而已。
    /// <br/><br/>
    /// id 來自 <c>Quest</c> 表的 <c>QuestParams[].ScriptArg</c>,那是原始 uint 而不是 Lumina 的 RowRef,
    /// 遊戲資料本身不保證它指得到列。而台服 7.20 的 <c>InstanceContent</c> 是<b>稀疏表</b>
    /// (719 列散布在 0..65002),<c>GetRow</c> 撲空就擲 <see cref="ArgumentOutOfRangeException"/>。
    /// <br/><br/>
    /// 📌 2026-08-15 離線核對台服 7.20 EXD:277 筆 QUESTBATTLE 參數<b>全部命中</b>
    /// (82 筆走 InstanceContent、195 筆走 QuestBattleResident),所以目前不會觸發 ——
    /// 這是預防「台服改版新增任務戰鬥但本地 sheet 還沒跟上」的那一刻,不是在修現有故障。
    /// </remarks>
    private static uint? LookupContentFinderConditionForQuestBattle(IDataManager dataManager, uint questBattleId)
    {
        if (questBattleId >= 5000)
        {
            return dataManager.GetExcelSheet<InstanceContent>().GetRowOrDefault(questBattleId)
                ?.ContentFinderCondition.RowId;
        }
        else
        {
            return dataManager.GetExcelSheet<QuestBattleResident>().GetRowOrDefault(questBattleId)?.Unknown0;
        }
    }

    public sealed record ContentFinderConditionData
    (
        uint ContentFinderConditionId,
        string Name,
        uint TerritoryId,
        ushort RequiredItemLevel)
    {
        public ContentFinderConditionData(ContentFinderCondition condition, ClientLanguage clientLanguage)
            : this(condition.RowId, FixName(condition.Name.ToDalamudString().ToString(), clientLanguage),
                condition.TerritoryType.RowId, condition.ItemLevelRequired)
        {
        }
    }
}

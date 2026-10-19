using Dalamud.Plugin.Services;
using Lumina.Excel;
using Lumina.Text.Payloads;
using Lumina.Text.ReadOnly;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
namespace Questionable.Data;

public static class DataManagerExtensions
{
    public static ReadOnlySeString? GetSeString<T>(this IDataManager dataManager, uint rowId, Func<T, ReadOnlySeString?> mapper)
    where T : struct, IExcelRow<T>
    {
        ArgumentNullException.ThrowIfNull(dataManager);
        ArgumentNullException.ThrowIfNull(mapper);

        T? row = dataManager.GetExcelSheet<T>().GetRowOrDefault(rowId);
        if (row == null)
        {
            return null;
        }

        return mapper(row.Value);
    }

    public static string? GetString<T>(this IDataManager dataManager, uint rowId, Func<T, ReadOnlySeString?> mapper,
        IPluginLog? pluginLog = null)
    where T : struct, IExcelRow<T>
    {
        string? text = dataManager.GetSeString(rowId, mapper)?.WithCertainMacroCodeReplacements();

        pluginLog?.Verbose($"{typeof(T).Name}.{rowId} => {text}");
        return text;
    }

    public static Regex? GetRegex<T>(this IDataManager dataManager, uint rowId, Func<T, ReadOnlySeString?> mapper,
        IPluginLog? pluginLog = null)
    where T : struct, IExcelRow<T>
    {
        ReadOnlySeString? text = dataManager.GetSeString(rowId, mapper);
        if (text == null)
        {
            return null;
        }

        Regex regex = text.ToRegex();
        pluginLog?.Verbose($"{typeof(T).Name}.{rowId} => /{regex}/");
        return regex;
    }

    public static Regex ToRegex(this ReadOnlySeString? text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new(string.Join("", text.Value.Select(payload =>
        {
            if (payload.Type == ReadOnlySePayloadType.Text)
            {
                return Regex.Escape(payload.ToString());
            }
            else
            {
                return "(.*)";
            }
        })));
    }

    public static string WithCertainMacroCodeReplacements(this ReadOnlySeString text)
    {
        return string.Join("", text.Select(payload =>
        {
            return payload.Type switch
            {
                ReadOnlySePayloadType.Text => payload.ToString(),
                ReadOnlySePayloadType.Macro => payload.MacroCode switch
                {
                    MacroCode.NewLine => "",
                    MacroCode.NonBreakingSpace => " ",
                    MacroCode.Hyphen => "-",
                    MacroCode.SoftHyphen => "",
                    var _ => payload.ToString()
                },
                var _ => payload.ToString()
            };
        }));
    }

    /// <summary>
    /// 樣板的固定文字至少要有幾個字母或數字；再少（例如「要‹名稱›嗎？」只剩兩個字）幾乎什麼確認框都比得中，
    /// 寧可維持比不中。現有最短的是「要摘‹名稱›嗎？」「要選‹名稱›嗎？」的三個字。
    /// </summary>
    private const int MinTemplateLetters = 3;

    /// <summary>
    /// 比對畫面文字用的整句錨定樣板：固定文字逐字、換行巨集可有可無，會代入文字的巨集（名稱、數字、
    /// 條件句、顏色之類）的位置接受任意字串。沒有這種巨集，或固定文字的字母數字不到
    /// <see cref="MinTemplateLetters"/> 個時回 <see langword="null"/>，呼叫端照舊逐字比。
    /// </summary>
    /// <remarks>
    /// 逐字比恆不相等的原因：<see cref="WithCertainMacroCodeReplacements(ReadOnlySeString)"/> 把這種巨集剝成
    /// 巨集字面，畫面上卻是代入後的字。與 <see cref="ToRegex"/> 不同，這裡頭尾都錨定、只放寬巨集位置；
    /// 固定文字與 NBSP／連字號／軟連字號沿用那支的對照，才和畫面那側（同一支剝法）一致。
    /// </remarks>
    public static Regex? ToScreenTemplate(this ReadOnlySeString text)
    {
        StringBuilder pattern = new(@"\A");
        bool wildcard = false;
        bool hasWildcard = false;
        int letters = 0;
        foreach (ReadOnlySePayload payload in text)
        {
            string? literal = payload.Type != ReadOnlySePayloadType.Macro
                ? payload.ToString()
                : payload.MacroCode switch
                {
                    MacroCode.NonBreakingSpace => " ",
                    MacroCode.Hyphen => "-",
                    MacroCode.SoftHyphen => "",
                    var _ => null
                };

            if (literal != null)
            {
                pattern.Append(Regex.Escape(literal));
                letters += literal.Count(char.IsLetterOrDigit);
                wildcard = wildcard && literal.Length == 0;
            }
            else if (payload.MacroCode == MacroCode.NewLine)
            {
                pattern.Append(@"(?:\r?\n)?");
            }
            else if (!wildcard)
            {
                pattern.Append(".*");
                wildcard = hasWildcard = true;
            }
        }

        if (!hasWildcard || letters < MinTemplateLetters)
        {
            return null;
        }

        // NonBacktracking：預設引擎在多個 .* 之間回溯，最壞情況隨巨集數成指數成長；這個保證線性時間。
        return new Regex(pattern.Append(@"\z").ToString(), RegexOptions.Singleline | RegexOptions.NonBacktracking);
    }
}

public interface IQuestDialogueText
{
    public ReadOnlySeString Key { get; }
    public ReadOnlySeString Value { get; }
}

[SuppressMessage("Performance", "CA1815")]
[Sheet("QuestDialogueText")]
public readonly struct QuestDialogueText(ExcelPage page, uint offset, uint row) : IQuestDialogueText, IExcelRow<QuestDialogueText>
{
    public ExcelPage ExcelPage => page;
    public uint RowOffset => offset;
    public uint RowId => row;

    public ReadOnlySeString Key => page.ReadString(offset, offset);
    public ReadOnlySeString Value => page.ReadString(offset + 4, offset);

    static QuestDialogueText IExcelRow<QuestDialogueText>.Create(ExcelPage page, uint offset, uint row)
    {
        return new(page, offset, row);
    }
}

using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Text.Payloads;
using Lumina.Text.ReadOnly;
using System.Linq;
namespace Questionable.Utils;

internal static class AtkValueAdapter
{
    public static unsafe string? ReadString(AtkValue value)
    {
        if (value.Type == FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Undefined)
        {
            return null;
        }

        if (value.String.HasValue)
        {
            return MemoryHelper.ReadSeStringNullTerminated(new(value.String)).WithCertainMacroCodeReplacements();
        }

        return null;
    }

    /// <summary>
    /// 邊界安全版:讀 <c>addon-&gt;AtkValues[index]</c> 的字串,取不到回 <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// 🔴 <see cref="ReadString(AtkValue)"/> 是<b>傳值</b>參數 —— 它裡面的 <c>Type</c>／
    /// <c>String.HasValue</c> 檢查發生在<b>複製之後</b>,而複製那一步就是危險的那一步:
    /// <c>AtkUnitBase.AtkValues</c> 是指標欄位,addon 剛 setup 或正在拆解時可能是 null,
    /// 而陣列長度在另一個欄位 <c>AtkValuesCount</c>(<c>ushort</c>)。既有寫法
    /// <c>ReadString(addon-&gt;AtkValues[N])</c> 兩者都沒驗:null 時從位址 <c>N*0x10</c> 讀,
    /// 長度不足時讀的是陣列後方的堆積垃圾(型別欄位是隨機值 ⇒ 可能通過 <c>String.HasValue</c>
    /// 再拿垃圾當字串指標)。AccessViolationException 在 .NET Core 是 corrupted-state exception,
    /// <c>try</c>/<c>catch</c> 完全攔不到,只能在讀取前擋。
    /// <para>三道守衛直接沿用 ECommons <c>GenericHelpers.TryGetAtkValue</c>(addon 判空 ＋
    /// <c>AtkValues</c> 判空 ＋ 索引在 <c>[0, AtkValuesCount)</c> 內),不另外複製一份實作。</para>
    /// <para>失敗語意:回 <see langword="null"/>,與「這一格不是字串」完全相同 ——
    /// 所有呼叫端本來就有處理 <see langword="null"/> 的路徑,所以取得到時行為一字不改。</para>
    /// </remarks>
    public static unsafe string? ReadString(AtkUnitBase* addon, int index)
        => ECommons.GenericHelpers.TryGetAtkValue(addon, index, out AtkValue value) ? ReadString(value) : null;

    /// <inheritdoc cref="ReadString(AtkUnitBase*, int)"/>
    /// <summary>邊界安全版:讀 <c>addon-&gt;AtkValues[index]</c> 的型別,取不到回 <c>Undefined</c>。</summary>
    public static unsafe FFXIVClientStructs.FFXIV.Component.GUI.ValueType ReadType(AtkUnitBase* addon, int index)
        => ECommons.GenericHelpers.GetAtkValueType(addon, index);

    /// <inheritdoc cref="ReadString(AtkUnitBase*, int)"/>
    /// <summary>邊界安全版:讀 <c>addon-&gt;AtkValues[index]</c> 的 <c>Int</c>,取不到回 <paramref name="fallback"/>。</summary>
    public static unsafe int ReadInt(AtkUnitBase* addon, int index, int fallback = 0)
        => ECommons.GenericHelpers.GetAtkValueInt(addon, index, fallback);

    /// <inheritdoc cref="ReadString(AtkUnitBase*, int)"/>
    /// <summary>邊界安全版:讀 <c>addon-&gt;AtkValues[index]</c> 的 <c>UInt</c>,取不到回 <paramref name="fallback"/>。</summary>
    public static unsafe uint ReadUInt(AtkUnitBase* addon, int index, uint fallback = 0)
        => ECommons.GenericHelpers.GetAtkValueUInt(addon, index, fallback);
}

internal static class SeStringAdapterExtensions
{
    public static string WithCertainMacroCodeReplacements(this SeString? str)
    {
        if (str == null)
        {
            return string.Empty;
        }

        ReadOnlySeString seString = new(str.Encode());
        return seString.WithCertainMacroCodeReplacementsFromReadOnly();
    }

    public static string WithCertainMacroCodeReplacementsFromReadOnly(this ReadOnlySeString text)
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
}

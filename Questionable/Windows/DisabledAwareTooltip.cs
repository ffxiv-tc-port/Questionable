using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using System.Numerics;

namespace Questionable.Windows;

/// <summary>
/// 停用區段（<c>BeginDisabled</c>）裡畫說明用的小工具。
/// </summary>
/// <remarks>
/// 停用區段會把 <c>Style.Alpha</c> 乘上 <c>DisabledAlpha</c>，在裡面打開的說明框跟著變淡；
/// 這裡在畫說明框的那一段暫時把 Alpha 推回 1.0，畫完就彈回。不在停用區段裡時 Alpha 本來就是 1.0，外觀不變。
/// </remarks>
internal static class DisabledAwareTooltip
{
    /// <summary>推一層 Alpha＝1.0，用 <c>using</c> 包住整段說明框（要包在 <c>ImRaii.Tooltip()</c> 外層）。</summary>
    public static ImRaii.Style FullAlpha() => ImRaii.PushStyle(ImGuiStyleVar.Alpha, 1f);

    public static void SetTooltip(string text)
    {
        using ImRaii.Style alpha = FullAlpha();
        ImGui.SetTooltip(text);
    }

    /// <summary>
    /// 外觀與 Dalamud <c>ImGuiComponents.HelpMarker</c> 相同，但在停用區段裡也看得到說明。
    /// </summary>
    /// <remarks>Dalamud 的版本判 hover 不帶 <c>AllowWhenDisabled</c>，停用區段裡永遠不會顯示說明。</remarks>
    public static void HelpMarker(string helpText, FontAwesomeIcon icon = FontAwesomeIcon.InfoCircle,
        Vector4? color = null)
    {
        using (ImRaii.PushColor(ImGuiCol.TextDisabled, color ?? default, color.HasValue))
        {
            ImGui.SameLine();

            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                ImGui.TextDisabled(icon.ToIconString());
            }

            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                using (FullAlpha())
                using (ImRaii.Tooltip())
                using (ImRaii.TextWrapPos(ImGui.GetFontSize() * 35.0f))
                {
                    ImGui.Text(helpText);
                }
            }
        }
    }
}

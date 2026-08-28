using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin;
using Dalamud.Utility;
using ECommons.LanguageHelpers;
using System;
using System.Linq;
namespace Questionable.Windows.ConfigComponents;

internal sealed class NotificationConfigComponent
(
    IDalamudPluginInterface pluginInterface,
    Configuration configuration) : ConfigComponent(pluginInterface, configuration)
{

    public override void DrawTab()
    {
        using var tab = ImRaii.TabItem($"{"Notifications".Loc()}###Notifications");
        if (!tab)
        {
            return;
        }

        bool enabled = Configuration.Notifications.Enabled;
        if (ImGui.Checkbox("Enable notifications when manual interaction is required".Loc(), ref enabled))
        {
            Configuration.Notifications.Enabled = enabled;
            Save();
        }

        using (ImRaii.Disabled(!Configuration.Notifications.Enabled))
        {
            using (ImRaii.PushIndent())
            {
                XivChatType[] xivChatTypes = Enum.GetValues<XivChatType>()
                    .Where(x => x != XivChatType.StandardEmote)
                    .ToArray();
                int selectedChatType = Array.IndexOf(xivChatTypes, Configuration.Notifications.ChatType);
                string[] chatTypeNames = xivChatTypes
                    .Select(t => t.GetAttribute<XivChatTypeInfoAttribute>()?.FancyName ?? t.ToString())
                    .ToArray();
                if (ImGui.Combo("Chat channel".Loc(), ref selectedChatType, chatTypeNames,
                    chatTypeNames.Length))
                {
                    Configuration.Notifications.ChatType = xivChatTypes[selectedChatType];
                    Save();
                }

                ImGui.Separator();
                ImGui.Text("Desktop notifications".Loc());
                ImGuiComponents.HelpMarker("Desktop tray and taskbar notifications are currently unavailable.".Loc());
                using (ImRaii.Disabled())
                {
                    bool showTrayMessage = Configuration.Notifications.ShowTrayMessage;
                    if (ImGui.Checkbox("Show tray notification".Loc(), ref showTrayMessage))
                    {
                        Configuration.Notifications.ShowTrayMessage = showTrayMessage;
                        Save();
                    }

                    bool flashTaskbar = Configuration.Notifications.FlashTaskbar;
                    if (ImGui.Checkbox("Flash taskbar icon".Loc(), ref flashTaskbar))
                    {
                        Configuration.Notifications.FlashTaskbar = flashTaskbar;
                        Save();
                    }
                }
            }
        }

        ImGui.Separator();

        // 📌 刻意不放進上面那個 Disabled 區塊：聊天通知的總開關管的是「走到手動步驟時印不印訊息」，
        //    而語音提醒連「因為錯誤／卡住而停下來」也會喊，兩者不是同一件事。
        bool praiseWithTataru = Configuration.Notifications.PraiseWithTataru;
        if (ImGui.Checkbox("Ask Tataru to speak up when questing gets stuck (requires TataruPraise)".Loc(),
            ref praiseWithTataru))
        {
            Configuration.Notifications.PraiseWithTataru = praiseWithTataru;
            Save();
        }

        ImGuiComponents.HelpMarker(
            ("Whenever automatic questing stops because it is stuck - an unsupported step, a task that failed, " +
             "or a step that has to be done by hand - TataruPraise is asked to say a line out loud. " +
             "Does nothing if TataruPraise is not installed.").Loc());
    }
}

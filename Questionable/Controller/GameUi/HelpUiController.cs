using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Microsoft.Extensions.Logging;
using Questionable.Utils;
using System;
namespace Questionable.Controller.GameUi;

internal sealed class HelpUiController : IDisposable
{
    /// <summary>
    ///     跟在 -2 後面的那一發 -1 要等這麼多幀才送出。
    /// </summary>
    /// <remarks>
    ///     🔴 <b>這不是節流，是為了避開原生 AccessViolation。</b>
    ///     <c>FireCallbackInt</c> 把 <c>close</c> 寫死成 true，處理常式回傳非零就<b>在同一個呼叫堆疊裡</b>關窗
    ///     —— 第一發 <c>-2</c> 有可能在回到我們這裡之前就把窗推進關閉流程，
    ///     緊接著送第二發就是「對正在關閉中的視窗按第二次」＝攔不到的存取違規（遊戲當場關閉）。
    ///     <para>
    ///         <c>-2</c> 對這扇窗到底會不會關窗，<b>離線證不出來</b>（取決於執行期才綁定的 agent 回傳值），
    ///         所以這裡用<b>不依賴那個假設</b>的做法。
    ///     </para>
    ///     <para>
    ///         30 幀遠大於實測的「關閉中危險窗口」（&lt; 10 幀），而這扇說明窗晚半秒關掉對
    ///         自動流程沒有任何影響。守衛的 PreFinalize 記號與每幀輪詢會再擋掉一層。
    ///     </para>
    /// </remarks>
    private const int MultipleHelpWindowFollowUpDelayFrames = 30;

    /// <summary>
    ///     與 <see cref="MultipleHelpWindowFollowUpDelayFrames" /> 併用的牆鐘下限（毫秒）。
    /// </summary>
    /// <remarks>
    ///     <c>IFramework.RunOnTick</c> 是延遲時間與 tick 數<b>兩個條件都滿足才跑</b>，所以這兩個常數是「取較晚者」。
    ///     光數幀不夠：關閉轉場的長度是<b>時間</b>而不是幀數，不鎖幀率時 30 個 tick 可能只有 0.2 秒。
    ///     反過來只用牆鐘也不夠：載入密集區幀率掉下來時，0.5 秒可能還不到幾幀。
    ///     <para>
    ///         📌 <c>delayTicks</c> 數的是 Dalamud 的 framework tick，<b>不是</b><c>UiBuilder.FrameCount</c>
    ///         —— 所以它不會在過場動畫／隱藏 UI 期間停住。
    ///     </para>
    /// </remarks>
    private const int MultipleHelpWindowFollowUpDelayMilliseconds = 500;

    /// <summary><c>-2</c> 的終結紀錄要在補送 <c>-1</c> 前變冷，否則守衛會擋掉那一發；少 10 幀吸收時鐘先後差，仍大於多次互動的 15 幀。</summary>
    private const int MultipleHelpWindowFirstPressEscapeFrames = MultipleHelpWindowFollowUpDelayFrames - 10;

    private readonly IAddonLifecycle _addonLifecycle;
    private readonly IFramework _framework;
    private readonly IGameGuiAdapter _gameGui;
    private readonly ILogger<HelpUiController> _logger;
    private readonly QuestController _questController;

    private bool _disposed;

    public HelpUiController(
        QuestController questController,
        IAddonLifecycle addonLifecycle,
        IGameGuiAdapter gameGui,
        IFramework framework,
        ILogger<HelpUiController> logger)
    {
        _questController = questController;
        _addonLifecycle = addonLifecycle;
        _gameGui = gameGui;
        _framework = framework;
        _logger = logger;

        _questController.AutomationTypeChanged += CloseHelpWindowsWhenStartingQuests;

        _addonLifecycle.RegisterListener(AddonEvent.PostSetup, "AkatsukiNote", UnendingCodexPostSetup);
        _addonLifecycle.RegisterListener(AddonEvent.PostSetup, "ContentsTutorial", ContentsTutorialPostSetup);
        _addonLifecycle.RegisterListener(AddonEvent.PostSetup, "MultipleHelpWindow", MultipleHelpWindowPostSetup);
        _addonLifecycle.RegisterListener(AddonEvent.PostSetup, "JobHudNotice", JobHudNoticePostSetup);
        _addonLifecycle.RegisterListener(AddonEvent.PostSetup, "Guide", GuidePostSetup);
        _addonLifecycle.RegisterListener(AddonEvent.PostSetup, "EventTutorial", EventTutorialPostSetup);
    }


    public void Dispose()
    {
        _disposed = true;

        _addonLifecycle.UnregisterListener(AddonEvent.PostSetup, "EventTutorial", EventTutorialPostSetup);
        _addonLifecycle.UnregisterListener(AddonEvent.PostSetup, "Guide", GuidePostSetup);
        _addonLifecycle.UnregisterListener(AddonEvent.PostSetup, "JobHudNotice", JobHudNoticePostSetup);
        _addonLifecycle.UnregisterListener(AddonEvent.PostSetup, "MultipleHelpWindow", MultipleHelpWindowPostSetup);
        _addonLifecycle.UnregisterListener(AddonEvent.PostSetup, "ContentsTutorial", ContentsTutorialPostSetup);
        _addonLifecycle.UnregisterListener(AddonEvent.PostSetup, "AkatsukiNote", UnendingCodexPostSetup);

        _questController.AutomationTypeChanged -= CloseHelpWindowsWhenStartingQuests;
    }

    private unsafe void CloseHelpWindowsWhenStartingQuests(object sender, QuestController.EAutomationType e)
    {
        if (e is QuestController.EAutomationType.Manual)
        {
            return;
        }

        if (_gameGui.TryGetAddonByName("Guide", out AtkUnitBase* addonGuide))
        {
            _logger.LogInformation("Guide window is open");
            GuidePostSetup(addonGuide);
        }

        if (_gameGui.TryGetAddonByName("EventTutorial", out AtkUnitBase* addonEventTutorial))
        {
            _logger.LogInformation("EventTutorial window is open");
            EventTutorialPostSetup(addonEventTutorial);
        }

        if (_gameGui.TryGetAddonByName("ContentsTutorial", out AtkUnitBase* addonContentsTutorial))
        {
            _logger.LogInformation("ContentsTutorial window is open");
            ContentsTutorialPostSetup(addonContentsTutorial);
        }

        if (_gameGui.TryGetAddonByName("JobHudNotice", out AtkUnitBase* addonJobHudNotice))
        {
            _logger.LogInformation("JobHudNotice window is open");
            JobHudNoticePostSetup(addonJobHudNotice);
        }
    }

    private unsafe void UnendingCodexPostSetup(AddonEvent type, AddonArgs args)
    {
        if (_questController.StartedQuest?.Quest.Id.Value == 4526)
        {
            _logger.LogInformation("Closing Unending Codex");
            AtkUnitBase* addon = (AtkUnitBase*)args.Addon.Address;
            AddonPressGuard.PressCallbackInt("AkatsukiNote", addon, -2);
        }
    }

    private unsafe void ContentsTutorialPostSetup(AddonEvent type, AddonArgs args)
    {
        if (_questController.StartedQuest?.Quest.Id.Value is 245 or 3872 or 5253)
        {
            ContentsTutorialPostSetup((AtkUnitBase*)args.Addon.Address);
        }
    }

    private unsafe void ContentsTutorialPostSetup(AtkUnitBase* addon)
    {
        _logger.LogInformation("Closing ContentsTutorial");
        AddonPressGuard.PressCallbackInt("ContentsTutorial", addon, 13);
    }

    /// <summary>
    ///     Opened e.g. the first time you open the duty finder window during Sastasha.
    /// </summary>
    /// <remarks>
    ///     -2、-1 兩發不可以同幀連送（守衛也會擋：-2 是終結按法，熱著時同窗其他按法都擋），
    ///     第一發 -2 若在同一個呼叫堆疊裡就把窗推進關閉流程，第二發就踩在關閉中的窗上
    ///     （詳見 <see cref="MultipleHelpWindowFollowUpDelayFrames" />）。
    ///     做法：先送 -2，隔幾幀回頭看，<b>窗還在、而且還是同一個實例</b>才補送 -1。
    /// </remarks>
    private unsafe void MultipleHelpWindowPostSetup(AddonEvent type, AddonArgs args)
    {
        if (_questController.StartedQuest?.Quest.Id.Value != 245)
        {
            return;
        }

        _logger.LogInformation("Closing MultipleHelpWindow");
        AtkUnitBase* addon = (AtkUnitBase*)args.Addon.Address;
        if (!AddonPressGuard.PressCallbackInt("MultipleHelpWindow", addon, -2, MultipleHelpWindowFirstPressEscapeFrames))
        {
            return;
        }

        // 位址在這裡就抄成不透明的整數，之後只做等值比較，永遠不解參考。
        nint pressedInstance = (nint)addon;
        _framework.RunOnTick(() =>
        {
            if (_disposed)
            {
                return;
            }

            // -2 已經把它收掉了：不需要補送，也不可以去碰別的東西。
            if (!_gameGui.TryGetAddonByName("MultipleHelpWindow", out AtkUnitBase* stillOpen))
            {
                return;
            }

            // 換成另一扇同名的窗了：那扇窗自己的 PostSetup 會處理它，
            // 不要拿舊窗的後援去按新窗（也可能是同一塊記憶體被重用）。
            if ((nint)stillOpen != pressedInstance)
            {
                return;
            }

            _logger.LogInformation("MultipleHelpWindow survived the first callback; sending the deferred one");
            AddonPressGuard.PressCallbackInt("MultipleHelpWindow", stillOpen, -1);
        }, delay: TimeSpan.FromMilliseconds(MultipleHelpWindowFollowUpDelayMilliseconds),
            delayTicks: MultipleHelpWindowFollowUpDelayFrames);
    }

    private unsafe void JobHudNoticePostSetup(AddonEvent type, AddonArgs args)
    {
        if (_questController.IsRunning || _questController.AutomationType != QuestController.EAutomationType.Manual)
        {
            JobHudNoticePostSetup((AtkUnitBase*)args.Addon.Address);
        }
    }

    private unsafe void JobHudNoticePostSetup(AtkUnitBase* addon)
    {
        _logger.LogInformation("Clicking the JobHudNotice window to open the relevant Guide page");
        AddonPressGuard.PressCallbackInt("JobHudNotice", addon, 0);
    }

    private unsafe void GuidePostSetup(AddonEvent type, AddonArgs args)
    {
        if (_questController.IsRunning || _questController.AutomationType != QuestController.EAutomationType.Manual)
        {
            GuidePostSetup((AtkUnitBase*)args.Addon.Address);
        }
    }

    private unsafe void GuidePostSetup(AtkUnitBase* addon)
    {
        _logger.LogInformation("Closing Guide window");
        AddonPressGuard.PressCallbackInt("Guide", addon, -1);
    }

    private unsafe void EventTutorialPostSetup(AddonEvent type, AddonArgs args)
    {
        if (_questController.IsRunning || _questController.AutomationType != QuestController.EAutomationType.Manual)
        {
            // TODO Verify that this actually works; in initial testing it didn't close the window.
            _framework.RunOnTick(() =>
            {
                if (_gameGui.TryGetAddonByName("EventTutorial", out AtkUnitBase* addonEventTutorial))
                {
                    EventTutorialPostSetup(addonEventTutorial);
                }
            });
        }
    }

    private unsafe void EventTutorialPostSetup(AtkUnitBase* addon)
    {
        _logger.LogInformation("Closing EventTutorial window");
        AddonPressGuard.PressCallbackInt("EventTutorial", addon, -1);
    }
}

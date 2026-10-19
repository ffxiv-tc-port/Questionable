using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Microsoft.Extensions.Logging;
using Questionable.Utils;
using System;
using System.Globalization;
namespace Questionable.Controller.GameUi;

internal sealed class CraftworksSupplyController : IDisposable
{
    private readonly IAddonLifecycle _addonLifecycle;
    private readonly IFramework _framework;
    private readonly IGameGuiAdapter _gameGui;
    private readonly ILogger<CraftworksSupplyController> _logger;
    private readonly QuestController _questController;

    public CraftworksSupplyController(QuestController questController, IAddonLifecycle addonLifecycle,
        IGameGuiAdapter gameGui, IFramework framework, ILogger<CraftworksSupplyController> logger)
    {
        _questController = questController;
        _addonLifecycle = addonLifecycle;
        _gameGui = gameGui;
        _framework = framework;
        _logger = logger;

        _addonLifecycle.RegisterListener(AddonEvent.PostReceiveEvent, "ContextIconMenu", ContextIconMenuPostReceiveEvent);
        _addonLifecycle.RegisterListener(AddonEvent.PostSetup, "BankaCraftworksSupply",
            BankaCraftworksSupplyPostUpdate);
    }

    private bool ShouldHandleUiInteractions => _questController.IsRunning;

    public void Dispose()
    {
        _addonLifecycle.UnregisterListener(AddonEvent.PostSetup, "BankaCraftworksSupply",
            BankaCraftworksSupplyPostUpdate);
        _addonLifecycle.UnregisterListener(AddonEvent.PostReceiveEvent, "ContextIconMenu", ContextIconMenuPostReceiveEvent);
    }

    private unsafe void BankaCraftworksSupplyPostUpdate(AddonEvent type, AddonArgs args)
    {
        if (!ShouldHandleUiInteractions)
        {
            return;
        }

        AtkUnitBase* addon = (AtkUnitBase*)args.Addon.Address;
        InteractWithBankaCraftworksSupply(addon);
    }

    private unsafe void InteractWithBankaCraftworksSupply()
    {
        if (_gameGui.TryGetAddonByName("BankaCraftworksSupply", out AtkUnitBase* addon))
        {
            InteractWithBankaCraftworksSupply(addon);
        }
    }

    /// <remarks>
    /// 🔴 <c>AtkUnitBase.AtkValues</c> 是指標欄位(addon 剛 setup／正在拆解時為 null),長度另存在
    /// <c>AtkValuesCount</c>;兩者都要先驗:null 解參考是攔不到的 AccessViolation,長度不足讀到的是
    /// 陣列後方的堆積垃圾,而 <c>missingCount = 6 - completedCount</c> 是 <c>uint</c> 減法 ——
    /// <c>completedCount</c> 只要是垃圾大數,迴圈次數就會下溢成接近 42 億。
    /// <para>失敗語意:安靜返回(＝這一次不動作)。這支由 addon 的 PostSetup 事件驅動,
    /// 下一次刷新還會再進來,取得到時行為一字不改。</para>
    /// </remarks>
    private unsafe void InteractWithBankaCraftworksSupply(AtkUnitBase* addon)
    {
        if (addon == null || addon->AtkValues == null)
        {
            return;
        }

        AtkValue* atkValues = addon->AtkValues;
        int valueCount = addon->AtkValuesCount;
        if (valueCount <= 31)
        {
            return;
        }

        uint completedCount = atkValues[7].UInt;
        uint missingCount = 6 - completedCount;
        for(int slot = 0; slot < missingCount; ++slot)
        {
            if (31 + slot >= valueCount)
            {
                break;
            }

            if (atkValues[31 + slot].UInt != 0)
            {
                continue;
            }

            // 多次互動窗:逐格挑選、窗不會因為被按而消失 ⇒ 逃生口用 15 幀。
            if (!AddonPressGuard.TryBeginPress("BankaCraftworksSupply", addon,
                    "slot:" + slot.ToString(CultureInfo.InvariantCulture),
                    AddonPressGuard.RoutineRePressEscapeFrames))
            {
                return;
            }

            _logger.LogInformation("Selecting an item for slot {Slot}", slot);
            AtkValue* selectSlot = stackalloc AtkValue[]
            {
                new() { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int, Int = 2 },
                new() { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int, Int = slot /* slot */ }
            };
            addon->FireCallback(2, selectSlot);
            return;
        }

        // do turn-in if any item is provided
        if (atkValues[31].UInt != 0)
        {
            if (AddonPressGuard.TryBeginPress("BankaCraftworksSupply", addon, "confirm"))
            {
                _logger.LogInformation("Confirming turn-in");
                addon->FireCallbackInt(0);
            }
        }
    }

    // FIXME: This seems to not work if the mouse isn't over the FFXIV window?
    private unsafe void ContextIconMenuPostReceiveEvent(AddonEvent type, AddonArgs args)
    {
        if (!ShouldHandleUiInteractions)
        {
            return;
        }

        AddonContextIconMenu* addonContextIconMenu = (AddonContextIconMenu*)args.Addon.Address;
        if (!addonContextIconMenu->IsVisible)
        {
            return;
        }

        ushort parentId = addonContextIconMenu->ContextMenuParentId;
        if (parentId == 0)
        {
            return;
        }

        // 走同 repo 既有的守衛版 helper：RaptureAtkUnitManager 與回傳值都判空。
        // GetAddonById 找不到對應的 addon 時回傳 null，直接解參考 NameString 會是攔不到的 AVE
        AtkUnitBase* parentAddon = AddonUtils.GetAddonById(parentId);
        if (parentAddon == null)
        {
            return;
        }

        // 🔴 名稱用有界讀法：CS 產生的 NameString 是無上限的 null-terminated 掃描，
        // 對「可能正在關閉／剛被回收」的視窗做無界讀取就是攔不到的存取違規。
        // 而且只讀這一次 —— 底下 FireCallback + Close(true) 之後再回頭讀同一個實例，
        // 那已經是對「可能已經被銷毀的視窗」解參考。
        string parentName = AddonUtils.ReadAddonName(parentAddon);

        if (parentName is "BankaCraftworksSupply")
        {
            // 🔴 這支掛在 PostReceiveEvent 上 —— ContextIconMenu 每收到一個事件就會進來一次。
            // 挑選(FireCallback 5)＋關閉(Close) 是刻意的一組動作，用同一個 key 一起罩住，
            // 免得對已經在關閉中的選單再送第二組。
            if (!AddonPressGuard.TryBeginPress("ContextIconMenu", (AtkUnitBase*)addonContextIconMenu, "pick"))
            {
                return;
            }

            _logger.LogInformation("Picking item for {AddonName}", parentName);
            AtkValue* selectSlot = stackalloc AtkValue[]
            {
                new() { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int, Int = 0 },
                new() { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int, Int = 0 /* slot */ },
                new() { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.UInt, UInt = 20802 /* probably the item's icon */ },
                new() { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.UInt, UInt = 0 },
                new() { Type = 0, Int = 0 }
            };
            // close 參數維持預設的 false：原生因此不會在這一發裡把選單收掉，回傳值也恆為 false、讀它沒有意義。
            // ⚠️ 不要「順手」改成 close: true —— 那會把關窗的決定權交給 agent 的回傳值，
            // agent 回 0 就沒有人關窗，交納流程會停在選單開著的狀態。
            addonContextIconMenu->FireCallback(5, selectSlot);

            // 🔴 第二次碰同一扇窗之前，先確認上面那一發沒有把它收掉：FireCallback 會同步跑完 agent 的處理常式，
            // 它會不會自己收掉選單離線證不出來；收掉的話 IsVisible 必定已經是 false（Hide 是同步寫回旗標）。
            // ⚠️ 這不是記憶體安全問題（同一個呼叫堆疊內 addon 不會被釋放），擋的是行為風險：
            // 對已經收掉的選單再送一發關窗事件，是遊戲自己從來不會做的事。
            if (addonContextIconMenu->IsVisible)
            {
                addonContextIconMenu->Close(true);
            }
            else
            {
                _logger.LogInformation(
                    "[CraftworksSupply] ContextIconMenu 在挑選 callback 的同一個呼叫堆疊裡就被收掉了" +
                    "(IsVisible 變成 false)，跳過原本無條件送出的 Close(true)。");
            }

            if (parentName == "BankaCraftworksSupply")
            {
                _framework.RunOnTick(InteractWithBankaCraftworksSupply, TimeSpan.FromMilliseconds(50));
            }
        }
        else
        {
            _logger.LogTrace("Ignoring contextmenu event for {AddonName}", parentName);
        }
    }
}

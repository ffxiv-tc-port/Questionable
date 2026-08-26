using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Microsoft.Extensions.Logging;
using Questionable.Utils;
using System;
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
    /// 🔴 <c>AtkUnitBase.AtkValues</c> 是指標欄位(addon 剛 setup／正在拆解時為 null),
    /// 長度另存在 <c>AtkValuesCount</c>。原本 <c>atkValues[7]</c>／<c>atkValues[31 + slot]</c>
    /// 兩者都沒驗:null 時從位址 <c>index * 0x10</c> 讀 ＝ AccessViolationException
    /// (corrupted-state exception,<c>try</c>/<c>catch</c> 攔不到);長度不足時讀到的是
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
            _logger.LogInformation("Confirming turn-in");
            addon->FireCallbackInt(0);
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

        if (parentAddon->NameString is "BankaCraftworksSupply")
        {
            _logger.LogInformation("Picking item for {AddonName}", parentAddon->NameString);
            AtkValue* selectSlot = stackalloc AtkValue[]
            {
                new() { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int, Int = 0 },
                new() { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int, Int = 0 /* slot */ },
                new() { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.UInt, UInt = 20802 /* probably the item's icon */ },
                new() { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.UInt, UInt = 0 },
                new() { Type = 0, Int = 0 }
            };
            addonContextIconMenu->FireCallback(5, selectSlot);
            addonContextIconMenu->Close(true);

            if (parentAddon->NameString == "BankaCraftworksSupply")
            {
                _framework.RunOnTick(InteractWithBankaCraftworksSupply, TimeSpan.FromMilliseconds(50));
            }
        }
        else
        {
            _logger.LogTrace("Ignoring contextmenu event for {AddonName}", parentAddon->NameString);
        }
    }
}

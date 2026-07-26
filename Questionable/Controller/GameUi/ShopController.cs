using Dalamud.Plugin.Services;
using ECommons.Automation;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using Microsoft.Extensions.Logging;
using Questionable.Controller.GameUi.Shop;
using Questionable.Controller.GameUi.Shop.Model;
using Questionable.Model.Questing;
using Questionable.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
namespace Questionable.Controller.GameUi;

// AddonMaster.Shop isn't available in the ECommons version pinned for this API level;
// this mirrors ECommons' own implementation (UIHelpers/AddonMasterImplementations/Shop.cs)
// directly against the raw AtkValues, which is stable across API levels since it just
// reads fixed indices out of the "Shop" addon's own UI data.
internal readonly struct ShopItemInfo
{
    public required uint ItemId { get; init; }
    public required uint CostAmount { get; init; }
    public required int Index { get; init; }

    public unsafe void Select(AtkUnitBase* shop, int amount = 1)
    {
        Callback.Fire(shop, true, 0, Index, amount);
    }
}

internal static class ShopAddonReader
{
    public static unsafe ShopItemInfo[] ReadShopItems(AtkUnitBase* addon)
    {
        List<ShopItemInfo> items = [];
        uint numEntries = addon->AtkValues[2].UInt;
        for(int i = 0; i < numEntries; ++i)
        {
            uint itemId = addon->AtkValues[441 + i].UInt;
            if (itemId == 0)
            {
                continue;
            }

            uint costAmount = addon->AtkValues[75 + i].UInt;
            items.Add(new ShopItemInfo { ItemId = itemId, CostAmount = costAmount, Index = i });
        }

        return [.. items];
    }
}

internal sealed class ShopController : IDisposable, IShopWindow
{
    private readonly IDataManager _dataManager;
    private readonly IFramework _framework;
    private readonly IGameGuiAdapter _gameGuiAdapter;
    private readonly ILogger<ShopController> _logger;
    private readonly QuestController _questController;
    private readonly RegularShopBase _shop;

    public ShopController(QuestController questController, IGameGui gameGui, IGameGuiAdapter gameGuiAdapter, IDataManager dataManager,
        IAddonLifecycle addonLifecycle, IFramework framework, ILogger<ShopController> logger, IPluginLog pluginLog)
    {
        _questController = questController;
        _gameGuiAdapter = gameGuiAdapter;
        _dataManager = dataManager;
        _framework = framework;
        _shop = new(this, "Shop", pluginLog, gameGui, addonLifecycle);
        _logger = logger;

        _framework.Update += FrameworkUpdate;
    }

    public bool IsAutoBuyEnabled => _shop.AutoBuyEnabled;

    public bool IsAwaitingYesNo
    {
        get => _shop.IsAwaitingYesNo;
        set => _shop.IsAwaitingYesNo = value;
    }

    public void Dispose()
    {
        _framework.Update -= FrameworkUpdate;
        _shop.Dispose();
    }

    public bool IsEnabled => _questController.IsRunning;
    public bool IsOpen { get; set; }

    public Vector2? Position { get; set; } // actual implementation doesn't matter, not a real window

    public int GetCurrencyCount()
    {
        return _shop.GetItemCount(1);
        // TODO: support other currencies
    }

    public unsafe void UpdateShopStock(AtkUnitBase* addon)
    {
        QuestStep? currentStep = FindCurrentStep();
        if (currentStep == null || currentStep.InteractionType != EInteractionType.PurchaseItem)
        {
            _shop.ItemForSale = null;
            return;
        }

        ShopItemInfo[] shopItems = ShopAddonReader.ReadShopItems(addon);
        if (shopItems.Length == 0)
        {
            _shop.ItemForSale = null;
            return;
        }

        _shop.ItemForSale = shopItems
            .Select((item, i) => new ItemForSale
            {
                Position = i,
                ItemId = item.ItemId,
                ItemName = _dataManager.GetExcelSheet<Item>().GetRowOrDefault(item.ItemId)?.Name.ToString() ?? string.Empty,
                Price = item.CostAmount,
                OwnedItems = (uint)_shop.GetItemCount(item.ItemId)
            })
            .FirstOrDefault(x => x.ItemId == currentStep.ItemId);
    }

    public unsafe void TriggerPurchase(AtkUnitBase* addonShop, int buyNow)
    {
        if (_shop.ItemForSale == null)
        {
            return;
        }

        ShopItemInfo[] shopItems = ShopAddonReader.ReadShopItems(addonShop);
        if (_shop.ItemForSale.Position >= 0 && _shop.ItemForSale.Position < shopItems.Length)
        {
            shopItems[_shop.ItemForSale.Position].Select(addonShop, buyNow);
        }
    }

    public void SaveExternalPluginState()
    {
    }

    public unsafe void RestoreExternalPluginState()
    {
        if (_gameGuiAdapter.TryGetAddonByName("Shop", out AtkUnitBase* addonShop))
        {
            addonShop->FireCallbackInt(-1);
        }
    }

    private void FrameworkUpdate(IFramework framework)
    {
        if (IsOpen && _shop.ItemForSale != null)
        {
            if (_shop.PurchaseState != null)
            {
                _shop.HandleNextPurchaseStep();
            }
            else
            {
                QuestStep? currentStep = FindCurrentStep();
                if (currentStep == null || currentStep.InteractionType != EInteractionType.PurchaseItem)
                {
                    return;
                }

                int missingItems = Math.Max(0,
                    currentStep.ItemCount.GetValueOrDefault() - (int)_shop.ItemForSale.OwnedItems);
                int toPurchase = Math.Min(_shop.GetMaxItemsToPurchase(), missingItems);
                if (toPurchase > 0)
                {
                    _logger.LogDebug("Auto-buying {MissingItems} {ItemName}", missingItems, _shop.ItemForSale.ItemName);
                    _shop.StartAutoPurchase(missingItems);
                    _shop.HandleNextPurchaseStep();
                }
                else
                {
                    _shop.CancelAutoPurchase();
                }
            }
        }
    }

    private QuestStep? FindCurrentStep()
    {
        QuestController.QuestProgress? currentQuest = _questController.CurrentQuest;
        QuestSequence? currentSequence = currentQuest?.Quest.FindSequence(currentQuest.Sequence);
        return currentSequence?.FindStep(currentQuest?.Step ?? 0);
    }
}

using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Microsoft.Extensions.Logging;
using Questionable.Utils;
using System;
namespace Questionable.Controller.GameUi;

internal sealed class CreditsController : IDisposable
{
    private readonly IAddonLifecycle _addonLifecycle;
    private readonly ILogger<CreditsController> _logger;
    private readonly QuestController _questController;

    public CreditsController(IAddonLifecycle addonLifecycle, QuestController questController, ILogger<CreditsController> logger)
    {
        _addonLifecycle = addonLifecycle;
        _questController = questController;
        _logger = logger;

        _addonLifecycle.RegisterListener(AddonEvent.PostSetup, "CreditScroll", CreditScrollPostSetup);
        _addonLifecycle.RegisterListener(AddonEvent.PostSetup, "Credit", CreditPostSetup);
        _addonLifecycle.RegisterListener(AddonEvent.PostSetup, "CreditPlayer", CreditPlayerPostSetup);
    }

    public void Dispose()
    {
        _addonLifecycle.UnregisterListener(AddonEvent.PostSetup, "CreditPlayer", CreditPlayerPostSetup);
        _addonLifecycle.UnregisterListener(AddonEvent.PostSetup, "Credit", CreditPostSetup);
        _addonLifecycle.UnregisterListener(AddonEvent.PostSetup, "CreditScroll", CreditScrollPostSetup);
    }


    /// <summary>只在自動化進行中才關；手動看片尾名單不動。</summary>
    private bool ShouldClose(string addonName)
    {
        if (_questController.IsRunning || _questController.AutomationType != QuestController.EAutomationType.Manual)
        {
            return true;
        }

        _logger.LogInformation("{Addon} opened while not questing, leaving it open", addonName);
        return false;
    }

    /// <summary>
    ///     ARR Credits.
    /// </summary>
    private unsafe void CreditScrollPostSetup(AddonEvent type, AddonArgs args)
    {
        if (!ShouldClose("CreditScroll"))
        {
            return;
        }

        _logger.LogInformation("Closing Credits sequence");
        AtkUnitBase* addon = (AtkUnitBase*)args.Addon.Address;
        AddonPressGuard.PressCallbackInt("CreditScroll", addon, -2);
    }

    /// <summary>
    ///     Credits for (possibly all?) expansions, not used for ARR.
    /// </summary>
    private unsafe void CreditPostSetup(AddonEvent type, AddonArgs args)
    {
        if (!ShouldClose("Credit"))
        {
            return;
        }

        _logger.LogInformation("Closing Credits sequence");
        AtkUnitBase* addon = (AtkUnitBase*)args.Addon.Address;
        AddonPressGuard.PressCallbackInt("Credit", addon, -2);
    }

    private unsafe void CreditPlayerPostSetup(AddonEvent type, AddonArgs args)
    {
        if (!ShouldClose("CreditPlayer"))
        {
            return;
        }

        _logger.LogInformation("Closing CreditPlayer");
        AtkUnitBase* addon = (AtkUnitBase*)args.Addon.Address;
        AddonPressGuard.PressClose("CreditPlayer", addon);
    }
}

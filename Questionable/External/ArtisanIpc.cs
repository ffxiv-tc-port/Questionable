using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using Microsoft.Extensions.Logging;
namespace Questionable.External;

internal sealed class ArtisanIpc(IDalamudPluginInterface pluginInterface, ILogger<ArtisanIpc> logger)
{
    private readonly ICallGateSubscriber<ushort, int, object> _craftItem = pluginInterface.GetIpcSubscriber<ushort, int, object>("Artisan.CraftItem");
    private readonly ICallGateSubscriber<bool> _getEnduranceStatus = pluginInterface.GetIpcSubscriber<bool>("Artisan.GetEnduranceStatus");
    private readonly ILogger<ArtisanIpc> _logger = logger;

    public bool CraftItem(ushort recipeId, int quantity)
    {
        try
        {
            _logger.LogInformation("Attempting to craft {Quantity} items with recipe {RecipeId} with Artisan", quantity,
                recipeId);
            _craftItem.InvokeAction(recipeId, quantity);
            return true;
        }
        catch(IpcError e)
        {
            _logger.LogError(e, "Unable to craft items");
            return false;
        }
    }

    private bool _enduranceStatusErrorLogged;

    /// <summary>
    ///     This ignores crafting lists, but we can't create/use those.
    /// </summary>
    /// <remarks>只在框架執行緒呼叫；會被每幀呼叫，所以問不到時只在每段失敗期間的第一次寫 Error。</remarks>
    public bool IsCrafting()
    {
        try
        {
            bool crafting = _getEnduranceStatus.InvokeFunc();
            _enduranceStatusErrorLogged = false;
            return crafting;
        }
        catch(IpcError e)
        {
            if (!_enduranceStatusErrorLogged)
            {
                _enduranceStatusErrorLogged = true;
                _logger.LogError(e, "Unable to check for Artisan endurance status (logged once until it works again)");
            }

            return false;
        }
    }
}

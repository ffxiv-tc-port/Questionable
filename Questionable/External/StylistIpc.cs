using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using Microsoft.Extensions.Logging;
namespace Questionable.External;

internal sealed class StylistIpc
{
    private readonly ICallGateSubscriber<bool> _isBusy;
    private readonly ILogger<AutomatonIpc> _logger;
    private readonly ICallGateSubscriber<bool?, bool?, object?> _updateGearset; //bool? moveItemsFromInventory, bool? shouldEquip
    private bool _loggedIpcError;
    private bool _loggedIsBusyError;

    public StylistIpc(IDalamudPluginInterface pluginInterface, ILogger<AutomatonIpc> logger)
    {
        _logger = logger;
        _updateGearset = pluginInterface.GetIpcSubscriber<bool?, bool?, object?>("Stylist.UpdateCurrentGearsetEx");
        _isBusy = pluginInterface.GetIpcSubscriber<bool>("Stylist.IsBusy");
    }

    /// <summary>
    /// Whether Stylist is currently updating the gearset. Reports "not busy" when Stylist cannot be
    /// reached: this is read every frame by <c>EquipRecommended</c>, and answering "busy" would leave
    /// that task StillRunning forever. Falling through to <see cref="UpdateGearset"/> instead hits
    /// the already-guarded path, which logs and moves on.
    /// </summary>
    public bool IsBusy
    {
        get
        {
            try
            {
                return _isBusy.InvokeFunc();
            }
            catch(IpcError e)
            {
                if (!_loggedIsBusyError)
                {
                    _loggedIsBusyError = true;
                    _logger.LogWarning(e, "Could not query stylist busy state, probably not installed; assuming not busy");
                }

                return false;
            }
        }
    }

    public void UpdateGearset()
    {
        try
        {
            _updateGearset.InvokeAction(true, true);
        }
        catch(IpcError e)
        {
            if (!_loggedIpcError)
            {
                _loggedIpcError = true;
                _logger.LogWarning(e, "Could not query stylist to update gearset, probably not installed");
            }
        }
    }
}

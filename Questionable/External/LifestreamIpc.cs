using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using Microsoft.Extensions.Logging;
using Questionable.Model.Common;
using System;
namespace Questionable.External;

internal sealed class LifestreamIpc(IDalamudPluginInterface pluginInterface, ILogger<LifestreamIpc> logger)
{
    private readonly ICallGateSubscriber<string, bool> _aethernetTeleport =
        pluginInterface.GetIpcSubscriber<string, bool>("Lifestream.AethernetTeleport");
    private readonly ICallGateSubscriber<uint, bool> _aethernetTeleportById =
        pluginInterface.GetIpcSubscriber<uint, bool>("Lifestream.AethernetTeleportById");
    private readonly ICallGateSubscriber<uint, bool> _aethernetTeleportByPlaceNameId =
        pluginInterface.GetIpcSubscriber<uint, bool>("Lifestream.AethernetTeleportByPlaceNameId");
    private readonly ICallGateSubscriber<bool> _aethernetTeleportToFirmament =
        pluginInterface.GetIpcSubscriber<bool>("Lifestream.AethernetTeleportToFirmament");
    private readonly ICallGateSubscriber<bool> _isBusy =
        pluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy");
    private readonly ICallGateSubscriber<object> _abort =
        pluginInterface.GetIpcSubscriber<object>("Lifestream.Abort");
    private readonly ILogger<LifestreamIpc> _logger = logger;
    private bool _loggedIsBusyError;

    /// <summary>
    /// Whether Lifestream is currently teleporting. Reports "not busy" when Lifestream cannot be
    /// reached: this is read every frame by <c>WaitLifestream</c>, and answering "busy" would leave
    /// that task StillRunning forever, hanging the quest queue on a plugin that is not responding.
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
                    _logger.LogWarning(e, "Could not query lifestream busy state, probably not installed; assuming not busy");
                }

                return false;
            }
        }
    }

    /// <summary>
    /// 請 Lifestream 中止它目前的傳送／移動。Lifestream 沒載入、端點不在或對方擲例外時只寫一行記錄就略過：
    /// 這支在停止路徑上，不可以讓別的插件的狀況打斷後續的停止步驟。
    /// </summary>
    public void Abort()
    {
        try
        {
            _abort.InvokeAction();
            _logger.LogInformation("已請 Lifestream 中止傳送（Questionable 停止時正在等它）");
        }
        catch (IpcError e)
        {
            _logger.LogInformation("Questionable 停止時想請 Lifestream 中止傳送，但端點不可用（{Error}），略過", e.GetType().Name);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Questionable 停止時呼叫 Lifestream.Abort 失敗，略過");
        }
    }

    public bool Teleport(string destination)
    {
        _logger.LogInformation($"Teleporting to vague string '{destination}'");
        return _aethernetTeleport.InvokeFunc(destination);
    }

    public bool Teleport(EAetheryteLocation aetheryteLocation)
    {
        _logger.LogInformation("Teleporting to '{Name}'", aetheryteLocation);
        return aetheryteLocation switch
        {
            EAetheryteLocation.IshgardFirmament => _aethernetTeleportToFirmament.InvokeFunc(),
            EAetheryteLocation.FirmamentMendicantsCourt => _aethernetTeleportByPlaceNameId.InvokeFunc(3436),
            EAetheryteLocation.FirmamentMattock => _aethernetTeleportByPlaceNameId.InvokeFunc(3473),
            EAetheryteLocation.FirmamentNewNest => _aethernetTeleportByPlaceNameId.InvokeFunc(3475),
            EAetheryteLocation.FirmanentSaintRoellesDais => _aethernetTeleportByPlaceNameId.InvokeFunc(3474),
            EAetheryteLocation.FirmamentFeatherfall => _aethernetTeleportByPlaceNameId.InvokeFunc(3525),
            EAetheryteLocation.FirmamentHoarfrostHall => _aethernetTeleportByPlaceNameId.InvokeFunc(3528),
            EAetheryteLocation.FirmamentWesternRisensongQuarter => _aethernetTeleportByPlaceNameId.InvokeFunc(3646),
            EAetheryteLocation.FIrmamentEasternRisensongQuarter => _aethernetTeleportByPlaceNameId.InvokeFunc(3645),
            EAetheryteLocation.None => throw new ArgumentOutOfRangeException(nameof(aetheryteLocation)),
            var _ => _aethernetTeleportById.InvokeFunc((uint)aetheryteLocation)
        };
    }
}

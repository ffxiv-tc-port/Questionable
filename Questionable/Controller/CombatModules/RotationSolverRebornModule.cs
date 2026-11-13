using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using System;
namespace Questionable.Controller.CombatModules;

internal sealed class RotationSolverRebornModule
(
    ILogger<RotationSolverRebornModule> logger,
    IDalamudPluginInterface pluginInterface,
    Configuration configuration) : ICombatModule, IDisposable
{
    private readonly ICallGateSubscriber<StateCommandType, object> _changeOperationMode =
        pluginInterface.GetIpcSubscriber<StateCommandType, object>("RotationSolverReborn.ChangeOperatingMode");
    private readonly Configuration _configuration = configuration;
    private readonly ILogger<RotationSolverRebornModule> _logger = logger;
    private readonly ICallGateSubscriber<string, object> _test = pluginInterface.GetIpcSubscriber<string, object>("RotationSolverReborn.Test");

    /// <summary>這次戰鬥是本插件把 RSR 切到 Manual 的；卸載時只在這種情況才送 Off。</summary>
    private bool _started;

    private bool _loggedModeNotRestored;

    public bool CanHandleFight(CombatController.CombatData combatData)
    {
        if (_configuration.General.CombatModule != Configuration.ECombatModule.RotationSolverReborn)
        {
            return false;
        }

        try
        {
            _test.InvokeAction("Validate RSR is callable from Questionable");
            return true;
        }
        catch(IpcError)
        {
            return false;
        }
    }

    public bool Start(CombatController.CombatData combatData)
    {
        try
        {
            if (!_loggedModeNotRestored)
            {
                // RSR 沒有讀目前模式的端點 ⇒ 開始前無從記下原狀態，結束時照舊送 Off。
                _loggedModeNotRestored = true;
                _logger.LogInformation(
                    "RotationSolverReborn has no IPC to read its current mode, so it is set to Off after combat instead of returning to the previous mode");
            }

            _changeOperationMode.InvokeAction(StateCommandType.Manual);
            _started = true;
            return true;
        }
        catch(IpcError e)
        {
            _logger.LogWarning(e, "Could not start combat");
            return false;
        }
    }

    public bool Stop()
    {
        _started = false;
        if (!_changeOperationMode.HasAction)
        {
            return true;
        }

        try
        {
            _changeOperationMode.InvokeAction(StateCommandType.Off);
            return true;
        }
        catch(IpcError e)
        {
            _logger.LogWarning(e, "Could not turn off combat");
            return false;
        }
    }

    public void Update(IGameObject gameObject)
    {
    }

    public bool CanAttack(IBattleNpc target)
    {
        return true;
    }

    public void Dispose()
    {
        if (_started)
        {
            Stop();
        }
    }

    [PublicAPI]
    private enum StateCommandType : byte
    {
        // 數值需與台服可用的 RotationSolverReborn 7.3.5 StateCommandType 契約同步
        // （該版沒有 TargetOnly，Manual 位移到 2）。
        Off = 0,
        Auto = 1,
        Manual = 2,
        AutoDuty = 3,
    }
}

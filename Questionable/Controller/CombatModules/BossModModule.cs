using Dalamud.Game.ClientState.Objects.Types;
using Microsoft.Extensions.Logging;
using Questionable.External;
using System;
namespace Questionable.Controller.CombatModules;

internal sealed class BossModModule
(
    ILogger<BossModModule> logger,
    BossModIpc bossModIpc,
    Configuration configuration) : ICombatModule, IDisposable
{
    private readonly BossModIpc _bossModIpc = bossModIpc;
    private readonly Configuration _configuration = configuration;
    private readonly ILogger<BossModModule> _logger = logger;

    public bool CanHandleFight(CombatController.CombatData combatData)
    {
        if (_configuration.General.CombatModule != Configuration.ECombatModule.BossMod)
        {
            return false;
        }

        return _bossModIpc.IsSupported();
    }

    public bool Start(CombatController.CombatData combatData)
    {
        // BossModIpc logs the underlying IPC error itself and reports failure via the return
        // value, so that a missing/incompatible BossMod degrades gracefully instead of
        // aborting the whole task queue. Returning false here keeps the honest signal that
        // combat is *not* being handled.
        if (_bossModIpc.SetPreset(BossModIpc.EPreset.Overworld))
        {
            return true;
        }

        _logger.LogWarning("Could not start combat");
        return false;
    }

    public bool Stop()
    {
        if (_bossModIpc.ClearPreset())
        {
            return true;
        }

        _logger.LogWarning("Could not turn off combat");
        return false;
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
        Stop();
    }
}

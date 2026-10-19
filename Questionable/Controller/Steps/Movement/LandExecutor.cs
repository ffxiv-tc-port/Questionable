using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Microsoft.Extensions.Logging;
using System;
namespace Questionable.Controller.Steps.Movement;

internal sealed class LandExecutor(IObjectTable objectTable, ICondition condition, ILogger<LandExecutor> logger)
    : TaskExecutor<LandTask>
{
    private DateTime _continueAt;
    private bool _landing;

    protected override bool Start()
    {
        if (!condition[ConditionFlag.InFlight])
        {
            logger.LogInformation("Not flying, not attempting to land");
            return false;
        }

        _landing = AttemptLanding();
        _continueAt = DateTime.Now.AddSeconds(0.25);
        return true;
    }

    public override ETaskResult Update()
    {
        if (DateTime.Now < _continueAt)
        {
            return ETaskResult.StillRunning;
        }

        if (condition[ConditionFlag.InFlight])
        {
            if (!_landing)
            {
                _landing = AttemptLanding();
                _continueAt = DateTime.Now.AddSeconds(0.25);
            }

            return ETaskResult.StillRunning;
        }

        return ETaskResult.TaskComplete;
    }

    private unsafe bool AttemptLanding()
    {
        Character* character = (Character*)(objectTable[0]?.Address ?? 0);
        if (character != null)
        {
            if (ActionManager.Instance()->GetActionStatus(ActionType.GeneralAction, 23) == 0)
            {
                logger.LogInformation("Attempting to land");
                return ActionManager.Instance()->UseAction(ActionType.GeneralAction, 23);
            }
        }

        return false;
    }

    /// <summary>不建任務、就地送出一次降落。</summary>
    /// <remarks>給 <c>MovementController</c> 的「先降落再重試」用：它在 framework 執行緒上，
    /// 只需要把動作送出去，有沒有降落成功由 <c>ConditionFlag.InFlight</c> 翻面來判定。</remarks>
    internal static unsafe bool TryLanding()
    {
        if (ActionManager.Instance()->GetActionStatus(ActionType.GeneralAction, 23) != 0)
        {
            return false;
        }

        return ActionManager.Instance()->UseAction(ActionType.GeneralAction, 23);
    }

    public override bool ShouldInterruptOnDamage()
    {
        return false;
    }
}

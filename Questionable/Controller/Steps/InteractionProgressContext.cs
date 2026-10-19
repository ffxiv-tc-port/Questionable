using FFXIVClientStructs.FFXIV.Client.Game;
using System;
namespace Questionable.Controller.Steps;

internal sealed class InteractionProgressContext
{
    private bool _firstUpdateDone;

    // 建立時沒觸發新施放：詠唱欄位還是建立當下那一組時，當成上一次施放已完成，
    // 否則上一次被打斷的殘值會讓這次互動立刻被判成中斷。只在判斷時這樣看，不寫遊戲記憶體。
    private readonly bool _hasStaleCast;
    private readonly float _staleCastElapsed;
    private readonly float _staleCastTotal;
    private bool _castChangedSinceCreate;

    private InteractionProgressContext(bool checkSequence, int currentSequence, float castElapsed, float castTotal)
    {
        CheckSequence = checkSequence;
        CurrentSequence = currentSequence;
        _hasStaleCast = !checkSequence;
        _staleCastElapsed = castElapsed;
        _staleCastTotal = castTotal;
    }
    public bool CheckSequence { get; private set; }
    public int CurrentSequence { get; private set; }

    public static unsafe InteractionProgressContext Create(bool checkSequence)
    {
        ActionManager* actionManager = ActionManager.Instance();
        return new(checkSequence, actionManager->LastUsedActionSequence,
            actionManager->CastTimeElapsed, actionManager->CastTimeTotal);
    }

    /// <remarks>欄位一旦和建立當下不同（遊戲開始了新的詠唱）就一律回真值，之後回到同一組數字也不再當殘值。</remarks>
    private unsafe (float Elapsed, float Total) ReadCastTime()
    {
        ActionManager* actionManager = ActionManager.Instance();
        float elapsed = actionManager->CastTimeElapsed;
        float total = actionManager->CastTimeTotal;
        if (_hasStaleCast && !_castChangedSinceCreate)
        {
            if (elapsed == _staleCastElapsed && total == _staleCastTotal)
            {
                return (total, total);
            }

            _castChangedSinceCreate = true;
        }

        return (elapsed, total);
    }

    private static unsafe (bool, InteractionProgressContext?) FromActionUseInternal(Func<bool> func)
    {
        int oldSequence = ActionManager.Instance()->LastUsedActionSequence;
        if (!func())
        {
            return (false, null);
        }
        int newSequence = ActionManager.Instance()->LastUsedActionSequence;
        if (oldSequence == newSequence)
        {
            return (true, null);
        }
        return (true, Create(true));
    }

    public static InteractionProgressContext? FromActionUse(Func<bool> func)
    {
        return FromActionUseInternal(func).Item2;
    }

    public static InteractionProgressContext? FromActionUseOrDefault(Func<bool> func)
    {
        (bool, InteractionProgressContext?) result = FromActionUseInternal(func);
        if (!result.Item1)
        {
            return null;
        }
        return result.Item2 ?? Create(false);
    }

    public unsafe void Update()
    {
        if (!_firstUpdateDone)
        {
            int lastSequence = ActionManager.Instance()->LastUsedActionSequence;
            if (!CheckSequence && lastSequence > CurrentSequence)
            {
                CheckSequence = true;
                CurrentSequence = lastSequence;
            }

            _firstUpdateDone = true;
        }
    }

    public unsafe bool WasSuccessful()
    {
        // 先讀：每次判斷都要更新「欄位變過沒」，不能被下面的提早 return 跳過。
        (float elapsed, float total) = ReadCastTime();
        if (CheckSequence)
        {
            if (CurrentSequence != ActionManager.Instance()->LastUsedActionSequence ||
                CurrentSequence != ActionManager.Instance()->LastHandledActionSequence)
            {
                return false;
            }
        }

        return elapsed > 0 && Math.Abs(elapsed - total) < 0.001f;
    }

    public unsafe bool WasInterrupted()
    {
        (float elapsed, float total) = ReadCastTime();
        if (CheckSequence)
        {
            if (CurrentSequence == ActionManager.Instance()->LastHandledActionSequence &&
                CurrentSequence == ActionManager.Instance()->LastUsedActionSequence)
            {
                return false;
            }
        }

        return elapsed == 0 && total > 0;
    }

    public override string ToString()
    {
        return $"IPCtx({(CheckSequence ? CurrentSequence : "-")} - {WasSuccessful()}, {WasInterrupted()})";
    }
}

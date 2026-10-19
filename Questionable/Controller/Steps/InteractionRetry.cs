using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Logging;
using System;

namespace Questionable.Controller.Steps;

/// <summary>
/// 送出互動後只看遊戲狀態決定要不要重送（<c>InteractWithObject</c> 的回傳值沒有語意，不能當成敗）。
/// 每一發之後等遊戲閒置滿 <see cref="ResponseTimeout"/> 步驟仍未完成才重送，送滿 <see cref="MaxAttempts"/> 次仍未完成就擲 <see cref="TaskException"/>。
/// </summary>
/// <remarks>只在框架執行緒使用；每個任務執行器自己一份（執行器是 transient）。遊戲忙碌（<see cref="IsGameBusy"/>）時一律不送。</remarks>
internal sealed class InteractionRetry
{
    public static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(3);
    public const int MaxAttempts = 3;

    private static readonly TimeSpan BusyLogAfter = TimeSpan.FromSeconds(10);

    private static readonly ConditionFlag[] BusyFlags =
    [
        ConditionFlag.OccupiedInEvent,
        ConditionFlag.OccupiedInQuestEvent,
        ConditionFlag.OccupiedInCutSceneEvent,
        ConditionFlag.WatchingCutscene,
        ConditionFlag.WatchingCutscene78,
        ConditionFlag.BetweenAreas,
        ConditionFlag.BetweenAreas51,
        ConditionFlag.Casting,
        ConditionFlag.Casting87,
        ConditionFlag.Gathering,
    ];

    private int _attempts;
    private bool _reacted;
    private DateTime _idleSince = DateTime.MinValue;
    private DateTime _busySince = DateTime.MinValue;
    private bool _busyLogged;

    public int Attempts => _attempts;

    /// <summary>遊戲正在處理事件／過場／詠唱／換區之類，這時送互動會被忽略。</summary>
    public static bool IsGameBusy(ICondition condition)
    {
        foreach (ConditionFlag flag in BusyFlags)
        {
            if (condition[flag])
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>呼叫端看到步驟專屬的反應（物件消失、變得無法選取之類）。</summary>
    public void OnReaction() => _reacted = true;

    /// <summary>每一次嘗試送出之後呼叫（含因距離等前置檢查沒送出的那種，由呼叫端決定算不算）。</summary>
    public void OnSent() => OnSent(DateTime.Now);

    internal void OnSent(DateTime now)
    {
        _attempts++;
        _reacted = false;
        _idleSince = now;
    }

    /// <returns><see langword="true"/>＝現在該送：第一次，或上一發之後遊戲已閒置滿 <see cref="ResponseTimeout"/> 而步驟仍未完成。</returns>
    /// <exception cref="TaskException">已送滿 <see cref="MaxAttempts"/> 次步驟仍未完成。</exception>
    public bool ShouldSend(ICondition condition, ILogger logger, object target) =>
        ShouldSend(IsGameBusy(condition), DateTime.Now, logger, target);

    internal bool ShouldSend(bool busy, DateTime now, ILogger logger, object target)
    {
        if (busy)
        {
            if (_attempts > 0)
            {
                _reacted = true;
                _idleSince = now;
            }
            else
            {
                if (_busySince == DateTime.MinValue)
                {
                    _busySince = now;
                }
                else if (!_busyLogged && now - _busySince >= BusyLogAfter)
                {
                    _busyLogged = true;
                    logger.LogInformation("Still waiting for the game to be free before interacting with {Target}", target);
                }
            }

            return false;
        }

        _busySince = DateTime.MinValue;
        if (_attempts == 0)
        {
            return true;
        }

        if (now - _idleSince < ResponseTimeout)
        {
            return false;
        }

        string outcome = _reacted ? "the game reacted but the step is not done" : "no reaction from the game";
        if (_attempts >= MaxAttempts)
        {
            logger.LogInformation("Giving up on interacting with {Target} after {Attempts} attempts: {Outcome}",
                target, _attempts, outcome);
            throw new TaskException($"Interaction with {target} did not work after {_attempts} attempts ({outcome})");
        }

        logger.LogInformation("Interaction with {Target} not done {Seconds:F0}s after attempt {Attempt}/{Max} ({Outcome}), trying again",
            target, ResponseTimeout.TotalSeconds, _attempts, MaxAttempts, outcome);
        return true;
    }
}

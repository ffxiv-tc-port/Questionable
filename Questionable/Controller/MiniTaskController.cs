using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Questionable.Controller.Steps;
using Questionable.Controller.Steps.Interactions;
using Questionable.Controller.Steps.Shared;
using Questionable.Data;
using Questionable.Functions;
using Questionable.Model.Questing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Mount = Questionable.Controller.Steps.Common.Mount;

namespace Questionable.Controller;

internal abstract class MiniTaskController<T> : IDisposable
{
    private readonly Regex _actionCanceledText;
    private readonly string _cantExecuteDueToStatusText;

    private readonly IChatGui _chatGui;
    private readonly ICondition _condition;
    private readonly string _eventCanceledText;
    private readonly InterruptHandler _interruptHandler;
    private readonly ILogger<T> _logger;
    private readonly IServiceProvider _serviceProvider;
    protected readonly TaskQueue _taskQueue = new();

    /// <summary>
    /// 連續幾次「中斷後重試」都沒有真正的進度（見 QuestController.CheckAutoRefreshCondition）。
    /// InterruptQueueWithCombat／InterruptWithoutCombat 每插一次 WaitAtEnd.WaitDelay() 就 +1；
    /// 偵測到真正進度時歸零。用來讓卡住偵測能看穿「每次都插同一種 WaitAtEnd 重試緩衝」的無限重試迴圈——
    /// 這種迴圈本身一直讓 CurrentTask 落在 WaitAtEnd 命名空間裡，若排除規則沒有上限，
    /// 卡住計時器永遠會在累積到門檻前被重置，觸發不了。
    /// </summary>
    protected int ConsecutiveInterruptions;

    /// <summary>錯誤訊息觸發的中斷所插入的重試緩衝；它還在佇列頭或正在跑時，同一波錯誤訊息不再重插。</summary>
    private WaitAtEnd.WaitDelay? _pendingToastRetry;

    private bool _toastSuppressionLogged;

    private const int MaxLoggedTasksAfterInterruption = 12;

    protected MiniTaskController(IChatGui chatGui, ICondition condition, IServiceProvider serviceProvider,
        InterruptHandler interruptHandler, IDataManager dataManager, ILogger<T> logger)
    {
        _chatGui = chatGui;
        _logger = logger;
        _serviceProvider = serviceProvider;
        _interruptHandler = interruptHandler;
        _condition = condition;

        _eventCanceledText = DataManagerAdapter.GetString<LogMessage>(dataManager, 1318, x => x.Text)!;
        _actionCanceledText = DataManagerAdapter.GetRegex<LogMessage>(dataManager, 1314, x => x.Text)!;
        _cantExecuteDueToStatusText = DataManagerAdapter.GetString<LogMessage>(dataManager, 7728, x => x.Text)!;
        _interruptHandler.Interrupted += HandleInterruption;
    }

    public virtual void Dispose()
    {
        _interruptHandler.Interrupted -= HandleInterruption;
    }

    protected virtual void UpdateCurrentTask()
    {
        if (_taskQueue.CurrentTaskExecutor == null)
        {
            if (_taskQueue.TryDequeue(out ITask? upcomingTask))
            {
                try
                {
                    _logger.LogInformation("Starting task {TaskName}", upcomingTask.ToString());
                    ITaskExecutor taskExecutor =
                        _serviceProvider.GetRequiredKeyedService<ITaskExecutor>(upcomingTask.GetType());
                    if (taskExecutor.Start(upcomingTask))
                    {
                        _taskQueue.CurrentTaskExecutor = taskExecutor;
                        return;
                    }
                    else
                    {
                        _logger.LogTrace("Task {TaskName} was skipped", upcomingTask.ToString());
                        return;
                    }
                }
                catch(ManualActionRequiredException e)
                {
                    StopForManualAction(upcomingTask, e);
                    return;
                }
                catch(Exception e)
                {
                    _logger.LogError(e, "Failed to start task {TaskName}", upcomingTask.ToString());
                    _chatGui.PrintError(
                        $"Failed to start task '{upcomingTask}', please check /xllog for details.", CommandHandler.MessageTag, CommandHandler.TagColor);
                    StopDueToFailure("Task failed to start");
                    return;
                }
            }
            else
            {
                return;
            }
        }

        ETaskResult result;
        try
        {
            if (_taskQueue.CurrentTaskExecutor.WasInterrupted())
            {
                InterruptQueueWithCombat();
                return;
            }

            result = _taskQueue.CurrentTaskExecutor.Update();
        }
        catch(ManualActionRequiredException e)
        {
            StopForManualAction(_taskQueue.CurrentTaskExecutor.CurrentTask, e);
            return;
        }
        catch(Exception e)
        {
            _logger.LogError(e, "Failed to update task {TaskName}",
                _taskQueue.CurrentTaskExecutor.CurrentTask.ToString());
            _chatGui.PrintError(
                $"Failed to update task '{_taskQueue.CurrentTaskExecutor.CurrentTask}', please check /xllog for details.", CommandHandler.MessageTag, CommandHandler.TagColor);
            StopDueToFailure("Task failed to update");
            return;
        }

        switch (result)
        {
            case ETaskResult.StillRunning:
                return;

            case ETaskResult.SkipRemainingTasksForStep:
                _logger.LogInformation("{Task} → {Result}, skipping remaining tasks for step",
                    _taskQueue.CurrentTaskExecutor.CurrentTask, result);
                _taskQueue.CurrentTaskExecutor = null;

                while(_taskQueue.TryDequeue(out ITask? nextTask))
                {
                    if (nextTask is ILastTask or Gather.SkipMarker)
                    {
                        ITaskExecutor taskExecutor =
                            _serviceProvider.GetRequiredKeyedService<ITaskExecutor>(nextTask.GetType());
                        taskExecutor.Start(nextTask);
                        _taskQueue.CurrentTaskExecutor = taskExecutor;
                        return;
                    }
                }

                return;

            case ETaskResult.TaskComplete:
            case ETaskResult.CreateNewTasks:
                _logger.LogInformation("{Task} → {Result}, remaining tasks: {RemainingTaskCount}",
                    _taskQueue.CurrentTaskExecutor.CurrentTask, result, _taskQueue.RemainingTasks.Count());

                OnTaskComplete(_taskQueue.CurrentTaskExecutor.CurrentTask);

                if (result == ETaskResult.CreateNewTasks && _taskQueue.CurrentTaskExecutor is IExtraTaskCreator extraTaskCreator)
                {
                    // 漏出去的例外會讓執行器留在原地、下一幀再回 CreateNewTasks 再擲一次（每幀一次），
                    // 所以任何例外都比照 Update 失敗：記錄、提示、停下。
                    List<ITask> extraTasks;
                    try
                    {
                        extraTasks = extraTaskCreator.CreateExtraTasks().ToList();
                    }
                    catch(ManualActionRequiredException e)
                    {
                        StopForManualAction(extraTaskCreator.CurrentTask, e);
                        return;
                    }
                    catch(Exception e)
                    {
                        _logger.LogError(e, "Failed to create extra tasks for {TaskName}", extraTaskCreator.CurrentTask.ToString());
                        _chatGui.PrintError(
                            $"Failed to update task '{extraTaskCreator.CurrentTask}', please check /xllog for details.", CommandHandler.MessageTag, CommandHandler.TagColor);
                        StopDueToFailure("Task failed to create extra tasks");
                        return;
                    }

                    _taskQueue.EnqueueAll(extraTasks);
                }

                _taskQueue.CurrentTaskExecutor = null;

                // handled in next update
                return;

            case ETaskResult.NextStep:
                _logger.LogInformation("{Task} → {Result}", _taskQueue.CurrentTaskExecutor.CurrentTask, result);

                ILastTask lastTask = (ILastTask)_taskQueue.CurrentTaskExecutor.CurrentTask;
                _taskQueue.CurrentTaskExecutor = null;

                OnNextStep(lastTask);
                return;

            case ETaskResult.End:
                _logger.LogInformation("{Task} → {Result}", _taskQueue.CurrentTaskExecutor.CurrentTask, result);
                _taskQueue.CurrentTaskExecutor = null;
                Stop("Task end");
                return;
        }
    }

    /// <summary>
    /// 任務卡在要玩家自己處理的地方：聊天視窗只印那句說明（不印「Failed to update task」），再走失敗停下。
    /// </summary>
    private void StopForManualAction(ITask task, ManualActionRequiredException e)
    {
        _logger.LogError("Task {TaskName} needs manual action, stopping: {Reason}", task.ToString(), e.Message);
        _chatGui.PrintError(e.ChatMessage, CommandHandler.MessageTag, CommandHandler.TagColor);
        StopDueToFailure($"Needs manual action: {e.Message}");
    }

    protected virtual void OnTaskComplete(ITask task)
    {
    }

    protected virtual void OnNextStep(ILastTask task)
    {
    }

    public abstract void Stop(string label);

    /// <summary>
    /// 因為「任務跑不下去」而停止（例外、資料不支援之類），跟使用者自己按停止、或流程正常結束不一樣。
    /// </summary>
    /// <remarks>
    /// 📌 預設行為與 <see cref="Stop"/> 完全相同，衍生類別可以覆寫來多做一件事
    /// （<see cref="QuestController"/> 會在這裡請 TataruPraise 喊一句「需要幫忙」）。
    /// <see cref="GatheringController"/> 沿用預設，行為不變。
    /// </remarks>
    protected virtual void StopDueToFailure(string label)
    {
        Stop(label);
    }

    /// <summary>
    /// 每次插入 WaitAtEnd.WaitDelay() 重試緩衝之後呼叫一次（InterruptQueueWithCombat／
    /// InterruptWithoutCombat 各自的呼叫點），帶目前的連續次數。預設不做事；
    /// <see cref="QuestController"/> 覆寫來在重試次數過多時做主動回復（見該類別的實作與註解）。
    /// </summary>
    protected virtual void OnRepeatedInterruption(int consecutiveCount)
    {
    }

    public virtual IList<string> GetRemainingTaskNames()
    {
        return _taskQueue.RemainingTasks.Select(x => x.ToString() ?? "?").ToList();
    }

    public void InterruptQueueWithCombat()
    {
        _logger.LogWarning("Interrupted, attempting to resolve (if in combat)");
        ConsecutiveInterruptions++;
        OnRepeatedInterruption(ConsecutiveInterruptions);
        if (_condition[ConditionFlag.InCombat])
        {
            List<ITask> tasks = [];
            if (_condition[ConditionFlag.Mounted])
            {
                tasks.Add(new Mount.UnmountTask());
            }

            tasks.Add(Combat.Factory.CreateTask(null, -1, false, EEnemySpawnType.QuestInterruption, [], [], [], null));
            tasks.Add(new WaitAtEnd.WaitDelay());
            _taskQueue.InterruptWith(tasks);
        }
        else
        {
            _taskQueue.InterruptWith([new WaitAtEnd.WaitDelay()]);
        }

        LogTasksAfterInterruption();
    }

    /// <summary>
    /// 收到「劇情被中斷。」／「目前狀態下無法進行該操作。」時，插一個重試緩衝進佇列、把先前的工作重做一次。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>閘門：佇列空的時候什麼都不做。</b>那兩則錯誤訊息是<b>全域</b>的，而 <see cref="OnErrorToast"/>
    /// 是外掛一載入就掛上去的、不看自動化有沒有在跑。少了這道閘門，閒置時一則與我們無關的錯誤訊息就會把
    /// <c>WaitAtEnd.WaitDelay</c> 插進空佇列，<c>IsRunning</c> 跟著成立 ⇒ 使用者沒按開始，
    /// Questionable 卻會去按 Yes/No、選單那些視窗，還會去拿 YesAlready 的壓制租約。
    /// ⚠️ 對真的在跑的情況是 no-op：有工作在跑時 <c>AllTasksComplete</c> 必為 <see langword="false"/>。
    /// </remarks>
    private void InterruptWithoutCombat(string message)
    {
        if (_taskQueue.AllTasksComplete)
        {
            return;
        }

        // 任務戰鬥打完之後也不重做：重做會把「找 NPC 對話」排回去，等於自動再進一次副本。
        if (_taskQueue.CurrentTaskExecutor is not (SinglePlayerDuty.WaitSinglePlayerDutyExecutor
            or SinglePlayerDuty.WaitDutyOutcomeExecutor))
        {
            // 同一個原因常常每幀都彈一次（例如別的插件在任務事件中一直嘗試施放）；
            // 重試緩衝還沒跑完之前再插一次只會讓佇列與記錄一起線性變長，重做的內容完全一樣。
            if (IsToastRetryPending())
            {
                if (!_toastSuppressionLogged)
                {
                    _toastSuppressionLogged = true;
                    _logger.LogInformation("Ignoring repeated interruption message(s) until the pending retry has run: {Message}", message);
                }

                return;
            }

            _logger.LogWarning("Interrupted, attempting to redo previous tasks (not in combat): {Message}", message);
            ConsecutiveInterruptions++;
            OnRepeatedInterruption(ConsecutiveInterruptions);

            WaitAtEnd.WaitDelay retryDelay = new() { RedoOnInterrupt = false };
            _taskQueue.InterruptWith([retryDelay]);
            _pendingToastRetry = retryDelay;
            _toastSuppressionLogged = false;
            LogTasksAfterInterruption();
        }
    }

    private bool IsToastRetryPending()
    {
        WaitAtEnd.WaitDelay? pending = _pendingToastRetry;
        if (pending == null)
        {
            return false;
        }

        if (ReferenceEquals(_taskQueue.CurrentTaskExecutor?.CurrentTask, pending) ||
            (_taskQueue.TryPeek(out ITask? head) && ReferenceEquals(head, pending)))
        {
            return true;
        }

        _pendingToastRetry = null;
        return false;
    }

    private void LogTasksAfterInterruption()
    {
        _logger.LogInformation("Remaining tasks after interruption:");
        int count = 0;
        foreach(ITask task in _taskQueue.RemainingTasks)
        {
            if (++count <= MaxLoggedTasksAfterInterruption)
            {
                _logger.LogInformation("- {TaskName}", task);
            }
        }

        if (count > MaxLoggedTasksAfterInterruption)
        {
            _logger.LogInformation("- ... {Count} more", count - MaxLoggedTasksAfterInterruption);
        }
    }

    public void OnErrorToast(ref SeString message, ref bool isHandled)
    {
        if (_taskQueue.CurrentTaskExecutor is IToastAware toastAware)
        {
            if (toastAware.OnErrorToast(message))
            {
                isHandled = true;
            }
        }

        if (!isHandled)
        {
            if (_actionCanceledText.IsMatch(message.TextValue) &&
                !_condition[ConditionFlag.InFlight] &&
                _taskQueue.CurrentTaskExecutor?.ShouldInterruptOnDamage() == true)
            {
                InterruptQueueWithCombat();
            }
            else if (GameFunctions.GameStringEquals(_cantExecuteDueToStatusText, message.TextValue) ||
                     GameFunctions.GameStringEquals(_eventCanceledText, message.TextValue))
            {
                InterruptWithoutCombat(message.TextValue);
            }
        }
    }

    protected virtual void HandleInterruption(object? sender, EventArgs e)
    {
        if (!_condition[ConditionFlag.InFlight] &&
            _taskQueue.CurrentTaskExecutor?.ShouldInterruptOnDamage() == true)
        {
            InterruptQueueWithCombat();
        }
    }
}

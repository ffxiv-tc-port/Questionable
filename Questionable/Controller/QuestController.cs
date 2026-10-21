using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Gui.Toast;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using ECommons.ExcelServices;
using ECommons.GameFunctions;
using ECommons.LanguageHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Microsoft.Extensions.Logging;
using Questionable.Controller.Steps;
using Questionable.Controller.Steps.Interactions;
using Questionable.Controller.Steps.Shared;
using Questionable.Controller.Utils;
using Questionable.Data;
using Questionable.External;
using Questionable.Functions;
using Questionable.Model;
using Questionable.Model.Questing;
using Questionable.Utils;
using Questionable.Windows.ConfigComponents;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading;
using Quest = Questionable.Model.Quest;

namespace Questionable.Controller;

internal sealed class QuestController : MiniTaskController<QuestController>
{
    public delegate void AutomationTypeChangedEventHandler(object sender, EAutomationType e);

    public enum EAutomationType
    {
        Manual,
        Automatic,
        GatheringOnly,
        SingleQuestA,
        SingleQuestB
    }

    public enum ECurrentQuestType
    {
        Normal,
        Next,
        Gathering,
        Simulated
    }

    private const char ClipboardSeparator = ';';
    private readonly AlliedSocietyQuestFunctions _alliedSocietyQuestFunctions;
    private readonly IChatGui _chatGui;
    private readonly IClientState _clientState;

    /// <summary>只用來把聊天輸出釘回 framework 執行緒，見 <see cref="ChatGuiExtensions"/>。</summary>
    private readonly IFramework _framework;
    private readonly CombatController _combatController;
    private readonly ICondition _condition;
    private readonly Configuration _configuration;
    //private readonly IPlayerState _playerState;
    private readonly GameFunctions _gameFunctions;
    private readonly GatheringController _gatheringController;
    private readonly HighlightObject _highlightObject;
    private readonly IKeyState _keyState;
    private readonly ILogger<QuestController> _logger;
    private readonly MovementController _movementController;
    private readonly IObjectTable _objectTable;

    private readonly object _progressLock = new();

    /// <summary>
    /// 目前這條執行緒「持有 <see cref="_progressLock"/> 期間」要把副作用收到哪一份清單。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>刻意是 <c>[ThreadStatic]</c></b>：<see cref="Stop(string, bool)"/>／
    /// <see cref="SetNextQuest"/> 這些方法同時也被 UI（繪製執行緒）與 IPC（呼叫端的執行緒）
    /// 呼叫，而那些呼叫<b>沒有</b>持鎖。做成一般欄位的話，framework 執行緒在鎖裡設好的清單
    /// 會被別條執行緒看見，於是別人的記錄被塞進我們的 <c>List</c>（裸集合，並行插入弄壞的是
    /// 集合本身），而且會在錯誤的時間點才寫出去。
    /// <para>
    /// 🔑 另外還要 <see cref="Monitor.IsEntered"/> 才算數：這個欄位在取鎖<b>之前</b>就設好，
    /// 取鎖前與放鎖後的那一小段仍然是「就地執行」。
    /// </para>
    /// </remarks>
    [ThreadStatic]
    private static List<PendingLog>? _deferralScope;
    private readonly QuestData _questData;
    private readonly QuestFunctions _questFunctions;
    private readonly QuestRegistry _questRegistry;
    private readonly SinglePlayerDutyConfigComponent _singlePlayerDutyConfigComponent;
    private readonly TaskCreator _taskCreator;
    private readonly TataruPraiseIpc _tataruPraiseIpc;
    private readonly BossModIpc _bossModIpc;
    private readonly LifestreamIpc _lifestreamIpc;
    private readonly AlliedDailyRouteEstimator _alliedDailyRouteEstimator;
    private readonly IToastGui _toastGui;
    private EAutomationType _automationType;
    private DateTime _lastAutoRefresh = DateTime.MinValue;

    /// <summary>
    ///     Auto-refresh fields for tracking player state and progress
    /// </summary>
    private Vector3 _lastPlayerPosition = Vector3.Zero;
    private DateTime _lastProgressUpdate = DateTime.Now;
    private ElementId? _lastQuestId;
    private byte _lastQuestSequence = 255;
    private int _lastQuestStep = -1;

    /// <summary>
    /// </summary>
    private DateTime _lastTaskUpdate = DateTime.Now;

    /// <summary>
    ///     Some combat encounters finish relatively early (i.e. they're done as part of progressing the quest, but not
    ///     technically necessary to progress the quest if we'd just run away and back). We add some slight delay, as
    ///     talking to NPCs, teleporting etc. won't successfully execute.
    /// </summary>
    private DateTime _safeAnimationEnd = DateTime.MinValue;

    /// <summary>
    /// 見 <see cref="OnRepeatedInterruption"/>：卡在攻頂乙太水晶時，先隨機微幅位移，
    /// 這個時間到了才強制整個步驟重新規劃。<see langword="null"/> 代表目前沒有排定中的回復。
    /// </summary>
    private DateTime? _pendingStuckRecoveryReloadAt;

    public const int MaxAutoRetryCount = 5;
    private const int MaxAutoRetryDelaySeconds = 30;
    private int _autoRetryCount;
    private DateTime _nextAutoRetryAt = DateTime.MinValue;

    /// <summary>Auto 模式排定中的重試原因；<see langword="null"/>＝沒有排定中的重試。</summary>
    private string? _autoRetryReason;

    /// <summary>Auto 模式「沒有任務可做、原地等下一個」中。框架執行緒寫入，<see cref="Stop(string, bool)"/> 清除。</summary>
    private bool _autoWaitingForNextQuest;

    /// <summary>等下一個任務的上限：等待期間 IPC IsRunning 為 true，別的外掛會以它為閘門一直暫停。</summary>
    public const int MaxAutoWaitForNextQuestSeconds = 90;
    private DateTime _autoWaitStartedAt = DateTime.MinValue;
    private string? _autoWaitReason;

    public QuestController(
        IClientState clientState,
        IObjectTable objectTable,
        //IPlayerState playerState,
        GameFunctions gameFunctions,
        QuestFunctions questFunctions,
        MovementController movementController,
        CombatController combatController,
        GatheringController gatheringController,
        ILogger<QuestController> logger,
        HighlightObject highlightObject,
        QuestRegistry questRegistry,
        QuestData questData,
        IKeyState keyState,
        IChatGui chatGui,
        IFramework framework,
        ICondition condition,
        IToastGui toastGui,
        Configuration configuration,
        TaskCreator taskCreator,
        IServiceProvider serviceProvider,
        InterruptHandler interruptHandler,
        IDataManager dataManager,
        SinglePlayerDutyConfigComponent singlePlayerDutyConfigComponent,
        AlliedSocietyQuestFunctions alliedSocietyQuestFunctions,
        TataruPraiseIpc tataruPraiseIpc,
        BossModIpc bossModIpc,
        LifestreamIpc lifestreamIpc,
        AlliedDailyRouteEstimator alliedDailyRouteEstimator)
        : base(chatGui, condition, serviceProvider, interruptHandler, dataManager, logger)
    {
        _clientState = clientState;
        _objectTable = objectTable;
        //_playerState = playerState;
        _gameFunctions = gameFunctions;
        _questFunctions = questFunctions;
        _movementController = movementController;
        _combatController = combatController;
        _gatheringController = gatheringController;
        _questRegistry = questRegistry;
        _questData = questData;
        _keyState = keyState;
        _chatGui = chatGui;
        _framework = framework;
        _condition = condition;
        _toastGui = toastGui;
        _configuration = configuration;
        _taskCreator = taskCreator;
        _singlePlayerDutyConfigComponent = singlePlayerDutyConfigComponent;
        _alliedSocietyQuestFunctions = alliedSocietyQuestFunctions;
        _tataruPraiseIpc = tataruPraiseIpc;
        _bossModIpc = bossModIpc;
        _lifestreamIpc = lifestreamIpc;
        _alliedDailyRouteEstimator = alliedDailyRouteEstimator;
        _logger = logger;
        _highlightObject = highlightObject;

        _condition.ConditionChange += OnConditionChange;
        _toastGui.Toast += OnNormalToast;
        _toastGui.ErrorToast += OnErrorToast;
        _clientState.Logout += ClearRedeemAttemptsOnLogout;
    }

    public EAutomationType AutomationType
    {
        get => _automationType;
        set
        {
            if (value == _automationType)
            {
                return;
            }

            _logger.LogInformation("Setting automation type to {NewAutomationType} (previous: {OldAutomationType})",
                value, _automationType);
            _automationType = value;
            AutomationTypeChanged?.Invoke(this, value);
        }
    }

    public (QuestProgress Progress, ECurrentQuestType Type)? CurrentQuestDetails
    {
        get
        {
            if (SimulatedQuest != null)
            {
                return (SimulatedQuest, ECurrentQuestType.Simulated);
            }
            else if (NextQuest != null && _questFunctions.IsReadyToAcceptQuest(NextQuest.Quest.Id))
            {
                return (NextQuest, ECurrentQuestType.Next);
            }
            else if (GatheringQuest != null)
            {
                return (GatheringQuest, ECurrentQuestType.Gathering);
            }
            else if (StartedQuest != null)
            {
                return (StartedQuest, ECurrentQuestType.Normal);
            }
            else
            {
                return null;
            }
        }
    }

    public QuestProgress? CurrentQuest => CurrentQuestDetails?.Progress;

    public QuestProgress? StartedQuest { get; private set; }
    public QuestProgress? SimulatedQuest { get; private set; }
    public QuestProgress? NextQuest { get; private set; }
    public QuestProgress? GatheringQuest { get; private set; }

    /// <summary>
    ///     Used when accepting leves, as there's a small delay
    /// </summary>
    public QuestProgress? PendingQuest { get; private set; }

    public List<Quest> ManualPriorityQuests { get; } = [];

    /// <summary>
    /// 這一次「開始」自動排入的友好部族日常，依排入順序（部族依列舉順序、部族內依可接清單順序）。
    /// 只在 <see cref="_progressLock"/> 內讀寫；下一次 <see cref="Start"/> 整份換掉，登出時清空。
    /// </summary>
    private readonly List<(ElementId QuestId, EAlliedSociety Society)> _alliedDailyBatch = [];

    /// <summary>「排入的都處理完了」那一行已經寫過；批次又有任務可做時重設。</summary>
    private bool _alliedDailyBatchIdleLogged;

    /// <summary><see cref="TryPickAlliedDailyBatchQuest"/> 每幀重用的暫存（不每幀配置）。</summary>
    private readonly List<(int Society, int State)> _alliedDailyBatchStates = [];

    /// <summary>上一次寫進記錄的批次狀態（雜湊）；狀態有變才再寫一行，<see langword="null"/>＝還沒寫過。</summary>
    private int? _alliedDailyBatchStateKey;

    /// <summary>上一幀批次選任務的結果種類（<c>AlliedDailyPick*</c>，0＝沒選）。</summary>
    private int _alliedDailyLastPickKind;

    /// <summary>路線優化每次重算時重用的暫存（與 <see cref="_alliedDailyBatch"/> 同順序）。</summary>
    private readonly List<AlliedDailyRoute.Item> _alliedDailyRouteItems = [];
    private readonly List<AlliedDailyRouteEstimator.Note> _alliedDailyRouteNotes = [];
    private readonly List<int> _alliedDailyRoutePlan = [];

    /// <summary>已經確認「只剩交任務」的任務與當時的序列；序列變了就不算。登出時清空。</summary>
    private readonly Dictionary<ElementId, byte> _alliedDailyTurnInReady = [];

    /// <summary>
    /// 延後交任務而換走的任務當時做到哪一步；換回來時從這一步接著做，不從序列第一步重來
    /// （同一序列裡交任務前若有不會自動略過的步驟，例如移動，重來會白跑一趟）。序列變了就不算。
    /// </summary>
    private readonly Dictionary<ElementId, (byte Sequence, int Step)> _alliedDailyResumeStep = [];

    /// <summary>路線決定的輸入（狀態、序列、目前任務的步驟）沒變就沿用上次的結果；<see langword="null"/>＝要重算。</summary>
    private int? _alliedDailyRouteKey;
    private int _alliedDailyRouteIndex = -1;
    private int _alliedDailyRouteKind;
    private (uint TerritoryId, Vector3 Position)? _alliedDailyLastPlayer;

    /// <summary>遊戲的上限：每個友好部族一天最多接 3 個日常。</summary>
    private const int MaxDailyQuestsPerAlliedSociety = 3;

    public string? DebugState { get; private set; }

    public bool IsQuestWindowOpen => IsQuestWindowOpenFunction?.Invoke() ?? true;
    public Func<bool>? IsQuestWindowOpenFunction { private get; set; } = () => true;

    public bool IsRunning => !_taskQueue.AllTasksComplete;
    public TaskQueue TaskQueue => _taskQueue;

    public string? CurrentTaskState
    {
        get
        {
            if (_taskQueue.CurrentTaskExecutor is IDebugStateProvider debugStateProvider)
            {
                return debugStateProvider.GetDebugState();
            }
            else
            {
                return null;
            }
        }
    }

    /// <summary>引擎<b>停著</b>：手動模式、工作佇列空的、視窗關著。<see langword="true"/> 代表<b>沒</b>在跑。</summary>
    public bool IsQuestingStopped
    {
        get
        {
            return AutomationType == EAutomationType.Manual && !IsRunning && !IsQuestWindowOpen;
        }
    }

    public event AutomationTypeChangedEventHandler? AutomationTypeChanged;

    /// <summary>Auto 模式正在等下一個任務或等重試：沒有工作在跑，但自動化沒有停。</summary>
    public bool IsAutoContinuationPending => _autoRetryReason != null || _autoWaitingForNextQuest;

    /// <summary>給主視窗顯示的 Auto 等待狀態；沒有在等時為 <see langword="null"/>。</summary>
    public string? AutoContinuationStatus
    {
        get
        {
            if (_autoRetryReason != null)
            {
                int seconds = Math.Max(1, (int)Math.Ceiling((_nextAutoRetryAt - DateTime.Now).TotalSeconds));
                return "Auto: retrying in ?? s (attempt ?? of ??)".Loc(seconds, _autoRetryCount, MaxAutoRetryCount);
            }

            if (_autoWaitingForNextQuest)
            {
                int remaining = Math.Max(0,
                    MaxAutoWaitForNextQuestSeconds - (int)(DateTime.Now - _autoWaitStartedAt).TotalSeconds);
                return "Auto: waiting for the next quest (at most ?? s more)".Loc(remaining);
            }

            return null;
        }
    }

    /// <summary>
    /// 持 <see cref="_progressLock"/> 時要做、但不能在鎖裡做的一件事：一行輸出，或一段副作用。
    /// <c>ILogger</c> 最後落到 Dalamud 的 Serilog sink，那邊自己有鎖、還會做檔案 I/O ——
    /// 在鎖裡呼叫等於把 <c>_progressLock</c> 的持有時間綁在磁碟上。跨外掛的 IPC 更糟：
    /// CallGate 是直接方法呼叫，在鎖裡打過去等於把對方的鎖排在我們的鎖後面。
    /// 所以鎖內只把要做的事記下來，出鎖之後才真的做。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>記錄那一種刻意是純資料</b>（等級／訊息模板／參數／例外），寫入放在
    /// <see cref="EmitPending"/>；<paramref name="Args"/> 在加進清單的那一刻就求值了，
    /// 所以延後寫出去的內容與「當場寫」逐字相同。
    /// <para>
    /// <paramref name="ChatMessage"/> 不是 <see langword="null"/> 時代表這一則是要印到聊天視窗的
    /// （<c>_chatGui.Print</c>）而不是寫進記錄檔；三種放在同一個清單裡，原本的先後順序才保得住。
    /// </para>
    /// </remarks>
    /// <param name="SideEffect">
    /// 不是 <see langword="null"/> 時，這一則不是輸出而是<b>一段延到出鎖之後才執行的副作用</b>
    /// —— 跨外掛 IPC（<c>TataruPraiseIpc.NotifyNeedHelp</c>），或需要重建 <c>BeginScope</c>
    /// 前綴的記錄。一律經由 <see cref="RunOrDefer"/> 加進來。
    /// </param>
    private readonly record struct PendingLog(
        LogLevel Level,
        string Template,
        object?[] Args,
        string? ChatMessage,
        Exception? Exception = null,
        System.Action? SideEffect = null);

    /// <summary>把鎖內記下來的訊息寫出去。<b>必須在 <see cref="_progressLock"/> 放掉之後才呼叫。</b></summary>
    private void EmitPending(List<PendingLog> pending)
    {
        foreach(PendingLog entry in pending)
        {
            if (entry.SideEffect is { } sideEffect)
            {
                // 🔴 一則失敗不可以讓後面的都不做：延後的清單裡可能同時有「停止移動」與
                //    「請塔塔露喊一句」，而它們彼此無關。
                try
                {
                    sideEffect();
                }
                catch(Exception e)
                {
                    _logger.LogError(e, "Deferred side effect failed");
                }
            }
            else if (entry.ChatMessage is { } chatMessage)
            {
                _chatGui.Print(chatMessage, CommandHandler.MessageTag, CommandHandler.TagColor);
            }
            else
            {
                _logger.Log(entry.Level, entry.Exception, entry.Template, entry.Args);
            }
        }

        pending.Clear();
    }

    /// <summary>
    /// 現在是不是「持有 <see cref="_progressLock"/>、而且有人在收延後清單」。
    /// </summary>
    private bool TryGetDeferralScope([NotNullWhen(true)] out List<PendingLog>? pending)
    {
        pending = Monitor.IsEntered(_progressLock) ? _deferralScope : null;
        return pending != null;
    }

    /// <summary>
    /// 寫一行記錄；持著 <see cref="_progressLock"/> 時改成收進延後清單。
    /// </summary>
    /// <remarks>
    /// 📌 <paramref name="args"/> 在<b>呼叫的那一刻</b>就求值了，所以延後寫出去的內容
    /// 不會變成「出鎖之後才去讀 <c>CurrentQuest</c>」的值。
    /// </remarks>
    private void LogOrDefer(LogLevel level, string template, params object?[] args)
    {
        LogOrDefer(level, null, template, args);
    }

    /// <inheritdoc cref="LogOrDefer(LogLevel, string, object?[])"/>
    private void LogOrDefer(LogLevel level, Exception? exception, string template, params object?[] args)
    {
        if (TryGetDeferralScope(out List<PendingLog>? pending))
        {
            pending.Add(new PendingLog(level, template, args, null, exception));
        }
        else
        {
            _logger.Log(level, exception, template, args);
        }
    }

    /// <summary>
    /// 做一件事；持著 <see cref="_progressLock"/> 時改成收進延後清單，出鎖之後才做。
    /// </summary>
    /// <remarks>
    /// 🔴 用在<b>跨外掛 IPC</b>（CallGate 是直接方法呼叫，會在我們的鎖裡面跑別的外掛的碼
    /// ⇒ 對方日後長出任何一條回頭呼叫的路徑就是死鎖）與「要保留 <c>BeginScope</c> 前綴」的記錄。
    /// <para>
    /// ⚠️ 只適合<b>純副作用</b>：回傳值會在鎖裡被拿去分支的呼叫不可以用這一支。
    /// </para>
    /// <para>
    /// 📌 沒有人在收延後清單時（不持鎖、或從 UI／IPC 進來）就<b>當場執行</b>。
    /// </para>
    /// </remarks>
    private void RunOrDefer(System.Action action)
    {
        if (TryGetDeferralScope(out List<PendingLog>? pending))
        {
            pending.Add(new PendingLog(LogLevel.None, string.Empty, [], null, null, action));
        }
        else
        {
            action();
        }
    }

    public void Reload()
    {
        // 刻意寫在取鎖之前（見 PendingLog）：訊息是常數、也不看任何鎖保護的狀態。
        _logger.LogInformation("Reload, resetting curent quest progress");

        // 🔴 檔案列舉、JSON 解析、驗證全部在 _progressLock 外面做 —— 那把鎖每一個 framework tick
        //    都會被 UpdateCurrentQuest 拿去。重新載入之間的序列化由 QuestRegistry 自己那把閘門負責
        //    （卡住重試計時器在 framework 執行緒、使用者按下重新載入在繪製執行緒，兩邊可以同時進來）。
        QuestRegistry.ReloadBatch registryReload = _questRegistry.PrepareReload();
        bool published = false;
        try
        {
            lock(_progressLock)
            {
                ResetInternalState();
                ResetAutoRefreshState();

                // 鎖裡只剩「把準備好的登錄換上去」這幾行常數時間的參照指派。
                published = _questRegistry.PublishReload(registryReload);
                _singlePlayerDutyConfigComponent.Reload();
                _alliedSocietyQuestFunctions.Reload();
            }
        }
        finally
        {
            // 🔴 記錄、Reloaded 事件、跨外掛的 IPC 廣播一律在鎖外：Reloaded 的訂閱端會再做一次完整的
            //    檔案系統列舉，而 SendMessage 是同步跑在我們這條執行緒上的、別的外掛的碼。
            _questRegistry.EmitReloadSideEffects(registryReload, published);
        }
    }

    private void ResetInternalState()
    {
        StartedQuest = null;
        NextQuest = null;
        GatheringQuest = null;
        PendingQuest = null;
        SimulatedQuest = null;
        _safeAnimationEnd = DateTime.MinValue;
        _pendingStuckRecoveryReloadAt = null;

        DebugState = null;
    }

    private void ResetAutoRefreshState()
    {
        _lastPlayerPosition = Vector3.Zero;
        _lastQuestStep = -1;
        _lastQuestSequence = 255;
        _lastQuestId = null;
        _lastProgressUpdate = DateTime.Now;
        _lastAutoRefresh = DateTime.Now;
        ConsecutiveInterruptions = 0;
    }

    public void Update()
    {
        if (_pendingStuckRecoveryReloadAt is { } dueAt && DateTime.Now >= dueAt)
        {
            _pendingStuckRecoveryReloadAt = null;
            _logger.LogInformation("Nudge-position stuck recovery: reloading current step now");
            Reload();
        }

        unsafe
        {
            ActionManager* actionManager = ActionManager.Instance();
            if (actionManager != null)
            {
                float animationLock = Math.Max(actionManager->AnimationLock,
                    actionManager->CastTimeElapsed > 0
                        ? actionManager->CastTimeTotal - actionManager->CastTimeElapsed
                        : 0);
                if (animationLock > 0)
                {
                    _safeAnimationEnd = DateTime.Now.AddSeconds(1 + animationLock);
                }
            }
        }

        if (IsQuestingStopped)
        {
            return;
        }

        UpdateCurrentQuest();

        if (!_clientState.IsLoggedIn)
        {
            // 🔑 這一行把「真的被登出砍掉的自動化」跟「什麼都沒在跑的登出」在記錄檔上分開。
            //    ⚠️ 條件與 Stop() 的守衛「刻意」寫成同一個：那個 if 本身就是狀態邊緣
            //    （下一幀 IsRunning 已是 false、AutomationType 已是 Manual）⇒ 一次登出只念一行。
            if (IsRunning || AutomationType != EAutomationType.Manual)
            {
                _logger.LogInformation(
                    "Logout interrupted active questing (automation type {AutomationType}, current task {CurrentTask}, {QueuedTaskCount} more task(s) queued); questing does NOT resume by itself, start it again after logging back in",
                    AutomationType, _taskQueue.CurrentTaskExecutor?.CurrentTask, _taskQueue.RemainingTasks.Count());
            }

            StopAllDueToConditionFailed("Logged out");
        }
        if (_condition[ConditionFlag.Unconscious])
        {
            if (_condition[ConditionFlag.Unconscious] &&
                _condition[ConditionFlag.SufferingStatusAffliction63] &&
                _clientState.TerritoryType == SinglePlayerDuty.SpecialTerritories.Lahabrea)
            {
                // ignore, we're in the lahabrea fight
            }
            else if (_taskQueue.CurrentTaskExecutor is Duty.WaitAutoDutyExecutor)
            {
                // ignoring death in a dungeon if it is being run by AD
            }
            else if (!_taskQueue.AllTasksComplete || IsAutoContinuationPending)
            {
                StopAllDueToConditionFailed("HP = 0", true);
            }
        }
        else if (_configuration.General.UseEscToCancelQuesting && _keyState[VirtualKey.ESCAPE])
        {
            if (!_taskQueue.AllTasksComplete || IsAutoContinuationPending)
            {
                StopAllDueToConditionFailed("ESC pressed");
            }
        }

        // check level stop condition
        // stops immediately instead of quest stop after completion of quest
        if (_configuration.Stop.Enabled && _configuration.Stop.LevelToStopAfter)
        {
            unsafe
            {
                short currentLevel = PlayerState.Instance()->CurrentLevel;
                if (currentLevel >= _configuration.Stop.TargetLevel && IsRunning)
                {
                    _logger.LogInformation("Reached level stop condition (level: {CurrentLevel}, target: {TargetLevel})", currentLevel, _configuration.Stop.TargetLevel);
                    _chatGui.Print($"Reached or exceeded target level {_configuration.Stop.TargetLevel}.", CommandHandler.MessageTag, CommandHandler.TagColor);
                    Stop($"Level stop condition reached [{currentLevel}]");
                    return;
                }
            }
        }

        if (TryRunScheduledAutoRetry())
        {
            return;
        }

        if (AutomationType == EAutomationType.Automatic &&
            (_taskQueue.AllTasksComplete || _taskQueue.CurrentTaskExecutor?.CurrentTask is WaitAtEnd.WaitQuestAccepted)
            && CurrentQuest is { Sequence: 0, Step: 0 } or { Sequence: 0, Step: 255 }
            && DateTime.Now >= CurrentQuest.StepProgress.StartedAt.AddSeconds(15))
        {
            // 刻意寫在取鎖之前（見 PendingLog）：訊息是常數、也不看任何鎖保護的狀態。
            _logger.LogWarning("Quest accept apparently didn't work out, resetting progress");

            lock(_progressLock)
            {
                CurrentQuest.SetStep(0);
            }

            ExecuteNextStep();
            return;
        }

        CheckAutoRefreshCondition();

        UpdateCurrentTask();
    }

    private void CheckAutoRefreshCondition()
    {
        if (!_configuration.General.AutoStepRefreshEnabled ||
            AutomationType != EAutomationType.Automatic ||
            !IsRunning ||
            CurrentQuest == null ||
            !_clientState.IsLoggedIn ||
            _objectTable[0] == null ||
            DateTime.Now < _lastAutoRefresh.AddSeconds(5))
        {
            return;
        }

        if (_condition[ConditionFlag.InCombat] ||
            _condition[ConditionFlag.Unconscious] ||
            _condition[ConditionFlag.BoundByDuty] ||
            _condition[ConditionFlag.InDeepDungeon] ||
            _condition[ConditionFlag.WatchingCutscene] ||
            _condition[ConditionFlag.WatchingCutscene78] ||
            _condition[ConditionFlag.BetweenAreas] ||
            _condition[ConditionFlag.BetweenAreas51] ||
            _gameFunctions.IsOccupied() ||
            !_movementController.IsNavmeshReady ||
            (_taskQueue.CurrentTaskExecutor?.CurrentTask.GetType().Namespace == typeof(WaitAtEnd).Namespace
                && ConsecutiveInterruptions < 3) ||
            DateTime.Now < _safeAnimationEnd)
        {
            _lastProgressUpdate = DateTime.Now;
            return;
        }

        Vector3 currentPosition = _objectTable[0]!.Position;
        ElementId currentQuestId = CurrentQuest.Quest.Id;
        byte currentSequence = CurrentQuest.Sequence;
        int currentStep = CurrentQuest.Step;

        bool hasProgressBeenMade =
            Vector3.Distance(currentPosition, _lastPlayerPosition) > 0.5f ||
            !currentQuestId.Equals(_lastQuestId) ||
            currentSequence != _lastQuestSequence ||
            currentStep != _lastQuestStep;

        if (hasProgressBeenMade)
        {
            _lastPlayerPosition = currentPosition;
            _lastQuestId = currentQuestId;
            _lastQuestSequence = currentSequence;
            _lastQuestStep = currentStep;
            _lastProgressUpdate = DateTime.Now;
            ConsecutiveInterruptions = 0;
        }
        else
        {
            // we detect no progress, check if we should auto-refresh
            TimeSpan timeSinceProgress = DateTime.Now - _lastProgressUpdate;
            TimeSpan refreshDelay = TimeSpan.FromSeconds(_configuration.General.AutoStepRefreshDelaySeconds);

            if (timeSinceProgress >= refreshDelay)
            {
                _logger.LogInformation("Automatically refreshing quest step as no progress detected for {TimeSinceProgress:F1} seconds (quest: {QuestId}, sequence: {Sequence}, step: {Step})",
                    timeSinceProgress.TotalSeconds, currentQuestId, currentSequence, currentStep);

                _chatGui.Print($"Automatically refreshing quest step as no progress detected for {timeSinceProgress.TotalSeconds:F0} seconds.",
                    CommandHandler.MessageTag, CommandHandler.TagColor);

                ClearTasksInternal();

                Reload();

                _lastAutoRefresh = DateTime.Now;
            }
        }
    }

    /// <summary>
    /// 連續中斷重試達到門檻（見 MiniTaskController.ConsecutiveInterruptions）時的主動回復。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>刻意只處理一種情境：目的地是乙太水晶，人已經到了，卻沒有成功攻頂/傳送。</b>
    /// 其他互動類型（NPC 對話、拾取、戰鬥動作……）持續失敗的重試迴圈先不處理，
    /// 交給被動的 AutoStepRefresh（<see cref="CheckAutoRefreshCondition"/>）去兜底——
    /// 那些情境的成因太雜，貿然套用同一套回復手段風險比不處理還高。
    /// <para>
    /// 📌 <b>回復手段是「隨機微幅位移＋重新規劃路線」，不是傳送走。</b>
    /// 這種卡住通常是 vnavmesh／遊戲判定角色「已經到了」，但實際位置或朝向差了一點，
    /// 導致攻頂互動判定不到——手動走一小段路就會恢復正常，正是這裡在做的事：
    /// 隨機挑一個約 1.5~3 碼外的鄰近點導航過去，讓角色物理上真的移動一段距離，
    /// 兩秒後再強制整個步驟重新規劃（<see cref="ClearTasksInternal"/> + <see cref="Reload"/>）。
    /// 不會把角色傳送到別的地方，微幅移動用肉眼幾乎看不出來。
    /// </para>
    /// <para>
    /// 🔴 只在非戰鬥時動作：戰鬥中要嘛不該在做這個任務步驟，要嘛移動本身另有戰鬥系統處理。
    /// </para>
    /// <para>
    /// ⚠️ <b>「位置／朝向差一點」是經驗推測，無法離線證明。</b>
    /// 📌 假設不成立時的後果：白走 1.5~3 碼，然後照樣重新規劃路線；不會卡死、不會傳送，
    /// 真正的兜底仍然是被動的 <see cref="CheckAutoRefreshCondition"/>（AutoStepRefresh）。
    /// </para>
    /// </remarks>
    protected override void OnRepeatedInterruption(int consecutiveCount)
    {
        if (consecutiveCount != 3 ||
            !_configuration.General.NudgePositionOnAetheryteAttuneStuck ||
            _condition[ConditionFlag.InCombat] ||
            _objectTable[0] == null ||
            _taskQueue.CurrentTaskExecutor?.CurrentTask is not Aetheryte.Attune attune)
        {
            return;
        }

        Vector3 currentPosition = _objectTable[0]!.Position;
        double angle = Random.Shared.NextDouble() * Math.PI * 2;
        float nudgeDistance = 1.5f + (float)Random.Shared.NextDouble() * 1.5f;
        Vector3 nudgeTarget = currentPosition with
        {
            X = currentPosition.X + (float)Math.Cos(angle) * nudgeDistance,
            Z = currentPosition.Z + (float)Math.Sin(angle) * nudgeDistance
        };

        _logger.LogWarning(
            "Repeated interruption ({Count}) while attuning aetheryte {Aetheryte}, nudging position by ~{Distance:F1}y and recalculating route",
            consecutiveCount, attune.AetheryteLocation, nudgeDistance);

        _chatGui.Print(
            $"Repeated interruptions attuning {attune.AetheryteLocation}, nudging position and recalculating route.",
            CommandHandler.MessageTag, CommandHandler.TagColor);

        ClearTasksInternal();
        _movementController.NavigateTo(EMovementType.None, null, nudgeTarget, false, false, 0.5f);
        _pendingStuckRecoveryReloadAt = DateTime.Now.AddSeconds(2);
    }

    private void UpdateCurrentQuest()
    {
        List<PendingLog> pending = [];
        List<PendingLog>? previousScope = _deferralScope;
        _deferralScope = pending;
        try
        {
            UpdateCurrentQuestLocked(pending);
        }
        finally
        {
            _deferralScope = previousScope;
            // 出鎖之後才寫（<c>lock</c> 自己的 <c>Monitor.Exit</c> 在內層的 finally，一定先跑）。
            // ⚠️ 這個鎖裡還有別的元件（TaskCreator／MovementController／CombatController…）
            // 自己在寫記錄，那些沒有被延後 ⇒ 這裡延後的幾行相對於它們會往後挪；
            // 延後的這幾行彼此之間的先後順序不變。
            EmitPending(pending);
        }
    }

    /// <summary><see cref="UpdateCurrentQuest"/> 持鎖的那一段。</summary>
    /// <remarks>
    /// 🔴 這裡面持有 <see cref="_progressLock"/>：<b>不要直接呼叫 <c>_logger</c>、<c>_chatGui</c>，
    /// 也不要直接打任何跨外掛的 IPC</b>。要寫的東西用 <c>pending.Add</c> 或 <see cref="LogOrDefer"/>，
    /// 要做的副作用用 <see cref="RunOrDefer"/>，兩者都會等出鎖之後才真的發生。
    /// </remarks>
    private void UpdateCurrentQuestLocked(List<PendingLog> pending)
    {
        lock(_progressLock)
        {
            DebugState = null;

            if (!_clientState.IsLoggedIn)
            {
                ResetInternalState();
                _alliedDailyBatch.Clear();
                _alliedDailyTurnInReady.Clear();
                _alliedDailyResumeStep.Clear();
                _alliedDailyRouteKey = null;
                _alliedDailyLastPlayer = null;
                DebugState = "Not logged in";
                return;
            }

            if (PendingQuest != null)
            {
                if (!_questFunctions.IsQuestAccepted(PendingQuest.Quest.Id))
                {
                    DebugState = $"Waiting for Leve {PendingQuest.Quest.Id}";
                    return;
                }
                else
                {
                    StartedQuest = PendingQuest;
                    PendingQuest = null;
                    CheckNextTasks("Pending quest accepted");
                }
            }

            if (SimulatedQuest == null && NextQuest != null)
            {
                // if the quest is accepted, we no longer track it
                bool canUseNextQuest;
                if (NextQuest.Quest.Info.IsRepeatable)
                {
                    canUseNextQuest = !_questFunctions.IsQuestAccepted(NextQuest.Quest.Id);
                }
                else
                {
                    canUseNextQuest = !_questFunctions.IsQuestAcceptedOrComplete(NextQuest.Quest.Id);
                }

                if (!canUseNextQuest)
                {
                    pending.Add(new PendingLog(LogLevel.Information,
                        "Next quest {QuestId} accepted or completed",
                        [NextQuest.Quest.Id], null));

                    if (AutomationType == EAutomationType.SingleQuestA)
                    {
                        StartedQuest = NextQuest;
                        AutomationType = EAutomationType.SingleQuestB;
                    }

                    pending.Add(new PendingLog(LogLevel.Debug, "Started: {StartedQuest}",
                        [StartedQuest?.Quest.Id], null));
                    NextQuest = null;
                }
            }

            QuestProgress? questToRun;
            byte currentSequence;
            if (SimulatedQuest != null)
            {
                currentSequence = SimulatedQuest.Sequence;
                questToRun = SimulatedQuest;
            }
            else if (NextQuest != null && _questFunctions.IsReadyToAcceptQuest(NextQuest.Quest.Id))
            {
                questToRun = NextQuest;
                currentSequence = NextQuest.Sequence; // by definition, this should always be 0
                if (NextQuest.Step == 0 &&
                    _taskQueue.AllTasksComplete &&
                    AutomationType == EAutomationType.Automatic)
                {
                    ExecuteNextStep();
                }
            }
            else if (GatheringQuest != null)
            {
                questToRun = GatheringQuest;
                currentSequence = GatheringQuest.Sequence;
                if (GatheringQuest.Step == 0 &&
                    _taskQueue.AllTasksComplete &&
                    AutomationType == EAutomationType.Automatic)
                {
                    ExecuteNextStep();
                }
            }
            else
            {
                // 🔴 用不印東西的那一支：GetCurrentQuest 會直接 _chatGui.Print，而這裡持著 _progressLock。
                //    提示收進延後清單、出鎖之後才印；去重（同一則 60 秒一次）在 PrintHintThrottled 裡面。
                (ElementId? currentQuestId, currentSequence, MainScenarioQuestState msqState) =
                    _questFunctions.GetCurrentQuestWithHint(allowNewMsq: AutomationType != EAutomationType.SingleQuestB,
                        out string? questHint);
                if (questHint != null)
                {
                    RunOrDefer(() => _questFunctions.PrintHintThrottled(questHint));
                }

                (ElementId, byte)? priorityQuestOption =
                    ManualPriorityQuests
                        .Where(x => _questFunctions.IsReadyToAcceptQuest(x.Id) || _questFunctions.IsQuestAccepted(x.Id))
                        .Select(x => (x.Id, _questFunctions.GetQuestProgressInfo(x.Id)?.Sequence ?? 0))
                        .FirstOrDefault();
                string questSource = "任務清單／主線";
                if (priorityQuestOption is { Item1: not null } priorityQuest)
                {
                    currentQuestId = priorityQuest.Item1;
                    currentSequence = priorityQuest.Item2;
                    questSource = "優先清單";
                }

                if (TryPickAlliedDailyBatchQuest(priorityQuestOption?.Item1, pending,
                        out ElementId? batchQuestId, out byte batchSequence, out string? batchSource))
                {
                    currentQuestId = batchQuestId;
                    currentSequence = batchSequence;
                    questSource = batchSource;
                }

                if (currentQuestId == null || currentQuestId.Value == 0)
                {
                    if (StartedQuest != null)
                    {
                        if (msqState == MainScenarioQuestState.Unavailable)
                        {
                            //_logger.LogWarning("MSQ information not available, doing nothing");
                            return;
                        }
                        else if (msqState == MainScenarioQuestState.LoadingScreen)
                        {
                            //_logger.LogWarning("On loading screen, no MSQ - doing nothing");
                            return;
                        }

                        pending.Add(new PendingLog(LogLevel.Information,
                            "No current quest, resetting data [CQI: {CurrrentQuestData}], [CQ: {QuestData}], [MSQ: {MsqData}]",
                            [
                                _questFunctions.GetCurrentQuestInternal(true),
                                // ⚠️ 這裡刻意丟掉提示（out _）：上面那次查詢已經在同一幀收下它了，
                                //    這一行只是診斷用的重查，再印一次是重複。
                                _questFunctions.GetCurrentQuestWithHint(true, out _),
                                _questFunctions.GetMainScenarioQuest()
                            ], null));
                        QuestProgress? finishedQuest = StartedQuest;
                        StartedQuest = null;
                        if (!TryWaitForNextQuest("Resetting current quest", finishedQuest))
                        {
                            Stop("Resetting current quest");
                        }
                    }

                    questToRun = null;
                }
                else if (StartedQuest == null || StartedQuest.Quest.Id != currentQuestId)
                {
                    if (_configuration.Stop.Enabled &&
                        StartedQuest != null &&
                        _configuration.Stop.QuestsToStopAfter.Contains(StartedQuest.Quest.Id) &&
                        _questFunctions.IsQuestComplete(StartedQuest.Quest.Id))
                    {
                        ElementId questId = StartedQuest.Quest.Id;
                        pending.Add(new PendingLog(LogLevel.Information,
                            "Reached stopping point (quest: {QuestId})", [questId], null));
                        pending.Add(new PendingLog(LogLevel.None, string.Empty, [],
                            $"Completed quest '{StartedQuest.Quest.Info.Name}', which is configured as a stopping point."));
                        StartedQuest = null;
                        Stop($"Stopping point [{questId}] reached");
                    }
                    else if (_questRegistry.TryGetQuest(currentQuestId, out Quest? quest))
                    {
                        unsafe
                        {
                            // 契約：CurrentLevel 的 0 ＝「還不知道」（登入／換角那一幀 PlayerState
                            // 還沒填好），不是「等級不足」。那一幀什麼都不判、也什麼都不改，
                            // 下一幀重新進到這裡再判。
                            if (PlayerState.Instance()->CurrentLevel == 0)
                            {
                                DebugState = "Waiting for player level";
                                return;
                            }
                        }

                        _highlightObject.SetHighlight([]);
                        pending.Add(new PendingLog(LogLevel.Information, "New quest: {QuestName}",
                            [quest.Info.Name], null));
                        pending.Add(new PendingLog(LogLevel.Information,
                            "[任務切換] {PreviousQuestId} → {QuestId} {QuestName}（序列 {Sequence}，來源：{QuestSource}）",
                            [StartedQuest?.Quest.Id.ToString() ?? "-", quest.Id, quest.Info.Name, currentSequence, questSource],
                            null));
                        StartedQuest = new(quest, currentSequence, TakeAlliedDailyResumeStep(quest.Id, currentSequence));
#if DEBUG
                        if (_configuration.Advanced.OpenEditor)
                        {
                            (bool success, string msg) = _questRegistry.OpenEditor(StartedQuest.Quest.Info);
                            pending.Add(new PendingLog(LogLevel.Debug,
                                $"OpenEditor {success}: {msg}", [], null));
                        }
#endif

                        unsafe
                        {
                            if (PlayerState.Instance()->CurrentLevel < quest.Info.Level)
                            {
                                pending.Add(new PendingLog(LogLevel.Information,
                                    "Stopping automation, player level ({PlayerLevel}) < quest level ({QuestLevel}",
                                    [PlayerState.Instance()->CurrentLevel, quest.Info.Level], null));
                                Stop("Quest level too high", true);
                            }
                            else
                            {
                                if (AutomationType == EAutomationType.SingleQuestB)
                                {
                                    pending.Add(new PendingLog(LogLevel.Information,
                                        "Single quest is finished", [], null));
                                    AutomationType = EAutomationType.Manual;
                                }

                                CheckNextTasks("Different Quest");
                            }
                        }
                    }
                    else if (StartedQuest != null)
                    {
                        pending.Add(new PendingLog(LogLevel.Information,
                            "No active quest anymore? Not sure what happened...", [], null));
                        StartedQuest = null;
                        Stop("No active Quest", true);
                    }

                    return;
                }
                else
                {
                    questToRun = StartedQuest;
                }
            }

            if (questToRun == null)
            {
                if (!TryWaitForNextQuest("No quest active", null))
                {
                    DebugState = "No quest active";
                    Stop("No quest active");
                }

                return;
            }

            if (_gameFunctions.IsOccupied() && !_gameFunctions.IsOccupiedWithCustomDeliveryNpc(questToRun.Quest))
            {
                DebugState = "Occupied";
                return;
            }

            // 🔴 讀的是快照不是即時值：IsPathRunning 的 getter 是一次跨外掛 IPC，而這裡持著
            //    _progressLock。快照由 DalamudInitializer 在同一幀、進到這裡之前取樣，
            //    而 MovementController.Stop()／ResetPathfinding() 會當場把它歸零
            //    ⇒ 鎖內停止移動之後再判斷不會讀到舊值。見 IsPathRunningSnapshot。
            if (_movementController.IsPathfindingSnapshot)
            {
                DebugState = "Pathfinding is running";
                return;
            }

            if (_movementController.IsPathRunningSnapshot)
            {
                DebugState = "Path is running";
                return;
            }

            if (DateTime.Now < _safeAnimationEnd)
            {
                DebugState = "Waiting for Animation";
                return;
            }

            if (questToRun.Sequence != currentSequence)
            {
                _highlightObject.SetHighlight([]);
                questToRun.SetSequence(currentSequence);
                CheckNextTasks(
                    $"New sequence {questToRun == StartedQuest}/{_questFunctions.GetCurrentQuestInternal(true)}");
            }

            Quest q = questToRun.Quest;
            QuestSequence? sequence = q.FindSequence(questToRun.Sequence);
            if (sequence == null)
            {
                DebugState = $"Sequence {questToRun.Sequence} not found";
                Stop("Unknown sequence", true);
                return;
            }

            if (questToRun.Step == 255)
            {
                DebugState = "Step completed";
                if (!_taskQueue.AllTasksComplete)
                {
                    CheckNextTasks("Step complete");
                }
                return;
            }

            if (sequence.Steps.Count > 0 && questToRun.Step >= sequence.Steps.Count)
            {
                DebugState = "Step not found";
                Stop("Unknown step", true);
                return;
            }

            DebugState = null;
        }
    }

    public (QuestSequence? Sequence, QuestStep? Step, bool createTasks) GetNextStep()
    {
        if (CurrentQuest == null)
        {
            return (null, null, false);
        }

        Quest q = CurrentQuest.Quest;
        QuestSequence? seq = q.FindSequence(CurrentQuest.Sequence);
        if (seq == null)
        {
            return (null, null, true);
        }

        if (seq.Steps.Count == 0)
        {
            return (seq, null, true);
        }

        if (CurrentQuest.Step >= seq.Steps.Count)
        {
            return (null, null, false);
        }

        return (seq, seq.Steps[CurrentQuest.Step], true);
    }

    public void IncreaseStepCount(ElementId? questId, int? sequence, bool shouldContinue = false)
    {
        List<PendingLog> pending = [];
        List<PendingLog>? previousScope = _deferralScope;
        _deferralScope = pending;
        bool stepped;
        try
        {
            stepped = IncreaseStepCountLocked(questId, sequence, pending);
        }
        finally
        {
            _deferralScope = previousScope;
            // 出鎖之後才寫，提早結束的路徑也會經過這裡；下面 ExecuteNextStep() 產生的記錄排在這幾行之後。
            EmitPending(pending);
        }

        if (!stepped)
        {
            return;
        }

        using IDisposable? scope = _logger.BeginScope("IncStepCt");
        if (shouldContinue && AutomationType != EAutomationType.Manual)
        {
            ExecuteNextStep();
        }
    }

    /// <summary><see cref="IncreaseStepCount"/> 持鎖的那一段。</summary>
    /// <param name="pending">要寫的記錄一律加進這裡，由呼叫端出鎖之後才寫出去。</param>
    /// <returns>
    /// <see langword="false"/>＝提早 <c>return</c> 的路徑（沒有真的增加步數）。
    /// </returns>
    /// <remarks>
    /// 🔴 這裡面持有 <see cref="_progressLock"/>：不要直接呼叫 <c>_logger</c> 或 <c>_chatGui</c>。
    /// <para>
    /// <see cref="SkipLocked"/> 直接呼叫這一支（而不是 <see cref="IncreaseStepCount"/>），
    /// 因為它自己已經持有同一把鎖——走公開那支的話，延後的記錄會在鎖還握著的時候被寫出去。
    /// </para>
    /// </remarks>
    private bool IncreaseStepCountLocked(ElementId? questId, int? sequence, List<PendingLog> pending)
    {
        lock(_progressLock)
        {
            (QuestSequence? seq, QuestStep? step, bool _) = GetNextStep();
            if (CurrentQuest == null || seq == null || step == null)
            {
                pending.Add(new PendingLog(LogLevel.Warning,
                    "Unable to retrieve next quest step, not increasing step count", [], null));
                return false;
            }

            if (questId != null && CurrentQuest.Quest.Id != questId)
            {
                pending.Add(new PendingLog(LogLevel.Warning,
                    "Ignoring 'increase step count' for different quest (expected {ExpectedQuestId}, but we are at {CurrentQuestId}",
                    [questId, CurrentQuest.Quest.Id], null));
                return false;
            }

            if (sequence != null && seq.Sequence != sequence.Value)
            {
                pending.Add(new PendingLog(LogLevel.Warning,
                    "Ignoring 'increase step count' for different sequence (expected {ExpectedSequence}, but we are at {CurrentSequence}",
                    [sequence, seq.Sequence], null));
            }

            pending.Add(new PendingLog(LogLevel.Information,
                "Increasing step count from {CurrentValue}", [CurrentQuest.Step], null));
            if (CurrentQuest.Step + 1 < seq.Steps.Count)
            {
                CurrentQuest.SetStep(CurrentQuest.Step + 1);
            }
            else
            {
                CurrentQuest.SetStep(255);
            }

            ResetAutoRefreshState();
        }

        return true;
    }

    internal void AbandonQuest(QuestId questId)
    {
        _logger.LogInformation($"AbandonQuest: {questId}");
        _logger.LogWarning("Abandoning a quest via direct command is not available on this Dalamud API level; use the in-game journal to abandon it manually.");
    }

    internal void AbandonQuest(string questId)
    {
        try
        {
            ushort parsedQuestId = ushort.Parse(questId, CultureInfo.InvariantCulture);
            if (_questFunctions.GetQuestProgressInfo(new QuestId(parsedQuestId)) != null)
            {
                AbandonQuest(new QuestId(parsedQuestId));
            }
            else
            {
                _logger.LogWarning("AbandonQuest failed: could not find quest ID");
            }
        }
        catch(Exception e)
        {
            _logger.LogWarning($"AbandonQuest failed: {e}");
        }
    }

    internal void AbandonQuest()
    {
        if (CurrentQuest != null && _questFunctions.GetQuestProgressInfo(CurrentQuest.Quest.Id) != null)
        {
            AbandonQuest((QuestId)CurrentQuest.Quest.Id);
        }
        else
        {
            _logger.LogWarning("AbandonQuest failed: could not find quest ID");
        }
    }

    private void ClearTasksInternal()
    {
        //_logger.LogDebug("Clearing task (internally)");
        if (_taskQueue.CurrentTaskExecutor is IStoppableTaskExecutor stoppableTaskExecutor)
        {
            stoppableTaskExecutor.StopNow();
        }

        _taskQueue.Reset();

        _combatController.Stop("ClearTasksInternal", RunOrDefer);
        _gatheringController.Stop("ClearTasksInternal");
    }

    public override void Stop(string label)
    {
        Stop(label, false);
    }

    /// <summary>
    /// 停止自動任務。
    /// </summary>
    /// <param name="label">停止原因，寫進記錄檔。</param>
    /// <param name="needsManualAttention">
    /// 這次停止是不是「卡住了，要人來看一下」——不支援的步驟、任務跑不下去、資料對不上之類。
    /// 使用者自己按停止、走到設定好的停止點、流程正常結束都<b>不算</b>。
    /// </param>
    /// <remarks>
    /// 🔴 <b>通知刻意寫在下面那個 <c>if</c> 裡面。</b>那個判斷本身就是「從執行中變成停下來」的狀態邊緣：
    /// 進得去代表這一幀真的把自動化停掉了，第二幀（<c>IsRunning</c> 已經是 false、
    /// <c>AutomationType</c> 已經被設成 <see cref="EAutomationType.Manual"/>）就進不去。
    /// <c>Stop("Unknown sequence")</c> 這類呼叫點是每幀都會走到的輪詢路徑，
    /// 少了這道邊緣就會變成「一直念」——而那是不會報錯的。
    /// </remarks>
    public void Stop(string label, bool needsManualAttention)
    {
        ResetAutoRetryState();
        _autoWaitingForNextQuest = false;
        _highlightObject.SetHighlight([]);
        using IDisposable? scope = _logger.BeginScope($"Stop/{label}");
        if (IsRunning || AutomationType != EAutomationType.Manual)
        {
            // 要在清空工作佇列之前判：清掉之後就不知道 Lifestream 是不是在替我們跑。
            bool lifestreamInFlight = _taskQueue.CurrentTaskExecutor is
                Steps.Common.WaitLifestream.Executor or AethernetShortcut.UseAethernetShortcut { LifestreamRequested: true };
            ClearTasksInternal();

            // Questionable 發起的乙太之光傳送一起停（跨插件 IPC，出鎖再打；Lifestream 不忙就不碰它）。
            if (lifestreamInFlight)
            {
                RunOrDefer(() =>
                {
                    if (_lifestreamIpc.IsBusy)
                    {
                        _lifestreamIpc.Abort();
                    }
                });
            }

            // 停下來就撤掉對 BMR 設定的執行期覆寫（跨插件 IPC，出鎖再打）。
            RunOrDefer(_bossModIpc.ClearTransientOverrides);

            // 延後的時候把 BeginScope 一起帶過去重建，輸出的前綴才不會掉。
            RunOrDefer(() =>
            {
                using IDisposable? deferredScope = _logger.BeginScope($"Stop/{label}");
                _logger.LogInformation("Stopping automatic questing");
            });
            AutomationType = EAutomationType.Manual;
            NextQuest = null;
            GatheringQuest = null;
            _lastTaskUpdate = DateTime.Now;

            ResetAutoRefreshState();
            unsafe
            {
                if (_objectTable[0] is IPlayerCharacter player)
                {
                    StatusManager* playerStatusManager = player.BattleChara()->GetStatusManager();
                    if (playerStatusManager->HasStatus(416)) // Transparent
                    {
                        StatusManager.ExecuteStatusOff(416);
                    }
                }
            }

            if (needsManualAttention)
            {
                // 🔴 這是跨外掛 IPC（CallGate＝直接方法呼叫，對方的碼跑在我們這條執行緒上）。
                //    UpdateCurrentQuest／Skip 是持著 _progressLock 進來的，在鎖裡打過去等於
                //    把 TataruPraise 的鎖排在我們的鎖後面 —— 對方日後長出任何一條回頭呼叫
                //    Questionable 的路徑就是死鎖。純通知、沒有回傳值 ⇒ 延到出鎖之後再打。
                RunOrDefer(() => _tataruPraiseIpc.NotifyNeedHelp(label));
            }
        }
    }

    /// <inheritdoc/>
    protected override void StopDueToFailure(string label)
    {
        Stop(label, true);
    }

    /// <summary>
    /// 這一步的任務戰鬥副本已經進去過、現在人在副本外，而任務進度還沒前進。框架執行緒呼叫。
    /// </summary>
    /// <remarks>看目前任務與佇列，略過 <c>DisableAi</c> 之後的第一個任務。</remarks>
    /// <param name="disableAiPending">
    /// <c>DisableAi</c> 還排在佇列裡沒執行（停下時要自己關；目前任務是 <c>WaitSinglePlayerDuty</c> 時由它的 StopNow 關）。
    /// </param>
    public bool IsAfterSinglePlayerDutyAttempt(out uint contentFinderConditionId, out bool disableAiPending)
    {
        contentFinderConditionId = 0;
        disableAiPending = false;
        if (_condition[ConditionFlag.BoundByDuty])
        {
            return false;
        }

        ITask? current = _taskQueue.CurrentTaskExecutor?.CurrentTask;
        IEnumerable<ITask> tasks = current != null
            ? _taskQueue.RemainingTasks.Prepend(current)
            : _taskQueue.RemainingTasks;
        foreach(ITask task in tasks)
        {
            switch (task)
            {
                case SinglePlayerDuty.DisableAi:
                    disableAiPending |= !ReferenceEquals(task, current);
                    continue;
                case SinglePlayerDuty.WaitSinglePlayerDuty wait:
                    contentFinderConditionId = wait.ContentFinderConditionId;
                    return true;
                case SinglePlayerDuty.WaitDutyOutcome outcome:
                    contentFinderConditionId = outcome.ContentFinderConditionId;
                    return true;
                default:
                    return false;
            }
        }

        return false;
    }

    /// <summary>佇列（含目前任務）裡還有 BossMod 代打任務戰鬥的進場或副本內等待。框架執行緒呼叫。</summary>
    public bool HasPendingSinglePlayerDutyWithBossMod()
    {
        if (_taskQueue.CurrentTaskExecutor?.CurrentTask is SinglePlayerDuty.StartSinglePlayerDuty
            or SinglePlayerDuty.WaitSinglePlayerDuty)
        {
            return true;
        }

        foreach (ITask task in _taskQueue.RemainingTasks)
        {
            if (task is SinglePlayerDuty.StartSinglePlayerDuty or SinglePlayerDuty.WaitSinglePlayerDuty)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 任務戰鬥失敗：停下自動任務並請塔塔露求助，不自動重試（重試與否留給使用者）。
    /// </summary>
    /// <param name="signal">判定失敗的依據，寫進記錄檔與聊天訊息。</param>
    public void StopAfterSinglePlayerDutyFailure(uint contentFinderConditionId, string dutyName, string signal)
    {
        if (!IsRunning && AutomationType == EAutomationType.Manual)
        {
            return;
        }

        _logger.LogInformation(
            "任務戰鬥看起來失敗了，停下自動任務（不自動重試）：副本 {CfcId}「{DutyName}」，任務 {QuestId}，依據：{Signal}",
            contentFinderConditionId, dutyName, CurrentQuest?.Quest.Id, signal);
        _chatGui.PrintError($"任務戰鬥「{dutyName}」看起來失敗了（{signal}），已停下自動任務、不會自動重試。處理好之後再按開始即可。",
            CommandHandler.MessageTag, CommandHandler.TagColor);
        Stop($"Single player duty failed: {contentFinderConditionId}", true);
    }

    /// <summary>
    /// Auto 模式下「沒有任務可做」時不停下、原地等下一個任務；不符合條件時回 <see langword="false"/>，由呼叫端照原本停下。
    /// </summary>
    /// <remarks>
    /// 持 <see cref="_progressLock"/> 呼叫：記錄一律走 <c>LogOrDefer</c>。
    /// 刻意不等的情況：主線暫停中（沒有任務可做就該停）、剛完成的任務是設定好的停止點
    /// （等到下一個任務時 <c>StartedQuest</c> 已是 null，停止點的判斷就再也走不到）、
    /// 剛交完的是部族日常批次的最後一個（一般規則已經沒有任務，等也等不到）、
    /// 已等滿 <see cref="MaxAutoWaitForNextQuestSeconds"/>（回 false 讓呼叫端照它原本的方式停下）。
    /// </remarks>
    private bool TryWaitForNextQuest(string label, QuestProgress? finishedQuest)
    {
        if (!_configuration.General.AutoMode ||
            AutomationType != EAutomationType.Automatic ||
            _configuration.Advanced.StopMSQ)
        {
            return false;
        }

        if (finishedQuest != null &&
            _configuration.Stop.Enabled &&
            _configuration.Stop.QuestsToStopAfter.Contains(finishedQuest.Quest.Id) &&
            _questFunctions.IsQuestComplete(finishedQuest.Quest.Id))
        {
            return false;
        }

        if (finishedQuest != null && IsAlliedDailyBatchFinishedBy(finishedQuest.Quest.Id))
        {
            LogOrDefer(LogLevel.Information,
                "[部族日常] 排入的 {Count} 個任務都交完了，不等下一個任務、直接停下（最後交的是 {QuestId}）",
                _alliedDailyBatch.Count, finishedQuest.Quest.Id);
            return false;
        }

        if (_autoWaitingForNextQuest)
        {
            double waitedSeconds = (DateTime.Now - _autoWaitStartedAt).TotalSeconds;
            if (waitedSeconds >= MaxAutoWaitForNextQuestSeconds)
            {
                LogOrDefer(LogLevel.Information,
                    "Auto mode: no next quest after waiting {WaitedSeconds:F0}s (wait started by: {WaitReason}, limit {LimitSeconds}s); stopping as '{Reason}'",
                    waitedSeconds, _autoWaitReason, MaxAutoWaitForNextQuestSeconds, label);
                return false;
            }
        }

        if (IsRunning)
        {
            ClearTasksInternal();
        }

        _lastTaskUpdate = DateTime.Now;
        if (!_autoWaitingForNextQuest)
        {
            _autoWaitingForNextQuest = true;
            _autoWaitStartedAt = DateTime.Now;
            _autoWaitReason = label;
            LogOrDefer(LogLevel.Information,
                "Auto mode: {Reason}, waiting up to {LimitSeconds}s for the next quest instead of stopping (press Stop to end)",
                label, MaxAutoWaitForNextQuestSeconds);
        }

        DebugState = AutoContinuationStatus;
        return true;
    }

    /// <summary>
    /// 可恢復的失敗（目前只有尋路失敗）：Auto 模式下延後重跑目前這一步，否則照原本停下。
    /// </summary>
    /// <remarks>
    /// 只從框架執行緒、不持鎖的路徑呼叫。重試間隔 2、4、8、16、30 秒；
    /// 同一步驟連續第 <see cref="MaxAutoRetryCount"/>＋1 次失敗就當成需要人處理而停下。
    /// </remarks>
    public void RetryAfterRecoverableFailure(string label)
    {
        if (!_configuration.General.AutoMode || AutomationType != EAutomationType.Automatic)
        {
            Stop(label);
            return;
        }

        if (_autoRetryReason != null)
        {
            _logger.LogInformation("Ignoring recoverable failure while an Auto retry is already scheduled: {Reason}", label);
            return;
        }

        ClearTasksInternal();
        _movementController.Stop();
        _autoRetryCount++;
        if (_autoRetryCount > MaxAutoRetryCount)
        {
            _chatGui.PrintError(
                "Auto: retried this step ?? times without success, stopping. Last error: ??".Loc(MaxAutoRetryCount, label),
                CommandHandler.MessageTag, CommandHandler.TagColor);
            Stop($"Auto retry limit reached: {label}", true);
            return;
        }

        int delaySeconds = Math.Min(MaxAutoRetryDelaySeconds, 1 << _autoRetryCount);
        _autoRetryReason = label;
        _nextAutoRetryAt = DateTime.Now.AddSeconds(delaySeconds);
        DebugState = AutoContinuationStatus;
        _logger.LogWarning("Auto retry {RetryCount}/{MaxRetryCount} scheduled in {DelaySeconds}s after: {Reason}",
            _autoRetryCount, MaxAutoRetryCount, delaySeconds, label);
    }

    /// <returns><see langword="true"/>＝這一幀由重試接手（還在等，或剛重跑），<see cref="Update"/> 不要再往下走。</returns>
    private bool TryRunScheduledAutoRetry()
    {
        if (_autoRetryReason == null)
        {
            return false;
        }

        if (!_configuration.General.AutoMode || AutomationType != EAutomationType.Automatic)
        {
            ResetAutoRetryState();
            return false;
        }

        if (DateTime.Now < _nextAutoRetryAt)
        {
            DebugState = AutoContinuationStatus;
            return true;
        }

        string reason = _autoRetryReason;
        _autoRetryReason = null;
        _nextAutoRetryAt = DateTime.MinValue;
        _logger.LogInformation("Auto retrying current step ({RetryCount}/{MaxRetryCount}) after: {Reason}",
            _autoRetryCount, MaxAutoRetryCount, reason);
        ExecuteNextStep();
        return true;
    }

    private void ResetAutoRetryState()
    {
        _autoRetryCount = 0;
        _nextAutoRetryAt = DateTime.MinValue;
        _autoRetryReason = null;
    }

    public void StopAllDueToConditionFailed(string label)
    {
        StopAllDueToConditionFailed(label, false);
    }

    /// <param name="needsManualAttention">見 <see cref="Stop(string, bool)"/>。</param>
    /// <inheritdoc cref="StopAllDueToConditionFailed(string)"/>
    public void StopAllDueToConditionFailed(string label, bool needsManualAttention)
    {
        Stop(label, needsManualAttention);
        _movementController.Stop(RunOrDefer);
        _combatController.Stop(label, RunOrDefer);
        _gatheringController.Stop(label);
    }

    private void CheckNextTasks(string label)
    {
        if (AutomationType is EAutomationType.Automatic or EAutomationType.SingleQuestA or EAutomationType.SingleQuestB)
        {
            using IDisposable? scope = _logger.BeginScope(label);

            ResetAutoRetryState();
            ClearTasksInternal();

            if (CurrentQuest?.Step is >= 0 and < 255)
            {
                ExecuteNextStep();
            }
            else
            {
                RunOrDefer(() =>
                {
                    using IDisposable? deferredScope = _logger.BeginScope(label);
                    _logger.LogInformation("Couldn't execute next step during Stop() call");
                });
            }

            _lastTaskUpdate = DateTime.Now;

            ResetAutoRefreshState();
        }
        else
        {
            Stop(label);
        }
    }

    public void SimulateQuest(IQuestInfo? questInfo, byte sequence, int step)
    {
        if (questInfo is not null && _questRegistry.TryGetQuest(questInfo.QuestId, out Quest? quest))
        {
            SimulateQuest(quest, sequence, step);
        }
    }

    public void SimulateQuest(Quest? quest, byte sequence, int step)
    {
        _highlightObject.SetHighlight([]);
        LogOrDefer(LogLevel.Information, "SimulateQuest: {QuestId}", quest?.Id);
        if (quest != null)
        {
            SimulatedQuest = new(quest, sequence, step);
        }
        else
        {
            SimulatedQuest = null;
        }
    }

    public void StopSimulate()
    {
        _highlightObject.SetHighlight([]);
        LogOrDefer(LogLevel.Information, "StopSimulate");
        SimulatedQuest = null;
    }

    public void SetNextQuest(Quest? quest)
    {
        _highlightObject.SetHighlight([]);
        LogOrDefer(LogLevel.Information, "NextQuest: {QuestId}", quest?.Id);
        if (quest != null)
        {
            NextQuest = new(quest);
        }
        else
        {
            NextQuest = null;
        }
    }

    public void SetGatheringQuest(Quest? quest)
    {
        _highlightObject.SetHighlight([]);
        LogOrDefer(LogLevel.Information, "GatheringQuest: {QuestId}", quest?.Id);
        if (quest != null)
        {
            GatheringQuest = new(quest);
        }
        else
        {
            GatheringQuest = null;
        }
    }

    public void SetPendingQuest(QuestProgress? quest)
    {
        _highlightObject.SetHighlight([]);
        LogOrDefer(LogLevel.Information, "PendingQuest: {QuestId}", quest?.Quest.Id);
        PendingQuest = quest;
    }

    protected override void UpdateCurrentTask()
    {
        if (_gameFunctions.IsOccupied() && !_gameFunctions.IsOccupiedWithCustomDeliveryNpc(CurrentQuest?.Quest))
        {
            return;
        }

        base.UpdateCurrentTask();
    }

    protected override void OnTaskComplete(ITask task)
    {
        if (task is WaitAtEnd.WaitQuestCompleted)
        {
            SimulatedQuest = null;
        }
    }

    protected override void OnNextStep(ILastTask task)
    {
        ResetAutoRetryState();
        IncreaseStepCount(task.ElementId, task.Sequence, true);
    }

    public void Start(string label)
    {
        using IDisposable? scope = _logger.BeginScope($"Q/{label}");
        RedeemRewardItems.ResetAttemptedItems();
        ResetAutoRetryState();
        AutomationType = EAutomationType.Automatic;
        QueueAlliedSocietyDailies();
        ExecuteNextStep();
    }

    /// <summary>
    /// IPC <c>Questionable.StartAlliedSocietyDailies</c> 的本體：今天有友好部族日常可排才走 <see cref="Start"/>，
    /// 否則回 <see langword="false"/> 並寫一行 Information 說明原因。框架執行緒呼叫。
    /// </summary>
    /// <remarks>批次空時刻意不開始：<see cref="Start"/> 會退回一般選任務規則，做出呼叫端沒預期的任務。</remarks>
    public bool TryStartAlliedSocietyDailies(string label)
    {
        bool running = IsRunning || AutomationType != EAutomationType.Manual;
        string? blocker = running ? null : GetAlliedDailyStartBlocker();
        int societyCount = _configuration.General.AutoQueueAlliedSocietyDailies.Count;
        AlliedDailyPlan? plan = null;
        if (!running && blocker == null && societyCount > 0)
        {
            plan = PlanAlliedSocietyDailies();
            if (plan == null)
            {
                blocker = "讀不到任務資料（QuestManager 為空）";
            }
        }

        int decision = DecideAlliedDailyIpcStart(running, blocker == null, societyCount,
            plan?.Outcomes ?? (IReadOnlyList<int>)Array.Empty<int>());
        if (decision == AlliedDailyIpcStart)
        {
            LogOrDefer(LogLevel.Information, "[部族日常] {Label}：今天有 {Count} 個部族日常可做，開始",
                label, plan!.Batch.Count);
            Start(label);
            return true;
        }

        string details = plan is { NotQueued.Count: > 0 } ? string.Join("；", plan.NotQueued) : "勾選的部族今天都沒有任務";
        string reason = decision switch
        {
            AlliedDailyIpcAlreadyRunning => "Questionable 已經在跑",
            AlliedDailyIpcCannotStart => $"現在不能開始：{blocker}",
            AlliedDailyIpcNoSocieties => "設定裡沒有勾選任何要自動排入日常的友好部族",
            AlliedDailyIpcNoAllowance => $"今日友好部族任務次數已用完（{details}）",
            AlliedDailyIpcAllDone => $"勾選的部族今天能接的都做完了（{details}）",
            _ => $"勾選的部族今天沒有能接的任務（{details}）",
        };
        LogOrDefer(LogLevel.Information, "[部族日常] {Label}：不開始，{Reason}", label, reason);
        return false;
    }

    /// <summary>開始了也不會先做部族日常、或根本跑不起來的情況；<see langword="null"/>＝可以開始。</summary>
    private string? GetAlliedDailyStartBlocker()
    {
        if (!_configuration.IsPluginSetupComplete())
        {
            return "還沒完成首次設定";
        }

        if (!_clientState.IsLoggedIn)
        {
            return "還沒登入角色";
        }

        // 模擬／指定下一個／採集任務排在批次選任務之前（見 CurrentQuestDetails），開始會先做那個。
        if (CurrentQuestDetails is { Type: not ECurrentQuestType.Normal } other)
        {
            return $"已指定 {other.Progress.Quest.Id}（{other.Type}）要先做";
        }

        return null;
    }

    /// <summary>
    /// 依 <see cref="Configuration.GeneralConfiguration.AutoQueueAlliedSocietyDailies"/> 把今天的友好部族日常排進優先清單，
    /// 並記成這一次的批次（選任務見 <see cref="TryPickAlliedDailyBatchQuest"/>）。框架執行緒呼叫。
    /// </summary>
    /// <remarks>排哪些由 <see cref="PlanAlliedSocietyDailies"/> 決定，與 <see cref="TryStartAlliedSocietyDailies"/> 共用。</remarks>
    private void QueueAlliedSocietyDailies()
    {
        HashSet<EAlliedSociety> societies = _configuration.General.AutoQueueAlliedSocietyDailies;
        if (societies.Count == 0)
        {
            lock(_progressLock)
            {
                _alliedDailyBatch.Clear();
            }

            return;
        }

        AlliedDailyPlan? plan = PlanAlliedSocietyDailies();
        if (plan == null)
        {
            LogOrDefer(LogLevel.Information, "[部族日常] 沒有排入：讀不到任務資料（QuestManager 為空）");
            return;
        }

        foreach ((ElementId questId, EAlliedSociety _) in plan.Batch)
        {
            AddQuestPriority(questId);
        }

        lock(_progressLock)
        {
            _alliedDailyBatch.Clear();
            _alliedDailyBatch.AddRange(plan.Batch);
            _alliedDailyBatchIdleLogged = false;
            _alliedDailyBatchStateKey = null;
            _alliedDailyLastPickKind = 0;
            _alliedDailyRouteKey = null;
            foreach (ElementId id in _alliedDailyTurnInReady.Keys.ToList())
            {
                if (!_questFunctions.IsQuestAccepted(id))
                {
                    _alliedDailyTurnInReady.Remove(id);
                }
            }

            foreach (ElementId id in _alliedDailyResumeStep.Keys.ToList())
            {
                if (!_questFunctions.IsQuestAccepted(id))
                {
                    _alliedDailyResumeStep.Remove(id);
                }
            }
        }

        string reasons = plan.NotQueued.Count > 0 ? string.Join("；", plan.NotQueued) : "勾選的部族今天都沒有任務";
        if (plan.Queued.Count > 0)
        {
            LogOrDefer(LogLevel.Information, "[部族日常] 排入：{Quests}（今日剩 {Allowance} 次）",
                string.Join("、", plan.Queued), plan.Allowance);
            if (plan.NotQueued.Count > 0)
            {
                LogOrDefer(LogLevel.Information, "[部族日常] 沒排入的：{Reasons}", reasons);
            }
        }
        else
        {
            LogOrDefer(LogLevel.Information, "[部族日常] 沒有排入任何任務（今日剩 {Allowance} 次）：{Reasons}",
                plan.Allowance, reasons);
        }
    }

    private sealed class AlliedDailyPlan
    {
        public required uint Allowance { get; init; }
        public List<(ElementId QuestId, EAlliedSociety Society)> Batch { get; } = [];

        /// <summary>每個可接清單裡的任務一個 <c>AlliedDailyPlan*</c>，跨部族依排入順序。</summary>
        public List<int> Outcomes { get; } = [];
        public List<string> Queued { get; } = [];
        public List<string> NotQueued { get; } = [];
    }

    /// <summary>只讀不改：算出這次「開始」會排入哪些部族日常。讀不到 QuestManager 時回 <see langword="null"/>。</summary>
    private AlliedDailyPlan? PlanAlliedSocietyDailies()
    {
        HashSet<EAlliedSociety> societies = _configuration.General.AutoQueueAlliedSocietyDailies;
        uint allowance;
        unsafe
        {
            QuestManager* questManager = QuestManager.Instance();
            if (questManager == null)
            {
                return null;
            }

            allowance = questManager->GetBeastTribeAllowance();
        }

        AlliedDailyPlan plan = new() { Allowance = allowance };
        List<(bool HasRoute, bool Accepted, bool Ready, bool DoneToday)> facts = [];
        List<Quest?> quests = [];
        List<int> outcomes = [];
        uint remaining = allowance;
        foreach (EAlliedSociety society in Enum.GetValues<EAlliedSociety>())
        {
            if (society == EAlliedSociety.None || !societies.Contains(society))
            {
                continue;
            }

            List<QuestId> available = _alliedSocietyQuestFunctions.GetAvailableAlliedSocietyQuests(society);
            if (available.Count == 0)
            {
                plan.NotQueued.Add($"{society}：今天沒有可接的任務（聲望未解鎖？）");
                continue;
            }

            facts.Clear();
            quests.Clear();
            foreach (QuestId questId in available)
            {
                bool hasRoute = _questRegistry.TryGetQuest(questId, out Quest? quest);
                bool accepted = _questFunctions.IsQuestAccepted(questId);
                bool ready = hasRoute && !accepted && _questFunctions.IsReadyToAcceptQuest(questId);
                facts.Add((hasRoute, accepted, ready, IsDailyQuestCompletedToday(questId)));
                quests.Add(quest);
            }

            outcomes.Clear();
            PlanAlliedDailySociety(facts, ref remaining, outcomes);
            for (int i = 0; i < available.Count; ++i)
            {
                QuestId questId = available[i];
                int outcome = outcomes[i];
                plan.Outcomes.Add(outcome);
                if (outcome == AlliedDailyPlanNoRoute)
                {
                    plan.NotQueued.Add($"{society} {questId}：沒有路線");
                    continue;
                }

                string name = $"{questId} {quests[i]!.Info.Name}";
                switch (outcome)
                {
                    case AlliedDailyPlanQueueAccepted:
                        plan.Batch.Add((questId, society));
                        plan.Queued.Add($"{name}（已接）");
                        break;
                    case AlliedDailyPlanQueue:
                        plan.Batch.Add((questId, society));
                        plan.Queued.Add(name);
                        break;
                    case AlliedDailyPlanDoneToday:
                        plan.NotQueued.Add($"{name}：今天已完成");
                        break;
                    case AlliedDailyPlanNotAcceptable:
                        plan.NotQueued.Add($"{name}：目前不能接");
                        break;
                    case AlliedDailyPlanNoAllowance:
                        plan.NotQueued.Add($"{name}：今日次數已用完");
                        break;
                    default:
                        plan.NotQueued.Add($"{name}：{society} 今天已接滿 {MaxDailyQuestsPerAlliedSociety} 個");
                        break;
                }
            }
        }

        return plan;
    }

    internal const int AlliedDailyPlanQueue = 1;
    internal const int AlliedDailyPlanQueueAccepted = 2;
    internal const int AlliedDailyPlanNoRoute = 3;
    internal const int AlliedDailyPlanDoneToday = 4;
    internal const int AlliedDailyPlanNotAcceptable = 5;
    internal const int AlliedDailyPlanNoAllowance = 6;
    internal const int AlliedDailyPlanSocietyFull = 7;

    /// <summary><see cref="PlanAlliedSocietyDailies"/> 對單一部族的純邏輯部分（不碰遊戲狀態，離線可測）。</summary>
    /// <param name="quests">該部族今天的可接清單；<c>Ready</c> 只在有路線且未接時有意義。</param>
    /// <param name="remaining">今日剩餘次數，跨部族遞減。</param>
    /// <param name="outcomes">依 <paramref name="quests"/> 順序附加一個 <c>AlliedDailyPlan*</c>。</param>
    /// <remarks>已接未交的照樣列入（不占次數）；還沒接的受「今日剩餘次數」與「每部族每天 3 個」兩個上限限制。</remarks>
    internal static void PlanAlliedDailySociety(
        IReadOnlyList<(bool HasRoute, bool Accepted, bool Ready, bool DoneToday)> quests, ref uint remaining,
        List<int> outcomes)
    {
        int usedToday = 0;
        foreach ((bool _, bool accepted, bool _, bool doneToday) in quests)
        {
            if (accepted || doneToday)
            {
                usedToday++;
            }
        }

        int societySlots = Math.Max(0, MaxDailyQuestsPerAlliedSociety - usedToday);
        foreach ((bool hasRoute, bool accepted, bool ready, bool doneToday) in quests)
        {
            if (!hasRoute)
            {
                outcomes.Add(AlliedDailyPlanNoRoute);
            }
            else if (accepted)
            {
                outcomes.Add(AlliedDailyPlanQueueAccepted);
            }
            else if (!ready)
            {
                outcomes.Add(doneToday ? AlliedDailyPlanDoneToday : AlliedDailyPlanNotAcceptable);
            }
            else if (remaining == 0)
            {
                outcomes.Add(AlliedDailyPlanNoAllowance);
            }
            else if (societySlots == 0)
            {
                outcomes.Add(AlliedDailyPlanSocietyFull);
            }
            else
            {
                outcomes.Add(AlliedDailyPlanQueue);
                remaining--;
                societySlots--;
            }
        }
    }

    internal const int AlliedDailyIpcStart = 0;
    internal const int AlliedDailyIpcAlreadyRunning = 1;
    internal const int AlliedDailyIpcCannotStart = 2;
    internal const int AlliedDailyIpcNoSocieties = 3;
    internal const int AlliedDailyIpcNoAllowance = 4;
    internal const int AlliedDailyIpcAllDone = 5;
    internal const int AlliedDailyIpcNothingAvailable = 6;

    /// <summary><see cref="TryStartAlliedSocietyDailies"/> 的純邏輯部分（不碰遊戲狀態，離線可測）。</summary>
    /// <param name="outcomes"><see cref="PlanAlliedDailySociety"/> 跨部族的結果；有排入的才回 <see cref="AlliedDailyIpcStart"/>。</param>
    internal static int DecideAlliedDailyIpcStart(bool running, bool canStart, int selectedSocieties,
        IReadOnlyList<int> outcomes)
    {
        if (running)
        {
            return AlliedDailyIpcAlreadyRunning;
        }

        if (!canStart)
        {
            return AlliedDailyIpcCannotStart;
        }

        if (selectedSocieties == 0)
        {
            return AlliedDailyIpcNoSocieties;
        }

        bool noAllowance = false;
        bool doneToday = false;
        foreach (int outcome in outcomes)
        {
            if (outcome is AlliedDailyPlanQueue or AlliedDailyPlanQueueAccepted)
            {
                return AlliedDailyIpcStart;
            }

            noAllowance |= outcome == AlliedDailyPlanNoAllowance;
            doneToday |= outcome is AlliedDailyPlanDoneToday or AlliedDailyPlanSocietyFull;
        }

        return noAllowance ? AlliedDailyIpcNoAllowance
            : doneToday ? AlliedDailyIpcAllDone
            : AlliedDailyIpcNothingAvailable;
    }

    private static unsafe bool IsDailyQuestCompletedToday(QuestId questId)
    {
        QuestManager* questManager = QuestManager.Instance();
        return questManager != null && questManager->IsDailyQuestCompleted(questId.Value);
    }

    /// <summary>
    /// 自動排入的部族日常用固定順序選任務：一次處理一個部族（批次順序），該部族還有能接的就依序先接完；
    /// 都接了之後先繼續目前那個，否則依批次順序做下一個已接的。
    /// </summary>
    /// <remarks>
    /// 只在持 <see cref="_progressLock"/> 時呼叫（記錄一律進 <paramref name="pending"/>）。
    /// 優先清單裡排在前面、而且不屬於本批次的任務（<paramref name="manualPick"/>）照舊讓它先做；
    /// 使用者從優先清單移除的任務不再選。
    /// </remarks>
    private bool TryPickAlliedDailyBatchQuest(ElementId? manualPick, List<PendingLog> pending,
        [NotNullWhen(true)] out ElementId? questId, out byte sequence, [NotNullWhen(true)] out string? source)
    {
        questId = null;
        sequence = 0;
        source = null;
        if (_alliedDailyBatch.Count == 0 || (manualPick != null && !IsInAlliedDailyBatch(manualPick)))
        {
            return false;
        }

        _alliedDailyBatchStates.Clear();
        int startedIndex = -1;
        for (int i = 0; i < _alliedDailyBatch.Count; ++i)
        {
            (ElementId id, EAlliedSociety society) = _alliedDailyBatch[i];
            _alliedDailyBatchStates.Add(((int)society, GetAlliedDailyBatchState(id)));
            if (StartedQuest != null && StartedQuest.Quest.Id == id)
            {
                startedIndex = i;
            }
        }

        LogAlliedDailyBatchStateIfChanged(pending);
        if (!_configuration.General.OptimizeAlliedSocietyDailyRoute ||
            !TryPickAlliedDailyRoute(startedIndex, pending, out int index, out int kind))
        {
            index = PickAlliedDailyBatchIndex(_alliedDailyBatchStates, startedIndex, _alliedDailyLastPickKind,
                out kind);
        }

        _alliedDailyLastPickKind = index < 0 ? 0 : kind;
        if (index < 0)
        {
            if (!_alliedDailyBatchIdleLogged)
            {
                _alliedDailyBatchIdleLogged = true;
                pending.Add(new PendingLog(LogLevel.Information,
                    "[部族日常] 排入的 {Count} 個任務都已交完或不在優先清單裡，改回一般的選任務規則",
                    [_alliedDailyBatch.Count], null));
            }

            return false;
        }

        _alliedDailyBatchIdleLogged = false;
        questId = _alliedDailyBatch[index].QuestId;
        source = kind switch
        {
            AlliedDailyPickAccept => "部族日常：接任務",
            AlliedDailyPickContinue => "部族日常：繼續",
            AlliedDailyPickRoute => "部族日常：路線",
            _ => "部族日常：下一個"
        };
        sequence = kind == AlliedDailyPickAccept ? (byte)0 : _questFunctions.GetQuestProgressInfo(questId)?.Sequence ?? 0;
        return true;
    }

    /// <summary>批次裡每個任務的「可接／已接／其他」有變才寫一行，用來對照實機的選任務順序。</summary>
    private void LogAlliedDailyBatchStateIfChanged(List<PendingLog> pending)
    {
        HashCode hash = new();
        foreach ((int _, int state) in _alliedDailyBatchStates)
        {
            hash.Add(state);
        }

        int key = hash.ToHashCode();
        if (key == _alliedDailyBatchStateKey)
        {
            return;
        }

        _alliedDailyBatchStateKey = key;
        string[] parts = new string[_alliedDailyBatch.Count];
        for (int i = 0; i < parts.Length; ++i)
        {
            string state = _alliedDailyBatchStates[i].State switch
            {
                AlliedDailyReady => "可接",
                AlliedDailyAccepted => "已接",
                _ => "已交或已移除"
            };
            parts[i] = $"{_alliedDailyBatch[i].QuestId}={state}";
        }

        pending.Add(new PendingLog(LogLevel.Information, "[部族日常] 狀態：{States}（目前任務 {StartedQuestId}）",
            [string.Join("、", parts), StartedQuest?.Quest.Id.ToString() ?? "-"], null));
    }

    internal const int AlliedDailyDone = 0;
    internal const int AlliedDailyReady = 1;
    internal const int AlliedDailyAccepted = 2;
    internal const int AlliedDailyPickAccept = 1;
    internal const int AlliedDailyPickContinue = 2;
    internal const int AlliedDailyPickNext = 3;
    internal const int AlliedDailyPickRoute = 4;

    /// <summary><see cref="TryPickAlliedDailyBatchQuest"/> 的純邏輯部分（不碰遊戲狀態，離線可測）。</summary>
    /// <param name="items">批次順序的（部族、狀態）；同一部族必須相鄰。</param>
    /// <param name="startedIndex">目前任務在批次裡的位置；不在批次裡＝-1。</param>
    /// <param name="lastKind">上一次的結果種類。剛接完任務（<see cref="AlliedDailyPickAccept"/>）時不黏著目前任務，
    /// 改從批次第一個已接的開始做，順序才會跟排入順序一樣。</param>
    /// <returns>選中的位置；-1＝批次裡沒有能做的。</returns>
    internal static int PickAlliedDailyBatchIndex(IReadOnlyList<(int Society, int State)> items, int startedIndex,
        int lastKind, out int kind)
    {
        kind = 0;
        if (lastKind == AlliedDailyPickAccept)
        {
            startedIndex = -1;
        }

        int activeSociety = -1;
        int firstReady = -1;
        int firstAccepted = -1;
        bool startedIsActive = false;
        for (int i = 0; i < items.Count; ++i)
        {
            (int society, int state) = items[i];
            if (state == AlliedDailyDone)
            {
                continue;
            }

            if (activeSociety < 0)
            {
                activeSociety = society;
            }
            else if (society != activeSociety)
            {
                break;
            }

            if (state == AlliedDailyReady)
            {
                if (firstReady < 0)
                {
                    firstReady = i;
                }
            }
            else
            {
                if (firstAccepted < 0)
                {
                    firstAccepted = i;
                }

                startedIsActive |= i == startedIndex;
            }
        }

        if (firstReady >= 0)
        {
            kind = AlliedDailyPickAccept;
            return firstReady;
        }

        if (startedIsActive)
        {
            kind = AlliedDailyPickContinue;
            return startedIndex;
        }

        if (firstAccepted >= 0)
        {
            kind = AlliedDailyPickNext;
            return firstAccepted;
        }

        return -1;
    }

    /// <summary>
    /// 路線優化版的選任務（<see cref="AlliedDailyRoute.Pick"/>）。輸入沒變就沿用上次結果：
    /// 位置與背包只在「批次狀態、序列或目前任務的步驟變了」那一刻讀一次，同一個決定不會一幀一幀改來改去。
    /// </summary>
    /// <returns><see langword="false"/>＝從來沒拿到過玩家位置，改用固定順序。</returns>
    private bool TryPickAlliedDailyRoute(int startedIndex, List<PendingLog> pending, out int index, out int kind)
    {
        index = -1;
        kind = 0;
        HashCode hash = new();
        hash.Add(startedIndex);
        hash.Add(_alliedDailyLastPickKind);
        hash.Add(StartedQuest?.Sequence ?? 0);
        hash.Add(StartedQuest?.Step ?? -1);
        for (int i = 0; i < _alliedDailyBatch.Count; ++i)
        {
            int state = _alliedDailyBatchStates[i].State;
            hash.Add(state);
            if (state == AlliedDailyAccepted &&
                AlliedDailyRouteEstimator.TryReadQuestWork(_alliedDailyBatch[i].QuestId, out byte sequence, out _))
            {
                hash.Add(sequence);
            }
        }

        int key = hash.ToHashCode();
        if (key == _alliedDailyRouteKey)
        {
            index = _alliedDailyRouteIndex;
            kind = _alliedDailyRouteKind;
            return true;
        }

        // 讀圖中拿不到玩家：用最後一次看到的位置排，不退回固定順序（否則會跟剛排好的路線打架）。
        uint territoryId;
        Vector3 position;
        if (_objectTable[0] is { } player)
        {
            territoryId = _clientState.TerritoryType;
            position = player.Position;
            _alliedDailyLastPlayer = (territoryId, position);
        }
        else if (_alliedDailyLastPlayer is { } last)
        {
            (territoryId, position) = last;
        }
        else
        {
            return false;
        }

        _alliedDailyRouteItems.Clear();
        _alliedDailyRouteNotes.Clear();
        for (int i = 0; i < _alliedDailyBatch.Count; ++i)
        {
            (ElementId id, EAlliedSociety society) = _alliedDailyBatch[i];
            if (_alliedDailyBatchStates[i].State != AlliedDailyAccepted)
            {
                _alliedDailyResumeStep.Remove(id);
            }

            int startedStep = i == startedIndex && StartedQuest != null &&
                              AlliedDailyRouteEstimator.TryReadQuestWork(id, out byte sequence, out _) &&
                              StartedQuest.Sequence == sequence
                ? StartedQuest.Step
                : -1;
            _alliedDailyRouteItems.Add(_alliedDailyRouteEstimator.Estimate(id, (int)society,
                _alliedDailyBatchStates[i].State, startedStep, _alliedDailyTurnInReady,
                out AlliedDailyRouteEstimator.Note note));
            _alliedDailyRouteNotes.Add(note);
        }

        index = AlliedDailyRoute.Pick(_alliedDailyRouteItems, startedIndex, _alliedDailyLastPickKind, territoryId,
            position, _alliedDailyRoutePlan, out kind, out bool deferredTurnIn);
        _alliedDailyRouteKey = key;
        _alliedDailyRouteIndex = index;
        _alliedDailyRouteKind = kind;
        if (kind == AlliedDailyPickRoute)
        {
            if (deferredTurnIn && StartedQuest != null &&
                AlliedDailyRouteEstimator.TryReadQuestWork(StartedQuest.Quest.Id, out byte startedSequence, out _) &&
                StartedQuest.Sequence == startedSequence)
            {
                _alliedDailyResumeStep[StartedQuest.Quest.Id] = (startedSequence, StartedQuest.Step);
            }

            LogAlliedDailyRoute(startedIndex, deferredTurnIn, territoryId, position, pending);
        }

        return true;
    }

    /// <summary>路線換到下一個任務時寫一行：排出來的順序、理由、每一段的估計距離，給使用者回報用。</summary>
    private void LogAlliedDailyRoute(int startedIndex, bool deferredTurnIn, uint territoryId, Vector3 position,
        List<PendingLog> pending)
    {
        List<string> work = [];
        List<string> turnIns = [];
        foreach (int i in _alliedDailyRoutePlan)
        {
            AlliedDailyRoute.Item item = _alliedDailyRouteItems[i];
            AlliedDailyRouteEstimator.Note note = _alliedDailyRouteNotes[i];
            float cost = AlliedDailyRoute.TravelCost(item, territoryId, position, out bool viaTeleport);
            string distance = item.Position == null ? "位置不明"
                : item.TerritoryId != territoryId ? "在別的區域"
                : string.Create(CultureInfo.InvariantCulture, $"離目前位置約 {cost:F0} 米{(viaTeleport ? "，傳送" : "")}");
            string text = $"{_alliedDailyBatch[i].QuestId} 序列 {note.Sequence} 步驟 {note.Step}（{note.Label}，{distance}）";
            (item.Segment == AlliedDailyRoute.SegmentWork ? work : turnIns).Add(text);
        }

        int next = _alliedDailyRoutePlan[0];
        string reason = deferredTurnIn
            ? $"{_alliedDailyBatch[startedIndex].QuestId} 只剩交任務，同一位委託人還有任務有目標要做，交任務留到最後一起交"
            : _alliedDailyRouteItems[next].Segment == AlliedDailyRoute.SegmentWork
                ? "還有目標的任務依「做完全部再回去交」總距離最短的順序"
                : "目標都做完了，依距離交任務";
        pending.Add(new PendingLog(LogLevel.Information,
            "[部族日常] 路線：下一個 {QuestId}；理由：{Reason}；目標順序：{Work}；交任務：{TurnIns}",
            [_alliedDailyBatch[next].QuestId, reason, work.Count > 0 ? string.Join(" → ", work) : "無",
                turnIns.Count > 0 ? string.Join("、", turnIns) : "無"], null));
    }

    /// <summary>取出（並清掉）<see cref="_alliedDailyResumeStep"/> 裡這個任務在這個序列的步驟；沒有＝0（從頭）。</summary>
    private int TakeAlliedDailyResumeStep(ElementId questId, byte sequence)
    {
        if (!_alliedDailyResumeStep.Remove(questId, out (byte Sequence, int Step) resume) || resume.Sequence != sequence)
        {
            return 0;
        }

        return resume.Step;
    }

    private int GetAlliedDailyBatchState(ElementId id)
    {
        if (!IsInManualPriorityQuests(id))
        {
            return AlliedDailyDone;
        }

        if (_questFunctions.IsReadyToAcceptQuest(id))
        {
            return AlliedDailyReady;
        }

        return _questFunctions.IsQuestAccepted(id) ? AlliedDailyAccepted : AlliedDailyDone;
    }

    /// <summary>剛交的任務屬於這一次的部族日常批次，而且批次裡已經沒有可接或已接的任務。持鎖呼叫。</summary>
    private bool IsAlliedDailyBatchFinishedBy(ElementId finishedQuestId)
    {
        bool finishedInBatch = IsInAlliedDailyBatch(finishedQuestId);
        bool anyLeft = false;
        if (finishedInBatch)
        {
            foreach ((ElementId id, EAlliedSociety _) in _alliedDailyBatch)
            {
                if (GetAlliedDailyBatchState(id) != AlliedDailyDone)
                {
                    anyLeft = true;
                    break;
                }
            }
        }

        return ShouldStopAfterAlliedDailyBatch(finishedInBatch, _alliedDailyBatch.Count, anyLeft);
    }

    /// <summary><see cref="IsAlliedDailyBatchFinishedBy"/> 的純邏輯部分（離線可測）。</summary>
    internal static bool ShouldStopAfterAlliedDailyBatch(bool finishedInBatch, int batchCount, bool anyLeft) =>
        finishedInBatch && batchCount > 0 && !anyLeft;

    private bool IsInAlliedDailyBatch(ElementId id)
    {
        foreach ((ElementId batchId, EAlliedSociety _) in _alliedDailyBatch)
        {
            if (batchId == id)
            {
                return true;
            }
        }

        return false;
    }

    private bool IsInManualPriorityQuests(ElementId id)
    {
        foreach (Quest quest in ManualPriorityQuests)
        {
            if (quest.Id == id)
            {
                return true;
            }
        }

        return false;
    }

    public void StartGatheringQuest(string label)
    {
        using IDisposable? scope = _logger.BeginScope($"GQ/{label}");
        RedeemRewardItems.ResetAttemptedItems();
        AutomationType = EAutomationType.GatheringOnly;
        ExecuteNextStep();
    }

    public void StartSingleQuest(string label)
    {
        using IDisposable? scope = _logger.BeginScope($"SQ/{label}");
        RedeemRewardItems.ResetAttemptedItems();
        AutomationType = EAutomationType.SingleQuestA;
        ExecuteNextStep();
    }

    public void StartSingleStep(string label)
    {
        using IDisposable? scope = _logger.BeginScope($"SS/{label}");
        RedeemRewardItems.ResetAttemptedItems();
        AutomationType = EAutomationType.Manual;
        ExecuteNextStep();
    }

    /// <summary>
    /// 登出時清空 <c>RedeemRewardItems</c> 的「已經試過的道具」表。
    /// </summary>
    /// <remarks>
    /// 那張表的鍵只有道具 id，而「用不掉」的理由多半是角色自己的（已經學過那個表情、背包滿、
    /// 等級不夠）——換角色之後前一個角色的結論不該繼續套用，否則新角色的獎勵道具會安靜地永遠不被使用。
    /// <para>
    /// 🔑 這是第二道保險，不是唯一一道：四個 <c>Start*</c> 進入點本來就各清一次，而登出會在
    /// <see cref="Update"/> 裡走到 <c>StopAllDueToConditionFailed("Logged out")</c> 把自動化停掉，
    /// 所以正常流程下換完角色一定會再經過一次 <c>Start*</c>。但那個不變式靠的是
    /// 「以後每一個新的自動化進入點都記得清」，這裡把它釘在真正失效的那一刻。
    /// </para>
    /// <para>
    /// ⚠️ 本 pin 的 <c>IClientState.Logout</c> 是 hook <c>AgentLobby</c> 的 vtable 得來的，
    /// Setup 當下拿不到 AgentLobby 就整個 session 都不會觸發（Dalamud 自己會記一行）。
    /// 所以這一條是補強，上面那層 <c>Start*</c> 的清空不要拿掉。
    /// </para>
    /// </remarks>
    private void ClearRedeemAttemptsOnLogout(int type, int code)
    {
        int cleared = RedeemRewardItems.ResetAttemptedItems();
        if (cleared > 0)
        {
            _logger.LogInformation(
                "Logout (type {LogoutType}, code {LogoutCode}): cleared {ClearedRedeemAttempts} attempted reward item(s)",
                type, code, cleared);
        }
    }

    private void ExecuteNextStep()
    {
        _autoWaitingForNextQuest = false;
        ClearTasksInternal();

        if (TryPickPriorityQuest())
        {
            LogOrDefer(LogLevel.Information, "Using priority quest over current quest");
        }

        (QuestSequence? seq, QuestStep? step, bool createTasks) = GetNextStep();
        if (CurrentQuest == null || seq == null)
        {
            if (CurrentQuestDetails?.Progress.Quest.Id is SatisfactionSupplyNpcId &&
                CurrentQuestDetails?.Progress.Sequence == 1 &&
                CurrentQuestDetails?.Progress.Step == 255 &&
                CurrentQuestDetails?.Type == ECurrentQuestType.Gathering)
            {
                LogOrDefer(LogLevel.Information, "Completed delivery quest");
                SetGatheringQuest(null);
                Stop("Gathering quest complete");
            }
            else
            {
                LogOrDefer(LogLevel.Warning,
                    "Could not retrieve next quest step, not doing anything [{QuestId}, {Sequence}, {Step}]",
                    CurrentQuest?.Quest.Id, CurrentQuest?.Sequence, CurrentQuest?.Step);
            }

            if (CurrentQuest == null || !createTasks)
            {
                return;
            }
        }

        // 🔴 傳 RunOrDefer 進去：這一支從 UpdateCurrentQuestLocked 進來時是持著 _progressLock 的，
        //    對 vnavmesh 打 Path.Stop、關自動前進、以及那幾行記錄會被收進延後清單、出鎖之後才做；
        //    狀態重設（ResetPathfinding／Destination／快照）仍然當場同步做。
        //    不在鎖裡呼叫時 RunOrDefer 就地執行。
        _movementController.Stop(RunOrDefer);
        _combatController.Stop("Execute next step", RunOrDefer);
        _gatheringController.Stop("Execute next step");

        try
        {
            // 🔴 傳 RunOrDefer 進去：工作清單仍然同步算好同步回來，
            //    CreateTasks 裡的聊天輸出與記錄被收進延後清單、出鎖之後才寫。
            foreach(ITask task in _taskCreator.CreateTasks(CurrentQuest.Quest, CurrentQuest.Sequence, seq, step,
                RunOrDefer))
            {
                if (SimulatedQuest != null)
                {
                    string repr = task.ToString() ?? "";
                    string[] SimSkip = ["Interact", "Action", "Emote", "Craft", "Unmount"];
                    if (repr.Contains('(') && SimSkip.Contains(repr[..repr.IndexOf('(')]) && step != null && step.TargetTerritoryId.Equals(step.TerritoryId))
                    {
                        LogOrDefer(LogLevel.Information, $"Skipping {repr} due to simulation");
                        continue;
                    }
                }
                _taskQueue.Enqueue(task);
            }

            ResetAutoRefreshState();
        }
        catch(Exception e)
        {
            LogOrDefer(LogLevel.Error, e, "Failed to create tasks");
            // 這個 catch 從 IPC 端點 Questionable.StartQuest／StartSingleQuest 可達
            //（QuestionableIpc.StartQuest -> StartSingleQuest -> ExecuteNextStep），而 IPC 跑在
            // 呼叫端外掛的執行緒上。見 ChatGuiExtensions：在 framework 執行緒上就地執行。
            RunOrDefer(() => _chatGui.PrintErrorOnFrameworkThread(_framework,
                "Failed to start next task sequence, please check /xllog for details.", CommandHandler.MessageTag,
                CommandHandler.TagColor));
            Stop("Tasks failed to create", true);
        }
    }

    public string ToStatString()
    {
        return _taskQueue.CurrentTaskExecutor?.CurrentTask is { } currentTask
            ? $"{currentTask} (+{_taskQueue.RemainingTasks.Count()})"
            : $"- (+{_taskQueue.RemainingTasks.Count()})";
    }

    public bool HasCurrentTaskExecutorMatching<T>([NotNullWhen(true)] out T? task)
    where T : class, ITaskExecutor
    {
        if (_taskQueue.CurrentTaskExecutor is T t)
        {
            task = t;
            return true;
        }
        else
        {
            task = null;
            return false;
        }
    }

    public bool HasCurrentTaskMatching<T>([NotNullWhen(true)] out T? task)
    where T : class, ITask
    {
        if (_taskQueue.CurrentTaskExecutor?.CurrentTask is T t)
        {
            task = t;
            return true;
        }
        else
        {
            task = null;
            return false;
        }
    }

    public void Skip(ElementId elementId, byte currentQuestSequence)
    {
        List<PendingLog> pending = [];
        List<PendingLog>? previousScope = _deferralScope;
        _deferralScope = pending;
        try
        {
            SkipLocked(elementId, currentQuestSequence, pending);
        }
        finally
        {
            _deferralScope = previousScope;
            EmitPending(pending);
        }
    }

    /// <summary><see cref="Skip"/> 持鎖的那一段。</summary>
    /// <remarks>
    /// 🔴 這裡面持有 <see cref="_progressLock"/>：要寫的東西用 <c>pending.Add</c> 或
    /// <see cref="LogOrDefer"/>，要做的副作用用 <see cref="RunOrDefer"/>。
    /// <para>
    /// 📌 這個鎖裡呼叫的 <c>Stop</c> 也會自己延後，並把 <c>BeginScope</c> 的
    /// <c>Stop/{label}</c> 前綴一起帶進延後的那一段裡重建。
    /// </para>
    /// </remarks>
    private void SkipLocked(ElementId elementId, byte currentQuestSequence, List<PendingLog> pending)
    {
        lock(_progressLock)
        {
            if (_taskQueue.CurrentTaskExecutor?.CurrentTask is ISkippableTask)
            {
                _taskQueue.CurrentTaskExecutor = null;
            }
            else if (_taskQueue.CurrentTaskExecutor != null)
            {
                _taskQueue.CurrentTaskExecutor = null;
                while(_taskQueue.TryPeek(out ITask? task))
                {
                    _taskQueue.TryDequeue(out ITask? _);
                    if (task is ISkippableTask)
                    {
                        return;
                    }
                }

                if (_taskQueue.AllTasksComplete)
                {
                    Stop("Skip");
                    IncreaseStepCountLocked(elementId, currentQuestSequence, pending);
                }
            }
            else
            {
                Stop("SkipNx");
                IncreaseStepCountLocked(elementId, currentQuestSequence, pending);
            }
        }
    }

    public void SkipSimulatedTask()
    {
        _taskQueue.CurrentTaskExecutor = null;
    }

    public bool IsInterruptible()
    {
        if (AutomationType is EAutomationType.SingleQuestA or EAutomationType.SingleQuestB)
        {
            return false;
        }

        (QuestProgress Progress, ECurrentQuestType Type)? details = CurrentQuestDetails;
        if (details == null)
        {
            return false;
        }

        (QuestProgress currentQuest, ECurrentQuestType type) = details.Value;
        if (type != ECurrentQuestType.Normal || !currentQuest.Quest.Root.Interruptible || currentQuest.Sequence == 0)
        {
            return false;
        }

        if (ManualPriorityQuests.Contains(currentQuest.Quest))
        {
            return false;
        }

        // "ifrit bleeds, we can kill it" isn't listed as priority quest, as we accept it during the MSQ 'Moving On'
        // the rest are priority quests, but that's fine here
        if (QuestData.HardModePrimals.Contains(currentQuest.Quest.Id))
        {
            return false;
        }

        if (currentQuest.Quest.Info.AlliedSociety != EAlliedSociety.None)
        {
            return false;
        }

        QuestSequence? currentSequence = currentQuest.Quest.FindSequence(currentQuest.Sequence);
        if (currentQuest.Step > 0)
        {
            return false;
        }

        QuestStep? currentStep = currentSequence?.FindStep(currentQuest.Step);
        return currentStep?.AetheryteShortcut != null &&
               (currentStep.SkipConditions?.AetheryteShortcutIf?.QuestsCompleted.Count ?? 0) == 0 &&
               (currentStep.SkipConditions?.AetheryteShortcutIf?.QuestsAccepted.Count ?? 0) == 0;
    }

    public bool TryPickPriorityQuest()
    {
        if (!IsInterruptible() || NextQuest != null || GatheringQuest != null || SimulatedQuest != null)
        {
            return false;
        }

        ElementId? priorityQuestId = _questFunctions.NextPriorityQuestsThatCanBeAccepted
            .Where(x => x.IsAvailable)
            .Select(x => x.QuestId)
            .FirstOrDefault();
        if (priorityQuestId == null)
        {
            return false;
        }

        // don't start a second priority quest until the first one is resolved
        if (StartedQuest != null && priorityQuestId == StartedQuest.Quest.Id)
        {
            return false;
        }

        if (_questRegistry.TryGetQuest(priorityQuestId, out Quest? quest))
        {
            SetNextQuest(quest);
            return true;
        }

        return false;
    }

    public void ImportQuestPriority(List<ElementId> questElements)
    {
        foreach(ElementId elementId in questElements)
        {
            if (_questRegistry.TryGetQuest(elementId, out Quest? quest) && !ManualPriorityQuests.Contains(quest))
            {
                ManualPriorityQuests.Add(quest);
            }
        }
    }
    public string ExportQuestPriority()
    {
        return string.Join(ClipboardSeparator, ManualPriorityQuests.Select(x => x.Id.ToString()));
    }

    public void ClearQuestPriority()
    {
        ManualPriorityQuests.Clear();
    }

    public bool AddQuestPriority(ElementId elementId)
    {
        // 優先清單也收老主顧交易（S）、友好部族日常（A）、解鎖連結（U）這些不是 QuestId 的項目
        // （手動挑選與匯入本來就收），所以只有 QuestId 才問「是不是友好部族日常任務」。
        if (_questRegistry.TryGetQuest(elementId, out Quest? quest) &&
            !ManualPriorityQuests.Contains(quest) &&
            ((elementId is QuestId questId && _questFunctions.IsDailyAlliedSocietyQuest(questId)) ||
             !_questFunctions.IsQuestComplete(elementId))
            )
        {
            ManualPriorityQuests.Add(quest);
        }
        return true;
    }

    public bool RemoveQuestPriority(ElementId elementId)
    {
        if (_questRegistry.TryGetQuest(elementId, out Quest? quest) && ManualPriorityQuests.Contains(quest))
        {
            ManualPriorityQuests.Remove(quest);
        }
        return true;
    }

    public bool InsertQuestPriority(int index, ElementId elementId)
    {
        try
        {
            if (_questRegistry.TryGetQuest(elementId, out Quest? quest) && !ManualPriorityQuests.Contains(quest))
            {
                ManualPriorityQuests.Insert(index, quest);
            }
            return true;
        }
        catch(Exception e)
        {
            _logger.LogError(e, "Failed to insert quest in priority list");
            // IPC 端點 Questionable.InsertQuestPriority 可達，見 ChatGuiExtensions。
            _chatGui.PrintErrorOnFrameworkThread(_framework, "Failed to insert quest in priority list, please check /xllog for details.", CommandHandler.MessageTag, CommandHandler.TagColor);
            return false;
        }
    }

    /// <summary>一次最多補幾個前置任務；也是視窗上把數字顯示成「N+」的門檻。</summary>
    public const int MaxPrerequisitesPerAdd = 100;

    /// <summary>單次查詢最多走訪幾個前置任務節點，避免整串已在清單裡的長鏈每次都全走一遍。</summary>
    private const int MaxPrerequisiteNodesVisited = 2000;

    /// <summary>
    /// 收集 <paramref name="quest"/> 尚未完成、也還沒排進優先清單的前置任務，
    /// 依「越前置的越前面」回傳——那就是該插進清單的順序。
    /// 🔴 用明確堆疊不用遞迴：台服任務表的前置鏈可達九百多層，遞迴會吃光繪製執行緒的堆疊。
    /// 上界到頂時回傳的是「最深的那一段」＝最該先做的，再按一次就接著補下一批。
    /// </summary>
    public List<Quest> GetMissingPrerequisiteQuests(Quest quest)
    {
        List<Quest> result = [];
        HashSet<ElementId> visited = [quest.Id];
        int budget = MaxPrerequisiteNodesVisited;

        // 後序走訪：子節點全部處理完才把自己加進 result，於是 result 天然是越前置的越前面。
        Stack<(Quest? Quest, List<QuestId> Pending, int Index)> stack = [];
        stack.Push((null, GetPrerequisitesToVisit(quest.Info), 0));

        while (stack.Count > 0)
        {
            (Quest? current, List<QuestId> pending, int index) = stack.Pop();
            if (index < pending.Count)
            {
                QuestId next = pending[index];
                stack.Push((current, pending, index + 1));

                if (--budget < 0)
                    break;

                // visited 同時是環的防護：PreviousQuests 是圖不是樹，而且 JSON 可以自己加邊。
                if (!visited.Add(next) || _questFunctions.IsQuestComplete(next))
                    continue;

                if (_questRegistry.TryGetQuest(next, out Quest? nextQuest))
                    stack.Push((nextQuest, GetPrerequisitesToVisit(nextQuest.Info), 0));
            }
            else if (current != null && !ManualPriorityQuests.Contains(current))
            {
                result.Add(current);
                if (result.Count >= MaxPrerequisitesPerAdd)
                    break;
            }
        }

        return result;
    }

    /// <summary>
    /// 這一層要往下走的前置。已經排進優先清單的仍然要走進去——不然使用者手動加過鏈中某一環之後，
    /// 它前面那一整段就永遠補不上；「不重複加入」是在收集端擋，不是在這裡擋。
    /// AtLeastOne 只要有一個選項已完成就當整層滿足；否則優先走已排進清單的那個選項。
    /// </summary>
    private List<QuestId> GetPrerequisitesToVisit(IQuestInfo questInfo)
    {
        if (questInfo.PreviousQuests.Count == 0)
            return [];

        if (questInfo.PreviousQuestJoin != EQuestJoin.AtLeastOne)
            return [.. questInfo.PreviousQuests.Select(x => x.QuestId)];

        if (questInfo.PreviousQuests.Any(x => _questFunctions.IsQuestComplete(x.QuestId)))
            return [];

        List<QuestId> options = [.. questInfo.PreviousQuests.Select(x => x.QuestId)];
        QuestId? chosen = options.FirstOrDefault(x => ManualPriorityQuests.Any(q => q.Id == x))
                          ?? options.FirstOrDefault(_questRegistry.IsKnownQuest);
        return chosen != null ? [chosen] : [];
    }

    /// <summary>
    /// 把 <paramref name="quest"/> 尚未完成、也還沒排進優先清單的前置任務依序插在它前面，
    /// 回傳實際新增的數量。
    /// </summary>
    public int AddMissingPrerequisites(Quest quest)
    {
        List<Quest> missing = GetMissingPrerequisiteQuests(quest);

        int insertIndex = ManualPriorityQuests.IndexOf(quest);
        if (insertIndex < 0)
            insertIndex = ManualPriorityQuests.Count;

        foreach(Quest prevQuest in missing)
        {
            ManualPriorityQuests.Insert(insertIndex, prevQuest);
            ++insertIndex;
        }

        _logger.LogInformation("Added {Count} prerequisite quest(s) before {QuestId}", missing.Count, quest.Id);
        return missing.Count;
    }

    public bool WasLastTaskUpdateWithin(TimeSpan timeSpan)
    {
        _logger.LogInformation("Last update: {Update}", _lastTaskUpdate);
        return IsRunning || DateTime.Now <= _lastTaskUpdate.Add(timeSpan);
    }

    private void OnConditionChange(ConditionFlag flag, bool value)
    {
        if (_taskQueue.CurrentTaskExecutor is IConditionChangeAware conditionChangeAware)
        {
            conditionChangeAware.OnConditionChange(flag, value);
        }
    }

    private void OnNormalToast(ref SeString message, ref ToastOptions options, ref bool isHandled)
    {
        _gatheringController.OnNormalToast(message);
    }

    protected override void HandleInterruption(object? sender, EventArgs e)
    {
        if (!IsRunning)
        {
            return;
        }

        if (AutomationType == EAutomationType.Manual)
        {
            return;
        }

        base.HandleInterruption(sender, e);
    }

    public bool StartGathering(uint npcId, uint itemId, Job classJob, int quantity = 1, ushort collectability = 0)
    {
        if (itemId > 1_000_000)
        {
            itemId -= 1_000_000;
        }

        if (itemId >= 500_000)
        {
            itemId -= 500_000;
        }

        SatisfactionSupplyInfo info = (SatisfactionSupplyInfo)_questData.GetAllByIssuerDataId(npcId)
            .Single(x => x is SatisfactionSupplyInfo);
        if (_questRegistry.TryGetQuest(info.QuestId, out Quest? quest))
        {
            QuestSequence sequence = quest.FindSequence(0)!;

            QuestStep switchClassStep = sequence.Steps.Single(x => x.InteractionType == EInteractionType.SwitchClass);
            switchClassStep.TargetClass = classJob switch
            {
                Job.MIN => EExtendedClassJob.Miner,
                Job.BTN => EExtendedClassJob.Botanist,
                var _ => throw new ArgumentOutOfRangeException(nameof(classJob), classJob, null)
            };

            QuestStep gatherStep = sequence.Steps.Single(x => x.InteractionType == EInteractionType.Gather);
            gatherStep.ItemsToGather =
            [
                new()
                {
                    ItemId = itemId,
                    ItemCount = quantity,
                    Collectability = collectability
                }
            ];
            SetGatheringQuest(quest);
            StartGatheringQuest("SatisfactionSupply prepare gathering");
            return true;
        }
        else
        {
            // IPC 端點 Questionable.StartGathering／StartGatheringComplex 可達，見 ChatGuiExtensions。
            _chatGui.PrintErrorOnFrameworkThread(_framework, $"No associated quest ({info.QuestId}).", "Questionable");
            return false;
        }
    }

    public override void Dispose()
    {
        _clientState.Logout -= ClearRedeemAttemptsOnLogout;
        _toastGui.ErrorToast -= OnErrorToast;
        _toastGui.Toast -= OnNormalToast;
        _condition.ConditionChange -= OnConditionChange;
        base.Dispose();
    }

    public sealed class QuestProgress
    {
        public QuestProgress(Quest quest, byte sequence = 0, int step = 0)
        {
            Quest = quest;
            SetSequence(sequence, step);
        }
        public Quest Quest { get; }
        public byte Sequence { get; private set; }
        public int Step { get; private set; }
        public StepProgress StepProgress { get; private set; } = new(DateTime.Now);

        public void SetSequence(byte sequence, int step = 0)
        {
            Sequence = sequence;
            SetStep(step);
        }

        public void SetStep(int step)
        {
            Step = step;
            StepProgress = new(DateTime.Now);
        }

        public void IncreasePointMenuCounter()
        {
            StepProgress = StepProgress with
            {
                PointMenuCounter = StepProgress.PointMenuCounter + 1
            };
        }
    }

    public sealed record StepProgress
    (
        DateTime StartedAt,
        int PointMenuCounter = 0);
}

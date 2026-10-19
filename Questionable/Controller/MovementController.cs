using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Services;
using ECommons.LanguageHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Questionable.Controller.NavigationOverrides;
using Questionable.Controller.Steps.Movement;
using Questionable.Data;
using Questionable.External;
using Questionable.Functions;
using Questionable.Model;
using Questionable.Model.Common;
using Questionable.Model.Common.Converter;
using Questionable.Model.Questing;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
namespace Questionable.Controller;

internal sealed class MovementController
(
    NavmeshIpc navmeshIpc,
    IClientState clientState,
    GameFunctions gameFunctions,
    ChatFunctions chatFunctions,
    ICondition condition,
    MovementOverrideController movementOverrideController,
    IObjectTable objectTable,
    AetheryteData aetheryteData,
    TerritoryData territoryData,
    ICommandManager commandManager,
    IChatGui chatGui,
    IServiceProvider serviceProvider,
    Configuration configuration,
    ILogger<MovementController> logger) : IDisposable
{
    public ICommandManager CommandManager { get; } = commandManager;
    public const float DefaultVerticalInteractionDistance = 1.95f;

    /// <summary>「先降落再重試」最多等多久；等不到就把失敗交出去。</summary>
    private const int LandAndRetryTimeoutSeconds = 10;

    /// <summary>降落動作被擋下時，每隔多久重送一次、總共送幾次（含第一次）。</summary>
    /// <remarks>🔴 刻意不是每幀重送——那是對遊戲送封包。間隔取 1 秒：動作鎖／讀條這類擋法都在一秒內清掉。
    /// <para>🔴 次數用完<b>不</b>提早放棄：降落動畫本身要好幾秒，何時交出失敗仍然只由
    /// <see cref="LandAndRetryTimeoutSeconds"/> 那條逾時決定。</para></remarks>
    private const double LandRetryIntervalSeconds = 1;

    /// <inheritdoc cref="LandRetryIntervalSeconds"/>
    private const int LandMaxAttempts = 5;

    private CancellationTokenSource? _cancellationTokenSource;
    private Task<List<Vector3>>? _pathfindTask;

    /// <summary>
    /// 水平距離已經進到 <see cref="DestinationData.StopDistance"/> 以內、但垂直距離檢查一直不通過的
    /// 起算時間。<see langword="null"/> 代表目前不在這個狀態。見 <see cref="GiveUpOnVerticalAlignmentAfterGrace"/>。
    /// </summary>
    private DateTime? _verticalStopCheckStuckSince;

    /// <summary>
    /// 已經進到目標物件的互動範圍、而且人完全沒再位移的起算時間與當時座標。
    /// <see langword="null"/> 代表目前不在這個狀態。
    /// </summary>
    private DateTime? _interactionRangeStuckSince;

    /// <inheritdoc cref="_interactionRangeStuckSince"/>
    private Vector3? _interactionRangeStuckAt;

    /// <summary>
    /// 尋路回 0 點而人還在飛行中時，先降落再重試一次：降落的截止時間，與要重試的那一個目的地。
    /// 兩個一起設、一起清；<see langword="null"/> 代表目前沒有待重試的降落。
    /// </summary>
    private DateTime? _landAndRetryTimeout;

    /// <inheritdoc cref="_landAndRetryTimeout"/>
    private DestinationData? _previousDestinationData;

    /// <summary>這一次「先降落再重試」已經送出幾次降落，與下一次可以重送的時間。</summary>
    private int _landAttempts;

    /// <inheritdoc cref="_landAttempts"/>
    private DateTime _landNextAttemptAt;

    public bool IsNavmeshReady
    {
        get
        {
            try
            {
                return navmeshIpc.IsReady;
            }
            catch(IpcNotReadyError)
            {
                return false;
            }
        }
    }

    public bool IsPathRunning
    {
        get
        {
            try
            {
                return navmeshIpc.IsPathRunning;
            }
            catch(IpcNotReadyError)
            {
                return false;
            }
        }
    }

    public bool IsPathfinding => _pathfindTask is { IsCompleted: false };

    /// <summary><see cref="IsPathRunning"/> 的每幀快照，見 <see cref="RefreshNavmeshSnapshot"/>。</summary>
    private volatile bool _pathRunningSnapshot;

    /// <summary><see cref="IsPathfinding"/> 的每幀快照，見 <see cref="RefreshNavmeshSnapshot"/>。</summary>
    private volatile bool _pathfindingSnapshot;

    /// <summary>
    /// 「vnavmesh 現在有沒有在跑路徑」的<b>快照</b>——讀這個不會打 IPC。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>存在的理由</b>：<see cref="IsPathRunning"/> 的 getter 是一次跨外掛 IPC。
    /// <c>QuestController.UpdateCurrentQuestLocked</c> 在<b>持有 <c>_progressLock</c> 的時候</b>讀它，
    /// 等於在我們每一幀都會拿的鎖裡面跑 vnavmesh 的碼。
    /// <para>
    /// 📌 新鮮度：<c>DalamudInitializer.FrameworkUpdate</c> 先呼叫 <see cref="RefreshNavmeshSnapshot"/>()
    /// 再呼叫 <c>_questController.Update()</c> ⇒ QuestController 讀到的是同一幀剛取樣的值。
    /// 🔴 那一行必須留在 <c>_questController.Update()</c> <b>之前</b>；移到後面會退化成「晚一幀」（仍然安全）。
    /// </para>
    /// <para>
    /// 📌 幀內一致性：<see cref="Stop"/> 與 <see cref="ResetPathfinding"/> 會<b>當場</b>把快照
    /// 設回 <see langword="false"/>（純本機寫入、不打 IPC），開始尋路／開始移動時也當場設成
    /// <see langword="true"/> ⇒ 鎖內停止移動之後再判斷，不會卡在「Path is running」。
    /// </para>
    /// </remarks>
    public bool IsPathRunningSnapshot => _pathRunningSnapshot;

    /// <inheritdoc cref="IsPathRunningSnapshot"/>
    public bool IsPathfindingSnapshot => _pathfindingSnapshot;

    /// <summary>
    /// 每幀取樣一次 vnavmesh 的路徑狀態。<b>只能在 framework 執行緒、而且不持有任何鎖時呼叫。</b>
    /// </summary>
    /// <remarks>
    /// 🔴 <b>刻意不擲例外</b>：它排在 <c>_questController.Update()</c> 之前，而
    /// <c>DalamudInitializer.FrameworkUpdate</c> 裡任何一支擲例外都會讓它之後的程式碼當幀不執行。
    /// </remarks>
    public void RefreshNavmeshSnapshot()
    {
        try
        {
            _pathRunningSnapshot = IsPathRunning;
        }
        catch(Exception e)
        {
            _pathRunningSnapshot = false;
            logger.LogDebug(e, "Could not read navmesh path state, assuming no path is running");
        }

        _pathfindingSnapshot = IsPathfinding;
    }
    public DestinationData? Destination { get; set; }
    public DateTime MovementStartedAt { get; private set; } = DateTime.Now;
    public int BuiltNavmeshPercent => navmeshIpc.GetBuildProgress();

    public void Dispose()
    {
        Stop();
    }

    public void Update()
    {
        if (UpdateLandAndRetry())
        {
            return;
        }

        if (_pathfindTask != null && Destination != null)
        {
            if (_pathfindTask.IsCompletedSuccessfully)
            {
                logger.LogInformation("Pathfinding complete, got {Count} points", _pathfindTask.Result.Count);
                if (_pathfindTask.Result.Count == 0)
                {
                    if (!Destination.IsFlying && condition[ConditionFlag.InFlight])
                    {
                        chatGui.Print(
                            "vnavmesh was not able to find a path. Attempting to land, then trying again.".Loc(),
                            CommandHandler.MessageTag, CommandHandler.TagColor);
                        _previousDestinationData = Destination;
                        _landAndRetryTimeout = DateTime.Now.AddSeconds(LandAndRetryTimeoutSeconds);
                        _landAttempts = 0;

                        // 刻意不呼叫 Stop()：Destination 與 MovementStartedAt(＝MaxValue) 要留著，
                        // MoveExecutor 靠那兩個值才會在等降落這段期間繼續回 StillRunning。
                        ResetPathfinding();
                        SendLandingAttempt();
                        return;
                    }

                    //_commandManager.ProcessCommand("/vnav rebuild");
                    Stop();
                    throw new PathfindingFailedException("Pathfinding complete, got 0 points");
                }

                List<Vector3> navPoints = _pathfindTask.Result.Skip(1).ToList();
                Vector3 start = objectTable[0]?.Position ?? navPoints[0];
                if (Destination.IsFlying && !condition[ConditionFlag.InFlight] && condition[ConditionFlag.Mounted])
                {
                    if (IsOnFlightPath(start) || navPoints.Any(IsOnFlightPath))
                    {
                        unsafe
                        {
                            ActionManager.Instance()->UseAction(ActionType.GeneralAction, 2);
                        }
                    }
                }

                if (!Destination.IsFlying)
                {
                    (navPoints, bool recalculateNavmesh) = movementOverrideController.AdjustPath(navPoints);
                    if (recalculateNavmesh && Destination.ShouldRecalculateNavmesh())
                    {
                        Destination.NavmeshCalculations++;
                        Destination.PartialRoute.AddRange(navPoints);
                        logger.LogInformation("Running navmesh recalculation with fudged point ({From} to {To})",
                            navPoints.Last(), Destination.Position);

                        _cancellationTokenSource = new();
                        _cancellationTokenSource.CancelAfter(TimeSpan.FromSeconds(30));
                        _pathfindTask =
                            navmeshIpc.Pathfind(navPoints.Last(), Destination.Position, Destination.IsFlying,
                                _cancellationTokenSource.Token);
                        return;
                    }
                }

                navPoints = Destination.PartialRoute.Concat(navPoints).ToList();
                logger.LogInformation("Navigating via route: [{Route}]",
                    string.Join(" → ",
                        _pathfindTask.Result.Select(x => x.ToString("G", CultureInfo.InvariantCulture))));

                navmeshIpc.MoveTo(navPoints, Destination.IsFlying);
                _pathRunningSnapshot = true;
                MovementStartedAt = DateTime.Now;

                ResetPathfinding();
            }
            else if (_pathfindTask.IsCompleted)
            {
                const string error = "Unable to complete pathfinding task";
                logger.LogWarning(error);
                //_commandManager.ProcessCommand("/vnav rebuild");
                Stop();
                throw new PathfindingFailedException(error);
            }
        }

        if (serviceProvider.GetRequiredService<QuestController>().IsQuestingStopped)
            return;

        if (IsPathRunning && Destination != null)
        {
            if (gameFunctions.IsLoadingScreenVisible())
            {
                logger.LogInformation("Stopping movement, loading screen visible");
                Stop();
                return;
            }

            if (Destination is { IsFlying: true } && condition[ConditionFlag.Swimming])
            {
                logger.LogInformation("Flying but swimming, restarting as non-flying path...");
                Restart(Destination);
                return;
            }
            else if (Destination is { IsFlying: true } && !condition[ConditionFlag.Mounted])
            {
                logger.LogInformation("Flying but not mounted, restarting as non-flying path...");
                Restart(Destination);
                return;
            }

            Vector3 localPlayerPosition = objectTable[0]?.Position ?? Vector3.Zero;
            if (Destination.MovementType == EMovementType.Landing)
            {
                if (!condition[ConditionFlag.InFlight])
                {
                    Stop();
                }
            }
            else if ((localPlayerPosition - Destination.Position).Length() < Destination.StopDistance)
            {
                if (localPlayerPosition.Y - Destination.Position.Y <= Destination.VerticalStopDistance)
                {
                    Stop();
                }
                else if (Destination.DataId != null)
                {
                    IGameObject? gameObject = gameFunctions.FindObjectByDataId(Destination.DataId.Value);
                    if (gameObject is ICharacter or IEventObj)
                    {
                        if (Math.Abs(localPlayerPosition.Y - gameObject.Position.Y) <
                            DefaultVerticalInteractionDistance)
                        {
                            Stop();
                        }
                        else
                        {
                            GiveUpOnVerticalAlignmentAfterGrace();
                        }
                    }
                    else if (gameObject is { ObjectKind: ObjectKind.Aetheryte })
                    {
                        if (AetheryteConverter.IsLargeAetheryte((EAetheryteLocation)Destination.DataId))
                        {
                            /*
                            if ((EAetheryteLocation) Destination.DataId is EAetheryteLocation.OldSharlayan
                                or EAetheryteLocation.UltimaThuleAbodeOfTheEa)
                                Stop();

                            // TODO verify the first part of this, is there any aetheryte like that?
                            // TODO Unsure if this is per-aetheryte or what; because e.g. old sharlayan is at -1.53;
                            //      but Elpis aetherytes fail at around -0.95
                            if (localPlayerPosition.Y - gameObject.Position.Y < 2.95f &&
                                localPlayerPosition.Y - gameObject.Position.Y > -0.9f)
                                Stop();
                            */
                            Stop();
                        }
                        else
                        {
                            // aethernet shard
                            if (Math.Abs(localPlayerPosition.Y - gameObject.Position.Y) <
                                DefaultVerticalInteractionDistance)
                            {
                                Stop();
                            }
                            else
                            {
                                GiveUpOnVerticalAlignmentAfterGrace();
                            }
                        }
                    }
                    else
                    {
                        Stop();
                    }
                }
                else
                {
                    Stop();
                }
            }
            else
            {
                List<Vector3> navPoints = navmeshIpc.GetWaypoints();
                Vector3? start = objectTable[0]?.Position;
                if (start != null)
                {
                    if (StopIfBlockedWithinInteractionRange(start.Value))
                    {
                        return;
                    }

                    if (RecalculateNavmesh(navPoints, start.Value))
                    {
                        return;
                    }

                    if (!Destination.IsFlying && !condition[ConditionFlag.Mounted] &&
                        !gameFunctions.HasStatusPreventingSprint() && Destination.CanSprint)
                    {
                        TriggerSprintIfNeeded(navPoints, start.Value);
                    }
                }
            }
        }
    }

    /// <summary>等「先降落再重試」的那段期間，每幀做的事。</summary>
    /// <returns>有沒有正在等降落；<see langword="true"/> 時 <see cref="Update"/> 必須立刻 return。</returns>
    private bool UpdateLandAndRetry()
    {
        if (_landAndRetryTimeout == null || _previousDestinationData == null)
        {
            return false;
        }

        if (condition[ConditionFlag.InFlight])
        {
            if (DateTime.Now <= _landAndRetryTimeout)
            {
                if (_landAttempts < LandMaxAttempts && DateTime.Now >= _landNextAttemptAt)
                {
                    SendLandingAttempt();
                }

                return true;
            }

            // Stop() 會一併清掉待重試狀態；不清的話下一幀又會走到這裡，變成每幀擲一次。
            logger.LogWarning(
                "Land-and-retry timed out after {Seconds}s, still InFlight "
                + "(sent {Attempts} landing attempt(s), max {Max})",
                LandAndRetryTimeoutSeconds, _landAttempts, LandMaxAttempts);
            Stop();
            throw new PathfindingFailedException("Land-and-retry timed out, still InFlight");
        }

        DestinationData retry = _previousDestinationData;
        _previousDestinationData = null;
        _landAndRetryTimeout = null;
        logger.LogInformation("Landed, retrying pathfinding to {Destination}", retry);
        NavigateTo(retry.MovementType, retry.DataId, retry.Position, retry.IsFlying, retry.CanSprint,
            retry.StopDistance, retry.VerticalStopDistance, retry.Land);
        return true;
    }

    /// <summary>送出一次降落，並排下一次可以重送的時間。只在 framework 執行緒上呼叫。</summary>
    private void SendLandingAttempt()
    {
        _landAttempts++;
        _landNextAttemptAt = DateTime.Now.AddSeconds(LandRetryIntervalSeconds);
        if (!LandExecutor.TryLanding())
        {
            logger.LogInformation("Landing request was not accepted (attempt {Attempt}/{Max})",
                _landAttempts, LandMaxAttempts);
        }
    }

    private void Restart(DestinationData destination)
    {
        Stop();

        if (destination.UseNavmesh)
        {
            NavigateTo(EMovementType.None, destination.DataId, destination.Position, false, false,
                destination.StopDistance, destination.VerticalStopDistance);
        }
        else
        {
            NavigateTo(EMovementType.None, destination.DataId, [destination.Position], false, false,
                destination.StopDistance, destination.VerticalStopDistance);
        }
    }

    private bool IsOnFlightPath(Vector3 p)
    {
        Vector3? pointOnFloor = navmeshIpc.GetPointOnFloor(p, true);
        return pointOnFloor != null && Math.Abs(pointOnFloor.Value.Y - p.Y) > 0.5f;
    }

    [MemberNotNull(nameof(Destination))]
    private void PrepareNavigation(EMovementType type, uint? dataId, Vector3 to, bool fly, bool sprint,
        float? stopDistance, float verticalStopDistance, bool land, bool useNavmesh)
    {
        ResetPathfinding();

        // 新的導航請求一律當作重新起算：「垂直卡住 → 人又走遠了（回到一般導航分支）→ 之後再靠近」
        // 這條路走不到 Stop()，殘留的起算時間會讓下一次的逾時判斷提早觸發。
        _verticalStopCheckStuckSince = null;
        ResetInteractionRangeStuckState();
        ResetLandAndRetry();

        // 送不出聊天指令時跳過：載入時已記過 Warning，這裡擲例外會讓導航起不來。
        if (InputManager.IsAutoRunning() && chatFunctions.IsAvailable)
        {
            logger.LogInformation("Turning off auto-move");
            chatFunctions.ExecuteCommand("/automove off");
        }

        Destination = new(type, dataId, to, stopDistance ?? (QuestStep.DefaultStopDistance - 0.2f), fly,
            sprint, verticalStopDistance, land, useNavmesh);
        MovementStartedAt = DateTime.MaxValue;
    }

    public void NavigateTo(EMovementType type, uint? dataId, Vector3 to, bool fly, bool sprint,
        float? stopDistance = null, float? verticalStopDistance = null, bool land = false)
    {
        fly |= condition[ConditionFlag.Diving];
        if (fly && land)
        {
            to = to with { Y = to.Y + 2.6f };
        }

        PrepareNavigation(type, dataId, to, fly, sprint, stopDistance, verticalStopDistance ?? DefaultVerticalInteractionDistance, land, true);
        logger.LogInformation("Pathfinding to {Destination}", Destination);

        Destination.NavmeshCalculations++;
        _cancellationTokenSource = new();
        _cancellationTokenSource.CancelAfter(TimeSpan.FromSeconds(30));

        Vector3? playerPosition = objectTable[0]?.Position;
        if (playerPosition == null)
        {
            // 只清掉 Destination 是不夠的：PrepareNavigation 把 MovementStartedAt 設成 MaxValue，
            // MoveExecutor 會因此永遠回 StillRunning —— 失敗必須被交出去才不會靜默卡死。
            logger.LogWarning("Cannot pathfind, local player object is not available");
            Stop();
            throw new PathfindingFailedException("Cannot pathfind, local player object is not available");
        }

        Vector3 startPosition = playerPosition.Value;
        if (fly && aetheryteData.CalculateDistance(startPosition, clientState.TerritoryType,
            EAetheryteLocation.CoerthasCentralHighlandsCampDragonhead) < 11f)
        {
            startPosition = startPosition with { Y = startPosition.Y + 1f };
            logger.LogInformation("Using modified start position for flying pathfinding: {StartPosition}",
                startPosition.ToString("G", CultureInfo.InvariantCulture));
        }
        else if (fly)
        {
            // other positions have a (lesser) chance of starting from underground too, in which case pathfinding takes
            // >10 seconds and gets stuck trying to go through the ground.
            // only for flying; as walking uses a different algorithm
            startPosition = startPosition with { Y = startPosition.Y + 0.2f };
        }

        _pathfindTask =
            navmeshIpc.Pathfind(startPosition, to, fly, _cancellationTokenSource.Token);
        _pathfindingSnapshot = true;
        //      float range = stopDistance ?? 2.8f;
        //      if (!_navmeshIpc.SimplePathfindAndMoveCloseTo(to, fly, range))
        //      {
        //          _logger.LogWarning("SimpleMove rejected pathfind request (already in progress), stopping first");
        //          _navmeshIpc.Stop();
        //          if (!_navmeshIpc.SimplePathfindAndMoveCloseTo(to, fly, range))
        //          {
        //              _logger.LogWarning("SimpleMove still rejected after stop");
        //          }
        //      }
        //      MovementStartedAt = DateTime.Now;
    }

    public void NavigateTo(EMovementType type, uint? dataId, List<Vector3> to, bool fly, bool sprint,
        float? stopDistance, float? verticalStopDistance = null, bool land = false)
    {
        fly |= condition[ConditionFlag.Diving];
        if (fly && land && to.Count > 0)
        {
            to[^1] = to[^1] with { Y = to[^1].Y + 2.6f };
        }

        PrepareNavigation(type, dataId, to.Last(), fly, sprint, stopDistance, verticalStopDistance ?? DefaultVerticalInteractionDistance, land, false);

        logger.LogInformation("Moving to {Destination}", Destination);
        navmeshIpc.MoveTo(to, fly);
        _pathRunningSnapshot = true;
        MovementStartedAt = DateTime.Now;
    }

    public void ResetPathfinding()
    {
        if (_cancellationTokenSource != null)
        {
            try
            {
                _cancellationTokenSource.Cancel();
            }
            catch(ObjectDisposedException)
            {
            }

            _cancellationTokenSource.Dispose();
        }

        _pathfindTask = null;
        _pathfindingSnapshot = false;
    }

    /// <summary>水平距離還沒進到 <see cref="DestinationData.StopDistance"/>、但人已經站在目標物件的互動範圍內
    /// 而且完全推不動時，停止移動，讓後面的互動步驟直接從原地試。</summary>
    /// <returns>有沒有停下來；<see langword="true"/> 時呼叫端必須立刻 return。</returns>
    /// <remarks>🔴 只在目的地帶 <c>DataId</c> 且該物件是可互動的角色／事件物件時才動作 ——
    /// 純座標移動（<c>DataId</c> 為 <see langword="null"/>）的語意完全不變。
    /// <para>🔴 停滯判準是「玩家自己有沒有位移」而不是「離目標有沒有更近」：繞過障礙物時會先變遠再變近，
    /// 拿距離當判準會把正常的繞路誤判成卡住；撞在障礙物上的位移則是零。</para></remarks>
    private bool StopIfBlockedWithinInteractionRange(Vector3 playerPosition)
    {
        DestinationData? destination = Destination;
        float fallbackRange = configuration.General.InteractionRangeStopDistance;
        if (destination is not { DataId: { } dataId, IsFlying: false } || fallbackRange <= 0f)
        {
            ResetInteractionRangeStuckState();
            return false;
        }

        IGameObject? gameObject = gameFunctions.FindObjectByDataId(dataId);
        if (gameObject is not (ICharacter or IEventObj) || !gameObject.IsTargetable)
        {
            ResetInteractionRangeStuckState();
            return false;
        }

        // 便宜的距離預檢擋在原生呼叫前面：否則走向遠處 NPC 的整段路上每幀都會打一次。
        float distance = (playerPosition - gameObject.Position).Length();
        if (distance > fallbackRange)
        {
            ResetInteractionRangeStuckState();
            return false;
        }

        bool? gameSaysInRange = gameFunctions.IsWithinGameInteractRange(gameObject);
        bool inRange = gameSaysInRange ??
                       Math.Abs(playerPosition.Y - gameObject.Position.Y) < DefaultVerticalInteractionDistance;
        if (!inRange)
        {
            ResetInteractionRangeStuckState();
            return false;
        }

        // 位移超過這個量就當作移動還在推進，重新起算；與 RecalculateNavmesh 的停滯判準同一個量級。
        if (_interactionRangeStuckAt is not { } stuckAt || (playerPosition - stuckAt).Length() > 0.5f)
        {
            _interactionRangeStuckAt = playerPosition;
            _interactionRangeStuckSince = DateTime.Now;
            return false;
        }

        if (DateTime.Now - _interactionRangeStuckSince <
            TimeSpan.FromSeconds(configuration.General.MovementStuckGraceSeconds))
        {
            return false;
        }

        logger.LogInformation(
            "Already within interaction range of {DataId} ({Distance:F1}y, stop distance {StopDistance:F1}y, " +
            "range check: {RangeCheck}) but movement is blocked; stopping so the interaction can be " +
            "attempted from here",
            dataId, distance, destination.StopDistance, gameSaysInRange != null ? "game" : "fallback distance");
        Stop();
        return true;
    }

    /// <summary>水平已經到了、垂直檢查卻一直不過時，等過寬限期就放棄對齊、直接停止移動。</summary>
    /// <remarks>不放棄的話 <c>Destination</c> 永遠不清，人每隔幾秒被迫動一下，恰好把目標物件的互動讀條打斷。
    /// <para>🔴 角色／事件物件與乙太之光晶塊兩個分支<b>共用這一支</b>：同一顆計時器
    /// <see cref="_verticalStopCheckStuckSince"/>、同一個 <c>MovementStuckGraceSeconds</c>、同一行警告。
    /// 「沒真的對齊到」交回任務自己的完成判定／重試邏輯處理。</para></remarks>
    private void GiveUpOnVerticalAlignmentAfterGrace()
    {
        _verticalStopCheckStuckSince ??= DateTime.Now;
        if (DateTime.Now - _verticalStopCheckStuckSince <
            TimeSpan.FromSeconds(configuration.General.MovementStuckGraceSeconds))
        {
            return;
        }

        logger.LogWarning(
            "Within horizontal stop distance of {DataId} but vertical check never passed; giving up and stopping movement",
            Destination?.DataId);
        Stop();
    }

    private void ResetInteractionRangeStuckState()
    {
        _interactionRangeStuckSince = null;
        _interactionRangeStuckAt = null;
    }

    private void ResetLandAndRetry()
    {
        _landAndRetryTimeout = null;
        _previousDestinationData = null;
        _landAttempts = 0;
        _landNextAttemptAt = default;
    }

    private bool RecalculateNavmesh(List<Vector3> navPoints, Vector3 start)
    {
        if (Destination == null)
        {
            throw new InvalidOperationException("Destination is null");
        }

        if (DateTime.Now - MovementStartedAt <= TimeSpan.FromSeconds(configuration.General.MovementStuckGraceSeconds))
        {
            return false;
        }

        Vector3 nextWaypoint = navPoints.FirstOrDefault();
        if (nextWaypoint == default)
        {
            return false;
        }

        float distance = Vector2.Distance(new(start.X, start.Z),
            new(nextWaypoint.X, nextWaypoint.Z));
        // 忙碌時（讀條、跳躍中、過場）人本來就不會前進，當成卡住會打斷互動讀條。
        if (Destination.LastWaypoint == null ||
            (Destination.LastWaypoint.Position - nextWaypoint).Length() > 0.1f ||
            gameFunctions.IsOccupied())
        {
            Destination.LastWaypoint = new(nextWaypoint)
            {
                Distance2DAtLastUpdate = distance,
                UpdatedAt = Environment.TickCount64
            };
            return false;
        }
        else if (Environment.TickCount64 - Destination.LastWaypoint.UpdatedAt > 500)
        {
            // check whether we've made any progress of any kind
            if (Math.Abs(distance - Destination.LastWaypoint.Distance2DAtLastUpdate) < 0.5f && !condition[ConditionFlag.WatchingCutscene])
            {
                int calculations = Destination.NavmeshCalculations;

                // 額度用完時把失敗交出去：只是不再重算的話 vnavmesh 的路徑還掛著
                // ⇒ MoveExecutor 永遠回 StillRunning、全程零訊息。
                if (!Destination.ShouldRecalculateNavmesh())
                {
                    logger.LogWarning(
                        "Giving up on navmesh recalculation after {Calculations} attempts, movement is stuck",
                        calculations);
                    Stop();
                    throw new PathfindingFailedException(
                        $"Movement stuck, gave up after {calculations} navmesh recalculations");
                }

                if (calculations % 6 == 1)
                {
                    logger.LogWarning("Jumping to try and resolve navmesh problem (n = {Calculations})",
                        calculations);
                    unsafe
                    {
                        ActionManager.Instance()->UseAction(ActionType.GeneralAction, 2);
                        Destination.NavmeshCalculations++;
                        Destination.LastWaypoint.UpdatedAt = Environment.TickCount64;
                    }
                }
                else
                {
                    logger.LogWarning("Recalculating navmesh (n = {Calculations})", calculations);
                    Restart(Destination);
                }

                Destination.NavmeshCalculations = calculations + 1;
                return true;
            }
            else
            {
                Destination.LastWaypoint.Distance2DAtLastUpdate = distance;
                Destination.LastWaypoint.UpdatedAt = Environment.TickCount64;
                return false;
            }
        }
        else
        {
            return false;
        }
    }

    private void TriggerSprintIfNeeded(IEnumerable<Vector3> navPoints, Vector3 start)
    {
        float actualDistance = 0;
        foreach(Vector3 end in navPoints)
        {
            actualDistance += (start - end).Length();
            start = end;
        }

        unsafe
        {
            // 70 is ~10 seconds of sprint
            float sprintDistance = 100f;

            // if we're in towns/event areas, jog is a neat fallback (if we're not already jogging,
            // if we're too close then sprinting will barely benefit us)
            if (!gameFunctions.HasStatus(EStatus.Jog) &&
                territoryData.GetIntendedUse(clientState.TerritoryType) is 0 or 7 or 13 or 14 or 15 or 19 or 23 or 29)
            {
                sprintDistance = 30f;
            }

            if (actualDistance > sprintDistance &&
                ActionManager.Instance()->GetActionStatus(ActionType.GeneralAction, 4) == 0)
            {
                logger.LogInformation("Triggering Sprint");
                ActionManager.Instance()->UseAction(ActionType.GeneralAction, 4);
            }
        }
    }

    /// <summary>停止移動。</summary>
    /// <param name="defer">
    /// 不是 <see langword="null"/> 時，<b>純副作用的那兩段</b>——對 vnavmesh 打 <c>Path.Stop</c>、
    /// 以及關掉自動前進——交給它安排；狀態重設（<see cref="ResetPathfinding"/>、
    /// <see cref="Destination"/>、快照）仍然當場同步做。
    /// </param>
    /// <remarks>
    /// 🔴 <b>為什麼要拆</b>：<c>QuestController.ExecuteNextStep</c> 是持著 <c>_progressLock</c>
    /// 呼叫這一支的，而 <c>navmeshIpc.Stop()</c> 是跨外掛 IPC（CallGate＝直接方法呼叫）
    /// ⇒ 在鎖裡打過去等於把 vnavmesh 的鎖排在我們的鎖後面，對方日後長出任何一條回頭呼叫
    /// Questionable 的路徑就是死鎖。
    /// <para>
    /// 🔴 <b>為什麼不能整支延後</b>：<see cref="ResetPathfinding"/> 與 <c>Destination = null</c>
    /// 會讓「還在不在尋路／移動」翻成 <see langword="false"/>，而<b>同一個鎖裡</b>後面就在判斷
    /// 這兩件事。整支延後的話那兩個判斷會翻面、提早 <c>return</c>——那是縮小引擎的原子性，
    /// 不是等價改寫。
    /// </para>
    /// <para>
    /// ⚠️ <c>InputManager.IsAutoRunning()</c> 讀的是遊戲原生狀態、而且決定要不要做事
    /// ⇒ 那個判斷留在原地，延後的只有「寫一行記錄＋送一個指令」。
    /// </para>
    /// <para>
    /// 📌 <paramref name="defer"/> 不給的時候就地依序執行。
    /// </para>
    /// </remarks>
    public void Stop(Action<Action>? defer = null)
    {
        if (defer != null)
        {
            defer(navmeshIpc.Stop);
        }
        else
        {
            navmeshIpc.Stop();
        }

        ResetPathfinding();
        Destination = null;

        // 我們已經決定停下來了 ⇒ 快照當場歸零，不必等下一幀重新取樣。
        _pathRunningSnapshot = false;

        // 跟上面同一批狀態一起清：不清的話，下一次完全不相關的移動請求如果剛好又卡在
        // 同一個「水平到了、垂直沒過」的分支，會沿用這次殘留的起算時間，逾時判斷提早觸發。
        _verticalStopCheckStuckSince = null;
        ResetInteractionRangeStuckState();
        ResetLandAndRetry();

        if (InputManager.IsAutoRunning())
        {
            if (defer != null)
            {
                defer(TurnOffAutoMove);
            }
            else
            {
                TurnOffAutoMove();
            }
        }
    }

    private void TurnOffAutoMove()
    {
        // Stop() 會從每幀的 Update 走到這裡：送不出聊天指令時跳過，不擲。
        if (!chatFunctions.IsAvailable)
        {
            return;
        }

        logger.LogInformation("Turning off auto-move [stop]");
        chatFunctions.ExecuteCommand("/automove off");
    }

    public sealed record DestinationData
    (
        EMovementType MovementType,
        uint? DataId,
        Vector3 Position,
        float StopDistance,
        bool IsFlying,
        bool CanSprint,
        float VerticalStopDistance,
        bool Land,
        bool UseNavmesh)
    {
        public int NavmeshCalculations { get; set; }
        public List<Vector3> PartialRoute { get; } = [];
        public LastWaypointData? LastWaypoint { get; set; }

        public bool ShouldRecalculateNavmesh()
        {
            return NavmeshCalculations < 10;
        }
    }

    public sealed record LastWaypointData(Vector3 Position)
    {
        public long UpdatedAt { get; set; }
        public double Distance2DAtLastUpdate { get; set; }
    }

    public sealed class PathfindingFailedException : Exception
    {
        public PathfindingFailedException()
        {
        }

        public PathfindingFailedException(string message)
            : base(message)
        {
        }

        public PathfindingFailedException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}

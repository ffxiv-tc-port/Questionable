using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Plugin.Services;
using ECommons.DalamudServices;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace Questionable.Utils;

/// <summary>
///     「同一扇視窗的同一個按法，按過就不要再按，直到它真的收掉」的共用閘門。
///     Questionable 對 addon 的所有按法（<c>FireCallbackInt</c>、<c>FireCallback</c>、
///     <c>Callback.Fire</c>、<c>Close(true)</c>、<c>AddonMaster</c> 的按鈕點擊）都要先問過
///     <see cref="TryBeginPress(string, AtkUnitBase*, string, int)" />。
/// </summary>
/// <remarks>
///     <para>
///         🔴🔴 <b>存在的唯一理由是原生 AccessViolation</b>：<c>SelectYesno</c> 這類「按下即關」的窗被按下之後
///         有<b>「正在關閉中」的幾幀</b>，這段期間 <c>GetAddonByName</c> 仍然回得到實例、<c>IsVisible</c> 與
///         <c>UldManager.LoadedState == Loaded</c> 也都還成立（<see cref="AddonUtils.IsAddonReady" /> 擋不住這個窗口）。
///         此時再對它送 callback／輸入事件就是 <c>try</c>/<c>catch</c> 攔不到的原生 AccessViolation ——
///         <b>唯一的防護是「不要送第二次」，不是「送了再接住」</b>。
///     </para>
///     <para>
///         🔴 節流<b>不是</b>防護：<c>EzThrottler.Throttle("Confirm", 2000)</c>（<c>CommenceExecutor</c>）記的是
///         「上一次動作在哪個時刻」而不是「這扇窗已經按過」，而且 key 是全外掛共用、<b>首次必放行</b>；
///         <c>PurchaseState.NextStep</c>／<c>_remainingAllowances</c>／<c>PointMenuCounter</c> 記的是
///         「流程走到哪」而不是「這個實例按過沒」。它們全都無法區分「同一扇正在關閉的窗」與「新的一扇窗」。
///     </para>
///     <para>
///         🔑 <b>粒度＝(窗名, 位址, 參數組)</b>，只對多次互動按法（逃生口 ≤ <see cref="RoutineRePressEscapeFrames" />）成立；
///         其餘是<b>終結按法</b>，熱著時同一扇窗的<b>任何</b>按法都擋（換參數去按關閉中的窗一樣是 AVE）。
///         <see cref="SingleAnswerAddons" /> 另外把所有參數併成同一個 key。
///     </para>
///     <para>
///         🔴 <b>時鐘不能用 <c>UiBuilder.FrameCount</c></b>：那個計數器的遞增點在 <c>OnDraw()</c> 的三個
///         「隱藏 UI 就 return」之後（過場動畫、使用者按隱藏 UI 熱鍵、GPose，三個開關預設全開），
///         過場中它<b>完全不前進</b> —— 而按下點走的是 AddonLifecycle（原生 detour）照常每幀被叫到，
///         結果會變成「按下照常、逃生口永不到期」。本類別自己在 <see cref="IFramework.Update" /> 裡數，
///         那條路徑與 UI 隱藏無關。
///     </para>
///     <para>
///         🔴 <b>解除一律按位址，不按名稱</b>：同名的第二扇窗被建立時若整包清掉 addonName 的紀錄，
///         第一扇（正在關閉中）的紀錄會跟著消失 ⇒ 下一幀對它再送一發＝崩潰。
///     </para>
///     <para>
///         被擋下時一律回 <see langword="false" />（不是 <see langword="null" />），對呼叫端的意義是
///         「這一輪沒按到，下一輪再來」，與「addon 還沒出現」走同一條既有路徑，不改變任何控制流。
///     </para>
/// </remarks>
internal static unsafe class AddonPressGuard
{
    /// <summary>「按下即關」的窗用的逃生口：遠大於關閉所需的幀數（關閉中的危險窗口實測 &lt; 10 幀）。</summary>
    internal const int DefaultEscapeFrames = 90;

    /// <summary>
    ///     「按一次翻一頁／挑一格，窗不會因為被按而消失」的多次互動窗用的逃生口。
    ///     15 幀不落在關閉中的危險窗口裡，而每次互動多 0.25 秒幾乎無感。
    ///     走這個逃生口是<b>常態</b>，所以寫 Debug 不洗版。
    /// </summary>
    internal const int RoutineRePressEscapeFrames = 15;

    /// <summary>掃全索引找「這扇窗還在不在」時的上界；掃到第一個空的就停，掃滿仍沒停＝查詢不成立、不解除。</summary>
    private const int MaxAddonIndex = 256;

    /// <summary>常駐窗按下後，要<b>連續</b>觀察到「還在清單裡、但已隱藏」這麼多幀才解除。</summary>
    /// <remarks>不承重：真正的防護是送出前要求常駐窗可見（<see cref="PersistentAddons" />），這個值只決定要等多久。</remarks>
    internal const int HiddenReleaseFrames = 20;

    /// <summary>每隔幾幀回收一次「已經不可能再擋住任何人」的冷紀錄。</summary>
    private const int SweepIntervalFrames = 60;

    /// <summary>冷紀錄回收的年齡下限（幀），遠大於任何逃生口：回收不改變任何一次「按／不按」的判定。</summary>
    private const int SweepMinAgeFrames = 1800;

    /// <summary>
    ///     「回答一次即終結」的窗：任何一個答案按下去之後窗就會收掉，所以<b>所有</b>參數併成同一個 key。
    ///     次級選單若重用同一個實例，第二層會等到逃生口才送（寫 Information、只是變慢）。
    /// </summary>
    private static readonly HashSet<string> SingleAnswerAddons = new(StringComparer.Ordinal)
    {
        "SelectYesno",
        "DifficultySelectYesNo",
        "HousingSelectBlock",
        "PointMenu",
        "SelectString",
        "SelectIconString",
        "ContextIconMenu",
        "ContentsFinderConfirm",
    };

    /// <summary>
    ///     實機證實是「常駐」的窗：關閉只是隱藏，位址一直留在清單裡，不會 PreFinalize 也不會重新 PostSetup。
    /// </summary>
    /// <remarks>🔴 這份名單同時是「送出前必須可見」的名單（與「連續隱藏才解除」互為反面）；把會被銷毀的窗放進來＝拆除途中提早解除。</remarks>
    private static readonly HashSet<string> PersistentAddons = new(StringComparer.Ordinal)
    {
        "ContextMenu",
        "ContextIconMenu",
        "Talk",
    };

    /// <summary>紀錄身分：同名實例各自一筆。位址只做等值比較，永遠不解參。</summary>
    private readonly record struct PressSlot(string AddonName, nint Address);

    /// <param name="BlocksAll">true＝終結按法：熱著時這扇窗上的所有按法都被擋。</param>
    private readonly record struct PressRecord(long Frame, int EscapeFrames, bool BlocksAll);

    /// <summary>一扇窗（窗名＋位址）底下的全部按法與解除狀態。刻意是 class：欄位要就地改。</summary>
    private sealed class SlotRecords
    {
        public Dictionary<string, PressRecord> ByKey { get; } = new(StringComparer.Ordinal);

        public long LastFrame;

        /// <summary>連續觀察到「還在清單裡、但已隱藏」的幀數；只有常駐窗會累加，看得見或重新按下就歸零。</summary>
        public int HiddenFrames;

        /// <summary>登記時在同名清單裡找得到這個位址；只有它成立才准用「從清單消失」解除。</summary>
        public bool Listed;

        /// <summary>看過 PreFinalize：在位址消失或同位址重建之前，任何按法都不准，也沒有逃生口。</summary>
        public bool Finalizing;
    }

    private static readonly Dictionary<PressSlot, SlotRecords> Pressed = new();

    private static readonly Dictionary<string, IAddonLifecycle.AddonEventDelegate> SetupWatchers =
        new(StringComparer.Ordinal);

    private static readonly Dictionary<string, IAddonLifecycle.AddonEventDelegate> FinalizeWatchers =
        new(StringComparer.Ordinal);

    // 每幀輪詢用的可重用緩衝，沒有窗被記著時整個輪詢是一個整數比較就回來，不配置任何東西。
    private static readonly List<PressSlot> ReleaseBuf = [];
    private static readonly List<string> ForgetBuf = [];

    /// <summary>「這個窗名第一次走隱藏解除」只寫一行 Information。</summary>
    private static readonly HashSet<string> HiddenReleaseReported = new(StringComparer.Ordinal);

    private static long frameCount;

    /// <summary>0＝還沒訂閱。🔴 用 <see cref="Interlocked" /> 不用 <see cref="bool" />：重複訂閱不是沒效果，
    /// 是計數器一個 tick 前進 2 ＝所有逃生口對半砍，會把補按往危險窗口推。</summary>
    private static int clockStarted;

    private static long CurrentFrame => frameCount;

    /// <summary>
    ///     啟動時鐘與每幀輪詢。要在外掛建構子裡<b>盡早</b>呼叫（<c>ECommonsMain.Init</c> 之後）：
    ///     同一個外掛內部的 <see cref="IFramework.Update" /> 多播委派<b>包在單一 try/catch 裡</b>，
    ///     排在前面的處理常式擲例外會讓後面所有處理常式那個 tick 完全不被呼叫 ——
    ///     時鐘排越前面越不容易被別人的例外弄停。
    /// </summary>
    internal static void EnsureClock()
    {
        if (Svc.Framework == null)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref clockStarted, 1, 0) != 0)
        {
            return;
        }

        Svc.Framework.Update += OnFrameworkUpdate;
    }

    /// <summary>拆掉監聽器。要在 <c>ECommonsMain.Dispose()</c> <b>之前</b>呼叫（之後 <c>Svc</c> 已經清空）。</summary>
    internal static void ForceTeardown()
    {
        if (Interlocked.Exchange(ref clockStarted, 0) == 1 && Svc.Framework != null)
        {
            Svc.Framework.Update -= OnFrameworkUpdate;
        }

        if (Svc.AddonLifecycle != null)
        {
            foreach ((string addonName, IAddonLifecycle.AddonEventDelegate handler) in SetupWatchers)
            {
                Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, addonName, handler);
            }

            foreach ((string addonName, IAddonLifecycle.AddonEventDelegate handler) in FinalizeWatchers)
            {
                Svc.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, addonName, handler);
            }
        }

        SetupWatchers.Clear();
        FinalizeWatchers.Clear();
        Pressed.Clear();
        HiddenReleaseReported.Clear();
    }

    /// <summary>
    ///     問「現在可以對這扇窗按下這一個按法嗎」，可以的話<b>順便把它登記起來</b>。
    ///     回 <see langword="false" /> 代表這一幀不要碰它。
    /// </summary>
    /// <param name="addonName">視窗名稱。要能被 <c>GetAddonByName</c> 找得到，解除才會生效。</param>
    /// <param name="addon">視窗實例。位址<b>只做等值比較，永遠不解參</b>。</param>
    /// <param name="pressKey">參數組。同一扇窗上不同的按法要用不同的 key。</param>
    /// <param name="escapeFrames">
    ///     逃生口：超過這麼多幀窗還在，就判定「上一次按下沒生效」而不是「正在關閉」，放行補按。
    ///     多次互動窗傳 <see cref="RoutineRePressEscapeFrames" />。
    /// </param>
    internal static bool TryBeginPress(string addonName, AtkUnitBase* addon, string pressKey = "",
        int escapeFrames = DefaultEscapeFrames)
    {
        return TryBeginPress(addonName, (nint)addon, pressKey, escapeFrames);
    }

    /// <summary>
    ///     視窗名稱從實例自己讀（給只拿得到 addon id、拿不到名稱的呼叫端用，例如
    ///     <c>AgentSatisfactionSupply</c> / <c>AgentRecipeNote</c> 那兩條路徑）。
    /// </summary>
    /// <remarks>
    ///     🔴🔴 名稱一定要走 <see cref="AddonUtils.ReadAddonName" /> 這種<b>有界</b>讀法，
    ///     <b>不可以</b>用 CS 產生的 <c>NameString</c>（無上限的 null-terminated 掃描）：
    ///     這支守衛被呼叫的時機正好是「這扇窗可能正在關閉」，
    ///     在判定安全<b>之前</b>先對它做無界讀取，等於守衛自己去踩它要防的那顆雷。
    ///     除了偏移 0x8 那 32 個 byte 的固定欄位之外，位址一樣不解任何二級指標。
    /// </remarks>
    internal static bool TryBeginPress(AtkUnitBase* addon, string pressKey = "",
        int escapeFrames = DefaultEscapeFrames)
    {
        if (addon == null)
        {
            return false;
        }

        return TryBeginPress(AddonUtils.ReadAddonName(addon), (nint)addon, pressKey, escapeFrames);
    }

    /// <summary>
    ///     先問過守衛再送 <c>FireCallbackInt</c>。被擋下時這一幀什麼都不做，回 <see langword="false" />。
    ///     參數值本身就是 press key（＝(窗名, 位址, 參數組) 粒度）。
    /// </summary>
    internal static bool PressCallbackInt(string addonName, AtkUnitBase* addon, int value,
        int escapeFrames = DefaultEscapeFrames)
    {
        if (!TryBeginPress(addonName, addon, value.ToString(CultureInfo.InvariantCulture), escapeFrames))
        {
            return false;
        }

        addon->FireCallbackInt(value);
        return true;
    }

    /// <summary>
    ///     先問過守衛再呼叫 <c>Close(true)</c>。關閉也是一種「對正在關閉中的窗動手」，
    ///     所以與 callback 走同一個閘門。
    /// </summary>
    internal static bool PressClose(string addonName, AtkUnitBase* addon,
        int escapeFrames = DefaultEscapeFrames)
    {
        if (!TryBeginPress(addonName, addon, "Close", escapeFrames))
        {
            return false;
        }

        addon->Close(true);
        return true;
    }

    internal static bool TryBeginPress(string addonName, nint address, string pressKey = "",
        int escapeFrames = DefaultEscapeFrames)
    {
        // 沒有窗就沒有按法 —— 呼叫端本來就會判空，這裡只是不讓 0 進到紀錄裡。
        if (address == 0)
        {
            return false;
        }

        // 讀不到窗名＝判斷不了它是不是正在拆除 ⇒ 不放行（不確定一律 fail-closed）。
        if (string.IsNullOrEmpty(addonName))
        {
            if (EzThrottler.Throttle("Questionable-AddonPressGuard-NoName", 1000))
            {
                Svc.Log.Information(
                    $"[AddonPressGuard] 實例 0x{address.ToString("X", CultureInfo.InvariantCulture)}（按法「{pressKey}」）讀不到視窗名稱，這一幀不碰它。");
            }

            return false;
        }

        // 🔴 時鐘沒起來就等於 frameCount 永遠是 0、逃生口永不到期、所有按下被擋死。
        // 這裡再保險一次（Interlocked 冪等），讓「建構子那次沒跑到」不會變成靜默鎖死。
        EnsureClock();

        bool singleAnswer = SingleAnswerAddons.Contains(addonName);
        if (singleAnswer)
        {
            pressKey = string.Empty;
        }

        // 🔴 常駐窗送出前必須可見：與「連續隱藏才解除」互為反面，兩者一組才是防護。
        if (PersistentAddons.Contains(addonName) && !PersistentAddonVisible(addonName, address))
        {
            LogPersistentHidden(addonName, address);
            return false;
        }

        EnsureWatching(addonName);

        long frame = CurrentFrame;
        bool routine = escapeFrames <= RoutineRePressEscapeFrames;
        bool terminating = !routine;
        var slot = new PressSlot(addonName, address);

        if (!Pressed.TryGetValue(slot, out SlotRecords? records))
        {
            records = new SlotRecords { Listed = IsListed(slot) };
            Pressed[slot] = records;
        }
        else if (records.Finalizing)
        {
            LogHold(addonName, address, pressKey, "PreFinalize", routine: false);
            return false;
        }
        else if (FindBlocking(records, pressKey, singleAnswer, frame, out string blockingKey))
        {
            // 🔴 這就是崩潰的那一幀。
            LogHold(addonName, address, pressKey, blockingKey, routine);
            return false;
        }
        else
        {
            if (records.ByKey.TryGetValue(pressKey, out PressRecord same))
            {
                LogRelease(addonName, address, pressKey, frame - same.Frame, routine);
            }

            if (!records.Listed)
            {
                records.Listed = IsListed(slot);
            }
        }

        records.ByKey[pressKey] = new PressRecord(frame, escapeFrames, terminating);
        records.LastFrame = frame;
        // 🔴 承重：隱藏解除是「從最後一次按下起算」連續隱藏幾幀。
        records.HiddenFrames = 0;
        LogPressDiag(addonName, address, pressKey);
        return true;
    }

    /// <summary>
    ///     每幀的時鐘＋輪詢解除：①登記時找得到、現在從同名清單消失 ⇒ 解除（含 PreFinalize 鎖）；
    ///     ②常駐窗連續隱藏 <see cref="HiddenReleaseFrames" /> 幀 ⇒ 解除。
    ///     🔴 <see cref="frameCount" /> 的遞增必須在所有 early return <b>之前</b> ——
    ///     放到後面的話，沒有窗被記著時時鐘就停住，逃生口的幀數會被算少，等於沒修。
    /// </summary>
    private static void OnFrameworkUpdate(IFramework framework)
    {
        frameCount++;

        if (Pressed.Count == 0)
        {
            return;
        }

        ReleaseBuf.Clear();
        foreach ((PressSlot slot, SlotRecords records) in Pressed)
        {
            bool persistent = PersistentAddons.Contains(slot.AddonName);
            AddonPresence presence = LookUp(slot.AddonName, slot.Address, persistent);
            if (presence == AddonPresence.Unknown)
            {
                continue;
            }

            bool present = presence != AddonPresence.NotListed;
            if (!present)
            {
                if (records.Listed)
                {
                    ReleaseBuf.Add(slot);
                }

                continue;
            }

            if (!persistent || records.Finalizing)
            {
                continue;
            }

            if (presence != AddonPresence.Hidden)
            {
                records.HiddenFrames = 0;
                continue;
            }

            if (++records.HiddenFrames < HiddenReleaseFrames)
            {
                continue;
            }

            ReleaseBuf.Add(slot);
            if (HiddenReleaseReported.Add(slot.AddonName))
            {
                Svc.Log.Information(
                    $"[AddonPressGuard] 「{slot.AddonName}」是常駐視窗(隱藏而不銷毀)，按下紀錄改由「連續隱藏 {HiddenReleaseFrames} 幀」解除。這一行每個視窗名每次遊戲只寫一次。");
            }
        }

        foreach (PressSlot slot in ReleaseBuf)
        {
            Pressed.Remove(slot);
        }

        ReleaseBuf.Clear();
        if (frameCount % SweepIntervalFrames == 0)
        {
            SweepColdRecords();
        }
    }

    /// <summary>回收「已經不可能再擋住任何人」的紀錄（不改變任何一次「按／不按」的判定）；上鎖的不動。</summary>
    private static void SweepColdRecords()
    {
        if (Pressed.Count == 0)
        {
            return;
        }

        long frame = CurrentFrame;
        ReleaseBuf.Clear();
        foreach ((PressSlot slot, SlotRecords records) in Pressed)
        {
            if (records.Finalizing || frame - records.LastFrame < SweepMinAgeFrames)
            {
                continue;
            }

            ReleaseBuf.Add(slot);
        }

        foreach (PressSlot slot in ReleaseBuf)
        {
            Pressed.Remove(slot);
        }

        ReleaseBuf.Clear();
    }

    private static bool FindBlocking(SlotRecords records, string pressKey, bool singleAnswer, long frame,
        out string blockingKey)
    {
        foreach ((string key, PressRecord rec) in records.ByKey)
        {
            if (frame - rec.Frame >= rec.EscapeFrames)
            {
                // 冷了：交給逃生口放行，免得呼叫端卡到逾時。
                continue;
            }

            if (!rec.BlocksAll && !singleAnswer && !string.Equals(key, pressKey, StringComparison.Ordinal))
            {
                continue;
            }

            blockingKey = key;
            return true;
        }

        blockingKey = string.Empty;
        return false;
    }

    /// <summary>這一幀在同名清單裡對某個位址的觀察。</summary>
    /// <remarks>🔴 <b>零值必須是「不解除」的答案</b>：查詢本身不成立時維持封鎖。</remarks>
    private enum AddonPresence
    {
        /// <summary>查詢不成立（名稱空、位址 0、同名實例超過掃描上限）。<b>這是零值</b>。</summary>
        Unknown = 0,

        /// <summary>掃完同名清單都沒有這個位址＝那扇窗已經收乾淨。</summary>
        NotListed = 1,

        Visible = 2,

        Hidden = 3,
    }

    /// <summary>在 <paramref name="addonName" /> 的<b>所有</b>同名實例裡找 <paramref name="address" />。</summary>
    /// <remarks>
    ///     🔴 位址只用來做等值比較；解參考的是 <c>GetAddonByName</c> 這一幀交回來的指標，
    ///     而且只在 <paramref name="readVisibility" /> 時才讀。不讀可見性時，找到一律回 <see cref="AddonPresence.Visible" />。
    /// </remarks>
    private static AddonPresence LookUp(string addonName, nint address, bool readVisibility)
    {
        if (string.IsNullOrEmpty(addonName) || address == 0)
        {
            return AddonPresence.Unknown;
        }

        for (int i = 1; i <= MaxAddonIndex; i++)
        {
            AtkUnitBase* unit = (AtkUnitBase*)Svc.GameGui.GetAddonByName<AtkUnitBase>(addonName, i);
            if (unit == null)
            {
                return AddonPresence.NotListed;
            }

            if ((nint)unit != address)
            {
                continue;
            }

            if (!readVisibility)
            {
                return AddonPresence.Visible;
            }

            return unit->IsVisible ? AddonPresence.Visible : AddonPresence.Hidden;
        }

        return AddonPresence.Unknown;
    }

    private static bool IsListed(PressSlot slot) =>
        LookUp(slot.AddonName, slot.Address, false) is AddonPresence.Visible or AddonPresence.Hidden;

    /// <summary>找不到就回 <see langword="false" />（＝不准送）：這支只給放行端用，查歪一律表現成「這一輪不送」。</summary>
    private static bool PersistentAddonVisible(string addonName, nint address)
        => LookUp(addonName, address, readVisibility: true) == AddonPresence.Visible;

    /// <summary>
    ///     為某個視窗名稱掛上生命週期監聽器。
    ///     🔴 兩個 handler 都<b>按位址</b>處理：PostSetup 只清「這個位址」的紀錄，PreFinalize 只鎖「這個位址」。
    ///     丟掉 <c>args</c> 改用名稱整包清，會在「同名第二扇窗開起來」時把第一扇（正在關閉中）的紀錄一起清掉。
    /// </summary>
    private static void EnsureWatching(string addonName)
    {
        if (SetupWatchers.ContainsKey(addonName))
        {
            return;
        }

        IAddonLifecycle.AddonEventDelegate onSetup = (_, args) =>
        {
            nint address = args.Addon.Address;
            if (address != 0)
            {
                ForgetAddress(addonName, address);
            }
        };

        IAddonLifecycle.AddonEventDelegate onFinalize = (_, args) =>
        {
            nint address = args.Addon.Address;
            if (address != 0)
            {
                MarkDestroying(addonName, address);
            }
        };

        SetupWatchers[addonName] = onSetup;
        FinalizeWatchers[addonName] = onFinalize;
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, addonName, onSetup);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, addonName, onFinalize);
    }

    /// <summary>
    ///     這個位址上開了一扇新的窗：解鎖，舊紀錄作廢。
    /// </summary>
    /// <remarks>
    ///     🔴 <b>同幀豁免</b>：<b>這一幀才登記</b>的紀錄不清。
    ///     有模組（<c>InteractionUiController</c>／<c>HelpUiController</c>／<c>CreditsController</c>／
    ///     <c>CraftworksSupplyController</c>）是<b>在 PostSetup handler 裡直接按下去</b>的，而 <c>AddonLifecycle</c>
    ///     監聽器<b>彼此之間的呼叫順序不可依賴</b>：沒有這條豁免，剛登記的紀錄會被本監聽器清掉。
    ///     同一遊戲幀內 <c>Framework.Update</c> 一律早於 addon 事件，所以按下與本監聽器讀到的 <see cref="CurrentFrame" /> 是同一個值。
    /// </remarks>
    private static void ForgetAddress(string addonName, nint address)
    {
        var slot = new PressSlot(addonName, address);
        if (!Pressed.TryGetValue(slot, out SlotRecords? records))
        {
            return;
        }

        records.Finalizing = false;
        long frame = CurrentFrame;
        ForgetBuf.Clear();
        foreach ((string key, PressRecord rec) in records.ByKey)
        {
            if (rec.Frame != frame)
            {
                ForgetBuf.Add(key);
            }
        }

        foreach (string key in ForgetBuf)
        {
            records.ByKey.Remove(key);
        }

        ForgetBuf.Clear();
        if (records.ByKey.Count == 0)
        {
            Pressed.Remove(slot);
        }
    }

    /// <summary>
    ///     這扇窗已經進入銷毀流程：鎖到它從清單消失（或同位址開出新窗），期間<b>任何</b>按法都不准、沒有逃生口。
    ///     這條路徑罩的是「從 PreFinalize 事件裡回頭去按同一扇窗」的呼叫端
    ///     （<c>RegularShopBase.ShopPreFinalize</c> → <c>RestoreExternalPluginState</c> 就是一例）。守護過的窗名即使沒按過也鎖。
    /// </summary>
    private static void MarkDestroying(string addonName, nint address)
    {
        var slot = new PressSlot(addonName, address);
        if (Pressed.TryGetValue(slot, out SlotRecords? records))
        {
            records.Finalizing = true;
            return;
        }

        Pressed[slot] = new SlotRecords { Listed = true, Finalizing = true, LastFrame = CurrentFrame };
    }

    /// <summary>
    ///     跨外掛「按窗診斷」：在<b>真的送出按壓</b>的那一刻寫一行 <c>Verbose</c>。
    /// </summary>
    /// <remarks>
    ///     各外掛的 <c>AddonPressGuard</c> 只擋自己按過的位址：外掛 A 按下之後「關閉中」那幾幀，
    ///     外掛 B 的表是空的 ⇒ 照按 ⇒ 攔不到的存取違規。格式<b>逐字</b>與其他外掛統一，才能按 <c>addr</c> 交叉比對。
    ///     不節流：節流會蓋掉「同毫秒重按」這個唯一有意義的樣本。
    ///     🔴 位址只印數值，<b>不解參考</b>。
    /// </remarks>
    private static void LogPressDiag(string addonName, nint address, string pressKey)
    {
        Svc.Log.Verbose($"[按窗診斷] plugin=Questionable addon={addonName} addr=0x{address:X} key={pressKey ?? string.Empty}");
    }

    private static void LogPersistentHidden(string addonName, nint address)
    {
        if (EzThrottler.Throttle($"Questionable-AddonPressGuard-PersistentHidden-{addonName}", 1000))
        {
            Svc.Log.Information(
                $"[AddonPressGuard] 「{addonName}」是常駐窗，實例 0x{address.ToString("X", CultureInfo.InvariantCulture)} 這一幀不可見(或不在同名清單裡)，不送。");
        }
    }

    private static void LogHold(string addonName, nint address, string pressKey, string blockingKey, bool routine)
    {
        if (!EzThrottler.Throttle($"Questionable-AddonPressGuard-Hold-{addonName}", 1000))
        {
            return;
        }

        string msg =
            $"[AddonPressGuard] 「{addonName}」(實例 0x{address.ToString("X", CultureInfo.InvariantCulture)}，按法「{pressKey}」)" +
            $"按過之後(紀錄「{blockingKey}」)還沒觀察到它收掉，這一幀不再碰它 —— " +
            "對關閉中的視窗送 callback 是攔不到的存取違規。";
        if (routine)
        {
            Svc.Log.Debug(msg);
        }
        else
        {
            Svc.Log.Information(msg);
        }
    }

    private static void LogRelease(string addonName, nint address, string pressKey, long waited, bool routine)
    {
        if (routine)
        {
            if (EzThrottler.Throttle($"Questionable-AddonPressGuard-RoutineRelease-{addonName}", 10000))
            {
                Svc.Log.Debug(
                    $"[AddonPressGuard] 「{addonName}」(實例 0x{address.ToString("X", CultureInfo.InvariantCulture)}，按法「{pressKey}」)" +
                    $"按下後 {waited} 幀窗還在(多次互動窗的常態)，放行下一次。");
            }
        }
        else if (EzThrottler.Throttle($"Questionable-AddonPressGuard-Release-{addonName}", 10000))
        {
            Svc.Log.Information(
                $"[AddonPressGuard] 「{addonName}」(實例 0x{address.ToString("X", CultureInfo.InvariantCulture)}，按法「{pressKey}」)" +
                $"按下後 {waited} 幀既沒有被銷毀也沒有重新建立，判定為「上一次按下沒生效」" +
                "而不是「正在關閉」，解除封鎖讓呼叫端重試。");
        }
    }
}

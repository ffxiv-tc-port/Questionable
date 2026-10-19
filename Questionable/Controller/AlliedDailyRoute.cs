using System.Collections.Generic;
using System.Numerics;

namespace Questionable.Controller;

/// <summary>
/// 友好部族日常「路線優化」的純邏輯：不碰遊戲狀態，離線探針用反射呼叫。
/// 狀態沿用 <see cref="QuestController"/> 的 <c>AlliedDaily*</c> 常數；同一部族的項目必須相鄰（批次順序）。
/// 成本單位是米；傳送算固定成本加上以太之光到目標的距離（與 <c>AetheryteShortcut</c> 的判斷相同）。
/// </summary>
internal static class AlliedDailyRoute
{
    internal const int SegmentUnknown = 0;
    internal const int SegmentWork = 1;
    internal const int SegmentTurnIn = 2;

    internal const float TeleportCost = 30f;

    /// <summary>目標在別的區域：一律排在同區域的目標之後（讀圖＋傳送）。</summary>
    internal const float OtherTerritoryCost = 5000f;

    /// <summary>不知道目標位置：排在知道位置的目標之後。</summary>
    internal const float UnknownPositionCost = 10000f;

    /// <summary>沒有步驟位置的傳送（採集步驟）只有在離以太之光這麼近時才會略過，見 <c>AetheryteShortcut</c>。</summary>
    internal const float ForcedTeleportSkipDistance = 100f;

    /// <summary>超過這個數量的「還有目標」任務不窮舉順序，改用最近優先。</summary>
    internal const int MaxPlannedItems = 6;

    /// <summary>總成本差在這個範圍內視為一樣（浮點誤差）。</summary>
    private const float CostTolerance = 1f;

    /// <param name="Segment">接下來要做的那一段：<see cref="SegmentWork"/>＝還有目標要做、<see cref="SegmentTurnIn"/>＝只剩交任務。</param>
    /// <param name="Position">這一段的第一個目標（只剩交任務時＝交任務的位置）；<see langword="null"/>＝不知道。</param>
    /// <param name="AetherytePosition">去第一個目標可以用的以太之光（與目標同區域），沒有＝<see langword="null"/>。</param>
    /// <param name="ForceTeleport">去第一個目標一定先傳送（步驟沒有位置，例如採集），除非已經在以太之光旁邊。</param>
    /// <param name="ExitPosition">做完這個任務的目標之後人會在哪裡（最後一個已知目標）。</param>
    /// <param name="InternalCost">從第一個目標做到最後一個目標的估計移動成本。</param>
    /// <param name="TurnInNpc">交任務對象的 DataId，0＝不知道（不知道就不延後交任務）。</param>
    internal readonly record struct Item(
        int Society,
        int State,
        int Segment,
        uint TerritoryId,
        Vector3? Position,
        Vector3? AetherytePosition,
        bool ForceTeleport,
        Vector3? ExitPosition,
        float InternalCost,
        uint TurnInNpc,
        Vector3? TurnInPosition,
        Vector3? TurnInAetherytePosition);

    /// <summary>從 <paramref name="from"/> 到目標的估計成本；可傳送時取「直接過去」與「傳送」較小者。</summary>
    internal static float LegCost(Vector3 from, uint fromTerritory, uint toTerritory, Vector3? to, Vector3? toAetheryte,
        bool forceTeleport, out bool viaTeleport)
    {
        viaTeleport = false;
        if (to is not { } target)
        {
            return UnknownPositionCost;
        }

        float teleport = toAetheryte is { } aetheryte ? TeleportCost + Vector3.Distance(aetheryte, target) : float.MaxValue;
        if (toTerritory != fromTerritory)
        {
            viaTeleport = toAetheryte != null;
            return OtherTerritoryCost + (viaTeleport ? teleport : 0f);
        }

        float direct = Vector3.Distance(from, target);
        if (forceTeleport && toAetheryte is { } forced)
        {
            viaTeleport = Vector3.Distance(from, forced) >= ForcedTeleportSkipDistance;
            return viaTeleport ? teleport : direct;
        }

        if (teleport < direct)
        {
            viaTeleport = true;
            return teleport;
        }

        return direct;
    }

    internal static float TravelCost(in Item item, uint playerTerritory, Vector3 playerPosition, out bool viaTeleport) =>
        LegCost(playerPosition, playerTerritory, item.TerritoryId, item.Position, item.AetherytePosition,
            item.ForceTeleport, out viaTeleport);

    /// <summary>
    /// 選下一個要做的任務。接任務照舊先接完；正在做的任務做到只剩交任務才換（任務內的步驟順序不動）。
    /// 只剩交任務、而同一位委託人還有別的任務有目標要做時，先去做那些目標，交任務留到最後一起交；
    /// 「還有目標」的任務之間窮舉順序，取「做完全部目標再回去交任務」總成本最小的那個順序的第一個。
    /// </summary>
    /// <param name="startedIndex">目前任務在批次裡的位置；不在批次裡＝-1。</param>
    /// <param name="lastKind">上一次的結果種類；剛接完任務時不黏著目前任務。</param>
    /// <param name="plan">選路線（<see cref="QuestController.AlliedDailyPickRoute"/>）時填入排出來的完整順序，其他情況清空。</param>
    /// <param name="deferredTurnIn">這一次是不是「目前任務只剩交任務、先去做別的」。</param>
    /// <returns>選中的位置；-1＝批次裡沒有能做的。</returns>
    internal static int Pick(IReadOnlyList<Item> items, int startedIndex, int lastKind, uint playerTerritory,
        Vector3 playerPosition, List<int> plan, out int kind, out bool deferredTurnIn)
    {
        plan.Clear();
        kind = 0;
        deferredTurnIn = false;
        if (lastKind == QuestController.AlliedDailyPickAccept)
        {
            startedIndex = -1;
        }

        int activeSociety = -1;
        int firstReady = -1;
        int end = items.Count;
        for (int i = 0; i < items.Count; ++i)
        {
            Item item = items[i];
            if (item.State == QuestController.AlliedDailyDone)
            {
                continue;
            }

            if (activeSociety < 0)
            {
                activeSociety = item.Society;
            }
            else if (item.Society != activeSociety)
            {
                end = i;
                break;
            }

            if (item.State == QuestController.AlliedDailyReady && firstReady < 0)
            {
                firstReady = i;
            }
        }

        if (activeSociety < 0)
        {
            return -1;
        }

        if (firstReady >= 0)
        {
            kind = QuestController.AlliedDailyPickAccept;
            return firstReady;
        }

        if (startedIndex >= 0 && startedIndex < end && IsActiveAccepted(items[startedIndex], activeSociety))
        {
            Item started = items[startedIndex];
            if (started.Segment != SegmentTurnIn || !HasWorkSharingTurnIn(items, end, activeSociety, startedIndex))
            {
                kind = QuestController.AlliedDailyPickContinue;
                return startedIndex;
            }

            deferredTurnIn = true;
        }

        List<int> work = [];
        List<int> others = [];
        for (int i = 0; i < end; ++i)
        {
            if (IsActiveAccepted(items[i], activeSociety))
            {
                (items[i].Segment == SegmentWork ? work : others).Add(i);
            }
        }

        if (work.Count == 0 && others.Count == 0)
        {
            return -1;
        }

        if (work.Count is > 0 and <= MaxPlannedItems)
        {
            PlanBestOrder(items, work, playerTerritory, playerPosition, plan);
        }
        else
        {
            AppendByDistance(items, work, playerTerritory, playerPosition, plan);
        }

        AppendByDistance(items, others, playerTerritory, playerPosition, plan);
        kind = QuestController.AlliedDailyPickRoute;
        return plan[0];
    }

    private static bool IsActiveAccepted(in Item item, int activeSociety) =>
        item.State == QuestController.AlliedDailyAccepted && item.Society == activeSociety;

    private static bool HasWorkSharingTurnIn(IReadOnlyList<Item> items, int end, int activeSociety, int startedIndex)
    {
        uint npc = items[startedIndex].TurnInNpc;
        if (npc == 0)
        {
            return false;
        }

        for (int i = 0; i < end; ++i)
        {
            Item item = items[i];
            if (i != startedIndex && IsActiveAccepted(item, activeSociety) && item.Segment == SegmentWork &&
                item.TurnInNpc == npc)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>同成本時保留批次裡較前面的（順序穩定）。</summary>
    private static void AppendByDistance(IReadOnlyList<Item> items, List<int> indexes, uint playerTerritory,
        Vector3 playerPosition, List<int> plan)
    {
        List<int> remaining = [..indexes];
        while (remaining.Count > 0)
        {
            int best = 0;
            float bestCost = float.MaxValue;
            for (int r = 0; r < remaining.Count; ++r)
            {
                float cost = TravelCost(items[remaining[r]], playerTerritory, playerPosition, out _);
                if (cost < bestCost)
                {
                    best = r;
                    bestCost = cost;
                }
            }

            plan.Add(remaining[best]);
            remaining.RemoveAt(best);
        }
    }

    /// <summary>
    /// 窮舉「還有目標」任務的順序（最多 <see cref="MaxPlannedItems"/> 個），總成本含最後回去交任務那一段。
    /// 總成本相同（例如從委託人出發繞一圈再回來，正反方向一樣長）時先去近的；再相同就照批次順序。
    /// </summary>
    private static void PlanBestOrder(IReadOnlyList<Item> items, List<int> work, uint playerTerritory,
        Vector3 playerPosition, List<int> plan)
    {
        int[] order = [..work];
        int[] best = [..work];
        float bestCost = float.MaxValue;
        float bestFirstLeg = float.MaxValue;
        Permute(0);
        plan.AddRange(best);

        void Permute(int k)
        {
            if (k == order.Length)
            {
                float cost = RouteCost(items, order, playerTerritory, playerPosition);
                float firstLeg = TravelCost(items[order[0]], playerTerritory, playerPosition, out _);
                if (cost < bestCost - CostTolerance || (cost <= bestCost + CostTolerance && firstLeg < bestFirstLeg))
                {
                    bestCost = cost;
                    bestFirstLeg = firstLeg;
                    order.CopyTo(best, 0);
                }

                return;
            }

            for (int i = k; i < order.Length; ++i)
            {
                (order[k], order[i]) = (order[i], order[k]);
                Permute(k + 1);
                (order[k], order[i]) = (order[i], order[k]);
            }
        }
    }

    internal static float RouteCost(IReadOnlyList<Item> items, IReadOnlyList<int> order, uint playerTerritory,
        Vector3 playerPosition)
    {
        float total = 0f;
        Vector3 position = playerPosition;
        uint territory = playerTerritory;
        Item last = default;
        foreach (int index in order)
        {
            last = items[index];
            total += LegCost(position, territory, last.TerritoryId, last.Position, last.AetherytePosition,
                         last.ForceTeleport, out _) +
                     last.InternalCost;
            if ((last.ExitPosition ?? last.Position) is { } exit)
            {
                position = exit;
                territory = last.TerritoryId;
            }
        }

        if (order.Count > 0 && last.TurnInPosition != null)
        {
            total += LegCost(position, territory, last.TerritoryId, last.TurnInPosition, last.TurnInAetherytePosition,
                false, out _);
        }

        return total;
    }
}

using System;
using System.Collections.Generic;
using System.Numerics;

namespace Questionable.Controller.Steps.Gathering;

/// <summary>
///     採集點之間「近到不必上坐騎」的判斷。純函式，只吃 BCL 型別（離線探針以反射直接呼叫）。
/// </summary>
internal static class GatheringMountRule
{
    /// <summary>
    ///     到下一個採集點的直線距離（米）。已知哪一點可採就量那一點；不知道時取最遠的候選，
    ///     寧可多上一次坐騎也不要走一段長路。沒有候選回 <see cref="float.PositiveInfinity"/>。
    /// </summary>
    public static float DistanceToNextNode(Vector3 playerPosition, IReadOnlyList<Vector3> candidatePositions,
        Vector3? activePosition)
    {
        if (activePosition is { } active)
        {
            return Vector3.Distance(playerPosition, active);
        }

        float farthest = float.PositiveInfinity;
        for (int i = 0; i < candidatePositions.Count; ++i)
        {
            float distance = Vector3.Distance(playerPosition, candidatePositions[i]);
            farthest = i == 0 ? distance : Math.Max(farthest, distance);
        }

        return farthest;
    }

    /// <summary>
    ///     <paramref name="thresholdYalms"/> ≤ 0＝停用（照原規則一律上坐騎）。已在坐騎上、不在目標區域、
    ///     潛水中都回 false（交回原流程，不強制下坐騎）。距離是 NaN 時同樣回 false。
    /// </summary>
    public static bool ShouldWalkInsteadOfMount(bool alreadyMounted, bool inTargetTerritory, bool diving,
        float distanceYalms, float thresholdYalms)
    {
        return !alreadyMounted && inTargetTerritory && !diving && thresholdYalms > 0 &&
               distanceYalms < thresholdYalms;
    }
}

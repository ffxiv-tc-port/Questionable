using ECommons.ExcelServices;
using FFXIVClientStructs.FFXIV.Application.Network.WorkDefinitions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Questionable.Data;
using Questionable.Model;
using Questionable.Model.Common;
using Questionable.Model.Gathering;
using Questionable.Model.Questing;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;

namespace Questionable.Controller;

/// <summary>
/// 估計一個已接的友好部族日常「接下來要去哪些地方」，給 <see cref="AlliedDailyRoute.Pick"/> 排路線用。
/// 只在框架執行緒、持 <c>QuestController._progressLock</c> 時呼叫；不寫記錄、不改遊戲狀態。
/// </summary>
/// <remarks>
/// 估計只用來排順序，步驟仍由原本的工作流程逐一執行（跳過條件照舊判）：估錯的代價是順序差一點，不是做錯事。
/// 採集步驟比照 <c>Gather.DelayedGatheringExecutor</c>：背包已夠數就略過，位置取採集點的第一個點。
/// </remarks>
internal sealed class AlliedDailyRouteEstimator(
    QuestRegistry questRegistry,
    GatheringPointRegistry gatheringPointRegistry,
    AetheryteData aetheryteData,
    ClassJobUtils classJobUtils)
{
    internal readonly record struct Note(byte Sequence, int Step, string Label);

    public static unsafe bool TryReadQuestWork(ElementId id, out byte sequence, out Job acceptJob)
    {
        sequence = 0;
        acceptJob = Job.ADV;
        QuestManager* questManager = QuestManager.Instance();
        if (id is not QuestId questId || questManager == null)
        {
            return false;
        }

        QuestWork* questWork = questManager->GetQuestById(questId.Value);
        if (questWork == null)
        {
            return false;
        }

        sequence = questWork->Sequence;
        acceptJob = (Job)questWork->AcceptClassJob;
        return true;
    }

    /// <param name="startedStep">目前任務就是這一個、而且序列相同時的步驟位置；其他情況傳 -1（從序列第一步估）。</param>
    /// <param name="turnInReady">已經確認「只剩交任務」的任務與當時的序列；估出只剩交任務時會記進去。</param>
    public AlliedDailyRoute.Item Estimate(ElementId id, int society, int state, int startedStep,
        Dictionary<ElementId, byte> turnInReady, out Note note)
    {
        note = new(0, -1, "?");
        AlliedDailyRoute.Item unknown = new(society, state, AlliedDailyRoute.SegmentUnknown, 0, null, null, false, null,
            0f, 0, null, null);
        if (state != QuestController.AlliedDailyAccepted)
        {
            turnInReady.Remove(id);
            return unknown;
        }

        if (!questRegistry.TryGetQuest(id, out Quest? quest) || !TryReadQuestWork(id, out byte sequence, out Job acceptJob))
        {
            return unknown;
        }

        QuestSequence? lastSequence = quest.FindSequence(255);
        QuestStep? turnInStep = lastSequence?.LastStep() is { InteractionType: EInteractionType.CompleteQuest } last
            ? last
            : null;
        AlliedDailyRoute.Item turnIn = unknown with
        {
            TerritoryId = turnInStep?.TerritoryId ?? 0,
            TurnInNpc = turnInStep?.DataId ?? 0,
            TurnInPosition = turnInStep?.Position,
            TurnInAetherytePosition = turnInStep == null
                ? null
                : AetherytePosition(turnInStep.AetheryteShortcut, turnInStep.TerritoryId)
        };
        AlliedDailyRoute.Item turnInSegment = turnIn with
        {
            Segment = AlliedDailyRoute.SegmentTurnIn,
            Position = turnIn.TurnInPosition,
            AetherytePosition = turnIn.TurnInAetherytePosition,
            ExitPosition = turnIn.TurnInPosition
        };
        if (turnInReady.TryGetValue(id, out byte readySequence) && readySequence == sequence && turnInStep != null)
        {
            note = new(sequence, lastSequence!.Steps.Count - 1, "交任務");
            return turnInSegment;
        }

        QuestSequence? questSequence = quest.FindSequence(sequence);
        int start = startedStep >= 0 ? startedStep : 0;
        if (questSequence == null || start >= questSequence.Steps.Count)
        {
            note = new(sequence, start, "等待序列更新");
            return turnIn;
        }

        bool first = true;
        AlliedDailyRoute.Item work = turnIn with { Segment = AlliedDailyRoute.SegmentWork };
        Vector3? exit = null;
        uint exitTerritory = 0;
        foreach ((QuestSequence s, int stepIndex, QuestStep step) in RemainingSteps(quest, questSequence, start))
        {
            if (!IsStepForJobs(step, id, acceptJob))
            {
                continue;
            }

            uint territoryId = step.TerritoryId;
            Vector3? position = step.Position;
            EAetheryteLocation? aetheryte = step.AetheryteShortcut;
            bool forceTeleport = false;
            if (step.InteractionType == EInteractionType.Gather)
            {
                if (!TryGetPendingGatherTarget(step, acceptJob, out territoryId, out position, out EAetheryteLocation? nodeAetheryte))
                {
                    continue;
                }

                aetheryte = nodeAetheryte ?? aetheryte;
                forceTeleport = aetheryte != null;
            }

            if (first)
            {
                first = false;
                note = new(s.Sequence, stepIndex, LabelOf(step.InteractionType));
                if (step.InteractionType == EInteractionType.CompleteQuest)
                {
                    turnInReady[id] = sequence;
                    return turnInSegment;
                }
            }

            if (step.InteractionType == EInteractionType.CompleteQuest)
            {
                break;
            }

            if (position is not { } target)
            {
                continue;
            }

            Vector3? aetherytePosition = AetherytePosition(aetheryte, territoryId);
            if (exit is { } from)
            {
                work = work with
                {
                    InternalCost = work.InternalCost +
                                   AlliedDailyRoute.LegCost(from, exitTerritory, territoryId, target, aetherytePosition,
                                       forceTeleport, out _)
                };
            }
            else
            {
                work = work with
                {
                    TerritoryId = territoryId,
                    Position = target,
                    AetherytePosition = aetherytePosition,
                    ForceTeleport = forceTeleport
                };
            }

            exit = target;
            exitTerritory = territoryId;
        }

        if (first)
        {
            note = new(sequence, questSequence.Steps.Count, "等待序列更新");
            return turnIn;
        }

        return work with { ExitPosition = exit };
    }

    /// <summary>目前序列從 <paramref name="start"/> 起的步驟，接著是之後各序列的全部步驟（255 最後）。</summary>
    private static IEnumerable<(QuestSequence Sequence, int StepIndex, QuestStep Step)> RemainingSteps(Quest quest,
        QuestSequence current, int start)
    {
        for (int i = start; i < current.Steps.Count; ++i)
        {
            yield return (current, i, current.Steps[i]);
        }

        foreach (QuestSequence later in quest.AllSequences().Where(x => x.Sequence > current.Sequence)
                     .OrderBy(x => x.Sequence))
        {
            for (int i = 0; i < later.Steps.Count; ++i)
            {
                yield return (later, i, later.Steps[i]);
            }
        }
    }

    private Vector3? AetherytePosition(EAetheryteLocation? aetheryte, uint territoryId)
    {
        if (aetheryte is not { } location ||
            !aetheryteData.TerritoryIds.TryGetValue(location, out uint aetheryteTerritory) ||
            aetheryteTerritory != territoryId ||
            !aetheryteData.Locations.TryGetValue(location, out Vector3 position))
        {
            return null;
        }

        return position;
    }

    /// <summary>比照 SkipCondition：接任務時的職業、目前職業對不上的步驟會被略過。</summary>
    private unsafe bool IsStepForJobs(QuestStep step, ElementId id, Job acceptJob)
    {
        if (step.RequiredQuestAcceptedJob.Count > 0 && acceptJob != Job.ADV &&
            !step.RequiredQuestAcceptedJob.SelectMany(x => classJobUtils.AsIndividualJobs(x, id)).Contains(acceptJob))
        {
            return false;
        }

        if (step.RequiredCurrentJob.Count > 0)
        {
            Job currentJob = (Job)PlayerState.Instance()->CurrentClassJobId;
            return step.RequiredCurrentJob.SelectMany(x => classJobUtils.AsIndividualJobs(x, id)).Contains(currentJob);
        }

        return true;
    }

    /// <returns><see langword="false"/>＝這一步的道具背包裡都夠了（實際執行時也會直接略過）。</returns>
    private unsafe bool TryGetPendingGatherTarget(QuestStep step, Job acceptJob, out uint territoryId,
        out Vector3? position, out EAetheryteLocation? aetheryte)
    {
        territoryId = step.TerritoryId;
        position = null;
        aetheryte = null;
        InventoryManager* inventoryManager = InventoryManager.Instance();
        foreach (GatheredItem item in step.ItemsToGather)
        {
            if (inventoryManager != null &&
                inventoryManager->GetInventoryItemCount(item.ItemId, minCollectability: (short)item.Collectability) >=
                item.ItemCount)
            {
                continue;
            }

            if (TryFindGatheringRoot(step, item.ItemId, acceptJob, out GatheringRoot? root))
            {
                territoryId = root.Steps.FirstOrDefault()?.TerritoryId ?? step.TerritoryId;
                aetheryte = root.Steps.FirstOrDefault(x => x.AetheryteShortcut != null)?.AetheryteShortcut;
                position = root.Steps.FirstOrDefault(x => x.Position != null)?.Position ??
                           root.Groups.FirstOrDefault()?.Nodes.FirstOrDefault()?.Locations.FirstOrDefault()?.Position;
            }

            return true;
        }

        return false;
    }

    /// <summary>實際採集用目前職業找採集點；目前不是採礦／園藝時改用接任務時的職業，再不行依序試兩者。</summary>
    private unsafe bool TryFindGatheringRoot(QuestStep step, uint itemId, Job acceptJob,
        [NotNullWhen(true)] out GatheringRoot? root)
    {
        root = null;
        if (step.GatheringPoint is ushort fixedPoint)
        {
            return gatheringPointRegistry.TryGetGatheringPoint(new GatheringPointId(fixedPoint), out root);
        }

        Job currentJob = (Job)PlayerState.Instance()->CurrentClassJobId;
        foreach (Job job in new[] { currentJob, acceptJob, Job.MIN, Job.BTN })
        {
            if (job is Job.MIN or Job.BTN &&
                gatheringPointRegistry.TryGetGatheringPointId(itemId, job, out GatheringPointId? pointId) &&
                gatheringPointRegistry.TryGetGatheringPoint(pointId, out root))
            {
                return true;
            }
        }

        return false;
    }

    private static string LabelOf(EInteractionType type) => type switch
    {
        EInteractionType.CompleteQuest => "交任務",
        EInteractionType.Gather => "採集",
        EInteractionType.Interact => "互動",
        EInteractionType.WalkTo => "移動",
        EInteractionType.Say => "說話",
        EInteractionType.Action => "使用技能",
        EInteractionType.Combat => "戰鬥",
        EInteractionType.Craft => "製作",
        EInteractionType.UseItem => "使用道具",
        EInteractionType.Emote => "情感動作",
        EInteractionType.PurchaseItem => "購買",
        EInteractionType.Fish => "釣魚",
        _ => type.ToString()
    };
}

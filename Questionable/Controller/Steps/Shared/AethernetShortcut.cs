using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Logging;
using Questionable.Controller.Steps.Common;
using Questionable.Data;
using Questionable.External;
using Questionable.Functions;
using Questionable.Model;
using Questionable.Model.Common;
using Questionable.Model.Common.Converter;
using Questionable.Model.Questing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
namespace Questionable.Controller.Steps.Shared;

internal static class AethernetShortcut
{
    internal sealed class Factory
    (
        AetheryteData aetheryteData,
        TerritoryData territoryData,
        IClientState clientState)
        : ITaskFactory
    {
        public IEnumerable<ITask> CreateAllTasks(Quest quest, QuestSequence sequence, QuestStep step)
        {
            if (step.AethernetShortcut == null)
            {
                yield break;
            }

            yield return new WaitNavmesh.Task();
            yield return new Task(step.AethernetShortcut.From, step.AethernetShortcut.To,
                step.SkipConditions?.AethernetShortcutIf ?? new());

            if (AetheryteShortcut.MoveAwayFromAetheryteExecutor.AppliesTo(step.AethernetShortcut.To))
            {
                yield return new WaitCondition.Task(
                    () => clientState.TerritoryType == aetheryteData.TerritoryIds[step.AethernetShortcut.To],
                    $"Wait(territory: {territoryData.GetNameAndId(aetheryteData.TerritoryIds[step.AethernetShortcut.To])})");
                yield return new AetheryteShortcut.MoveAwayFromAetheryte(step.AethernetShortcut.To);
            }
        }
    }

    internal sealed record Task
    (
        EAetheryteLocation From,
        EAetheryteLocation To,
        SkipAetheryteCondition SkipConditions) : ISkippableTask
    {
        public Task(EAetheryteLocation from,
            EAetheryteLocation to)
            : this(from, to, new())
        {
        }

        public override string ToString()
        {
            return $"UseAethernet({From} -> {To})";
        }
    }

    internal sealed class UseAethernetShortcut
    (
        ILogger<UseAethernetShortcut> logger,
        AetheryteFunctions aetheryteFunctions,
        GameFunctions gameFunctions,
        QuestFunctions questFunctions,
        IClientState clientState,
        IObjectTable objectTable,
        AetheryteData aetheryteData,
        TerritoryData territoryData,
        LifestreamIpc lifestreamIpc,
        MovementController movementController,
        ICondition condition) : TaskExecutor<Task>
    {
        private DateTime _continueAt = DateTime.MinValue;
        private bool _moving;
        private bool _teleported;
        private bool _triedMounting;
        private EAetheryteLocation? _fallbackAetheryte;
        private DateTime _fallbackTeleportStartedAt = DateTime.MaxValue;

        /// <summary>已經把傳送交給 Lifestream（之後到任務完成前，Lifestream 可能正在替我們跑）。</summary>
        internal bool LifestreamRequested => _teleported;

        protected override bool Start()
        {
            if (!Task.SkipConditions.Never)
            {
                if (Task.SkipConditions.InSameTerritory &&
                    clientState.TerritoryType == aetheryteData.TerritoryIds[Task.To])
                {
                    logger.LogInformation("Skipping aethernet shortcut because the target is in the same territory");
                    return false;
                }

                if (Task.SkipConditions.InTerritory.Contains(clientState.TerritoryType))
                {
                    logger.LogInformation(
                        "Skipping aethernet shortcut because the target is in the specified territory");
                    return false;
                }

                if (Task.SkipConditions.QuestsCompleted.Count > 0 &&
                    Task.SkipConditions.QuestsCompleted.All(questFunctions.IsQuestComplete))
                {
                    logger.LogInformation("Skipping aethernet shortcut, all prequisite quests are complete");
                    return true;
                }

                if (Task.SkipConditions.QuestsAccepted.Count > 0 &&
                    Task.SkipConditions.QuestsAccepted.All(questFunctions.IsQuestAccepted))
                {
                    logger.LogInformation("Skipping aethernet shortcut, all prequisite quests are accepted");
                    return true;
                }

                if (Task.SkipConditions.AetheryteLocked != null &&
                    !aetheryteFunctions.IsAetheryteUnlocked(Task.SkipConditions.AetheryteLocked.Value))
                {
                    logger.LogInformation("Skipping aethernet shortcut because the target aetheryte is locked");
                    return false;
                }

                if (Task.SkipConditions.AetheryteUnlocked != null &&
                    aetheryteFunctions.IsAetheryteUnlocked(Task.SkipConditions.AetheryteUnlocked.Value))
                {
                    logger.LogInformation("Skipping aethernet shortcut because the target aetheryte is unlocked");
                    return false;
                }
            }

            if (aetheryteFunctions.IsAetheryteUnlocked(Task.From) &&
                aetheryteFunctions.IsAetheryteUnlocked(Task.To))
            {
                uint territoryType = clientState.TerritoryType;
                Vector3 playerPosition = objectTable[0]!.Position;

                // closer to the source
                if (aetheryteData.CalculateDistance(playerPosition, territoryType, Task.From) <
                    aetheryteData.CalculateDistance(playerPosition, territoryType, Task.To))
                {
                    if (aetheryteData.CalculateDistance(playerPosition, territoryType, Task.From) <
                        (Task.From.IsFirmamentAetheryte() ? 11f : 4f))
                    {
                        DoTeleport();
                        return true;
                    }
                    else if (Task.From == EAetheryteLocation.SolutionNine)
                    {
                        logger.LogInformation("Moving to S9 aetheryte");
                        List<Vector3> nearbyPoints =
                        [
                            new(0, 8.442986f, 9),
                            new(9, 8.442986f, 0),
                            new(-9, 8.442986f, 0),
                            new(0, 8.442986f, -9)
                        ];

                        Vector3 closestPoint = nearbyPoints.MinBy(x => Vector3.Distance(playerPosition, x));
                        _moving = true;
                        movementController.NavigateTo(EMovementType.Quest, (uint)Task.From, closestPoint, false, true,
                            0.25f);
                        return true;
                    }
                    else
                    {
                        if (territoryData.CanUseMount(territoryType) &&
                            aetheryteData.CalculateDistance(playerPosition, territoryType, Task.From) > 30 &&
                            !gameFunctions.HasStatusPreventingMount())
                        {
                            _triedMounting = gameFunctions.Mount();
                            if (_triedMounting)
                            {
                                _continueAt = DateTime.Now.AddSeconds(0.5);
                                return true;
                            }
                        }

                        MoveTo();
                        return true;
                    }
                }
            }
            else if (clientState.TerritoryType == aetheryteData.TerritoryIds[Task.To])
            {
                logger.LogWarning(
                    "Aethernet shortcut not unlocked (from: {FromAetheryte}, to: {ToAetheryte}), skipping as we are already in the destination territory",
                    Task.From, Task.To);
            }
            else if (aetheryteFunctions.IsTeleportUnlocked() &&
                     FindFallbackAetheryte(Task.To, aetheryteData.TerritoryIds, aetheryteData.Locations,
                         aetheryteFunctions.IsAetheryteUnlocked) is { } fallback)
            {
                logger.LogInformation(
                    "都市傳送網水晶尚未開通（{From} 已開通={FromUnlocked}、{To} 已開通={ToUnlocked}），改用傳送到同一區的乙太之光 {Fallback}，之後走路過去",
                    Task.From, aetheryteFunctions.IsAetheryteUnlocked(Task.From),
                    Task.To, aetheryteFunctions.IsAetheryteUnlocked(Task.To), fallback);
                _fallbackAetheryte = fallback;
                return true;
            }
            else
            {
                throw new TaskException($"Aethernet shortcut not unlocked (from: {Task.From}, to: {Task.To})");
            }

            return false;
        }

        /// <summary>
        /// 目的地那一區裡、已開通的大型乙太之光，離目的地水晶最近的一座；沒有就回 null。
        /// 只在 <see cref="Start"/> 呼叫一次，不進每幀路徑。
        /// </summary>
        internal static EAetheryteLocation? FindFallbackAetheryte(EAetheryteLocation to,
            IReadOnlyDictionary<EAetheryteLocation, uint> territoryIds,
            IReadOnlyDictionary<EAetheryteLocation, Vector3> locations,
            Func<EAetheryteLocation, bool> isUnlocked)
        {
            if (!territoryIds.TryGetValue(to, out uint targetTerritory))
            {
                return null;
            }

            bool hasTargetPosition = locations.TryGetValue(to, out Vector3 targetPosition);
            return territoryIds
                .Where(x => x.Value == targetTerritory && AetheryteConverter.IsLargeAetheryte(x.Key))
                .Select(x => x.Key)
                .Where(isUnlocked)
                .OrderBy(x => hasTargetPosition && locations.TryGetValue(x, out Vector3 p)
                    ? Vector3.Distance(p, targetPosition)
                    : float.MaxValue)
                .ThenBy(x => (uint)x)
                .Select(x => (EAetheryteLocation?)x)
                .FirstOrDefault();
        }

        private void MoveTo()
        {
            logger.LogInformation("Moving to aethernet shortcut");
            _moving = true;
            float distance = Task.From switch
            {
                var _ when Task.From.IsFirmamentAetheryte() => 4.4f,
                EAetheryteLocation.UldahChamberOfRule => 5f,
                var _ when AetheryteConverter.IsLargeAetheryte(Task.From) => 10.9f,
                var _ => 6.9f
            };

            bool goldSaucerAethernetShard = aetheryteData.IsGoldSaucerAetheryte(Task.From) &&
                                            !AetheryteConverter.IsLargeAetheryte(Task.From);
            movementController.NavigateTo(EMovementType.Quest, (uint)Task.From, aetheryteData.Locations[Task.From],
                false, true, distance,
                goldSaucerAethernetShard ? 5f : null);
        }

        private void DoTeleport()
        {
            logger.LogInformation("Using lifestream to teleport to {Destination}", Task.To);
            lifestreamIpc.Teleport(Task.To);
            _teleported = true;
        }

        public override ETaskResult Update()
        {
            if (DateTime.Now < _continueAt)
            {
                return ETaskResult.StillRunning;
            }

            if (_fallbackAetheryte is { } fallback)
            {
                return UpdateFallbackTeleport(fallback);
            }

            if (_triedMounting)
            {
                if (condition[ConditionFlag.Mounted])
                {
                    _triedMounting = false;
                    MoveTo();
                    return ETaskResult.StillRunning;
                }
                else
                {
                    return ETaskResult.StillRunning;
                }
            }

            if (_moving)
            {
                DateTime movementStartedAt = movementController.MovementStartedAt;
                if (movementStartedAt == DateTime.MaxValue || movementStartedAt.AddSeconds(2) >= DateTime.Now)
                {
                    return ETaskResult.StillRunning;
                }

                if (!movementController.IsPathfinding && !movementController.IsPathRunning)
                {
                    _moving = false;
                }

                return ETaskResult.StillRunning;
            }

            if (!_teleported)
            {
                DoTeleport();
                return ETaskResult.StillRunning;
            }

            if (objectTable[0] == null)
            {
                return ETaskResult.StillRunning;
            }
            Vector3? position = objectTable[0]!.Position;
            if (position == null)
            {
                return ETaskResult.StillRunning;
            }

            if (aetheryteData.IsAirshipLanding(Task.To))
            {
                if (aetheryteData.CalculateAirshipLandingDistance(position.Value, clientState.TerritoryType, Task.To) > 5)
                {
                    return ETaskResult.StillRunning;
                }
            }
            else if (aetheryteData.IsCityAetheryte(Task.To) || aetheryteData.IsGoldSaucerAetheryte(Task.To))
            {
                if (aetheryteData.CalculateDistance(position.Value, clientState.TerritoryType, Task.To) > 20)
                {
                    return ETaskResult.StillRunning;
                }
            }
            else
            {
                // some overworld location (e.g. 'Tesselation (Lakeland)' would end up here
                if (clientState.TerritoryType != aetheryteData.TerritoryIds[Task.To])
                {
                    return ETaskResult.StillRunning;
                }
            }


            return ETaskResult.TaskComplete;
        }

        /// <summary>完成條件只看「到了目的地水晶那一區」；區內剩下的路交給後面的移動工作。</summary>
        private ETaskResult UpdateFallbackTeleport(EAetheryteLocation fallback)
        {
            if (_fallbackTeleportStartedAt == DateTime.MaxValue)
            {
                if (!aetheryteFunctions.CanTeleport(fallback))
                {
                    _continueAt = DateTime.Now.AddSeconds(1);
                    return ETaskResult.StillRunning;
                }

                ProgressContext = InteractionProgressContext.FromActionUseOrDefault(() =>
                    aetheryteFunctions.TeleportAetheryte(fallback));
                if (ProgressContext == null)
                {
                    throw new TaskException($"Unable to teleport to aetheryte {fallback} (aethernet fallback)");
                }

                _fallbackTeleportStartedAt = DateTime.Now;
                _continueAt = DateTime.Now.AddSeconds(8);
                return ETaskResult.StillRunning;
            }

            if (clientState.TerritoryType == aetheryteData.TerritoryIds[Task.To])
            {
                return ETaskResult.TaskComplete;
            }

            // 傳送沒成行（Gil 不足、被打斷又沒被判成中斷）時不要無聲地永遠等下去，退回原本的停下。
            if (DateTime.Now > _fallbackTeleportStartedAt.AddSeconds(45) &&
                !condition[ConditionFlag.BetweenAreas] && !condition[ConditionFlag.BetweenAreas51])
            {
                throw new TaskException($"Aethernet fallback teleport to {fallback} did not arrive in the destination territory");
            }

            return ETaskResult.StillRunning;
        }

        public override bool ShouldInterruptOnDamage()
        {
            return true;
        }
    }
}

using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Questionable.Controller.Steps.Common;
using Questionable.Controller.Steps.Shared;
using Questionable.Data;
using Questionable.External;
using Questionable.Functions;
using Questionable.Model;
using Questionable.Model.Questing;
using Questionable.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
namespace Questionable.Controller.Steps.Interactions;

internal static class SinglePlayerDuty
{
    internal static class SpecialTerritories
    {
        public const ushort Lahabrea = 1052;
        public const ushort ItsProbablyATrap = 665;
        public const ushort Naadam = 688;
        public const ushort Patisserie = 1298;
    }

    internal sealed class Factory
    (
        BossModIpc bossModIpc,
        TerritoryData territoryData,
        IObjectTable objectTable,
        ICondition condition,
        IClientState clientState,
        Configuration configuration) : ITaskFactory
    {
        public IEnumerable<ITask> CreateAllTasks(Quest quest, QuestSequence sequence, QuestStep step)
        {
            if (step.InteractionType != EInteractionType.SinglePlayerDuty)
            {
                yield break;
            }

            if (bossModIpc.IsConfiguredToRunSoloInstance(quest.Id, step.SinglePlayerDutyOptions))
            {
                uint cfcId = 0;
                uint tId = 0;
                TerritoryData.ContentFinderConditionData? cfcData = null;
                if (quest.Id.Value.Equals(5325))
                {
                    cfcId = 1045;
                    tId = 1298;
                }
                else if (!territoryData.TryGetContentFinderConditionForSoloInstance(quest.Id, step.SinglePlayerDutyIndex, out cfcData))
                {
                    throw new TaskException("Failed to get content finder condition for solo instance");
                }
                if (cfcData != null)
                {
                    cfcId = cfcData.ContentFinderConditionId;
                    tId = cfcData.TerritoryId;
                }

                yield return new Mount.UnmountTask();
                if (tId == SpecialTerritories.Patisserie)
                {
                    yield return new Commence(cfcId);
                }
                yield return new StartSinglePlayerDuty(cfcId);
                yield return new WaitAtStart.WaitDelay(TimeSpan.FromSeconds(2)); // maybe a delay will work here too, needs investigation
                if (tId == SpecialTerritories.Lahabrea)
                {
                    yield return new SetTarget(14643);
                    yield return new EnableAi();
                    yield return new WaitCondition.Task(
                        () => condition[ConditionFlag.Unconscious] || clientState.TerritoryType != SpecialTerritories.Lahabrea,
                        "Wait(death)");
                    yield return new DisableAi();
                    yield return new WaitCondition.Task(
                        () => !condition[ConditionFlag.Unconscious] || clientState.TerritoryType != SpecialTerritories.Lahabrea,
                        "Wait(resurrection)");
                    yield return new EnableAi();
                }
                else if (tId is SpecialTerritories.ItsProbablyATrap)
                {
                    yield return new WaitCondition.Task(() => DutyActionsAvailable() || clientState.TerritoryType != SpecialTerritories.ItsProbablyATrap,
                        "Wait(Phase 2)");
                    yield return new EnableAi(true);
                }
                else if (tId is SpecialTerritories.Naadam)
                {
                    yield return new WaitCondition.Task(
                        () =>
                        {
                            if (clientState.TerritoryType != SpecialTerritories.Naadam)
                            {
                                return true;
                            }

                            Vector3 pos = objectTable[0]?.Position ?? default;
                            return (new Vector3(352.01f, -1.45f, 288.59f) - pos).Length() < 10f;
                        },
                        "Wait(moving to Ovoo)");
                    yield return new Mount.UnmountTask();
                    yield return new EnableAi();
                }
                else if (tId == SpecialTerritories.Patisserie)
                {
                    yield return new SetPreset(BossModIpc.EPreset.NormalMovement);
                }
                else
                {
                    yield return new EnableAi(tId == SpecialTerritories.Naadam);
                }

                yield return new WaitSinglePlayerDuty(cfcId);
                yield return new DisableAi();
                if (configuration.SinglePlayerDuties.StopOnFailure)
                {
                    yield return new WaitDutyOutcome(quest.Id, sequence.Sequence, cfcId);
                }

                yield return new WaitAtEnd.WaitNextStepOrSequence();
            }
        }

        private unsafe bool DutyActionsAvailable()
        {
            // 🔴 RaptureHotbarModule.Instance() 是手寫包裝，UIModule 還沒建好時合法回 null；
            //    對 null 解參考是 try/catch 攔不到的 AccessViolation。
            // fail-closed：回 false＝「任務技能還沒出現」＝輪詢述詞繼續等；回 true 會讓流程誤判階段已推進
            //    而提早 EnableAi(true)。這是輪詢述詞，刻意不寫 log。
            RaptureHotbarModule* hotbarModule = RaptureHotbarModule.Instance();
            return hotbarModule != null && hotbarModule->DutyActionsPresent;
        }
    }

    internal sealed record StartSinglePlayerDuty(uint ContentFinderConditionId) : ITask
    {
        public override string ToString()
        {
            return $"Wait(BossMod, entered instance {ContentFinderConditionId})";
        }
    }

    internal sealed class StartSinglePlayerDutyExecutor(ICondition condition) : TaskExecutor<StartSinglePlayerDuty>
    {
        private DateTime _enteredAt = DateTime.MinValue;

        protected override bool Start()
        {
            return true;
        }

        public override unsafe ETaskResult Update()
        {
            GameMain* gameMain = GameMain.Instance();
            if (gameMain->CurrentContentFinderConditionId != Task.ContentFinderConditionId)
            {
                return ETaskResult.StillRunning;
            }

            if (!condition[ConditionFlag.BoundByDuty])
            {
                return ETaskResult.StillRunning;
            }

            // we add a minimum wait time to try avoid issues with starting too early
            // could also be adding unnecessary wait time but needs more investigation ig
            if (_enteredAt == DateTime.MinValue)
            {
                _enteredAt = DateTime.Now;
            }

            return DateTime.Now - _enteredAt >= TimeSpan.FromSeconds(2)
                ? ETaskResult.TaskComplete
                : ETaskResult.StillRunning;
        }

        public override bool ShouldInterruptOnDamage()
        {
            return false;
        }
    }

    internal sealed record EnableAi(bool Passive = false) : ITask
    {
        public override string ToString()
        {
            return $"BossMod.EnableAi({(Passive ? "Passive" : "AutoPull")})";
        }
    }

    internal sealed class EnableAiExecutor
    (
        BossModIpc bossModIpc) : TaskExecutor<EnableAi>
    {
        protected override bool Start()
        {
            bossModIpc.EnableAi(Task.Passive);
            return true;
        }

        public override ETaskResult Update()
        {
            return ETaskResult.TaskComplete;
        }

        public override bool ShouldInterruptOnDamage()
        {
            return false;
        }
    }

    internal sealed record SetPreset(BossModIpc.EPreset Preset) : ITask
    {
        public override string ToString()
        {
            return $"BossMod.SetPreset({Enum.GetName(Preset)})";
        }
    }

    internal sealed class SetPresetExecutor
    (
        BossModIpc bossModIpc) : TaskExecutor<SetPreset>
    {
        protected override bool Start()
        {
            bossModIpc.SetPreset(Task.Preset);
            return true;
        }

        public override ETaskResult Update()
        {
            return ETaskResult.TaskComplete;
        }

        public override bool ShouldInterruptOnDamage()
        {
            return false;
        }
    }

    internal sealed record WaitSinglePlayerDuty(uint ContentFinderConditionId) : ITask
    {
        public override string ToString()
        {
            return $"Wait(BossMod, left instance {ContentFinderConditionId})";
        }
    }

    internal sealed class WaitSinglePlayerDutyExecutor
    (
        BossModIpc bossModIpc,
        MovementController movementController)
        : TaskExecutor<WaitSinglePlayerDuty>, IStoppableTaskExecutor, IDebugStateProvider
    {
        public string? GetDebugState()
        {
            if (!movementController.IsNavmeshReady)
            {
                return $"Navmesh: {movementController.BuiltNavmeshPercent}%";
            }
            else
            {
                return null;
            }
        }

        public override unsafe ETaskResult Update()
        {
            return GameMain.Instance()->CurrentContentFinderConditionId != Task.ContentFinderConditionId
                ? ETaskResult.TaskComplete
                : ETaskResult.StillRunning;
        }

        public void StopNow()
        {
            bossModIpc.DisableAi();
        }

        public override bool ShouldInterruptOnDamage()
        {
            return false;
        }
        protected override bool Start()
        {
            return true;
        }
    }

    internal sealed record DisableAi : ITask
    {
        public override string ToString()
        {
            return "BossMod.DisableAi";
        }
    }

    internal sealed record WaitDutyOutcome(ElementId QuestId, byte Sequence, uint ContentFinderConditionId) : ITask
    {
        public override string ToString()
        {
            return $"Wait(BossMod, quest progress after instance {ContentFinderConditionId})";
        }
    }

    /// <summary>
    /// 離開任務戰鬥副本之後等任務進度前進；等不到就當成戰鬥失敗，停下自動任務並求助，不重試。
    /// </summary>
    /// <remarks>
    /// 成功時 <see cref="QuestController"/> 一看到任務序列前進就清空佇列，這個執行器不會再被呼叫
    /// ⇒ 它還在跑本身就代表進度沒有前進。計時只算真的有被呼叫的時間：讀取畫面、過場、
    /// 對話中控制器不呼叫 Update，那段時間不算；回到副本裡（手動重打）時歸零。
    /// 判定前再直接讀一次遊戲的任務序列：已經不是建立任務時那一段（控制器只是還沒跟上）就不判失敗。
    /// </remarks>
    internal sealed class WaitDutyOutcomeExecutor
    (
        ICondition condition,
        QuestController questController,
        QuestFunctions questFunctions,
        TerritoryData territoryData) : TaskExecutor<WaitDutyOutcome>
    {
        private static readonly TimeSpan NoProgressLimit = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan MaxCreditPerUpdate = TimeSpan.FromMilliseconds(250);

        private TimeSpan _outsideWithoutProgress;
        private DateTime _lastUpdate;

        protected override bool Start()
        {
            _lastUpdate = DateTime.Now;
            return true;
        }

        public override ETaskResult Update()
        {
            DateTime now = DateTime.Now;
            TimeSpan sinceLastUpdate = now - _lastUpdate;
            _lastUpdate = now;

            if (condition[ConditionFlag.BoundByDuty])
            {
                _outsideWithoutProgress = TimeSpan.Zero;
                return ETaskResult.StillRunning;
            }

            _outsideWithoutProgress += sinceLastUpdate < MaxCreditPerUpdate ? sinceLastUpdate : MaxCreditPerUpdate;
            if (_outsideWithoutProgress < NoProgressLimit)
            {
                return ETaskResult.StillRunning;
            }

            if (Task.QuestId is QuestId &&
                questFunctions.GetQuestProgressInfo(Task.QuestId)?.Sequence != Task.Sequence)
            {
                _outsideWithoutProgress = TimeSpan.Zero;
                return ETaskResult.StillRunning;
            }

            string dutyName = territoryData.GetContentFinderCondition(Task.ContentFinderConditionId)?.Name ?? "?";
            questController.StopAfterSinglePlayerDutyFailure(Task.ContentFinderConditionId, dutyName,
                $"離開副本後 {NoProgressLimit.TotalSeconds:F0} 秒任務進度沒有前進");

            // 上面那一行已經清空佇列；照 StillRunning 回去，呼叫端不會再碰這個執行器。
            return ETaskResult.StillRunning;
        }

        public override bool ShouldInterruptOnDamage()
        {
            return false;
        }
    }

    internal sealed class DisableAiExecutor
    (
        BossModIpc bossModIpc) : TaskExecutor<DisableAi>
    {
        protected override bool Start()
        {
            bossModIpc.DisableAi();
            return true;
        }

        public override ETaskResult Update()
        {
            return ETaskResult.TaskComplete;
        }

        public override bool ShouldInterruptOnDamage()
        {
            return false;
        }
    }

    // TODO this should be handled in VBM
    internal sealed record SetTarget(uint DataId) : ITask
    {
        public override string ToString()
        {
            return $"SetTarget({DataId})";
        }
    }

    internal sealed class SetTargetExecutor
    (
        ITargetManager targetManager,
        IObjectTable objectTable) : TaskExecutor<SetTarget>
    {
        protected override bool Start()
        {
            return true;
        }

        public override ETaskResult Update()
        {
            if (GameFunctions.GetBaseID(targetManager.Target) == Task.DataId)
            {
                return ETaskResult.TaskComplete;
            }

            IGameObject? gameObject = objectTable.FirstOrDefault(x => GameFunctions.GetBaseID(x) == Task.DataId);
            if (gameObject == null)
            {
                return ETaskResult.StillRunning;
            }

            targetManager.Target = gameObject;
            return ETaskResult.StillRunning;
        }

        public override bool ShouldInterruptOnDamage()
        {
            return false;
        }
    }

    // TODO valentiones hack
    internal sealed record Commence(uint ContentFinderConditionId) : ITask
    {
        public override string ToString()
        {
            return $"Commence({ContentFinderConditionId})";
        }
    }

    internal sealed class CommenceExecutor(ICondition condition) : TaskExecutor<Commence>
    {
        private DateTime _enteredAt = DateTime.MinValue;
        protected override bool Start()
        {
            return true;
        }

        public override unsafe ETaskResult Update()
        {
            if (GenericHelpers.TryGetAddonMaster(out AddonMaster.ContentsFinderConfirm m) && m.IsAddonReady)
            {
                // EzThrottler 記的是「上一次動作在哪個時刻」而不是「這扇窗按過了」，
                // 而且 key 全外掛共用、首次必放行 —— 真正擋住重按的是守衛。
                if (EzThrottler.Throttle("Confirm", 2000) &&
                    AddonPressGuard.TryBeginPress("ContentsFinderConfirm", m.Base, "commence"))
                {
                    m.Commence();
                }
            }
            GameMain* gameMain = GameMain.Instance();
            if (gameMain->CurrentContentFinderConditionId != Task.ContentFinderConditionId)
            {
                return ETaskResult.StillRunning;
            }

            if (!condition[ConditionFlag.BoundByDuty])
            {
                return ETaskResult.StillRunning;
            }
            if (_enteredAt == DateTime.MinValue)
            {
                _enteredAt = DateTime.Now;
            }

            return DateTime.Now - _enteredAt >= TimeSpan.FromSeconds(2)
                ? ETaskResult.TaskComplete
                : ETaskResult.StillRunning;
        }

        public override bool ShouldInterruptOnDamage()
        {
            return false;
        }
    }
}

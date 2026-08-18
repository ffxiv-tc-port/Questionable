using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.Interop;
using Questionable.External;
using Questionable.Model;
using Questionable.Model.Questing;
using System;
namespace Questionable.Controller.Steps.Interactions;

internal static class EquipRecommended
{
    internal sealed class Factory : SimpleTaskFactory
    {
        public override ITask? CreateTask(Quest quest, QuestSequence sequence, QuestStep step)
        {
            if (step.InteractionType != EInteractionType.EquipRecommended)
            {
                return null;
            }

            return new EquipTask();
        }
    }

    internal sealed class BeforeDutyOrInstance : SimpleTaskFactory
    {
        public override ITask? CreateTask(Quest quest, QuestSequence sequence, QuestStep step)
        {
            if (step.InteractionType != EInteractionType.Duty &&
                step.InteractionType != EInteractionType.SinglePlayerDuty &&
                step.InteractionType != EInteractionType.Combat)
            {
                return null;
            }

            return new EquipTask();
        }
    }

    internal sealed class EquipTask : ITask
    {
        public override string ToString()
        {
            return "EquipRecommended";
        }
    }

    internal sealed unsafe class DoEquipRecommended(IChatGui chatGui, ICondition condition, Configuration config, StylistIpc stylist)
        : TaskExecutor<EquipTask>
    {
        private bool _checkedOrTriggeredEquipmentUpdate;
        private DateTime _continueAt = DateTime.MinValue;

        protected override bool Start()
        {
            if (condition[ConditionFlag.InCombat])
            {
                return false;
            }

            switch (config.General.GearsetUpdateSource)
            {
                case Configuration.EGearsetUpdateSource.Vanilla:
                    // RecommendEquipModule.Instance() 走 UIModule，UI 尚未建立時回 null（CS 手寫實作）。
                    // 取不到就 return false ＝ 這個任務被跳過，與上面 InCombat 相同的失敗形式。
                    RecommendEquipModule* recommendEquipModule = RecommendEquipModule.Instance();
                    if (recommendEquipModule == null)
                        return false;

                    recommendEquipModule->SetupForClassJob(PlayerState.Instance()->CurrentClassJobId);
                    break;
                case Configuration.EGearsetUpdateSource.Stylist:
                    // RaptureGearsetModule.Instance() 走 UIModule，UI 尚未建立時回 null（CS 手寫實作）。
                    // 取不到就 return false ＝ 這個任務被跳過，與上面 InCombat 相同的失敗形式。
                    RaptureGearsetModule* gearsetModule = RaptureGearsetModule.Instance();
                    if (gearsetModule == null)
                        return false;

                    gearsetModule->UpdateGearset(gearsetModule->CurrentGearsetIndex);
                    break;
            }
            return true;
        }

        public override ETaskResult Update()
        {
            switch (config.General.GearsetUpdateSource)
            {
                case Configuration.EGearsetUpdateSource.Vanilla:
                    // 同 Start()：UIModule 還沒建立時回 null。這裡的「跳過」等價物是 TaskComplete，
                    // 讓佇列往下走；回 StillRunning 會讓任務永遠卡住。
                    RecommendEquipModule* recommendedEquipModule = RecommendEquipModule.Instance();
                    if (recommendedEquipModule == null)
                    {
                        return ETaskResult.TaskComplete;
                    }

                    if (recommendedEquipModule->IsUpdating)
                    {
                        return ETaskResult.StillRunning;
                    }

                    if (!_checkedOrTriggeredEquipmentUpdate)
                    {
                        if (!IsAllRecommendeGearEquipped())
                        {
                            chatGui.Print("Equipping recommended gear.", CommandHandler.MessageTag, CommandHandler.TagColor);
                            recommendedEquipModule->EquipRecommendedGear();
                            _continueAt = DateTime.Now.AddSeconds(1);
                        }

                        _checkedOrTriggeredEquipmentUpdate = true;
                        return ETaskResult.StillRunning;
                    }
                    break;
                case Configuration.EGearsetUpdateSource.Stylist:
                    if (stylist.IsBusy)
                    {
                        return ETaskResult.StillRunning;
                    }
                    else if (!_checkedOrTriggeredEquipmentUpdate)
                    {
                        stylist.UpdateGearset();
                        _checkedOrTriggeredEquipmentUpdate = true;
                        _continueAt = DateTime.Now.AddSeconds(1);
                        return ETaskResult.StillRunning;
                    }
                    break;
            }

            return DateTime.Now >= _continueAt ? ETaskResult.TaskComplete : ETaskResult.StillRunning;
        }

        private bool IsAllRecommendeGearEquipped()
        {
            // 判斷不出來時回 true（＝「都已裝備」），呼叫端就不會去呼叫 EquipRecommendedGear()。
            // 回 false 反而會對可能是 null 的模組發動裝備動作。
            RecommendEquipModule* recommendedEquipModule = RecommendEquipModule.Instance();
            if (recommendedEquipModule == null)
            {
                return true;
            }

            InventoryManager* inventoryManager = InventoryManager.Instance();
            InventoryContainer* equippedItems =
                inventoryManager->GetInventoryContainer(InventoryType.EquippedItems);
            if (equippedItems == null)
            {
                return true;
            }

            bool isAllEquipped = true;
            foreach(Pointer<InventoryItem> recommendedItemPtr in recommendedEquipModule->RecommendedItems)
            {
                InventoryItem* recommendedItem = recommendedItemPtr.Value;
                if (recommendedItem == null || recommendedItem->ItemId == 0)
                {
                    continue;
                }

                bool isEquipped = false;
                for(int i = 0; i < equippedItems->Size; ++i)
                {
                    InventoryItem equippedItem = equippedItems->Items[i];
                    if (equippedItem.ItemId != 0 && equippedItem.ItemId == recommendedItem->ItemId)
                    {
                        isEquipped = true;
                        break;
                    }
                }

                if (!isEquipped)
                {
                    isAllEquipped = false;
                }
            }

            return isAllEquipped;
        }

        public override bool ShouldInterruptOnDamage()
        {
            return true;
        }
    }
}

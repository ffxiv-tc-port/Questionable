using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using Microsoft.Extensions.Logging;
using Questionable.Controller.Steps.Interactions;
using Questionable.Controller.Utils;
using Questionable.Model;
using Questionable.Model.Questing;
using Questionable.Utils;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Action = Lumina.Excel.Sheets.Action;
using BattleChara = FFXIVClientStructs.FFXIV.Client.Game.Character.BattleChara;
using ContentFinderCondition = Lumina.Excel.Sheets.ContentFinderCondition;
using ObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;
using Quest = Questionable.Model.Quest;

namespace Questionable.Functions;

internal sealed unsafe class GameFunctions
(
    QuestFunctions questFunctions,
    IDataManager dataManager,
    IObjectTable objectTable,
    ITargetManager targetManager,
    ICondition condition,
    IClientState clientState,
    IGameGuiAdapter gameGui,
    Configuration configuration,
    ILogger<GameFunctions> logger,
    HighlightObject highlightObject)
{
    private readonly AbandonDutyDelegate _abandonDuty =
        Marshal.GetDelegateForFunctionPointer<AbandonDutyDelegate>(EventFramework.Addresses.LeaveCurrentContent.Value);
    private readonly IClientState _clientState = clientState;
    private readonly ICondition _condition = condition;
    private readonly Configuration _configuration = configuration;
    private readonly ReadOnlyDictionary<uint, uint> _contentFinderConditionToContentId = dataManager.GetExcelSheet<ContentFinderCondition>()
        .Where(x => x.RowId > 0 && x.Content.RowId > 0)
        .ToDictionary(x => x.RowId, x => x.Content.RowId)
        .AsReadOnly();
    private readonly IDataManager _dataManager = dataManager;
    private readonly IGameGuiAdapter _gameGui = gameGui;
    private readonly HighlightObject _highlightObject = highlightObject;
    private readonly ILogger<GameFunctions> _logger = logger;
    private readonly IObjectTable _objectTable = objectTable;

    private readonly QuestFunctions _questFunctions = questFunctions;
    private readonly ITargetManager _targetManager = targetManager;

    private readonly ReadOnlyDictionary<uint, uint> _territoryToAetherCurrentCompFlgSet = dataManager.GetExcelSheet<TerritoryType>()
        .Where(x => x.RowId > 0)
        .Where(x => x.AetherCurrentCompFlgSet.RowId > 0)
        .ToDictionary(x => x.RowId, x => x.AetherCurrentCompFlgSet.RowId)
        .AsReadOnly();

    public bool IsFlyingUnlocked(uint territoryId)
    {
        if (_configuration.Advanced.NeverFly)
        {
            return false;
        }

        if (_questFunctions.IsQuestAccepted(new(3304)) && _condition[ConditionFlag.Mounted])
        {
            // special quest amaro, not the normal one
            // TODO Check if this also applies to beast tribe mounts
            if (GetMountId() == 198)
            {
                return true;
            }
        }

        PlayerState* playerState = PlayerState.Instance();
        return playerState != null &&
               _territoryToAetherCurrentCompFlgSet.TryGetValue(territoryId, out uint aetherCurrentCompFlgSet) &&
               playerState->IsAetherCurrentZoneComplete(aetherCurrentCompFlgSet);
    }

    public ushort? GetMountId()
    {
        BattleChara* battleChara = (BattleChara*)(_objectTable[0]?.Address ?? 0);
        if (battleChara != null && battleChara->Mount.MountId != 0)
        {
            return battleChara->Mount.MountId;
        }
        else
        {
            return null;
        }
    }

    public bool IsFlyingUnlockedInCurrentZone()
    {
        return IsFlyingUnlocked(_clientState.TerritoryType);
    }

    public bool IsAetherCurrentUnlocked(uint aetherCurrentId)
    {
        PlayerState* playerState = PlayerState.Instance();
        return playerState != null &&
               playerState->IsAetherCurrentUnlocked(aetherCurrentId);
    }

    public IGameObject? FindObjectByDataId(uint dataId, ObjectKind? kind = null)
    {
        foreach(IGameObject gameObject in _objectTable)
        {
            if (gameObject.ObjectKind is ObjectKind.Player or ObjectKind.Companion or ObjectKind.MountType
                or ObjectKind.Retainer or ObjectKind.Housing)
            {
                continue;
            }

            // multiple objects in the object table can share the same data id for gathering points; only one of those
            // (at most) is visible
            if (gameObject is { ObjectKind: ObjectKind.GatheringPoint, IsTargetable: false })
            {
                continue;
            }

            if (GetBaseID(gameObject) == dataId && (kind == null || kind.Value == gameObject.ObjectKind))
            {
                _highlightObject.AddHighlight(GetBaseID(gameObject));
                return gameObject;
            }
        }

        _logger.LogWarning("Could not find GameObject with dataId {DataId}", dataId);
        return null;
    }

    public bool InteractWith(uint dataId, ObjectKind? kind = null)
    {
        IGameObject? gameObject = FindObjectByDataId(dataId, kind);
        if (gameObject != null)
        {
            return InteractWith(gameObject);
        }

        _logger.LogDebug("Game object is null");
        return false;
    }

    public bool InteractWith(IGameObject gameObject)
    {
        _logger.LogInformation("Setting target with {DataId} to {ObjectId}", GetBaseID(gameObject), gameObject.EntityId);
        _targetManager.Target = null;
        _targetManager.Target = gameObject;

        if (gameObject.ObjectKind == ObjectKind.GatheringPoint)
        {
            TargetSystem.Instance()->OpenObjectInteraction((GameObject*)gameObject.Address);
            _logger.LogInformation("Interact result: (none) for GatheringPoint");
            return true;
        }
        else
        {
            long result = (long)TargetSystem.Instance()->InteractWithObject((GameObject*)gameObject.Address, false);

            _logger.LogInformation("Interact result: {Result}", result);
            return result != 7 && result > 0;
        }
    }

    public bool UseItem(uint itemId)
    {
        // AgentInventoryContext.Instance() 走 [Agent] 產生器，本體即
        // 「agentModule == null ? null : GetAgentByInternalId(...)」，兩層都能合法回 null，
        // 裸接 ->UseItem() 等於從位址 0 讀 vtable，是攔不到的 AccessViolation。
        // fail-closed：取不到就回 false＝「沒用成道具」，與呼叫端既有的失敗處理一致（會重試）。
        AgentInventoryContext* agentInventoryContext = AgentInventoryContext.Instance();
        if (agentInventoryContext == null)
        {
            _logger.LogWarning("Inventory context agent is unavailable, not using item {ItemId}", itemId);
            return false;
        }

        long result = agentInventoryContext->UseItem(itemId);
        _logger.LogInformation("UseItem result: {Result}", result);

        return result == 0;
    }

    public bool UseItem(uint dataId, uint itemId)
    {
        IGameObject? gameObject = FindObjectByDataId(dataId);
        if (gameObject != null)
        {
            _targetManager.Target = gameObject;
            // 同上：兩層都能合法回 null，裸解參考是攔不到的 AccessViolation。
            // fail-closed：取不到就回 false＝「沒用成道具」，呼叫端會重試。
            AgentInventoryContext* agentInventoryContext = AgentInventoryContext.Instance();
            if (agentInventoryContext == null)
            {
                _logger.LogWarning("Inventory context agent is unavailable, not using item {ItemId} on {DataId}",
                    itemId, dataId);
                return false;
            }

            long result = agentInventoryContext->UseItem(itemId);

            _logger.LogInformation("UseItem result on {DataId}: {Result}", dataId, result);
            return result is 0 or 1;
        }

        return false;
    }

    public bool UseItemOnGround(uint dataId, uint itemId)
    {
        IGameObject? gameObject = FindObjectByDataId(dataId);
        if (gameObject != null)
        {
            Vector3 position = gameObject.Position;
            return ActionManager.Instance()->UseActionLocation(ActionType.KeyItem, itemId, location: &position);
        }

        return false;
    }

    public bool UseItemOnPosition(Vector3 position, uint itemId)
    {
        return ActionManager.Instance()->UseActionLocation(ActionType.KeyItem, itemId, location: &position);
    }

    public bool UseAction(EAction action)
    {
        uint actionId = (uint)action & 0xFFFF;
        ActionType actionType = ((uint)action & 0x10000) == 0x10000 ? ActionType.GeneralAction : ActionType.Action;
        if (actionType == ActionType.Action)
        {
            actionId = ActionManager.Instance()->GetAdjustedActionId(actionId);
        }

        if (ActionManager.Instance()->GetActionStatus(actionType, actionId) == 0)
        {
            bool result = ActionManager.Instance()->UseAction(actionType, actionId);
            _logger.LogInformation("UseAction {Action} (adjusted: {AdjustedActionId}) result: {Result}", action,
                actionId, result);

            return result;
        }

        return false;
    }

    public bool UseAction(IGameObject gameObject, EAction action, bool checkCanUse = true)
    {
        uint actionId = (uint)action & 0xFFFF;
        ActionType actionType = ((uint)action & 0x10000) == 0x10000 ? ActionType.GeneralAction : ActionType.Action;
        if (actionType == ActionType.GeneralAction)
        {
            _logger.LogWarning("Can not use general action {Action} on target {Target}", action, gameObject);
            return false;
        }

        actionId = ActionManager.Instance()->GetAdjustedActionId(actionId);
        if (checkCanUse && !ActionManager.CanUseActionOnTarget(actionId, (GameObject*)gameObject.Address))
        {
            _logger.LogWarning("Can not use action {Action} (adjusted: {AdjustedActionId}) on target {Target}", action,
                actionId, gameObject);
            return false;
        }

        Action actionRow = _dataManager.GetExcelSheet<Action>().GetRow(actionId);
        _targetManager.Target = gameObject;
        if (ActionManager.Instance()->GetActionStatus(actionType, actionId, gameObject.GameObjectId) == 0)
        {
            bool result;
            if (actionRow.TargetArea)
            {
                Vector3 position = gameObject.Position;
                result = ActionManager.Instance()->UseActionLocation(actionType, actionId,
                    location: &position);
                _logger.LogInformation(
                    "UseAction {Action} (adjusted: {AdjustedActionId}) on target area {Target} result: {Result}",
                    action, actionId, gameObject, result);
            }
            else
            {
                result = ActionManager.Instance()->UseAction(actionType, actionId, gameObject.GameObjectId);
                _logger.LogInformation(
                    "UseAction {Action} (adjusted: {AdjustedActionId}) on target {Target} result: {Result}", action,
                    actionId, gameObject, result);
            }

            return result;
        }

        return false;
    }

    public bool IsObjectAtPosition(uint dataId, Vector3 position, float distance)
    {
        IGameObject? gameObject = FindObjectByDataId(dataId);
        return gameObject != null && (gameObject.Position - position).Length() < distance;
    }

    public bool HasStatusPreventingMount()
    {
        if (_condition[ConditionFlag.Swimming] && !IsFlyingUnlockedInCurrentZone())
        {
            return true;
        }

        // company chocobo is locked
        PlayerState* playerState = PlayerState.Instance();
        if (playerState != null && !playerState->IsMountUnlocked(1))
        {
            return true;
        }

        IGameObject? localPlayer = _objectTable[0];
        if (localPlayer == null)
        {
            return false;
        }

        BattleChara* battleChara = (BattleChara*)localPlayer.Address;
        StatusManager* statusManager = battleChara->GetStatusManager();
        if (statusManager->HasStatus(1151) ||
            statusManager->HasStatus(1945)) // hoofing it
        {
            return true;
        }

        return HasCharacterStatusPreventingMountOrSprint();
    }

    public bool HasStatusPreventingSprint()
    {
        return HasCharacterStatusPreventingMountOrSprint();
    }

    private bool HasCharacterStatusPreventingMountOrSprint()
    {
        IGameObject? localPlayer = _objectTable[0];
        if (localPlayer == null)
        {
            return false;
        }

        BattleChara* battleChara = (BattleChara*)localPlayer.Address;
        StatusManager* statusManager = battleChara->GetStatusManager();
        return statusManager->HasStatus(565) ||
               statusManager->HasStatus(404) ||
               statusManager->HasStatus(416) ||
               statusManager->HasStatus(2729) ||
               statusManager->HasStatus(2730);
    }

    public bool HasStatus(EStatus statusId)
    {
        IGameObject? localPlayer = _objectTable[0];
        if (localPlayer == null)
        {
            return false;
        }

        BattleChara* battleChara = (BattleChara*)localPlayer.Address;
        StatusManager* statusManager = battleChara->GetStatusManager();
        return statusManager->HasStatus((uint)statusId);
    }

    public static bool RemoveStatus(EStatus statusId)
    {
        return StatusManager.ExecuteStatusOff((uint)statusId);
    }

    public bool Mount()
    {
        if (_condition[ConditionFlag.Mounted])
        {
            return true;
        }

        PlayerState* playerState = PlayerState.Instance();
        if (playerState != null && _configuration.General.MountId != 0 &&
            playerState->IsMountUnlocked(_configuration.General.MountId))
        {
            if (ActionManager.Instance()->GetActionStatus(ActionType.Mount, _configuration.General.MountId) == 0)
            {
                _logger.LogDebug("Attempting to use preferred mount...");
                if (ActionManager.Instance()->UseAction(ActionType.Mount, _configuration.General.MountId))
                {
                    _logger.LogInformation("Using preferred mount");
                    return true;
                }
            }
        }
        else
        {
            if (ActionManager.Instance()->GetActionStatus(ActionType.GeneralAction, 9) == 0)
            {
                _logger.LogDebug("Attempting to use mount roulette...");
                if (ActionManager.Instance()->UseAction(ActionType.GeneralAction, 9))
                {
                    _logger.LogInformation("Using mount roulette");
                    return true;
                }
            }
        }

        return false;
    }

    public bool Unmount()
    {
        if (!_condition[ConditionFlag.Mounted])
        {
            return true;
        }

        if (ActionManager.Instance()->GetActionStatus(ActionType.GeneralAction, 23) == 0)
        {
            _logger.LogDebug("Attempting to unmount...");
            if (ActionManager.Instance()->UseAction(ActionType.GeneralAction, 23))
            {
                _logger.LogInformation("Unmounted");
                return true;
            }

            return false;
        }
        else
        {
            _logger.LogWarning("Can't unmount right now?");
            return false;
        }
    }

    public void OpenDutyFinder(uint contentFinderConditionId)
    {
        if (_contentFinderConditionToContentId.TryGetValue(contentFinderConditionId, out uint contentId))
        {
            if (UIState.IsInstanceContentUnlocked(contentId))
            {
                // AgentContentsFinder.Instance() 走 [Agent] 產生器，合法回 null，
                // 裸解參考是攔不到的 AccessViolation。
                // fail-closed：取不到就不開任務搜尋器，只記錄——與這個方法既有的
                // 兩個「開不了」分支同一種處理方式。
                AgentContentsFinder* agentContentsFinder = AgentContentsFinder.Instance();
                if (agentContentsFinder != null)
                {
                    agentContentsFinder->OpenRegularDuty(contentFinderConditionId);
                }
                else
                {
                    _logger.LogWarning(
                        "Contents finder agent is unavailable, not opening duty (cf: {ContentFinderId})",
                        contentFinderConditionId);
                }
            }
            else
            {
                _logger.LogError(
                    "Trying to access a locked duty (cf: {ContentFinderId}, content: {ContentId})",
                    contentFinderConditionId, contentId);
            }
        }
        else
        {
            _logger.LogError("Could not find content for content finder condition (cf: {ContentFinderId})",
                contentFinderConditionId);
        }
    }

    /// <summary>
    ///     Ensures characters like '-' are handled equally in both strings.
    /// </summary>
    public static bool GameStringEquals(string? a, string? b)
    {
        if (a == null)
        {
            return b == null;
        }

        if (b == null)
        {
            return false;
        }

        return a.ReplaceLineEndings().Replace('\u2013', '-') == b.ReplaceLineEndings().Replace('\u2013', '-');
    }

    public bool IsOccupied()
    {
        if (!_clientState.IsLoggedIn || _objectTable[0] == null)
        {
            return true;
        }

        if (IsLoadingScreenVisible())
        {
            return true;
        }

        if (_condition[ConditionFlag.Crafting])
        {
            // AgentRecipeNote.Instance() 走 [Agent] 產生器，合法回 null，
            // 裸接 ->IsAgentActive() 是攔不到的 AccessViolation。
            // fail-closed：null 與「代理人未啟用」走同一條路＝回 true（視為忙碌），
            // 讓流程等下一輪，而不是在讀不到狀態時往前推進。
            AgentRecipeNote* agentRecipeNote = AgentRecipeNote.Instance();
            if (agentRecipeNote == null || !agentRecipeNote->IsAgentActive())
            {
                return true;
            }

            if (!_condition[ConditionFlag.PreparingToCraft])
            {
                return true;
            }
        }

        if (_condition[ConditionFlag.Unconscious] &&
            _condition[ConditionFlag.SufferingStatusAffliction63] &&
            _clientState.TerritoryType == SinglePlayerDuty.SpecialTerritories.Lahabrea)
        {
            return false; // needed to process the tasks
        }

        return _condition[ConditionFlag.Occupied] || _condition[ConditionFlag.Occupied30] ||
               _condition[ConditionFlag.Occupied33] || _condition[ConditionFlag.Occupied38] ||
               _condition[ConditionFlag.Occupied39] || _condition[ConditionFlag.OccupiedInEvent] ||
               _condition[ConditionFlag.OccupiedInQuestEvent] || _condition[ConditionFlag.OccupiedInCutSceneEvent] ||
               _condition[ConditionFlag.Casting] || _condition[ConditionFlag.MountOrOrnamentTransition] ||
               _condition[ConditionFlag.BetweenAreas] || _condition[ConditionFlag.BetweenAreas51] ||
               _condition[ConditionFlag.Jumping61] || _condition[ConditionFlag.ExecutingGatheringAction] ||
               _condition[ConditionFlag.Jumping];
    }

    public bool IsOccupiedWithCustomDeliveryNpc(Quest? currentQuest)
    {
        // not a supply quest?
        if (currentQuest is not { Info: SatisfactionSupplyInfo })
        {
            return false;
        }

        if (_targetManager.Target == null || GetBaseID(_targetManager.Target) != currentQuest.Info.IssuerDataId)
        {
            return false;
        }

        // AgentSatisfactionSupply.Instance() 走 [Agent] 產生器，合法回 null，
        // 裸接 ->IsAgentActive() 是攔不到的 AccessViolation。
        // null 時的退化與「代理人未啟用」完全相同＝回 false（沒在跟外送 NPC 對話），
        // 而且那也確實是正確答案：agent 都還沒建立，就不可能正在對話。
        AgentSatisfactionSupply* agentSatisfactionSupply = AgentSatisfactionSupply.Instance();
        if (agentSatisfactionSupply == null || !agentSatisfactionSupply->IsAgentActive())
        {
            return false;
        }

        HashSet<ConditionFlag> flags = _condition.AsReadOnlySet().ToHashSet();
        flags.Remove(ConditionFlag.InDutyQueue); // irrelevant
        return flags.Count == 2 &&
               flags.Contains(ConditionFlag.NormalConditions) &&
               flags.Contains(ConditionFlag.OccupiedInQuestEvent);
    }

    public bool IsLoadingScreenVisible()
    {
        if (_gameGui.TryGetAddonByName("FadeMiddle", out AtkUnitBase* fade) && AddonUtils.IsAddonReady(fade) &&
            fade->IsVisible)
        {
            return true;
        }

        if (_gameGui.TryGetAddonByName("FadeBack", out fade) && AddonUtils.IsAddonReady(fade) && fade->IsVisible)
        {
            return true;
        }

        if (_gameGui.TryGetAddonByName("NowLoading", out fade) && AddonUtils.IsAddonReady(fade) && fade->IsVisible)
        {
            return true;
        }

        return false;
    }

    public static int GetFreeInventorySlots()
    {
        InventoryManager* inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
        {
            return 0;
        }

        int slots = 0;
        for(InventoryType inventoryType = InventoryType.Inventory1;
            inventoryType <= InventoryType.Inventory4;
            ++inventoryType)
        {
            InventoryContainer* inventoryContainer = inventoryManager->GetInventoryContainer(inventoryType);
            if (inventoryContainer == null)
            {
                continue;
            }

            for(int i = 0; i < inventoryContainer->Size; ++i)
            {
                InventoryItem* item = inventoryContainer->GetInventorySlot(i);
                if (item == null || item->ItemId == 0)
                {
                    ++slots;
                }
            }
        }

        return slots;
    }

    public static uint GetBaseID(IGameObject? obj)
    {
        if (obj == null)
        {
            return 0;
        }
        if (obj.GetType().GetProperty("BaseId") is { } baseIdProp)
        {
            return (uint)baseIdProp.GetValue(obj)!;
        }

        if (obj.GetType().GetProperty("DataId") is { } dataIdProp)
        {
            return (uint)dataIdProp.GetValue(obj)!;
        }

        return 0;
    }

    /// <summary>
    ///     Abandons <em>some</em> quest battles/duties; but not all? Useful for debugging some quest battle/vbm related
    ///     issues.
    /// </summary>
    public void AbandonDuty()
    {
        _abandonDuty(false);
    }

    public IReadOnlyList<uint> GetUnlockLinks()
    {
        UIState* uiState = UIState.Instance();
        if (uiState == null)
        {
            _logger.LogError("Could not query unlock links");
            return [];
        }

        List<uint> unlockedUnlockLinks = [];
        Span<byte> unlockLinkBitmask = uiState->UnlockLinkBitmask;
        for(int byteIndex = 0; byteIndex < unlockLinkBitmask.Length; ++byteIndex)
        {
            byte b = unlockLinkBitmask[byteIndex];
            for(int bit = 0; bit < 8; ++bit)
            {
                if ((b & (1 << bit)) != 0)
                {
                    unlockedUnlockLinks.Add((uint)(byteIndex * 8 + bit));
                }
            }
        }

        _logger.LogInformation("Unlocked unlock links: {UnlockedUnlockLinks}", string.Join(", ", unlockedUnlockLinks));
        return unlockedUnlockLinks;
    }
    private delegate void AbandonDutyDelegate(bool a1);
}

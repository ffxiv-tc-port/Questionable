using Dalamud.Configuration;
using Dalamud.Game.Text;
using ECommons.ExcelServices;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Newtonsoft.Json;
using Questionable.Model;
using Questionable.Model.Questing;
using Questionable.Windows.Common;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using GrandCompany = FFXIVClientStructs.FFXIV.Client.UI.Agent.GrandCompany;
namespace Questionable;

internal sealed class Configuration : IPluginConfiguration
{
    public const int PluginSetupVersion = 5;
    public int PluginSetupCompleteVersion { get; set; }
    public GeneralConfiguration General { get; } = new();
    public StopConfiguration Stop { get; } = new();
    public DutyConfiguration Duties { get; } = new();
    public SinglePlayerDutyConfiguration SinglePlayerDuties { get; } = new();
    public NotificationConfiguration Notifications { get; } = new();
    public AdvancedConfiguration Advanced { get; } = new();
    public WindowConfig DebugWindowConfig { get; } = new();
    public WindowConfig ConfigWindowConfig { get; } = new();

    public int Version { get; set; } = 1;

    internal bool IsPluginSetupComplete()
    {
        return PluginSetupCompleteVersion == PluginSetupVersion;
    }

    internal void MarkPluginSetupComplete()
    {
        PluginSetupCompleteVersion = PluginSetupVersion;
    }

    internal sealed class GeneralConfiguration
    {
        public ECombatModule CombatModule { get; set; } = ECombatModule.None;
        public uint MountId { get; set; } = 71;
        public GrandCompany GrandCompany { get; set; } = GrandCompany.None;
        public Job CombatJob { get; set; } = Job.ADV;
        public Job CraftingJob { get; set; } = Job.CRP;
        public Job GatheringJob { get; set; } = Job.MIN;
        public EGearsetUpdateSource GearsetUpdateSource { get; set; } = EGearsetUpdateSource.Vanilla;
        public bool HideInAllInstances { get; set; } = true;
        public bool UseEscToCancelQuesting { get; set; } = true;
        public bool ShowIncompleteSeasonalEvents { get; set; } = true;
        public bool SkipLowPriorityDuties { get; set; }
        public bool ConfigureTextAdvance { get; set; } = true;
        public bool AutoMode { get; set; }
        public bool DontSkipCutscenes { get; set; }
        public bool AutoStepRefreshEnabled { get; set; }
        public int AutoStepRefreshDelaySeconds { get; set; } = 30;
        public bool NudgePositionOnAetheryteAttuneStuck { get; set; } = true;
        public float MovementStuckGraceSeconds { get; set; } = 2.5f;
        public float InteractionRangeStopDistance { get; set; } = 7f;
        public bool UseTickets { get; set; }
        public bool HideSponsorButton { get; set; }
        public bool DismissedReportWarning { get; set; }
        public bool ReportsDisabled { get; set; }
        public string ReportMessage { get; set; } = "";

        /// <summary>
        /// 按「開始」（<c>QuestController.Start</c>）時，把這些部族今天可接的友好部族日常排進優先清單並依序做完。
        /// 空集合（預設）＝關，與舊版行為相同。
        /// </summary>
        public HashSet<EAlliedSociety> AutoQueueAlliedSocietyDailies { get; set; } = [];
    }

    internal sealed class StopConfiguration
    {
        public bool Enabled { get; set; }

        [JsonProperty(ItemConverterType = typeof(ElementIdNConverter))]
        public List<ElementId> QuestsToStopAfter { get; set; } = [];

        public bool LevelToStopAfter { get; set; }
        public int TargetLevel { get; set; } = 50;
    }

    internal sealed class DutyConfiguration
    {
        public bool RunInstancedContentWithAutoDuty { get; set; }
        public HashSet<uint> WhitelistedDutyCfcIds { get; set; } = [];
        public HashSet<uint> BlacklistedDutyCfcIds { get; set; } = [];
        public Dictionary<string, bool> ExpansionHeaderStates { get; set; } = [];
    }

    internal sealed class SinglePlayerDutyConfiguration
    {
        public bool RunSoloInstancesWithBossMod { get; set; }

        [SuppressMessage("Performance", "CA1822", Justification = "Will be fixed when no longer WIP")]
        public byte RetryDifficulty => 0;

        /// <summary>
        /// 任務戰鬥失敗（離開副本後任務進度沒有前進、或跳出重試難度框）時停下自動任務並求助，不自動重試。
        /// </summary>
        /// <remarks>舊設定檔沒有這個鍵時沿用這裡的初始值（開）。</remarks>
        public bool StopOnFailure { get; set; } = true;

        public HashSet<uint> WhitelistedSinglePlayerDutyCfcIds { get; set; } = [];
        public HashSet<uint> BlacklistedSinglePlayerDutyCfcIds { get; set; } = [];
        public Dictionary<string, bool> HeaderStates { get; set; } = [];
    }

    internal sealed class NotificationConfiguration
    {
        public bool Enabled { get; set; } = true;
        public XivChatType ChatType { get; set; } = XivChatType.Debug;
        public bool ShowTrayMessage { get; set; }
        public bool FlashTaskbar { get; set; }

        /// <summary>
        /// 自動任務卡住／走到需要人工的步驟時，請 TataruPraise 用語音喊一句「需要幫忙」。
        /// </summary>
        /// <remarks>
        /// 📌 預設開著：沒裝 TataruPraise 的人完全感覺不到（IPC 沒有人註冊，記錄檔只會留一行 Information），
        /// 裝了的人不用再去翻設定才會生效。<b>刻意不受上面 <see cref="Enabled"/> 那個總開關管</b>——
        /// 那個管的是「走到手動步驟時要不要印聊天訊息」，而這裡連「因為錯誤／卡住而停下來」也算。
        /// </remarks>
        public bool PraiseWithTataru { get; set; } = true;
    }

    internal sealed class AdvancedConfiguration
    {
        public bool DebugOverlay { get; set; }
        public bool CombatDataOverlay { get; set; }
        public bool HighlightSelectedNpc { get; set; } = true;
        public ObjectHighlightColor HighlightColor { get; set; } = ObjectHighlightColor.Yellow;
        public bool NeverFly { get; set; }
        public bool AdditionalStatusInformation { get; set; }
        public bool ShowTracked { get; set; }
        public bool ShowDailies { get; set; }
        public bool ShowDirector { get; set; }
        public bool ShowActionManager { get; set; }
        public bool ShowNewGamePlus { get; set; }
        public bool DisableAutoDutyBareMode { get; set; }
        public bool SkipAetherCurrents { get; set; }

        /// <summary>任務途中順路解鎖陸行鳥運輸站。關閉時略過整個解鎖步驟（不走過去、不對話）。</summary>
        public bool UnlockChocoboTaxiStands { get; set; } = true;

        public bool SkipClassJobQuests { get; set; }
        public bool SkipARealmRebornHardModePrimals { get; set; }
        public bool SkipCrystalTowerRaids { get; set; }

        /// <summary>
        /// 暫停主線：不再依主線導覽自動取下一個主線任務。預設關＝現行行為。
        /// 任務清單中追蹤的任務、優先任務與手動指定的任務不受影響。
        /// </summary>
        public bool StopMSQ { get; set; }

        public bool PreventQuestCompletion { get; set; }
        public bool ShowWindowOnStart { get; set; }
        public bool StartMinimized { get; set; }
        public bool OpenEditor { get; set; }
        public bool NamazuPreferCraft { get; set; }

        /// <summary>
        /// 接任務時自動開啟背包裡的任務獎勵寶箱。
        /// </summary>
        /// <remarks>
        /// 🔴 <b>預設關是刻意的</b>：寶箱沒有「已解鎖」的概念（<c>CofferReward.IsUnlocked()</c> 恆為 false），
        /// 開了就分辨不出哪一箱是使用者想留的；要留著不開的用 <see cref="AutoRedeemItemBlacklist"/> 排除。
        /// </remarks>
        public bool AutoRedeemCoffers { get; set; }

        /// <summary>
        /// 不自動使用的道具 id。對所有可兌換道具生效，不只是寶箱。
        /// 空集合（預設）等於現行行為：什麼都不排除。
        /// </summary>
        public HashSet<uint> AutoRedeemItemBlacklist { get; set; } = [];
    }

    internal enum EGearsetUpdateSource
    {
        Vanilla,
        Stylist
    }

    internal enum ECombatModule
    {
        None,
        BossMod,
        RotationSolverReborn
    }

    public sealed class ElementIdNConverter : JsonConverter<ElementId>
    {
        public override void WriteJson(JsonWriter writer, ElementId? value, JsonSerializer serializer)
        {
            writer.WriteValue(value?.ToString());
        }

        public override ElementId? ReadJson(JsonReader reader, Type objectType, ElementId? existingValue,
            bool hasExistingValue, JsonSerializer serializer)
        {
            string? value = reader.Value?.ToString();
            return value != null ? ElementId.FromString(value) : null;
        }
    }
}

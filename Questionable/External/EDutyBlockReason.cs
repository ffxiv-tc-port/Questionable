namespace Questionable.External;

/// <summary>
/// 為什麼這一場副本／任務戰鬥不會自動打。
/// <para>
/// 契約：<see cref="None"/> 是唯一代表「沒有被擋」的值，也是列舉的零值 —— 其餘每一個值
/// 都對應到一個明確的擋下點，拿去給使用者看時必須說得出是哪一條。
/// </para>
/// </summary>
internal enum EDutyBlockReason
{
    None = 0,

    /// <summary>叫不到負責打的那個外掛（BossMod／AutoDuty）。</summary>
    PluginUnavailable,

    /// <summary>Questionable 設定裡的總開關沒開。</summary>
    DisabledInConfig,

    /// <summary>這一步沒有副本資料，或查不到對應的 ContentFinderCondition。</summary>
    NoDutyData,

    /// <summary>使用者把這一場設成停用。</summary>
    Blacklisted,

    /// <summary>這一場預設沒有開啟（這是唯一可以一鍵啟用的原因）。</summary>
    NotEnabledByDefault,

    /// <summary>AutoDuty 沒有這張地圖的路徑資料。</summary>
    NoPath,
}

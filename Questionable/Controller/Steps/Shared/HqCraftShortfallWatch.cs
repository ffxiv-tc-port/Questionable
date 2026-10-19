namespace Questionable.Controller.Steps.Shared;

/// <summary>
/// 要求高品質的製作步驟：Artisan 收工後高品質成品仍不夠時，提示一次（不改流程）。
/// 每個製作工作一個實例；在框架執行緒上每幀呼叫 <see cref="Update"/>。
/// </summary>
internal sealed class HqCraftShortfallWatch
{
    /// <summary>Artisan 停下來之後要持續不夠這麼久才提示；收工那一刻背包數量可能還沒更新。</summary>
    public const long SettleMilliseconds = 5_000;

    private bool _sawCrafting;
    private long? _shortSinceMs;
    private bool _notified;

    /// <summary>
    /// 回傳 true＝這一刻該提示（同一段不夠的期間只回一次）。
    /// 沒看過 Artisan 進入製作中就不判：配方還沒選好、或 Artisan 自己拒絕時它會自己報錯。
    /// Artisan 再次製作、或高品質數量補夠，就算條件解除，之後可以再提示。
    /// </summary>
    public bool Update(bool requiresHq, bool isCrafting, int hqOwned, int required, long nowMs)
    {
        if (!requiresHq)
            return false;

        if (isCrafting)
        {
            _sawCrafting = true;
            _shortSinceMs = null;
            _notified = false;
            return false;
        }

        if (!_sawCrafting || hqOwned >= required)
        {
            _shortSinceMs = null;
            _notified = false;
            return false;
        }

        _shortSinceMs ??= nowMs;
        if (_notified || nowMs - _shortSinceMs.Value < SettleMilliseconds)
            return false;

        _notified = true;
        return true;
    }
}

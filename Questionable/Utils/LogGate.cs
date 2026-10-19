using Serilog.Events;

namespace Questionable.Utils;

/// <summary>
///     Dalamud 根層記錄等級的閘門：每幀路徑上的 <c>LogTrace</c>／<c>LogDebug</c> 先判這裡，
///     關著時連格式化與參數裝箱都不做。每次即時讀，不可快取（使用者會在遊戲裡切等級）。
/// </summary>
/// <remarks>
///     不能改用 <c>ILogger.IsEnabled</c>：ECommons 把插件層設成 Verbose，它恆真，擋不住被根層丟掉的記錄。
/// </remarks>
internal static class LogGate
{
    public static bool TraceEnabled =>
        global::Dalamud.EntryPoint.LogLevelSwitch.MinimumLevel <= LogEventLevel.Verbose;

    public static bool DebugEnabled =>
        global::Dalamud.EntryPoint.LogLevelSwitch.MinimumLevel <= LogEventLevel.Debug;
}

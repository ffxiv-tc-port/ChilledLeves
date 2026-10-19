using Serilog.Events;

namespace ChilledLeves;

/// <summary>
///     Dalamud 根層記錄等級的閘門：排程與任務每幀（或每 10 幀）重跑的 <c>PluginVerbos</c>／<c>PluginDebug</c> 先判這裡，
///     關著時連字串內插都不做。每次即時讀，不可快取（使用者會在遊戲裡切等級）。
/// </summary>
/// <remarks>
///     不能改用 <c>Svc.Log.MinimumLogLevel</c>：ECommons 把插件層設成 Verbose，它恆真。
/// </remarks>
internal static class LogGate
{
    public static bool VerboseEnabled
        => global::Dalamud.EntryPoint.LogLevelSwitch.MinimumLevel <= LogEventLevel.Verbose;

    public static bool DebugEnabled
        => global::Dalamud.EntryPoint.LogLevelSwitch.MinimumLevel <= LogEventLevel.Debug;
}

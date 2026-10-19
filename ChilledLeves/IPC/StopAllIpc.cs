using System.Threading;
using ChilledLeves.Scheduler;
using ChilledLeves.Scheduler.Handlers;
using Dalamud.Plugin.Ipc;

namespace ChilledLeves.IPC;

/// <summary>急停端點 <c>ChilledLeves.StopAll</c>（Action&lt;string&gt;，參數是呼叫端署名，寫進記錄）。</summary>
/// <remarks>任何執行緒都可以呼叫、不會擲例外給呼叫端：在 framework 執行緒上當場停；其他執行緒只排隊，下一幀由 <see cref="ProcessPending"/> 停。
/// 停止走與「停止」按鈕、<c>/chilledleves stop</c> 相同的 <see cref="SchedulerMain.DisablePlugin"/>，不改、不存設定。
/// 刻意只提供停止、不提供開始：開始一律要使用者自己按。</remarks>
internal sealed class StopAllIpc : IDisposable
{
    internal const string EndpointName = "ChilledLeves.StopAll";

    private readonly ICallGateProvider<string, object> provider;

    // 其他執行緒排進來的停止要求（呼叫端署名）；null＝沒有。
    private string? pending;

    private volatile bool disposed;

    internal StopAllIpc()
    {
        provider = Svc.PluginInterface.GetIpcProvider<string, object>(EndpointName);
        provider.RegisterAction(Request);
    }

    private void Request(string? source)
    {
        if (disposed)
            return;

        var label = string.IsNullOrWhiteSpace(source) ? "?" : source.Trim();
        if (Svc.Framework.IsInFrameworkUpdateThread)
        {
            StopNow(label);
            return;
        }

        Interlocked.Exchange(ref pending, label);
    }

    /// <summary>只在 framework 執行緒呼叫。</summary>
    internal void ProcessPending()
    {
        var label = Interlocked.Exchange(ref pending, null);
        if (label != null && !disposed)
            StopNow(label);
    }

    private static void StopNow(string label)
    {
        try
        {
            var ticking = SchedulerMain.AreWeTicking;
            var mode = SchedulerMain.WorkListMode ? "工作清單" : SchedulerMain.GatheringMode ? "採集模式" : "無";
            var queued = P.taskManager?.NumQueuedTasks ?? 0;
            // 交納視窗逐格點選用的是另一個佇列：DisablePlugin 也會清，但沒在跑時不呼叫它，所以下面另外清。
            var clicks = GenericManager.taskManager.NumQueuedTasks;

            if (!ticking && queued == 0 && clicks == 0)
            {
                Svc.Log.Information($"[ChilledLeves] 收到急停（{label}）：原本就沒在跑，未動作。");
                return;
            }

            // 沒在跑時不呼叫 DisablePlugin：它會無條件停 vnavmesh，那時的導航是別人的。
            var disabled = ticking || queued > 0;
            if (disabled)
                SchedulerMain.DisablePlugin();
            if (clicks > 0)
                GenericManager.taskManager.Abort();

            var nav = disabled ? "，並停下 vnavmesh 導航" : "";
            Svc.Log.Information($"[ChilledLeves] 收到急停（{label}）：已停止理符自動化（原本執行中＝{ticking}、模式＝{mode}、任務佇列 {queued} 項、交納點選佇列 {clicks} 項）{nav}。");
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"[ChilledLeves] 急停（{label}）時發生例外");
        }
    }

    public void Dispose()
    {
        disposed = true;
        provider.UnregisterAction();
    }
}

using ECommons.Automation.NeoTaskManager;

namespace ChilledLeves.Scheduler.Handlers;

/// <summary>NPC 互動後那幾步（接理符、離開委託人、交納）逾時就停掉整個理符自動化。</summary>
/// <remarks>只在 framework 執行緒使用。逾時事件裡只記下步驟名：在事件裡 Abort 會讓 NeoTaskManager 接著讀已清空的
/// <c>CurrentTask</c>。佇列由 <c>abortOnTimeout</c> 清掉，下一次 <see cref="ProcessPending"/> 走與停止按鈕、
/// <c>ChilledLeves.StopAll</c> 相同的 <see cref="SchedulerMain.DisablePlugin"/>。</remarks>
internal static class StepTimeoutStop
{
    internal const int LimitMS = 2 * 60 * 1000;

    private static string? pending;

    internal static void OnTimeout(TaskManagerTask task, ref long remainingTimeMS) => pending = task.Name;

    internal static void ProcessPending()
    {
        var step = pending;
        if (step == null)
            return;
        pending = null;

        // 急停或停止按鈕已經先停過，就不再呼叫 DisablePlugin（它會無條件停 vnavmesh）。
        if (!SchedulerMain.AreWeTicking)
        {
            Svc.Log.Information($"[ChilledLeves] 步驟「{step}」逾時，但理符自動化已經停止，未動作。");
            return;
        }

        SchedulerMain.DisablePlugin();
        Svc.Log.Information($"[ChilledLeves] 步驟「{step}」超過 {LimitMS / 1000} 秒沒有完成，已停止理符自動化。");
        Svc.Chat.Print("[Chilled Leves] A step after talking to the NPC did not finish within 2 minutes, so the leve run was stopped. Check the NPC or hand-in window, then press Start again.".Loc());
    }
}

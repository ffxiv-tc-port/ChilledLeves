namespace ChilledLeves.Scheduler.Tasks
{
    internal static class TaskInteract
    {
        public static void Enqueue(ulong dataID)
        {
            // 閉包只捕獲 GameObjectId,不要捕獲 IGameObject:下面每個任務都是在
            // 後續的幀才執行,而 IGameObject 是物件表共用的包裝,跨幀持有會靜默換人或懸空。
            // 目標在這期間消失(NPC 離場、換區、任務結束)時,沿用舊參考就是
            // 攔不到的 AccessViolation(corrupted-state exception,try/catch 無效)。
            ulong? objectId = null;
            P.taskManager.Enqueue(PlayerNotBusy, "Waiting for player to not be busy");
            P.taskManager.Enqueue(() => TryGetObjectIdByDataId(dataID, out objectId), "Getting Objec by DataId");
            P.taskManager.Enqueue(() => PluginVerbos($"Data ID of the target is: {dataID}"), "Plugin Verbose");
            P.taskManager.Enqueue(() => PluginVerbos($"Interacting w/ {ResolveObject(objectId)?.Name}"), "Plugin Verbose");
            var attempt = new InteractAttempt();
            P.taskManager.Enqueue(() => InteractUntilEngaged(objectId, dataID, attempt), "Interacting with Object");
            P.taskManager.Enqueue(() => PluginVerbos("Interacted w/ target now"));
        }

        private const long RetryIntervalMs = 1500;
        private const long GiveUpMs = 10_000;

        private sealed class InteractAttempt
        {
            public long StartedAt;
            public long LastSentAt;
        }

        /// <summary>
        /// 互動直到看到「進入對話／選單」（Occupied 或對話類視窗出現）才回 true；沒反應就每 1.5 秒補一次。
        /// 10 秒仍沒反應寫一行 Information 後照舊往下走（與原本「送一次就往下」相同的退路）。
        /// </summary>
        private static bool? InteractUntilEngaged(ulong? objectId, ulong dataID, InteractAttempt attempt)
        {
            if (IsOccupied() || IsAddonActive("Talk") || IsAddonActive("SelectString") || IsAddonActive("SelectIconString") || IsAddonActive("TelepotTown"))
                return true;

            var now = Environment.TickCount64;
            if (attempt.StartedAt == 0)
                attempt.StartedAt = now;
            else if (now - attempt.StartedAt >= GiveUpMs)
            {
                PluginInfo($"[ChilledLeves] 與目標（DataId {dataID}）互動 {GiveUpMs / 1000} 秒都沒有反應（距離不夠或目標暫時無法選取），照原流程往下走。");
                return true;
            }

            if (attempt.LastSentAt == 0 || now - attempt.LastSentAt >= RetryIntervalMs)
            {
                attempt.LastSentAt = now;
                InteractWithObject(ResolveObject(objectId));
            }
            return false;
        }
    }
}

namespace ChilledLeves.Scheduler.Tasks
{
    internal static class TaskInteract
    {
        public static void Enqueue(ulong dataID)
        {
            // 閉包只捕獲 GameObjectId,不要捕獲 IGameObject:下面每個任務都是在
            // 後續的幀才執行,而 IGameObject.Address 是建構時凍結的原生指標。
            // 目標在這期間消失(NPC 離場、換區、任務結束)時,沿用舊參考就是
            // 攔不到的 AccessViolation(corrupted-state exception,try/catch 無效)。
            ulong? objectId = null;
            P.taskManager.Enqueue(PlayerNotBusy, "Waiting for player to not be busy");
            P.taskManager.Enqueue(() => TryGetObjectIdByDataId(dataID, out objectId), "Getting Objec by DataId");
            P.taskManager.Enqueue(() => PluginVerbos($"Data ID of the target is: {dataID}"), "Plugin Verbose");
            P.taskManager.Enqueue(() => PluginVerbos($"Interacting w/ {ResolveObject(objectId)?.Name}"), "Plugin Verbose");
            P.taskManager.Enqueue(() => InteractWithObject(ResolveObject(objectId)), "Interacting with Object");
            P.taskManager.Enqueue(() => PluginVerbos("Interacted w/ target now"));
        }
    }
}

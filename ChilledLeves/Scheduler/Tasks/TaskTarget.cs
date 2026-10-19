namespace ChilledLeves.Scheduler.Tasks
{
    internal static class TaskTarget
    {
        public static void Enqueue(ulong objectID)
        {
            Svc.Log.Debug($"Targeting {objectID}");
            // 閉包只捕獲 GameObjectId,不要捕獲 IGameObject:跨幀持有的包裝可能已換人或懸空,
            // 交給 Svc.Targets.SetTarget 就是把可能已釋放的指標「寫進」遊戲的目標系統,比讀取更糟。
            ulong? resolvedId = null;
            P.taskManager.Enqueue(() => TryGetObjectIdByDataId(objectID, out resolvedId), "Getting Object");
            P.taskManager.Enqueue(() => PluginVerbos($"Targeting By ID. Target is: {ResolveObject(resolvedId)?.BaseId}"), "Plugin Verbos");
            P.taskManager.Enqueue(() => TargetgameObject(ResolveObject(resolvedId)), "Targeting Object");
        }
    }
}

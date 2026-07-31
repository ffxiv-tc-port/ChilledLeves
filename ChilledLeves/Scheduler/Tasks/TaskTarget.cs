namespace ChilledLeves.Scheduler.Tasks
{
    internal static class TaskTarget
    {
        public static void Enqueue(ulong objectID)
        {
            Svc.Log.Debug($"Targeting {objectID}");
            // 閉包只捕獲 GameObjectId,不要捕獲 IGameObject。原本 TargetgameObject
            // 會在後續的幀把已凍結的位址交給 Svc.Targets.SetTarget——那是把可能
            // 已釋放的指標「寫進」遊戲的目標系統,比讀取更糟。
            ulong? resolvedId = null;
            P.taskManager.Enqueue(() => TryGetObjectIdByDataId(objectID, out resolvedId), "Getting Object");
            P.taskManager.Enqueue(() => PluginVerbos($"Targeting By ID. Target is: {ResolveObject(resolvedId)?.DataId}"), "Plugin Verbos");
            P.taskManager.Enqueue(() => TargetgameObject(ResolveObject(resolvedId)), "Targeting Object");
        }
    }
}

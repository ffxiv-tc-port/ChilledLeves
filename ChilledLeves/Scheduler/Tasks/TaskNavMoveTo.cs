using ChilledLeves.Scheduler.Handlers;
using ECommons.Automation.NeoTaskManager;
using ECommons.GameHelpers;

namespace ChilledLeves.Scheduler.Tasks
{
    internal static class TaskMoveTo
    {
        /// <summary>ChilledLeves 自己送的尋路可能還在算（送出後還沒看到它算完）。只在框架執行緒讀寫。</summary>
        internal static bool IssuedPathfind;

        internal unsafe static void Enqueue(Vector3 targetPosition, string destination, bool fly, float toleranceDistance = 3f)
        {
            P.taskManager.Enqueue(() => MoveTo(targetPosition, fly, toleranceDistance), destination, configuration: DConfig);
        }
        internal unsafe static bool? MoveTo(Vector3 targetPosition, bool fly, float toleranceDistance = 3f)
        {
            // 算完就不再是「自己的尋路」：之後的進行中可能是別的插件送的，停止時不能拿它當理由全域取消。
            if (IssuedPathfind && !P.navmesh.PathfindInProgress()) IssuedPathfind = false;
            if (targetPosition.Distance(Player.GameObject->Position) <= toleranceDistance)
            {
                P.navmesh.Stop();
                return true;
            }
            if (P.navmesh.PathfindInProgress() || P.navmesh.IsRunning() || PlayerHandlers.IsMoving()) return false;

            IssuedPathfind = true;
            P.navmesh.PathfindAndMoveTo(targetPosition, fly);
            // 🔴 不要直接寫 vnavmesh 的全域開關:那是無主的,寫下去沒有人負責還。
            //    走 AlignCameraManager 才會在排程停止/外掛卸載時還原成使用者原本的設定。
            AlignCameraManager.SuppressNow();
            return false;
        }
        private static TaskManagerConfiguration DConfig => new(timeLimitMS: 10 * 60 * 1000, abortOnTimeout: false);
    }
}

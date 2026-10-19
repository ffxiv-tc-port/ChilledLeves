using ECommons.EzSharedDataManager;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ChilledLeves.Scheduler.Handlers
{
    /// <summary>
    /// 排程執行期間請 YesAlready 暫停；持有期間每幀補回（它的看門狗逾時會移除持有者）。只在框架執行緒呼叫。
    /// 集合參考由 EzSharedData 快取：本插件的登記撐著條目，YesAlready 重載後拿到的仍是同一個集合。
    /// </summary>
    internal static class YesAlreadyManager
    {
        private static bool WasChanged = false;
        private static bool ReAddLogged = false;
        internal static void Tick()
        {
            if (WasChanged)
            {
                if (!SchedulerMain.AreWeTicking)
                {
                    WasChanged = false;
                    Unlock();
                    PluginDebug($"YesAlready unlocked");
                }
                else if (Lock() && !ReAddLogged)
                {
                    ReAddLogged = true;
                    Svc.Log.Information("[ChilledLeves] YesAlready 的暫停名單裡沒有本插件了（逾時移除或被清空），已補回。");
                }
            }
            else
            {
                if (SchedulerMain.AreWeTicking)
                {
                    WasChanged = true;
                    ReAddLogged = false;
                    Lock();
                    PluginDebug($"YesAlready locked");
                }
            }
        }

        /// <returns>這一次真的把名字加進去了（原本不在名單上）。</returns>
        internal static bool Lock()
        {
            if (!EzSharedData.TryGet<HashSet<string>>("YesAlready.StopRequests", out var data))
                return false;
            try
            {
                return data.Add(P.Name);
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        internal static void Unlock()
        {
            if (EzSharedData.TryGet<HashSet<string>>("YesAlready.StopRequests", out var data))
            {
                data.Remove(P.Name);
            }
        }
    }
}

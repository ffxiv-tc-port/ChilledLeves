using ECommons.EzSharedDataManager;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ChilledLeves.Scheduler.Handlers
{
    /// <summary>
    /// 排程執行期間請 TextAdvance 暫停；持有期間每幀補回（它重新載入會清空名單）。只在框架執行緒呼叫。
    /// 集合參考由 EzSharedData 快取：本插件的登記撐著條目，TextAdvance 重載後拿到的仍是同一個集合。
    /// </summary>
    internal static class TextAdvancedManager
    {
        private static bool WasChanged = false;
        private static bool ReAddLogged = false;
        private static DateTime AddedInFrame = DateTime.MaxValue;
        internal static void Tick()
        {
            if (WasChanged)
            {
                if (!SchedulerMain.AreWeTicking)
                {
                    WasChanged = false;
                    UnlockTA();
                    PluginDebug($"TextAdvance unlocked");
                }
                else if (LockTA() && !ReAddLogged)
                {
                    ReAddLogged = true;
                    Svc.Log.Information("[ChilledLeves] TextAdvance 的暫停名單裡沒有本插件了（它可能重新載入過），已補回。");
                }
            }
            else
            {
                if (SchedulerMain.AreWeTicking)
                {
                    WasChanged = true;
                    ReAddLogged = false;
                    LockTA();
                    AddedInFrame = Svc.Framework.LastUpdateUTC;
                    PluginDebug($"TextAdvance locked");
                }
            }
        }

        /// <summary>沒在暫停＝true；暫停中則要等名字在名單上滿一整幀（TextAdvance 同一幀稍早可能已經按過）。</summary>
        internal static bool MayClickTalk => !WasChanged || Svc.Framework.LastUpdateUTC > AddedInFrame;

        /// <returns>這一次真的把名字加進去了（原本不在名單上）。</returns>
        internal static bool LockTA()
        {
            if (!EzSharedData.TryGet<HashSet<string>>("TextAdvance.StopRequests", out var data))
                return false;
            try
            {
                if (!data.Add(P.Name))
                    return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            AddedInFrame = Svc.Framework.LastUpdateUTC;
            return true;
        }

        internal static void UnlockTA()
        {
            if (EzSharedData.TryGet<HashSet<string>>("TextAdvance.StopRequests", out var data))
            {
                data.Remove(P.Name);
            }
        }
    }
}


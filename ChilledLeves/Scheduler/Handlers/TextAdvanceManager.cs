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
        private const string StopRequestsKey = "TextAdvance.StopRequests";
        /// <summary>名字最近一次加進名單的幀（<see cref="AddonPressGuard.FrameCount"/>）。</summary>
        private static long AddedFrame;
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
                    AddedFrame = AddonPressGuard.FrameCount;
                    PluginDebug($"TextAdvance locked");
                }
            }
        }

        /// <summary>沒在暫停＝true。暫停中：名單在、名字卻不在＝false（這一幀稍後 <see cref="Tick"/> 會補回）；
        /// 補回之後要隔滿一整幀才放行（補回那一幀 TextAdvance 可能已經按過，下一幀它才一定看得到名字）。</summary>
        /// <remarks>名字要在點擊當下查：本插件的任務佇列比 <see cref="Tick"/> 早跑，只靠 Tick 補回會晚一幀才知道名字掉了。</remarks>
        internal static bool MayClickTalk
        {
            get
            {
                if (!WasChanged) return true;
                if (EzSharedData.TryGet<HashSet<string>>(StopRequestsKey, out var data) && !data.Contains(P.Name)) return false;
                // 時鐘沒跑的話這裡永遠不放行（TryBeginPress 同一個理由）。
                AddonPressGuard.EnsureClockRunning();
                return AddonPressGuard.FrameCount > AddedFrame + 1;
            }
        }

        /// <returns>這一次真的把名字加進去了（原本不在名單上）。</returns>
        internal static bool LockTA()
        {
            if (!EzSharedData.TryGet<HashSet<string>>(StopRequestsKey, out var data))
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
            AddedFrame = AddonPressGuard.FrameCount;
            return true;
        }

        internal static void UnlockTA()
        {
            if (EzSharedData.TryGet<HashSet<string>>(StopRequestsKey, out var data))
            {
                data.Remove(P.Name);
            }
        }
    }
}


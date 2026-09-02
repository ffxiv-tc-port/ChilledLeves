using ECommons.Automation;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Callback = ECommons.Automation.Callback;

namespace ChilledLeves.Scheduler.Handlers
{
    internal class GenericHandlers
    {
        internal static bool? Throttle(string name, int ms)
        {
            return EzThrottler.Throttle(name, ms);
        }

        internal static bool? WaitFor(string name)
        {
            return EzThrottler.Check(name);
        }

        /// <remarks>
        /// 🔴 送出前一定要過 <see cref="AddonPressGuard.TryBeginPress"/>：確認框被按下之後有
        /// 「正在關閉中」的幾幀，這期間 <c>TryGetAddonByName</c> 仍拿得到實例、
        /// <c>IsAddonReady</c> 三關也全過（<b>所以上面那兩個判斷不是防護</b>），
        /// 再送一次 callback 就是原生 AccessViolation，<c>try</c>/<c>catch</c> 攔不到。
        /// 防護下沉在這裡而不是各呼叫端，是因為所有具名的 callback 呼叫點共用這一支，
        /// 漏掉任何一個都等於沒防護。詳見 <see cref="AddonPressGuard"/>。
        ///
        /// 被閘門擋下時回 <see langword="false"/>，與「addon 還沒就緒」那條路徑同一個語意
        /// （這一輪沒做成、下個 tick 再試），<b>不是</b> <see langword="null"/> ——
        /// <c>NeoTaskManager</c> 的 <c>bool?</c> 是三態，<c>null</c> 會清掉整條佇列。
        /// </remarks>
        internal static unsafe bool? FireCallback(string AddonName, bool visibilty, params int[] callback_fires)
        {
            if (TryGetAddonByName<AtkUnitBase>(AddonName, out var addon) && IsAddonReady(addon))
            {
                // 走 TryBeginCallback 而不是 TryBeginPress：粒度與逃生口長度由守衛依
                // addon 名稱判定（「按一次翻一頁」的窗用短逃生口，其餘一律「整扇窗只准按一次」）。
                if (!AddonPressGuard.TryBeginCallback(AddonName, addon)) return false;
                Callback.Fire(addon, visibilty, callback_fires.Cast<object>().ToArray());
                return true;
            }
            return false;
        }
    }
}


using ECommons.Automation;
using ECommons.UIHelpers.AddonMasterImplementations;
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
        /// 🔴 送出前一定要過 <see cref="AddonPressGuard.TryBeginPress"/>：確認框被按下之後有「正在關閉中」的幾幀，
        /// 這期間 <c>TryGetAddonByName</c> 與 <c>IsAddonReady</c> 都照過（<b>本方法開頭那兩個判斷不是防護</b>），
        /// 再送一次 callback 就是原生 AccessViolation，<c>try</c>/<c>catch</c> 攔不到。
        /// 防護下沉在這裡而不是各呼叫端：所有具名的 callback 呼叫點共用這一支，漏一個就等於沒防護。詳見 <see cref="AddonPressGuard"/>。
        /// 被閘門擋下時回 <see langword="false"/>（這一輪沒做成、下個 tick 再試），<b>不是</b> <see langword="null"/> ——
        /// <c>NeoTaskManager</c> 的 <c>bool?</c> 是三態，<c>null</c> 會清掉整條佇列。
        /// </remarks>
        internal static unsafe bool? FireCallback(string AddonName, bool visibilty, params int[] callback_fires)
        {
            // TextAdvance 的暫停剛加上／補回的那一幀不點 Talk（它同一幀稍早可能已經點過）。
            if (AddonName == "Talk" && !TextAdvancedManager.MayClickTalk)
                return false;
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

        private static readonly System.Collections.Generic.HashSet<(uint Npc, string Button, int Index, string Text)> loggedNpcMenuPresses = [];

        /// <summary>
        /// 按理符委託人選單（SelectString）的寫死索引：點選照舊走 <see cref="FireCallback"/>，只在按下前讀當下那一格的文字，
        /// 真的送出才記一行 Information（同 NPC／按鈕／索引／文字只記一次），讓 log 能對帳 Data.cs 的按鈕索引。
        /// 只在框架執行緒、當下這一幀讀，不保存任何原生指標。
        /// </summary>
        internal static unsafe bool? FireNpcMenu(uint npcId, string button, int index)
        {
            var text = "?";
            var count = -1;
            // 讀文字失敗不能擋住點選：例外只換成標記文字。
            try
            {
                if (TryGetAddonByName<AtkUnitBase>("SelectString", out var addon) && IsAddonReady(addon))
                {
                    var menu = new AddonMaster.SelectString(addon);
                    count = menu.EntryCount;
                    text = index >= 0 && index < count ? menu.Entries[index].Text : "（索引超出選項範圍）";
                }
            }
            catch (Exception e)
            {
                text = $"（讀取失敗：{e.GetType().Name}）";
            }
            var fired = FireCallback("SelectString", true, index);
            if (fired == true && loggedNpcMenuPresses.Add((npcId, button, index, text)))
                Svc.Log.Information($"[ChilledLeves] 理符委託人選單：NPC {npcId} 按「{button}」索引 {index}（共 {count} 項）＝「{text}」");
            return fired;
        }
    }
}


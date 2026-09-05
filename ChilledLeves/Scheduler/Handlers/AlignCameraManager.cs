using ChilledLeves.IPC;

namespace ChilledLeves.Scheduler.Handlers
{
    /// <summary>
    /// vnavmesh 的「移動時對齊鏡頭」(<c>Path.SetAlignCamera</c>)的<b>對稱借還</b>。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>要解決的問題</b>:AlignCamera 是 vnavmesh 的<b>全域開關</b>,而且是<b>無主的</b>——
    /// 誰都能寫、沒有人負責還。本外掛原本只在 <c>TaskMoveTo.MoveTo</c> 每次移動後寫一次
    /// <c>SetAlignCamera(false)</c>,<b>從來沒有還原過</b>:使用者跑完一趟理符,自己在 vnavmesh
    /// 設定裡開著的「移動時對齊鏡頭」就這樣被關掉,而且舊版 vnavmesh 的 IPC setter 會
    /// <c>NotifyModified()</c> 寫進設定檔 ⇒ <b>重開遊戲也回不來</b>。<br/>
    /// <br/>
    /// 🔑 <b>形狀比照同目錄的 <see cref="YesAlreadyManager"/></b>:借的時候記下原值,
    /// 排程停止(<c>SchedulerMain.AreWeTicking</c> 轉 false)或外掛卸載時還回去。<br/>
    /// <br/>
    /// 🔴 <b>已知極限</b>:這裡<b>沒有租約</b>,所以遊戲崩潰或行程被強制結束時仍然還不回去。
    /// 這只是把「永久且會寫進磁碟」降級成「單次執行期間短暫」,<b>不是根治</b>——
    /// 根治要 vnavmesh 那側提供有逾時的租約端點。<br/>
    /// <br/>
    /// ⚠️ <b>快照的語意是「我借走時看到的值」</b>:借用期間使用者自己去 vnavmesh 把它打開,
    /// 停止時仍然會被還原成借走當下的值。這是刻意的——否則就分不出「使用者改的」與
    /// 「第三個外掛改的」。<br/>
    /// <br/>
    /// 🔴 <b>fail-safe</b>:vnavmesh 沒安裝/沒載入完時,<c>NavmeshIPC</c> 帶的是
    /// <c>SafeWrapper.AnyException</c>,取值會被吞成 <see langword="false"/>。
    /// 那種情況還原寫回 <see langword="false"/> 等於沒動作,不會卡住任何流程。
    /// </remarks>
    internal static class AlignCameraManager
    {
        /// <summary>現在有沒有借著(＝ <see cref="_saved"/> 是否有效)。</summary>
        private static bool _suppressed;

        /// <summary>借走當下 vnavmesh 的原值,還的時候寫回這個。</summary>
        private static bool _saved;

        /// <summary>
        /// 借走並關掉對齊鏡頭。<b>每次移動呼叫一次,冪等</b>——只有第一次會拍快照,
        /// 之後單純維持關閉(與改動前「每次移動都寫一次」的行為相同)。
        /// </summary>
        internal static void SuppressNow()
        {
            if (!_suppressed)
            {
                _saved      = P.navmesh.GetAlignCamera();
                _suppressed = true;
                Svc.Log.Information($"[vnavmesh 對齊鏡頭] 已借走並關閉(借走當下的原值是 {_saved});排程停止或外掛卸載時會還原回去。");
            }

            P.navmesh.SetAlignCamera(false);
        }

        /// <summary>排程停下來就把它還回去。<b>每個 framework tick 呼叫一次,冪等。</b></summary>
        internal static void Tick()
        {
            if (_suppressed && !SchedulerMain.AreWeTicking)
                RestoreNow();
        }

        /// <summary>把對齊鏡頭還原成借走當下的值(沒借就什麼都不做)。</summary>
        internal static void RestoreNow()
        {
            if (!_suppressed)
                return;

            _suppressed = false;
            if (!NavmeshIPC.Installed)
                return;

            P.navmesh.SetAlignCamera(_saved);
            Svc.Log.Information($"[vnavmesh 對齊鏡頭] 已還原為 {_saved}。");
        }
    }
}

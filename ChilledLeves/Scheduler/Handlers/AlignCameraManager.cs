using ChilledLeves.IPC;

namespace ChilledLeves.Scheduler.Handlers
{
    /// <summary>
    /// vnavmesh 的「移動時對齊鏡頭」(<c>Path.SetAlignCamera</c>)的<b>對稱借還</b>。
    /// </summary>
    /// <remarks>
    /// AlignCamera 是 vnavmesh 的<b>全域開關</b>，誰都能寫、沒有人負責還 ⇒ 借的時候記下原值，
    /// 排程停止(<c>SchedulerMain.AreWeTicking</c> 轉 false)或插件卸載時還回去（形狀比照同目錄的 <see cref="YesAlreadyManager"/>）。<br/>
    /// 🔴 <b>已知極限</b>：這裡<b>沒有租約</b>，遊戲崩潰或行程被強制結束時還不回去。<br/>
    /// ⚠️ 快照是「借走當下看到的值」：借用期間使用者自己改了，停止時仍還原成借走當下的值——刻意的，
    /// 否則分不出「使用者改的」與「第三個插件改的」。<br/>
    /// 🔴 vnavmesh 沒安裝時取值被 <c>SafeWrapper.AnyException</c> 吞成 <see langword="false"/> ⇒ 借的時候不拍快照、還原時不寫，不會卡住任何流程。
    /// </remarks>
    internal static class AlignCameraManager
    {
        /// <summary>現在有沒有借著(＝ <see cref="_saved"/> 是否有效)。</summary>
        private static bool _suppressed;

        /// <summary>借走當下 vnavmesh 的原值,還的時候寫回這個。</summary>
        private static bool _saved;

        /// <summary>
        /// 借走並關掉對齊鏡頭。<b>每次移動呼叫一次,冪等</b>——只有第一次會拍快照,
        /// 之後單純維持關閉。
        /// </summary>
        internal static void SuppressNow()
        {
            if (!_suppressed)
            {
                // vnavmesh 不在時取值是被吞出來的 false,不是原值:不拍快照、不算借走(與 RestoreNow 對稱)。
                if (!NavmeshIPC.Installed)
                    return;

                _saved      = P.navmesh.GetAlignCamera();
                _suppressed = true;
                Svc.Log.Information($"[vnavmesh 對齊鏡頭] 已借走並關閉(借走當下的原值是 {_saved});排程停止或插件卸載時會還原回去。");
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

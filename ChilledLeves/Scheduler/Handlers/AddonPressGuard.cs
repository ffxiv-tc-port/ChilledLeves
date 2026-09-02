using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Plugin.Services;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System.Collections.Generic;
using System.Threading;

namespace ChilledLeves.Scheduler.Handlers
{
    /// <summary>
    /// 「同一扇視窗按過就不要再按，直到它真的收掉」的共用閘門。
    /// </summary>
    /// <remarks>
    /// 🔴🔴 <b>存在的唯一理由是 2026-08-31 的實機崩潰</b>：<c>SelectYesno</c> 這類確認框
    /// 被按下之後有<b>「正在關閉中」的幾幀</b>，這段期間 <c>GetAddonByName</c> 仍然回得到實例、
    /// <c>IsVisible</c> 與 <c>UldManager.LoadedState == Loaded</c> 也都還成立——
    /// 也就是說 <c>IsAddonReady</c>／<c>IsReady</c> <b>三關全過、擋不住這個窗口</b>。
    /// 此時再對它送一次 callback 就是原生 AccessViolation（<c>C0000005</c>）。
    /// AVE 在 .NET Core 是 corrupted-state exception，<c>try</c>/<c>catch</c> 與任何
    /// 例外隔離都攔不到，遊戲當場關閉 ——<b>唯一的防護是「不要送第二次」，不是「送了再接住」</b>。
    /// <para>
    /// ⚠️ 呼叫端原有的 <c>EzThrottler.Throttle(..., 100)</c> 之類<b>不是</b>防護：
    /// 它記的是「上一次動作在哪個時刻」，不是「這扇窗已經按過」，而 100 毫秒
    /// （60fps 下約 6 幀）遠遠短於一扇窗關閉所需的時間。
    /// </para>
    /// <para>
    /// 🔑 <b>做法</b>：按下之前先登記「這個名字底下的哪一個實例位址被按過」，
    /// 在觀察到那扇窗真的走完生命週期之前不准再按同一個位址。
    /// 🔴 全程只做<b>位址等值比較，永遠不解參</b>——被記下的那個位址隨時可能已經失效。
    /// </para>
    /// <para>
    /// <b>解除封鎖有兩條互補的觀察點</b>（兩條都只會讓封鎖<b>提早</b>解除，不會延後）：
    /// <list type="number">
    /// <item>
    /// <b>輪詢</b>：被記下的位址已經不在該名稱的 addon 清單裡 ⇒ 那扇窗真的收乾淨了。
    /// 這條在本外掛可行，是因為 <see cref="GenericHandlers.FireCallback"/> 只從
    /// <c>Svc.Framework.Update</c>（<c>ChilledLeves.Tick</c> → <c>NeoTaskManager</c>）進來，
    /// <b>每個 tick 都會再進來一次</b>；<c>AddonLifecycle PostDraw</c> 驅動的外掛沒有這個性質
    /// （窗消失的那一幀監聽器根本不會被叫到），那種地方只能靠下面第 2 條。
    /// </item>
    /// <item>
    /// <b><see cref="IAddonLifecycle"/> 事件</b>：<see cref="AddonEvent.PreFinalize"/>（這一扇正在被銷毀）
    /// 與 <see cref="AddonEvent.PostSetup"/>（有新的一扇被建立起來）。
    /// 🔴 這條是<b>必要的</b>而不是錦上添花：同名 addon 關掉再開常常會<b>重用同一塊記憶體位址</b>，
    /// 只靠第 1 條的話，重開的那扇會被誤認成「按過的那扇還沒收掉」而白白被擋到逾時
    /// （多張理符連續接取正是這個形狀）。
    /// ⚠️ 刻意<b>不</b>把 <c>PostRefresh</c> 也當解除點：它有可能在「關閉中」那幾幀觸發，
    /// 那會把封鎖提早解除，正好把這道防線變成沒有。
    /// </item>
    /// </list>
    /// </para>
    /// <para>
    /// 🔴 <b>逾時放行是刻意的</b>（<see cref="ReleaseEscapeFrames"/>）：萬一某扇窗既不 finalize
    /// 也不重新 setup（例如上一次的 callback 根本沒生效、視窗就是還開著），
    /// 沒有逃生口的話呼叫端會<b>永遠</b>按不下去，等於把崩潰換成靜默失效——而本外掛的
    /// <c>NeoTaskManager</c> 是 <c>abortOnTimeout: true</c>，卡住會清掉整條佇列。
    /// 用<b>幀數</b>而不是毫秒：危險窗口的長度本來就是以幀計的，遊戲卡頓時兩者一起拉長。
    /// </para>
    /// <para>
    /// 📌 <b>正常路徑行為零變化</b>：第一次看到某扇窗一律當場按下去，
    /// <see cref="GenericHandlers.FireCallback"/> 的回傳值也與改動前逐一相同。
    /// </para>
    /// <para>⚠️ 只在主執行緒使用（與呼叫端的 <c>EzThrottler</c> 同一個前提）。</para>
    /// </remarks>
    internal static unsafe class AddonPressGuard
    {
        /// <summary>
        /// 已經按過、那扇窗卻既沒消失也沒重建時，最多再等這麼多幀才允許補按一次。
        /// </summary>
        /// <remarks>
        /// 🔑 這不是節流 —— 真正的防護是「同一扇窗只按一次」，這個值只是防死鎖的逃生口。
        /// 90 幀（60fps 下約 1.5 秒）遠遠大於「關閉中的那幾幀」，補按永遠不會落在危險窗口內。
        /// </remarks>
        private const int ReleaseEscapeFrames = 90;

        /// <summary>輪詢解除時最多掃到第幾個同名實例。</summary>
        /// <remarks>同名視窗同時開著超過這個數量在實務上不存在；掃到第一個空的就提早停。</remarks>
        private const int MaxAddonIndex = 32;

        private readonly record struct PressRecord(nint Address, long Frame);

        private static readonly Dictionary<string, PressRecord> PressedByAddon = new(StringComparer.Ordinal);

        private static readonly Dictionary<string, IAddonLifecycle.AddonEventDelegate> Watchers =
            new(StringComparer.Ordinal);

        /// <summary>守衛自己的幀計數器（<see cref="OnFrameworkUpdate"/> 每個遊戲幀 +1）。</summary>
        /// <remarks>
        /// 🔴🔴 <b>刻意不用 <c>UiBuilder.FrameCount</c></b>：那個計數器加在 Dalamud
        /// <c>UiBuilder.OnDraw()</c> 的<b>最後</b>，而該函式在三種情況下會提早 return ——
        /// ①使用者按下隱藏 UI 熱鍵 ②<b>過場動畫</b> ③GPose（三個對應的設定預設全開）。
        /// 也就是說<b>過場期間那個計數器完全不前進</b>。
        /// 按下點走的是 <c>Framework.Update</c>／原生事件，過場中照常每幀被叫到，
        /// 於是「按下照常、逃生口永不到期」⇒ <c>Talk</c> 這種要靠逃生口翻頁的窗會卡在第一頁，
        /// 而封鎖也永遠不會因逾時解除。
        /// <para>
        /// <c>Framework.Update</c> 在遊戲的 update hook 裡，與繪製與否無關，所以自己數才是對的時鐘。
        /// </para>
        /// </remarks>
        private static long frameCount;

        /// <summary>0 ＝時鐘還沒訂閱、1 ＝已訂閱。</summary>
        /// <remarks>
        /// 🔴 用 <see cref="Interlocked.CompareExchange(ref int, int, int)"/> 而不是 <c>bool</c>：
        /// 重複訂閱不是「沒效果」，而是<b>一個 tick 前進 2 幀 ＝ 所有逃生口對半砍</b>，
        /// 會把補按往「關閉中」的危險窗口推。
        /// </remarks>
        private static int clockSubscribed;

        /// <summary>目前的幀序號。</summary>
        private static long CurrentFrame => frameCount;

        /// <summary>時鐘本體。<b>函式體內不可以有任何條件</b>，否則會出現「時鐘停住」的空窗。</summary>
        private static void OnFrameworkUpdate(IFramework framework) => frameCount++;

        /// <summary>啟動守衛用的幀時鐘（可重複呼叫，只會真的訂閱一次）。</summary>
        /// <remarks>
        /// 🔑 <b>越早訂閱越好</b>：同一個外掛內部的 <c>Framework.Update</c> 是一條多播委派、
        /// 整條包在單一 try/catch 裡（沒有 per-handler 例外隔離），
        /// 排在前面的處理常式擲例外時，<b>後面所有處理常式那個 tick 完全不會被呼叫</b>。
        /// 時鐘排在最前面，才不會被別人的例外連帶停掉。
        /// </remarks>
        internal static void EnsureClockRunning()
        {
            if (Interlocked.CompareExchange(ref clockSubscribed, 1, 0) != 0) return;
            Svc.Framework.Update += OnFrameworkUpdate;
        }

        /// <summary>
        /// 登記「即將對這扇視窗送出 callback」。<b>回 <see langword="false"/> ＝這一幀絕對不能送。</b>
        /// </summary>
        /// <remarks>
        /// 呼叫點要放在<b>緊接著送出動作之前</b>——這支一回 <see langword="true"/> 就已經把
        /// 「按過了」記下去，登記完卻不按的話會白白封鎖到逃生口為止。
        /// </remarks>
        public static bool TryBeginPress(string addonName, AtkUnitBase* addon)
        {
            // 保險絲：外掛建構式已經叫過一次，這裡再叫一次是為了「萬一那條路徑改掉了」——
            // 時鐘沒跑的話 waited 恆為 0、逃生口永不到期，等於把崩潰換成死鎖。
            // 🔴 必須放在所有 early return 之前。
            EnsureClockRunning();

            if (addon == null || string.IsNullOrEmpty(addonName)) return false;

            // 先把「那扇窗已經從 addon 清單消失」的紀錄清掉（含其他名字的），
            // 下一扇同名窗才會被當成全新的窗處理。
            ReleaseVanished();
            EnsureWatching(addonName);

            var address = (nint)addon;
            var frame = CurrentFrame;

            if (PressedByAddon.TryGetValue(addonName, out var pressed) && pressed.Address == address)
            {
                var waited = frame - pressed.Frame;
                if (waited < ReleaseEscapeFrames)
                {
                    // 🔴 這就是崩潰的那一幀。診斷寫 Information（使用者跑 LogLevel 2），並節流免得洗版。
                    if (EzThrottler.Throttle($"AddonPressGuard-Hold-{addonName}", 1000))
                        PluginInfo($"[AddonPressGuard] 「{addonName}」（實例 0x{address:X}）按過之後還沒觀察到它收掉，" +
                                   "這一幀不再送 callback —— 對關閉中的視窗送 callback 是攔不到的存取違規。");

                    return false;
                }

                if (EzThrottler.Throttle($"AddonPressGuard-Release-{addonName}", 10000))
                    PluginInfo($"[AddonPressGuard] 「{addonName}」（實例 0x{address:X}）按下後 {waited} 幀" +
                               "既沒有被銷毀也沒有重新建立，判定為「上一次按下沒生效」而不是「正在關閉」，解除封鎖讓呼叫端重試。");
            }

            PressedByAddon[addonName] = new PressRecord(address, frame);
            return true;
        }

        /// <summary>停止排程時清掉所有封鎖紀錄（監聽器留著，下次啟動照用）。</summary>
        public static void Reset() => PressedByAddon.Clear();

        /// <summary>外掛卸載時硬拆所有監聽器（不留指向本組件的委派）。</summary>
        public static void ForceTeardown()
        {
            if (Interlocked.Exchange(ref clockSubscribed, 0) == 1)
                Svc.Framework.Update -= OnFrameworkUpdate;

            foreach (var (addonName, handler) in Watchers)
            {
                Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, addonName, handler);
                Svc.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, addonName, handler);
            }

            Watchers.Clear();
            PressedByAddon.Clear();
        }

        /// <summary>
        /// 清掉「被記下的那個實例已經不在同名 addon 清單裡」的紀錄。
        /// </summary>
        /// <remarks>
        /// 🔴 只做位址等值比較，永遠不解參。
        /// ⚠️ 判準刻意<b>不</b>用「視窗看起來還 ready 嗎」：關閉中的那幾幀三關全過，
        /// 拿那個當「窗不見了」會在最危險的那幾幀把封鎖解除掉，等於沒有這道防線。
        /// </remarks>
        private static void ReleaseVanished()
        {
            if (PressedByAddon.Count == 0) return;

            // 先抄一份鍵：字典在迭代途中不能移除。同時存在的紀錄實務上是 0~3 個，這份複製可忽略，
            // 而且只有在真的有按下紀錄時才會走到這裡。
            foreach (var addonName in PressedByAddon.Keys.ToArray())
            {
                if (PressedByAddon.TryGetValue(addonName, out var pressed)
                    && !IsStillPresent(addonName, pressed.Address))
                    PressedByAddon.Remove(addonName);
            }
        }

        private static bool IsStillPresent(string addonName, nint address)
        {
            for (var i = 1; i <= MaxAddonIndex; i++)
            {
                var live = (nint)Svc.GameGui.GetAddonByName<AtkUnitBase>(addonName, i);
                if (live == 0) return false;
                if (live == address) return true;
            }

            return false;
        }

        /// <summary>
        /// 第一次守護某個 addon 名稱時掛上解除封鎖用的監聽器。
        /// </summary>
        /// <remarks>
        /// 掛上去之後就不再拆（只在 <see cref="ForceTeardown"/> 拆）：這兩條監聽器只做
        /// 一次字典移除，成本可忽略，而動態掛／拆比較容易留下懸空的監聽器。
        /// </remarks>
        private static void EnsureWatching(string addonName)
        {
            if (Watchers.ContainsKey(addonName)) return;

            // 🔴 只清「就是這一扇」的紀錄，不能按名稱整包清。
            // 失效路徑：幀 F 對 #A 按下並登記；幀 F+1 #A 進入關閉幀（三關仍全過），
            // 此時同名的第二扇 #B 被建立 → PostSetup 觸發 → 若按名稱清就把 #A 的紀錄一起清掉；
            // 幀 F+2 按下點解到 index 1 仍是 #A、查無紀錄 → 放行 → 對關閉中的 #A 送第二發 ⇒ 原生 AVE。
            IAddonLifecycle.AddonEventDelegate handler = (_, args) =>
            {
                var address = (nint)args.Addon.Address;
                if (address == 0) return;
                if (PressedByAddon.TryGetValue(addonName, out var pressed) && pressed.Address == address)
                    PressedByAddon.Remove(addonName);
            };


            Watchers[addonName] = handler;
            Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, addonName, handler);
            Svc.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, addonName, handler);
        }
    }
}

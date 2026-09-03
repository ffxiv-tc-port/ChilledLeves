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
    /// ⚠️ <b>「按過的按鈕會被遊戲停用，所以不會重按」也不成立</b>：ECommons 的
    /// <c>ClickButtonIfEnabled</c>／<c>AddonMaster</c> 那條路遇到停用的按鈕會直接翻
    /// <c>NodeFlags</c> 強制啟用再點，所以<b>模擬點擊（<c>ReceiveEvent</c>）比送 callback
    /// 更早踩到關閉中的窗</b>——同一扇窗的所有按法都必須過同一道閘門。
    /// </para>
    /// <para>
    /// 🔑 <b>做法</b>：按下之前先登記「這個名字底下的哪一個實例位址、用哪一組參數被按過」，
    /// 在觀察到那扇窗真的走完生命週期之前不准再按同一個位址。
    /// 🔴 全程只做<b>位址等值比較，永遠不解參</b>——被記下的那個位址隨時可能已經失效。
    /// </para>
    /// <para>
    /// <b>解除封鎖有兩條互補的觀察點</b>（兩條都只會讓封鎖<b>提早</b>解除，不會延後）：
    /// <list type="number">
    /// <item>
    /// <b>輪詢</b>：被記下的位址已經不在該名稱的 addon 清單裡 ⇒ 那扇窗真的收乾淨了。
    /// 這條在本外掛可行，是因為所有按下點都從 <c>Svc.Framework.Update</c> 進來
    /// （<c>ChilledLeves.Tick</c> → <c>NeoTaskManager</c>／<c>GenericManager</c> 的
    /// <c>LegacyTaskManager</c>），<b>每個 tick 都會再進來一次</b>；
    /// <c>AddonLifecycle PostDraw</c> 驅動的外掛沒有這個性質
    /// （窗消失的那一幀監聽器根本不會被叫到），那種地方只能靠下面第 2 條。
    /// </item>
    /// <item>
    /// <b><see cref="IAddonLifecycle"/> 事件</b>：<see cref="AddonEvent.PreFinalize"/>（這一扇正在被銷毀）
    /// 與 <see cref="AddonEvent.PostSetup"/>（有新的一扇被建立起來）。
    /// 🔴 這條是<b>必要的</b>而不是錦上添花：同名 addon 關掉再開常常會<b>重用同一塊記憶體位址</b>，
    /// 只靠第 1 條的話，重開的那扇會被誤認成「按過的那扇還沒收掉」而白白被擋到逾時
    /// （多張理符連續接取正是這個形狀）。
    /// 🔴 監聽器<b>只清位址對得上的那幾筆</b>，不可以按名稱整包清——理由寫在
    /// <see cref="EnsureWatching"/>。
    /// ⚠️ 刻意<b>不</b>把 <c>PostRefresh</c> 也當解除點：它有可能在「關閉中」那幾幀觸發，
    /// 那會把封鎖提早解除，正好把這道防線變成沒有。
    /// </item>
    /// </list>
    /// </para>
    /// <para>
    /// 🔴 <b>逾時放行是刻意的</b>：萬一某扇窗既不 finalize 也不重新 setup
    /// （例如上一次的 callback 根本沒生效、視窗就是還開著），沒有逃生口的話呼叫端會<b>永遠</b>
    /// 按不下去，等於把崩潰換成靜默失效——而本外掛的 <c>NeoTaskManager</c> 是
    /// <c>abortOnTimeout: true</c>，卡住會清掉整條佇列。
    /// 用<b>幀數</b>而不是毫秒：危險窗口的長度本來就是以幀計的，遊戲卡頓時兩者一起拉長。
    /// 逃生口有長短兩種，取決於這一按會不會讓視窗消失——見
    /// <see cref="ReleaseEscapeFrames"/> 與 <see cref="RoutineRePressEscapeFrames"/>。
    /// </para>
    /// <para>⚠️ 只在主執行緒使用（與呼叫端的 <c>EzThrottler</c> 同一個前提）。</para>
    /// </remarks>
    internal static unsafe class AddonPressGuard
    {
        /// <summary>
        /// 「按下即關」的視窗按過之後，那扇窗卻既沒消失也沒重建時，最多再等這麼多幀才允許補按一次。
        /// </summary>
        /// <remarks>
        /// 🔑 這不是節流 —— 真正的防護是「同一扇窗只按一次」，這個值只是防死鎖的逃生口。
        /// 90 幀（60fps 下約 1.5 秒）遠遠大於「關閉中的那幾幀」，補按永遠不會落在危險窗口內。
        /// 這類窗正常情況下<b>根本走不到</b>逃生口：按下去窗就沒了，封鎖由上面兩個觀察點解除。
        /// </remarks>
        private const int ReleaseEscapeFrames = 90;

        /// <summary>
        /// 「按一次翻一頁、視窗不會因為被按而消失」的視窗，同一組參數之間至少要隔這麼多幀。
        /// </summary>
        /// <remarks>
        /// 🔑 <c>Talk</c> 是代表：它每按一次翻一頁，窗一直都在，所以<b>走逃生口是常態而不是異常</b>，
        /// 用 <see cref="ReleaseEscapeFrames"/> 會讓每一頁對話多等 1.5 秒。
        /// 15 幀（約 0.25 秒）的依據是：關閉中的危險窗口<b>短於 10 幀</b>，15 不落在裡面；
        /// 而每頁多 0.25 秒幾乎無感。
        /// ⚠️ 刻意<b>不</b>用「文字變了沒」當翻頁證據——關閉中的視窗讀出來的文字會壞掉
        /// （崩潰前讀到 U+FFFD 亂碼正是這個徵兆）。
        /// </remarks>
        private const int RoutineRePressEscapeFrames = 15;

        /// <summary>輪詢解除時最多掃到第幾個同名實例。</summary>
        /// <remarks>同名視窗同時開著超過這個數量在實務上不存在；掃到第一個空的就提早停。</remarks>
        private const int MaxAddonIndex = 32;

        /// <summary>單一 addon 名稱底下最多保留幾筆按下紀錄。</summary>
        /// <remarks>
        /// 只是不讓「一扇一直開著的窗」無限累積參數組（例如在理符清單裡逐一點過幾百張）；
        /// 超過就丟掉最舊的那筆。實務上同時存在的紀錄是個位數。
        /// </remarks>
        private const int MaxTrackedPressesPerAddon = 64;

        /// <summary>
        /// 「按下即關」那類按法共用的參數鍵。
        /// </summary>
        /// <remarks>
        /// 🔑 <b>回答一次就終結的視窗要併 key</b>：<c>SelectYesno</c> 被按了「是」之後，
        /// 換一組參數再按一次一樣是對關閉中的窗送第二發。反過來，「按一次翻一頁」的窗
        /// （<c>Talk</c>／理符清單的選取／逐格交納）必須<b>照參數分開記</b>，
        /// 否則「同一扇窗連送不同參數」的正常流程會被自己擋掉。
        /// </remarks>
        private const string SingleAnswerKey = "*";

        /// <summary>
        /// 走 <see cref="TryBeginCallback"/> 這條共用入口、且屬於「按一次翻一頁」的視窗名稱。
        /// </summary>
        /// <remarks>
        /// ⚠️ 這份名單<b>只影響逃生口長度與 log 等級</b>，不影響「按過就擋」這件事本身。
        /// 沒列進來的一律當成「按下即關」處理（比較保守的那一邊）。
        /// </remarks>
        private static readonly HashSet<string> RoutineRePressAddons = new(StringComparer.Ordinal)
        {
            "Talk",
        };

        /// <param name="Address">被按過的那個實例位址。<b>只做等值比較，永遠不解參。</b></param>
        /// <param name="Frame">按下的那一幀。</param>
        /// <param name="Terminating">這一按會讓視窗消失 ⇒ 之後不管換什麼參數都不准再碰這個實例。</param>
        /// <param name="RoutineRePress">重複按同一組參數是正常流程 ⇒ 用短逃生口、log 降到 Debug。</param>
        private readonly record struct PressRecord(nint Address, long Frame, bool Terminating, bool RoutineRePress);

        /// <summary>addon 名稱 → 參數鍵 → 按下紀錄。</summary>
        private static readonly Dictionary<string, Dictionary<string, PressRecord>> PressedByAddon =
            new(StringComparer.Ordinal);

        private static readonly Dictionary<string, IAddonLifecycle.AddonEventDelegate> Watchers =
            new(StringComparer.Ordinal);

        // 以下四個只在主執行緒的同步流程裡當暫存用，避免每幀配置新集合。
        private static readonly List<string> nameScratch = new();
        private static readonly List<string> keyScratch = new();
        private static readonly List<string> watcherScratch = new();
        private static readonly Dictionary<nint, bool> presenceScratch = new();

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
        /// 🔴 用 <see cref="Interlocked"/> 而不是 <c>bool</c>：重複訂閱不是「沒效果」，
        /// 而是<b>一個 tick 前進 2 幀 ＝ 所有逃生口對半砍</b>，會把補按往「關閉中」的危險窗口推。
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
        /// 給 <see cref="GenericHandlers.FireCallback"/> 這條共用入口用的包裝：
        /// 依 addon 名稱自動判定粒度與逃生口長度。
        /// </summary>
        /// <remarks>
        /// 名字沒列在 <see cref="RoutineRePressAddons"/> 裡的一律當「按下即關」，
        /// 也就是<b>整扇窗只准按一次</b>——這正是這道閘門原本的行為，所以既有的呼叫點行為不變。
        /// </remarks>
        public static bool TryBeginCallback(string addonName, AtkUnitBase* addon)
        {
            if (string.IsNullOrEmpty(addonName)) return false;

            var routine = RoutineRePressAddons.Contains(addonName);
            return TryBeginPress(addonName, addon, SingleAnswerKey, terminating: !routine, routineRePress: routine);
        }

        /// <summary>
        /// 登記「即將對這扇視窗送出某一個按法」。<b>回 <see langword="false"/> ＝這一幀絕對不能送。</b>
        /// </summary>
        /// <param name="addonName">視窗名稱。</param>
        /// <param name="addon">這一幀剛解出來的實例指標（<b>只拿來當識別碼，不會解參</b>）。</param>
        /// <param name="pressKey">
        /// 參數組。同一扇窗上「意義不同的按法」要給不同的鍵，否則正常流程會被自己擋掉
        /// （例如逐格交納、逐張選理符）。預設 <see cref="SingleAnswerKey"/> ＝整扇窗共用一個鍵。
        /// </param>
        /// <param name="terminating">
        /// 這一按會讓視窗消失嗎？<see langword="true"/>（預設）代表之後<b>不管換什麼參數</b>
        /// 都不准再碰這個實例——關閉中的窗換個參數送一樣會爆。
        /// </param>
        /// <param name="routineRePress">
        /// 重複按同一組參數是正常流程嗎？<see langword="true"/> 會改用
        /// <see cref="RoutineRePressEscapeFrames"/> 這個短逃生口，並把診斷降到 Debug。
        /// </param>
        /// <remarks>
        /// 呼叫點要放在<b>緊接著送出動作之前</b>——這支一回 <see langword="true"/> 就已經把
        /// 「按過了」記下去，登記完卻不按的話會白白封鎖到逃生口為止。
        /// <para>
        /// 🔴 被擋下時呼叫端要回 <see langword="false"/> 而不是 <see langword="null"/>：
        /// 兩套 TaskManager 的 <c>bool?</c> 都是三態，<c>null</c> 會清掉整條佇列。
        /// 「這一輪沒按到、下一個 tick 再來」與「addon 還沒就緒」走同一條既有路徑。
        /// </para>
        /// </remarks>
        public static bool TryBeginPress(string addonName, AtkUnitBase* addon,
                                         string pressKey = SingleAnswerKey,
                                         bool terminating = true,
                                         bool routineRePress = false)
        {
            // 保險絲：外掛建構式已經叫過一次，這裡再叫一次是為了「萬一那條路徑改掉了」——
            // 時鐘沒跑的話 waited 恆為 0、逃生口永不到期，等於把崩潰換成死鎖。
            // 🔴 必須放在所有 early return 之前。
            EnsureClockRunning();

            if (addon == null || string.IsNullOrEmpty(addonName)) return false;
            if (string.IsNullOrEmpty(pressKey)) pressKey = SingleAnswerKey;

            // 先把「那扇窗已經從 addon 清單消失」的紀錄清掉（含其他名字的），
            // 下一扇同名窗才會被當成全新的窗處理。
            ReleaseVanished();
            EnsureWatching(addonName);

            var address = (nint)addon;
            var frame = CurrentFrame;

            if (!PressedByAddon.TryGetValue(addonName, out var presses))
            {
                presses = new Dictionary<string, PressRecord>(StringComparer.Ordinal);
                PressedByAddon[addonName] = presses;
            }
            else
            {
                foreach (var (key, record) in presses)
                {
                    if (record.Address != address) continue;

                    // 同一組參數一定要擋；而「按下即關」的那一發按過以後，
                    // 這個實例上的「任何」按法都要擋——窗都在關了，換個參數送一樣是同一個崩潰。
                    var sameKey = string.Equals(key, pressKey, StringComparison.Ordinal);
                    if (!record.Terminating && !sameKey) continue;

                    var escape = record.RoutineRePress ? RoutineRePressEscapeFrames : ReleaseEscapeFrames;
                    var waited = frame - record.Frame;

                    if (waited >= escape)
                    {
                        // 逾時放行：這一筆不再擋，但同一個位址上的其他紀錄還是要看完。
                        if (record.RoutineRePress)
                        {
                            // 這類窗走逃生口是常態（每翻一頁一次），寫 Debug 免得洗掉整份 log。
                            PluginDebug($"[AddonPressGuard] 「{addonName}」（實例 0x{address:X}）距上次同樣的按法已 {waited} 幀，" +
                                        "視為正常的下一次互動，放行。");
                        }
                        else if (EzThrottler.Throttle($"AddonPressGuard-Release-{addonName}", 10000))
                        {
                            PluginInfo($"[AddonPressGuard] 「{addonName}」（實例 0x{address:X}）按下後 {waited} 幀" +
                                       "既沒有被銷毀也沒有重新建立，判定為「上一次按下沒生效」而不是「正在關閉」，解除封鎖讓呼叫端重試。");
                        }

                        continue;
                    }

                    // 「按一次翻一頁」的窗每一幀都會走到這裡（等的就是那 15 幀），
                    // 🔴 這條路徑上絕對不能寫 log —— 每幀一行會把整份 log 洗掉，
                    //    而且 ECommons 的 log 沒有寫入端閘門，「等級關著就免費」是錯的。
                    if (!record.RoutineRePress && EzThrottler.Throttle($"AddonPressGuard-Hold-{addonName}", 1000))
                    {
                        // 🔴 這就是崩潰的那一幀。診斷寫 Information（使用者跑 LogLevel 1），並節流免得洗版。
                        PluginInfo($"[AddonPressGuard] 「{addonName}」（實例 0x{address:X}）按過之後還沒觀察到它收掉，" +
                                   "這一幀不再送 —— 對關閉中的視窗送出按下是攔不到的存取違規。");
                    }

                    return false;
                }
            }

            TrimIfNeeded(presses);
            presses[pressKey] = new PressRecord(address, frame, terminating, routineRePress);
            // 跨外掛重按診斷：只在真的送出按壓時記一行，刻意不節流。
            PluginInfo($"[按窗診斷] plugin=ChilledLeves addon={addonName} addr=0x{address:X} key={pressKey}");
            return true;
        }

        /// <summary>停止排程時清掉所有封鎖紀錄（監聽器與時鐘留著，下次啟動照用）。</summary>
        public static void Reset() => PressedByAddon.Clear();

        /// <summary>外掛卸載時硬拆時鐘與所有監聽器（不留指向本組件的委派）。</summary>
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

        /// <summary>紀錄多到不像話時丟掉最舊的那筆，免得一扇一直開著的窗無限累積參數組。</summary>
        private static void TrimIfNeeded(Dictionary<string, PressRecord> presses)
        {
            if (presses.Count < MaxTrackedPressesPerAddon) return;

            var oldestKey = string.Empty;
            var oldestFrame = long.MaxValue;
            foreach (var (key, record) in presses)
            {
                if (record.Frame >= oldestFrame) continue;
                oldestFrame = record.Frame;
                oldestKey = key;
            }

            if (oldestKey.Length > 0) presses.Remove(oldestKey);
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

            // 字典在迭代途中不能移除，先抄一份鍵。同時存在的名稱實務上是 0~3 個。
            nameScratch.Clear();
            nameScratch.AddRange(PressedByAddon.Keys);

            foreach (var addonName in nameScratch)
            {
                if (!PressedByAddon.TryGetValue(addonName, out var presses)) continue;

                presenceScratch.Clear();
                keyScratch.Clear();

                foreach (var (key, record) in presses)
                {
                    // 同一個位址只查一次：掃 addon 清單是這裡最貴的動作。
                    if (!presenceScratch.TryGetValue(record.Address, out var present))
                    {
                        present = IsStillPresent(addonName, record.Address);
                        presenceScratch[record.Address] = present;
                    }

                    if (!present) keyScratch.Add(key);
                }

                foreach (var key in keyScratch) presses.Remove(key);
                if (presses.Count == 0) PressedByAddon.Remove(addonName);
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
                if (address == 0 || !PressedByAddon.TryGetValue(addonName, out var presses)) return;

                watcherScratch.Clear();
                foreach (var (key, record) in presses)
                    if (record.Address == address)
                        watcherScratch.Add(key);

                foreach (var key in watcherScratch) presses.Remove(key);
                if (presses.Count == 0) PressedByAddon.Remove(addonName);
            };

            Watchers[addonName] = handler;
            Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, addonName, handler);
            Svc.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, addonName, handler);
        }
    }
}

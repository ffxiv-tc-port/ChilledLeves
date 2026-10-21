using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
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
    /// <c>SelectYesno</c> 這類確認框被按下之後有<b>「正在關閉中」的幾幀</b>：<c>GetAddonByName</c> 仍然回得到實例、
    /// <c>IsAddonReady</c> 三關全過；此時再送一次 callback／模擬點擊就是攔不到的原生存取違規 ——
    /// <b>唯一的防護是「不要送第二次」</b>。呼叫端的 <c>EzThrottler</c> 與「按鈕會被停用」都不是防護。
    /// <para>
    /// 粒度＝（視窗位址，參數鍵）；位址<b>只做等值比較，永遠不解參</b>。<c>terminating</c> 的紀錄還熱著時，同一位址任何按法都擋。
    /// 解除點：位址從同名清單消失（每幀輪詢）、同位址 <c>PostSetup</c>（這一幀才登記的不清）、常駐窗連續隱藏 N 幀、逃生口。
    /// <c>PreFinalize</c> 是<b>上鎖點</b>，鎖到位址消失或同位址重建。刻意不把 <c>PostRefresh</c> 當解除點（關閉中也可能觸發）。
    /// </para>
    /// <para>⚠️ 只在主執行緒使用（與呼叫端的 <c>EzThrottler</c> 同一個前提）。任何不確定一律回 <see langword="false"/>。</para>
    /// </remarks>
    internal static unsafe class AddonPressGuard
    {
        /// <summary>
        /// 「按下即關」的視窗按過之後，那扇窗卻既沒消失也沒重建時，最多再等這麼多幀才允許補按一次。
        /// </summary>
        /// <remarks>這不是節流，只是防死鎖的逃生口；用幀數而不是毫秒，卡頓時兩者一起拉長。</remarks>
        private const int ReleaseEscapeFrames = 90;

        /// <summary>
        /// 「按一次翻一頁、視窗不會因為被按而消失」的視窗，同一組參數之間至少要隔這麼多幀。
        /// </summary>
        /// <remarks>
        /// <c>Talk</c> 是代表：走逃生口是常態。關閉中的危險窗口短於 10 幀，15 不落在裡面。
        /// ⚠️ 刻意不用「文字變了沒」當翻頁證據——關閉中的視窗讀出來的文字會壞掉。
        /// </remarks>
        private const int RoutineRePressEscapeFrames = 15;

        /// <summary>同名實例最多掃到第幾個（遊戲同名實例上限）；掃到第一個空的就提早停。</summary>
        private const int MaxAddonIndex = 256;

        /// <summary>單一實例底下最多保留幾筆參數鍵；滿了只淘汰已過終結逃生口的冷紀錄，淘汰不到這一發不送。</summary>
        private const int MaxTrackedPressesPerAddon = 64;

        /// <summary>超過這麼多幀沒再被按過、也沒上鎖的位址紀錄，定期回收（早就過了逃生口，不改變任何判定）。</summary>
        private const int SweepMinAgeFrames = 3600;

        private const int SweepIntervalFrames = 60;

        /// <summary>「按下即關」那類按法共用的參數鍵（回答一次就終結的視窗要併 key）。</summary>
        private const string SingleAnswerKey = "*";

        /// <summary>
        /// 走 <see cref="TryBeginCallback"/> 這條共用入口、且屬於「按一次翻一頁」的視窗名稱。
        /// </summary>
        /// <remarks>只影響逃生口長度與 log 等級；沒列進來的一律當成「按下即關」（比較保守的那一邊）。</remarks>
        private static readonly HashSet<string> RoutineRePressAddons = new(StringComparer.Ordinal)
        {
            "Talk",
        };

        /// <summary>
        /// 常駐視窗：關閉只是隱藏，位址整場留在清單裡，PreFinalize／PostSetup 都不會再發生。
        /// 這些窗改由「按下後連續隱藏 <see cref="HiddenReleaseFrames"/> 幀」解除，而且送出前必須看得見（兩者互為反面）。
        /// </summary>
        /// <remarks>⚠️ 只收實機證實常駐的名字：把會被銷毀的窗誤收進來，等於在它拆除途中提早解除封鎖。</remarks>
        private static readonly HashSet<string> PersistentAddons = new(StringComparer.Ordinal)
        {
            "ContextIconMenu",
            "ContextMenu",
            "Talk",
        };

        /// <summary>常駐視窗按下後，要連續觀察到「還在清單裡、但已隱藏」這麼多幀才解除；看到可見就歸零重數。</summary>
        private const int HiddenReleaseFrames = 20;

        /// <param name="Frame">按下的那一幀。</param>
        /// <param name="Terminating">這一按會讓視窗消失 ⇒ 之後不管換什麼參數都不准再碰這個實例。</param>
        /// <param name="RoutineRePress">重複按同一組參數是正常流程 ⇒ 用短逃生口、log 降到 Debug。</param>
        private readonly record struct PressRecord(long Frame, bool Terminating, bool RoutineRePress);

        private sealed class AddressRecords(string addonName)
        {
            public string AddonName { get; set; } = addonName;
            public Dictionary<string, PressRecord> ByKey { get; } = new(StringComparer.Ordinal);
            public long LastFrame { get; set; }

            /// <summary>連續觀察到「還在清單裡、但已隱藏」的幀數；只有常駐窗會累加，看得見或重新按下就歸零。</summary>
            public int HiddenFrames { get; set; }

            /// <summary>登記時在同名清單裡找得到這個位址；只有它成立才准用「從清單消失」解除。</summary>
            public bool Listed { get; set; }

            /// <summary>看過 <see cref="AddonEvent.PreFinalize"/>：在位址消失或同位址重建之前，任何按法都不准，也沒有逃生口。</summary>
            public bool Finalizing { get; set; }
        }

        /// <summary>這一幀在同名清單裡對某個位址的觀察。零值必須是「不解除」的答案。</summary>
        private enum AddonPresence
        {
            /// <summary>查詢不成立（名稱空、位址 0、同名實例超過掃描上限）。零值。</summary>
            Unknown = 0,

            /// <summary>掃完同名清單都沒有這個位址＝那扇窗已經收乾淨。</summary>
            NotListed = 1,

            Visible = 2,
            Hidden = 3,
        }

        /// <summary>位址 → 這個實例被按過的參數鍵。位址只當字典鍵，從不解參考。</summary>
        private static readonly Dictionary<nint, AddressRecords> Pressed = new();

        /// <summary>曾經守護過的窗名：這些名字的實例一進 <see cref="AddonEvent.PreFinalize"/> 就上鎖（即使還沒按過）。</summary>
        private static readonly HashSet<string> GuardedNames = new(StringComparer.Ordinal);

        // 以下幾個只在主執行緒的同步流程裡當暫存用，避免每幀配置新集合。
        private static readonly List<nint> releaseScratch = new();
        private static readonly List<string> keyScratch = new();

        /// <summary>「這個窗名第一次走隱藏解除」只寫一行 Information。</summary>
        private static readonly HashSet<string> HiddenReleaseReported = new(StringComparer.Ordinal);

        /// <summary>守衛自己的幀計數器（<see cref="OnFrameworkUpdate"/> 每個遊戲幀 +1）。</summary>
        /// <remarks>
        /// 🔴 刻意不用 <c>UiBuilder.FrameCount</c>：隱藏 UI／過場動畫／GPose 期間它不前進，逃生口永不到期，
        /// <c>Talk</c> 這種要靠逃生口翻頁的窗會卡在第一頁。
        /// </remarks>
        private static long frameCount;

        /// <summary>同一個幀時鐘給別的閘門用（建構子就訂閱，同一幀裡比任務佇列與主 Tick 都先遞增）。只在主執行緒讀。</summary>
        internal static long FrameCount => frameCount;

        /// <summary>0 ＝時鐘還沒訂閱、1 ＝已訂閱。</summary>
        /// <remarks>用 <see cref="Interlocked"/>：重複訂閱會讓一個 tick 前進 2 幀 ＝ 所有逃生口對半砍。</remarks>
        private static int clockSubscribed;

        private static IAddonLifecycle.AddonEventDelegate? lifecycleHandler;

        /// <summary>🔴 遞增必須是第一行、前面不可以有任何條件：一旦時鐘會停，逃生口就跟著失準。</summary>
        private static void OnFrameworkUpdate(IFramework framework)
        {
            frameCount++;
            PollRecords();
            if (frameCount % SweepIntervalFrames == 0) SweepColdRecords();
        }

        /// <summary>啟動守衛用的幀時鐘與解除封鎖用的全域監聽器（可重複呼叫，只會真的訂閱一次）。</summary>
        /// <remarks>
        /// 🔑 越早訂閱越好：同一個外掛的 <c>Framework.Update</c> 整條共用一個 try/catch，
        /// 排在前面的處理常式擲例外時，後面所有處理常式那個 tick 完全不會被呼叫。
        /// </remarks>
        internal static void EnsureClockRunning()
        {
            if (Interlocked.CompareExchange(ref clockSubscribed, 1, 0) != 0) return;
            Svc.Framework.Update += OnFrameworkUpdate;
            lifecycleHandler = OnAddonLifecycle;
            Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, lifecycleHandler);
            Svc.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, lifecycleHandler);
        }

        /// <summary>
        /// 給 <see cref="GenericHandlers.FireCallback"/> 這條共用入口用的包裝：依 addon 名稱自動判定粒度與逃生口長度。
        /// </summary>
        /// <remarks>名字沒列在 <see cref="RoutineRePressAddons"/> 裡的一律當「按下即關」，也就是整扇窗只准按一次。</remarks>
        public static bool TryBeginCallback(string addonName, AtkUnitBase* addon)
        {
            if (string.IsNullOrEmpty(addonName)) return false;

            var routine = RoutineRePressAddons.Contains(addonName);
            return TryBeginPress(addonName, addon, SingleAnswerKey, terminating: !routine, routineRePress: routine);
        }

        /// <summary>
        /// 登記「即將對這扇視窗送出某一個按法」。<b>回 <see langword="false"/> ＝這一幀絕對不能送。</b>
        /// </summary>
        /// <param name="addonName">視窗名稱；空的一律不放行。</param>
        /// <param name="addon">這一幀剛解出來的實例指標（<b>只拿來當識別碼，不會解參</b>）。</param>
        /// <param name="pressKey">參數組；同一扇窗上「意義不同的按法」要給不同的鍵。預設整扇窗共用一個鍵。</param>
        /// <param name="terminating">這一按會讓視窗消失嗎？<see langword="true"/>（預設）＝之後不管換什麼參數都不准再碰這個實例。</param>
        /// <param name="routineRePress">重複按同一組參數是正常流程嗎？<see langword="true"/> 改用短逃生口並把診斷降到 Debug。</param>
        /// <remarks>
        /// 呼叫點要放在<b>緊接著送出動作之前</b>。🔴 被擋下時呼叫端要回 <see langword="false"/> 而不是 <see langword="null"/>：
        /// 兩套 TaskManager 的 <c>bool?</c> 都是三態，<c>null</c> 會清掉整條佇列。
        /// </remarks>
        public static bool TryBeginPress(string addonName, AtkUnitBase* addon,
                                         string pressKey = SingleAnswerKey,
                                         bool terminating = true,
                                         bool routineRePress = false)
        {
            // 🔴 必須放在所有 early return 之前：時鐘沒跑的話逃生口永不到期，等於把崩潰換成死鎖。
            EnsureClockRunning();

            if (addon == null || string.IsNullOrEmpty(addonName)) return false;
            if (string.IsNullOrEmpty(pressKey)) pressKey = SingleAnswerKey;

            var address = (nint)addon;
            var frame = frameCount;

            // 常駐窗的解除條件是「連續隱藏」，這裡是它的反面：隱藏中（或這一幀查不到）的常駐窗一律不送。
            if (PersistentAddons.Contains(addonName) && LookUp(addonName, address, readVisibility: true) != AddonPresence.Visible)
                return false;

            GuardedNames.Add(addonName);
            Pressed.TryGetValue(address, out var records);
            if (records != null)
            {
                if (records.Finalizing)
                {
                    LogHold(addonName, address, "已經看到它 PreFinalize、還沒看到它消失");
                    return false;
                }

                foreach (var (key, record) in records.ByKey)
                {
                    // 同一組參數一定要擋；「按下即關」的那一發按過以後，這個實例上的任何按法都要擋。
                    var sameKey = string.Equals(key, pressKey, StringComparison.Ordinal);
                    if (!record.Terminating && !sameKey) continue;

                    var escape = record.RoutineRePress ? RoutineRePressEscapeFrames : ReleaseEscapeFrames;
                    var waited = frame - record.Frame;
                    if (waited >= escape)
                    {
                        LogEscape(addonName, address, waited, record.RoutineRePress);
                        continue;
                    }

                    // 🔴 「按一次翻一頁」的窗每一幀都會走到這裡，這條路徑上不能寫 log。
                    if (!record.RoutineRePress) LogHold(addonName, address, "按過之後還沒觀察到它收掉");
                    return false;
                }

                if (!MakeRoom(addonName, records, pressKey, frame)) return false;
            }
            else
            {
                records = new AddressRecords(addonName);
                Pressed[address] = records;
            }

            if (!records.Listed && LookUp(addonName, address, readVisibility: false) == AddonPresence.Visible)
            {
                records.AddonName = addonName;
                records.Listed = true;
            }

            records.ByKey[pressKey] = new PressRecord(frame, terminating, routineRePress);
            records.LastFrame = frame;
            // 🔴 承重：隱藏解除是「從最後一次按下起算」連續隱藏幾幀；漏掉這一行，前一次累到一半的幀數會被沿用。
            records.HiddenFrames = 0;

            // 跨外掛重按診斷：只在真的送出按壓時記一行，刻意不節流。
            PluginVerbos($"[按窗診斷] plugin=ChilledLeves addon={addonName} addr=0x{address:X} key={pressKey}");
            return true;
        }

        /// <summary>外掛卸載時硬拆時鐘與所有監聽器（不留指向本組件的委派）。</summary>
        public static void ForceTeardown()
        {
            if (Interlocked.Exchange(ref clockSubscribed, 0) == 1)
            {
                Svc.Framework.Update -= OnFrameworkUpdate;
                if (lifecycleHandler != null)
                {
                    Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, lifecycleHandler);
                    Svc.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, lifecycleHandler);
                }
            }

            lifecycleHandler = null;
            Pressed.Clear();
            GuardedNames.Clear();
        }

        /// <summary>
        /// 同一實例的參數鍵滿了時，只淘汰已過終結逃生口的冷紀錄；淘汰完仍滿回 <see langword="false"/>（這一發不送）。
        /// </summary>
        /// <remarks>擋的是「一扇一直開著的窗無限累積參數組」（例如在理符清單裡逐一點過幾百張）。</remarks>
        private static bool MakeRoom(string addonName, AddressRecords records, string pressKey, long frame)
        {
            if (records.ByKey.Count < MaxTrackedPressesPerAddon || records.ByKey.ContainsKey(pressKey)) return true;

            keyScratch.Clear();
            foreach (var (key, record) in records.ByKey)
            {
                if (frame - record.Frame >= ReleaseEscapeFrames) keyScratch.Add(key);
            }

            foreach (var key in keyScratch) records.ByKey.Remove(key);
            keyScratch.Clear();
            if (records.ByKey.Count < MaxTrackedPressesPerAddon) return true;

            if (EzThrottler.Throttle($"AddonPressGuard-Full-{addonName}", 10000))
                PluginInfo($"[AddonPressGuard] 「{addonName}」的按下紀錄已滿 {MaxTrackedPressesPerAddon} 筆且都還熱著，這一發不送。");
            return false;
        }

        /// <summary>被擋那一幀的診斷：要使用者回報的診斷寫 Information，每個窗名 1 秒一則。</summary>
        private static void LogHold(string addonName, nint address, string reason)
        {
            if (EzThrottler.Throttle($"AddonPressGuard-Hold-{addonName}", 1000))
                PluginInfo($"[AddonPressGuard] 「{addonName}」（實例 0x{address:X}）{reason}，" +
                           "這一幀不再送 —— 對關閉中的視窗送出按下是攔不到的存取違規。");
        }

        /// <summary>走逃生口的診斷：多次互動窗是常態寫 Debug；終結窗走到這裡才是異常，寫 Information。</summary>
        private static void LogEscape(string addonName, nint address, long waited, bool routine)
        {
            if (routine)
            {
                PluginDebug($"[AddonPressGuard] 「{addonName}」（實例 0x{address:X}）距上次同樣的按法已 {waited} 幀，" +
                            "視為正常的下一次互動，放行。");
            }
            else if (EzThrottler.Throttle($"AddonPressGuard-Release-{addonName}", 10000))
            {
                PluginInfo($"[AddonPressGuard] 「{addonName}」（實例 0x{address:X}）按下後 {waited} 幀" +
                           "既沒有被銷毀也沒有重新建立，判定為「上一次按下沒生效」而不是「正在關閉」，解除封鎖讓呼叫端重試。");
            }
        }

        /// <summary>
        /// 每幀的解除路徑：①登記時找得到、現在從同名清單消失 ⇒ 解除（含 PreFinalize 鎖）；
        /// ②常駐窗連續隱藏 <see cref="HiddenReleaseFrames"/> 幀 ⇒ 解除。
        /// </summary>
        /// <remarks>判準刻意不用「還可不可見」（常駐窗例外）：拆除途中那幾幀三關全過。只有「找到了而且看不見」才累加隱藏幀。</remarks>
        private static void PollRecords()
        {
            if (Pressed.Count == 0) return;

            releaseScratch.Clear();
            foreach (var (address, records) in Pressed)
            {
                var persistent = PersistentAddons.Contains(records.AddonName);
                var presence = LookUp(records.AddonName, address, readVisibility: persistent);
                var present = presence != AddonPresence.NotListed;
                if (!present)
                {
                    if (records.Listed) releaseScratch.Add(address);
                    else records.HiddenFrames = 0;
                    continue;
                }

                if (!persistent || records.Finalizing) continue;
                if (presence != AddonPresence.Hidden)
                {
                    records.HiddenFrames = 0;
                    continue;
                }

                if (++records.HiddenFrames < HiddenReleaseFrames) continue;

                releaseScratch.Add(address);
                if (HiddenReleaseReported.Add(records.AddonName))
                    PluginInfo($"[AddonPressGuard] 「{records.AddonName}」是常駐視窗（隱藏而不銷毀），按下記號改由「連續隱藏 {HiddenReleaseFrames} 幀」解除。這一行每個視窗名每次遊戲只寫一次。");
            }

            foreach (var address in releaseScratch) Pressed.Remove(address);
            releaseScratch.Clear();
        }

        /// <summary>在同名的所有實例裡找 <paramref name="address"/>。</summary>
        /// <remarks>
        /// 位址只做等值比較；解參考的是 <c>GetAddonByName</c> 這一幀交回來的指標，而且只在 <paramref name="readVisibility"/> 時才讀。
        /// 不讀可見性時，找到一律回 <see cref="AddonPresence.Visible"/>。
        /// </remarks>
        private static AddonPresence LookUp(string addonName, nint address, bool readVisibility)
        {
            if (string.IsNullOrEmpty(addonName) || address == 0) return AddonPresence.Unknown;

            for (var index = 1; index <= MaxAddonIndex; index++)
            {
                var unit = Svc.GameGui.GetAddonByName<AtkUnitBase>(addonName, index);
                if (unit == null) return AddonPresence.NotListed;
                if ((nint)unit != address) continue;

                if (!readVisibility) return AddonPresence.Visible;
                return unit->IsVisible ? AddonPresence.Visible : AddonPresence.Hidden;
            }

            return AddonPresence.Unknown;
        }

        /// <summary>回收「已經不可能再擋住任何人」的槽位（不改變任何一次「按／不按」的判定）。</summary>
        private static void SweepColdRecords()
        {
            if (Pressed.Count == 0) return;

            releaseScratch.Clear();
            foreach (var (address, records) in Pressed)
            {
                if (records.Finalizing || frameCount - records.LastFrame < SweepMinAgeFrames) continue;
                releaseScratch.Add(address);
            }

            foreach (var address in releaseScratch) Pressed.Remove(address);
            releaseScratch.Clear();
        }

        /// <summary>該位址進入銷毀（上鎖）或重新開始（解除）生命週期。</summary>
        /// <remarks>
        /// 🔴 按位址比對，不是按名稱清空：同名的兩扇窗可以並存（第一扇正在關閉、第二扇剛建好）。
        /// <see cref="AddonEvent.PostSetup"/> 只清「不是這一幀才登記的」紀錄；<see cref="AddonEvent.PreFinalize"/> 把位址鎖到它從清單消失。
        /// </remarks>
        private static void OnAddonLifecycle(AddonEvent type, AddonArgs args)
        {
            var address = (nint)args.Addon.Address;
            if (address == 0) return;

            if (type == AddonEvent.PreFinalize)
            {
                LockFinalizing(address, args);
                return;
            }

            if (!Pressed.TryGetValue(address, out var records)) return;

            records.Finalizing = false;
            keyScratch.Clear();
            foreach (var (key, record) in records.ByKey)
            {
                if (record.Frame == frameCount) continue;
                keyScratch.Add(key);
            }

            foreach (var key in keyScratch) records.ByKey.Remove(key);
            keyScratch.Clear();
            if (records.ByKey.Count == 0) Pressed.Remove(address);
        }

        private static void LockFinalizing(nint address, AddonArgs args)
        {
            if (Pressed.TryGetValue(address, out var records))
            {
                records.Finalizing = true;
                return;
            }

            if (GuardedNames.Count == 0) return;

            var name = args.AddonName;
            if (!GuardedNames.Contains(name)) return;

            Pressed[address] = new AddressRecords(name) { Listed = true, Finalizing = true, LastFrame = frameCount };
        }
    }
}

using ECommons.Automation.LegacyTaskManager;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System.Collections.Generic;

namespace ChilledLeves.Scheduler.Handlers
{
    internal static unsafe class GenericManager
    {
        internal static TaskManager taskManager = new();
        static TaskManager TaskManager => taskManager;
        private static List<int> SlotsFilled { get; set; } = new();
        private static bool? ConfirmOrAbort(AddonRequest* addon)
        {
            // AtkComponentButton.IsEnabled 解的是 OwnerNode(0xA8)，不是 AtkResNode(0xA0)。
            // 原本的 HandOverButton != null 只擋住按鈕本身，擋不到 OwnerNode 為 null，
            // 那是 AccessViolationException(corrupted-state，try/catch 無效)。
            if (GenericHelpers.IsComponentEnabled(addon->HandOverButton))
            {
                // 🔴 交納鈕按下即關 Request：關閉中的那幾幀 GetAddonByName 仍回得到實例、
                //    按鈕也可能還是 enabled + visible，此時再點一次就是攔不到的原生存取違規。
                //    這條路走的是「模擬點擊」（ReceiveEvent），比送 callback 更早踩到那個窗口，
                //    而且 ECommons 遇到停用的按鈕會翻 NodeFlags 強制啟用再點 —— 按鈕狀態不是防護。
                //    這一按會讓視窗消失 ⇒ terminating（之後這個實例上任何按法都擋掉，
                //    包含佇列裡還沒跑完的逐格交納任務）。
                if (!AddonPressGuard.TryBeginPress("Request", &addon->AtkUnitBase)) return false;

                new AddonMaster.Request((IntPtr)addon).HandOver();
                return true;
            }
            return false;
        }
        /// <remarks>
        /// 🔴 這支是<b>幾幀之後</b>才被 TaskManager 跑到的（中間隔著兩個 DelayNext 任務），
        /// 所以視窗指標一定要在<b>執行當下</b>重新解，不能沿用排入佇列那一幀的指標——
        /// Request 在這中間被關掉的話，舊指標指向的就是已經釋放的記憶體。
        /// </remarks>
        private static bool? TryClickItem(int i)
        {
            if (SlotsFilled.Contains(i)) return true;

            if (!TryGetAddonByName<AddonRequest>("Request", out var addon)) return false;

            var contextMenu = (AtkUnitBase*)Svc.GameGui.GetAddonByName("ContextIconMenu", 1).Address;

            if (contextMenu is null || !contextMenu->IsVisible)
            {
                var slot = i - 1;
                var unk = (44 * i) + (i - 1);

                // 這一按是「打開第 slot 格的道具選單」，Request 本身不會因此消失 ⇒
                // 照參數分開記（否則逐格交納會被自己擋住），而重複送同一格是刻意的重試迴圈
                // （選單還沒開就再送一次）⇒ 用短逃生口。原本這裡是每一幀無節流重送。
                if (!AddonPressGuard.TryBeginPress("Request", &addon->AtkUnitBase, $"2/{slot}",
                                                   terminating: false, routineRePress: true))
                    return false;

                ECommons.Automation.Callback.Fire(&addon->AtkUnitBase, false, 2, slot, 0, 0);

                return false;
            }
            else
            {
                // 選單項目按下即關 ContextIconMenu ⇒ terminating。
                // 🔴 被擋下時一定要在標記 SlotsFilled <b>之前</b>就回 false：
                //    這一格根本還沒送出去，標成已填會讓整批交納少一格。
                //    回 false（不是 null）＝這一輪沒做成、下一個 tick 再來。
                if (!AddonPressGuard.TryBeginPress("ContextIconMenu", contextMenu)) return false;

                ECommons.Automation.Callback.Fire(contextMenu, false, 0, 0, 1021003, 0, 0);
                Svc.Log.Debug($"Filled slot {i}");
                SlotsFilled.Add(i);
                return true;
            }
        }

        internal static void Tick()
        {
            if (SchedulerMain.AreWeTicking)
            {
                //by Taurenkey https://github.com/PunishXIV/PandorasBox/blob/24a4352f5b01751767c7ca7f1d4b48369be98711/PandorasBox/Features/UI/AutoSelectTurnin.cs

                var featureEnabled = (P.pandora.GetFeatureEnabled("Auto-select Turn-ins") ?? false);
                var configEnabled = (P.pandora.GetConfigEnabled("Auto-select Turn-ins", "AutoSelect") ?? false);

                var isenabled = featureEnabled && configEnabled;

                if (!isenabled)
                {
                    if (featureEnabled && !configEnabled)
                    {
                        if (EzThrottler.Throttle("Enabling AutoSelect", 1000))
                        {
                            P.pandora.PauseFeature("Auto-select Turn-ins", 1100);
                        }
                    }

                    if (TryGetAddonByName<AddonRequest>("Request", out var addon3))
                    {
                        for (var i = 1; i <= addon3->EntryCount; i++)
                        {
                            if (SlotsFilled.Contains(addon3->EntryCount)) ConfirmOrAbort(addon3);
                            if (SlotsFilled.Contains(i)) return;
                            var val = i;
                            TaskManager.DelayNext($"ClickTurnin{val}", 10);
                            TaskManager.Enqueue(() => TryClickItem(val));
                        }
                    }
                    else
                    {
                        SlotsFilled.Clear();
                        TaskManager.Abort();
                    }
                }
            }

        }
    }
}

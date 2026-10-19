using ChilledLeves.Scheduler.Handlers;
using ECommons.Automation.NeoTaskManager;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System.Collections.Generic;
namespace ChilledLeves.Scheduler.Tasks
{
    internal static class TaskTurnin
    {
        /// <summary>
        /// Waits that follow a turn-in must never abort the queue: <see cref="TaskUpdateWorkList"/> is
        /// enqueued right after us by the caller, and dropping it would leave the leve in C.workList with
        /// its counter un-decremented, which makes the scheduler re-accept a leve that was already
        /// handed in (burning allowances). Timing out here only discards the wait itself.
        /// </summary>
        private static TaskManagerConfiguration WaitConfig => new(timeLimitMS: 20000, abortOnTimeout: false);

        // 「清單裡沒有這張理符」要連續看到這麼久才停，避開清單只填了一半的那幾幀。
        private const long NotListedConfirmMs = 1500;
        // 兩次判定間隔超過這個值＝另一次交納，重新計時。
        private const long NotListedGapMs = 500;
        private static long notListedSinceMs;
        private static long notListedLastMs;
        private static string? notListedQuest;
        private static long lastUnreadableLogMs;

        /// <summary>只在 framework 執行緒呼叫。</summary>
        private static bool NotListedConfirmed(string questName)
        {
            var now = Environment.TickCount64;
            if (notListedSinceMs == 0 || notListedQuest != questName || now - notListedLastMs > NotListedGapMs)
            {
                notListedSinceMs = now;
                notListedQuest = questName;
            }
            notListedLastMs = now;
            return now - notListedSinceMs >= NotListedConfirmMs;
        }

        private static void StopNotListed(string questName)
        {
            notListedSinceMs = 0;
            var seen = new List<string>();
            GetCallback(questName, seen);
            Svc.Log.Information($"[ChilledLeves] 交納清單裡找不到理符「{questName}」，未選擇任何項目，已停止理符自動化。清單內容：{string.Join("／", seen)}");
            Svc.Chat.Print("[Chilled Leves] The leve \"??\" is not in the hand-in list, so nothing was selected and the leve run was stopped. Check the hand-in list, then press Start again.".Loc(questName));
            SchedulerMain.DisablePlugin();
        }

        internal static unsafe void Enqueue(string QuestName, uint leveID)
        {
            P.taskManager.Enqueue(() => Turnin(QuestName, leveID), "Turning in Leve", configuration: DConfig);
            P.taskManager.Enqueue(() => !IsAccepted(leveID), "Waiting for leve to not be accepted", configuration: WaitConfig);
            P.taskManager.Enqueue(() => PlayerNotBusy(), "Waiting for player to not be busy", configuration: WaitConfig);
        }

        internal static unsafe bool? Turnin(string QuestName, uint leveID)
        {
            if (!IsAccepted(leveID))
            {
                return true;
            }
            else if (TryGetAddonByName<AtkUnitBase>("SelectIconString", out var QuestAddon) && IsAddonReady(QuestAddon))
            {
                int callback = GetCallback(QuestName);
                if (callback >= 0)
                {
                    notListedSinceMs = 0;
                    if (EzThrottler.Throttle("Selecting Correct Quest Turnin", 2000))
                    {
                        PluginVerbos("Selecting the turnin quest");
                        GenericHandlers.FireCallback("SelectIconString", true, callback);
                    }
                }
                else if (callback == CallbackUnreadable)
                {
                    // 不按、下一個 tick 再讀；一直讀不到時由這一步的 2 分鐘上限停掉。
                    var now = Environment.TickCount64;
                    if (now - lastUnreadableLogMs >= 10_000)
                    {
                        lastUnreadableLogMs = now;
                        Svc.Log.Information($"[ChilledLeves] 交納清單暫時讀不到任何項目，未選擇，稍後再讀（理符「{QuestName}」）。");
                    }
                }
                else if (NotListedConfirmed(QuestName))
                {
                    StopNotListed(QuestName);
                }
            }
            else if (TryGetAddonByName<AtkUnitBase>("Talk", out var TalkAddon) && IsAddonReady(TalkAddon))
            {
                if (EzThrottler.Throttle("Talk Box", 100))
                {
                    PluginVerbos("Closing the Talk box");
                    GenericHandlers.FireCallback("Talk", true);
                }
            }
            else if (TryGetAddonByName<AtkUnitBase>("SelectYesno", out var SelectYes) && IsAddonReady(SelectYes))
            {
                if (EzThrottler.Throttle("SelectYes to HQ", 100))
                {
                    PluginVerbos("Re-confirming that you want to turn in HQ");
                    GenericHandlers.FireCallback("SelectYesno", true, 0);
                }
            }
            else if (TryGetAddonByName<AtkUnitBase>("JournalResult", out var JournalAccept) && IsAddonReady(JournalAccept))
            {
                if (EzThrottler.Throttle("Accepting the journal", 100))
                {
                    PluginVerbos("Accepting the journal results");
                    GenericHandlers.FireCallback("JournalResult", true, 0, 0);
                }
            }
            else if (TryGetAddonByName<AtkUnitBase>("SelectString", out var SelectString) && IsAddonReady(SelectString))
            {
                if (EzThrottler.Throttle("Accepting Multi-Turnin", 200))
                {
                    PluginVerbos("Saying yes to turning in multiple items");
                    GenericHandlers.FireCallback("SelectString", true, 0);
                }
            }

            return false;
        }
    }
}


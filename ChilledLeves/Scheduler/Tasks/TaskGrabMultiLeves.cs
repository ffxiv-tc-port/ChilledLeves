using ChilledLeves.Scheduler.Handlers;
using ChilledLeves.Utilities;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ChilledLeves.Scheduler.Tasks
{
    internal static class TaskGrabMultiLeves
    {
        private static SortedDictionary<uint, string> ViableLeves = new SortedDictionary<uint, string>();

        internal static void Enqueue(uint npcID, int classButton)
        {
            P.taskManager.Enqueue(() => { GuildLeve.LastSelectedLeveId = 0; }, "Reset leve selection");
            TaskInteract.Enqueue(npcID);
            P.taskManager.Enqueue(() => UpdateViableLeves(), "updating Viable Leves");
            P.taskManager.Enqueue(() => GrabLeveMulti(npcID, classButton), "Grabbing multiple leves", DConfig);
            P.taskManager.Enqueue(() => LeaveLeveVendor(npcID), "Leaving Leve Vendor", DConfig);
            P.taskManager.Enqueue(() => PlayerNotBusy(), "Waiting for player to not be busy", DConfig);
        }

        internal static void UpdateViableLeves()
        {
            ViableLeves.Clear();
            foreach (var leve in C.workList)
            {
                // ViableLeves is cleared on entry, so its ContainsKey guard says nothing about
                // LeveDictionary - the two have different key sets (C.workList is persisted
                // config, LeveDictionary is rebuilt from the sheet). Skip what we cannot resolve.
                if (!LeveDictionary.TryGetValue(leve.LeveID, out var leveData))
                    continue;

                if (!ViableLeves.ContainsKey(leve.LeveID))
                {
                    ViableLeves.Add(leve.LeveID, leveData.LeveName);
                }
            }
        }

        internal static unsafe bool? GrabLeveMulti(uint npcID, int classButton)
        {
            bool IsAllAccepted = false;

            if (TryGetAddonByName<AtkUnitBase>("Talk", out var TalkAddon) && IsAddonActive("Talk"))
            {
                if (EzThrottler.Throttle("Talk Box", 100))
                {
                    GenericHandlers.FireCallback("Talk", true, 0);
                }
            }
            else if (TryGetAddonByName<AtkUnitBase>("SelectString", out var LeveWindow) && IsAddonReady(LeveWindow))
            {
                if (EzThrottler.Throttle("Opening the Levequests Window", 100))
                {
                    GenericHandlers.FireCallback("SelectString", true, classButton);
                }
            }
            else if (TryGetAddonByName<AtkUnitBase>("SelectYesno", out var SelectYesno) && IsAddonReady(SelectYesno))
            {
                if (EzThrottler.Throttle("Accepting the leve", 500))
                {
                    GenericHandlers.FireCallback("SelectYesno", true, 0);
                }
            }
            else if (TryGetAddonMaster<GuildLeve>("GuildLeve", out var m) && m.IsAddonReady)
            {
                bool foundLeve = false;

                foreach (var l in m.Levequests)
                {
                    var compareInfo = CultureInfo.InvariantCulture.CompareInfo;
                    var compareOptions = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
                    // 先把這一列對回理符 ID 再查工作清單：同名理符不能只靠名稱挑工作清單裡的第一張。
                    var listedId = ResolveListedLeveId(l.Name, l.Level, npcID);
                    var leveMatch = listedId != 0 && ViableLeves.TryGetValue(listedId, out var viableName)
                        ? new KeyValuePair<uint, string>(listedId, viableName)
                        : default;

                    if (!leveMatch.Equals(default(KeyValuePair<uint, string>))) // If a match is found/that it didn't find one, and you haven't accepted the quest yet
                    {
                        if (LogGate.VerboseEnabled)
                            PluginVerbos($"Leve: {leveMatch.Value} [{leveMatch.Key}] was found");
                        var leveId = leveMatch.Key;
                        var leveName = leveMatch.Value;
                        var itemId = CraftDictionary[leveId].ItemID;
                        var itemAmountRequired = CraftDictionary[leveId].TurninAmount;
                        var itemAmountHave = GetItemCount((int)itemId);

                        // GenericThrottle 必須先判：它是共用的節流計數，關掉記錄也要照常消耗。
                        if (GenericThrottle && LogGate.VerboseEnabled)
                            PluginVerbos($"Leve {leveMatch.Value}: Accepted? {IsAccepted(leveId)}");
                        // if 3 <= 2
                        if (itemAmountRequired <= itemAmountHave && !IsAccepted(leveId) && Allowances > 0)
                        {
                            if (GenericThrottle && LogGate.VerboseEnabled)
                            PluginVerbos($"Leve: {leveName} [{leveId}] has not been picked up yet");

                            foundLeve = true;
                            if (TryGetAddonMaster<AddonMaster.JournalDetail>("JournalDetail", out var det) && det.IsAddonReady)
                            {
                                var currentJournalName = GetNodeText("JournalDetail", 19);
                                if (compareInfo.Compare(currentJournalName, leveName, compareOptions) != 0
                                    || (IsSharedLeveName(leveName) && GuildLeve.LastSelectedLeveId != leveId))
                                {
                                    if (EzThrottler.Throttle("Selecting the Leve"))
                                    {
                                        l.Select(leveId);
                                        PluginVerbos($"Selecting leve: {leveName}");
                                    }
                                }
                                else
                                {
                                    if (EzThrottler.Throttle("Accepting leve", 500))
                                    {
                                        GenericHandlers.FireCallback("JournalDetail", true, 3, (int)leveId);
                                        PluginVerbos($"Accepting the leve: {leveName}");
                                    }
                                }
                                break;
                            }
                        }
                    }
                }

                if (!foundLeve)
                {
                    PluginVerbos("All possible leves at this vendor has been accepted, continuing on w/ the process");
                    IsAllAccepted = true;
                }
            }

            return IsAllAccepted;
        }

        internal static unsafe bool? LeaveLeveVendor(uint npcID)
        {
            var leaveButton = LeveNPCDict[npcID].LeaveButton;

            if (TryGetAddonByName<AtkUnitBase>("SelectString", out var LeveWindow) && IsAddonReady(LeveWindow))
            {
                if (EzThrottler.Throttle("Leaving Leve Person", 500))
                {
                    GenericHandlers.FireCallback("SelectString", true, leaveButton);
                }
            }
            else if (TryGetAddonByName<AtkUnitBase>("GuildLeve", out var GuildLeve) && IsAddonReady(GuildLeve))
            {
                if (EzThrottler.Throttle("Leaving Journal Window", 500))
                {
                    GenericHandlers.FireCallback("GuildLeve", true, -1);
                }
            }
            else if (TryGetAddonByName<AtkUnitBase>("Talk", out var TalkAddon) && IsAddonActive("Talk"))
            {
                if (EzThrottler.Throttle("Talk Box", 100))
                {
                    GenericHandlers.FireCallback("Talk", true, 0);
                }
            }
            else if (PlayerNotBusy())
            {
                return true;
            }

            return false;
        }
    }
}

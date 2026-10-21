using ChilledLeves.Scheduler.Handlers;
using ChilledLeves.Utilities;
using ECommons.Logging;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System.Collections.Generic;
using System.Globalization;

namespace ChilledLeves.Scheduler.Tasks
{
    internal static class TaskGrabLeve
    {
        private static uint FishLeve = 0;

        private static Dictionary<uint, string> FisherDictionary = new Dictionary<uint, string>();

        internal static void Enqueue(uint leveID, uint npcID, int classButton)
        {
            P.taskManager.Enqueue(() => PluginLog.Information($"Leve Type is: {LeveDictionary[leveID].JobAssignmentType}"), "Log Message");
            P.taskManager.Enqueue(() => { GuildLeve.LastSelectedLeveId = 0; }, "Reset leve selection");
            TaskInteract.Enqueue(npcID);
            var jobType = LeveDictionary[leveID].JobAssignmentType;
            P.taskManager.Enqueue(() => PluginLog.Information($"Job Type is: {jobType}"), "Log Message");

            if (jobType == 4)
            {
                P.taskManager.Enqueue(() => PluginLog.Information($"Fisher leve Detected"), "Log Message");
                P.taskManager.Enqueue(() => UpdateFisherLeves(), "Updating fisher Leves");
                P.taskManager.Enqueue(() => GrabFisher((ushort)leveID, npcID, classButton), "Grabbing fisher leves", DConfig);
            }
            else
            {
                P.taskManager.Enqueue(() => PluginLog.Information($"Non Fisher leve Detected"), "Log Message");
                P.taskManager.Enqueue(() => GrabLeve((ushort)leveID, npcID, classButton), "Grab Leve Task", DConfig);
            }
            P.taskManager.Enqueue(() => LeaveLeveVendor(npcID), "leaving Leve Vendor", DConfig);
            P.taskManager.Enqueue(() => PlayerNotBusy(), "Leaving Leve Vendor", DConfig);
        }

        internal static void UpdateFisherLeves()
        {
            FisherDictionary.Clear();
            foreach (var leve in C.workList)
            {
                // FisherDictionary is cleared on entry, so its ContainsKey guard says nothing
                // about LeveDictionary - the two have different key sets (C.workList is
                // persisted config, LeveDictionary is rebuilt from the sheet). Skip anything we
                // cannot resolve instead of throwing out of the task.
                if (!LeveDictionary.TryGetValue(leve.LeveID, out var leveData))
                    continue;

                if (!FisherDictionary.ContainsKey(leve.LeveID) && leveData.JobAssignmentType == 4)
                {
                    FisherDictionary.Add(leve.LeveID, leveData.LeveName);
                }
            }
        }

        internal static unsafe bool? GrabLeve(ushort leveID, uint npcID, int classButton)
        {
            var LeveName = LeveDictionary[leveID].LeveName;
            var craftButton = LeveNPCDict[npcID].CrafterButton;

            if (IsAccepted(leveID))
            {
                return true;
            }
            else if (TryGetAddonByName<AtkUnitBase>("Talk", out var TalkAddon) && IsAddonActive("Talk"))
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
                bool hasLeve = false;
                bool nameOnly = false;
                var compareInfo = CultureInfo.InvariantCulture.CompareInfo;
                var compareOptions = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
                var sharedName = IsSharedLeveName(LeveName);

                foreach (var l in m.Levequests)
                {
                    if (compareInfo.Compare(l.Name, LeveName, compareOptions) != 0)
                        continue;
                    if (ResolveListedLeveId(l.Name, l.Level, npcID) != leveID)
                    {
                        nameOnly = true;
                        continue;
                    }

                    hasLeve = true;
                    if (TryGetAddonMaster<AddonMaster.JournalDetail>("JournalDetail", out var det) && det.IsAddonReady)
                    {
                        var currentJournalName = GetNodeText("JournalDetail", 19);
                        if (compareInfo.Compare(currentJournalName, LeveName, compareOptions) != 0
                            || (sharedName && GuildLeve.LastSelectedLeveId != leveID))
                        {
                            if (EzThrottler.Throttle("Selecting the Leve"))
                                l.Select(leveID);
                        }
                        else
                        {
                            if (EzThrottler.Throttle("Accepting leve"))
                            {
                                GenericHandlers.FireCallback("JournalDetail", true, 3, leveID);
                            }
                        }
                    }
                }
                if (!hasLeve)
                {
                    PluginVerbos($"The following leve: {LeveName} could not be found at the vendor.");
                    PluginVerbos($"This could be due to the following problems: ");
                    PluginVerbos($"1: Leve turnin area not completed (aka missing a quest)");
                    PluginVerbos($"2: It's a gathering leve and the vendor doesn't have this leve currently.");
                    PluginVerbos($"In the case of #2, please do other gathering leves of this type to be able to do this leve potentionally");

                    // 移出的是找不到的那一張，不是清單第一項；停止時 DisablePlugin 會從 ListCycled 放回去。
                    var missing = C.workList.FirstOrDefault(e => e.LeveID == leveID);
                    if (missing != null)
                    {
                        ListCycled.Add(missing);
                        C.workList.Remove(missing);
                    }
                    PluginLog.Information(nameOnly
                        ? $"[ChilledLeves] 理符委託人那裡只有同名的其他理符（或分不出是哪一張），沒有「{LeveName}」（{leveID}），先從工作清單移出這一張，停止時會放回。"
                        : $"[ChilledLeves] 理符委託人那裡找不到「{LeveName}」（{leveID}），先從工作清單移出這一張，停止時會放回。");
                    return true;
                }
            }
            return false;
        }

        internal static unsafe bool? GrabFisher(ushort leveID, uint npcID, int classButton)
        {
            var LeveName = LeveDictionary[leveID].LeveName;
            var craftButton = LeveNPCDict[npcID].CrafterButton;

            if (IsAccepted(leveID) || IsAccepted(FishLeve))
            {
                return true;
            }
            else if (TryGetAddonByName<AtkUnitBase>("Talk", out var TalkAddon) && IsAddonActive("Talk"))
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
            else if (TryGetAddonMaster<GuildLeve>("GuildLeve", out var m) && m.IsAddonReady)
            {
                HashSet<uint> PotentionalLeves = new HashSet<uint>();
                var entries = m.Levequests;
                var entryIds = new uint[entries.Length];

                for (var i = 0; i < entries.Length; i++)
                {
                    var l = entries[i];
                    entryIds[i] = ResolveListedLeveId(l.Name, l.Level, npcID);
                    if (entryIds[i] != 0)
                        PotentionalLeves.Add(entryIds[i]);
                    if (LogGate.DebugEnabled)
                        PluginDebug($"Potentional Leves: {l.Name} [{entryIds[i]}]");
                }

                string? firstLeve = GetFirstLeve(PotentionalLeves);

                if (firstLeve != null)
                {
                    var sharedName = IsSharedLeveName(firstLeve);
                    for (var i = 0; i < entries.Length; i++)
                    {
                        var l = entries[i];
                        if (entryIds[i] == FishLeve)
                        {
                            if (TryGetAddonMaster<AddonMaster.JournalDetail>("JournalDetail", out var det) && det.IsAddonReady)
                            {
                                if (GetNodeText("JournalDetail", 19) != firstLeve
                                    || (sharedName && GuildLeve.LastSelectedLeveId != FishLeve))
                                {
                                    if (EzThrottler.Throttle("Selecting the Leve"))
                                        l.Select(FishLeve);
                                }
                                else if (GetNodeText("JournalDetail", 19) == firstLeve)
                                {
                                    if (EzThrottler.Throttle("Accepting leve"))
                                    {
                                        {
                                            PluginDebug($"Grabbing the leve: {FishLeve}");
                                            GenericHandlers.FireCallback("JournalDetail", true, 3, (int)FishLeve);
                                        }
                                    }
                                }

                            }
                        }
                    }
                }
            }
            return false;
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

        private static string? GetFirstLeve(HashSet<uint> PotentionalLeves)
        {
            var leve = LeveDictionary
                        .Where(kvp => PotentionalLeves.Contains(kvp.Key) && (CraftDictionary[kvp.Key].TurninAmount <= CraftDictionary[kvp.Key].CurrentItemAmount))
                        .FirstOrDefault();

            if (!LeveDictionary.ContainsKey(leve.Key))
            {
                FishLeve = 0;
                return null;
            }
            else
            {
                FishLeve = leve.Key;
                return leve.Value.LeveName;
            }
        }
    }
}

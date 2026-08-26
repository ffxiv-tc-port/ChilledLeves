using ChilledLeves.Scheduler.Handlers;
using ECommons.ExcelServices;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace ChilledLeves.Scheduler.Tasks
{
    internal static class TaskClassChange
    {
        public static void Enqueue(Job jobID)
        {
            P.taskManager.Enqueue(() => ChangeClass(jobID), "Changing Class");
        }

        internal unsafe static bool? ChangeClass(Job jobID)
        {
            if (GetClassJobId() == (uint)jobID)
                return true;

            if (EzThrottler.Throttle("Equipping class"))
            {
                var gearsets = RaptureGearsetModule.Instance();
                // RaptureGearsetModule.Instance() 走 UIModule，未登入／UI 尚未建立時回 null
                //（CS 手寫實作逐字是 uiModule == null ? null : uiModule->GetRaptureGearsetModule()）。
                // 取不到就回 false ＝ 與本方法尾端相同的「這次沒換成，下個 tick 再試」。
                if (gearsets == null)
                    return false;

                foreach (ref var gs in gearsets->Entries)
                {
                    if (!gearsets->IsValidGearset(gs.Id)) continue;
                    if ((Job)gs.ClassJob == jobID)
                    {
                        if (gs.Flags.HasFlag(RaptureGearsetModule.GearsetFlag.MainHandMissing))
                        {
                            if (TryGetAddonByName<AtkUnitBase>("SelectYesno", out var selectYesno) && IsAddonActive("SelectYesno"))
                            {
                                GenericHandlers.FireCallback("SelectYesno", true, 0);
                            }
                            else
                            {
                                gearsets->EquipGearset(gs.Id);
                            }
                        }
                        else
                        {
                            gearsets->EquipGearset(gs.Id);
                        }
                    }
                }
            }

            return false;
        }
    }
}

using ECommons.Automation.NeoTaskManager;
using ECommons.Configuration;
using ChilledLeves.Scheduler;
using ChilledLeves.Ui;
using ChilledLeves.IPC;
using ChilledLeves.Scheduler.Handlers;
using ChilledLeves.Utilities;

namespace ChilledLeves;

public sealed class ChilledLeves : IDalamudPlugin
{
    public string Name => "ChilledLeves";
    internal static ChilledLeves P = null!;
    public static Config C => P.config;
    private Config config;

    // Window's that I use, base window to the settings... need these to actually show shit 
    internal WindowSystem windowSystem;
    internal MainWindow mainWindow;
    internal SettingsWindow settingWindow;
    internal DebugWindow debugWindow;
    internal WorkListUi workListUi;
    internal GatherModeUi gatherModeUi;
    internal AlertWindow alertUi; 
    internal AlertSettings alertSettings;

    // Taskmanager from Ecommons
    internal TaskManager taskManager;

    // Internal IPC's that I use for... well plugins. 
    internal LifestreamIPC lifestream;
    internal NavmeshIPC navmesh;
    internal PandoraIPC pandora;
    internal ArtisanIPC artisan;

#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider adding the 'required' modifier or declaring as nullable.
    public ChilledLeves(IDalamudPluginInterface pi)
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider adding the 'required' modifier or declaring as nullable.
    {
        P = this;
        ECommonsMain.Init(pi, P, ECommons.Module.DalamudReflector, ECommons.Module.ObjectFunctions);
        // 讓「呼叫了對方沒有的 IPC 方法」不再完全靜默。
        // 訂閱越早越好：事件只在 IPC **呼叫**當下才被查閱，在這裡訂閱就涵蓋往後所有呼叫。
        EzIpcFailureLog.Enable();
        ECommons.LanguageHelpers.Localization.Init("ChineseTraditional");
        // AddonPressGuard 的幀時鐘：在這裡訂閱是為了讓它排在本外掛所有 Framework.Update
        // 處理常式的最前面（整條多播委派共用一個 try/catch，前面的人擲例外會讓後面的人
        // 那個 tick 整個不跑）。時鐘停住 = 守衛的逃生口永不到期 = 死鎖。
        Scheduler.Handlers.AddonPressGuard.EnsureClockRunning();
        new ECommons.Schedulers.TickScheduler(Load);
    }

    public void Load()
    {
        EzConfig.Migrate<Config>();
        config = EzConfig.Init<Config>();

        //IPC's that are used
        taskManager = new();
        lifestream = new();
        navmesh = new();
        pandora = new();
        artisan = new();

        // all the windows
        windowSystem = new();
        mainWindow = new();
        settingWindow = new();
        debugWindow = new();
        workListUi = new();
        gatherModeUi = new();
        alertUi = new();
        alertSettings = new();

        taskManager = new(new(abortOnTimeout: true, timeLimitMS: 20000, showDebug: true));
        Svc.PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        Svc.PluginInterface.UiBuilder.OpenMainUi += () =>
        {
            mainWindow.IsOpen = true;
        };
        Svc.PluginInterface.UiBuilder.OpenConfigUi += () =>
        {
            workListUi.IsOpen = true;
        };
        EzCmd.Add("/chilledleves", OnCommand, string.Join("\n",
            "Open plugin interface".Loc(),
            "/chilledleves add [leveID] [amount] - adds the leveID/amount to worklist".Loc(),
            "/chilledleves clear - clears the worklist".Loc(),
            "/chilledleves start | stop - starts/stops the turnin process".Loc(),
            "/chilledleves s|settings - Opens the worklist menu".Loc(),
            "/leveitalone - alias".Loc()));
        EzCmd.Add("/leveitalone", OnCommand);
        Svc.Framework.Update += Tick;
    }

    private void Tick(object _)
    {
        if (SchedulerMain.AreWeTicking && Svc.Objects.LocalPlayer != null)
        {
            SchedulerMain.Tick();
        }
        GenericManager.Tick();
        TextAdvancedManager.Tick();
        YesAlreadyManager.Tick();
        AlignCameraManager.Tick();
        SoundAlert.Tick();
    }

    public void Dispose()
    {
        Safe(() => Svc.Framework.Update -= Tick);
        Safe(() => Svc.PluginInterface.UiBuilder.Draw -= windowSystem.Draw);
        // 拆掉 AddonPressGuard 掛在 AddonLifecycle 上的解除封鎖監聽器，
        // 不留任何指向本組件的委派（否則熱重載後仍會被叫到）。
        Safe(Scheduler.Handlers.AddonPressGuard.ForceTeardown);
        Safe(EzIpcFailureLog.Disable);
        // 🔴 一定要在 ECommonsMain.Dispose() **之前**:還原要靠 EzIPC 的委派打回 vnavmesh,
        //    ECommons 收掉之後那些委派就不保證還能用了(而且失敗是靜默的)。
        Safe(AlignCameraManager.RestoreNow);
        ECommonsMain.Dispose();
        Safe(TextAdvancedManager.UnlockTA);
        Safe(YesAlreadyManager.Unlock);
    }

    private void OnCommand(string command, string args)
    {
        var subcommands = args.Split(' ');

        if (subcommands.Length == 0 || args == "")
        {
            mainWindow.IsOpen = !mainWindow.IsOpen;
            return;
        }

        var firstArg = subcommands[0];

        if (firstArg.ToLower() == "d" || firstArg.ToLower() == "debug")
        {
            debugWindow.IsOpen = true;
            return;
        }
        else if (firstArg.ToLower() == "s" || firstArg.ToLower() == "settings")
        {
            workListUi.IsOpen = true;
            return;
        }
        else if (firstArg.ToLower() == "add")
        {
            // 上面只擋了 Length == 0,而 "add" 需要再吃兩個參數。
            // 少打參數(例如只輸入 /chilledleves add)會直接越界擲 IndexOutOfRangeException。
            if (subcommands.Length < 3)
            {
                PluginVerbos("用法:/chilledleves add <理符ID> <次數1-100>");
                return;
            }

            string secondCommand = subcommands[1];
            string thirdCommand = subcommands[2];
            uint leveId = 0;
            int repeatAmount = 0;
            if (int.TryParse(secondCommand, out int value) && LeveDictionary.ContainsKey((uint)value))
            {
                leveId = (uint)value;
                if (int.TryParse(thirdCommand, out int repeat) && (repeat > 0 && repeat <= 100))
                {
                    repeatAmount = repeat;
                    if (!C.workList.Any(e => e.LeveID == leveId))
                    {
                        C.workList.Add(new LeveEntry { LeveID = leveId, InputValue = repeatAmount});
                        C.Save();
                        return;
                    }
                }
                else
                {
                    PluginVerbos($"{repeat} is not a valud input for the amount that you would like to do. Please input between 1-100");
                    return;
                }
            }
            else
            {
                PluginVerbos($"The leve you tried adding isn't a valid leveID: {subcommands[1]}");
                return;
            }
        }
        else if (firstArg.ToLower() == "clear")
        {
            C.workList.Clear();
            C.Save();
            PluginVerbos("Cleared the worklist of all leves");
            return;
        }
        else if (firstArg.ToLower() == "start")
        {
            // 上游 20165e2 是把 WorkListMode = true 塞進 EnablePlugin() 本體(commit 訊息自稱 "temp")。
            // 我方不照抄:採集模式啟動鈕是「WorkListMode = false → GatheringMode = true → EnablePlugin()」,
            // 那樣改會讓 EnablePlugin() 反手把剛設成 false 的 WorkListMode 又打開,
            // 而 Tick() 的分派是 if (WorkListMode) ... else if (GatheringMode) —— 採集模式會靜默永遠跑不到。
            // 改在呼叫端設定,與其餘三個工作清單啟動點(MainWindow / WorkListUi ×2)寫法一致。
            SchedulerMain.WorkListMode = true;
            SchedulerMain.EnablePlugin();
            PluginVerbos("Starting the turnin process");
            return;
        }
        else if (firstArg.ToLower() == "stop")
        {
            SchedulerMain.DisablePlugin();
            PluginVerbos("Sopping the turnin process");
            return;
        }
        else
        {
            PluginVerbos($"Length of array is: {subcommands.Length} & no matching description. Can't do command");
            PluginVerbos($"Command: -{command}- args?:-{args}-");
            return;
        }
    }
}

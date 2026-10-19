using ECommons.EzIpcManager;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

#nullable disable
namespace ChilledLeves.IPC
{
    public class LifestreamIPC
    {
        public const string Name = "Lifestream";
        // 這裡絕對不能指國際服的外掛庫：那些庫裡的 Lifestream 內部名與台服版完全相同，
        // 加進去會把 API15 的版本裝進台服環境並撞同一個已安裝鍵。一律指本艦隊的 feed。
        public const string Repo = "https://raw.githubusercontent.com/ffxiv-tc-port/DalamudPluginsTC/main/repo.json";
        public LifestreamIPC() => EzIPC.Init(this, Name, SafeWrapper.AnyException);

        [EzIPC] public Func<string, bool> AethernetTeleport;
        [EzIPC] public Func<uint, byte, bool> Teleport;
        [EzIPC] public Func<bool> TeleportToHome;
        [EzIPC] public Func<bool> TeleportToFC;
        [EzIPC] public Func<bool> TeleportToApartment;
        [EzIPC] public Func<bool> IsBusy;
        [EzIPC] public Action<string> ExecuteCommand;
    }
}
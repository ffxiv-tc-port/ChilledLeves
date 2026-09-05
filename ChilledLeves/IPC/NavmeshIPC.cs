using ECommons.EzIpcManager;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

#nullable disable
namespace ChilledLeves.IPC;

public class NavmeshIPC
{
    public const string Name = "vnavmesh";
    // 這裡絕對不能指國際服的外掛庫：那裡的 vnavmesh 內部名與台服版完全相同，
    // 加進去會把 API15 的版本裝進台服環境並撞同一個已安裝鍵。一律指本艦隊的 feed。
    public const string Repo = "https://raw.githubusercontent.com/ffxiv-tc-port/DalamudPluginsTC/main/repo.json";
    public NavmeshIPC() => EzIPC.Init(this, Name, SafeWrapper.AnyException);
    public static bool Installed => HasPlugin(Name);
    public bool Ready
    {
        get
        {
            try
            {
                return Installed && IsReady();
            }
            catch
            {
                return false;
            }
        }
    }

    [EzIPC("Nav.%m")] public readonly Func<bool> IsReady;
    [EzIPC("Nav.%m")] public readonly Func<float> BuildProgress;
    [EzIPC("Nav.%m")] public readonly Func<bool> Reload;
    [EzIPC("Nav.%m")] public readonly Func<bool> Rebuild;
    // 🔴 簽章必須與提供端一致:vnavmesh 的 Nav.Pathfind 是
    //    NavmeshManager.QueryPathBasic ⇒ Task<List<Vector3>>,不是 Vector3。
    //    型別對不上時 Dalamud 擲 IpcTypeMismatchError,而 SafeWrapper.IPCException 只攔
    //    IpcNotReadyError ⇒ 一旦有人加上呼叫點就會每次呼叫都擲例外。目前零呼叫點,現在改零風險。
    [EzIPC("Nav.%m")] public readonly Func<Vector3, Vector3, bool, Task<List<Vector3>>> Pathfind;

    [EzIPC("SimpleMove.%m")] public readonly Func<Vector3, bool, bool> PathfindAndMoveTo;
    [EzIPC("SimpleMove.%m")] public readonly Func<bool> PathfindInProgress;

    [EzIPC("Path.%m")] public readonly Action<List<Vector3>, bool> MoveTo;
    [EzIPC("Path.%m")] public readonly Action Stop;
    // 🔑 GetAlignCamera 是 AlignCameraManager 拍快照用的——沒有 getter 就只能單向寫、永不還原。
    [EzIPC("Path.%m")] public readonly Func<bool> GetAlignCamera;
    [EzIPC("Path.%m")] public readonly Action<bool> SetAlignCamera;
    [EzIPC("Path.%m")] public readonly Func<bool> IsRunning;

    [EzIPC("Query.Mesh.%m")] public readonly Func<Vector3, float, float, Vector3?> NearestPoint;
    [EzIPC("Query.Mesh.%m")] public readonly Func<Vector3, bool, float, Vector3?> PointOnFloor;
}
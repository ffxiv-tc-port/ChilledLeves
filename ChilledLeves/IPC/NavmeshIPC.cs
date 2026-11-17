using ECommons.EzIpcManager;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

#nullable disable
namespace ChilledLeves.IPC;

public class NavmeshIPC : IVnavPort
{
    public const string Name = "vnavmesh";
    // 這裡絕對不能指國際服的外掛庫：那裡的 vnavmesh 內部名與台服版完全相同，
    // 加進去會把 API15 的版本裝進台服環境並撞同一個已安裝鍵。一律指本艦隊的 feed。
    public const string Repo = "https://raw.githubusercontent.com/ffxiv-tc-port/DalamudPluginsTC/main/repo.json";
    public NavmeshIPC()
    {
        EzIPC.Init(this, Name, SafeWrapper.AnyException);
        own = new VnavOwnPath(this, () => Environment.TickCount64);
    }
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
    // 🔴 簽章必須與提供端一致：vnavmesh 的 Nav.Pathfind 回 Task<List<Vector3>>，不是 Vector3。
    //    型別對不上時 Dalamud 擲 IpcTypeMismatchError，本檔的 SafeWrapper.AnyException 會吞掉並回 default ⇒ 靜默失效。
    [EzIPC("Nav.%m")] public readonly Func<Vector3, Vector3, bool, Task<List<Vector3>>> Pathfind;

    [EzIPC("SimpleMove.%m")] public readonly Func<Vector3, bool, bool> PathfindAndMoveTo;
    [EzIPC("SimpleMove.%m")] public readonly Func<bool> PathfindInProgress;
    // 取消還在算的尋路（SimpleMove 算完才開始走，算的期間只送 Path.Stop 停不住）。舊版沒有這個端點時由 SafeWrapper 吞掉。
    [EzIPC("Nav.%m")] public readonly Action PathfindCancelAll;
    // SafeWrapper 下端點不存在時回 0／null：0＝退回改前「自己送的還在算就取消」，null＝VnavOwnPath 照契約信旗標。
    [EzIPC("Nav.%m")] public readonly Func<int> PathfindNumQueued;
    [EzIPC("Path.%m")] public readonly Func<List<Vector3>> ListWaypoints;

    /// <summary>ChilledLeves 自己送出的移動。只在框架執行緒讀寫。</summary>
    private readonly VnavOwnPath own;

    /// <summary><see cref="PathfindAndMoveTo"/> 送出成功後立刻呼叫（同一步）。</summary>
    internal void NoteOwnIssued(Vector3 destination) => own.NoteIssued(destination);

    /// <summary>每幀呼叫：自己那一筆走完或被別人換掉就放手。沒送過時不做任何 IPC。</summary>
    internal void ObserveOwn() => own.Observe();

    /// <summary>只停 ChilledLeves 自己送出、還沒結束的移動；別人的路線或尋路不碰。流程自己停手時用。</summary>
    internal VnavOwnPath.StopResult StopOwn() => own.StopOwn();

    bool IVnavPort.PathIsRunning() => IsRunning();
    bool IVnavPort.SimpleMoveInProgress() => PathfindInProgress();
    int IVnavPort.NavPathfindNumQueued() => PathfindNumQueued();
    IReadOnlyList<Vector3> IVnavPort.PathWaypoints() => ListWaypoints();
    void IVnavPort.PathStop() => Stop();
    void IVnavPort.NavPathfindCancelAll() => PathfindCancelAll();

    [EzIPC("Path.%m")] public readonly Action<List<Vector3>, bool> MoveTo;
    [EzIPC("Path.%m")] public readonly Action Stop;
    // 🔑 GetAlignCamera 是 AlignCameraManager 拍快照用的——沒有 getter 就只能單向寫、永不還原。
    [EzIPC("Path.%m")] public readonly Func<bool> GetAlignCamera;
    [EzIPC("Path.%m")] public readonly Action<bool> SetAlignCamera;
    [EzIPC("Path.%m")] public readonly Func<bool> IsRunning;

    [EzIPC("Query.Mesh.%m")] public readonly Func<Vector3, float, float, Vector3?> NearestPoint;
    [EzIPC("Query.Mesh.%m")] public readonly Func<Vector3, bool, float, Vector3?> PointOnFloor;
}
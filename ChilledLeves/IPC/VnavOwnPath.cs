// 抄自 docs/architecture/templates/vnav-stop-own-path.md 的 VnavOwnPath 片段（決策表由 vnav_own_path_template_probe 驗）。
// 跨 repo 同步：各 repo 各帶一份（ICE、ChilledLeves…），改一份就同步其他份：grep -rl "class VnavOwnPath"。
using System;
using System.Collections.Generic;
using System.Numerics;

namespace ChilledLeves.IPC;

// 本檔契約：只在框架執行緒呼叫；沒發起過時 NoteIssued 以外的成員不做任何 IPC；
// vnavmesh 沒裝或端點擲例外一律當「問不到」，結果是不動手（停不到比停錯人安全）。

/// <summary>vnavmesh IPC 的最小介面；ChilledLeves 由 <see cref="NavmeshIPC"/> 實作。</summary>
internal interface IVnavPort
{
    bool PathIsRunning();                       // Path.IsRunning（還有路徑點＝在走）
    bool SimpleMoveInProgress();                // SimpleMove.PathfindInProgress（SimpleMove 在算或排隊）
    int NavPathfindNumQueued();                 // Nav.PathfindNumQueued（進行中那筆以外還在排隊的筆數）
    IReadOnlyList<Vector3>? PathWaypoints();    // Path.ListWaypoints（剩下的路徑點；端點不存在回 null）
    void PathStop();                            // Path.Stop（全域）
    void NavPathfindCancelAll();                // Nav.PathfindCancelAll（全域，只取消尋路、不動已在走的路徑）
}

internal sealed class VnavOwnPath
{
    /// <summary>路線終點離自己最後送的目的地多近（只比 X/Z）才算自己的；vnavmesh 會把離網格的目的地落到旁邊，也有 CloseTo 的容許距離。</summary>
    public const float OwnPathMatchRadius = 20f;

    /// <summary>送出後超過這麼久還在「算路」就不再當成自己的（算路通常只要幾秒）。</summary>
    public const long MaxComputeMs = 30_000;

    private readonly IVnavPort port;
    private readonly Func<long> nowMs;
    private bool issued;
    private Vector3 destination;
    private long issuedAtMs;

    public VnavOwnPath(IVnavPort port, Func<long> nowMs)
    {
        this.port = port;
        this.nowMs = nowMs;
    }

    public bool HasIssued => issued;

    public readonly record struct StopResult(bool HadIssued, bool CancelledPathfind, bool StoppedPath, string Why);

    /// <summary>剛呼叫完 SimpleMove.PathfindAndMoveTo／Path.MoveTo 就呼叫（同一步，中間不要 return）。</summary>
    public void NoteIssued(Vector3 dest)
    {
        issued = true;
        destination = dest;
        issuedAtMs = nowMs();
    }

    /// <summary>每幀呼叫（放在 Player.Available 判斷外面：跨區時路徑照樣會結束）。沒發起過時第一行就回去。</summary>
    public void Observe()
    {
        if (!issued)
            return;
        try
        {
            var computing = port.SimpleMoveInProgress();
            var running = port.PathIsRunning();
            if (!computing && !running)
            {
                issued = false; // 看到「沒在算也沒在走」＝自己這一筆結束了，之後看到的任何移動都是別人的
                return;
            }

            if (!computing && running && !EndsNearDestination(port.PathWaypoints()))
                issued = false; // 路徑被別人換掉了：放手
        }
        catch (Exception)
        {
            issued = false;
        }
    }

    /// <summary>停自己發起的移動。沒發起過、或證據不足時不碰 vnavmesh。</summary>
    public StopResult StopOwn()
    {
        if (!issued)
            return new(false, false, false, "沒有自己發起的移動");
        issued = false; // 這次決定不論結果都結案

        bool computing, running;
        var queued = 0;
        IReadOnlyList<Vector3>? waypoints = null;
        try
        {
            computing = port.SimpleMoveInProgress();
            running = port.PathIsRunning();
            if (computing)
                queued = port.NavPathfindNumQueued();
            if (running)
                waypoints = port.PathWaypoints();
        }
        catch (Exception)
        {
            return new(true, false, false, "問不到 vnavmesh，不動手");
        }

        // vnavmesh 沒有逐筆取消：算路階段只有在「只剩這一筆尋路」而且還在合理時間內才全域取消。
        var cancel = computing && queued == 0 && nowMs() - issuedAtMs <= MaxComputeMs;
        // 在走的路徑：終點對得上自己最後送的目的地才停；讀不到路徑點時信旗標（端點不存在的舊版）。
        var stop = running && (waypoints == null || waypoints.Count == 0 || EndsNearDestination(waypoints));
        try
        {
            if (cancel)
                port.NavPathfindCancelAll();
            if (stop)
                port.PathStop();
        }
        catch (Exception)
        {
            return new(true, false, false, "送出時擲例外");
        }

        return new(true, cancel, stop, cancel || stop ? "已停自己的移動" : "目前在走／在算的不是自己的，不停");
    }

    /// <summary>使用者按下停止／急停：刻意全停，不受「只停自己」限制。呼叫處要在註解寫明是哪個使用者動作。</summary>
    public static void StopAllByUser(IVnavPort port)
    {
        try { port.NavPathfindCancelAll(); } catch (Exception) { }
        try { port.PathStop(); } catch (Exception) { }
    }

    private bool EndsNearDestination(IReadOnlyList<Vector3>? waypoints)
    {
        if (waypoints == null || waypoints.Count == 0)
            return true; // 讀不到＝不能否認是自己的（旗標還在才會走到這裡）
        var end = waypoints[waypoints.Count - 1];
        var dx = end.X - destination.X;
        var dz = end.Z - destination.Z;
        return dx * dx + dz * dz <= OwnPathMatchRadius * OwnPathMatchRadius;
    }
}

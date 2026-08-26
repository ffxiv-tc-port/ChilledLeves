using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ChilledLeves.Scheduler.Handlers;

internal static unsafe class PlayerHandlers
{
    public static float Distance(this Vector3 v, Vector3 v2)
    {
        return new Vector2(v.X - v2.X, v.Z - v2.Z).Length();
    }
    /// <summary>
    /// 🔴 <c>AgentMap.Instance()</c> 由 <c>[Agent(AgentId.Map)]</c> 產生:內部鏈
    /// AgentModule → UIModule → Framework,任一層回 null 整條就回 null(登入前、切場景、
    /// 登出後都是常態),底層 <c>[StaticAddress]</c>／<c>[MemberFunction]</c> 特徵碼失配時
    /// 則改為擲 <c>InvalidOperationException</c>——兩種失效模式並存,缺一等於假防護。
    /// 裸解參考 null 原生指標是 AccessViolationException,在 .NET Core 屬 corrupted-state
    /// exception,<c>try/catch</c> 完全攔不到 ⇒ 只能事前判空。
    /// 本函式跑在排程 tick(高頻),因此不寫 log;取不到 agent 時回 <c>true</c>
    /// (fail-closed:唯一的呼叫端 TaskNavMoveTo 看到「還在移動」會回 false 繼續等,
    /// 而不是誤判成「已停下」往下推進)。
    /// </summary>
    public static unsafe bool IsMoving()
    {
        AgentMap* agent;
        try
        {
            agent = AgentMap.Instance();
        }
        catch
        {
            return true;
        }

        return agent == null || agent->IsPlayerMoving;
    }
}

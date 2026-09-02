using Dalamud.Memory;
using ECommons.Automation;
using ECommons.Logging;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.UI;
using Lumina.Excel.Sheets;
using System.Collections.Generic;
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;
using Callback = ECommons.Automation.Callback;

namespace ChilledLeves.Utilities;

// Grabbed from Lim's Battlevest here:
// https://github.com/NightmareXIV/Battlevest/blob/main/Battlevest/GuildLeve.cs#L28
// (Thank you lim for this, this saves a lot of headache)

public unsafe class GuildLeve : AddonMasterBase<AddonGuildLeve>
{
    public GuildLeve(nint addon) : base(addon)
    {
    }

    public GuildLeve(void* addon) : base(addon)
    {
    }

    /// <summary>
    /// 取出第 index 個 AtkValue,取不到就回 null。
    /// AtkValues 是原生指標陣列,沒有 C# 陣列的 Length 可以靠 —— 只驗 != null 等於沒驗上界,
    /// 越界讀到的是堆積垃圾而不是 null,再把它當字串指標解參考就是攔不到的 AccessViolationException。
    /// 上界的真值來源是 AtkUnitBase.AtkValuesCount(FieldOffset 0x1E2),本來就是為此存在的。
    /// </summary>
    private FFXIVClientStructs.FFXIV.Component.GUI.AtkValue* GetAtkValue(int index)
    {
        var addon = Addon;
        if (addon == null)
            return null;

        var values = addon->AtkValues;
        if (values == null || index < 0 || index >= addon->AtkValuesCount)
            return null;

        return &values[index];
    }

    /// <summary>
    /// 讀出字串型 AtkValue 的內容;型別不符或指標為空就回 null。
    /// 型別白名單沿用本檔 Levequests 原有的寫法。
    /// </summary>
    private static string? ReadAtkString(FFXIVClientStructs.FFXIV.Component.GUI.AtkValue* value)
    {
        if (value == null || !value->Type.EqualsAny(ValueType.String, ValueType.ManagedString, ValueType.String8))
            return null;

        var ptr = value->String.Value;
        return ptr == null ? null : MemoryHelper.ReadSeStringNullTerminated((nint)ptr).GetText();
    }

    public uint NumEntries
    {
        get
        {
            // .UInt 只是讀 union 欄位、不解參考,所以這裡刻意不加型別檢查以維持原本行為;
            // 唯一補的是「索引在不在陣列內」。取不到時回 0 = 沒有任何條目,呼叫端的迴圈自然不跑。
            var value = GetAtkValue(25);
            return value == null ? 0u : value->UInt;
        }
    }

    public string SelectedLeve => ReadAtkString(GetAtkValue(1233)) ?? string.Empty;

    public Levequest[] Levequests
    {
        get
        {
            var ret = new List<Levequest>();
            for (var i = 0; i < NumEntries; i++)
            {
                var leveName = ReadAtkString(GetAtkValue(626 + i * 2));
                if (leveName == null)
                {
                    // 原本的寫法在型別不符時 break,這裡把「索引越界/addon 消失」也歸到同一條路徑:
                    // 一律停止走訪,不回傳半截資料。
                    break;
                }

                var leve = new Levequest(this, i)
                {
                    Name = leveName
                };
                leve.Level = ReadAtkString(GetAtkValue(627 + i * 2));
                ret.Add(leve);
            }
            return [.. ret];
        }
    }

    public override string AddonDescription { get; }

    public class Levequest(GuildLeve master, int index)
    {
        public string Name;
        public string? Level;

        public void Select()
        {
            var quest = Svc.Data.GetExcelSheet<Leve>().FirstOrNull(x => x.Name.GetText() == Name);
            if (quest == null)
            {
                PluginLog.Error($"Failed to select levequest, requested name not found: {Name}");
            }
            else
            {
                var rowId = (int)quest?.RowId;

                // 🔴 這條 Callback.Fire 不經 GenericHandlers.FireCallback，要自己過同一道閘門，
                //    否則對「正在關閉中」的理符清單再送一發就是攔不到的原生存取違規。
                //    按下「選取」只是換選取項、視窗不會消失 ⇒ 照 (index, 理符 ID) 分開記，
                //    否則連續接多張理符會被自己擋住；而呼叫端本來就是「選到對的那張為止」的
                //    重試迴圈（外面只有一把 500ms 節流）⇒ 用短逃生口。
                if (!global::ChilledLeves.Scheduler.Handlers.AddonPressGuard.TryBeginPress(
                        "GuildLeve", master.Base, $"13/{index}/{rowId}",
                        terminating: false, routineRePress: true))
                    return;

                Callback.Fire(master.Base, true, 13, index, rowId);
            }
        }
    }
}

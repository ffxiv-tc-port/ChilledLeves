using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ChilledLeves.Utilities;

/// <summary>
/// 理符委託人／交納清單只給名稱時，把名稱對回理符 ID。
/// 同名多列時依呼叫端條件（委託人、清單上的等級）挑，挑不出唯一就回 0，絕不取第一列。
/// 不碰 Dalamud：離線探針直接編這個檔。
/// </summary>
public static class LeveNameMatch
{
    /// <summary>與委託人清單既有的名稱比對同一套規則（忽略大小寫與附加符號）。</summary>
    public static readonly StringComparer NameComparer =
        StringComparer.Create(CultureInfo.InvariantCulture, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace);

    public readonly record struct Row(uint Id, string Name, uint Level);

    public static Dictionary<string, List<Row>> BuildIndex(IEnumerable<Row> rows)
    {
        var index = new Dictionary<string, List<Row>>(NameComparer);
        foreach (var row in rows)
        {
            if (!index.TryGetValue(row.Name, out var list))
                index[row.Name] = list = new List<Row>(1);
            list.Add(row);
        }
        return index;
    }

    /// <param name="preferredIds">同名時優先的 ID（例如這位委託人發的理符）；null＝不篩。</param>
    /// <returns>唯一挑中的 ID；0＝沒有這個名稱，或同名分不出（<paramref name="nameMatches"/> &gt; 1）。</returns>
    public static uint Pick(Dictionary<string, List<Row>> index, string shownName, string? shownLevel,
                            uint[]? preferredIds, out int nameMatches)
    {
        if (!index.TryGetValue(shownName, out var same))
        {
            nameMatches = 0;
            return 0;
        }
        nameMatches = same.Count;
        return Pick(same, shownLevel, preferredIds);
    }

    /// <param name="sameName">名稱已經相等的候選列。</param>
    public static uint Pick(IReadOnlyList<Row> sameName, string? shownLevel, uint[]? preferredIds)
    {
        if (sameName.Count == 1)
            return sameName[0].Id;
        if (sameName.Count == 0)
            return 0;

        IReadOnlyList<Row> pool = sameName;
        if (preferredIds != null)
        {
            var atVendor = sameName.Where(r => Array.IndexOf(preferredIds, r.Id) >= 0).ToList();
            if (atVendor.Count == 1)
                return atVendor[0].Id;
            if (atVendor.Count > 1)
                pool = atVendor;
        }

        if (TryParseLevel(shownLevel, out var level))
        {
            var atLevel = pool.Where(r => r.Level == level).ToList();
            if (atLevel.Count == 1)
                return atLevel[0].Id;
        }
        return 0;
    }

    /// <summary>取文字裡第一段連續數字（全形數字也算）；沒有數字回 false。</summary>
    public static bool TryParseLevel(string? text, out uint level)
    {
        level = 0;
        if (string.IsNullOrEmpty(text))
            return false;

        var seen = false;
        foreach (var c in text)
        {
            var digit = char.IsDigit(c) ? (int)char.GetNumericValue(c) : -1;
            if (digit < 0)
            {
                if (seen)
                    break;
                continue;
            }
            seen = true;
            if (level > 100_000)
                return false;
            level = level * 10 + (uint)digit;
        }
        return seen;
    }
}

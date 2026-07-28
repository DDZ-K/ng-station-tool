namespace NgStationTool;

/// <summary>
/// 产线料号白名单：产品 DMC（一级文件夹名）只要包含任一维护料号即视为本产线产品。
/// 料号一般约 10 位，DMC 更长；匹配为子串包含（忽略大小写）。
/// 列表为空或全是空白时 = 不过滤（兼容旧配置 / 自检）。
/// </summary>
public static class PartNumberRules
{
    public static IEnumerable<string> Normalize(IEnumerable<string>? partNumbers)
    {
        if (partNumbers == null) yield break;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in partNumbers)
        {
            var pn = (raw ?? "").Trim();
            if (pn.Length == 0) continue;
            if (seen.Add(pn)) yield return pn;
        }
    }

    public static bool IsServedProduct(string? productDmc, IEnumerable<string>? partNumbers)
    {
        var dmc = (productDmc ?? "").Trim();
        if (dmc.Length == 0) return false;

        var list = Normalize(partNumbers).ToList();
        if (list.Count == 0) return true; // 未维护料号 → 不过滤

        return list.Any(pn => dmc.IndexOf(pn, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    public static bool TryFindMatchedPart(string? productDmc, IEnumerable<string>? partNumbers, out string matched)
    {
        matched = "";
        var dmc = (productDmc ?? "").Trim();
        if (dmc.Length == 0) return false;
        foreach (var pn in Normalize(partNumbers))
        {
            if (dmc.IndexOf(pn, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                matched = pn;
                return true;
            }
        }
        return false;
    }
}

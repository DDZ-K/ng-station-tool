using System.Collections.Concurrent;

namespace NgStationTool.Services;

public sealed class NgPendingItem
{
    public string ImageName { get; init; } = "";
    public string ProductDmc { get; init; } = "";
    public string StagedPath { get; init; } = "";
    public DateTime EnqueuedAt { get; init; } = DateTime.Now;
}

/// <summary>图片已改名并进入 A，但尚未由 XML identifier 确认的待 NG 队列。</summary>
public sealed class NgPendingQueue
{
    private readonly ConcurrentDictionary<string, NgPendingItem> _items =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly AppLogger _log;

    public NgPendingQueue(AppLogger log) => _log = log;

    public event Action? Changed;
    public int Count => _items.Count;

    public bool Enqueue(string imageName, string productDmc, string stagedPath)
    {
        imageName = (imageName ?? "").Trim();
        productDmc = (productDmc ?? "").Trim();
        if (imageName.Length == 0 || productDmc.Length == 0 || string.IsNullOrWhiteSpace(stagedPath)) return false;
        var item = new NgPendingItem
        {
            ImageName = imageName,
            ProductDmc = productDmc,
            StagedPath = stagedPath,
            EnqueuedAt = DateTime.Now
        };
        if (!_items.TryAdd(imageName, item))
        {
            _log.Skip("待NG", $"图片已在队列，忽略重复: {imageName}");
            return false;
        }
        _log.Info("待NG", $"入队 产品DMC={productDmc} 图片={imageName} A={stagedPath}");
        Changed?.Invoke();
        return true;
    }

    public List<NgPendingItem> Snapshot() => _items.Values.OrderBy(x => x.EnqueuedAt).ToList();

    public List<NgPendingItem> SnapshotByProduct(string productDmc)
    {
        var key = (productDmc ?? "").Trim();
        if (key.Length == 0) return new List<NgPendingItem>();

        // 统一走 ProductDmcMatchesIdentifier：精确 / 正反面 000·001 / 站位后缀
        return _items.Values
            .Where(x => ProductDmcMatchesIdentifier(x.ProductDmc, key))
            .OrderBy(x => x.EnqueuedAt)
            .ToList();
    }

    /// <summary>
    /// 正反面扫码差异：DMC 固定从 0 起第 14～16 位（下标 13，长度 3）为 000 或 001，
    /// 其余位相同即视为同一件。写死，不开放配置。
    /// 例：图 6916500051066001… 与报文 6916500051066000… 互认。
    /// </summary>
    public const int DualFaceMarkerOffset = 13;
    public const int DualFaceMarkerLength = 3;

    /// <summary>
    /// identifier 与产品文件夹名是否视为同一件。
    /// 1) 精确相等
    /// 2) 正反面 000/001 互认（仅固定偏移处）
    /// 3) 文件夹 = 上述任一主体 + '_' / '-' 等站位后缀（如 _S1）
    /// </summary>
    public static bool ProductDmcMatchesIdentifier(string productDmc, string identifier)
    {
        productDmc = (productDmc ?? "").Trim();
        identifier = (identifier ?? "").Trim();
        if (productDmc.Length == 0 || identifier.Length == 0) return false;

        if (string.Equals(productDmc, identifier, StringComparison.OrdinalIgnoreCase))
            return true;

        if (AreDualFaceEquivalent(productDmc, identifier))
            return true;

        // 站位后缀：产品文件夹比 identifier（或其 000/001 变体）更长
        foreach (var idCore in DualFaceVariants(identifier))
        {
            if (productDmc.Length <= idCore.Length) continue;
            if (!productDmc.StartsWith(idCore, StringComparison.OrdinalIgnoreCase)) continue;
            var next = productDmc[idCore.Length];
            if (next is '_' or '-' or ' ' or '.')
                return true;
        }

        return false;
    }

    /// <summary>两串在去掉/对齐正反面标记后是否同一件（等长、仅允许 000↔001）。</summary>
    public static bool AreDualFaceEquivalent(string a, string b)
    {
        a = (a ?? "").Trim();
        b = (b ?? "").Trim();
        if (a.Length == 0 || b.Length == 0) return false;
        if (a.Length != b.Length) return false;
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;

        var need = DualFaceMarkerOffset + DualFaceMarkerLength;
        if (a.Length < need) return false;

        var aMark = a.Substring(DualFaceMarkerOffset, DualFaceMarkerLength);
        var bMark = b.Substring(DualFaceMarkerOffset, DualFaceMarkerLength);
        if (!IsDualFaceMarker(aMark) || !IsDualFaceMarker(bMark))
            return false;

        // 标记位之前、之后必须完全一致（忽略大小写）
        if (!string.Equals(a[..DualFaceMarkerOffset], b[..DualFaceMarkerOffset], StringComparison.OrdinalIgnoreCase))
            return false;
        var tailStart = DualFaceMarkerOffset + DualFaceMarkerLength;
        return string.Equals(a[tailStart..], b[tailStart..], StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsDualFaceMarker(string marker) =>
        string.Equals(marker, "000", StringComparison.OrdinalIgnoreCase)
        || string.Equals(marker, "001", StringComparison.OrdinalIgnoreCase);

    /// <summary>若核心串在固定位是 000/001，返回自身 + 另一面；否则仅自身。</summary>
    public static IEnumerable<string> DualFaceVariants(string core)
    {
        core = (core ?? "").Trim();
        if (core.Length == 0) yield break;
        yield return core;

        var need = DualFaceMarkerOffset + DualFaceMarkerLength;
        if (core.Length < need) yield break;

        var mark = core.Substring(DualFaceMarkerOffset, DualFaceMarkerLength);
        string? other = null;
        if (string.Equals(mark, "000", StringComparison.OrdinalIgnoreCase)) other = "001";
        else if (string.Equals(mark, "001", StringComparison.OrdinalIgnoreCase)) other = "000";
        if (other == null) yield break;

        var alt = string.Concat(
            core.AsSpan(0, DualFaceMarkerOffset),
            other,
            core.AsSpan(DualFaceMarkerOffset + DualFaceMarkerLength));
        if (!string.Equals(alt, core, StringComparison.OrdinalIgnoreCase))
            yield return alt;
    }

    public bool Remove(string imageName)
    {
        var removed = _items.TryRemove((imageName ?? "").Trim(), out _);
        if (removed) Changed?.Invoke();
        return removed;
    }

    public void ClearAll(string reason)
    {
        _items.Clear();
        _log.Warn("待NG", "清空全部: " + reason);
        Changed?.Invoke();
    }
}

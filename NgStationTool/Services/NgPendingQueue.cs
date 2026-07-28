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

        // 1) 精确匹配：文件夹名 == XML identifier
        var exact = _items.Values
            .Where(x => string.Equals(x.ProductDmc, key, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.EnqueuedAt)
            .ToList();
        if (exact.Count > 0) return exact;

        // 2) 宽松匹配：产线文件夹常带站位后缀，如 identifier=DMC123，文件夹=DMC123_S1
        //    仅允许「文件夹以 identifier + 分隔符开头」，避免短串误伤。
        return _items.Values
            .Where(x => ProductDmcMatchesIdentifier(x.ProductDmc, key))
            .OrderBy(x => x.EnqueuedAt)
            .ToList();
    }

    /// <summary>
    /// identifier 与产品文件夹名是否视为同一件。
    /// 精确相等，或文件夹 = identifier + '_'/' -' 后缀（站位/夹号）。
    /// </summary>
    public static bool ProductDmcMatchesIdentifier(string productDmc, string identifier)
    {
        productDmc = (productDmc ?? "").Trim();
        identifier = (identifier ?? "").Trim();
        if (productDmc.Length == 0 || identifier.Length == 0) return false;
        if (string.Equals(productDmc, identifier, StringComparison.OrdinalIgnoreCase)) return true;
        if (productDmc.Length <= identifier.Length) return false;
        if (!productDmc.StartsWith(identifier, StringComparison.OrdinalIgnoreCase)) return false;
        var next = productDmc[identifier.Length];
        return next is '_' or '-' or ' ' or '.';
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

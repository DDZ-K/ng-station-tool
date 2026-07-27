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
        => _items.Values.Where(x => string.Equals(x.ProductDmc, productDmc?.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.EnqueuedAt).ToList();

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

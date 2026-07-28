using System.Collections.Concurrent;
using System.Xml.Linq;

namespace NgStationTool.Services;

/// <summary>监听 XML 报文，以 partReceived@identifier 匹配产品 DMC，驱动 A→B 和队列晋级。</summary>
public sealed class XmlDmcGateService : IDisposable
{
    private readonly AppLogger _log;
    private readonly Func<AppConfig> _cfg;
    private readonly NgPendingQueue _ngQueue;
    private readonly Action<string, string, string> _onPromoted;
    private readonly ConcurrentQueue<string> _queue = new();
    private readonly ConcurrentDictionary<string, long> _handledWrites = new(StringComparer.OrdinalIgnoreCase);
    private readonly AutoResetEvent _signal = new(false);
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _cts;
    private Task? _worker;
    private int _running;

    public XmlDmcGateService(AppLogger log, Func<AppConfig> cfg, NgPendingQueue ngQueue,
        Action<string, string, string> onPromoted)
    {
        _log = log;
        _cfg = cfg;
        _ngQueue = ngQueue;
        _onPromoted = onPromoted;
    }

    public bool IsRunning => Volatile.Read(ref _running) == 1;

    public void Start()
    {
        if (IsRunning) return;
        var cfg = _cfg();
        Directory.CreateDirectory(cfg.XmlWatchRoot);
        Directory.CreateDirectory(cfg.XmlArchiveRoot);
        Directory.CreateDirectory(cfg.JudgingRoot);
        _handledWrites.Clear();
        _cts = new CancellationTokenSource();
        _worker = Task.Factory.StartNew(() => WorkerLoop(_cts.Token), TaskCreationOptions.LongRunning);
        _watcher = new FileSystemWatcher(cfg.XmlWatchRoot, "*.xml")
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
            InternalBufferSize = 32 * 1024
        };
        _watcher.Created += OnXml;
        _watcher.Changed += OnXml;
        _watcher.Renamed += OnRenamed;
        _watcher.EnableRaisingEvents = true;
        Volatile.Write(ref _running, 1);
        _log.Success("报文", $"监视中 XML={cfg.XmlWatchRoot} | B={cfg.JudgingRoot} | 归档={cfg.XmlArchiveRoot}");
    }

    private void OnXml(object? sender, FileSystemEventArgs e)
    {
        if (!string.Equals(Path.GetExtension(e.FullPath), ".xml", StringComparison.OrdinalIgnoreCase)) return;
        _queue.Enqueue(e.FullPath);
        _signal.Set();
    }

    private void OnRenamed(object? sender, RenamedEventArgs e) => OnXml(sender,
        new FileSystemEventArgs(WatcherChangeTypes.Created, Path.GetDirectoryName(e.FullPath) ?? "", Path.GetFileName(e.FullPath)));

    private void WorkerLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            while (_queue.TryDequeue(out var path))
            {
                if (token.IsCancellationRequested) break;
                try { ProcessXml(path); }
                catch (Exception ex) { _log.Error("报文", $"处理失败 {path}: {ex.Message}"); }
            }
            _signal.WaitOne(300);
        }
    }

    private void ProcessXml(string path)
    {
        if (!File.Exists(path)) return;
        long write;
        try { write = File.GetLastWriteTimeUtc(path).Ticks; } catch { return; }
        if (_handledWrites.TryGetValue(path, out var previous) && previous >= write) return;

        var cfg = _cfg();
        var ready = FileReady.WaitReady(path, cfg.XmlReadyBudgetMs, 2, 100, 100, 20, 1, false);
        if (!ready.Ok) return;

        string identifier;
        try
        {
            var doc = XDocument.Load(path, LoadOptions.None);
            identifier = doc.Descendants().FirstOrDefault(x =>
                string.Equals(x.Name.LocalName, "partReceived", StringComparison.OrdinalIgnoreCase))
                ?.Attribute("identifier")?.Value?.Trim() ?? "";
        }
        catch (Exception ex)
        {
            _log.Warn("报文", $"XML 解析失败，归档未匹配: {Path.GetFileName(path)} | {ex.Message}");
            ArchiveReport(path, matched: false, "无有效identifier");
            return;
        }

        if (identifier.Length == 0)
        {
            _log.Warn("报文", $"XML 无 identifier，归档未匹配: {Path.GetFileName(path)}");
            ArchiveReport(path, matched: false, "无有效identifier");
            _handledWrites[path] = write;
            return;
        }

        var pending = _ngQueue.SnapshotByProduct(identifier);
        if (pending.Count == 0)
        {
            // 诊断：列出当前待NG 的产品DMC，方便对照 identifier 是否差后缀/大小写
            var all = _ngQueue.Snapshot();
            var products = all.Select(x => x.ProductDmc).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToList();
            _log.Warn("报文",
                $"identifier={identifier} 不在待NG队列（当前待NG={all.Count} 产品=[{string.Join(", ", products)}]）");
            ArchiveReport(path, matched: false, $"identifier={identifier} 不在待NG队列");
            _handledWrites[path] = write;
            return;
        }

        var promoted = 0;
        foreach (var item in pending)
        {
            try
            {
                if (!File.Exists(item.StagedPath))
                {
                    _log.Warn("报文", $"A 图片不存在，保留待NG: {item.StagedPath}");
                    continue;
                }
                var day = DateTime.Now;
                var dayDir = Path.Combine(cfg.JudgingRoot, day.ToString("yyyy"), day.ToString("MM"), day.ToString("dd"));
                Directory.CreateDirectory(dayDir);
                var destination = UniquePath(dayDir, Path.GetFileName(item.StagedPath));
                File.Move(item.StagedPath, destination);
                _ngQueue.Remove(item.ImageName);
                _onPromoted(item.ImageName, destination, item.ProductDmc);
                promoted++;
                _log.Success("报文",
                    $"identifier={identifier} 命中产品DMC={item.ProductDmc}：A→B {item.ImageName} → {destination}");
            }
            catch (Exception ex)
            {
                _log.Error("报文", $"A→B 失败 图片={item.ImageName}: {ex.Message}");
            }
        }

        var matched = promoted > 0;
        ArchiveReport(path, matched, matched ? $"命中{promoted}张" : $"identifier={identifier} A→B失败0张");
        _handledWrites[path] = write;
    }

    private void ArchiveReport(string path, bool matched, string detail)
    {
        if (!File.Exists(path)) return;
        var cfg = _cfg();
        var category = matched ? "已匹配" : "未匹配";
        var dir = Path.Combine(cfg.XmlArchiveRoot, category);
        Directory.CreateDirectory(dir);
        var destination = UniquePath(dir, Path.GetFileName(path));
        File.Move(path, destination);
        _log.Info("报文", $"{category}归档（{detail}）→ {destination}");
    }

    private static string UniquePath(string dir, string fileName)
    {
        var path = Path.Combine(dir, fileName);
        if (!File.Exists(path)) return path;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        return Path.Combine(dir, $"{stem}_{DateTime.Now:yyyyMMddHHmmssfff}{ext}");
    }

    public void Stop()
    {
        Volatile.Write(ref _running, 0);
        try { if (_watcher != null) { _watcher.EnableRaisingEvents = false; _watcher.Created -= OnXml; _watcher.Changed -= OnXml; _watcher.Renamed -= OnRenamed; _watcher.Dispose(); } } catch { }
        _watcher = null;
        try { _cts?.Cancel(); _signal.Set(); _worker?.Wait(2000); } catch { }
        _cts?.Dispose(); _cts = null; _worker = null;
    }

    public void Dispose() => Stop();
}

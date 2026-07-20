using System.Collections.Concurrent;
using System.Diagnostics;

namespace NgStationTool.Services;

/// <summary>
/// 一级子目录图片 → 等文件夹内「本会话新图」静默后，一次性全部改名复制到输出目录。
/// 源文件不动；每张成功拷贝各自入 DMC 缓存（改名完整名）。
/// </summary>
public sealed class ImageCopyWatcher : IDisposable
{
    private readonly AppLogger _log;
    private readonly Func<AppConfig> _cfg;
    private readonly Action<string, string, string>? _onCopiedRenamedDmc; // renamedStem, outputPath, folderKey
    private readonly ConcurrentDictionary<string, byte> _copiedOk = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>一级文件夹 → 最后活动时间 TickCount64</summary>
    private readonly ConcurrentDictionary<string, long> _folderTouch = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>正在整批处理的文件夹，防重入</summary>
    private readonly ConcurrentDictionary<string, byte> _folderBusy = new(StringComparer.OrdinalIgnoreCase);
    private readonly AutoResetEvent _signal = new(false);
    private CancellationTokenSource? _cts;
    private Task? _worker;
    private int _running;
    private DateTime _startedAtUtc;
    private FileSystemWatcher? _watcher;

    public bool IsRunning => Volatile.Read(ref _running) == 1;
    public string? LastError { get; private set; }

    public ImageCopyWatcher(AppLogger log, Func<AppConfig> cfg, Action<string, string, string>? onCopiedRenamedDmc = null)
    {
        _log = log;
        _cfg = cfg;
        _onCopiedRenamedDmc = onCopiedRenamedDmc;
    }

    public void Start()
    {
        if (IsRunning) return;
        var cfg = _cfg();
        if (!cfg.EnableImageCopy)
        {
            _log.Info("图片", "模块已关闭（EnableImageCopy=false）");
            return;
        }

        try
        {
            Directory.CreateDirectory(cfg.WatchRoot);
            Directory.CreateDirectory(cfg.OutputRoot);

            if (PathsEqualOrNested(cfg.WatchRoot, cfg.OutputRoot))
            {
                LastError = "WatchRoot 与 OutputRoot 不能相同或互相包含";
                _log.Error("图片", LastError);
                return;
            }

            _cts = new CancellationTokenSource();
            _copiedOk.Clear();
            _folderTouch.Clear();
            _folderBusy.Clear();
            _startedAtUtc = DateTime.UtcNow;
            _worker = Task.Factory.StartNew(() => WorkerLoop(_cts.Token), TaskCreationOptions.LongRunning);

            _watcher = new FileSystemWatcher(cfg.WatchRoot)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName
                               | NotifyFilters.LastWrite
                               | NotifyFilters.Size
                               | NotifyFilters.CreationTime,
                InternalBufferSize = 64 * 1024
            };
            _watcher.Created += OnFsEvent;
            _watcher.Changed += OnFsEvent;
            _watcher.Renamed += OnRenamed;
            _watcher.Error += OnWatcherError;
            _watcher.EnableRaisingEvents = true;

            Volatile.Write(ref _running, 1);
            LastError = null;
            _log.Success("图片",
                $"监视中（整夹批处理）WatchRoot={cfg.WatchRoot} → OutputRoot={cfg.OutputRoot} | " +
                $"静默={cfg.FolderSettleMs}ms 批超时={cfg.BatchMaxWaitMs}ms startedAtUtc={_startedAtUtc:O}");
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _log.Error("图片", "启动失败: " + ex.Message);
            Stop();
        }
    }

    public void Stop()
    {
        Volatile.Write(ref _running, 0);
        try
        {
            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Created -= OnFsEvent;
                _watcher.Changed -= OnFsEvent;
                _watcher.Renamed -= OnRenamed;
                _watcher.Error -= OnWatcherError;
                _watcher.Dispose();
                _watcher = null;
            }
        }
        catch { /* ignore */ }

        try { _cts?.Cancel(); } catch { /* ignore */ }
        try { _signal.Set(); } catch { /* ignore */ }
        try { _worker?.Wait(3000); } catch { /* ignore */ }
        _cts?.Dispose();
        _cts = null;
        _worker = null;
        _log.Info("图片", "已停止");
    }

    private void OnFsEvent(object sender, FileSystemEventArgs e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(e.FullPath)) return;
            if (Directory.Exists(e.FullPath)) return;
            TouchFromFilePath(e.FullPath);
        }
        catch { /* 事件回调绝不抛 */ }
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(e.FullPath)) return;
            if (Directory.Exists(e.FullPath)) return;
            TouchFromFilePath(e.FullPath);
        }
        catch { /* ignore */ }
    }

    private void TouchFromFilePath(string path)
    {
        var cfg = _cfg();
        if (IsUnder(cfg.OutputRoot, path)) return;
        if (!cfg.ImageExtSet().Contains(Path.GetExtension(path))) return;

        var folder = GetFirstLevelFolder(cfg.WatchRoot, path);
        if (folder == null) return;
        if (cfg.OnlyDirectImages)
        {
            var parent = Path.GetDirectoryName(path);
            if (parent == null || !PathsEqual(parent, folder)) return;
        }

        // 只关心本会话新图；旧图事件忽略
        if (File.Exists(path) && !IsSessionFreshFile(path)) return;

        _folderTouch[folder] = Environment.TickCount64;
        _signal.Set();
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        var ex = e.GetException();
        _log.Error("图片", "FileSystemWatcher 错误: " + (ex?.Message ?? "unknown") + "，尝试重建");
        try
        {
            Stop();
            Thread.Sleep(500);
            Start();
        }
        catch (Exception ex2)
        {
            _log.Error("图片", "重建失败: " + ex2.Message);
        }
    }

    private void WorkerLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var cfg = _cfg();
                var idleMs = Math.Max(100, cfg.FolderSettleMs); // 静默多久算「夹内拍完」
                var now = Environment.TickCount64;

                foreach (var kv in _folderTouch.ToArray())
                {
                    if (token.IsCancellationRequested) break;
                    var folder = kv.Key;
                    var last = kv.Value;
                    if (now - last < idleMs) continue;
                    if (!_folderTouch.TryRemove(folder, out _)) continue;
                    if (!_folderBusy.TryAdd(folder, 1)) continue;

                    try { ProcessFolderBatch(folder, cfg); }
                    catch (Exception ex) { _log.Error("图片", $"整夹处理异常 {folder}: {ex.Message}"); }
                    finally { _folderBusy.TryRemove(folder, out _); }
                }

                _signal.WaitOne(200);
            }
            catch (Exception ex)
            {
                _log.Error("图片", "工作线程: " + ex.Message);
                Thread.Sleep(200);
            }
        }
    }

    /// <summary>
    /// 整夹批处理：收集本会话未拷贝新图 → 等全部就绪 → 一次性全部复制到输出 → 逐张入 DMC。
    /// </summary>
    private void ProcessFolderBatch(string folder, AppConfig cfg)
    {
        if (!Directory.Exists(folder)) return;

        var folderName = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(folderName)) return;

        // 1) 列本会话新图且未成功拷过
        var candidates = new List<string>();
        try
        {
            foreach (var f in Directory.EnumerateFiles(folder))
            {
                if (!cfg.ImageExtSet().Contains(Path.GetExtension(f))) continue;
                if (_copiedOk.ContainsKey(f)) continue;
                if (!IsSessionFreshFile(f)) continue;
                candidates.Add(f);
            }
        }
        catch (Exception ex)
        {
            _log.Warn("图片", $"枚举文件夹失败 {folder}: {ex.Message}");
            return;
        }

        if (candidates.Count == 0) return;

        _log.Info("图片", $"整夹待处理 文件夹={folderName} 候选={candidates.Count} 静默已到，开始等全部就绪…");

        // 2) 等全部就绪（或批超时后只处理已就绪的）
        var batchDeadline = Environment.TickCount64 + Math.Max(cfg.ReadyBudgetMs, cfg.BatchMaxWaitMs);
        var readyFiles = new List<(string path, long length, long readyMs)>();
        var pending = new HashSet<string>(candidates, StringComparer.OrdinalIgnoreCase);

        while (pending.Count > 0 && Environment.TickCount64 < batchDeadline)
        {
            // 静默期内又有新图？则中止本批，重新计时（由 Touch 再次入队）
            if (_folderTouch.ContainsKey(folder))
            {
                _log.Info("图片", $"整夹等待中又有新图，推迟批次: {folderName}");
                // 把剩余候选留给下次
                return;
            }

            foreach (var path in pending.ToArray())
            {
                if (!File.Exists(path))
                {
                    pending.Remove(path);
                    continue;
                }
                if (_copiedOk.ContainsKey(path))
                {
                    pending.Remove(path);
                    continue;
                }

                var ready = FileReady.WaitReady(
                    path,
                    Math.Min(cfg.ReadyBudgetMs, 500),
                    cfg.SizeStableChecks,
                    cfg.SizeStableIntervalMs,
                    cfg.RetryDelayMs,
                    Math.Min(cfg.MaxRetries, 4),
                    cfg.MinImageBytes,
                    requireImageMagic: true);

                if (ready.Ok)
                {
                    readyFiles.Add((path, ready.Length, ready.Ms));
                    pending.Remove(path);
                }
            }

            if (pending.Count > 0)
                Thread.Sleep(Math.Max(50, cfg.SizeStableIntervalMs));
        }

        // 批超时仍未就绪的记日志
        foreach (var p in pending)
            _log.Skip("图片", $"整夹超时仍未就绪，跳过: {p}");

        if (readyFiles.Count == 0)
        {
            _log.Warn("图片", $"整夹无就绪图片: {folderName}");
            return;
        }

        // 3) 一次性全部拷到输出（同一日期目录）
        // 用第一张源路径取日期夹；DateFolderFrom=Now 时都是今天
        var dayDir = GetOutputDayDirectory(cfg, readyFiles[0].path);
        Directory.CreateDirectory(dayDir);

        _log.Info("图片", $"整夹开始拷贝 文件夹={folderName} 张数={readyFiles.Count} → {dayDir}");

        var okCount = 0;
        var dmcList = new List<string>();
        var swAll = Stopwatch.StartNew();

        foreach (var item in readyFiles)
        {
            var path = item.path;
            try
            {
                if (!File.Exists(path)) continue;
                if (_copiedOk.ContainsKey(path)) continue;

                // 再确认一次未被占用
                if (!FileReady.IsUnlocked(path) || !FileReady.HasImageMagic(path))
                {
                    _log.Skip("图片", $"拷贝前复查失败: {path}");
                    continue;
                }

                var targetName = folderName + "_" + Path.GetFileName(path);
                var targetPath = Path.Combine(dayDir, targetName);

                if (cfg.SkipIfSameSizeExists && File.Exists(targetPath))
                {
                    try
                    {
                        var dstLen = new FileInfo(targetPath).Length;
                        if (dstLen == item.length)
                        {
                            _copiedOk[path] = 1;
                            _log.Skip("图片", $"同大小已存在(整夹) | {path} → {targetPath}");
                            // 整夹场景：同大小跳过仍视为本张已处理，但通常不重复入缓存
                            continue;
                        }
                    }
                    catch { /* fallthrough */ }
                    targetPath = GetUniqueTargetPath(dayDir, targetName);
                }
                else if (File.Exists(targetPath))
                {
                    targetPath = GetUniqueTargetPath(dayDir, targetName);
                }

                File.Copy(path, targetPath, overwrite: false);
                _copiedOk[path] = 1;
                okCount++;

                var renamedStem = Path.GetFileNameWithoutExtension(targetPath);
                dmcList.Add(renamedStem);
                _log.Success("图片",
                    $"整夹拷贝成功 ready={item.readyMs}ms | {path} → {targetPath}");

                if (cfg.EnableCloudRelease && cfg.EnqueueFromImageCopyFolderName)
                    _onCopiedRenamedDmc?.Invoke(renamedStem, targetPath, folderName);
            }
            catch (Exception ex)
            {
                _log.Error("图片", $"整夹单张失败 {path}: {ex.Message}");
            }
        }

        _log.Success("图片",
            $"整夹完成 文件夹={folderName} 成功={okCount}/{readyFiles.Count} " +
            $"耗时={swAll.ElapsedMilliseconds}ms DMC数={dmcList.Count} " +
            (dmcList.Count > 0 ? ("[" + string.Join(", ", dmcList) + "]") : ""));
    }

    private bool IsSessionFreshFile(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return false;
            var threshold = _startedAtUtc.AddSeconds(-3);
            return fi.CreationTimeUtc >= threshold || fi.LastWriteTimeUtc >= threshold;
        }
        catch { return false; }
    }

    private static string? GetFirstLevelFolder(string watchRoot, string path)
    {
        try
        {
            var rootFull = Path.GetFullPath(watchRoot).TrimEnd('\\', '/');
            var full = Path.GetFullPath(path).TrimEnd('\\', '/');
            if (full.Equals(rootFull, StringComparison.OrdinalIgnoreCase)) return null;
            var prefix = rootFull + Path.DirectorySeparatorChar;
            var prefix2 = rootFull + Path.AltDirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && !full.StartsWith(prefix2, StringComparison.OrdinalIgnoreCase))
                return null;
            var rel = full.Substring(rootFull.Length).TrimStart('\\', '/');
            if (string.IsNullOrEmpty(rel)) return null;
            var first = rel.Split(new[] { '\\', '/' }, 2)[0];
            if (string.IsNullOrEmpty(first)) return null;
            return Path.Combine(rootFull, first);
        }
        catch { return null; }
    }

    private static string GetOutputDayDirectory(AppConfig cfg, string sourcePath)
    {
        if (!cfg.UseDateFolders) return cfg.OutputRoot;
        var dt = DateTime.Now;
        var from = (cfg.DateFolderFrom ?? "Now").Trim();
        if (string.Equals(from, "LastWriteTime", StringComparison.OrdinalIgnoreCase))
        {
            try { dt = File.GetLastWriteTime(sourcePath); }
            catch { dt = DateTime.Now; }
        }
        var style = (cfg.DateFolderStyle ?? "ymd").Trim();
        if (string.Equals(style, "dash", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(cfg.OutputRoot, dt.ToString("yyyy-MM-dd"));
        return Path.Combine(cfg.OutputRoot, dt.ToString("yyyy"), dt.ToString("MM"), dt.ToString("dd"));
    }

    private static string GetUniqueTargetPath(string directory, string fileName)
    {
        var candidate = Path.Combine(directory, fileName);
        if (!File.Exists(candidate)) return candidate;
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 1; i <= 9999; i++)
        {
            candidate = Path.Combine(directory, $"{baseName} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        return Path.Combine(directory, $"{baseName}_{DateTime.Now:yyyyMMddHHmmssfff}{ext}");
    }

    private static bool PathsEqual(string a, string b)
        => string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'),
            Path.GetFullPath(b).TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);

    private static bool IsUnder(string root, string path)
    {
        try
        {
            var r = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            var p = Path.GetFullPath(path);
            return p.StartsWith(r, StringComparison.OrdinalIgnoreCase)
                   || PathsEqual(root, path);
        }
        catch { return false; }
    }

    private static bool PathsEqualOrNested(string a, string b)
    {
        try
        {
            if (PathsEqual(a, b)) return true;
            return IsUnder(a, b) || IsUnder(b, a);
        }
        catch { return true; }
    }

    public void Dispose() => Stop();
}

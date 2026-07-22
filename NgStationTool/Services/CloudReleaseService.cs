using System.Collections.Concurrent;
using System.Text;

namespace NgStationTool.Services;

/// <summary>
/// 1) 可选：监视 NG 图目录 → DMC 入缓存
/// 2) 监视云端 log → 缓存有 DMC 才按键，然后出队
/// 3) 无缓存的 log 不执行
/// </summary>
public sealed class CloudReleaseService : IDisposable
{
    private readonly AppLogger _log;
    private readonly Func<AppConfig> _cfg;
    private readonly DmcPendingCache _cache;
    private readonly KeyboardService _keyboard;
    private readonly Func<bool>? _canPressKeys; // null/true=可按

    private FileSystemWatcher? _imgWatcher;
    private FileSystemWatcher? _logWatcher;
    private readonly ConcurrentQueue<string> _imgQ = new();
    private readonly ConcurrentQueue<string> _logQ = new();
    private readonly ConcurrentDictionary<string, byte> _processedLogs = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>曾出现超时的文件夹组：该组不再按回车。</summary>
    private readonly ConcurrentDictionary<string, byte> _folderHadTimeout = new(StringComparer.OrdinalIgnoreCase);
    private readonly AutoResetEvent _signal = new(false);
    private CancellationTokenSource? _cts;
    private Task? _worker;
    private int _running;

    public bool IsRunning => Volatile.Read(ref _running) == 1;
    public string? LastError { get; private set; }

    public CloudReleaseService(
        AppLogger log,
        Func<AppConfig> cfg,
        DmcPendingCache cache,
        KeyboardService keyboard,
        Func<bool>? canPressKeys = null)
    {
        _log = log;
        _cfg = cfg;
        _cache = cache;
        _keyboard = keyboard;
        _canPressKeys = canPressKeys;
    }

    /// <summary>同文件夹组全部结束（回车成功/跳过/超时）时通知，用于会话串行。</summary>
    public event Action<string, string>? FolderGroupFinished;

    public void EnqueueDmc(string dmc, string source, string? path = null, string? folderKey = null)
        => _cache.TryEnqueue(dmc, source, path, folderKey);

    public void Start()
    {
        if (IsRunning) return;
        var cfg = _cfg();
        if (!cfg.EnableCloudRelease)
        {
            _log.Info("放行", "模块已关闭（EnableCloudRelease=false）");
            return;
        }

        try
        {
            Directory.CreateDirectory(cfg.CloudLogRoot);
            if (!string.IsNullOrWhiteSpace(cfg.CloudLogArchiveRoot))
                Directory.CreateDirectory(cfg.CloudLogArchiveRoot);

            _cts = new CancellationTokenSource();
            _processedLogs.Clear();
            _folderHadTimeout.Clear();
            _worker = Task.Factory.StartNew(() => WorkerLoop(_cts.Token), TaskCreationOptions.LongRunning);

            _logWatcher = CreateWatcher(cfg.CloudLogRoot, OnLogFs, recursive: false);

            var enableNgImg = cfg.EnqueueFromNgImageWatch;
            if (enableNgImg)
            {
                // 禁止把输出目录当 NG 监视源：会把「已改名图」文件名整段当 DMC 再入队
                if (IsSameOrUnder(cfg.NgImageRoot, cfg.OutputRoot) || IsSameOrUnder(cfg.OutputRoot, cfg.NgImageRoot))
                {
                    _log.Warn("放行",
                        "已关闭 NG 图目录监视：NgImageRoot 与 OutputRoot 相同或互相包含，避免改名图二次入缓存。DMC 请用「复制成功→子文件夹名」入队。");
                    enableNgImg = false;
                }
            }

            if (enableNgImg)
            {
                Directory.CreateDirectory(cfg.NgImageRoot);
                _imgWatcher = CreateWatcher(cfg.NgImageRoot, OnImgFs, recursive: true);
            }

            Volatile.Write(ref _running, 1);
            LastError = null;
            _log.Success("放行",
                $"运行中 log={cfg.CloudLogRoot} ngImg={(enableNgImg ? cfg.NgImageRoot : "关闭")} copyEnqueue={cfg.EnqueueFromImageCopyFolderName} 超时={cfg.PendingTimeoutSec}s");
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _log.Error("放行", "启动失败: " + ex.Message);
            Stop();
        }
    }

    public void Stop()
    {
        Volatile.Write(ref _running, 0);
        DisposeWatcher(ref _logWatcher, OnLogFs);
        DisposeWatcher(ref _imgWatcher, OnImgFs);
        try { _cts?.Cancel(); } catch { /* ignore */ }
        try { _signal.Set(); } catch { /* ignore */ }
        try { _worker?.Wait(2000); } catch { /* ignore */ }
        _cts?.Dispose();
        _cts = null;
        _worker = null;
        _log.Info("放行", "已停止");
    }

    private FileSystemWatcher CreateWatcher(string root, FileSystemEventHandler handler, bool recursive)
    {
        var w = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = recursive,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
            InternalBufferSize = 64 * 1024
        };
        w.Created += handler;
        w.Changed += handler;
        w.Renamed += (_, e) => handler(w, new FileSystemEventArgs(WatcherChangeTypes.Created, Path.GetDirectoryName(e.FullPath) ?? root, e.Name ?? Path.GetFileName(e.FullPath)));
        w.Error += (_, e) =>
        {
            _log.Error("放行", "Watcher 错误: " + (e.GetException()?.Message ?? "") + "，重建");
            try { Stop(); Thread.Sleep(500); Start(); } catch (Exception ex) { _log.Error("放行", ex.Message); }
        };
        w.EnableRaisingEvents = true;
        return w;
    }

    private void DisposeWatcher(ref FileSystemWatcher? w, FileSystemEventHandler handler)
    {
        try
        {
            if (w == null) return;
            w.EnableRaisingEvents = false;
            w.Created -= handler;
            w.Changed -= handler;
            w.Dispose();
            w = null;
        }
        catch { /* ignore */ }
    }

    private void OnImgFs(object sender, FileSystemEventArgs e)
    {
        try
        {
            if (File.Exists(e.FullPath))
            {
                _imgQ.Enqueue(e.FullPath);
                _signal.Set();
            }
        }
        catch { /* ignore */ }
    }

    private void OnLogFs(object sender, FileSystemEventArgs e)
    {
        try
        {
            if (File.Exists(e.FullPath))
            {
                _logQ.Enqueue(e.FullPath);
                _signal.Set();
            }
        }
        catch { /* ignore */ }
    }

    private void WorkerLoop(CancellationToken token)
    {
        var lastPurge = Environment.TickCount64;
        var lastScan = Environment.TickCount64;
        while (!token.IsCancellationRequested)
        {
            try
            {
                // 超时清缓存 → 匹配 log 归档；同文件夹组若已全部结束则回车
                if (Environment.TickCount64 - lastPurge > 1000)
                {
                    lastPurge = Environment.TickCount64;
                    var timedOut = _cache.PurgeExpired();
                    var folderKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var item in timedOut)
                    {
                        try { ArchiveLogsContainingDmc(item.Dmc, "timeout"); }
                        catch (Exception ex) { _log.Warn("放行", $"超时归档 log 失败 DMC={item.Dmc}: {ex.Message}"); }
                        if (!string.IsNullOrWhiteSpace(item.FolderKey))
                        {
                            folderKeys.Add(item.FolderKey);
                            _folderHadTimeout[item.FolderKey] = 1;
                            _log.Warn("放行", $"文件夹组={item.FolderKey} 发生超时，本组结束后不按回车");
                        }
                    }
                    foreach (var fk in folderKeys)
                        MaybePressEnterForFolder(fk, "timeout");
                }

                // 有待确认 DMC 时周期性扫 log 目录（文件名可能是「前缀+DMC」；也防漏事件）
                if (_cache.Count > 0 && Environment.TickCount64 - lastScan > 800)
                {
                    lastScan = Environment.TickCount64;
                    try { ScanLogDirForPending(); } catch (Exception ex) { _log.Warn("放行", "扫 log 目录: " + ex.Message); }
                }

                while (_imgQ.TryDequeue(out var img))
                {
                    if (token.IsCancellationRequested) break;
                    try { HandleNgImage(img); } catch (Exception ex) { _log.Error("放行", "NG图: " + ex.Message); }
                }

                while (_logQ.TryDequeue(out var logPath))
                {
                    if (token.IsCancellationRequested) break;
                    try { HandleLog(logPath); } catch (Exception ex) { _log.Error("放行", "Log: " + ex.Message); }
                }

                _signal.WaitOne(400);
            }
            catch (Exception ex)
            {
                _log.Error("放行", "工作线程: " + ex.Message);
                Thread.Sleep(200);
            }
        }
    }

    private void ScanLogDirForPending()
    {
        var cfg = _cfg();
        if (!Directory.Exists(cfg.CloudLogRoot)) return;
        foreach (var f in Directory.EnumerateFiles(cfg.CloudLogRoot))
        {
            try
            {
                if (!cfg.LogExtSet().Contains(Path.GetExtension(f))) continue;
                if (!string.IsNullOrWhiteSpace(cfg.CloudLogArchiveRoot) && IsUnder(cfg.CloudLogArchiveRoot, f)) continue;
                var fullKey = Path.GetFullPath(f);
                if (_processedLogs.ContainsKey(fullKey)) continue;
                // 文件名是否包含任一待确认 DMC
                if (FindPendingDmcContainedInFileName(f) == null) continue;
                _logQ.Enqueue(f);
            }
            catch { /* next */ }
        }
        _signal.Set();
    }

    private void HandleNgImage(string path)
    {
        var cfg = _cfg();
        if (!cfg.EnqueueFromNgImageWatch) return;
        if (!File.Exists(path)) return;
        if (!cfg.ImageExtSet().Contains(Path.GetExtension(path))) return;

        var ready = FileReady.WaitReady(
            path,
            cfg.ReadyBudgetMs,
            cfg.SizeStableChecks,
            cfg.SizeStableIntervalMs,
            cfg.RetryDelayMs,
            cfg.MaxRetries,
            cfg.MinImageBytes,
            requireImageMagic: true);
        if (!ready.Ok) return;

        var dmc = ExtractDmcFromImage(cfg, path);
        if (string.IsNullOrEmpty(dmc)) return;
        _cache.TryEnqueue(dmc, "NgImage", path);
    }

    private void HandleLog(string path)
    {
        var cfg = _cfg();
        if (!File.Exists(path)) return;

        var ext = Path.GetExtension(path);
        if (!cfg.LogExtSet().Contains(ext)) return;

        if (path.EndsWith(".done", StringComparison.OrdinalIgnoreCase)) return;
        if (!string.IsNullOrWhiteSpace(cfg.CloudLogArchiveRoot)
            && IsUnder(cfg.CloudLogArchiveRoot, path)) return;

        var fullKey = Path.GetFullPath(path);
        if (_processedLogs.ContainsKey(fullKey)) return;

        var ready = FileReady.WaitReady(
            path,
            cfg.LogReadyBudgetMs,
            sizeStableChecks: 2,
            sizeStableIntervalMs: 100,
            retryDelayMs: 100,
            maxRetries: 20,
            minBytes: 1,
            requireImageMagic: false);
        if (!ready.Ok) return;

        string text;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            text = sr.ReadToEnd();
        }
        catch
        {
            return; // 下次再试
        }

        // ★ 文件名包含缓存中的 DMC（支持 前缀_DMC / 任意包含）
        var dmc = FindPendingDmcContainedInFileName(path);
        if (string.IsNullOrEmpty(dmc))
        {
            // 无待确认匹配：不标记 processed，等以后有缓存再扫
            _log.Skip("放行", $"log 文件名未包含任何待确认 DMC，暂忽略: {Path.GetFileName(path)}");
            return;
        }

        if (!TryExtractDecision(text, cfg, out var token, out var decision, out var parseNote))
        {
            _log.Warn("放行", $"结果未就绪/未识别 ({parseNote}) DMC={dmc} | {path}");
            // 不标记 processed，内容可能还在写
            return;
        }

        var keyName = decision == "OK" ? cfg.OkKey : cfg.NokKey;
        _log.Info("放行", $"命中 DMC={dmc} 结果={decision} token={token} log={Path.GetFileName(path)} → {keyName}");

        if (_canPressKeys != null && !_canPressKeys())
        {
            _log.Warn("放行", $"HARAN 未处于 Waiting，暂不按键，保留缓存 DMC={dmc}");
            return;
        }

        var okPress = _keyboard.SendKey(
            keyName,
            cfg.KeyRepeatCount,
            cfg.KeyPressDelayMs,
            string.IsNullOrWhiteSpace(cfg.TargetWindowTitleContains) ? null : cfg.TargetWindowTitleContains,
            string.IsNullOrWhiteSpace(cfg.TargetProcessName) ? null : cfg.TargetProcessName,
            cfg.ActivateWindowDelayMs);

        if (!okPress)
        {
            _log.Error("放行", $"按键失败，保留缓存 DMC={dmc} 以便重试");
            return;
        }

        // 先出队，再判断同文件夹是否还有待判定 → 全部结束后才回车
        _cache.TryRemove(dmc, out var removedItem);
        var folderKey = removedItem?.FolderKey ?? dmc;
        _log.Success("放行", $"完成 DMC={dmc} {decision} 组={folderKey}，已移出缓存并归档 log");
        _processedLogs[fullKey] = 1;
        ArchiveLog(cfg, path, reason: decision);

        MaybePressEnterForFolder(folderKey, decision);
    }

    /// <summary>
    /// 同文件夹组内所有 DMC 都结束后，再按一次回车。
    /// 单张 NG：按完 OK/NOK 立刻回车；多张：等全部 OK/NOK 后才回车。
    /// 若该组曾有超时：不按回车。
    /// </summary>
    private void MaybePressEnterForFolder(string folderKey, string reason)
    {
        var cfg = _cfg();
        if (!cfg.EnterAfterFolderAllDone) return;
        folderKey = (folderKey ?? "").Trim();
        if (string.IsNullOrEmpty(folderKey)) return;

        var remain = _cache.CountInFolder(folderKey);
                if (remain > 0)
                {
                    _log.Info("放行", $"文件夹组={folderKey} 尚有 {remain} 条未判定，暂不回车（本次={reason}）");
                    return;
                }

                // 组内出现过超时 → 不回车（仍通知会话结束）
                if (string.Equals(reason, "timeout", StringComparison.OrdinalIgnoreCase)
                    || _folderHadTimeout.ContainsKey(folderKey))
                {
                    _log.Warn("放行", $"文件夹组={folderKey} 全部结束但含超时，不按回车（原因={reason}）");
                    _folderHadTimeout.TryRemove(folderKey, out _);
                    try { FolderGroupFinished?.Invoke(folderKey, reason); }
                    catch (Exception ex) { _log.Warn("放行", "FolderGroupFinished 回调异常: " + ex.Message); }
                    return;
                }

                var enterKey = string.IsNullOrWhiteSpace(cfg.ConfirmEnterKey) ? "Enter" : cfg.ConfirmEnterKey.Trim();
                if (_canPressKeys != null && !_canPressKeys())
                {
                    _log.Warn("放行", $"文件夹组={folderKey} 应回车但 HARAN 非 Waiting，跳过回车");
                    // 仍通知会话结束，避免永远卡会话
                    try { FolderGroupFinished?.Invoke(folderKey, reason + "+noEnter"); }
                    catch (Exception ex) { _log.Warn("放行", "FolderGroupFinished 回调异常: " + ex.Message); }
                    return;
                }

                // 最后一张 9/7 刚按下，HARAN 往往还在处理；立即回车容易丢
                var enterDelay = Math.Max(0, cfg.EnterAfterLastKeyDelayMs);
                if (enterDelay > 0)
                {
                    _log.Info("放行",
                        $"文件夹组={folderKey} 全部判定结束 → 延迟 {enterDelay}ms 后再按 {enterKey}（触发={reason}）");
                    Thread.Sleep(enterDelay);
                }
                else
                {
                    _log.Info("放行", $"文件夹组={folderKey} 全部判定结束 → 按 {enterKey}（触发={reason}）");
                }

                if (_canPressKeys != null && !_canPressKeys())
                    _log.Warn("放行", $"延迟后 HARAN 非 Waiting，仍尝试回车 组={folderKey}");

                var enterRepeat = Math.Max(1, cfg.EnterRepeatCount);
                var ok = _keyboard.SendKey(
                    enterKey,
                    enterRepeat,
                    Math.Max(cfg.KeyPressDelayMs, 80),
                    string.IsNullOrWhiteSpace(cfg.TargetWindowTitleContains) ? null : cfg.TargetWindowTitleContains,
                    string.IsNullOrWhiteSpace(cfg.TargetProcessName) ? null : cfg.TargetProcessName,
                    Math.Max(cfg.ActivateWindowDelayMs, 100));
                if (!ok)
                    _log.Error("放行", $"回车键发送失败 组={folderKey} key={enterKey}");
                else
                    _log.Success("放行", $"已回车 组={folderKey} key={enterKey} x{enterRepeat}（末键后延迟 {enterDelay}ms）");

                // 回车完成后再通知会话结束（避免会话先切走影响其它逻辑）
                try { FolderGroupFinished?.Invoke(folderKey, reason); }
                catch (Exception ex) { _log.Warn("放行", "FolderGroupFinished 回调异常: " + ex.Message); }
            }
    private string? FindPendingDmcContainedInFileName(string logPath)
    {
        var name = Path.GetFileNameWithoutExtension(logPath) ?? "";
        if (string.IsNullOrEmpty(name)) return null;

        string? best = null;
        foreach (var item in _cache.Snapshot())
        {
            var d = item.Dmc?.Trim();
            if (string.IsNullOrEmpty(d)) continue;
            if (name.IndexOf(d, StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (best == null || d.Length > best.Length)
                best = d;
        }
        return best;
    }

    /// <summary>
    /// 解析 OK/NOK：整份 log 任意位置出现词表即可（不限行号）。
    /// 用「词边界」匹配：OK 不会命中 NOK 里的子串；Result=OK / Result=NOK 均可。
    /// 若同时出现独立 OK 与 NOK 词，优先 NOK。
    /// </summary>
    private static bool TryExtractDecision(
        string text,
        AppConfig cfg,
        out string token,
        out string decision,
        out string note)
    {
        token = "";
        decision = "";
        note = "";
        if (string.IsNullOrWhiteSpace(text))
        {
            note = "内容为空";
            return false;
        }

        var ignoreCase = cfg.ResultMatchIgnoreCase;
        var hay = text;

        // 更长词优先；NOK 组整体优先于 OK 组
        if (TryFindToken(hay, cfg.NokTokens, ignoreCase, out var nokTok))
        {
            token = nokTok;
            decision = "NOK";
            note = "token:NOK";
            return true;
        }
        if (TryFindToken(hay, cfg.OkTokens, ignoreCase, out var okTok))
        {
            token = okTok;
            decision = "OK";
            note = "token:OK";
            return true;
        }

        note = "全文未出现独立的 OK/NOK 词表项";
        return false;
    }

    /// <summary>
    /// 在全文中查找词表：必须作为独立词出现（左右不能是字母/数字）。
    /// 这样 Result=OK 命中 OK；Result=NOK 不会因内部含 OK 而误判。
    /// </summary>
    private static bool TryFindToken(
        string hay,
        IEnumerable<string>? tokens,
        bool ignoreCase,
        out string found)
    {
        found = "";
        if (tokens == null) return false;
        var cmp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var raw in tokens.OrderByDescending(x => (x ?? "").Trim().Length))
        {
            var t = (raw ?? "").Trim();
            if (t.Length == 0) continue;
            if (ContainsWholeToken(hay, t, cmp))
            {
                found = t;
                return true;
            }
        }
        return false;
    }

    private static bool ContainsWholeToken(string hay, string token, StringComparison cmp)
    {
        var start = 0;
        while (start <= hay.Length - token.Length)
        {
            var i = hay.IndexOf(token, start, cmp);
            if (i < 0) return false;
            var leftOk = i == 0 || !IsWordChar(hay[i - 1]);
            var end = i + token.Length;
            var rightOk = end >= hay.Length || !IsWordChar(hay[end]);
            if (leftOk && rightOk) return true;
            start = i + 1;
        }
        return false;
    }

    private static bool IsWordChar(char c)
        => char.IsLetterOrDigit(c) || c == '_';

    private void ArchiveLogsContainingDmc(string dmc, string reason)
    {
        var cfg = _cfg();
        if (string.IsNullOrWhiteSpace(dmc) || !Directory.Exists(cfg.CloudLogRoot))
        {
            if (string.Equals(reason, "timeout", StringComparison.OrdinalIgnoreCase))
                WriteTimeoutPlaceholder(cfg, dmc, "log目录不存在或DMC为空");
            return;
        }

        var matched = 0;
        foreach (var f in Directory.EnumerateFiles(cfg.CloudLogRoot))
        {
            try
            {
                if (!cfg.LogExtSet().Contains(Path.GetExtension(f))) continue;
                if (!string.IsNullOrWhiteSpace(cfg.CloudLogArchiveRoot) && IsUnder(cfg.CloudLogArchiveRoot, f)) continue;
                var name = Path.GetFileNameWithoutExtension(f) ?? "";
                if (name.IndexOf(dmc, StringComparison.OrdinalIgnoreCase) < 0) continue;
                ArchiveLog(cfg, f, reason);
                matched++;
            }
            catch (Exception ex)
            {
                _log.Warn("放行", $"归档匹配 log 失败 {f}: {ex.Message}");
            }
        }

        if (matched == 0 && string.Equals(reason, "timeout", StringComparison.OrdinalIgnoreCase))
        {
            // 超时且从未等到云端 log：仍写一条归档占位，方便追溯
            WriteTimeoutPlaceholder(cfg, dmc, "等待超时且log目录内无匹配文件");
        }
        else if (matched > 0)
        {
            _log.Info("放行", $"超时归档：DMC={dmc} 共移动 {matched} 个 log");
        }
    }

    /// <summary>超时且无云端 log 时，在归档目录写占位文件，避免「完成了但归档夹什么都没有」。</summary>
    private void WriteTimeoutPlaceholder(AppConfig cfg, string dmc, string detail)
    {
        try
        {
            var archiveRoot = string.IsNullOrWhiteSpace(cfg.CloudLogArchiveRoot)
                ? cfg.CloudLogRoot
                : cfg.CloudLogArchiveRoot;
            if (string.IsNullOrWhiteSpace(archiveRoot))
            {
                _log.Warn("放行", $"超时占位无法写入：归档目录为空 DMC={dmc}");
                return;
            }
            Directory.CreateDirectory(archiveRoot);

            // 文件名尽量避开非法字符
            var safe = string.Join("_", (dmc ?? "unknown").Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
            if (string.IsNullOrWhiteSpace(safe)) safe = "unknown";
            var name = $"{safe}__timeout.txt";
            var path = Path.Combine(archiveRoot, name);
            if (File.Exists(path))
                path = Path.Combine(archiveRoot, $"{safe}__timeout_{DateTime.Now:HHmmssfff}.txt");

            var body =
                "result=TIMEOUT\n" +
                $"dmc={dmc}\n" +
                $"time={DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}\n" +
                $"detail={detail}\n" +
                "note=缓存等待云端log超时，未按键；本文件为占位归档记录\n";
            File.WriteAllText(path, body, Encoding.UTF8);
            _log.Info("放行", $"超时占位已写入归档 → {path}");
        }
        catch (Exception ex)
        {
            _log.Warn("放行", $"写超时占位失败 DMC={dmc}: {ex.Message}");
        }
    }

    private void ArchiveLog(AppConfig cfg, string path, string reason = "done")
    {
        try
        {
            if (!File.Exists(path)) return;
            var fullKey = Path.GetFullPath(path);
            _processedLogs[fullKey] = 1;

            if (!string.IsNullOrWhiteSpace(cfg.CloudLogArchiveRoot))
            {
                Directory.CreateDirectory(cfg.CloudLogArchiveRoot);
                // 归档名带原因，便于区分 OK / NOK / timeout
                var baseName = Path.GetFileNameWithoutExtension(path);
                var ext = Path.GetExtension(path);
                var destName = $"{baseName}__{reason}{ext}";
                var dest = Path.Combine(cfg.CloudLogArchiveRoot, destName);
                if (File.Exists(dest))
                    dest = Path.Combine(cfg.CloudLogArchiveRoot,
                        $"{baseName}__{reason}_{DateTime.Now:HHmmssfff}{ext}");
                File.Move(path, dest);
                _log.Info("放行", $"log 已归档({reason}) → {dest}");
            }
            else
            {
                var done = path + $".{reason}.done";
                if (File.Exists(done)) File.Delete(done);
                File.Move(path, done);
                _log.Info("放行", $"log 已标记完成({reason}) → {done}");
            }
        }
        catch (Exception ex)
        {
            _log.Warn("放行", "归档 log 失败: " + ex.Message);
        }
    }

    private static string ExtractDmcFromImage(AppConfig cfg, string path)
    {
        if (string.Equals(cfg.DmcFromImage, "ParentFolder", StringComparison.OrdinalIgnoreCase))
        {
            var dir = Path.GetDirectoryName(path);
            return dir == null ? "" : Path.GetFileName(dir.TrimEnd('\\', '/'));
        }
        return Path.GetFileNameWithoutExtension(path);
    }

    private static bool IsUnder(string root, string path)
    {
        try
        {
            var r = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            var p = Path.GetFullPath(path);
            return p.StartsWith(r, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static bool IsSameOrUnder(string a, string b)
    {
        try
        {
            var fa = Path.GetFullPath(a).TrimEnd('\\', '/');
            var fb = Path.GetFullPath(b).TrimEnd('\\', '/');
            if (fa.Equals(fb, StringComparison.OrdinalIgnoreCase)) return true;
            return IsUnder(a, b) || IsUnder(b, a);
        }
        catch { return true; }
    }

    public void Dispose() => Stop();
}

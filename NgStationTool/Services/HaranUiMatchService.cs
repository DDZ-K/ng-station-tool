using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace NgStationTool.Services;

/// <summary>
/// HARAN 底栏/自定义区域多模板匹配（不依赖 UIA 读字）。
/// 按需轮询：仅有图片暂存 / 会话中 / DMC 待判定时才截图找 Waiting。
/// 模板：{TemplateRoot}/idle/*.png 与 waiting/*.png
/// </summary>
public sealed class HaranUiMatchService : IDisposable
{
    public enum MatchKind { Unknown, Idle, Waiting }

    private readonly AppLogger _log;
    private readonly Func<AppConfig> _cfg;
    /// <summary>为 true 时才截图比模板。</summary>
    private readonly Func<bool>? _needMatch;
    private CancellationTokenSource? _cts;
    private Task? _worker;
    private int _running;
    private MatchKind _last = MatchKind.Unknown;
    private int _stableHits;
    private long _lastScoreLogTick;
    private long _lastErrorLogTick;
    private long _lastIdleLogTick;
    private bool _wasMatching;

    public bool IsRunning => Volatile.Read(ref _running) == 1;
    public MatchKind LastKind => _last;
    public bool IsWaiting => _last == MatchKind.Waiting;
    public double LastIdleScore { get; private set; }
    public double LastWaitScore { get; private set; }
    public string? LastHitFile { get; private set; }
    public string? LastError { get; private set; }
    /// <summary>当前是否因「有活」而在截图轮询。</summary>
    public bool IsActivelyMatching { get; private set; }

    public event Action<MatchKind>? StateChanged;
    public event Action? EnteredWaiting;
    public event Action? StillWaiting;

    public HaranUiMatchService(AppLogger log, Func<AppConfig> cfg, Func<bool>? needMatch = null)
    {
        _log = log;
        _cfg = cfg;
        _needMatch = needMatch;
    }

    public void Start()
    {
        if (IsRunning) return;
        var cfg = _cfg();
        if (!cfg.EnableHaranUiGate)
        {
            _log.Info("HARAN", "界面就绪门闩已关闭");
            return;
        }

        EnsureTemplateDirs(cfg);
        _cts = new CancellationTokenSource();
        _last = MatchKind.Unknown;
        _stableHits = 0;
        _wasMatching = false;
        IsActivelyMatching = false;
        _worker = Task.Factory.StartNew(() => Loop(_cts.Token), TaskCreationOptions.LongRunning);
        Volatile.Write(ref _running, 1);
        _log.Success("HARAN",
            $"界面匹配服务已就绪（按需轮询） 轮询={cfg.HaranPollMs}ms 阈值={cfg.HaranMinScore:F2} " +
            $"模板={cfg.ResolvedHaranTemplateRoot()} | 有图片暂存/待判定时才截图找 Waiting");
    }

    public void Stop()
    {
        Volatile.Write(ref _running, 0);
        try { _cts?.Cancel(); } catch { /* */ }
        try { _worker?.Wait(2000); } catch { /* */ }
        _cts?.Dispose();
        _cts = null;
        _worker = null;
        IsActivelyMatching = false;
        _wasMatching = false;
        if (_last != MatchKind.Unknown)
        {
            _last = MatchKind.Unknown;
            try { StateChanged?.Invoke(MatchKind.Unknown); } catch { /* */ }
        }
    }

    public void Dispose() => Stop();

    private void Loop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var cfg = _cfg();
                if (!cfg.EnableHaranUiGate)
                {
                    IsActivelyMatching = false;
                    Thread.Sleep(500);
                    continue;
                }

                var need = _needMatch?.Invoke() ?? true;
                if (!need)
                {
                    EnterIdleStandby(cfg);
                    continue;
                }

                if (!_wasMatching)
                {
                    _wasMatching = true;
                    _log.Success("HARAN", "检测到待处理任务（暂存/会话/DMC）→ 开始截图轮询 Waiting");
                }
                IsActivelyMatching = true;

                var kind = MatchOnce(cfg, out var idle, out var wait, out var hit);
                LastIdleScore = idle;
                LastWaitScore = wait;
                LastHitFile = hit;
                LastError = null;

                var nowTick = Environment.TickCount64;
                if (nowTick - _lastScoreLogTick >= 2000)
                {
                    _lastScoreLogTick = nowTick;
                    _log.Info("HARAN",
                        $"轮询中 state={_last} frame={kind} idle={idle:F3} wait={wait:F3} thr={cfg.HaranMinScore:F2} hit={hit ?? "-"}");
                }

                if (kind == _last)
                {
                    _stableHits = Math.Min(_stableHits + 1, 100);
                    if (kind == MatchKind.Waiting)
                    {
                        try { StillWaiting?.Invoke(); } catch { /* */ }
                    }
                }
                else
                {
                    if (kind != MatchKind.Unknown)
                    {
                        _stableHits++;
                        if (_stableHits >= Math.Max(1, cfg.HaranStableFrames))
                        {
                            var prev = _last;
                            _last = kind;
                            _stableHits = 0;
                            _log.Info("HARAN",
                                $"状态 → {kind}  idle={idle:F3} wait={wait:F3} hit={hit ?? "-"}");
                            try { StateChanged?.Invoke(kind); } catch { /* */ }
                            if (kind == MatchKind.Waiting && prev != MatchKind.Waiting)
                            {
                                try { EnteredWaiting?.Invoke(); } catch { /* */ }
                            }
                        }
                    }
                    else
                    {
                        _stableHits = 0;
                    }
                }
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                var nowTick = Environment.TickCount64;
                if (nowTick - _lastErrorLogTick >= 3000)
                {
                    _lastErrorLogTick = nowTick;
                    _log.Warn("HARAN", "本轮匹配异常（继续轮询）: " + ex.Message);
                }
            }

            var sleep = Math.Max(100, _cfg().HaranPollMs);
            try { token.WaitHandle.WaitOne(sleep); } catch { break; }
        }
    }

    private void EnterIdleStandby(AppConfig cfg)
    {
        IsActivelyMatching = false;
        if (_wasMatching)
        {
            _wasMatching = false;
            _log.Info("HARAN", "暂无待处理（无暂存/无会话/无DMC缓存）→ 暂停截图轮询，等图片改名暂存后再找 Waiting");
            if (_last != MatchKind.Unknown)
            {
                _last = MatchKind.Unknown;
                _stableHits = 0;
                try { StateChanged?.Invoke(MatchKind.Unknown); } catch { /* */ }
            }
        }
        else
        {
            var t = Environment.TickCount64;
            if (t - _lastIdleLogTick >= 15000)
            {
                _lastIdleLogTick = t;
                _log.Info("HARAN", "待命中：先等图片改名进入暂存，再开始 Waiting 匹配");
            }
        }
        Thread.Sleep(Math.Max(200, cfg.HaranPollMs));
    }

    public MatchKind MatchOnce(AppConfig cfg, out double bestIdle, out double bestWait, out string? hitFile)
    {
        bestIdle = 0;
        bestWait = 0;
        hitFile = null;

        var filters = (cfg.HaranWindowTitleFilter ?? "HARAN")
            .Split(new[] { ';', ',', '，' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (filters.Length == 0) filters = new[] { "HARAN" };

        var wins = FindWindows(filters);
        if (wins.Count == 0) return MatchKind.Unknown;

        var best = wins.OrderByDescending(w => w.Title.Length).First();
        using var crop = CaptureRoi(best.Hwnd, cfg);
        if (crop == null) return MatchKind.Unknown;

        var idleDir = Path.Combine(cfg.ResolvedHaranTemplateRoot(), "idle");
        var waitDir = Path.Combine(cfg.ResolvedHaranTemplateRoot(), "waiting");
        string? hitIdle = null, hitWait = null;
        var idleCount = 0;
        var waitCount = 0;

        if (Directory.Exists(idleDir))
        {
            foreach (var f in Directory.GetFiles(idleDir, "*.png"))
            {
                try
                {
                    using var t = LoadClone(f);
                    var s = Similarity(crop, t);
                    idleCount++;
                    if (s > bestIdle) { bestIdle = s; hitIdle = Path.GetFileName(f); }
                }
                catch { /* */ }
            }
        }
        if (Directory.Exists(waitDir))
        {
            foreach (var f in Directory.GetFiles(waitDir, "*.png"))
            {
                try
                {
                    using var t = LoadClone(f);
                    var s = Similarity(crop, t);
                    waitCount++;
                    if (s > bestWait) { bestWait = s; hitWait = Path.GetFileName(f); }
                }
                catch { /* */ }
            }
        }

        // 无 idle 模板时：只靠 wait 极易把「Currently no Repair Data」误判成 Waiting
        // （底栏都是蓝条，缩图后文字差被抹平）。无 idle 时提高阈值，并要求 wait 明显更高。
        var baseMin = cfg.HaranMinScore;
        var hasIdleTpl = idleCount > 0;
        var hasWaitTpl = waitCount > 0;
        var minWait = hasIdleTpl ? baseMin : Math.Min(0.98, baseMin + 0.08);
        var margin = Math.Max(0.03, cfg.HaranWaitOverIdleMargin);

        if (!hasIdleTpl && hasWaitTpl && bestWait >= baseMin)
        {
            // 节流警告在 Loop 的分数日志里可见 idle=0
        }

        var waitOk = bestWait >= minWait;
        var idleOk = hasIdleTpl && bestIdle >= baseMin;

        // 两边都过线：必须 wait 比 idle 高出 margin，否则 Unknown（宁可漏判不误放）
        if (waitOk && idleOk)
        {
            if (bestWait >= bestIdle + margin)
            {
                hitFile = hitWait;
                return MatchKind.Waiting;
            }
            if (bestIdle >= bestWait + margin)
            {
                hitFile = hitIdle;
                return MatchKind.Idle;
            }
            hitFile = hitWait ?? hitIdle;
            return MatchKind.Unknown;
        }

        // 仅 wait 过线：无 idle 模板时已用更高 minWait；有 idle 但 idle 未过线时，仍要求 wait 明显高于 idle
        if (waitOk)
        {
            if (hasIdleTpl && bestWait < bestIdle + margin)
            {
                hitFile = hitWait;
                return MatchKind.Unknown;
            }
            hitFile = hitWait;
            return MatchKind.Waiting;
        }
        if (idleOk)
        {
            hitFile = hitIdle;
            return MatchKind.Idle;
        }
        return MatchKind.Unknown;
    }

    public static void EnsureTemplateDirs(AppConfig cfg)
    {
        var root = cfg.ResolvedHaranTemplateRoot();
        Directory.CreateDirectory(Path.Combine(root, "idle"));
        Directory.CreateDirectory(Path.Combine(root, "waiting"));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
    private const uint PW_RENDERFULLCONTENT = 0x2;
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lp);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private static List<(IntPtr Hwnd, string Title, uint Pid)> FindWindows(string[] filters)
    {
        var self = (uint)Environment.ProcessId;
        var list = new List<(IntPtr, string, uint)>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            GetWindowThreadProcessId(h, out var pid);
            if (pid == self) return true;
            var len = GetWindowTextLength(h);
            if (len <= 0) return true;
            var sb = new StringBuilder(len + 1);
            GetWindowText(h, sb, sb.Capacity);
            var title = sb.ToString();
            if (string.IsNullOrWhiteSpace(title)) return true;
            if (title.Contains("工位工具", StringComparison.OrdinalIgnoreCase)) return true;
            if (title.Contains("NgStationTool", StringComparison.OrdinalIgnoreCase)) return true;
            if (title.Contains("运行日志", StringComparison.OrdinalIgnoreCase)) return true;
            if (filters.Any(f => title.Contains(f, StringComparison.OrdinalIgnoreCase)))
                list.Add((h, title, pid));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private static Bitmap? CaptureRoi(IntPtr hwnd, AppConfig cfg)
    {
        if (!GetWindowRect(hwnd, out var rc)) return null;
        var w = rc.Right - rc.Left;
        var h = rc.Bottom - rc.Top;
        if (w < 40 || h < 40) return null;

        using var full = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(full))
        {
            var hdc = g.GetHdc();
            try
            {
                if (!PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT))
                    PrintWindow(hwnd, hdc, 0);
            }
            finally { g.ReleaseHdc(hdc); }
        }

        if (IsMostlyBlack(full))
        {
            using var g2 = Graphics.FromImage(full);
            try
            {
                g2.CopyFromScreen(rc.Left, rc.Top, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
            }
            catch { return null; }
        }

        var height = Math.Clamp(cfg.HaranRoiHeight <= 0 ? 48 : cfg.HaranRoiHeight, 8, h);
        int left = Math.Max(0, cfg.HaranRoiLeft);
        int width = cfg.HaranRoiWidth <= 0 ? (w - left) : cfg.HaranRoiWidth;
        width = Math.Clamp(width, 8, w - left);
        int top;
        if (cfg.HaranRoiFromBottom)
            top = Math.Clamp(h - Math.Max(0, cfg.HaranRoiBottomOffset) - height, 0, h - height);
        else
            top = Math.Clamp(cfg.HaranRoiTop, 0, h - height);

        try
        {
            var roi = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            using var g = Graphics.FromImage(roi);
            g.CopyFromScreen(rc.Left + left, rc.Top + top, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
            if (!IsMostlyBlack(roi)) return roi;
            roi.Dispose();
        }
        catch { /* fallthrough */ }

        return full.Clone(new Rectangle(left, top, width, height), PixelFormat.Format24bppRgb);
    }

    private static bool IsMostlyBlack(Bitmap bmp)
    {
        long bright = 0;
        var step = Math.Max(1, bmp.Width / 40);
        var n = 0;
        for (var y = 0; y < bmp.Height; y += step)
        for (var x = 0; x < bmp.Width; x += step)
        {
            var c = bmp.GetPixel(x, y);
            bright += c.R + c.G + c.B;
            n++;
        }
        return n > 0 && bright / n < 30;
    }

    private static Bitmap LoadClone(string path)
    {
        using var fs = File.OpenRead(path);
        using var tmp = new Bitmap(fs);
        return new Bitmap(tmp);
    }

    private static double Similarity(Bitmap a, Bitmap b)
    {
        // 提高分辨率，减轻「Waiting for Input」与「Currently no Repair Data」被糊成同一块蓝条
        using var aa = Resize(a, 640, 48);
        using var bb = Resize(b, 640, 48);
        long sum = 0;
        long n = 0;
        for (var y = 0; y < aa.Height; y++)
        for (var x = 0; x < aa.Width; x++)
        {
            var ca = aa.GetPixel(x, y);
            var cb = bb.GetPixel(x, y);
            // 略加重绿色通道（状态条文字常在亮底上）
            var ga = (ca.R + ca.G * 2 + ca.B) / 4;
            var gb = (cb.R + cb.G * 2 + cb.B) / 4;
            sum += Math.Abs(ga - gb);
            n++;
        }
        if (n == 0) return 0;
        var full = Math.Max(0, 1.0 - sum / (double)n / 255.0);

        // 中部文字带再比一次（排除左右装饰），取更严的分数（较低者）→ 更不易误判
        var x0 = aa.Width / 8;
        var x1 = aa.Width - x0;
        long sum2 = 0;
        long n2 = 0;
        for (var y = 0; y < aa.Height; y++)
        for (var x = x0; x < x1; x++)
        {
            var ca = aa.GetPixel(x, y);
            var cb = bb.GetPixel(x, y);
            var ga = (ca.R + ca.G * 2 + ca.B) / 4;
            var gb = (cb.R + cb.G * 2 + cb.B) / 4;
            sum2 += Math.Abs(ga - gb);
            n2++;
        }
        var mid = n2 == 0 ? full : Math.Max(0, 1.0 - sum2 / (double)n2 / 255.0);
        return Math.Min(full, mid);
    }

    private static Bitmap Resize(Bitmap src, int w, int h)
    {
        var dst = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(dst);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
        g.DrawImage(src, 0, 0, w, h);
        return dst;
    }
}

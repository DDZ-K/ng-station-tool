using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace NgStationTool.Services;

/// <summary>
/// HARAN 底栏/自定义区域多模板匹配（不依赖 UIA 读字）。
/// 模板：{TemplateRoot}/idle/*.png 与 waiting/*.png
/// </summary>
public sealed class HaranUiMatchService : IDisposable
{
    public enum MatchKind { Unknown, Idle, Waiting }

    private readonly AppLogger _log;
    private readonly Func<AppConfig> _cfg;
    private CancellationTokenSource? _cts;
    private Task? _worker;
    private int _running;
    private MatchKind _last = MatchKind.Unknown;
    private int _stableHits;

    public bool IsRunning => Volatile.Read(ref _running) == 1;
    public MatchKind LastKind => _last;
    public bool IsWaiting => _last == MatchKind.Waiting;
    public double LastIdleScore { get; private set; }
    public double LastWaitScore { get; private set; }
    public string? LastHitFile { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>状态变化（含进入/离开 Waiting）</summary>
    public event Action<MatchKind>? StateChanged;
    /// <summary>刚进入 Waiting（上升沿）</summary>
    public event Action? EnteredWaiting;

    public HaranUiMatchService(AppLogger log, Func<AppConfig> cfg)
    {
        _log = log;
        _cfg = cfg;
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
        _worker = Task.Factory.StartNew(() => Loop(_cts.Token), TaskCreationOptions.LongRunning);
        Volatile.Write(ref _running, 1);
        _log.Success("HARAN",
            $"界面匹配已启动 轮询={cfg.HaranPollMs}ms 阈值={cfg.HaranMinScore:F2} 稳定帧={cfg.HaranStableFrames} " +
            $"模板={cfg.ResolvedHaranTemplateRoot()}");
    }

    public void Stop()
    {
        Volatile.Write(ref _running, 0);
        try { _cts?.Cancel(); } catch { /* */ }
        try { _worker?.Wait(2000); } catch { /* */ }
        _cts?.Dispose();
        _cts = null;
        _worker = null;
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
                    Thread.Sleep(500);
                    continue;
                }

                var kind = MatchOnce(cfg, out var idle, out var wait, out var hit);
                LastIdleScore = idle;
                LastWaitScore = wait;
                LastHitFile = hit;
                LastError = null;

                if (kind == _last)
                {
                    _stableHits = Math.Min(_stableHits + 1, 100);
                }
                else
                {
                    // 需连续 N 帧一致才切换，防闪
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
            }

            var sleep = Math.Max(100, _cfg().HaranPollMs);
            try { token.WaitHandle.WaitOne(sleep); } catch { break; }
        }
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

        if (Directory.Exists(idleDir))
        {
            foreach (var f in Directory.GetFiles(idleDir, "*.png"))
            {
                try
                {
                    using var t = LoadClone(f);
                    var s = Similarity(crop, t);
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
                    if (s > bestWait) { bestWait = s; hitWait = Path.GetFileName(f); }
                }
                catch { /* */ }
            }
        }

        var min = cfg.HaranMinScore;
        if (bestWait >= min && bestWait >= bestIdle)
        {
            hitFile = hitWait;
            return MatchKind.Waiting;
        }
        if (bestIdle >= min)
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

    // ---- capture / match helpers (from probe) ----

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

        // 若几乎全黑则屏幕拷贝整窗，再裁 ROI（仍比错误全黑强）
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

        // 优先：直接屏幕拷贝 ROI（更快）
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
        using var aa = Resize(a, 320, 32);
        using var bb = Resize(b, 320, 32);
        long sum = 0;
        long n = 0;
        for (var y = 0; y < aa.Height; y++)
        for (var x = 0; x < aa.Width; x++)
        {
            var ca = aa.GetPixel(x, y);
            var cb = bb.GetPixel(x, y);
            sum += Math.Abs((ca.R + ca.G + ca.B) / 3 - (cb.R + cb.G + cb.B) / 3);
            n++;
        }
        if (n == 0) return 0;
        return Math.Max(0, 1.0 - sum / (double)n / 255.0);
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

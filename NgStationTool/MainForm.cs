using NgStationTool.Services;

namespace NgStationTool;

public sealed class MainForm : Form
{
    private AppConfig _cfg;
    private readonly AppLogger _log;
    private readonly DmcPendingCache _cache;
    private readonly KeyboardService _keyboard;
    private readonly ImageCopyWatcher _imageWatcher;
    private readonly CloudReleaseService _cloud;
    private readonly System.Windows.Forms.Timer _uiTimer;

    private readonly Label _lblStatus;
    private readonly ListView _lvCache;
    private readonly ListBox _lstLog;
    private readonly Button _btnStart;
    private readonly Button _btnStop;
    private readonly Button _btnConfig;
    private readonly Button _btnClearCache;
    private readonly Button _btnTestOk;
    private readonly Button _btnTestNok;
    private readonly NotifyIcon _tray;
    private readonly CheckBox _chkTop;

    public MainForm()
    {
        _cfg = AppConfig.Load();
        var logPath = Path.Combine(AppContext.BaseDirectory, "station_log.txt");
        _log = new AppLogger(logPath, _cfg.MaxLogLines);
        _cache = new DmcPendingCache(_log, _cfg.PendingTimeoutSec);
        _keyboard = new KeyboardService(_log);
        _cloud = new CloudReleaseService(_log, () => _cfg, _cache, _keyboard);
        _imageWatcher = new ImageCopyWatcher(_log, () => _cfg, (renamedDmc, path, folderKey) =>
        {
            if (_cfg.EnableCloudRelease && _cfg.EnqueueFromImageCopyFolderName)
                _cloud.EnqueueDmc(renamedDmc, "ImageCopyRenamed", path, folderKey);
        });

        Text = "工位工具 · 图片命名 + 云端放行  v1.2.4";
        Width = 980;
        Height = 640;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9F);
        MinimumSize = new Size(800, 500);

        _lblStatus = new Label
        {
            Dock = DockStyle.Top,
            Height = 36,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(10, 0, 0, 0),
            BackColor = Color.FromArgb(32, 40, 48),
            ForeColor = Color.White,
            Text = "状态: 已停止"
        };

        var topBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 44,
            Padding = new Padding(8, 8, 8, 4),
            WrapContents = false
        };
        _btnStart = new Button { Text = "开始", Width = 90, Height = 28 };
        _btnStop = new Button { Text = "停止", Width = 90, Height = 28, Enabled = false };
        _btnConfig = new Button { Text = "配置…", Width = 90, Height = 28 };
        _btnClearCache = new Button { Text = "清空缓存", Width = 90, Height = 28 };
        _btnTestOk = new Button { Text = "测试OK键", Width = 90, Height = 28 };
        _btnTestNok = new Button { Text = "测试NOK键", Width = 100, Height = 28 };
        _chkTop = new CheckBox { Text = "窗口置顶", AutoSize = true, Margin = new Padding(12, 6, 0, 0) };
        topBar.Controls.AddRange(new Control[]
        {
            _btnStart, _btnStop, _btnConfig, _btnClearCache, _btnTestOk, _btnTestNok, _chkTop
        });

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 220
        };

        _lvCache = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true
        };
        _lvCache.Columns.Add("DMC(改名完整名)", 220);
        _lvCache.Columns.Add("类型", 70);
        _lvCache.Columns.Add("文件夹组", 110);
        _lvCache.Columns.Add("同组剩余", 70);
        _lvCache.Columns.Add("入队时间", 80);
        _lvCache.Columns.Add("剩余秒", 55);
        _lvCache.Columns.Add("来源", 90);
        _lvCache.Columns.Add("输出图片路径", 260);
        var cachePanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
        var cacheTitle = new Label
        {
            Text = "待确认 DMC：【单】=该夹仅1条  【多】=同夹多条 | 全组OK/NOK完才回车；有超时则不回车",
            Dock = DockStyle.Top,
            Height = 22
        };
        cachePanel.Controls.Add(_lvCache);
        cachePanel.Controls.Add(cacheTitle);
        split.Panel1.Controls.Add(cachePanel);

        _lstLog = new ListBox
        {
            Dock = DockStyle.Fill,
            HorizontalScrollbar = true,
            IntegralHeight = false
        };
        var logPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
        var logTitle = new Label { Text = "运行日志", Dock = DockStyle.Top, Height = 22 };
        logPanel.Controls.Add(_lstLog);
        logPanel.Controls.Add(logTitle);
        split.Panel2.Controls.Add(logPanel);

        Controls.Add(split);
        Controls.Add(topBar);
        Controls.Add(_lblStatus);

        _tray = new NotifyIcon
        {
            Text = "工位工具",
            Visible = true,
            Icon = SystemIcons.Application
        };
        _tray.DoubleClick += (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); };

        _btnStart.Click += (_, _) => StartAll();
        _btnStop.Click += (_, _) => StopAll();
        _btnConfig.Click += (_, _) => OpenConfig();
        _btnClearCache.Click += (_, _) =>
        {
            _cache.ClearAll("用户清空");
            RefreshCacheList();
        };
        _btnTestOk.Click += (_, _) => TestKey(_cfg.OkKey);
        _btnTestNok.Click += (_, _) => TestKey(_cfg.NokKey);
        _chkTop.CheckedChanged += (_, _) => TopMost = _chkTop.Checked;

        _log.Logged += e =>
        {
            try
            {
                if (IsDisposed) return;
                BeginInvoke(new Action(() =>
                {
                    _lstLog.Items.Add(e.ToString());
                    while (_lstLog.Items.Count > _cfg.MaxLogLines)
                        _lstLog.Items.RemoveAt(0);
                    _lstLog.TopIndex = Math.Max(0, _lstLog.Items.Count - 1);
                }));
            }
            catch { /* ignore */ }
        };

        _cache.Changed += () =>
        {
            try { if (!IsDisposed) BeginInvoke(RefreshCacheList); } catch { /* ignore */ }
        };

        _uiTimer = new System.Windows.Forms.Timer { Interval = Math.Max(200, _cfg.UiRefreshMs) };
        _uiTimer.Tick += (_, _) =>
        {
            RefreshCacheList();
            UpdateStatus();
        };
        _uiTimer.Start();

        FormClosing += OnFormClosing;
        Load += (_, _) =>
        {
            _log.Info("系统", "程序启动 Win10/net8 | 版本=v1.2.4 | 程序目录=" + AppContext.BaseDirectory + " | 配置=" + AppConfig.DefaultPath);
            if (_cfg.AutoStartOnLaunch)
                StartAll();
            else
                UpdateStatus();
        };
    }

    private void StartAll()
    {
        try
        {
            _cfg = AppConfig.Load(); // 重新读盘
            _cache.SetTimeoutSec(_cfg.PendingTimeoutSec);
            _log.SetMaxLines(_cfg.MaxLogLines);
            _imageWatcher.Start();
            _cloud.Start();
            _btnStart.Enabled = false;
            _btnStop.Enabled = true;
            UpdateStatus();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void StopAll()
    {
        _imageWatcher.Stop();
        _cloud.Stop();
        _btnStart.Enabled = true;
        _btnStop.Enabled = false;
        UpdateStatus();
    }

    private void OpenConfig()
    {
        using var dlg = new ConfigForm(_cfg);
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _cfg = dlg.Result;
            _cfg.Save();
            _cache.SetTimeoutSec(_cfg.PendingTimeoutSec);
            _log.SetMaxLines(_cfg.MaxLogLines);
            _log.Info("系统", "配置已保存。若已在运行，请停止后重新开始以应用目录监视变更。");
            MessageBox.Show(this,
                "配置已保存。\n监视目录等变更请点「停止」再「开始」。",
                "配置", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private void TestKey(string key)
    {
        Task.Run(() =>
        {
            _log.Info("键盘", $"3 秒后测试发送 {key}，请点到目标窗口…");
            Thread.Sleep(3000);
            _keyboard.SendKey(key, 1, 50,
                string.IsNullOrWhiteSpace(_cfg.TargetWindowTitleContains) ? null : _cfg.TargetWindowTitleContains,
                string.IsNullOrWhiteSpace(_cfg.TargetProcessName) ? null : _cfg.TargetProcessName,
                _cfg.ActivateWindowDelayMs);
        });
    }

    private void RefreshCacheList()
    {
        if (IsDisposed) return;
        var items = _cache.Snapshot();
        var timeout = Math.Max(5, _cfg.PendingTimeoutSec);
        _lvCache.BeginUpdate();
        try
        {
            _lvCache.Items.Clear();
            // 统计同组数量
            var groupCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var it in items)
            {
                var fk = string.IsNullOrWhiteSpace(it.FolderKey) ? it.Dmc : it.FolderKey;
                groupCount[fk] = groupCount.TryGetValue(fk, out var c) ? c + 1 : 1;
            }

            foreach (var it in items)
            {
                var left = timeout - (DateTime.Now - it.EnqueuedAt).TotalSeconds;
                if (left < 0) left = 0;
                var fk = string.IsNullOrWhiteSpace(it.FolderKey) ? it.Dmc : it.FolderKey;
                var n = groupCount.TryGetValue(fk, out var gc) ? gc : 1;
                var kind = n <= 1 ? "单" : $"多×{n}";

                var lvi = new ListViewItem(it.Dmc);
                lvi.SubItems.Add(kind);
                lvi.SubItems.Add(it.FolderKey);
                lvi.SubItems.Add(n.ToString());
                lvi.SubItems.Add(it.EnqueuedAt.ToString("HH:mm:ss"));
                lvi.SubItems.Add(((int)left).ToString());
                lvi.SubItems.Add(it.Source);
                lvi.SubItems.Add(it.SourcePath ?? "");

                // 视觉区分：多条同组用浅橙底，单条浅绿底
                if (n > 1)
                {
                    lvi.BackColor = Color.FromArgb(255, 244, 229);
                    lvi.ForeColor = Color.FromArgb(120, 60, 0);
                }
                else
                {
                    lvi.BackColor = Color.FromArgb(232, 248, 237);
                    lvi.ForeColor = Color.FromArgb(20, 70, 40);
                }

                _lvCache.Items.Add(lvi);
            }
        }
        finally { _lvCache.EndUpdate(); }
    }

    private void UpdateStatus()
    {
        var img = _imageWatcher.IsRunning ? "图片✓" : "图片✗";
        var cloud = _cloud.IsRunning ? "放行✓" : "放行✗";
        var run = _imageWatcher.IsRunning || _cloud.IsRunning ? "运行中" : "已停止";
        _lblStatus.Text = $"状态: {run}  |  {img}  {cloud}  |  缓存 {_cache.Count}  |  OK键={_cfg.OkKey} NOK键={_cfg.NokKey} 超时={_cfg.PendingTimeoutSec}s";
        _lblStatus.BackColor = run == "运行中" ? Color.FromArgb(20, 90, 50) : Color.FromArgb(32, 40, 48);
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            _tray.ShowBalloonTip(1500, "工位工具", "已最小化到托盘，监视继续（若已开始）。右键托盘可退出。", ToolTipIcon.Info);
            return;
        }
        Cleanup();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开主窗口", null, (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); });
        menu.Items.Add("开始", null, (_, _) => StartAll());
        menu.Items.Add("停止", null, (_, _) => StopAll());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) =>
        {
            Cleanup();
            _tray.Visible = false;
            Application.Exit();
        });
        _tray.ContextMenuStrip = menu;
    }

    private void Cleanup()
    {
        try { _uiTimer.Stop(); } catch { /* ignore */ }
        try { StopAll(); } catch { /* ignore */ }
        try { _imageWatcher.Dispose(); } catch { /* ignore */ }
        try { _cloud.Dispose(); } catch { /* ignore */ }
        try { _tray.Visible = false; _tray.Dispose(); } catch { /* ignore */ }
    }
}

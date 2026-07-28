using NgStationTool.Services;

namespace NgStationTool;

public sealed class MainForm : Form
{
    private AppConfig _cfg;
    private readonly AppLogger _log;
    private readonly NgPendingQueue _ngQueue;
    private readonly DmcPendingCache _judgingQueue;
    private readonly KeyboardService _keyboard;
    private readonly ImageCopyWatcher _imageWatcher;
    private readonly XmlDmcGateService _xmlGate;
    private readonly CloudReleaseService _cloud;
    private readonly System.Windows.Forms.Timer _uiTimer;
    private readonly Label _status;
    private readonly Label _ngCount;
    private readonly Label _judgingCount;
    private readonly Button _clearNg;
    private readonly Button _clearJudging;
    private readonly ListView _ngList;
    private readonly ListView _judgingList;
    private readonly ListBox _logs;
    private readonly Button _start;
    private readonly Button _stop;
    private readonly NotifyIcon _tray;
    private bool _explicitExitRequested;
    private bool _cleaned;

    private static readonly Color Bg = Color.FromArgb(244, 247, 250);
    private static readonly Color Ink = Color.FromArgb(31, 41, 55);
    private static readonly Color Muted = Color.FromArgb(100, 116, 139);
    private static readonly Color Blue = Color.FromArgb(37, 99, 235);
    private static readonly Color Green = Color.FromArgb(22, 163, 74);
    private static readonly Color Danger = Color.FromArgb(185, 28, 28);

    public MainForm()
    {
        _cfg = AppConfig.Load();
        _log = new AppLogger(Path.Combine(AppContext.BaseDirectory, "station_log.txt"), _cfg.MaxLogLines);
        _ngQueue = new NgPendingQueue(_log);
        _judgingQueue = new DmcPendingCache(_log);
        _keyboard = new KeyboardService(_log);
        _cloud = new CloudReleaseService(_log, () => _cfg, _judgingQueue, _keyboard);
        _xmlGate = new XmlDmcGateService(_log, () => _cfg, _ngQueue,
            (imageName, path, productDmc) => _cloud.EnqueueDmc(imageName, "XmlMatched", path, productDmc));
        _imageWatcher = new ImageCopyWatcher(_log, () => _cfg,
            (imageName, path, productDmc) => _ngQueue.Enqueue(imageName, productDmc, path));

        Text = "NG 工位流转中心  v1.6.0";
        Width = 1180;
        Height = 760;
        MinimumSize = new Size(960, 620);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9F);
        BackColor = Bg;

        var header = new Panel { Dock = DockStyle.Top, Height = 78, BackColor = Color.White, Padding = new Padding(22, 14, 18, 10) };
        var title = new Label { Text = "NG 工位流转中心", Font = new Font(Font.FontFamily, 16F, FontStyle.Bold), ForeColor = Ink, AutoSize = true, Left = 22, Top = 12 };
        var subtitle = new Label { Text = "图片 → A 待NG → XML确认 → B 待判断 → Log放行", ForeColor = Muted, AutoSize = true, Left = 24, Top = 46 };
        _start = Button("开始监控", Blue, 110);
        _stop = Button("停止", Color.FromArgb(71, 85, 105), 86); _stop.Enabled = false;
        var config = Button("配置", Color.FromArgb(15, 118, 110), 86);
        var exit = Button("收起", Color.FromArgb(71, 85, 105), 76);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 390, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Padding = new Padding(0, 8, 0, 0) };
        actions.Controls.AddRange(new Control[] { _start, _stop, config, exit });
        header.Controls.AddRange(new Control[] { title, subtitle, actions });

        _status = new Label { Dock = DockStyle.Top, Height = 38, BackColor = Color.FromArgb(226, 232, 240), ForeColor = Ink, Padding = new Padding(22, 10, 0, 0), Text = "已停止" };

        var queues = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(16, 14, 16, 8) };
        queues.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        queues.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        queues.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        _ngList = QueueList();
        _ngList.ShowItemToolTips = true;
        _ngList.Columns.Add("完整图片名", 340); _ngList.Columns.Add("产品DMC", 260); _ngList.Columns.Add("进入A", 90); _ngList.Columns.Add("A路径", 400);
        _judgingList = QueueList();
        _judgingList.ShowItemToolTips = true;
        _judgingList.Columns.Add("完整图片名", 340); _judgingList.Columns.Add("产品DMC", 260); _judgingList.Columns.Add("进入B", 90); _judgingList.Columns.Add("B路径", 400);
        _ngCount = new Label(); _judgingCount = new Label();
        _clearNg = SmallButton("清空队列", Danger);
        _clearJudging = SmallButton("清空队列", Danger);
        _clearNg.Click += (_, _) => ClearNgQueue();
        _clearJudging.Click += (_, _) => ClearJudgingQueue();
        queues.Controls.Add(Card("待 NG 队列", "图片已进入 A，等待 XML identifier", _ngCount, _clearNg, _ngList, Color.FromArgb(234, 88, 12)), 0, 0);
        queues.Controls.Add(Card("待判断队列", "XML 已匹配并进入 B，等待云端 Log", _judgingCount, _clearJudging, _judgingList, Green), 0, 1);

        _logs = new ListBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = Color.FromArgb(15, 23, 42), ForeColor = Color.FromArgb(203, 213, 225), Font = new Font("Consolas", 9F), HorizontalScrollbar = true, IntegralHeight = false };
        var logWrap = new Panel { Dock = DockStyle.Bottom, Height = 218, Padding = new Padding(18, 8, 18, 16), BackColor = Bg };
        var logTitle = new Label { Text = "运行日志", Dock = DockStyle.Top, Height = 28, ForeColor = Ink, Font = new Font(Font.FontFamily, 10F, FontStyle.Bold) };
        logWrap.Controls.Add(_logs); logWrap.Controls.Add(logTitle);

        Controls.Add(queues); Controls.Add(logWrap); Controls.Add(_status); Controls.Add(header);

        _start.Click += (_, _) => StartAll();
        _stop.Click += (_, _) => StopAll();
        config.Click += (_, _) => OpenConfig();
        exit.Click += (_, _) => MinimizeToTray(showTip: true);
        _ngQueue.Changed += RefreshAsync;
        _judgingQueue.Changed += RefreshAsync;
        _log.Logged += entry =>
        {
            try { if (!IsDisposed) BeginInvoke(new Action(() => { _logs.Items.Add(entry.ToString()); while (_logs.Items.Count > _cfg.MaxLogLines) _logs.Items.RemoveAt(0); _logs.TopIndex = Math.Max(0, _logs.Items.Count - 1); })); } catch { }
        };
        _uiTimer = new System.Windows.Forms.Timer { Interval = Math.Max(300, _cfg.UiRefreshMs) };
        _uiTimer.Tick += (_, _) => RefreshUi();
        _uiTimer.Start();
        _tray = new NotifyIcon
        {
            Text = "NG 工位流转中心",
            Icon = SystemIcons.Application,
            Visible = true,
            ContextMenuStrip = BuildTrayMenu()
        };
        _tray.DoubleClick += (_, _) => RestoreFromTray();
        Load += (_, _) => { _log.Info("系统", "程序启动 | 版本=v1.6.0"); if (_cfg.AutoStartOnLaunch) StartAll(); else RefreshUi(); };
        FormClosing += OnFormClosing;
        Resize += (_, _) => { ResizeQueueColumns(_ngList); ResizeQueueColumns(_judgingList); };
        Shown += (_, _) => { ResizeQueueColumns(_ngList); ResizeQueueColumns(_judgingList); };
    }

    private static Button Button(string text, Color color, int width) => new()
    {
        Text = text, Width = width, Height = 34, FlatStyle = FlatStyle.Flat, BackColor = color, ForeColor = Color.White,
        Margin = new Padding(7, 0, 0, 0), Cursor = Cursors.Hand, FlatAppearance = { BorderSize = 0 }
    };

    private static Button SmallButton(string text, Color color) => new()
    {
        Text = text, Width = 88, Height = 28, FlatStyle = FlatStyle.Flat, BackColor = color, ForeColor = Color.White,
        Cursor = Cursors.Hand, FlatAppearance = { BorderSize = 0 },
        Font = new Font("Microsoft YaHei UI", 8.5F)
    };

    private static ListView QueueList() => new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = false,
        BorderStyle = BorderStyle.None, BackColor = Color.White, ForeColor = Ink, HeaderStyle = ColumnHeaderStyle.Nonclickable
    };

    private static Panel Card(string title, string subtitle, Label count, Button clearBtn, ListView list, Color accent)
    {
        var p = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(16), Margin = new Padding(7) };
        var top = new Panel { Dock = DockStyle.Top, Height = 58 };
        var heading = new Label { Text = title, Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold), ForeColor = Ink, AutoSize = true, Left = 0, Top = 1 };
        var hint = new Label { Text = subtitle, ForeColor = Muted, AutoSize = true, Left = 1, Top = 31 };
        count.Text = "0";
        count.Font = new Font("Segoe UI", 16F, FontStyle.Bold);
        count.ForeColor = accent;
        count.AutoSize = true;
        count.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        clearBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        void LayoutRight()
        {
            // 右侧：计数 + 清空按钮，随卡片宽度贴右
            clearBtn.Top = 12;
            clearBtn.Left = Math.Max(heading.Right + 12, top.ClientSize.Width - clearBtn.Width - 4);
            count.Top = 7;
            count.Left = Math.Max(heading.Right + 8, clearBtn.Left - count.Width - 10);
        }
        top.Resize += (_, _) => LayoutRight();
        top.Controls.AddRange(new Control[] { heading, hint, count, clearBtn });
        top.HandleCreated += (_, _) => LayoutRight();
        p.Controls.Add(list);
        p.Controls.Add(top);
        return p;
    }

    private void ClearNgQueue()
    {
        var n = _ngQueue.Count;
        if (n == 0)
        {
            MessageBox.Show(this, "待 NG 队列已经是空的。", "清空队列", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var r = MessageBox.Show(this,
            $"确定清空「待 NG」队列中的 {n} 条吗？\n\n仅清除软件内存中的排队记录，不会删除 A 目录里的图片文件。",
            "清空待 NG 队列",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (r != DialogResult.Yes) return;
        _ngQueue.ClearAll("用户点击清空待NG");
        RefreshUi();
    }

    private void ClearJudgingQueue()
    {
        var n = _judgingQueue.Count;
        if (n == 0)
        {
            MessageBox.Show(this, "待判断队列已经是空的。", "清空队列", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var r = MessageBox.Show(this,
            $"确定清空「待判断」队列中的 {n} 条吗？\n\n仅清除软件内存中的排队记录，不会删除 B 目录图片，也不会发送 9/7 按键。",
            "清空待判断队列",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (r != DialogResult.Yes) return;
        _judgingQueue.ClearAll("用户点击清空待判断");
        RefreshUi();
    }

    private void StartAll()
    {
        try
        {
            _cfg = AppConfig.Load();
            _log.SetMaxLines(_cfg.MaxLogLines);
            _imageWatcher.Start(); _xmlGate.Start(); _cloud.Start();
            _start.Enabled = false; _stop.Enabled = true; RefreshUi();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void StopAll()
    {
        _imageWatcher.Stop(); _xmlGate.Stop(); _cloud.Stop();
        if (!IsDisposed) { _start.Enabled = true; _stop.Enabled = false; RefreshUi(); }
    }

    private void OpenConfig()
    {
        using var dlg = new ConfigForm(_cfg);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _cfg = dlg.Result; _cfg.Save(); _log.SetMaxLines(_cfg.MaxLogLines);
        _log.Info("系统", "配置已保存；目录变更请停止后再开始。 ");
    }

    private void RefreshAsync() { try { if (!IsDisposed) BeginInvoke(RefreshUi); } catch { } }

    private void RefreshUi()
    {
        if (IsDisposed) return;
        var ng = _ngQueue.Snapshot(); var judging = _judgingQueue.Snapshot();
        Fill(_ngList, ng.Select(x => new[] { x.ImageName, x.ProductDmc, x.EnqueuedAt.ToString("HH:mm:ss"), x.StagedPath }));
        Fill(_judgingList, judging.Select(x => new[] { x.Dmc, x.FolderKey, x.EnqueuedAt.ToString("HH:mm:ss"), x.SourcePath ?? "" }));
        _ngCount.Text = ng.Count.ToString(); _judgingCount.Text = judging.Count.ToString();
        _clearNg.Enabled = ng.Count > 0;
        _clearJudging.Enabled = judging.Count > 0;
        var running = _imageWatcher.IsRunning || _xmlGate.IsRunning || _cloud.IsRunning;
                var partCount = PartNumberRules.Normalize(_cfg.PartNumbers).Count();
                var partHint = partCount == 0 ? "料号过滤关(空列表)" : $"服务料号 {partCount}";
                _status.Text = running
                    ? $"● 运行中    图片 ✓    XML ✓    Log ✓    待NG {ng.Count}    待判断 {judging.Count}    {partHint}"
                    : $"● 已停止    待NG {ng.Count}    待判断 {judging.Count}    {partHint}";
                _status.ForeColor = running ? Green : Muted;
    }

    private static void Fill(ListView list, IEnumerable<string[]> rows)
    {
        list.BeginUpdate();
        try
        {
            list.Items.Clear();
            foreach (var cells in rows)
            {
                var row = new ListViewItem(cells[0]) { ToolTipText = $"完整图片名：{cells[0]}\n产品DMC：{cells[1]}\n路径：{cells[3]}" };
                foreach (var cell in cells.Skip(1)) row.SubItems.Add(cell);
                list.Items.Add(row);
            }
        }
        finally { list.EndUpdate(); }
    }

    private ContextMenuStrip BuildTrayMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开主窗口", null, (_, _) => RestoreFromTray());
        menu.Items.Add("开始监控", null, (_, _) => StartAll());
        menu.Items.Add("停止监控", null, (_, _) => StopAll());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出程序", null, (_, _) => ExitApplication());
        return menu;
    }

    private void MinimizeToTray(bool showTip)
    {
        Hide();
        ShowInTaskbar = false;
        if (showTip)
            _tray.ShowBalloonTip(1500, "NG 工位流转中心", "程序已收起到右下角托盘，监控继续运行。", ToolTipIcon.Info);
    }

    private void RestoreFromTray()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    private void ExitApplication()
    {
        _explicitExitRequested = true;
        Close();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (TrayClosePolicy.ShouldMinimizeToTray(e.CloseReason, _explicitExitRequested))
        {
            e.Cancel = true;
            MinimizeToTray(showTip: true);
            return;
        }
        Cleanup();
    }

    protected override void WndProc(ref Message m)
    {
        const int WmClose = 0x0010;
        if (m.Msg == WmClose && !_explicitExitRequested && !_cleaned)
        {
            MinimizeToTray(showTip: true);
            return;
        }
        base.WndProc(ref m);
    }

    private static void ResizeQueueColumns(ListView list)
    {
        if (list.Columns.Count != 4 || list.ClientSize.Width <= 0) return;
        var width = Math.Max(640, list.ClientSize.Width - 8);
        list.Columns[2].Width = 90;
        list.Columns[0].Width = Math.Max(220, (int)(width * 0.31));
        list.Columns[1].Width = Math.Max(180, (int)(width * 0.24));
        list.Columns[3].Width = Math.Max(240, width - list.Columns[0].Width - list.Columns[1].Width - list.Columns[2].Width);
    }

    private void Cleanup()
    {
        if (_cleaned) return;
        _cleaned = true;
        try { _uiTimer.Stop(); } catch { }
        try { _imageWatcher.Stop(); _xmlGate.Stop(); _cloud.Stop(); } catch { }
        try { _imageWatcher.Dispose(); _xmlGate.Dispose(); _cloud.Dispose(); } catch { }
        try { _tray.Visible = false; _tray.Dispose(); } catch { }
    }
}

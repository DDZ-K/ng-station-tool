namespace NgStationTool;

public sealed class ConfigForm : Form
{
    public AppConfig Result { get; private set; }
    private readonly TextBox _watch, _a, _b, _xml, _xmlArchive, _logs, _logsArchive;
    private readonly TextBox _okKey, _nokKey, _enterKey, _title, _process, _imageExts, _logExts, _okTokens, _nokTokens, _dateFormat;
    private readonly CheckBox _auto, _enterAfterAll, _appendDate, _skipSame, _onlyDirect;
        private readonly NumericUpDown _settle, _batchWait, _xmlReady, _logReady, _keyDelay, _activateDelay, _enterDelay, _enterRepeat, _keyRepeat;
        private readonly FlowLayoutPanel _sections;
        private readonly ListBox _partList;
        private readonly TextBox _partInput;

    private static readonly Color Bg = Color.FromArgb(244, 247, 250);
    private static readonly Color Ink = Color.FromArgb(30, 41, 59);
    private static readonly Color Muted = Color.FromArgb(100, 116, 139);
    private static readonly Color Blue = Color.FromArgb(37, 99, 235);

    public ConfigForm(AppConfig cfg)
    {
        Result = cfg;
        Text = "配置中心";
        Width = 900; Height = 760; MinimumSize = new Size(760, 620); StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Microsoft YaHei UI", 9F); BackColor = Bg;

        var header = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
        header.Controls.Add(new Label { Text = "配置中心", Left = 24, Top = 13, AutoSize = true, Font = new Font(Font.FontFamily, 15F, FontStyle.Bold), ForeColor = Ink });
        header.Controls.Add(new Label { Text = "点击分类展开；日常只需查看“核心目录”", Left = 26, Top = 43, AutoSize = true, ForeColor = Muted });

        _sections = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(18, 14, 18, 28), BackColor = Bg };

        var core = Section("📁  核心目录", "图片、XML 报文和 Log 的流转位置", true);
        _watch = Field(core.Body, "图片源目录", cfg.WatchRoot, "监听其下一级产品文件夹；文件夹名就是产品 DMC");
        _a = Field(core.Body, "A · 待 NG 图片目录", cfg.OutputRoot, "改名后先进入这里，等待 XML identifier");
        _b = Field(core.Body, "B · 待判断图片目录", cfg.JudgingRoot, "匹配后按 年/月/日 移入这里");
        _xml = Field(core.Body, "XML 报文监控目录", cfg.XmlWatchRoot, "读取 partReceived 节点的 identifier");
        _xmlArchive = Field(core.Body, "XML 报文归档目录", cfg.XmlArchiveRoot, "自动分为 已匹配 / 未匹配");
        _logs = Field(core.Body, "云端 Log 目录", cfg.CloudLogRoot, "文件名需包含完整图片名");
                _logsArchive = Field(core.Body, "Log 归档目录", cfg.CloudLogArchiveRoot, "OK/NOK 后移入");

                var parts = Section("🏷  服务料号", "只处理产品 DMC 中包含这些料号的型号；空列表=不过滤", true);
                (_partList, _partInput) = PartNumberEditor(parts.Body, cfg.PartNumbers);

                var keys = Section("⌨  判定与按键", "OK/NOK、回车和目标软件", false);
        _okKey = Field(keys.Body, "OK 键", cfg.OkKey);
        _nokKey = Field(keys.Body, "NOK 键", cfg.NokKey);
        _keyRepeat = Number(keys.Body, "9/7 连按次数", cfg.KeyRepeatCount, 1, 5);
        _enterKey = Field(keys.Body, "整组确认键", cfg.ConfirmEnterKey);
        _enterAfterAll = Check(keys.Body, "同一产品全部图片判断完后按确认键", cfg.EnterAfterFolderAllDone);
        _enterDelay = Number(keys.Body, "末张 9/7 后延迟回车（ms）", cfg.EnterAfterLastKeyDelayMs, 0, 10000);
        _enterRepeat = Number(keys.Body, "回车次数", cfg.EnterRepeatCount, 1, 5);
        _title = Field(keys.Body, "目标窗口标题（可空）", cfg.TargetWindowTitleContains);
        _process = Field(keys.Body, "目标进程名（可空）", cfg.TargetProcessName);
        _okTokens = Field(keys.Body, "OK 词表", string.Join(',', cfg.OkTokens));
        _nokTokens = Field(keys.Body, "NOK 词表", string.Join(',', cfg.NokTokens));

        var images = Section("🖼  图片与命名", "扩展名、日期和重复图片", false);
        _imageExts = Field(images.Body, "图片扩展名", string.Join(',', cfg.ImageExtensions));
        _logExts = Field(images.Body, "Log 扩展名", string.Join(',', cfg.LogExtensions));
        _appendDate = Check(images.Body, "完整图片名末尾追加日期", cfg.AppendDateToFileName);
        _dateFormat = Field(images.Body, "日期格式", cfg.FileNameDateFormat);
        _skipSame = Check(images.Body, "A 中同名同大小时跳过复制但仍入队", cfg.SkipIfSameSizeExists);
        _onlyDirect = Check(images.Body, "只处理产品文件夹内的直接图片", cfg.OnlyDirectImages);

        var advanced = Section("⚙  高级时序", "通常无需修改", false);
        _settle = Number(advanced.Body, "整夹静默时间（ms）", cfg.FolderSettleMs, 100, 60000);
        _batchWait = Number(advanced.Body, "图片等就绪上限（ms）", cfg.BatchMaxWaitMs, 500, 300000);
        _xmlReady = Number(advanced.Body, "XML 写完等待上限（ms）", cfg.XmlReadyBudgetMs, 100, 60000);
        _logReady = Number(advanced.Body, "Log 写完等待上限（ms）", cfg.LogReadyBudgetMs, 100, 60000);
        _keyDelay = Number(advanced.Body, "连按间隔（ms）", cfg.KeyPressDelayMs, 0, 5000);
        _activateDelay = Number(advanced.Body, "激活窗口后延迟（ms）", cfg.ActivateWindowDelayMs, 0, 5000);
        _auto = Check(advanced.Body, "程序打开后自动开始监控", cfg.AutoStartOnLaunch);

        _sections.Controls.AddRange(new Control[] { core.Container, parts.Container, keys.Container, images.Container, advanced.Container });

        var footer = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
        var footerActions = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 250, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Padding = new Padding(8, 14, 12, 8) };
        var save = ActionButton("保存配置", Blue, 110);
        var cancel = ActionButton("取消", Color.FromArgb(100, 116, 139), 90);
        save.Click += (_, _) => { Result = Apply(cfg); DialogResult = DialogResult.OK; Close(); };
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        footerActions.Controls.AddRange(new Control[] { save, cancel });
        footer.Controls.Add(footerActions);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty, Padding = Padding.Empty };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        layout.Controls.Add(header, 0, 0);
        layout.Controls.Add(_sections, 0, 1);
        layout.Controls.Add(footer, 0, 2);
        Controls.Add(layout);
        Resize += (_, _) => ResizeSections();
        Shown += (_, _) => ResizeSections();
    }

    private (Panel Container, FlowLayoutPanel Body) Section(string title, string subtitle, bool expanded)
    {
        var container = new Panel { Width = 820, BackColor = Color.White, Margin = new Padding(0, 0, 0, 10) };
        var body = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(18, 8, 18, 24), BackColor = Color.White, Top = 56, Left = 0, Width = 820, Visible = expanded };
        var header = new Panel { Height = 56, Dock = DockStyle.Top, Cursor = Cursors.Hand, BackColor = Color.White };
        var arrow = new Label { Text = expanded ? "▾" : "▸", AutoSize = true, Left = 18, Top = 18, ForeColor = Blue, Font = new Font(Font.FontFamily, 11F, FontStyle.Bold) };
        header.Controls.Add(new Label { Text = title, AutoSize = true, Left = 43, Top = 9, ForeColor = Ink, Font = new Font(Font.FontFamily, 11F, FontStyle.Bold) });
        header.Controls.Add(new Label { Text = subtitle, AutoSize = true, Left = 44, Top = 32, ForeColor = Muted });
        header.Controls.Add(arrow);
        void Toggle() { body.Visible = !body.Visible; arrow.Text = body.Visible ? "▾" : "▸"; container.Height = body.Visible ? body.PreferredSize.Height + 56 : 56; }
        header.Click += (_, _) => Toggle(); foreach (Control c in header.Controls) c.Click += (_, _) => Toggle();
        container.Controls.Add(body); container.Controls.Add(header);
        container.Height = expanded ? body.PreferredSize.Height + 56 : 56;
        body.SizeChanged += (_, _) => { if (body.Visible) container.Height = body.PreferredSize.Height + 56; };
        return (container, body);
    }

    private static (ListBox list, TextBox input) PartNumberEditor(FlowLayoutPanel body, IEnumerable<string> initial)
    {
        var wrap = new Panel { Width = 760, Height = 210, Margin = new Padding(0, 4, 0, 4), Tag = "parts" };
        wrap.Controls.Add(new Label
        {
            Text = "产品 DMC 包含任一料号即服务（一般 10 位；可维护多条）",
            Left = 0, Top = 0, Width = 720, Height = 22, ForeColor = Muted
        });
        var list = new ListBox
        {
            Left = 0, Top = 28, Width = 520, Height = 140,
            IntegralHeight = false, BorderStyle = BorderStyle.FixedSingle,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };
        foreach (var pn in PartNumberRules.Normalize(initial)) list.Items.Add(pn);
        var input = new TextBox { Left = 0, Top = 176, Width = 360, BorderStyle = BorderStyle.FixedSingle };
        var add = ActionButton("添加", Blue, 70); add.Left = 370; add.Top = 172; add.Height = 30;
        var del = ActionButton("删除选中", Color.FromArgb(100, 116, 139), 90); del.Left = 448; del.Top = 172; del.Height = 30;
        var clear = ActionButton("清空", Color.FromArgb(148, 163, 184), 70); clear.Left = 546; clear.Top = 172; clear.Height = 30;
        void AddCurrent()
        {
            foreach (var pn in PartNumberRules.Normalize(new[] { input.Text }))
            {
                var exists = false;
                foreach (var item in list.Items)
                {
                    if (string.Equals(item?.ToString(), pn, StringComparison.OrdinalIgnoreCase)) { exists = true; break; }
                }
                if (!exists) list.Items.Add(pn);
            }
            input.Clear();
            input.Focus();
        }
        add.Click += (_, _) => AddCurrent();
        input.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; AddCurrent(); }
        };
        del.Click += (_, _) =>
                {
                    var selected = list.SelectedItems.Cast<object>().Where(x => x != null).ToList();
                    foreach (var item in selected) list.Items.Remove(item!);
                };
        clear.Click += (_, _) => list.Items.Clear();
        wrap.Controls.AddRange(new Control[] { list, input, add, del, clear });
        body.Controls.Add(wrap);
        return (list, input);
    }

    private static TextBox Field(FlowLayoutPanel body, string label, string value, string? hint = null)
    {
        var wrap = new Panel { Width = 760, Height = hint == null ? 44 : 60, Margin = new Padding(0, 2, 0, 2), Tag = "field" };
        wrap.Controls.Add(new Label { Text = label, Left = 0, Top = 3, Width = 220, Height = 24, ForeColor = Ink, AutoEllipsis = false });
        var input = new TextBox { Text = value, Left = 230, Top = 0, Width = 510, BorderStyle = BorderStyle.FixedSingle, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        input.AutoCompleteMode = AutoCompleteMode.None;
        wrap.Controls.Add(input);
        if (hint != null) wrap.Controls.Add(new Label { Text = hint, Left = 230, Top = 29, Width = 510, Height = 24, ForeColor = Muted, AutoSize = false, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right });
        body.Controls.Add(wrap); return input;
    }

    private static NumericUpDown Number(FlowLayoutPanel body, string label, int value, int min, int max)
    {
        var wrap = new Panel { Width = 760, Height = 40, Margin = new Padding(0, 3, 0, 3), Tag = "number" };
        wrap.Controls.Add(new Label { Text = label, Left = 0, Top = 4, Width = 340, Height = 22, ForeColor = Ink });
        var input = new NumericUpDown { Left = 410, Top = 0, Width = 180, Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max) };
        wrap.Controls.Add(input); body.Controls.Add(wrap); return input;
    }

    private static CheckBox Check(FlowLayoutPanel body, string label, bool value)
    {
        var input = new CheckBox { Text = label, Checked = value, Width = 760, Height = 34, ForeColor = Ink, Margin = new Padding(0, 3, 0, 3), Tag = "check" };
        body.Controls.Add(input); return input;
    }

    private static Button ActionButton(string text, Color color, int width) => new() { Text = text, Width = width, Height = 36, BackColor = color, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, FlatAppearance = { BorderSize = 0 } };

    private void ResizeSections()
    {
        var width = Math.Max(680, _sections.ClientSize.Width - _sections.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 8);
        foreach (Control control in _sections.Controls)
        {
            control.Width = width;
            if (control.Controls.OfType<FlowLayoutPanel>().FirstOrDefault() is not { } body) continue;
            body.Width = width;
            var contentWidth = Math.Max(620, width - body.Padding.Horizontal - 4);
            foreach (Control row in body.Controls)
            {
                row.Width = contentWidth;
                if (row.Tag as string == "field")
                {
                    var left = Math.Clamp((int)(contentWidth * 0.30), 190, 240);
                    var input = row.Controls.OfType<TextBox>().First();
                    input.Left = left;
                    input.Width = Math.Max(300, contentWidth - left - 8);
                    foreach (var hint in row.Controls.OfType<Label>().Where(x => x.Top >= 28))
                    {
                        hint.Left = left;
                        hint.Width = input.Width;
                    }
                }
                else if (row.Tag as string == "check")
                                {
                                    row.Width = contentWidth;
                                }
                                else if (row.Tag as string == "parts")
                                {
                                    row.Width = contentWidth;
                                    if (row.Controls.OfType<ListBox>().FirstOrDefault() is { } lb)
                                    {
                                        lb.Width = Math.Max(360, contentWidth - 20);
                                    }
                                }
            }
            if (body.Visible) control.Height = body.PreferredSize.Height + 56;
        }
    }

    private AppConfig Apply(AppConfig c)
    {
        c.WatchRoot = _watch.Text.Trim(); c.OutputRoot = _a.Text.Trim(); c.JudgingRoot = _b.Text.Trim(); c.XmlWatchRoot = _xml.Text.Trim(); c.XmlArchiveRoot = _xmlArchive.Text.Trim();
        c.CloudLogRoot = _logs.Text.Trim(); c.CloudLogArchiveRoot = _logsArchive.Text.Trim();
        c.OkKey = _okKey.Text.Trim(); c.NokKey = _nokKey.Text.Trim(); c.KeyRepeatCount = (int)_keyRepeat.Value; c.ConfirmEnterKey = _enterKey.Text.Trim(); c.EnterAfterFolderAllDone = _enterAfterAll.Checked;
        c.EnterAfterLastKeyDelayMs = (int)_enterDelay.Value; c.EnterRepeatCount = (int)_enterRepeat.Value; c.TargetWindowTitleContains = _title.Text.Trim(); c.TargetProcessName = _process.Text.Trim();
        c.OkTokens = Split(_okTokens.Text); c.NokTokens = Split(_nokTokens.Text); c.ImageExtensions = Split(_imageExts.Text); c.LogExtensions = Split(_logExts.Text);
        c.AppendDateToFileName = _appendDate.Checked; c.FileNameDateFormat = _dateFormat.Text.Trim(); c.SkipIfSameSizeExists = _skipSame.Checked; c.OnlyDirectImages = _onlyDirect.Checked;
        c.FolderSettleMs = (int)_settle.Value; c.BatchMaxWaitMs = (int)_batchWait.Value; c.XmlReadyBudgetMs = (int)_xmlReady.Value; c.LogReadyBudgetMs = (int)_logReady.Value;
        c.KeyPressDelayMs = (int)_keyDelay.Value; c.ActivateWindowDelayMs = (int)_activateDelay.Value; c.AutoStartOnLaunch = _auto.Checked;
                c.PartNumbers = _partList.Items.Cast<object>()
                    .Select(x => x?.ToString() ?? "")
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                // 图片/云端模块默认保持开启；A 队列入队由 ImageCopyWatcher 强制执行，不再依赖旧开关。
                        // 仍把 EnqueueFromImageCopyFolderName 写成 true，避免旧字段在别处被误读成关闭。
                        c.EnableImageCopy = true;
                        c.EnableCloudRelease = true;
                        c.EnqueueFromImageCopyFolderName = true;
                        c.EnqueueFromNgImageWatch = false;
                        return c;
    }

    private static List<string> Split(string value) => value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
}

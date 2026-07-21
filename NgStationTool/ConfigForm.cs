namespace NgStationTool;

/// <summary>配置对话框：图片命名 / 云端 log 放行分栏，时序参数单独一页。</summary>
public sealed class ConfigForm : Form
{
    public AppConfig Result { get; private set; }

    // 通用
    private readonly CheckBox _autoStart;

    // 图片命名
    private readonly CheckBox _enableCopy;
    private readonly TextBox _watchRoot;
    private readonly TextBox _outputRoot;
    private readonly CheckBox _useDate;
    private readonly ComboBox _dateFrom;
    private readonly ComboBox _dateStyle;
    private readonly CheckBox _onlyDirect;
    private readonly CheckBox _skipSameSize;
    private readonly TextBox _imageExts;

    // 云端 log 放行
    private readonly CheckBox _enableCloud;
    private readonly TextBox _logRoot;
    private readonly TextBox _logArchive;
    private readonly TextBox _logExts;
    private readonly CheckBox _enqueueCopy;
    private readonly CheckBox _enqueueNgWatch;
    private readonly TextBox _ngRoot;
    private readonly ComboBox _dmcFromImage;
    private readonly TextBox _okTokens;
    private readonly TextBox _nokTokens;
    private readonly TextBox _okKey;
    private readonly TextBox _nokKey;
    private readonly NumericUpDown _keyRepeat;
    private readonly TextBox _confirmEnter;
    private readonly CheckBox _enterAfterAll;
    private readonly TextBox _winTitle;
    private readonly TextBox _procName;

    // 时序
    private readonly NumericUpDown _readyBudget;
    private readonly NumericUpDown _sizeStableChecks;
    private readonly NumericUpDown _sizeStableInterval;
    private readonly NumericUpDown _retryDelay;
    private readonly NumericUpDown _maxRetries;
    private readonly NumericUpDown _folderSettle;
    private readonly NumericUpDown _batchMaxWait;
    private readonly NumericUpDown _debounce;
    private readonly NumericUpDown _minImageBytes;
    private readonly NumericUpDown _pendingTimeout;
    private readonly NumericUpDown _logReadyBudget;
    private readonly NumericUpDown _keyPressDelay;
    private readonly NumericUpDown _activateDelay;
    private readonly NumericUpDown _uiRefresh;
    private readonly NumericUpDown _maxLogLines;

    // HARAN 门闩
    private readonly CheckBox _enableHaran;
    private readonly CheckBox _haranGateKeys;
    private readonly TextBox _haranFilter;
    private readonly TextBox _haranTplRoot;
    private readonly NumericUpDown _haranPoll;
    private readonly NumericUpDown _haranScore;
    private readonly NumericUpDown _haranStable;
    private readonly CheckBox _haranFromBottom;
    private readonly NumericUpDown _haranBottomOff;
    private readonly NumericUpDown _haranLeft;
    private readonly NumericUpDown _haranTop;
    private readonly NumericUpDown _haranW;
    private readonly NumericUpDown _haranH;

    public ConfigForm(AppConfig cfg)
    {
        Result = cfg;
        Text = "配置 · 图片 / 云端 / 时序 / HARAN门闩";
        Width = 780;
        Height = 640;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Microsoft YaHei UI", 9F);
        MinimizeBox = false;
        MaximizeBox = true;
        FormBorderStyle = FormBorderStyle.Sizable;

        var tabs = new TabControl { Dock = DockStyle.Fill };
        var tabCommon = new TabPage("① 通用");
        var tabImage = new TabPage("② 图片命名");
        var tabCloud = new TabPage("③ 云端 log 放行");
        var tabTime = new TabPage("④ 时序参数");
        var tabHaran = new TabPage("⑤ HARAN界面就绪");
        tabs.TabPages.AddRange(new[] { tabCommon, tabImage, tabCloud, tabTime, tabHaran });

        // ----- 通用 -----
        var p0 = MakeScroll();
        var y = 12;
        AddHeader(p0, ref y, "模块总开关（可独立开/关）");
        _enableCopy = AddCheck(p0, ref y, "启用【图片命名】：监视 WatchRoot，整夹静默后改名（门闩开时先暂存）", cfg.EnableImageCopy);
        _enableCloud = AddCheck(p0, ref y, "启用【云端 log 放行】：DMC 缓存 + 读 log + 按键", cfg.EnableCloudRelease);
        _enableHaran = AddCheck(p0, ref y, "启用【HARAN 界面就绪门闩】：匹配 Waiting 后才进 Out（见⑤）", cfg.EnableHaranUiGate);
        _autoStart = AddCheck(p0, ref y, "启动程序后自动点「开始」", cfg.AutoStartOnLaunch);
        AddNote(p0, ref y,
            "门闩开启时流程：\n" +
            "· 图片就绪 → 改名暂存（不进 Out、不入 DMC）\n" +
            "· HARAN 匹配到 Waiting for Input → 输出到 Out 并入 DMC\n" +
            "· 云端 log → 按键（可配置是否也要求 Waiting）\n" +
            "· 模板请用 HaranUiProbe v2.1 录到模板目录 idle/ waiting/");
        tabCommon.Controls.Add(p0);

        // ----- 图片命名 -----
        var p1 = MakeScroll();
        y = 12;
        AddHeader(p1, ref y, "目录");
        AddLabel(p1, ref y, "监控根目录 WatchRoot（其下一级子文件夹内的图片）");
        _watchRoot = AddTb(p1, ref y, cfg.WatchRoot);
        AddLabel(p1, ref y, "输出目录 OutputRoot（改名后复制到这里；勿与 WatchRoot 互相包含）");
        _outputRoot = AddTb(p1, ref y, cfg.OutputRoot);
        AddLabel(p1, ref y, "图片扩展名（逗号分隔）");
        _imageExts = AddTb(p1, ref y, string.Join(",", cfg.ImageExtensions));

        AddHeader(p1, ref y, "归档与行为");
        _useDate = AddCheck(p1, ref y, "输出按年/月/日归档", cfg.UseDateFolders);
        AddLabel(p1, ref y, "日期取自");
        _dateFrom = AddCombo(p1, ref y, new[] { "Now", "LastWriteTime" },
            string.Equals(cfg.DateFolderFrom, "LastWriteTime", StringComparison.OrdinalIgnoreCase) ? "LastWriteTime" : "Now");
        AddLabel(p1, ref y, "日期目录格式（ymd=年\\月\\日；dash=2026-07-19）");
        _dateStyle = AddCombo(p1, ref y, new[] { "ymd", "dash" },
            string.Equals(cfg.DateFolderStyle, "dash", StringComparison.OrdinalIgnoreCase) ? "dash" : "ymd");
        _onlyDirect = AddCheck(p1, ref y, "仅处理一级子目录内的直接图片（推荐开）", cfg.OnlyDirectImages);
        _skipSameSize = AddCheck(p1, ref y, "目标已存在且同大小则跳过（防重复拷）", cfg.SkipIfSameSizeExists);
        AddNote(p1, ref y, "改名规则：子文件夹名_原文件名 → 例如 ABC\\cam.jpg → ABC_cam.jpg");
        tabImage.Controls.Add(p1);

        // ----- 云端 log -----
        var p2 = MakeScroll();
        y = 12;
        AddHeader(p2, ref y, "log 目录");
        AddLabel(p2, ref y, "云端 log 目录（文件名包含 DMC 即可，支持 前缀+DMC）");
        _logRoot = AddTb(p2, ref y, cfg.CloudLogRoot);
        AddLabel(p2, ref y, "log 归档目录（判定成功后移入；可空）");
        _logArchive = AddTb(p2, ref y, cfg.CloudLogArchiveRoot);
        AddLabel(p2, ref y, "log 扩展名（逗号分隔，如 .log,.txt）");
        _logExts = AddTb(p2, ref y, string.Join(",", cfg.LogExtensions));

        AddHeader(p2, ref y, "DMC 如何进入缓存");
        _enqueueCopy = AddCheck(p2, ref y,
            "图片命名成功后：用「改名后完整文件名（无扩展名）」入 DMC 缓存（推荐）",
            cfg.EnqueueFromImageCopyFolderName);
        _enqueueNgWatch = AddCheck(p2, ref y,
            "另：监视 NG 图目录入缓存（一般关闭；与 OutputRoot 相同会自动禁用）",
            cfg.EnqueueFromNgImageWatch);
        AddLabel(p2, ref y, "NG 图目录（仅上一项开启时使用）");
        _ngRoot = AddTb(p2, ref y, cfg.NgImageRoot);
        AddLabel(p2, ref y, "NG 图如何取 DMC（FileStem=文件名；ParentFolder=父文件夹名）");
        _dmcFromImage = AddCombo(p2, ref y, new[] { "FileStem", "ParentFolder" },
            cfg.DmcFromImage is "ParentFolder" ? "ParentFolder" : "FileStem");

        AddHeader(p2, ref y, "结果判定与按键");
        AddLabel(p2, ref y, "判定方式：整份 log 出现词表独立词即可（Result=OK / Result=NOK；OK 不会误伤 NOK）");
        AddLabel(p2, ref y, "OK 词表（逗号分隔）");
        _okTokens = AddTb(p2, ref y, string.Join(",", cfg.OkTokens));
        AddLabel(p2, ref y, "NOK/NG 词表（逗号分隔；与 OK 是两套词，NOK 不会被当成 OK）");
        _nokTokens = AddTb(p2, ref y, string.Join(",", cfg.NokTokens));
        AddLabel(p2, ref y, "OK 键（例：NumPad9 / Home / PageUp / PgUp）");
        _okKey = AddTb(p2, ref y, cfg.OkKey);
        AddLabel(p2, ref y, "NOK 键（例：NumPad7 / End / PageDown / PgDn）");
        _nokKey = AddTb(p2, ref y, cfg.NokKey);
        AddLabel(p2, ref y, "按键重复次数");
        _keyRepeat = AddNum(p2, ref y, cfg.KeyRepeatCount, 1, 5);
        AddLabel(p2, ref y, "整夹全部判定结束后确认键（默认 Enter）");
        _confirmEnter = AddTb(p2, ref y, cfg.ConfirmEnterKey);
        _enterAfterAll = AddCheck(p2, ref y,
            "同文件夹多 NG：全部 OK/NOK 结束后才按回车（有超时则不回车；每条仍先按 OK/NG 键）",
            cfg.EnterAfterFolderAllDone);
        AddLabel(p2, ref y, "目标窗口标题包含（可空=当前前台）");
        _winTitle = AddTb(p2, ref y, cfg.TargetWindowTitleContains);
        AddLabel(p2, ref y, "目标进程名（不含 .exe，可空）");
        _procName = AddTb(p2, ref y, cfg.TargetProcessName);
        tabCloud.Controls.Add(p2);

        // ----- 时序 -----
        var p3 = MakeScroll();
        y = 12;
        AddHeader(p3, ref y, "图片就绪 / 防抖（毫秒，除非另注）");
        AddLabel(p3, ref y, "ReadyBudgetMs 单次就绪判定软上限");
        _readyBudget = AddNum(p3, ref y, cfg.ReadyBudgetMs, 50, 60000);
        AddLabel(p3, ref y, "SizeStableChecks 大小连续不变次数");
        _sizeStableChecks = AddNum(p3, ref y, cfg.SizeStableChecks, 1, 20);
        AddLabel(p3, ref y, "SizeStableIntervalMs 稳定采样间隔");
        _sizeStableInterval = AddNum(p3, ref y, cfg.SizeStableIntervalMs, 10, 5000);
        AddLabel(p3, ref y, "RetryDelayMs 占用/未就绪重试间隔");
        _retryDelay = AddNum(p3, ref y, cfg.RetryDelayMs, 10, 5000);
        AddLabel(p3, ref y, "MaxRetries 单次就绪最大重试次数");
        _maxRetries = AddNum(p3, ref y, cfg.MaxRetries, 1, 100);
        AddLabel(p3, ref y, "DebounceMs 成功后同路径防抖");
        _debounce = AddNum(p3, ref y, cfg.DebounceMs, 0, 10000);
        AddLabel(p3, ref y, "FolderSettleMs 整夹静默时间：最后一张新图后再等多久，一次性拷贝全夹");
        _folderSettle = AddNum(p3, ref y, cfg.FolderSettleMs, 100, 60000);
        AddLabel(p3, ref y, "BatchMaxWaitMs 整夹等就绪最长等待（超时只拷已就绪的）");
        _batchMaxWait = AddNum(p3, ref y, cfg.BatchMaxWaitMs, 500, 300000);
        AddLabel(p3, ref y, "MinImageBytes 最小有效图片字节");
        _minImageBytes = AddNum(p3, ref y, cfg.MinImageBytes, 1, 1_000_000);

        AddHeader(p3, ref y, "云端放行时序");
        AddLabel(p3, ref y, "PendingTimeoutSec 缓存等待 log 超时（秒，超时只清缓存不按键）");
        _pendingTimeout = AddNum(p3, ref y, cfg.PendingTimeoutSec, 5, 86400);
        AddLabel(p3, ref y, "LogReadyBudgetMs log 文件写完等待上限");
        _logReadyBudget = AddNum(p3, ref y, cfg.LogReadyBudgetMs, 50, 60000);
        AddLabel(p3, ref y, "KeyPressDelayMs 连按时间隔");
        _keyPressDelay = AddNum(p3, ref y, cfg.KeyPressDelayMs, 0, 5000);
        AddLabel(p3, ref y, "ActivateWindowDelayMs 激活窗口后延迟再按键");
        _activateDelay = AddNum(p3, ref y, cfg.ActivateWindowDelayMs, 0, 5000);

        AddHeader(p3, ref y, "界面 / 日志");
        AddLabel(p3, ref y, "UiRefreshMs 缓存列表刷新间隔");
        _uiRefresh = AddNum(p3, ref y, cfg.UiRefreshMs, 100, 10000);
        AddLabel(p3, ref y, "MaxLogLines 界面/文件日志保留行数");
        _maxLogLines = AddNum(p3, ref y, cfg.MaxLogLines, 50, 20000);
        AddNote(p3, ref y,
            "调参建议：\n" +
            "· 整夹多图：FolderSettleMs=800～1500（等拍完）；BatchMaxWaitMs=10～20 秒\n" +
            "· 拷贝慢/网络盘：加大 ReadyBudgetMs、SizeStableIntervalMs\n" +
            "· 云端慢：加大 PendingTimeoutSec\n" +
            "· log 半截写入：加大 LogReadyBudgetMs");
        tabTime.Controls.Add(p3);

        // ----- HARAN 界面就绪 -----
        var p4 = MakeScroll();
        y = 12;
        AddHeader(p4, ref y, "开关（与①通用中的门闩开关联动保存）");
        _haranGateKeys = AddCheck(p4, ref y, "按键也要求 Waiting（建议开：非 Waiting 不模拟 9/7/回车）", cfg.HaranGateKeys);
        AddHeader(p4, ref y, "窗口与模板");
        AddLabel(p4, ref y, "窗口标题过滤（分号分隔，命中任一）");
        _haranFilter = AddTb(p4, ref y, cfg.HaranWindowTitleFilter);
        AddLabel(p4, ref y, "模板根目录（下含 idle\\*.png 与 waiting\\*.png，可用 HaranUiProbe 录制）");
        var tpl = string.IsNullOrWhiteSpace(cfg.HaranTemplateRoot) ? cfg.ResolvedHaranTemplateRoot() : cfg.HaranTemplateRoot;
        _haranTplRoot = AddTb(p4, ref y, tpl);
        AddHeader(p4, ref y, "轮询 / 匹配");
        AddLabel(p4, ref y, "轮询间隔 HaranPollMs（建议 250～300）");
        _haranPoll = AddNum(p4, ref y, cfg.HaranPollMs, 100, 5000);
        AddLabel(p4, ref y, "相似度阈值 HaranMinScore ×100（例如 86 = 0.86）");
        _haranScore = AddNum(p4, ref y, (decimal)(cfg.HaranMinScore * 100), 50, 99);
        AddLabel(p4, ref y, "稳定帧数（连续 N 次一致才切换状态，防闪）");
        _haranStable = AddNum(p4, ref y, cfg.HaranStableFrames, 1, 10);
        AddHeader(p4, ref y, "截取区域 ROI（相对 HARAN 窗口）");
        _haranFromBottom = AddCheck(p4, ref y, "从窗口底边向上截取（推荐）", cfg.HaranRoiFromBottom);
        AddLabel(p4, ref y, "底边上移像素 BottomOffset");
        _haranBottomOff = AddNum(p4, ref y, cfg.HaranRoiBottomOffset, 0, 400);
        AddLabel(p4, ref y, "左边距 Left");
        _haranLeft = AddNum(p4, ref y, cfg.HaranRoiLeft, 0, 4000);
        AddLabel(p4, ref y, "顶边距 Top（仅关闭「从底边」时有效）");
        _haranTop = AddNum(p4, ref y, cfg.HaranRoiTop, 0, 4000);
        AddLabel(p4, ref y, "宽度 Width（0=铺满到右边）");
        _haranW = AddNum(p4, ref y, cfg.HaranRoiWidth, 0, 4000);
        AddLabel(p4, ref y, "高度 Height（状态栏建议 40～60）");
        _haranH = AddNum(p4, ref y, cfg.HaranRoiHeight, 8, 800);
        AddNote(p4, ref y,
            "模板：用 HaranUiProbe v2.1 框选同样区域，空闲多存几张（含 archiving），待判存 Waiting。\n" +
            "把 probe 的 templates\\idle 与 waiting 拷到本页「模板根目录」下即可。\n" +
            "改 ROI/模板后请停止再开始。");
        tabHaran.Controls.Add(p4);

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 52 };
        var btnOk = new Button { Text = "保存", Width = 100, Height = 32, Left = 520, Top = 10 };
        var btnCancel = new Button { Text = "取消", Width = 100, Height = 32, Left = 630, Top = 10 };
        btnOk.Click += (_, _) =>
        {
            Result = CloneApply(cfg);
            DialogResult = DialogResult.OK;
            Close();
        };
        btnCancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        bottom.Controls.Add(btnOk);
        bottom.Controls.Add(btnCancel);

        Controls.Add(tabs);
        Controls.Add(bottom);
        AcceptButton = btnOk;
        CancelButton = btnCancel;
    }

    private static Panel MakeScroll()
        => new() { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(8) };

    private static void AddHeader(Control parent, ref int y, string text)
    {
        var l = new Label
        {
            Text = text,
            Left = 12,
            Top = y,
            Width = 700,
            Height = 24,
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
            ForeColor = Color.FromArgb(20, 80, 140)
        };
        parent.Controls.Add(l);
        y += 28;
    }

    private static void AddNote(Control parent, ref int y, string text)
    {
        var l = new Label
        {
            Text = text,
            Left = 12,
            Top = y,
            Width = 700,
            Height = 90,
            ForeColor = Color.DimGray
        };
        parent.Controls.Add(l);
        y += 96;
    }

    private static void AddLabel(Control parent, ref int y, string text)
    {
        var l = new Label { Text = text, Left = 12, Top = y, Width = 700, Height = 20 };
        parent.Controls.Add(l);
        y += 22;
    }

    private static TextBox AddTb(Control parent, ref int y, string v)
    {
        var tb = new TextBox { Left = 12, Top = y, Width = 700, Height = 26, Text = v ?? "" };
        parent.Controls.Add(tb);
        y += 34;
        return tb;
    }

    private static CheckBox AddCheck(Control parent, ref int y, string text, bool on)
    {
        var c = new CheckBox { Left = 12, Top = y, Width = 700, Height = 24, Text = text, Checked = on };
        parent.Controls.Add(c);
        y += 28;
        return c;
    }

    private static ComboBox AddCombo(Control parent, ref int y, string[] items, string selected)
    {
        var cb = new ComboBox
        {
            Left = 12,
            Top = y,
            Width = 320,
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        cb.Items.AddRange(items);
        cb.SelectedItem = items.Contains(selected) ? selected : items[0];
        parent.Controls.Add(cb);
        y += 34;
        return cb;
    }

    private static NumericUpDown AddNum(Control parent, ref int y, decimal v, decimal min, decimal max)
    {
        var n = new NumericUpDown
        {
            Left = 12,
            Top = y,
            Width = 180,
            Minimum = min,
            Maximum = max,
            Value = Math.Min(max, Math.Max(min, v))
        };
        parent.Controls.Add(n);
        y += 34;
        return n;
    }

    private static List<string> SplitList(string text)
        => text.Split(new[] { ',', '，', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    private AppConfig CloneApply(AppConfig src)
    {
        src.AutoStartOnLaunch = _autoStart.Checked;

        // 图片命名
        src.EnableImageCopy = _enableCopy.Checked;
        src.WatchRoot = _watchRoot.Text.Trim();
        src.OutputRoot = _outputRoot.Text.Trim();
        var exts = SplitList(_imageExts.Text);
        if (exts.Count > 0)
            src.ImageExtensions = exts.Select(e => e.StartsWith('.') ? e : "." + e).ToList();
        src.UseDateFolders = _useDate.Checked;
        src.DateFolderFrom = _dateFrom.SelectedItem?.ToString() ?? "Now";
        src.DateFolderStyle = _dateStyle.SelectedItem?.ToString() ?? "ymd";
        src.OnlyDirectImages = _onlyDirect.Checked;
        src.SkipIfSameSizeExists = _skipSameSize.Checked;

        // 云端
        src.EnableCloudRelease = _enableCloud.Checked;
        src.CloudLogRoot = _logRoot.Text.Trim();
        src.CloudLogArchiveRoot = _logArchive.Text.Trim();
        var logExts = SplitList(_logExts.Text);
        if (logExts.Count > 0)
            src.LogExtensions = logExts.Select(e => e.StartsWith('.') ? e : "." + e).ToList();
        src.EnqueueFromImageCopyFolderName = _enqueueCopy.Checked;
        src.EnqueueFromNgImageWatch = _enqueueNgWatch.Checked;
        src.NgImageRoot = _ngRoot.Text.Trim();
        src.DmcFromImage = _dmcFromImage.SelectedItem?.ToString() ?? "FileStem";
        src.OkTokens = SplitList(_okTokens.Text);
        src.NokTokens = SplitList(_nokTokens.Text);
        if (src.OkTokens.Count == 0) src.OkTokens = new List<string> { "OK" };
        if (src.NokTokens.Count == 0) src.NokTokens = new List<string> { "NOK" };
        src.OkKey = _okKey.Text.Trim();
        src.NokKey = _nokKey.Text.Trim();
        src.KeyRepeatCount = (int)_keyRepeat.Value;
        src.ConfirmEnterKey = string.IsNullOrWhiteSpace(_confirmEnter.Text) ? "Enter" : _confirmEnter.Text.Trim();
        src.EnterAfterFolderAllDone = _enterAfterAll.Checked;
        src.TargetWindowTitleContains = _winTitle.Text.Trim();
        src.TargetProcessName = _procName.Text.Trim();

        // 时序
        src.ReadyBudgetMs = (int)_readyBudget.Value;
        src.SizeStableChecks = (int)_sizeStableChecks.Value;
        src.SizeStableIntervalMs = (int)_sizeStableInterval.Value;
        src.RetryDelayMs = (int)_retryDelay.Value;
        src.MaxRetries = (int)_maxRetries.Value;
        src.DebounceMs = (int)_debounce.Value;
        src.FolderSettleMs = (int)_folderSettle.Value;
        src.BatchMaxWaitMs = (int)_batchMaxWait.Value;
        src.MinImageBytes = (int)_minImageBytes.Value;
        src.PendingTimeoutSec = (int)_pendingTimeout.Value;
        src.LogReadyBudgetMs = (int)_logReadyBudget.Value;
        src.KeyPressDelayMs = (int)_keyPressDelay.Value;
        src.ActivateWindowDelayMs = (int)_activateDelay.Value;
        src.UiRefreshMs = (int)_uiRefresh.Value;
        src.MaxLogLines = (int)_maxLogLines.Value;

        // HARAN
        src.EnableHaranUiGate = _enableHaran.Checked;
        src.HaranGateKeys = _haranGateKeys.Checked;
        src.HaranWindowTitleFilter = _haranFilter.Text.Trim();
        src.HaranTemplateRoot = _haranTplRoot.Text.Trim();
        if (string.IsNullOrWhiteSpace(src.HaranTemplateRoot))
            src.HaranTemplateRoot = src.ResolvedHaranTemplateRoot();
        src.HaranPollMs = (int)_haranPoll.Value;
        src.HaranMinScore = (double)_haranScore.Value / 100.0;
        src.HaranStableFrames = (int)_haranStable.Value;
        src.HaranRoiFromBottom = _haranFromBottom.Checked;
        src.HaranRoiBottomOffset = (int)_haranBottomOff.Value;
        src.HaranRoiLeft = (int)_haranLeft.Value;
        src.HaranRoiTop = (int)_haranTop.Value;
        src.HaranRoiWidth = (int)_haranW.Value;
        src.HaranRoiHeight = (int)_haranH.Value;

        return src;
    }
}

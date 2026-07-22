using System.Text.Json;
using System.Text.Json.Serialization;

namespace NgStationTool;

/// <summary>全部可配置项，落盘 config.json，界面可改。</summary>
public sealed class AppConfig
{
    // ---- 模块开关 ----
    public bool EnableImageCopy { get; set; } = true;
    public bool EnableCloudRelease { get; set; } = true;

    // ---- 图片复制命名（兼容原 PS 脚本）----
    public string WatchRoot { get; set; } = @"E:\Download\AI\Test";
    public string OutputRoot { get; set; } = @"E:\Download\AI\TestOut";
    public List<string> ImageExtensions { get; set; } = new()
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp"
    };
    public int SizeStableChecks { get; set; } = 2;
    public int SizeStableIntervalMs { get; set; } = 200;
    public int RetryDelayMs { get; set; } = 150;
    public int MaxRetries { get; set; } = 10;
    public int ReadyBudgetMs { get; set; } = 1000;
    /// <summary>整夹批处理：文件夹内最后一张新图后静默多久（ms）再一次性拷贝全部。</summary>
    public int FolderSettleMs { get; set; } = 800;
    /// <summary>整夹批处理：从开始等就绪到强制开拷的最长等待（ms）。</summary>
    public int BatchMaxWaitMs { get; set; } = 15000;
    public int DebounceMs { get; set; } = 300;
    public int MinImageBytes { get; set; } = 32;
    public bool OnlyDirectImages { get; set; } = true;
    public bool SkipIfSameSizeExists { get; set; } = true;
    public bool UseDateFolders { get; set; } = true;
    /// <summary>LastWriteTime = 源文件修改时间；Now = 复制/处理时刻（产线日归档推荐）</summary>
    public string DateFolderFrom { get; set; } = "Now";
    /// <summary>ymd = 年\月\日；dash = 2026-07-19 单层</summary>
    public string DateFolderStyle { get; set; } = "ymd";

    // ---- 云端放行 / DMC 缓存 ----
    /// <summary>NG 图监视目录。文件名（去扩展名）默认即 DMC。可与 OutputRoot 相同或单独 NG 目录。</summary>
    public string NgImageRoot { get; set; } = @"E:\Download\AI\TestOut";
    /// <summary>云端结果 log 目录。</summary>
    public string CloudLogRoot { get; set; } = @"E:\Download\AI\CloudResult";
    /// <summary>处理后的 log 归档目录（空则改名为 .done）。</summary>
    public string CloudLogArchiveRoot { get; set; } = @"E:\Download\AI\CloudResult\done";
    public List<string> LogExtensions { get; set; } = new() { ".log", ".txt" };
    /// <summary>已废弃字段：仅兼容旧 config.json，结果改为全文包含匹配，不再按行号。</summary>
    public int ResultLineNumber { get; set; } = 2;
    public List<string> OkTokens { get; set; } = new() { "OK", "PASS" };
    public List<string> NokTokens { get; set; } = new() { "NOK", "NG", "FAIL" };
    public bool ResultMatchIgnoreCase { get; set; } = true;
    /// <summary>FileStem=文件名去扩展名；ParentFolder=父文件夹名。</summary>
    public string DmcFromImage { get; set; } = "FileStem";
    /// <summary>FileStem=log 文件名去扩展名。</summary>
    public string DmcFromLog { get; set; } = "FileStem";
    /// <summary>从有缓存到完成判定的超时（秒）。超时只清缓存，不按键。</summary>
    public int PendingTimeoutSec { get; set; } = 120;
    /// <summary>是否监视 NG 图目录进缓存。默认 false：推荐只用「复制成功后子文件夹名=DMC」入队，避免 OutputRoot 与 NgImage 相同导致改名图二次入缓存。</summary>
    public bool EnqueueFromNgImageWatch { get; set; } = false;
    /// <summary>图片复制成功后是否把「改名后完整文件名（无扩展名）」作为 DMC 入缓存。</summary>
    public bool EnqueueFromImageCopyFolderName { get; set; } = true;
    /// <summary>整夹全部 OK/NOK 后是否再按一次回车。</summary>
    public bool EnterAfterFolderAllDone { get; set; } = true;
    /// <summary>整夹确认键，默认 Enter。</summary>
    public string ConfirmEnterKey { get; set; } = "Enter";
    /// <summary>最后一张 9/7 之后，延迟多久再按回车（ms）。HARAN 常需要消化最后一键。</summary>
    public int EnterAfterLastKeyDelayMs { get; set; } = 500;
    /// <summary>回车连按次数（默认 1；个别机台可设 2）。</summary>
    public int EnterRepeatCount { get; set; } = 1;
    public int LogReadyBudgetMs { get; set; } = 2000;
    public int KeyPressDelayMs { get; set; } = 50;
    public int ActivateWindowDelayMs { get; set; } = 150;
    /// <summary>OK 键：NumPad9 / D9 / 等，见 KeyboardService。</summary>
    public string OkKey { get; set; } = "NumPad9";
    public string NokKey { get; set; } = "NumPad7";
    public int KeyRepeatCount { get; set; } = 1;
    /// <summary>目标窗口标题包含此字符串则先激活；空=发给当前前台。</summary>
    public string TargetWindowTitleContains { get; set; } = "";
    public string TargetProcessName { get; set; } = "";
    public bool RequireNumPad { get; set; } = true;
    /// <summary>启动后是否自动开始监视。</summary>
    public bool AutoStartOnLaunch { get; set; } = false;
    public int MaxLogLines { get; set; } = 500;
    public int UiRefreshMs { get; set; } = 500;

    // ---- HARAN 界面就绪门闩（Waiting for Input）----
    /// <summary>启用后：图片先暂存改名，匹配到 Waiting 再进 Out；可选限制按键。</summary>
    public bool EnableHaranUiGate { get; set; } = false;
    /// <summary>为 true 时仅在 Waiting 状态才允许模拟按键。</summary>
    public bool HaranGateKeys { get; set; } = true;
    public string HaranWindowTitleFilter { get; set; } = "HARAN;Repair Station;Semi-automatic";
    /// <summary>模板根目录（下含 idle/ waiting/）</summary>
    public string HaranTemplateRoot { get; set; } = "";
    public int HaranPollMs { get; set; } = 300;
    public double HaranMinScore { get; set; } = 0.86;
    /// <summary>连续多少帧一致才切换状态（防闪）</summary>
    public int HaranStableFrames { get; set; } = 2;
    public bool HaranRoiFromBottom { get; set; } = true;
    public int HaranRoiBottomOffset { get; set; } = 0;
    public int HaranRoiLeft { get; set; } = 0;
    public int HaranRoiTop { get; set; } = 0;
    /// <summary>0=铺满到右边</summary>
    public int HaranRoiWidth { get; set; } = 0;
    public int HaranRoiHeight { get; set; } = 48;
    /// <summary>A/B 重叠时只串行一个文件夹组：前组结束并离开 Waiting 后再开下一组。</summary>
    public bool HaranSerialSessions { get; set; } = true;
    /// <summary>离开 Waiting 后，再过多久才允许下一组会话（ms）。</summary>
    public int HaranNextSessionDelayMs { get; set; } = 500;
    /// <summary>
    /// 组结束后等界面离开 Waiting 的最长等待（ms）。0=一直等离开；
    /// 产线若连续 Waiting 不闪断，建议 3000～10000 作兜底，避免下一组暂存永久卡死。
    /// </summary>
    public int HaranLeaveWaitTimeoutMs { get; set; } = 5000;

    public string ResolvedHaranTemplateRoot()
    {
        if (!string.IsNullOrWhiteSpace(HaranTemplateRoot))
            return HaranTemplateRoot;
        return Path.Combine(AppContext.BaseDirectory, "haran-templates");
    }

    public static string DefaultPath
    {
        get
        {
            var dir = AppContext.BaseDirectory;
            return Path.Combine(dir, "config.json");
        }
    }

    public static AppConfig Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOpts());
                if (cfg != null)
                {
                    if (string.IsNullOrWhiteSpace(cfg.HaranTemplateRoot))
                        cfg.HaranTemplateRoot = cfg.ResolvedHaranTemplateRoot();
                    return cfg;
                }
            }
        }
        catch
        {
            // 损坏则回落默认并尝试备份
            try
            {
                if (File.Exists(path))
                    File.Copy(path, path + ".bad." + DateTime.Now.ToString("yyyyMMddHHmmss"), true);
            }
            catch { /* ignore */ }
        }
        var fresh = new AppConfig();
        try { fresh.Save(path); } catch { /* ignore */ }
        return fresh;
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(this, JsonOpts());
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Copy(tmp, path, true);
        try { File.Delete(tmp); } catch { /* ignore */ }
    }

    private static JsonSerializerOptions JsonOpts() => new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() }
    };

    public HashSet<string> ImageExtSet()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in ImageExtensions)
        {
            var x = e.StartsWith('.') ? e : "." + e;
            set.Add(x);
        }
        return set;
    }

    public HashSet<string> LogExtSet()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in LogExtensions)
        {
            var x = e.StartsWith('.') ? e : "." + e;
            set.Add(x);
        }
        return set;
    }
}

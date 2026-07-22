namespace NgStationTool.Services;

/// <summary>
/// A/B 双片重叠：同一时刻只放行一个文件夹组。
/// 当前组整组 9/7+回车（或超时结束）后，等界面离开 Waiting，再延迟，再开下一组。
/// </summary>
public sealed class JudgmentSessionCoordinator
{
    private readonly AppLogger _log;
    private readonly Func<AppConfig> _cfg;
    private readonly object _lock = new();

    private string? _activeFolder;
    /// <summary>当前组判定流程已结束，等界面离开 Waiting。</summary>
    private bool _awaitLeaveWaiting;
    private long _cooldownUntilTick;

    public JudgmentSessionCoordinator(AppLogger log, Func<AppConfig> cfg)
    {
        _log = log;
        _cfg = cfg;
    }

    public string? ActiveFolder
    {
        get { lock (_lock) return _activeFolder; }
    }

    public bool HasActiveSession
    {
        get { lock (_lock) return !string.IsNullOrEmpty(_activeFolder) || _awaitLeaveWaiting; }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _activeFolder = null;
            _awaitLeaveWaiting = false;
            _cooldownUntilTick = 0;
        }
    }

    /// <summary>
    /// Waiting 轮询时：是否允许 Flush；串行时返回应输出的文件夹名。
    /// 非串行：folder=null 且 return true → 调用方 Flush 全部。
    /// </summary>
    public bool TryGetFolderToFlush(Func<string?> peekFirstStagedFolder, out string? folder)
    {
        folder = null;
        var cfg = _cfg();
        if (!cfg.EnableHaranUiGate || !cfg.HaranSerialSessions)
        {
            folder = null;
            return true;
        }

        lock (_lock)
        {
            var now = Environment.TickCount64;
            if (now < _cooldownUntilTick)
                return false;

            // 组已判完，必须等离开 Waiting 后再开新组（期间也不再 Flush 其它组）
            if (_awaitLeaveWaiting)
                return false;

            if (!string.IsNullOrEmpty(_activeFolder))
            {
                folder = _activeFolder;
                return true;
            }

            var next = peekFirstStagedFolder();
            if (string.IsNullOrWhiteSpace(next))
                return false;

            _activeFolder = next.Trim();
            _awaitLeaveWaiting = false;
            folder = _activeFolder;
            _log.Info("会话",
                $"开始判定会话 文件夹组={_activeFolder}（其它组暂存排队）");
            return true;
        }
    }

    /// <summary>整组结束（已回车 / 超时不回车）。uiStillWaiting=界面是否仍匹配 Waiting。</summary>
    public void NotifyFolderGroupFinished(string folderKey, string reason, bool uiStillWaiting)
    {
        folderKey = (folderKey ?? "").Trim();
        if (string.IsNullOrEmpty(folderKey)) return;

        var cfg = _cfg();
        if (!cfg.EnableHaranUiGate || !cfg.HaranSerialSessions) return;

        lock (_lock)
        {
            if (!string.Equals(_activeFolder, folderKey, StringComparison.OrdinalIgnoreCase))
            {
                _log.Skip("会话",
                    $"组结束忽略（非当前会话）组={folderKey} 当前={_activeFolder ?? "-"} 原因={reason}");
                return;
            }

            _awaitLeaveWaiting = true;
            _log.Info("会话",
                $"当前组判定流程结束 组={folderKey} 原因={reason} → 等待界面离开 Waiting");

            // 若此时已不在 Waiting（回车后界面已变），立即进入延迟
            if (!uiStillWaiting)
                ApplyLeaveWaitingLocked("组结束时已非Waiting");
        }
    }

    /// <summary>仅在「等离开 Waiting」阶段处理非 Waiting；判定中途闪断不拆会话。</summary>
    public void OnHaranMatchKind(HaranUiMatchService.MatchKind kind)
    {
        var cfg = _cfg();
        if (!cfg.EnableHaranUiGate || !cfg.HaranSerialSessions) return;

        lock (_lock)
        {
            if (kind == HaranUiMatchService.MatchKind.Waiting)
                return;
            if (!_awaitLeaveWaiting)
                return;

            ApplyLeaveWaitingLocked($"界面→{kind}");
        }
    }

    private void ApplyLeaveWaitingLocked(string why)
    {
        var delay = Math.Max(0, _cfg().HaranNextSessionDelayMs);
        _cooldownUntilTick = Environment.TickCount64 + delay;
        var prev = _activeFolder;
        _activeFolder = null;
        _awaitLeaveWaiting = false;
        _log.Info("会话",
            $"离开 Waiting（{why}）→ 清空会话 原组={prev ?? "-"}，{delay}ms 后可开下一组");
    }

    public string Describe()
    {
        lock (_lock)
        {
            if (_cooldownUntilTick > Environment.TickCount64)
            {
                var left = _cooldownUntilTick - Environment.TickCount64;
                return $"冷却{left}ms";
            }
            if (_awaitLeaveWaiting)
                return $"等离Wait 组={_activeFolder}";
            if (!string.IsNullOrEmpty(_activeFolder))
                return $"会话={_activeFolder}";
            return "可接下组";
        }
    }
}

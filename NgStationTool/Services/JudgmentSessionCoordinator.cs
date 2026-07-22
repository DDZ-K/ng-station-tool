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
    private long _awaitLeaveSinceTick;
    private long _lastBlockLogTick;

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
            _awaitLeaveSinceTick = 0;
        }
    }

    /// <summary>阻塞原因（空=不阻塞）。</summary>
    public string? BlockReason
    {
        get
        {
            lock (_lock)
            {
                var now = Environment.TickCount64;
                if (now < _cooldownUntilTick)
                    return $"冷却中剩余{_cooldownUntilTick - now}ms";
                if (_awaitLeaveWaiting)
                    return $"等离开Waiting 组={_activeFolder}";
                return null;
            }
        }
    }

    /// <summary>
    /// Waiting / 暂存就绪时：是否允许 Flush；串行时返回应输出的文件夹名。
    /// 注意：peek 在锁外调用，避免与 ImageCopyWatcher 锁交叉死锁。
    /// </summary>
    public bool TryGetFolderToFlush(Func<string?> peekFirstStagedFolder, out string? folder, out string? blockReason)
    {
        folder = null;
        blockReason = null;
        var cfg = _cfg();
        if (!cfg.EnableHaranUiGate || !cfg.HaranSerialSessions)
        {
            folder = null;
            return true;
        }

        // 锁外 peek，避免 session锁 + flush锁 嵌套
        string? peeked;
        try { peeked = peekFirstStagedFolder(); }
        catch (Exception ex)
        {
            blockReason = "peek暂存失败:" + ex.Message;
            return false;
        }

        lock (_lock)
        {
            var now = Environment.TickCount64;

            // 等离开 Waiting 超时兜底：界面若一直 Waiting（产线常连续 Waiting），超时后仍允许下一组
            if (_awaitLeaveWaiting)
            {
                var maxWait = Math.Max(0, cfg.HaranLeaveWaitTimeoutMs);
                if (maxWait > 0 && _awaitLeaveSinceTick > 0 && now - _awaitLeaveSinceTick >= maxWait)
                {
                    _log.Warn("会话",
                        $"等离开Waiting 超时 {maxWait}ms → 强制结束会话并进入冷却（界面可能连续 Waiting）");
                    ApplyLeaveWaitingLocked("等离开超时兜底");
                }
            }

            now = Environment.TickCount64;
            if (now < _cooldownUntilTick)
            {
                blockReason = $"冷却中剩余{_cooldownUntilTick - now}ms";
                return false;
            }

            if (_awaitLeaveWaiting)
            {
                blockReason = $"等离开Waiting 组={_activeFolder}";
                return false;
            }

            if (!string.IsNullOrEmpty(_activeFolder))
            {
                // 活动会话：只 Flush 该组；若暂存里已没有该组、但有其它组，且调用方会处理 0 输出
                folder = _activeFolder;
                return true;
            }

            if (string.IsNullOrWhiteSpace(peeked))
            {
                blockReason = "暂存为空或无文件夹名";
                return false;
            }

            _activeFolder = peeked.Trim();
            _awaitLeaveWaiting = false;
            folder = _activeFolder;
            _log.Info("会话",
                $"开始判定会话 文件夹组={_activeFolder}（其它组暂存排队）");
            return true;
        }
    }

    /// <summary>整组结束（已回车 / 超时不回车）。</summary>
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
            _awaitLeaveSinceTick = Environment.TickCount64;
            _log.Info("会话",
                $"当前组判定流程结束 组={folderKey} 原因={reason} → 等待界面离开 Waiting");

            if (!uiStillWaiting)
                ApplyLeaveWaitingLocked("组结束时已非Waiting");
        }
    }

    /// <summary>仅在「等离开 Waiting」阶段处理非 Waiting。</summary>
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

    /// <summary>活动会话下：若该组已无待判 DMC、暂存也无该组，可释放会话接下组。</summary>
    public void TryReleaseOrphanSession(string? activeFolderStillStaged, int pendingInActiveFolder)
    {
        lock (_lock)
        {
            if (string.IsNullOrEmpty(_activeFolder) || _awaitLeaveWaiting)
                return;
            // 暂存还有本组 → 不释放
            if (!string.IsNullOrEmpty(activeFolderStillStaged)
                && string.Equals(activeFolderStillStaged, _activeFolder, StringComparison.OrdinalIgnoreCase))
                return;
            if (pendingInActiveFolder > 0)
                return;

            // 活动会话组既无暂存也无缓存 → 空会话，直接清掉以便接下组
            _log.Warn("会话",
                $"空会话释放 组={_activeFolder}（无暂存且无待判DMC）");
            _activeFolder = null;
            _awaitLeaveWaiting = false;
        }
    }

    private void ApplyLeaveWaitingLocked(string why)
    {
        var delay = Math.Max(0, _cfg().HaranNextSessionDelayMs);
        _cooldownUntilTick = Environment.TickCount64 + delay;
        var prev = _activeFolder;
        _activeFolder = null;
        _awaitLeaveWaiting = false;
        _awaitLeaveSinceTick = 0;
        _log.Info("会话",
            $"离开 Waiting（{why}）→ 清空会话 原组={prev ?? "-"}，{delay}ms 后可开下一组");
    }

    public void LogBlockedThrottled(string reason, int stagedCount)
    {
        var t = Environment.TickCount64;
        if (t - _lastBlockLogTick < 3000) return;
        _lastBlockLogTick = t;
        _log.Warn("会话", $"暂存={stagedCount} 暂不能输出：{reason} | {Describe()}");
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

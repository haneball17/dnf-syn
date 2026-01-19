using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Windows.Threading;

namespace DNFSyncBox;

public sealed class SyncController : IDisposable
{
    private const bool VerboseLogging = true;
    private static readonly object LogFileLock = new();
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DNFSyncBox",
        "logs");
    private static readonly string LogFilePath = Path.Combine(LogDirectory, "latest.log");

    // 统一保护核心状态（窗口快照、暂停状态、按键状态）。
    private readonly object _stateLock = new();
    private readonly WindowManager _windowManager = new("DNF Taiwan", "dnf.exe");
    private readonly KeyboardHook _keyboardHook = new();
    private readonly KeyStateTracker _keyState = new();
    private readonly DispatcherTimer _scanTimer;

    private WindowSnapshot _snapshot = WindowSnapshot.Empty;
    private bool _userPaused;
    private bool _autoPaused = true;
    private bool _altDown;
    private bool _hotkeyDown;
    private string _lastSnapshotSignature = string.Empty;

    /// <summary>
    /// 状态变化事件：用于 UI 展示。
    /// </summary>
    public event Action<SyncStatus>? StatusChanged;
    /// <summary>
    /// 日志事件：用于 UI 记录。
    /// </summary>
    public event Action<string>? LogAdded;

    /// <summary>
    /// 初始化扫描定时器（每秒刷新一次窗口列表）。
    /// </summary>
    public SyncController()
    {
        _scanTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _scanTimer.Tick += (_, _) => RefreshWindows();
    }

    /// <summary>
    /// 启动同步控制器：安装钩子、扫描窗口、启动定时器。
    /// </summary>
    public void Start()
    {
        _keyboardHook.KeyEvent += OnKeyEvent;
        _keyboardHook.Install();
        RefreshWindows();
        _scanTimer.Start();
    }

    /// <summary>
    /// 刷新窗口快照并根据前台状态触发自动暂停/恢复。
    /// </summary>
    public void RefreshWindows()
    {
        var snapshot = _windowManager.Refresh();
        var autoPausedChanged = false;
        bool autoPaused;

        lock (_stateLock)
        {
            _snapshot = snapshot;
            // 前台不是 DNF 时进入自动暂停，防止误同步。
            var newAutoPaused = !snapshot.ForegroundIsDnf;
            if (newAutoPaused != _autoPaused)
            {
                _autoPaused = newAutoPaused;
                autoPausedChanged = true;
            }
            autoPaused = _autoPaused;
        }

        if (autoPausedChanged && autoPaused)
        {
            // 自动暂停时先清键，避免卡键。
            ClearStuckKeys();
            Log("前台非 DNF，已自动暂停同步");
        }
        else if (autoPausedChanged && !autoPaused)
        {
            Log("前台已回到 DNF，自动暂停解除");
        }

        LogSnapshotIfNeeded(snapshot);
        RaiseStatusChanged();
    }

    /// <summary>
    /// 手动暂停/恢复；暂停时强制清键。
    /// </summary>
    public void TogglePause()
    {
        bool isPausedNow;
        lock (_stateLock)
        {
            _userPaused = !_userPaused;
            isPausedNow = _userPaused;
        }

        if (isPausedNow)
        {
            // 手动暂停同样需要清键。
            ClearStuckKeys();
            Log("已手动暂停同步");
        }
        else
        {
            Log("已恢复同步");
        }

        RaiseStatusChanged();
    }

    /// <summary>
    /// 处理键盘钩子事件：热键、过滤、同步投递。
    /// </summary>
    private void OnKeyEvent(Keys key, bool isDown)
    {
        // 记录 Alt 状态，用于组合热键识别。
        if (IsAltKey(key))
        {
            lock (_stateLock)
            {
                _altDown = isDown;
            }
            LogVerbose($"热键辅助键 Alt：{(isDown ? "按下" : "抬起")}");
            return;
        }

        // Alt + . 热键：只在按下时触发一次，避免连发。
        if (key == Keys.OemPeriod)
        {
            var shouldToggle = false;
            lock (_stateLock)
            {
                if (isDown && _altDown && !_hotkeyDown)
                {
                    _hotkeyDown = true;
                    shouldToggle = true;
                }
                else if (!isDown)
                {
                    _hotkeyDown = false;
                }
            }

            if (shouldToggle)
            {
                LogVerbose("热键触发：Alt + .");
                TogglePause();
            }
            return;
        }

        // MVP 仅同步方向键。
        if (!IsDirectionKey(key))
        {
            LogVerbose($"忽略非方向键：{key} {(isDown ? "按下" : "抬起")}");
            return;
        }

        WindowSnapshot snapshot;
        bool paused;
        bool changed;

        lock (_stateLock)
        {
            // 更新按键状态，用于去重与清键。
            changed = _keyState.SetState(key, isDown);
            paused = IsPausedLocked();
            snapshot = _snapshot;
        }

        if (!changed && isDown)
        {
            // 忽略重复按下，避免后端收到过多重复消息。
            LogVerbose($"忽略重复按下：{key}");
            return;
        }

        LogVerbose($"方向键事件：{key} {(isDown ? "按下" : "抬起")} | 暂停={paused} | 前台DNF={snapshot.ForegroundIsDnf} | 从控={snapshot.SlaveHandles.Count}");

        // 暂停或前台不是 DNF 时不投递。
        if (paused || !snapshot.ForegroundIsDnf)
        {
            LogVerbose("已拦截：暂停或前台非 DNF");
            return;
        }

        foreach (var handle in snapshot.SlaveHandles)
        {
            var attempts = KeySender.PostKey(handle, key, isDown);
            foreach (var attempt in attempts)
            {
                var targetLabel = attempt.IsChild ? "子窗口" : "主窗口";
                var resultLabel = attempt.Success ? "成功" : $"失败(Win32Error={attempt.ErrorCode})";
                var classLabel = string.IsNullOrWhiteSpace(attempt.ClassName) ? "未知类名" : attempt.ClassName;
                var titleLabel = string.IsNullOrWhiteSpace(attempt.Title) ? "无标题" : attempt.Title;
                var channelLabel = attempt.Channel == KeySender.SendChannel.ThreadMessage
                    ? $"线程消息(TID={attempt.ThreadId})"
                    : "窗口消息";
                LogVerbose($"投递 {targetLabel} 0x{attempt.Target.ToInt64():X} [{classLabel}] \"{titleLabel}\"：{key} {(isDown ? "Down" : "Up")} {channelLabel} -> {resultLabel}");
            }
        }
    }

    /// <summary>
    /// 清理所有按下状态，并向从控窗口发送 KeyUp，避免卡键。
    /// </summary>
    private void ClearStuckKeys()
    {
        List<Keys> downKeys;
        WindowSnapshot snapshot;

        lock (_stateLock)
        {
            // 复制快照后清空，避免锁内执行投递。
            downKeys = new List<Keys>(_keyState.GetDownKeys());
            _keyState.Clear();
            snapshot = _snapshot;
        }

        if (downKeys.Count == 0)
        {
            return;
        }

        LogVerbose($"清键触发：{string.Join(", ", downKeys)}");
        foreach (var key in downKeys)
        {
            foreach (var handle in snapshot.SlaveHandles)
            {
                var attempts = KeySender.PostKey(handle, key, false);
                foreach (var attempt in attempts)
                {
                    var targetLabel = attempt.IsChild ? "子窗口" : "主窗口";
                    var resultLabel = attempt.Success ? "成功" : $"失败(Win32Error={attempt.ErrorCode})";
                    var classLabel = string.IsNullOrWhiteSpace(attempt.ClassName) ? "未知类名" : attempt.ClassName;
                    var titleLabel = string.IsNullOrWhiteSpace(attempt.Title) ? "无标题" : attempt.Title;
                    var channelLabel = attempt.Channel == KeySender.SendChannel.ThreadMessage
                        ? $"线程消息(TID={attempt.ThreadId})"
                        : "窗口消息";
                    LogVerbose($"清键 {targetLabel} 0x{attempt.Target.ToInt64():X} [{classLabel}] \"{titleLabel}\"：{key} Up {channelLabel} -> {resultLabel}");
                }
            }
        }
    }

    /// <summary>
    /// 汇总当前状态并通知 UI。
    /// </summary>
    private void RaiseStatusChanged()
    {
        WindowSnapshot snapshot;
        bool paused;
        bool autoPaused;

        lock (_stateLock)
        {
            snapshot = _snapshot;
            paused = IsPausedLocked();
            autoPaused = _autoPaused;
        }

        StatusChanged?.Invoke(new SyncStatus
        {
            IsPaused = paused,
            IsAutoPaused = autoPaused,
            ForegroundIsDnf = snapshot.ForegroundIsDnf,
            MasterHandle = snapshot.MasterHandle,
            SlaveCount = snapshot.SlaveHandles.Count,
            TotalCount = snapshot.TotalCount
        });
    }

    /// <summary>
    /// 记录一条时间戳日志。
    /// </summary>
    private void Log(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}";
        LogAdded?.Invoke(line);
        AppendLogFile(line);
    }

    private static void AppendLogFile(string line)
    {
        try
        {
            lock (LogFileLock)
            {
                Directory.CreateDirectory(LogDirectory);
                File.AppendAllText(LogFilePath, line + Environment.NewLine);
            }
        }
        catch
        {
            // 文件日志失败不影响主流程，避免影响同步稳定性。
        }
    }

    private void LogVerbose(string message)
    {
        if (!VerboseLogging)
        {
            return;
        }

        Log($"[调试] {message}");
    }

    /// <summary>
    /// 计算综合暂停状态（手动暂停或自动暂停）。
    /// </summary>
    private bool IsPausedLocked() => _userPaused || _autoPaused;

    /// <summary>
    /// 方向键过滤。
    /// </summary>
    private static bool IsDirectionKey(Keys key)
    {
        return key is Keys.Left or Keys.Right or Keys.Up or Keys.Down;
    }

    /// <summary>
    /// Alt 键判定（系统菜单键）。
    /// </summary>
    private static bool IsAltKey(Keys key)
    {
        return key is Keys.Menu or Keys.LMenu or Keys.RMenu;
    }

    private void LogSnapshotIfNeeded(WindowSnapshot snapshot)
    {
        if (!VerboseLogging)
        {
            return;
        }

        var master = snapshot.MasterHandle == IntPtr.Zero
            ? "无"
            : $"0x{snapshot.MasterHandle.ToInt64():X}";
        var slaves = snapshot.SlaveHandles.Count == 0
            ? "无"
            : string.Join(", ", snapshot.SlaveHandles.Select(h => $"0x{h.ToInt64():X}"));
        var signature = $"{master}|{snapshot.ForegroundIsDnf}|{slaves}";

        if (signature == _lastSnapshotSignature)
        {
            return;
        }

        _lastSnapshotSignature = signature;
        LogVerbose($"窗口扫描：总数={snapshot.TotalCount} 主控={master} 从控={slaves} 前台DNF={snapshot.ForegroundIsDnf}");
    }

    /// <summary>
    /// 停止扫描并卸载钩子。
    /// </summary>
    public void Dispose()
    {
        _scanTimer.Stop();
        _keyboardHook.KeyEvent -= OnKeyEvent;
        _keyboardHook.Dispose();
    }
}

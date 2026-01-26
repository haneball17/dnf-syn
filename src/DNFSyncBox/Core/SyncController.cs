using System;
using System.IO;
using System.Linq;
using System.Threading;
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
    private readonly SharedMemoryWriter _sharedMemory = new();
    private readonly KeyboardProfileManager _profileManager = new();
    private readonly DispatcherTimer _scanTimer;
    private readonly System.Threading.Timer _heartbeatTimer;

    private readonly byte[] _keyboardState = new byte[SharedMemoryConstants.KeyCount];
    private readonly uint[] _edgeCounter = new uint[SharedMemoryConstants.KeyCount];
    private readonly byte[] _targetMask = new byte[SharedMemoryConstants.KeyCount];
    private readonly byte[] _blockMask = new byte[SharedMemoryConstants.KeyCount];
    private readonly byte[] _toggleState = new byte[SharedMemoryConstants.KeyCount];

    private WindowSnapshot _snapshot = WindowSnapshot.Empty;
    private KeyboardProfile _activeProfile;
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
        _activeProfile = _profileManager.ActiveProfile;
        _scanTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _scanTimer.Tick += (_, _) => RefreshWindows();
        _heartbeatTimer = new System.Threading.Timer(HeartbeatTick, null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// 启动同步控制器：安装钩子、扫描窗口、启动定时器。
    /// </summary>
    public void Start()
    {
        _keyboardHook.KeyEvent += OnKeyEvent;
        _keyboardHook.Install();

        TryInitializeSharedMemory();
        ReloadProfileIfNeeded(force: true);
        Log($"配置文件路径：{_profileManager.ConfigPath}");

        RefreshWindows();
        _scanTimer.Start();
        _heartbeatTimer.Change(0, SharedMemoryConstants.HeartbeatIntervalMs);
    }

    /// <summary>
    /// 刷新窗口快照并根据前台状态触发自动暂停/恢复。
    /// </summary>
    public void RefreshWindows()
    {
        ReloadProfileIfNeeded(force: false);

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
            if (!_sharedMemory.IsReady)
            {
                TryInitializeSharedMemory();
            }
            // 自动暂停时先清键，避免卡键。
            ClearStuckKeys();
            Log("前台非 DNF，已自动暂停同步");
        }
        else if (autoPausedChanged && !autoPaused)
        {
            Log("前台已回到 DNF，自动暂停解除");
            if (!_sharedMemory.IsReady)
            {
                TryInitializeSharedMemory();
            }
            // 解除自动暂停后立即发布，避免等待心跳或按键事件。
            PublishSnapshot(forceClear: false);
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

        // 确保共享内存就绪，避免暂停状态写入被延迟。
        if (!_sharedMemory.IsReady)
        {
            TryInitializeSharedMemory();
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
            PublishSnapshot(forceClear: false);
        }

        RaiseStatusChanged();
    }

    /// <summary>
    /// 处理键盘钩子事件：热键、过滤、共享内存写入。
    /// </summary>
    private void OnKeyEvent(Keys key, bool isDown)
    {
        var isAltKey = IsAltKey(key);
        if (isAltKey)
        {
            lock (_stateLock)
            {
                _altDown = isDown;
            }
            LogVerbose($"热键辅助键 Alt：{(isDown ? "按下" : "抬起")}");
        }

        if (key == Keys.OemPeriod)
        {
            var shouldToggle = false;
            var altDown = false;
            lock (_stateLock)
            {
                altDown = _altDown;
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

            if (altDown)
            {
                // 热键组合时不写入 OemPeriod，避免误触同步。
                return;
            }
        }

        var vKey = (int)(key & Keys.KeyCode);
        if (vKey < 0 || vKey >= SharedMemoryConstants.KeyCount)
        {
            LogVerbose($"忽略超范围键：{key}");
            return;
        }

        WindowSnapshot snapshot;
        bool paused;
        bool changed;

        lock (_stateLock)
        {
            snapshot = _snapshot;
            paused = IsPausedLocked();

            if (paused || !snapshot.ForegroundIsDnf)
            {
                return;
            }

            changed = _keyState.SetState(vKey, isDown);
        }

        if (!changed)
        {
            LogVerbose($"忽略重复按键：{key}");
            return;
        }

        LogVerbose($"键盘事件：{key} {(isDown ? "按下" : "抬起")} | 暂停={paused} | 前台DNF={snapshot.ForegroundIsDnf}");
        PublishSnapshot(forceClear: false);
    }

    /// <summary>
    /// 清理所有按下状态并通知共享内存，避免卡键。
    /// </summary>
    private void ClearStuckKeys()
    {
        lock (_stateLock)
        {
            _keyState.Clear();
        }

        PublishSnapshot(forceClear: true);
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
    /// 发布共享内存快照（含心跳、方案与按键状态）。
    /// </summary>
    private void PublishSnapshot(bool forceClear)
    {
        if (!_sharedMemory.IsReady)
        {
            return;
        }

        WindowSnapshot snapshot;
        KeyboardProfile profile;
        bool paused;

        lock (_stateLock)
        {
            snapshot = _snapshot;
            profile = _activeProfile;
            paused = IsPausedLocked();

            UpdateToggleState();

            if (paused)
            {
                Array.Clear(_keyboardState, 0, _keyboardState.Length);
                _keyState.CopyEdgeCounters(_edgeCounter);
                profile.BuildMask(_targetMask);
                profile.BuildBlockMask(_blockMask);
            }
            else
            {
                _keyState.ApplyProfile(profile, _toggleState, _keyboardState, _edgeCounter, _targetMask);
                profile.BuildBlockMask(_blockMask);
            }
        }

        var flags = paused ? SharedMemoryConstants.FlagPaused : 0u;
        if (forceClear)
        {
            flags |= SharedMemoryConstants.FlagClear;
        }

        var activePid = snapshot.ForegroundIsDnf ? snapshot.ForegroundProcessId : 0u;
        var tick = (ulong)Environment.TickCount64;

        // Replace 映射依赖 RawInput 事件路径；上报 Mapping 模式可触发注入端生成映射事件。
        var reportedMode = profile.Mode;
        if (reportedMode != KeyboardProfileMode.Mapping &&
            profile.MappingBehavior == KeyboardMappingBehavior.Replace)
        {
            reportedMode = KeyboardProfileMode.Mapping;
        }

        _sharedMemory.PublishSnapshot(
            flags,
            activePid,
            profile.ProfileId,
            (uint)reportedMode,
            tick,
            _keyboardState,
            _edgeCounter,
            _targetMask,
            _blockMask);
    }

    /// <summary>
    /// 读取当前系统键盘切换态（Caps/Num/Scroll 等）。
    /// </summary>
    private void UpdateToggleState()
    {
        if (!NativeMethods.GetKeyboardState(_toggleState))
        {
            Array.Clear(_toggleState, 0, _toggleState.Length);
            return;
        }

        for (var i = 0; i < _toggleState.Length; i++)
        {
            _toggleState[i] &= 0x01;
        }
    }

    private void HeartbeatTick(object? state)
    {
        try
        {
            PublishSnapshot(forceClear: false);
        }
        catch (Exception ex)
        {
            Log($"共享内存心跳异常：{ex.Message}");
        }
    }

    private void ReloadProfileIfNeeded(bool force)
    {
        var changed = _profileManager.ReloadIfChanged(out var message);
        if (!force && !changed)
        {
            return;
        }

        lock (_stateLock)
        {
            _activeProfile = _profileManager.ActiveProfile;
        }

        if (!string.IsNullOrWhiteSpace(message))
        {
            Log(message);
        }

        PublishSnapshot(forceClear: true);
    }

    private void TryInitializeSharedMemory()
    {
        try
        {
            _sharedMemory.Initialize();
            Log("共享内存已初始化");
        }
        catch (Exception ex)
        {
            Log($"共享内存初始化失败：{ex.Message}");
        }
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
        var signature = $"{master}|{snapshot.ForegroundIsDnf}|{snapshot.ForegroundProcessId}|{slaves}";

        if (signature == _lastSnapshotSignature)
        {
            return;
        }

        _lastSnapshotSignature = signature;
        LogVerbose($"窗口扫描：总数={snapshot.TotalCount} 主控={master} 从控={slaves} 前台DNF={snapshot.ForegroundIsDnf} PID={snapshot.ForegroundProcessId}");
    }

    /// <summary>
    /// 停止扫描并卸载钩子。
    /// </summary>
    public void Dispose()
    {
        _scanTimer.Stop();
        _keyboardHook.KeyEvent -= OnKeyEvent;
        _keyboardHook.Dispose();
        _heartbeatTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _heartbeatTimer.Dispose();
        _sharedMemory.Dispose();
    }
}

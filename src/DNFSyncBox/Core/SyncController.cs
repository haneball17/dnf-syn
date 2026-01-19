using System;
using System.Collections.Generic;
using System.Windows.Forms;
using System.Windows.Threading;

namespace DNFSyncBox;

public sealed class SyncController : IDisposable
{
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

    public event Action<SyncStatus>? StatusChanged;
    public event Action<string>? LogAdded;

    public SyncController()
    {
        _scanTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _scanTimer.Tick += (_, _) => RefreshWindows();
    }

    public void Start()
    {
        _keyboardHook.KeyEvent += OnKeyEvent;
        _keyboardHook.Install();
        RefreshWindows();
        _scanTimer.Start();
    }

    public void RefreshWindows()
    {
        var snapshot = _windowManager.Refresh();
        var autoPausedChanged = false;
        bool autoPaused;

        lock (_stateLock)
        {
            _snapshot = snapshot;
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
            ClearStuckKeys();
            Log("前台非 DNF，已自动暂停同步");
        }
        else if (autoPausedChanged && !autoPaused)
        {
            Log("前台已回到 DNF，自动暂停解除");
        }

        RaiseStatusChanged();
    }

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
            ClearStuckKeys();
            Log("已手动暂停同步");
        }
        else
        {
            Log("已恢复同步");
        }

        RaiseStatusChanged();
    }

    private void OnKeyEvent(Keys key, bool isDown)
    {
        if (IsAltKey(key))
        {
            lock (_stateLock)
            {
                _altDown = isDown;
            }
            return;
        }

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
                TogglePause();
            }
            return;
        }

        if (!IsDirectionKey(key))
        {
            return;
        }

        WindowSnapshot snapshot;
        bool paused;
        bool changed;

        lock (_stateLock)
        {
            changed = _keyState.SetState(key, isDown);
            paused = IsPausedLocked();
            snapshot = _snapshot;
        }

        if (!changed && isDown)
        {
            return; // 忽略重复按下
        }

        if (paused || !snapshot.ForegroundIsDnf)
        {
            return;
        }

        foreach (var handle in snapshot.SlaveHandles)
        {
            KeySender.PostKey(handle, key, isDown);
        }
    }

    private void ClearStuckKeys()
    {
        List<Keys> downKeys;
        WindowSnapshot snapshot;

        lock (_stateLock)
        {
            downKeys = new List<Keys>(_keyState.GetDownKeys());
            _keyState.Clear();
            snapshot = _snapshot;
        }

        if (downKeys.Count == 0)
        {
            return;
        }

        foreach (var key in downKeys)
        {
            foreach (var handle in snapshot.SlaveHandles)
            {
                KeySender.PostKey(handle, key, false);
            }
        }
    }

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

    private void Log(string message)
    {
        LogAdded?.Invoke($"{DateTime.Now:HH:mm:ss} {message}");
    }

    private bool IsPausedLocked() => _userPaused || _autoPaused;

    private static bool IsDirectionKey(Keys key)
    {
        return key is Keys.Left or Keys.Right or Keys.Up or Keys.Down;
    }

    private static bool IsAltKey(Keys key)
    {
        return key is Keys.Menu or Keys.LMenu or Keys.RMenu;
    }

    public void Dispose()
    {
        _scanTimer.Stop();
        _keyboardHook.KeyEvent -= OnKeyEvent;
        _keyboardHook.Dispose();
    }
}

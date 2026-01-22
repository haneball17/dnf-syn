using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace DNFSyncBox;

public sealed class WindowManager
{
    private readonly string _titleKeyword;
    private readonly string _processName;
    private readonly StringComparison _comparison = StringComparison.OrdinalIgnoreCase;

    /// <summary>
    /// 根据窗口标题关键字与进程名筛选 DNF 窗口。
    /// </summary>
    public WindowManager(string titleKeyword, string processName)
    {
        _titleKeyword = titleKeyword ?? string.Empty;
        _processName = NormalizeProcessName(processName);
    }

    /// <summary>
    /// 扫描当前系统窗口并返回主控/从控快照。
    /// </summary>
    public WindowSnapshot Refresh()
    {
        var windows = new List<WindowInfo>();

        NativeMethods.EnumWindows((hWnd, _) =>
        {
            var title = GetWindowTitle(hWnd);
            var className = GetWindowClass(hWnd);
            var isVisible = NativeMethods.IsWindowVisible(hWnd);
            var processMatch = TryGetProcessInfo(hWnd, out var processName, out var pid)
                && string.Equals(processName, _processName, _comparison);
            var titleMatch = !string.IsNullOrWhiteSpace(_titleKeyword)
                && title.Contains(_titleKeyword, _comparison);

            // 标题命中或进程名命中即视为 DNF 相关窗口（进程命中时允许不可见窗口）
            if (titleMatch || processMatch)
            {
                windows.Add(new WindowInfo(hWnd, pid, title, className, isVisible, titleMatch, processMatch));
            }

            return true;
        }, IntPtr.Zero);

        // 前台窗口为 DNF 进程则视为主控
        var foreground = NativeMethods.GetForegroundWindow();
        var foregroundIsDnf = TryGetProcessInfo(foreground, out var foregroundProcess, out var foregroundPid)
            && string.Equals(foregroundProcess, _processName, _comparison);

        var master = foregroundIsDnf
            ? ChooseBestHandle(windows, foregroundPid)
            : IntPtr.Zero;
        var activePid = foregroundIsDnf ? foregroundPid : 0;

        var slaveHandles = new List<IntPtr>();
        var seenPids = new HashSet<uint>();
        foreach (var info in windows)
        {
            if (info.ProcessId == 0 || info.ProcessId == foregroundPid)
            {
                continue;
            }

            if (!seenPids.Add(info.ProcessId))
            {
                continue;
            }

            var target = ChooseBestHandle(windows, info.ProcessId);
            if (target != IntPtr.Zero && target != master)
            {
                slaveHandles.Add(target);
            }
        }

        var processCount = seenPids.Count + (foregroundIsDnf ? 1 : 0);
        return new WindowSnapshot(master, slaveHandles, foregroundIsDnf, processCount, activePid);
    }

    private IntPtr ChooseBestHandle(List<WindowInfo> windows, uint pid)
    {
        if (pid == 0)
        {
            return IntPtr.Zero;
        }

        WindowInfo? best = null;
        var bestScore = int.MinValue;
        foreach (var window in windows)
        {
            if (window.ProcessId != pid)
            {
                continue;
            }

            var score = 0;
            if (window.IsVisible)
            {
                score += 10;
            }
            if (window.TitleMatch)
            {
                score += 20;
            }
            if (!IsChatWindow(window.ClassName))
            {
                score += 5;
            }
            if (window.ClassName.Contains("DNF", _comparison))
            {
                score += 2;
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = window;
            }
        }

        return best?.Handle ?? IntPtr.Zero;
    }

    private static bool TryGetProcessInfo(IntPtr hWnd, out string processName, out uint pid)
    {
        processName = string.Empty;
        pid = 0;

        NativeMethods.GetWindowThreadProcessId(hWnd, out pid);
        if (pid == 0)
        {
            return false;
        }

        try
        {
            var process = Process.GetProcessById((int)pid);
            processName = NormalizeProcessName(process.ProcessName);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 读取窗口标题，用于关键字匹配。
    /// </summary>
    private static string GetWindowTitle(IntPtr hWnd)
    {
        var buffer = new StringBuilder(256);
        _ = NativeMethods.GetWindowText(hWnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private static string GetWindowClass(IntPtr hWnd)
    {
        var buffer = new StringBuilder(256);
        _ = NativeMethods.GetClassName(hWnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private static bool IsChatWindow(string className)
    {
        return className.Contains("CHAT", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 统一进程名格式：去掉 .exe，便于对比。
    /// </summary>
    private static string NormalizeProcessName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var normalized = name.Trim();
        if (normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^4];
        }

        return normalized;
    }

    private sealed class WindowInfo
    {
        public WindowInfo(IntPtr handle, uint processId, string title, string className, bool isVisible, bool titleMatch, bool processMatch)
        {
            Handle = handle;
            ProcessId = processId;
            Title = title;
            ClassName = className;
            IsVisible = isVisible;
            TitleMatch = titleMatch;
            ProcessMatch = processMatch;
        }

        public IntPtr Handle { get; }
        public uint ProcessId { get; }
        public string Title { get; }
        public string ClassName { get; }
        public bool IsVisible { get; }
        public bool TitleMatch { get; }
        public bool ProcessMatch { get; }
    }
}

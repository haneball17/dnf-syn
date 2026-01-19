using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace DNFSyncBox;

public sealed class WindowManager
{
    private readonly string _titleKeyword;
    private readonly string _processName;

    public WindowManager(string titleKeyword, string processName)
    {
        _titleKeyword = titleKeyword ?? string.Empty;
        _processName = NormalizeProcessName(processName);
    }

    public WindowSnapshot Refresh()
    {
        var handles = new List<IntPtr>();

        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hWnd))
            {
                return true;
            }

            var title = GetWindowTitle(hWnd);
            var titleMatch = !string.IsNullOrWhiteSpace(_titleKeyword)
                && title.Contains(_titleKeyword, StringComparison.OrdinalIgnoreCase);
            var processMatch = IsProcessMatch(hWnd);

            if (titleMatch || processMatch)
            {
                handles.Add(hWnd);
            }

            return true;
        }, IntPtr.Zero);

        var foreground = NativeMethods.GetForegroundWindow();
        var foregroundIsDnf = handles.Contains(foreground);
        var master = foregroundIsDnf ? foreground : IntPtr.Zero;

        var slaves = new List<IntPtr>();
        foreach (var handle in handles)
        {
            if (handle != master)
            {
                slaves.Add(handle);
            }
        }

        return new WindowSnapshot(master, slaves, foregroundIsDnf, handles.Count);
    }

    private bool IsProcessMatch(IntPtr hWnd)
    {
        if (string.IsNullOrWhiteSpace(_processName))
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(hWnd, out var pid);
        if (pid == 0)
        {
            return false;
        }

        try
        {
            var process = Process.GetProcessById((int)pid);
            var name = NormalizeProcessName(process.ProcessName);
            return string.Equals(name, _processName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string GetWindowTitle(IntPtr hWnd)
    {
        var buffer = new StringBuilder(256);
        _ = NativeMethods.GetWindowText(hWnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

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
}

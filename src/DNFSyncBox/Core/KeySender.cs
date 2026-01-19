using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace DNFSyncBox;

public static class KeySender
{
    private const bool UseThreadMessageFallback = true;

    public enum SendChannel
    {
        WindowMessage,
        ThreadMessage
    }

    /// <summary>
    /// 单次投递结果，用于调试日志。
    /// </summary>
    public readonly struct SendAttempt
    {
        public SendAttempt(IntPtr target, bool success, int errorCode, bool isChild, string className, string title, SendChannel channel, uint threadId)
        {
            Target = target;
            Success = success;
            ErrorCode = errorCode;
            IsChild = isChild;
            ClassName = className;
            Title = title;
            Channel = channel;
            ThreadId = threadId;
        }

        public IntPtr Target { get; }
        public bool Success { get; }
        public int ErrorCode { get; }
        public bool IsChild { get; }
        public string ClassName { get; }
        public string Title { get; }
        public SendChannel Channel { get; }
        public uint ThreadId { get; }
    }

    /// <summary>
    /// 向指定窗口投递键盘消息（含扫描码与扩展键标志）。
    /// </summary>
    public static IReadOnlyList<SendAttempt> PostKey(IntPtr hWnd, Keys key, bool isDown)
    {
        if (hWnd == IntPtr.Zero)
        {
            return Array.Empty<SendAttempt>();
        }

        var children = GetChildWindows(hWnd, maxChildren: 32);
        var attempts = new List<SendAttempt>(children.Count * 2 + 2);

        if (children.Count > 0)
        {
            // 有子窗口时优先投递到子窗口，避免父子同时响应造成重复。
            foreach (var child in children)
            {
                attempts.AddRange(SendKeyMessage(child, key, isDown, isChild: true));
            }
        }
        else
        {
            attempts.AddRange(SendKeyMessage(hWnd, key, isDown, isChild: false));
        }

        return attempts;
    }

    private static List<SendAttempt> SendKeyMessage(IntPtr hWnd, Keys key, bool isDown, bool isChild)
    {
        var attempts = new List<SendAttempt>(2);
        var vkCode = (int)key;
        var scanCode = NativeMethods.MapVirtualKey((uint)vkCode, 0);

        // lParam 按 DNF 期望构建：重复计数 + 扫描码 + 扩展键 + Up/Down 标志位
        uint lParam = 1; // 重复计数
        lParam |= scanCode << 16; // 扫描码

        if (IsExtendedKey(key))
        {
            lParam |= 1u << 24;
        }

        if (!isDown)
        {
            lParam |= 1u << 30; // 上一次按键状态
            lParam |= 1u << 31; // 释放
        }

        var msg = isDown ? NativeMethods.WM_KEYDOWN : NativeMethods.WM_KEYUP;
        var sendResult = NativeMethods.SendMessageTimeout(
            hWnd,
            msg,
            (IntPtr)vkCode,
            (UIntPtr)lParam,
            NativeMethods.SMTO_ABORTIFHUNG | NativeMethods.SMTO_BLOCK,
            50,
            out _);

        var success = sendResult != IntPtr.Zero;
        var error = success ? 0 : Marshal.GetLastWin32Error();
        var className = GetWindowClass(hWnd);
        var title = GetWindowTitle(hWnd);
        attempts.Add(new SendAttempt(hWnd, success, error, isChild, className, title, SendChannel.WindowMessage, 0));

        if (UseThreadMessageFallback)
        {
            var threadId = NativeMethods.GetWindowThreadProcessId(hWnd, out _);
            if (threadId != 0)
            {
                var threadOk = NativeMethods.PostThreadMessage(threadId, msg, (IntPtr)vkCode, (UIntPtr)lParam);
                var threadError = threadOk ? 0 : Marshal.GetLastWin32Error();
                attempts.Add(new SendAttempt(hWnd, threadOk, threadError, isChild, className, title, SendChannel.ThreadMessage, threadId));
            }
        }

        return attempts;
    }

    private static List<IntPtr> GetChildWindows(IntPtr hWnd, int maxChildren)
    {
        var results = new List<IntPtr>();
        NativeMethods.EnumChildWindows(hWnd, (child, _) =>
        {
            if (results.Count >= maxChildren)
            {
                return false;
            }

            results.Add(child);
            return true;
        }, IntPtr.Zero);

        return results;
    }

    private static string GetWindowClass(IntPtr hWnd)
    {
        var buffer = new StringBuilder(128);
        _ = NativeMethods.GetClassName(hWnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private static string GetWindowTitle(IntPtr hWnd)
    {
        var buffer = new StringBuilder(256);
        _ = NativeMethods.GetWindowText(hWnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    /// <summary>
    /// 判定是否为扩展键，需要设置 lParam 第 24 位。
    /// </summary>
    private static bool IsExtendedKey(Keys key)
    {
        return key is Keys.Left or Keys.Right or Keys.Up or Keys.Down
            or Keys.Insert or Keys.Delete or Keys.Home or Keys.End
            or Keys.PageUp or Keys.PageDown;
    }
}

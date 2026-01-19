using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DNFSyncBox;

public sealed class KeyboardHook : IDisposable
{
    private IntPtr _hookId = IntPtr.Zero;
    private NativeMethods.LowLevelKeyboardProc? _proc;

    /// <summary>
    /// 键盘事件回调：参数为按键与是否按下。
    /// </summary>
    public event Action<Keys, bool>? KeyEvent;

    /// <summary>
    /// 安装全局低级键盘钩子。
    /// </summary>
    public void Install()
    {
        if (_hookId != IntPtr.Zero)
        {
            return;
        }

        _proc = HookCallback;
        using var currentProcess = Process.GetCurrentProcess();
        using var currentModule = currentProcess.MainModule;
        var moduleHandle = NativeMethods.GetModuleHandle(currentModule?.ModuleName);
        _hookId = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _proc, moduleHandle, 0);

        if (_hookId == IntPtr.Zero)
        {
            throw new InvalidOperationException("安装键盘钩子失败。");
        }
    }

    /// <summary>
    /// 卸载钩子，释放系统资源。
    /// </summary>
    public void Uninstall()
    {
        if (_hookId == IntPtr.Zero)
        {
            return;
        }

        _ = NativeMethods.UnhookWindowsHookEx(_hookId);
        _hookId = IntPtr.Zero;
    }

    /// <summary>
    /// 钩子回调：仅处理按下/抬起事件，其余消息交给下一个钩子。
    /// </summary>
    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var message = unchecked((uint)wParam.ToInt64());
            var isDown = message == NativeMethods.WM_KEYDOWN || message == NativeMethods.WM_SYSKEYDOWN;
            var isUp = message == NativeMethods.WM_KEYUP || message == NativeMethods.WM_SYSKEYUP;

            if (isDown || isUp)
            {
                var data = Marshal.PtrToStructure<NativeMethods.KbdLlHookStruct>(lParam);
                var key = (Keys)data.VkCode;
                KeyEvent?.Invoke(key, isDown);
            }
        }

        return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    /// <summary>
    /// 释放钩子并清理引用。
    /// </summary>
    public void Dispose()
    {
        Uninstall();
        _proc = null;
    }
}

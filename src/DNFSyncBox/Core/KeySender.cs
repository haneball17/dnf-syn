using System;
using System.Windows.Forms;

namespace DNFSyncBox;

public static class KeySender
{
    public static void PostKey(IntPtr hWnd, Keys key, bool isDown)
    {
        if (hWnd == IntPtr.Zero)
        {
            return;
        }

        var vkCode = (int)key;
        var scanCode = NativeMethods.MapVirtualKey((uint)vkCode, 0);

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
        _ = NativeMethods.PostMessage(hWnd, msg, (IntPtr)vkCode, (UIntPtr)lParam);
    }

    private static bool IsExtendedKey(Keys key)
    {
        return key is Keys.Left or Keys.Right or Keys.Up or Keys.Down
            or Keys.Insert or Keys.Delete or Keys.Home or Keys.End
            or Keys.PageUp or Keys.PageDown;
    }
}

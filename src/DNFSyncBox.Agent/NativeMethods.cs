using System;
using System.Runtime.InteropServices;

namespace DNFSyncBox.Agent
{
    internal static class NativeMethods
    {
        /// <summary>
        /// 获取已加载模块句柄（用于查找导出函数地址）。
        /// </summary>
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern IntPtr GetModuleHandle(string lpModuleName);

        /// <summary>
        /// 强制加载模块，确保 dinput8.dll 可用。
        /// </summary>
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern IntPtr LoadLibrary(string lpFileName);

        /// <summary>
        /// 获取导出函数地址。
        /// </summary>
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        public static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);
    }
}

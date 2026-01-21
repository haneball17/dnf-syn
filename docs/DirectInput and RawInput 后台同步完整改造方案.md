### 🚀 DirectInput/RawInput 后台同步完整改造方案

本方案将从“外部投递消息”架构转变为“内部状态欺骗”架构。

#### 1. 架构设计 (Architecture)

我们需要新增一个 C++ DLL 项目（作为注入的 Payload），并修改现有的 C# 项目。

- **Host (C# DNFSyncBox)**: 负责监听全局按键，将按键状态写入 **共享内存 (Shared Memory)**。
- **Payload (C++ DLL)**: 注入到 DNF 游戏进程中。它 Hook 住 `DirectInput8Create` 或 `GetDeviceState`。
- **Communication**: 通过“内存映射文件 (Memory Mapped File)”进行即时通讯。当 Host 写入“按下 A 键”时，DLL 里的 Hook 函数读取该状态，并告诉游戏“A 键被按下了”，无论窗口是否在前台。

---

#### 2. 实施步骤与关键代码

此方案包含三个部分：**C++ Hook DLL**、**C# 注入器**、**C# 状态同步器**。

### 第一步：新建 C++ DLL 项目 (`DNFSyncHook`)

你需要创建一个 C++ 动态链接库项目，用于注入游戏。

**文件：`dllmain.cpp**` (核心逻辑)

```C++
#include <windows.h>
#include <dinput.h>
#include <map>

// 定义共享内存名称，需与 C# 端一致
#define SHARED_MEM_NAME L"Local\\DNFSyncBox_SharedMem"

// 共享内存结构：简单的 256 字节数组表示键盘状态 (0=Up, 0x80=Down)
struct SharedData {
    BYTE keyState[256];
};

SharedData* g_pSharedData = nullptr;
HANDLE g_hMapFile = nullptr;

// 函数指针定义
typedef HRESULT(STDMETHODCALLTYPE* GetDeviceState_t)(IDirectInputDevice8*, DWORD, LPVOID);
GetDeviceState_t Original_GetDeviceState = nullptr;

// 我们的伪造函数
HRESULT STDMETHODCALLTYPE Hooked_GetDeviceState(IDirectInputDevice8* pDevice, DWORD cbData, LPVOID lpvData) {
    // 1. 调用原始函数，获取真实的硬件状态（或者空状态）
    HRESULT hr = Original_GetDeviceState(pDevice, cbData, lpvData);
    
    // 2. 如果成功且是键盘数据，则覆盖状态
    if (SUCCEEDED(hr) && cbData == 256 && g_pSharedData != nullptr) {
        BYTE* keyboardState = (BYTE*)lpvData;
        
        // 遍历共享内存，将 C# 发来的按键状态“合并”到游戏读取的结果中
        for (int i = 0; i < 256; i++) {
            if (g_pSharedData->keyState[i] & 0x80) {
                keyboardState[i] = 0x80; // 强制置为按下
            }
        }
    }
    return hr;
}

// 简单的 VTable Hook 逻辑 (简化版，实际建议使用 MinHook 库)
void SetupHook(IDirectInputDevice8* pDevice) {
    // 获取虚表地址
    void** vTable = *(void***)pDevice;
    DWORD oldProtect;
    
    // GetDeviceState 通常是虚表中的第 9 个函数 (索引 9)
    // 注意：这里需要根据具体的 DirectInput 版本确认索引，DI8 一般是 9
    if (Original_GetDeviceState == nullptr) {
        VirtualProtect(&vTable[9], sizeof(void*), PAGE_EXECUTE_READWRITE, &oldProtect);
        Original_GetDeviceState = (GetDeviceState_t)vTable[9];
        vTable[9] = (void*)Hooked_GetDeviceState;
        VirtualProtect(&vTable[9], sizeof(void*), oldProtect, &oldProtect);
    }
}

// 初始化共享内存
void InitSharedMemory() {
    g_hMapFile = OpenFileMapping(FILE_MAP_READ, FALSE, SHARED_MEM_NAME);
    if (g_hMapFile) {
        g_pSharedData = (SharedData*)MapViewOfFile(g_hMapFile, FILE_MAP_READ, 0, 0, sizeof(SharedData));
    }
}

// 实际入口需要 Hook DirectInput8Create 来捕获 Device 对象
// 为简化演示，这里假设在 DllMain 中通过扫描或其他方式获取 Device，
// 实际工程中推荐使用 MinHook 库 Hook DirectInput8Create。

```

> **补充说明**：以上 C++ 代码是逻辑核心。在实际生产中，为了稳定性，请务必使用 **MinHook** 库来执行 Hook，而不是手动修改虚表。你需要 Hook `DirectInput8Create`，在回调中拿到 `IDirectInput8` 接口，再 Hook `CreateDevice`，最后 Hook `GetDeviceState`。

---

### 第二步：C# 端实现注入器 (`Injector.cs`)

在 `src/DNFSyncBox/Core/` 下新增 `Injector.cs`。

```C#
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace DNFSyncBox.Core;

public static class Injector
{
    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenProcess(int dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("kernel32", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, uint nSize, out UIntPtr lpNumberOfBytesWritten);

    [DllImport("kernel32.dll")]
    private static extern IntPtr CreateRemoteThread(IntPtr hProcess, IntPtr lpThreadAttributes, uint dwStackSize, IntPtr lpStartAddress, IntPtr lpParameter, uint dwCreationFlags, IntPtr lpThreadId);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    // 权限常量
    private const int PROCESS_CREATE_THREAD = 0x0002;
    private const int PROCESS_QUERY_INFORMATION = 0x0400;
    private const int PROCESS_VM_OPERATION = 0x0008;
    private const int PROCESS_VM_WRITE = 0x0020;
    private const int PROCESS_VM_READ = 0x0010;

    private const uint MEM_COMMIT = 0x00001000;
    private const uint MEM_RESERVE = 0x00002000;
    private const uint PAGE_READWRITE = 0x04;

    public static bool Inject(int processId, string dllPath)
    {
        if (!File.Exists(dllPath)) return false;

        IntPtr hProcess = OpenProcess(PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION | PROCESS_VM_OPERATION | PROCESS_VM_WRITE | PROCESS_VM_READ, false, processId);
        if (hProcess == IntPtr.Zero) return false;

        try
        {
            // 1. 在目标进程申请内存存放 DLL 路径
            IntPtr allocMemAddress = VirtualAllocEx(hProcess, IntPtr.Zero, (uint)((dllPath.Length + 1) * Marshal.SizeOf(typeof(char))), MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            
            // 2. 写入 DLL 路径
            byte[] bytes = Encoding.Default.GetBytes(dllPath + "\0"); // 注意编码，建议匹配目标进程（通常 ANSI）
            WriteProcessMemory(hProcess, allocMemAddress, bytes, (uint)bytes.Length, out _);

            // 3. 获取 LoadLibraryA 地址
            IntPtr loadLibraryAddr = GetProcAddress(GetModuleHandle("kernel32.dll"), "LoadLibraryA");

            // 4. 创建远程线程执行 LoadLibrary
            IntPtr hThread = CreateRemoteThread(hProcess, IntPtr.Zero, 0, loadLibraryAddr, allocMemAddress, 0, IntPtr.Zero);
            
            return hThread != IntPtr.Zero;
        }
        finally
        {
            CloseHandle(hProcess);
        }
    }
}

```

---

### 第三步：C# 端共享内存通讯 (`SyncController.cs` 改造)

你需要修改 `SyncController.cs`，不再调用 `KeySender.PostKey` 发送窗口消息，而是将按键状态写入共享内存。

**1. 新增共享内存管理类 `SharedMemoryManager.cs**`

```C#
using System;
using System.IO.MemoryMappedFiles;

namespace DNFSyncBox.Core;

public class SharedMemoryManager : IDisposable
{
    private MemoryMappedFile _mmf;
    private MemoryMappedViewAccessor _accessor;
    private const string MapName = "Local\\DNFSyncBox_SharedMem";
    private const int MapSize = 256; // 对应 256 个虚拟键码

    public SharedMemoryManager()
    {
        // 创建共享内存，大小 256 字节
        _mmf = MemoryMappedFile.CreateOrOpen(MapName, MapSize);
        _accessor = _mmf.CreateViewAccessor();
    }

    public void SetKeyState(System.Windows.Forms.Keys key, bool isDown)
    {
        int offset = (int)key;
        if (offset >= 0 && offset < 256)
        {
            // 0x80 表示按下，0x00 表示抬起
            _accessor.Write(offset, isDown ? (byte)0x80 : (byte)0x00);
        }
    }
    
    // 暂停时清空所有
    public void ClearAll()
    {
        byte[] empty = new byte[MapSize];
        _accessor.WriteArray(0, empty, 0, MapSize);
    }

    public void Dispose()
    {
        _accessor?.Dispose();
        _mmf?.Dispose();
    }
}

```

**2. 修改 `SyncController.cs**`

```C#
// 在类成员中添加
private readonly SharedMemoryManager _sharedMemory = new();
private readonly HashSet<int> _injectedPids = new();

// 修改 RefreshWindows 方法，对新发现的从控窗口进行注入
public void RefreshWindows()
{
    var snapshot = _windowManager.Refresh();
    // ... 原有逻辑 ...
    
    foreach (var handle in snapshot.SlaveHandles)
    {
        NativeMethods.GetWindowThreadProcessId(handle, out uint pid);
        if (!_injectedPids.Contains((int)pid))
        {
            // 假设 DLL 放在程序运行目录下
            string dllPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DNFSyncHook.dll");
            if (Injector.Inject((int)pid, dllPath))
            {
                Log($"已向进程 {pid} 注入 Hook DLL");
                _injectedPids.Add((int)pid);
            }
            else
            {
                Log($"向进程 {pid} 注入失败");
            }
        }
    }
    // ...
}

// 修改 OnKeyEvent 方法
private void OnKeyEvent(Keys key, bool isDown)
{
    // ... 原有热键判断逻辑 ...

    if (paused || !snapshot.ForegroundIsDnf)
    {
        // 暂停时也要记得清理共享内存状态，防止后台一直卡着按键
        _sharedMemory.SetKeyState(key, false); 
        return;
    }

    // ★★★ 核心修改：不再 PostMessage，而是更新共享内存 ★★★
    // 这种方式下，所有注入了 DLL 的后台窗口都会立刻读到这个状态
    _sharedMemory.SetKeyState(key, isDown);
    
    LogVerbose($"更新共享内存：{key} {(isDown ? "Down" : "Up")}");
}

// 修改 ClearStuckKeys
private void ClearStuckKeys()
{
    // ...
    _sharedMemory.ClearAll(); // 一键清空所有按键状态
    LogVerbose("已清空共享内存按键状态");
}

```

---

#### 3. 风险与注意事项 (经验总结)

1. **32位 vs 64位**：DNF 2012 几乎肯定是 **32位进程**。因此，你的 C++ DLL 项目 **必须编译为 x86 (Win32)**，且你的 C# 程序如果是 AnyCPU，最好也强制为 x86 或者确保注入逻辑能处理跨架构注入（通常建议 C# 工具也设为 x86 以免麻烦）。
2. **Hook 稳定性**：DirectInput 的 Hook 需要精确。如果 Hook 了错误的地址，游戏会崩溃。建议先使用 `API Monitor` 等工具观察 DNF 2012 具体调用了哪个 API（是 `GetDeviceState` 还是 `GetDeviceData`，或者是 Win32 的 `GetKeyboardState`）。
3. **调试**：由于 DLL 在目标进程运行，调试比较困难。建议在 DLL 中使用 `OutputDebugString` 输出日志，并使用 `DebugView` 工具在外部查看。


using EasyHook;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading;

namespace DNFSyncBox.Agent
{
/// <summary>
/// 进程内代理入口：安装钩子并监听 IPC 指令。
/// </summary>
public sealed class AgentEntryPoint : IEntryPoint
{
    // COM vtable 索引：IDirectInputDevice8 的 GetDeviceState/GetDeviceData 固定位置。
    private const int GetDeviceStateVtableIndex = 9;
    private const int GetDeviceDataVtableIndex = 10;

    private static readonly Dictionary<DirectionKey, int> VirtualKeyMap = new Dictionary<DirectionKey, int>
    {
        [DirectionKey.Up] = 0x26,
        [DirectionKey.Down] = 0x28,
        [DirectionKey.Left] = 0x25,
        [DirectionKey.Right] = 0x27
    };

    private static readonly Dictionary<DirectionKey, int> DirectInputKeyMap = new Dictionary<DirectionKey, int>
    {
        [DirectionKey.Up] = 0xC8,
        [DirectionKey.Down] = 0xD0,
        [DirectionKey.Left] = 0xCB,
        [DirectionKey.Right] = 0xCD
    };

    private static readonly object DeviceHookLock = new object();
    private static IntPtr _getDeviceStatePtr = IntPtr.Zero;
    private static IntPtr _getDeviceDataPtr = IntPtr.Zero;

    private static GetAsyncKeyStateDelegate? _getAsyncKeyStateOriginal;
    private static DirectInput8CreateDelegate? _directInput8CreateOriginal;
    private static CreateDeviceDelegate? _createDeviceOriginal;
    private static GetDeviceStateDelegate? _getDeviceStateOriginal;
    private static GetDeviceDataDelegate? _getDeviceDataOriginal;

    private readonly AgentState _state = new AgentState();
    private readonly AgentLogger _logger;
    private readonly List<LocalHook> _hooks = new List<LocalHook>();
    private readonly string _pipeName;

    public AgentEntryPoint(RemoteHooking.IContext context, string pipeName)
    {
        var pid = Process.GetCurrentProcess().Id;
        _pipeName = string.IsNullOrWhiteSpace(pipeName)
            ? $"DNFSyncBox.Agent.{pid}"
            : pipeName;
        _logger = new AgentLogger(pid);
    }

    /// <summary>
    /// 代理运行入口：安装钩子、启动管道、循环驻留。
    /// </summary>
    public void Run(RemoteHooking.IContext context, string pipeName)
    {
        _logger.Info($"代理启动，PID={Process.GetCurrentProcess().Id}，Pipe={_pipeName}");

        try
        {
            InstallHooks();
        }
        catch (Exception ex)
        {
            _logger.Error("安装钩子失败，代理将保持最小运行", ex);
        }

        var ipcThread = new Thread(() => RunPipeServer(_pipeName))
        {
            IsBackground = true
        };
        ipcThread.Start();

        while (!_state.StopRequested)
        {
            Thread.Sleep(200);
        }

        DisposeHooks();
        _logger.Info("代理退出");
    }

    /// <summary>
    /// 安装全部输入钩子（验证阶段的核心逻辑）。
    /// </summary>
    private void InstallHooks()
    {
        InstallGetAsyncKeyStateHook();
        InstallDirectInputHooks();
    }

    /// <summary>
    /// 钩住 GetAsyncKeyState，用于统计与可选方向键伪造。
    /// </summary>
    private void InstallGetAsyncKeyStateHook()
    {
        var module = NativeMethods.GetModuleHandle("user32.dll");
        if (module == IntPtr.Zero)
        {
            module = NativeMethods.LoadLibrary("user32.dll");
        }

        if (module == IntPtr.Zero)
        {
            _logger.Error("无法加载 user32.dll");
            return;
        }

        var proc = NativeMethods.GetProcAddress(module, "GetAsyncKeyState");
        if (proc == IntPtr.Zero)
        {
            _logger.Error("GetAsyncKeyState 地址获取失败");
            return;
        }

        _getAsyncKeyStateOriginal = Marshal.GetDelegateForFunctionPointer<GetAsyncKeyStateDelegate>(proc);
        var hook = LocalHook.Create(proc, new GetAsyncKeyStateDelegate(GetAsyncKeyStateHook), this);
        hook.ThreadACL.SetExclusiveACL(new[] { 0 });
        _hooks.Add(hook);

        _logger.Info("GetAsyncKeyState 钩子已安装");
    }

    /// <summary>
    /// 钩住 DirectInput8Create，并在后续挂接设备级钩子。
    /// </summary>
    private void InstallDirectInputHooks()
    {
        var module = NativeMethods.GetModuleHandle("dinput8.dll");
        if (module == IntPtr.Zero)
        {
            module = NativeMethods.LoadLibrary("dinput8.dll");
        }

        if (module == IntPtr.Zero)
        {
            _logger.Error("无法加载 dinput8.dll");
            return;
        }

        var proc = NativeMethods.GetProcAddress(module, "DirectInput8Create");
        if (proc == IntPtr.Zero)
        {
            _logger.Error("DirectInput8Create 地址获取失败");
            return;
        }

        _directInput8CreateOriginal = Marshal.GetDelegateForFunctionPointer<DirectInput8CreateDelegate>(proc);
        var hook = LocalHook.Create(proc, new DirectInput8CreateDelegate(DirectInput8CreateHook), this);
        hook.ThreadACL.SetExclusiveACL(new[] { 0 });
        _hooks.Add(hook);

        _logger.Info("DirectInput8Create 钩子已安装");
    }

    private void DisposeHooks()
    {
        foreach (var hook in _hooks)
        {
            hook.Dispose();
        }

        _hooks.Clear();
    }

    /// <summary>
    /// GetAsyncKeyState 的拦截实现：统计调用并叠加方向键状态。
    /// </summary>
    private short GetAsyncKeyStateHook(int vKey)
    {
        _state.IncrementGetAsyncKeyState();

        if (_getAsyncKeyStateOriginal == null)
        {
            return 0;
        }

        var original = _getAsyncKeyStateOriginal(vKey);
        if (!_state.SpoofEnabled || !TryMapDirectionKey(vKey, out var direction))
        {
            return original;
        }

        return _state.IsDirectionKeyDown(direction)
            ? (short)(original | unchecked((short)0x8001))
            : original;
    }

    /// <summary>
    /// DirectInput8Create 的拦截实现：统计调用并尝试挂接 CreateDevice。
    /// </summary>
    private int DirectInput8CreateHook(IntPtr hInstance, uint dwVersion, ref Guid riidltf, out IntPtr ppvOut, IntPtr punkOuter)
    {
        _state.IncrementDirectInputCreate();

        if (_directInput8CreateOriginal == null)
        {
            ppvOut = IntPtr.Zero;
            return unchecked((int)0x80004005);
        }

        var result = _directInput8CreateOriginal(hInstance, dwVersion, ref riidltf, out ppvOut, punkOuter);

        if (result >= 0 && ppvOut != IntPtr.Zero)
        {
            TryHookCreateDevice(ppvOut);
        }

        return result;
    }

    /// <summary>
    /// CreateDevice 的拦截实现：获取设备对象并挂接 GetDeviceState/GetDeviceData。
    /// </summary>
    private int CreateDeviceHook(IntPtr self, ref Guid rguid, out IntPtr device, IntPtr pUnkOuter)
    {
        _state.IncrementCreateDevice();

        if (_createDeviceOriginal == null)
        {
            device = IntPtr.Zero;
            return unchecked((int)0x80004005);
        }

        var result = _createDeviceOriginal(self, ref rguid, out device, pUnkOuter);

        if (result >= 0 && device != IntPtr.Zero)
        {
            TryHookDevice(device);
        }

        return result;
    }

    /// <summary>
    /// GetDeviceState 的拦截实现：统计调用并叠加方向键状态。
    /// </summary>
    private int GetDeviceStateHook(IntPtr self, int cbData, IntPtr lpvData)
    {
        _state.IncrementGetDeviceState();

        if (_getDeviceStateOriginal == null)
        {
            return unchecked((int)0x80004005);
        }

        var result = _getDeviceStateOriginal(self, cbData, lpvData);

        if (result >= 0 && _state.SpoofEnabled && cbData >= 256 && lpvData != IntPtr.Zero)
        {
            ApplyDirectInputSpoof(lpvData);
        }

        return result;
    }

    /// <summary>
    /// GetDeviceData 的拦截实现：当前仅做统计，便于判断调用路径。
    /// </summary>
    private int GetDeviceDataHook(IntPtr self, int cbObjectData, IntPtr rgdod, ref int pdwInOut, int dwFlags)
    {
        _state.IncrementGetDeviceData();

        if (_getDeviceDataOriginal == null)
        {
            return unchecked((int)0x80004005);
        }

        return _getDeviceDataOriginal(self, cbObjectData, rgdod, ref pdwInOut, dwFlags);
    }

    /// <summary>
    /// 尝试挂接 IDirectInput8::CreateDevice。
    /// </summary>
    private void TryHookCreateDevice(IntPtr directInputPtr)
    {
        try
        {
            var vtable = Marshal.ReadIntPtr(directInputPtr);
            // QI/AddRef/Release 之后的第 3 项为 CreateDevice。
            var createDevicePtr = Marshal.ReadIntPtr(vtable, IntPtr.Size * 3);

            if (createDevicePtr == IntPtr.Zero)
            {
                return;
            }

            lock (DeviceHookLock)
            {
                if (_createDeviceOriginal != null)
                {
                    return;
                }

                _createDeviceOriginal = Marshal.GetDelegateForFunctionPointer<CreateDeviceDelegate>(createDevicePtr);
                var hook = LocalHook.Create(createDevicePtr, new CreateDeviceDelegate(CreateDeviceHook), this);
                hook.ThreadACL.SetExclusiveACL(new[] { 0 });
                _hooks.Add(hook);
            }

            _logger.Info("IDirectInput8::CreateDevice 钩子已安装");
        }
        catch (Exception ex)
        {
            _logger.Error("CreateDevice 钩子安装失败", ex);
        }
    }

    /// <summary>
    /// 尝试挂接设备级的 GetDeviceState/GetDeviceData。
    /// </summary>
    private void TryHookDevice(IntPtr devicePtr)
    {
        try
        {
            var vtable = Marshal.ReadIntPtr(devicePtr);
            var getDeviceStatePtr = Marshal.ReadIntPtr(vtable, IntPtr.Size * GetDeviceStateVtableIndex);
            var getDeviceDataPtr = Marshal.ReadIntPtr(vtable, IntPtr.Size * GetDeviceDataVtableIndex);

            lock (DeviceHookLock)
            {
                if (_getDeviceStatePtr == IntPtr.Zero && getDeviceStatePtr != IntPtr.Zero)
                {
                    _getDeviceStatePtr = getDeviceStatePtr;
                    _getDeviceStateOriginal = Marshal.GetDelegateForFunctionPointer<GetDeviceStateDelegate>(getDeviceStatePtr);
                    var hook = LocalHook.Create(getDeviceStatePtr, new GetDeviceStateDelegate(GetDeviceStateHook), this);
                    hook.ThreadACL.SetExclusiveACL(new[] { 0 });
                    _hooks.Add(hook);
                    _logger.Info("IDirectInputDevice8::GetDeviceState 钩子已安装");
                }

                if (_getDeviceDataPtr == IntPtr.Zero && getDeviceDataPtr != IntPtr.Zero)
                {
                    _getDeviceDataPtr = getDeviceDataPtr;
                    _getDeviceDataOriginal = Marshal.GetDelegateForFunctionPointer<GetDeviceDataDelegate>(getDeviceDataPtr);
                    var hook = LocalHook.Create(getDeviceDataPtr, new GetDeviceDataDelegate(GetDeviceDataHook), this);
                    hook.ThreadACL.SetExclusiveACL(new[] { 0 });
                    _hooks.Add(hook);
                    _logger.Info("IDirectInputDevice8::GetDeviceData 钩子已安装");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error("DirectInput 设备钩子安装失败", ex);
        }
    }

    /// <summary>
    /// 叠加方向键状态到 DirectInput 键盘缓冲区。
    /// </summary>
    private void ApplyDirectInputSpoof(IntPtr dataPtr)
    {
        unsafe
        {
            var data = (byte*)dataPtr.ToPointer();

            foreach (var pair in DirectInputKeyMap)
            {
                if (_state.IsDirectionKeyDown(pair.Key))
                {
                    data[pair.Value] = 0x80;
                }
            }
        }
    }

    /// <summary>
    /// 命名管道服务端：接收注入器指令。
    /// </summary>
    private void RunPipeServer(string pipeName)
    {
        while (!_state.StopRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                _logger.Info("等待注入器连接...");
                pipe.WaitForConnection();
                _logger.Info("注入器已连接");

                using var reader = new BinaryReader(pipe);
                using var writer = new BinaryWriter(pipe);

                while (pipe.IsConnected && !_state.StopRequested)
                {
                    var command = (AgentCommand)reader.ReadInt32();
                    var key = (DirectionKey)reader.ReadInt32();
                    HandleCommand(command, key, writer);
                    writer.Flush();
                }
            }
            catch (EndOfStreamException)
            {
                _logger.Info("注入器连接断开");
            }
            catch (IOException ex)
            {
                _logger.Error("管道 IO 异常", ex);
                Thread.Sleep(200);
            }
            catch (Exception ex)
            {
                _logger.Error("管道异常", ex);
                Thread.Sleep(200);
            }
        }
    }

    /// <summary>
    /// 处理注入器指令并返回响应。
    /// </summary>
    private void HandleCommand(AgentCommand command, DirectionKey key, BinaryWriter writer)
    {
        switch (command)
        {
            case AgentCommand.KeyDown:
                if (IsValidDirectionKey(key))
                {
                    _state.SetDirectionKey(key, true);
                }
                writer.Write((int)AgentResponseType.Ack);
                return;
            case AgentCommand.KeyUp:
                if (IsValidDirectionKey(key))
                {
                    _state.SetDirectionKey(key, false);
                }
                writer.Write((int)AgentResponseType.Ack);
                return;
            case AgentCommand.Clear:
                _state.ClearAll();
                writer.Write((int)AgentResponseType.Ack);
                return;
            case AgentCommand.EnableSpoof:
                _state.SetSpoofEnabled(true);
                writer.Write((int)AgentResponseType.Ack);
                return;
            case AgentCommand.DisableSpoof:
                _state.SetSpoofEnabled(false);
                writer.Write((int)AgentResponseType.Ack);
                return;
            case AgentCommand.DumpStats:
                var stats = _state.SnapshotStats();
                writer.Write((int)AgentResponseType.Stats);
                writer.Write(stats.GetAsyncKeyStateCalls);
                writer.Write(stats.DirectInputCreateCalls);
                writer.Write(stats.CreateDeviceCalls);
                writer.Write(stats.GetDeviceStateCalls);
                writer.Write(stats.GetDeviceDataCalls);
                return;
            case AgentCommand.Stop:
                _state.RequestStop();
                writer.Write((int)AgentResponseType.Ack);
                return;
            default:
                writer.Write((int)AgentResponseType.Ack);
                return;
        }
    }

    /// <summary>
    /// 根据 VK 映射方向键。
    /// </summary>
    private static bool TryMapDirectionKey(int vKey, out DirectionKey direction)
    {
        foreach (var pair in VirtualKeyMap)
        {
            if (pair.Value == vKey)
            {
                direction = pair.Key;
                return true;
            }
        }

        direction = DirectionKey.Up;
        return false;
    }

    /// <summary>
    /// 方向键合法性检查。
    /// </summary>
    private static bool IsValidDirectionKey(DirectionKey key)
    {
        return key == DirectionKey.Up
            || key == DirectionKey.Down
            || key == DirectionKey.Left
            || key == DirectionKey.Right;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate short GetAsyncKeyStateDelegate(int vKey);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DirectInput8CreateDelegate(IntPtr hInstance, uint dwVersion, ref Guid riidltf, out IntPtr ppvOut, IntPtr punkOuter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateDeviceDelegate(IntPtr self, ref Guid rguid, out IntPtr device, IntPtr pUnkOuter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDeviceStateDelegate(IntPtr self, int cbData, IntPtr lpvData);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDeviceDataDelegate(IntPtr self, int cbObjectData, IntPtr rgdod, ref int pdwInOut, int dwFlags);
}
}

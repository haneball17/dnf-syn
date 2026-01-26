using System.Runtime.InteropServices;

namespace DNFSyncBox;

/// <summary>
/// 共享内存中的键盘状态结构（V2）。
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal unsafe struct SharedKeyboardStateV2
{
    public uint Version;
    public uint Seq;
    public uint Flags;
    public uint ActivePid;
    public uint ProfileId;
    public uint ProfileMode;
    public ulong LastTick;
    public fixed byte KeyboardState[SharedMemoryConstants.KeyCount];
    public fixed uint EdgeCounter[SharedMemoryConstants.KeyCount];
    public fixed byte TargetMask[SharedMemoryConstants.KeyCount];
    public fixed byte BlockMask[SharedMemoryConstants.KeyCount];
}

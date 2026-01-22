using System;
using System.Collections.Generic;

namespace DNFSyncBox;

public sealed class WindowSnapshot
{
    /// <summary>
    /// 空快照，用于初始化与异常回退。
    /// </summary>
    public static WindowSnapshot Empty { get; } = new(IntPtr.Zero, Array.Empty<IntPtr>(), false, 0, 0);

    /// <summary>
    /// 窗口扫描结果快照。
    /// </summary>
    public WindowSnapshot(IntPtr masterHandle, IReadOnlyList<IntPtr> slaveHandles, bool foregroundIsDnf, int totalCount, uint foregroundProcessId)
    {
        MasterHandle = masterHandle;
        SlaveHandles = slaveHandles;
        ForegroundIsDnf = foregroundIsDnf;
        TotalCount = totalCount;
        ForegroundProcessId = foregroundProcessId;
    }

    public IntPtr MasterHandle { get; }
    public IReadOnlyList<IntPtr> SlaveHandles { get; }
    public bool ForegroundIsDnf { get; }
    public int TotalCount { get; }
    /// <summary>
    /// 前台 DNF 进程 ID（用于共享内存旁路）。
    /// </summary>
    public uint ForegroundProcessId { get; }
}

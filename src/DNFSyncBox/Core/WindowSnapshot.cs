using System;
using System.Collections.Generic;

namespace DNFSyncBox;

public sealed class WindowSnapshot
{
    /// <summary>
    /// 空快照，用于初始化与异常回退。
    /// </summary>
    public static WindowSnapshot Empty { get; } = new(IntPtr.Zero, Array.Empty<IntPtr>(), false, 0);

    /// <summary>
    /// 窗口扫描结果快照。
    /// </summary>
    public WindowSnapshot(IntPtr masterHandle, IReadOnlyList<IntPtr> slaveHandles, bool foregroundIsDnf, int totalCount)
    {
        MasterHandle = masterHandle;
        SlaveHandles = slaveHandles;
        ForegroundIsDnf = foregroundIsDnf;
        TotalCount = totalCount;
    }

    public IntPtr MasterHandle { get; }
    public IReadOnlyList<IntPtr> SlaveHandles { get; }
    public bool ForegroundIsDnf { get; }
    public int TotalCount { get; }
}

using System;
using System.Collections.Generic;

namespace DNFSyncBox;

public sealed class WindowSnapshot
{
    public static WindowSnapshot Empty { get; } = new(IntPtr.Zero, Array.Empty<IntPtr>(), false, 0);

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

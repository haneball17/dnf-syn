using System;

namespace DNFSyncBox;

public sealed class SyncStatus
{
    public bool IsPaused { get; init; }
    public bool IsAutoPaused { get; init; }
    public bool ForegroundIsDnf { get; init; }
    public IntPtr MasterHandle { get; init; }
    public int SlaveCount { get; init; }
    public int TotalCount { get; init; }
}

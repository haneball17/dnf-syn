using System;

namespace DNFSyncBox;

public sealed class SyncStatus
{
    /// <summary>
    /// 用户暂停或自动暂停后的综合状态。
    /// </summary>
    public bool IsPaused { get; init; }
    /// <summary>
    /// 前台非 DNF 导致的自动暂停。
    /// </summary>
    public bool IsAutoPaused { get; init; }
    /// <summary>
    /// 当前前台窗口是否为 DNF。
    /// </summary>
    public bool ForegroundIsDnf { get; init; }
    /// <summary>
    /// 主控窗口句柄（前台 DNF）。
    /// </summary>
    public IntPtr MasterHandle { get; init; }
    /// <summary>
    /// 从控窗口数量。
    /// </summary>
    public int SlaveCount { get; init; }
    /// <summary>
    /// 总窗口数量。
    /// </summary>
    public int TotalCount { get; init; }
}

using System;
using System.IO.MemoryMappedFiles;
using System.Threading;

namespace DNFSyncBox;

/// <summary>
/// 共享内存写入端：负责把键盘快照写入全局共享内存。
/// </summary>
internal sealed unsafe class SharedMemoryWriter : IDisposable
{
    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _accessor;
    private byte* _basePtr;
    private bool _initialized;

    public bool IsReady => _initialized;

    /// <summary>
    /// 初始化共享内存并写入版本号。
    /// </summary>
    public void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        _mmf = MemoryMappedFile.CreateOrOpen(
            SharedMemoryConstants.MappingName,
            SharedMemoryConstants.SharedMemorySize,
            MemoryMappedFileAccess.ReadWrite);

        _accessor = _mmf.CreateViewAccessor(0, SharedMemoryConstants.SharedMemorySize, MemoryMappedFileAccess.ReadWrite);
        byte* rawPtr = null;
        _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref rawPtr);
        _basePtr = rawPtr + _accessor.PointerOffset;
        _initialized = true;

        new Span<byte>(_basePtr, SharedMemoryConstants.SharedMemorySize).Clear();
        var shared = (SharedKeyboardStateV2*)_basePtr;
        shared->Version = SharedMemoryConstants.Version;
    }

    /// <summary>
    /// 发布键盘状态快照（使用 seq 进行无锁一致性保护）。
    /// </summary>
    public void PublishSnapshot(
        uint flags,
        uint activePid,
        uint profileId,
        uint profileMode,
        ulong lastTick,
        byte[] keyboardState,
        uint[] edgeCounter,
        byte[] targetMask,
        byte[] blockMask)
    {
        if (!_initialized)
        {
            return;
        }

        var shared = (SharedKeyboardStateV2*)_basePtr;
        var seq = shared->Seq + 1;
        if ((seq & 1) == 0)
        {
            seq++;
        }

        shared->Seq = seq;
        shared->Flags = flags;
        shared->ActivePid = activePid;
        shared->ProfileId = profileId;
        shared->ProfileMode = profileMode;
        shared->LastTick = lastTick;

        fixed (byte* srcState = keyboardState)
        {
            Buffer.MemoryCopy(
                srcState,
                shared->KeyboardState,
                SharedMemoryConstants.KeyCount,
                SharedMemoryConstants.KeyCount);
        }

        fixed (uint* srcEdge = edgeCounter)
        {
            Buffer.MemoryCopy(
                srcEdge,
                shared->EdgeCounter,
                SharedMemoryConstants.KeyCount * sizeof(uint),
                SharedMemoryConstants.KeyCount * sizeof(uint));
        }

        fixed (byte* srcMask = targetMask)
        {
            Buffer.MemoryCopy(
                srcMask,
                shared->TargetMask,
                SharedMemoryConstants.KeyCount,
                SharedMemoryConstants.KeyCount);
        }

        fixed (byte* srcBlock = blockMask)
        {
            Buffer.MemoryCopy(
                srcBlock,
                shared->BlockMask,
                SharedMemoryConstants.KeyCount,
                SharedMemoryConstants.KeyCount);
        }

        Thread.MemoryBarrier();
        shared->Seq = seq + 1;
    }

    /// <summary>
    /// 释放共享内存资源。
    /// </summary>
    public void Dispose()
    {
        if (_accessor != null)
        {
            _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
            _accessor.Dispose();
            _accessor = null;
        }

        _mmf?.Dispose();
        _mmf = null;
        _initialized = false;
    }

}

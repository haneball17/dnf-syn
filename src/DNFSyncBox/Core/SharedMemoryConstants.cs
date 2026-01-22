namespace DNFSyncBox;

/// <summary>
/// 共享内存协议常量。
/// </summary>
internal static unsafe class SharedMemoryConstants
{
    public const string MappingName = "Local\\DNFSyncBox.KeyboardState.V1";
    public const uint Version = 1;
    public const int KeyCount = 256;

    public const uint FlagPaused = 0x1;
    public const uint FlagClear = 0x2;

    public const int HeartbeatIntervalMs = 50;

    public static int SharedMemorySize => sizeof(SharedKeyboardStateV1);
}

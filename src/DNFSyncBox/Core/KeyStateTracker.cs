namespace DNFSyncBox;

public sealed class KeyStateTracker
{
    private readonly bool[] _down = new bool[SharedMemoryConstants.KeyCount];
    private readonly uint[] _edgeCounter = new uint[SharedMemoryConstants.KeyCount];

    /// <summary>
    /// 更新按键状态，返回是否发生变化（用于去重与边沿计数）。
    /// </summary>
    public bool SetState(int vKey, bool isDown)
    {
        if (vKey < 0 || vKey >= SharedMemoryConstants.KeyCount)
        {
            return false;
        }

        if (_down[vKey] == isDown)
        {
            return false;
        }

        _down[vKey] = isDown;
        if (isDown)
        {
            _edgeCounter[vKey] = unchecked(_edgeCounter[vKey] + 1);
        }

        return true;
    }

    /// <summary>
    /// 按方案生成键盘快照。
    /// </summary>
    internal void ApplyProfile(KeyboardProfile profile, byte[] toggleState, byte[] keyboardState, uint[] edgeOut, byte[] maskOut)
    {
        profile.Apply(_down, _edgeCounter, toggleState, keyboardState, edgeOut, maskOut);
    }

    /// <summary>
    /// 复制边沿计数（用于暂停/清键时保持一致性）。
    /// </summary>
    public void CopyEdgeCounters(uint[] edgeOut)
    {
        Array.Copy(_edgeCounter, edgeOut, SharedMemoryConstants.KeyCount);
    }

    /// <summary>
    /// 清空所有按下状态（用于暂停时的清键逻辑）。
    /// </summary>
    public void Clear()
    {
        Array.Clear(_down, 0, _down.Length);
    }
}

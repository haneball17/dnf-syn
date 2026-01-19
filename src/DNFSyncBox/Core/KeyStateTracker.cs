using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace DNFSyncBox;

public sealed class KeyStateTracker
{
    private readonly HashSet<Keys> _downKeys = new();

    /// <summary>
    /// 更新按键状态，返回是否发生变化（用于去重）。
    /// </summary>
    public bool SetState(Keys key, bool isDown)
    {
        return isDown ? _downKeys.Add(key) : _downKeys.Remove(key);
    }

    /// <summary>
    /// 获取当前按下的键集合快照。
    /// </summary>
    public IReadOnlyList<Keys> GetDownKeys()
    {
        return _downKeys.ToList();
    }

    /// <summary>
    /// 清空所有按下状态（用于暂停时的清键逻辑）。
    /// </summary>
    public void Clear()
    {
        _downKeys.Clear();
    }
}

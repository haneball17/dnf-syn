using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace DNFSyncBox;

public sealed class KeyStateTracker
{
    private readonly HashSet<Keys> _downKeys = new();

    public bool SetState(Keys key, bool isDown)
    {
        return isDown ? _downKeys.Add(key) : _downKeys.Remove(key);
    }

    public IReadOnlyList<Keys> GetDownKeys()
    {
        return _downKeys.ToList();
    }

    public void Clear()
    {
        _downKeys.Clear();
    }
}

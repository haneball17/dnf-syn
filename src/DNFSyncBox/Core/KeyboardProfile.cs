using System;
using System.Collections.Generic;

namespace DNFSyncBox;

/// <summary>
/// 伪造方案类型。
/// </summary>
internal enum KeyboardProfileMode : uint
{
    All = 0,
    Whitelist = 1,
    Blacklist = 2,
    Mapping = 3
}

/// <summary>
/// 映射行为：用于非 Mapping 模式下的按键替换策略。
/// </summary>
internal enum KeyboardMappingBehavior : uint
{
    None = 0,
    Replace = 1
}

/// <summary>
/// 已解析的键盘伪造方案。
/// </summary>
internal sealed class KeyboardProfile
{
    private readonly HashSet<int> _keys;
    private readonly List<KeyMapping> _mappings;
    private readonly KeyboardMappingBehavior _mappingBehavior;

    public KeyboardProfile(
        string id,
        KeyboardProfileMode mode,
        IEnumerable<int> keys,
        IEnumerable<KeyMapping> mappings,
        KeyboardMappingBehavior mappingBehavior)
    {
        Id = string.IsNullOrWhiteSpace(id) ? "default" : id.Trim();
        Mode = mode;
        ProfileId = ComputeProfileId(Id);
        _keys = new HashSet<int>(keys);
        _mappings = new List<KeyMapping>(mappings);
        _mappingBehavior = mappingBehavior;
    }

    public string Id { get; }
    public KeyboardProfileMode Mode { get; }
    public uint ProfileId { get; }
    public KeyboardMappingBehavior MappingBehavior => _mappingBehavior;

    /// <summary>
    /// 根据方案生成目标键掩码（1 表示覆盖该键）。
    /// </summary>
    public void BuildMask(byte[] maskOut)
    {
        Array.Clear(maskOut, 0, maskOut.Length);

        switch (Mode)
        {
            case KeyboardProfileMode.All:
                for (var i = 0; i < SharedMemoryConstants.KeyCount; i++)
                {
                    maskOut[i] = 1;
                }
                break;
            case KeyboardProfileMode.Whitelist:
                foreach (var key in _keys)
                {
                    if (key >= 0 && key < SharedMemoryConstants.KeyCount)
                    {
                        maskOut[key] = 1;
                    }
                }
                break;
            case KeyboardProfileMode.Blacklist:
                for (var i = 0; i < SharedMemoryConstants.KeyCount; i++)
                {
                    maskOut[i] = 1;
                }
                foreach (var key in _keys)
                {
                    if (key >= 0 && key < SharedMemoryConstants.KeyCount)
                    {
                        maskOut[key] = 0;
                    }
                }
                break;
            case KeyboardProfileMode.Mapping:
                foreach (var mapping in _mappings)
                {
                    if (mapping.Target >= 0 && mapping.Target < SharedMemoryConstants.KeyCount)
                    {
                        maskOut[mapping.Target] = 1;
                    }
                }
                break;
        }
    }

    /// <summary>
    /// 根据方案生成强制拦截掩码（1 表示强制抬起该键）。
    /// </summary>
    public void BuildBlockMask(byte[] blockMaskOut)
    {
        Array.Clear(blockMaskOut, 0, blockMaskOut.Length);

        if (Mode != KeyboardProfileMode.Blacklist)
        {
            return;
        }

        foreach (var key in _keys)
        {
            if (key >= 0 && key < SharedMemoryConstants.KeyCount)
            {
                blockMaskOut[key] = 1;
            }
        }
    }

    /// <summary>
    /// 按方案生成键盘状态与边沿计数快照。
    /// </summary>
    public void Apply(bool[] down, uint[] edgeCounter, byte[] toggleState, byte[] keyboardState, uint[] edgeOut, byte[] maskOut)
    {
        Array.Clear(keyboardState, 0, keyboardState.Length);
        Array.Clear(edgeOut, 0, edgeOut.Length);
        BuildMask(maskOut);

        if (Mode == KeyboardProfileMode.Mapping)
        {
            foreach (var mapping in _mappings)
            {
                if (mapping.Source < 0 || mapping.Source >= SharedMemoryConstants.KeyCount)
                {
                    continue;
                }

                if (mapping.Target < 0 || mapping.Target >= SharedMemoryConstants.KeyCount)
                {
                    continue;
                }

                if (down[mapping.Source])
                {
                    keyboardState[mapping.Target] = (byte)(0x80 | (toggleState[mapping.Target] & 0x01));
                }

                edgeOut[mapping.Target] = edgeCounter[mapping.Source];
            }

            return;
        }

        // 覆盖式映射：在非 Mapping 模式下，用目标键替换源键输出。
        bool useReplace = _mappingBehavior == KeyboardMappingBehavior.Replace && _mappings.Count > 0;
        bool[]? suppressSource = null;
        bool[]? mappedDown = null;
        uint[]? mappedEdge = null;

        if (useReplace)
        {
            suppressSource = new bool[SharedMemoryConstants.KeyCount];
            mappedDown = new bool[SharedMemoryConstants.KeyCount];
            mappedEdge = new uint[SharedMemoryConstants.KeyCount];

            foreach (var mapping in _mappings)
            {
                if (mapping.Source < 0 || mapping.Source >= SharedMemoryConstants.KeyCount)
                {
                    continue;
                }

                if (mapping.Target < 0 || mapping.Target >= SharedMemoryConstants.KeyCount)
                {
                    continue;
                }

                // 白名单/黑名单仅以主控视角判断：源键不在掩码中则不参与映射。
                if (maskOut[mapping.Source] == 0)
                {
                    continue;
                }

                // 目标键必须被输出，否则替换不会生效。
                maskOut[mapping.Target] = 1;
                suppressSource[mapping.Source] = true;

                if (down[mapping.Source])
                {
                    mappedDown[mapping.Target] = true;
                }

                if (edgeCounter[mapping.Source] > mappedEdge[mapping.Target])
                {
                    mappedEdge[mapping.Target] = edgeCounter[mapping.Source];
                }
            }
        }

        for (var i = 0; i < SharedMemoryConstants.KeyCount; i++)
        {
            if (maskOut[i] == 0)
            {
                continue;
            }

            if (useReplace && suppressSource != null && suppressSource[i])
            {
                // 替换模式下屏蔽源键，避免后台读取到原键状态。
                edgeOut[i] = 0;
                continue;
            }

            var desiredDown = down[i];
            var desiredEdge = edgeCounter[i];

            if (useReplace && mappedDown != null && mappedDown[i])
            {
                desiredDown = true;
            }

            if (useReplace && mappedEdge != null && mappedEdge[i] > desiredEdge)
            {
                desiredEdge = mappedEdge[i];
            }

            if (desiredDown)
            {
                keyboardState[i] = (byte)(0x80 | (toggleState[i] & 0x01));
            }

            edgeOut[i] = desiredEdge;
        }
    }

    private static uint ComputeProfileId(string id)
    {
        const uint offset = 2166136261;
        const uint prime = 16777619;
        var hash = offset;
        foreach (var ch in id)
        {
            hash ^= ch;
            hash *= prime;
        }

        return hash;
    }

    internal readonly struct KeyMapping
    {
        public KeyMapping(int source, int target)
        {
            Source = source;
            Target = target;
        }

        public int Source { get; }
        public int Target { get; }
    }
}

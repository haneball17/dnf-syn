using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows.Forms;

namespace DNFSyncBox;

/// <summary>
/// 方案配置管理器：负责读取/热切换配置并生成可执行方案。
/// </summary>
internal sealed class KeyboardProfileManager
{
    private readonly string _configPath;
    private DateTime _lastWriteTimeUtc;
    private KeyboardProfile _activeProfile;

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public KeyboardProfileManager(string? configPath = null)
    {
        _configPath = string.IsNullOrWhiteSpace(configPath)
            ? DefaultConfigPath
            : configPath!;

        _activeProfile = CreateDefaultProfile();
        EnsureConfigFile();
        _ = ReloadInternal(force: true, out _);
    }

    public static string DefaultConfigPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DNFSyncBox",
        "profiles.json");

    public string ConfigPath => _configPath;
    public KeyboardProfile ActiveProfile => _activeProfile;

    /// <summary>
    /// 若配置文件有变化则重载，返回是否更新以及日志信息。
    /// </summary>
    public bool ReloadIfChanged(out string? message)
    {
        return ReloadInternal(force: false, out message);
    }

    private bool ReloadInternal(bool force, out string? message)
    {
        message = null;
        EnsureConfigFile();

        var lastWrite = File.GetLastWriteTimeUtc(_configPath);
        if (!force && lastWrite <= _lastWriteTimeUtc)
        {
            return false;
        }

        _lastWriteTimeUtc = lastWrite;

        try
        {
            var json = File.ReadAllText(_configPath);
            var config = JsonSerializer.Deserialize<KeyboardProfileConfig>(json, _jsonOptions);
            if (config == null)
            {
                _activeProfile = CreateDefaultProfile();
                message = "配置解析失败，已回退到默认方案。";
                return true;
            }

            var warnings = new List<string>();
            var profile = BuildProfile(config, warnings);
            _activeProfile = profile;

            message = $"已加载方案：{profile.Id}（{profile.Mode}）";
            if (warnings.Count > 0)
            {
                message += $" | 警告：{string.Join("，", warnings)}";
            }

            return true;
        }
        catch (Exception ex)
        {
            message = $"配置加载异常，沿用旧方案。原因：{ex.Message}";
            return false;
        }
    }

    private void EnsureConfigFile()
    {
        if (File.Exists(_configPath))
        {
            return;
        }

        var directory = Path.GetDirectoryName(_configPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var defaultConfig = KeyboardProfileConfig.CreateDefault();
        var json = JsonSerializer.Serialize(defaultConfig, _jsonOptions);
        File.WriteAllText(_configPath, json);
    }

    private static KeyboardProfile BuildProfile(KeyboardProfileConfig config, List<string> warnings)
    {
        if (config.Profiles == null || config.Profiles.Count == 0)
        {
            warnings.Add("未找到有效方案，已使用默认方案。");
            return CreateDefaultProfile();
        }

        KeyboardProfileDefinition? activeDefinition = null;
        foreach (var profile in config.Profiles)
        {
            if (!string.IsNullOrWhiteSpace(config.ActiveProfile)
                && string.Equals(profile.Id, config.ActiveProfile, StringComparison.OrdinalIgnoreCase))
            {
                activeDefinition = profile;
                break;
            }
        }

        activeDefinition ??= config.Profiles[0];

        var mode = ParseMode(activeDefinition.Mode);
        var keys = ParseKeys(activeDefinition.Keys, warnings);
        var mappings = ParseMappings(activeDefinition.Mappings, warnings);
        var mappingBehavior = ParseMappingBehavior(activeDefinition.MappingBehavior, warnings);

        if (mode == KeyboardProfileMode.Mapping && mappings.Count == 0)
        {
            warnings.Add("映射方案未配置有效映射，已按空映射处理。");
        }

        if (mappingBehavior != KeyboardMappingBehavior.None && mappings.Count == 0)
        {
            warnings.Add("映射行为已配置但无有效映射，已忽略映射行为。");
            mappingBehavior = KeyboardMappingBehavior.None;
        }

        return new KeyboardProfile(activeDefinition.Id, mode, keys, mappings, mappingBehavior);
    }

    private static KeyboardProfileMode ParseMode(string? text)
    {
        if (!string.IsNullOrWhiteSpace(text)
            && Enum.TryParse<KeyboardProfileMode>(text, true, out var mode))
        {
            return mode;
        }

        return KeyboardProfileMode.All;
    }

    private static KeyboardMappingBehavior ParseMappingBehavior(string? text, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return KeyboardMappingBehavior.None;
        }

        if (Enum.TryParse<KeyboardMappingBehavior>(text, true, out var behavior))
        {
            return behavior;
        }

        warnings.Add($"无效映射行为:{text}");
        return KeyboardMappingBehavior.None;
    }

    private static List<int> ParseKeys(List<string>? keys, List<string> warnings)
    {
        var result = new List<int>();
        if (keys == null)
        {
            return result;
        }

        foreach (var keyText in keys)
        {
            if (TryParseVirtualKey(keyText, out var vKey))
            {
                result.Add(vKey);
            }
            else
            {
                warnings.Add($"无效键名:{keyText}");
            }
        }

        return result;
    }

    private static List<KeyboardProfile.KeyMapping> ParseMappings(Dictionary<string, string>? mappings, List<string> warnings)
    {
        var result = new List<KeyboardProfile.KeyMapping>();
        if (mappings == null)
        {
            return result;
        }

        foreach (var pair in mappings)
        {
            if (!TryParseVirtualKey(pair.Key, out var source))
            {
                warnings.Add($"无效映射键:{pair.Key}");
                continue;
            }

            if (!TryParseVirtualKey(pair.Value, out var target))
            {
                warnings.Add($"无效映射目标:{pair.Value}");
                continue;
            }

            result.Add(new KeyboardProfile.KeyMapping(source, target));
        }

        return result;
    }

    private static bool TryParseVirtualKey(string? text, out int vKey)
    {
        vKey = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (!Enum.TryParse<Keys>(text, true, out var key))
        {
            return false;
        }

        var code = (int)(key & Keys.KeyCode);
        if (code < 0 || code >= SharedMemoryConstants.KeyCount)
        {
            return false;
        }

        vKey = code;
        return true;
    }

    private static KeyboardProfile CreateDefaultProfile()
    {
        return new KeyboardProfile(
            "all_except_f12",
            KeyboardProfileMode.Blacklist,
            new[] { (int)Keys.F12 },
            new[]
            {
                new KeyboardProfile.KeyMapping((int)Keys.Q, (int)Keys.Oem4),
                new KeyboardProfile.KeyMapping((int)Keys.D, (int)Keys.L),
                new KeyboardProfile.KeyMapping((int)Keys.F, (int)Keys.OemSemicolon),
                new KeyboardProfile.KeyMapping((int)Keys.G, (int)Keys.Oem7),
                new KeyboardProfile.KeyMapping((int)Keys.C, (int)Keys.Oem6)
            },
            KeyboardMappingBehavior.Replace);
    }
}

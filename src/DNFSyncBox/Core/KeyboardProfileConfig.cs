using System.Collections.Generic;

namespace DNFSyncBox;

/// <summary>
/// 配置文件对应的模型。
/// </summary>
internal sealed class KeyboardProfileConfig
{
    public string ActiveProfile { get; set; } = "full";
    public List<KeyboardProfileDefinition> Profiles { get; set; } = new();

    public static KeyboardProfileConfig CreateDefault()
    {
        return new KeyboardProfileConfig
        {
            ActiveProfile = "full",
            Profiles = new List<KeyboardProfileDefinition>
            {
                new KeyboardProfileDefinition
                {
                    Id = "full",
                    Mode = "All"
                }
            }
        };
    }
}

/// <summary>
/// 单个方案定义（来自 JSON）。
/// </summary>
internal sealed class KeyboardProfileDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Mode { get; set; } = "All";
    public List<string>? Keys { get; set; }
    public Dictionary<string, string>? Mappings { get; set; }
}

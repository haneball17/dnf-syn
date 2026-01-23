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
            ActiveProfile = "all_except_f12",
            Profiles = new List<KeyboardProfileDefinition>
            {
                new KeyboardProfileDefinition
                {
                    Id = "all_except_f12",
                    Mode = "Blacklist",
                    Keys = new List<string> { "F12" },
                    Mappings = new Dictionary<string, string>
                    {
                        { "Q", "Oem4" },
                        { "D", "L" },
                        { "F", "OemSemicolon" },
                        { "G", "Oem7" },
                        { "C", "Oem6" }
                    },
                    MappingBehavior = "Replace"
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
    public string? MappingBehavior { get; set; }
}

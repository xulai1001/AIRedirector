using System.Text.Json;

namespace AIRedirector;

internal sealed class AIRedirectorConfig
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public bool UAF { get; set; }
    public string UAF_Path { get; set; } = string.Empty;
    public bool Cook { get; set; }
    public string Cook_Path { get; set; } = string.Empty;
    public bool Mecha { get; set; }
    public string Mecha_Path { get; set; } = string.Empty;
    public bool Legend { get; set; }
    public string Legend_Path { get; set; } = string.Empty;
    /// 拉面剧本（scenarioId=14，详见集成文档 §3.4 / §4.2）
    public bool Ramen { get; set; }
    public string Ramen_Path { get; set; } = string.Empty;

    public static AIRedirectorConfig Load(string path)
    {
        if (!File.Exists(path))
            return new AIRedirectorConfig();

        var config = JsonSerializer.Deserialize<AIRedirectorConfig>(File.ReadAllText(path), JsonOptions);
        if (config is null)
            throw new InvalidDataException($"AIRedirector 配置文件为空或格式无效: {path}");
        // 单选约束：历史多选配置可能同时勾选多个 AI，加载时只保留第一个启用项
        config.EnforceSingleSelection();
        return config;
    }

    /// <summary>单选约束：多选历史配置只保留第一个启用的 AI，其余关闭。</summary>
    public void EnforceSingleSelection()
    {
        var scenarios = new (string Name, Func<bool> Get, Action<bool> Set)[]
        {
            ("UAF", () => UAF, v => UAF = v),
            ("Cook", () => Cook, v => Cook = v),
            ("Mecha", () => Mecha, v => Mecha = v),
            ("Legend", () => Legend, v => Legend = v),
            ("Ramen", () => Ramen, v => Ramen = v),
        };

        string? firstEnabled = null;
        foreach (var s in scenarios)
        {
            if (s.Get())
            {
                firstEnabled = s.Name;
                break;
            }
        }
        if (firstEnabled is null)
            return;

        foreach (var s in scenarios)
        {
            if (s.Name != firstEnabled && s.Get())
                s.Set(false);
        }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }
}

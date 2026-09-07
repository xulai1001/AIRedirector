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
        return config ?? throw new InvalidDataException($"AIRedirector 配置文件为空或格式无效: {path}");
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }
}

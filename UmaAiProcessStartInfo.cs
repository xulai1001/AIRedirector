using System.Diagnostics;
using System.Text;

namespace AIRedirector;

internal static class UmaAiProcessStartInfo
{
    /// <summary>
    /// 构造启动 UmaAI 子进程的 <see cref="ProcessStartInfo"/>。
    /// </summary>
    /// <param name="executablePath">UmaAI.exe 完整路径（用户配置输入，必须已经过验证）</param>
    /// <param name="jsonMode">
    /// 是否以 AIRedirector 模式启动——stdout 严格只 JSON，stderr 用于横幅 / 日志。
    /// 玩家手玩不传（保留彩色 + 启动横幅走 stdout）。
    /// </param>
    public static ProcessStartInfo Create(string executablePath, bool jsonMode = false)
    {
        var fullPath = Path.GetFullPath(executablePath);
        var workingDirectory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException($"无法解析 UmaAI 程序所在目录: {executablePath}", nameof(executablePath));

        return new ProcessStartInfo
        {
            FileName = fullPath,
            WorkingDirectory = workingDirectory,
            Arguments = jsonMode ? "--json" : string.Empty,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
    }
}

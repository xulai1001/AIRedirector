using System.Text;
using System.Text.RegularExpressions;
using UmamusumeResponseAnalyzer.LiveDisplay;

namespace AIRedirector;

internal sealed class UmaAiRawOutputWorkspace : IDisposable
{
    const string WorkspaceTitle = "AIRedirector";
    const string PanelKey = "umaai-raw-output";
    const string PanelTitle = "UmaAI.exe 原始输出";
    internal const int MaxLines = 300;
    static readonly Regex TerminalControlSequencePattern = new(
        @"\x1B\][^\a]*(?:\a|\x1B\\)|\x1B\[[0-?]*[ -/]*[@-~]|\x1B[@-Z\\-_]",
        RegexOptions.Compiled);

    readonly object gate = new();
    readonly List<string> lines = [];
    ILiveDisplayOutput? liveDisplay;
    LiveDisplayWorkspace? workspace;

    public void Initialize(ILiveDisplayOutput output)
    {
        lock (gate)
        {
            liveDisplay = output;
            workspace = output.CreateWorkspace(WorkspaceTitle);
        }
    }

    public void AppendLine(string? line)
    {
        if (line is null)
            return;

        lock (gate)
        {
            if (liveDisplay is not { } output || workspace is not { } target)
                return;

            lines.Add(line);
            if (lines.Count > MaxLines)
                lines.RemoveRange(0, lines.Count - MaxLines);

            output.SetPanel(
                target,
                PanelKey,
                PanelTitle,
                LiveDisplayContent.Text(Render()),
                fullBleed: true,
                switchToWorkspace: false);
        }
    }

    string Render()
        => string.Join(Environment.NewLine, lines.Select(SanitizeForLiveDisplay));

    public void Dispose()
    {
        ILiveDisplayOutput? output;
        LiveDisplayWorkspace? target;
        lock (gate)
        {
            output = liveDisplay;
            target = workspace;
            liveDisplay = null;
            workspace = null;
            lines.Clear();
        }

        if (output is not null && target is not null)
            output.RemoveWorkspace(target);
    }

    static string SanitizeForLiveDisplay(string line)
    {
        var withoutTerminalSequences = TerminalControlSequencePattern.Replace(line, string.Empty);
        var text = new StringBuilder(withoutTerminalSequences.Length);
        foreach (var ch in withoutTerminalSequences)
        {
            if (ch == '\t')
            {
                text.Append("    ");
                continue;
            }

            if (!char.IsControl(ch))
                text.Append(ch);
        }

        return text.ToString();
    }
}

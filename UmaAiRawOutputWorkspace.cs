using System.Text;
using System.Text.RegularExpressions;
using UmamusumeResponseAnalyzer.TerminalGui;

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
    readonly SemaphoreSlim publishGate = new(1, 1);
    readonly List<string> lines = [];
    Workspace? workspace;
    long nextSequence;
    long latestPublishSequence;

    public (long Sequence, string Text)? AppendLine(string? line)
    {
        if (line is null)
            return null;

        lock (gate)
        {
            lines.Add(line);
            if (lines.Count > MaxLines)
                lines.RemoveRange(0, lines.Count - MaxLines);
            return (++nextSequence, Render());
        }
    }

    public void WaitToPublish()
        => publishGate.Wait();

    public void ReleasePublish()
        => publishGate.Release();

    public bool Publish((long Sequence, string Text) snapshot)
    {
        lock (gate)
        {
            if (snapshot.Sequence <= latestPublishSequence)
                return false;
        }

        var target = Workspace.Create(WorkspaceTitle);
        target.SetPanel(
            PanelKey,
            PanelTitle,
            WorkspaceContent.Text(snapshot.Text),
            fullBleed: true,
            switchToWorkspace: false);

        lock (gate)
        {
            latestPublishSequence = snapshot.Sequence;
            workspace = target;
        }
        return true;
    }

    string Render()
        => string.Join(Environment.NewLine, lines.Select(SanitizeForWorkspace));

    public void Dispose()
    {
        Workspace? target;
        lock (gate)
        {
            target = workspace;
            lines.Clear();
        }

        if (target is null)
            return;

        target.RemovePanel(PanelKey);
        lock (gate)
        {
            if (ReferenceEquals(workspace, target))
                workspace = null;
        }
    }

    static string SanitizeForWorkspace(string line)
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

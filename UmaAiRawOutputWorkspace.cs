using System.Text;
using System.Text.RegularExpressions;
using Terminal.Gui;
using Terminal.Gui.Drawing;
using Terminal.Gui.Text;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using UmamusumeResponseAnalyzer.TerminalGui;

namespace AIRedirector;

/// <summary>屏幕消息类型（决定着色）：info 亮黄色、error 红色；null 表示原样 raw 输出。</summary>
internal enum UmaAiMessageKind
{
    Info,
    Error,
}

/// <summary>原始输出面板里的一行：可选着色类型。</summary>
internal readonly record struct UmaAiDisplayLine(string Text, UmaAiMessageKind? Kind);

/// <summary>
/// AIRedirector 的唯一输出面板：Ramen 分支把已解析的 info / error 处理成文字后，
/// 以彩色（info 亮黄 / error 红）输出；未处理的 JSON（如 decision）保留原样输出。
/// </summary>
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
    readonly List<UmaAiDisplayLine> lines = [];
    Workspace? workspace;

    /// 面板底部按钮「重启AI进程」的回调（由 AIRedirector 注入；为 null 时不显示该按钮）。
    public Action? RestartRequested { get; set; }

    /// 面板底部按钮「删除回合数据」的回调（由 AIRedirector 注入；为 null 时不显示该按钮）。
    public Action? DeleteTurnDataRequested { get; set; }

    /// 面板底部按钮「独立窗口运行AI」的回调（由 AIRedirector 注入；为 null 时不显示该按钮）。
    public Action? RunInSeparateWindowRequested { get; set; }

    public bool PublishLine(string? line)
    {
        if (line is null)
            return false;

        return Publish(new(line, null));
    }

    /// <summary>发布一条 info（亮黄色）：`info: <event>`。</summary>
    public bool PublishInfo(string text) => Publish(new(text, UmaAiMessageKind.Info));

    /// <summary>发布一条 error（红色）：`error: <message>`。</summary>
    public bool PublishError(string text) => Publish(new(text, UmaAiMessageKind.Error));

    bool Publish(UmaAiDisplayLine line)
    {
        var sanitized = line with { Text = SanitizeForWorkspace(line.Text) };
        lock (gate)
        {
            lines.Add(sanitized);
            if (lines.Count > MaxLines)
                lines.RemoveRange(0, lines.Count - MaxLines);
        }

        var target = Workspace.Create(WorkspaceTitle);
        target.SetPanel(
            PanelKey,
            PanelTitle,
            new WorkspaceContent(() => new UmaAiDisplayView(LineSnapshot(), RestartRequested, DeleteTurnDataRequested, RunInSeparateWindowRequested)),
            fullBleed: true,
            switchToWorkspace: false);
        workspace = target;
        return true;
    }

    UmaAiDisplayLine[] LineSnapshot()
    {
        lock (gate)
            return lines.ToArray();
    }

    public void Dispose()
    {
        var target = workspace;
        lock (gate)
            lines.Clear();

        if (target is null)
            return;

        target.RemovePanel(PanelKey);
        workspace = null;
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

/// <summary>
/// 以「新消息在底部」的滚动方式绘制该面板：info 亮黄、error 红、其余默认色；
/// 底部保留一行按钮条，提供「重启AI进程」「删除回合数据」「独立窗口运行AI」三个可点击操作。
/// </summary>
internal sealed class UmaAiDisplayView : View
{
    const int ButtonBarHeight = 1;

    /// <summary>面板底部预留的空行数，避免与宿主底部任务栏重叠（窗口高度之上再扣掉这些行，底部留白）。</summary>
    const int BottomReserved = 2;

    readonly UmaAiDisplayLine[] lines;
    readonly Button restartButton;
    readonly Button deleteButton;
    readonly Button runSeparateButton;

    public UmaAiDisplayView(UmaAiDisplayLine[] lines, Action? onRestart, Action? onDelete, Action? onRunSeparate)
    {
        this.lines = lines;
        CanFocus = true;
        TabStop = TabBehavior.TabGroup;

        restartButton = new Button { Text = "重启AI进程" };
        if (onRestart is not null)
            restartButton.Accepting += (_, _) => onRestart();
        deleteButton = new Button { Text = "删除回合数据" };
        if (onDelete is not null)
            deleteButton.Accepting += (_, _) => onDelete();
        runSeparateButton = new Button { Text = "独立窗口运行AI" };
        if (onRunSeparate is not null)
            runSeparateButton.Accepting += (_, _) => onRunSeparate();
        Add(restartButton, deleteButton, runSeparateButton);
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        // 文本区高度 = 视口高度 - 底部按钮条 - 底部留白（避开宿主任务栏）
        var width = Viewport.Width;
        var height = Math.Max(0, Viewport.Height - ButtonBarHeight - BottomReserved);
        var start = Math.Max(0, lines.Length - height);
        var normal = GetAttributeForRole(VisualRole.Normal);

        for (var index = start; index < lines.Length; index++)
        {
            var line = lines[index];
            var attribute = line.Kind is { } kind ? KindAttribute(kind) : null;
            SetAttribute(attribute ?? normal);
            AddStr(0, index - start, TextFormatter.ClipOrPad(line.Text, width));
        }

        context?.AddDrawnRectangle(ViewportToScreen());
        return true;
    }

    protected override void OnSubViewLayout(LayoutEventArgs args)
    {
        var barY = Math.Max(0, Viewport.Height - BottomReserved - ButtonBarHeight);
        restartButton.X = 1;
        restartButton.Y = barY;
        deleteButton.X = Pos.Right(restartButton) + 2;
        deleteButton.Y = restartButton.Y;
        runSeparateButton.X = Pos.Right(deleteButton) + 2;
        runSeparateButton.Y = restartButton.Y;
        base.OnSubViewLayout(args);
    }

    static Terminal.Gui.Drawing.Attribute? KindAttribute(UmaAiMessageKind kind)
        => kind switch
        {
            UmaAiMessageKind.Info => new(StandardColor.BrightYellow, Color.None),
            UmaAiMessageKind.Error => new(StandardColor.Red, Color.None),
            _ => null
        };
}
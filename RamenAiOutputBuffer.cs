using System.Linq.Expressions;
using System.Reflection;

namespace AIRedirector;

/// AIRedirector -> RamenScenarioAnalyzer 的反射桥。
///
/// 与 LegendScenarioAnalyzer（AIRedirector 以编译期 ProjectReference 依赖）不同，
/// RamenScenarioAnalyzer 不以编译期依赖接入：这里通过反射动态调用其公开显示 API，
/// 把 UmaAI 的 decision / info / error 渲染到 RamenTrainingDisplay 的 "AI" 分区
/// （下半区 AI 面板）。反射目标（均为 public）：
///   - RamenTrainingDisplay.RegisterPartProducer(string) -> RamenTrainingDisplayPartProducer
///   - RamenTrainingDisplayPartProducer.Update(RamenTrainingDisplayId, Action<Context,Editor>)
///   - RamenTrainingDisplay.Show(RamenTrainingDisplayId, bool, CancellationToken)
/// 需要构造的委托体为 editor.Extra.AddText(line)（普通行）/ AddStyled(红色片段)（error 行）。
///
/// 面板显示模型（单轮 compute）：
///   compute_start   → 清空面板，显示「AI计算中...」
///   decision #1     → 清空面板，显示决策（若无链式则仅此一条）
///   compute_next_step → 追加「计算后续步骤中」
///   decision #2..   → 依序追加显示
///   compute_done    → 不显示
///   new_game        → 清空面板（新一局）
///   error           → 以红色追加显示
internal sealed class RamenAiOutputBuffer : IRamenAiOutputBridge
{
    static readonly bool ReflectionReady;
    static readonly Type? DisplayType;
    static readonly MethodInfo? RegisterPartProducer;
    static readonly MethodInfo? Show;
    static readonly Type? ProducerType;
    static readonly MethodInfo? Update;
    static readonly Type? IdType;
    static readonly Type? ContextType;
    static readonly Type? EditorType;
    static readonly PropertyInfo? ExtraProperty;
    static readonly MethodInfo? AddText;
    static readonly MethodInfo? AddStyled;
    static readonly Type? SegmentType;
    static readonly ConstructorInfo? SegmentCtor;
    static readonly Type? ColorType;
    static readonly object? RedColorValue;

    static RamenAiOutputBuffer()
    {
        try
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "RamenScenarioAnalyzer")
                ?? Assembly.Load("RamenScenarioAnalyzer");
            if (assembly is null)
                return;

            DisplayType = FindType(assembly, "RamenTrainingDisplay");
            ProducerType = FindType(assembly, "RamenTrainingDisplayPartProducer");
            IdType = FindType(assembly, "RamenTrainingDisplayId");
            ContextType = FindType(assembly, "RamenTrainingDisplayContext");
            EditorType = FindType(assembly, "RamenTrainingDisplayEditor");
            SegmentType = FindType(assembly, "RamenDisplaySegment");
            ColorType = FindType(assembly, "RamenDisplayColor");

            RegisterPartProducer = DisplayType?.GetMethod("RegisterPartProducer");
            Show = DisplayType?.GetMethod("Show");
            Update = ProducerType?.GetMethod("Update");
            ExtraProperty = EditorType?.GetProperty("Extra");
            var extraType = ExtraProperty?.PropertyType;
            AddText = extraType?.GetMethod("AddText");
            AddStyled = extraType?.GetMethod("AddStyled");
            SegmentCtor = SegmentType is not null && ColorType is not null
                ? SegmentType.GetConstructor([typeof(string), ColorType])
                : null;
            RedColorValue = ColorType is { } colorType ? Enum.Parse(colorType, "Red") : null;

            ReflectionReady = RegisterPartProducer is not null
                && Show is not null
                && Update is not null
                && IdType is not null
                && ContextType is not null
                && EditorType is not null
                && ExtraProperty is not null
                && AddText is not null
                && AddStyled is not null
                && SegmentType is not null
                && SegmentCtor is not null
                && ColorType is not null
                && RedColorValue is not null;
        }
        catch
        {
            // 反射初始化失败时 ReflectionReady 保持 false，构造桥时抛明确错误。
        }
    }

    static Type? FindType(Assembly assembly, string name)
        => assembly.GetType($"RamenScenarioAnalyzer.{name}", throwOnError: false);

    /// 面板里的一行：普通文本，或需要红色渲染的 error 行。
    readonly record struct UmaAiPanelLine(string Text, bool IsError);

    readonly object gate = new();
    readonly object producer; // RamenTrainingDisplayPartProducer（引用类型）
    readonly List<UmaAiPanelLine> lines = [];
    object? targetId; // boxed RamenTrainingDisplayId（值类型）
    int decisionsInRound; // 当前 compute 轮内已显示的决策数（0=尚无决策，首个决策需清屏）

    public RamenAiOutputBuffer()
    {
        if (!ReflectionReady)
            throw new InvalidOperationException("RamenScenarioAnalyzer 反射初始化失败，无法注册 AI 输出分区。");

        producer = RegisterPartProducer!.Invoke(null, ["AI"])!;
    }

    public bool ProcessLine(string? rawLine)
    {
        if (string.IsNullOrWhiteSpace(rawLine))
            return false;

        // info 事件（channel 里的行形如 "info: <event>"，由 AIRedirector.HandleOutput 生成）。
        if (TryExtractPrefix(rawLine, "info: ", out var infoEvent))
            return ApplyInfoEvent(infoEvent);

        // error（"error: <message>"）→ 红色追加显示。
        if (TryExtractPrefix(rawLine!, "error: ", out var errorMessage))
        {
            lock (gate)
                lines.Add(new(errorMessage, IsError: true));
            return true;
        }

        // decision（原始 JSON 行）。
        if (AIRedirector.TryParseUmaAiDecision(rawLine!, out var decision))
        {
            var text = AIRedirector.FormatDecisionText(decision);
            if (text.Count == 0)
                return false;

            lock (gate)
            {
                // 该 compute 轮的第一个决策：清掉「AI计算中...」占位，展示它本身；
                // 链式后续决策依序追加，实现「依次显示出来」。
                if (decisionsInRound == 0)
                    lines.Clear();
                decisionsInRound++;
                lines.AddRange(text.Select(t => new UmaAiPanelLine(t, IsError: false)));
            }

            return true;
        }

        return false;
    }

    /// 处理 info 事件（compute_start / compute_next_step / compute_done / new_game 等）。
    bool ApplyInfoEvent(string evt)
    {
        lock (gate)
        {
            switch (evt)
            {
                case "new_game":
                    lines.Clear();
                    decisionsInRound = 0;
                    return true;
                case "compute_start":
                    lines.Clear();
                    lines.Add(new("AI计算中...", IsError: false));
                    decisionsInRound = 0;
                    return true;
                case "compute_next_step":
                    lines.Add(new("计算后续步骤中", IsError: false));
                    return true;
                default:
                    // connected / compute_done / 未知事件 → 不改变显示。
                    return false;
            }
        }
    }

    static bool TryExtractPrefix(string line, string prefix, out string remainder)
    {
        if (line.StartsWith(prefix, StringComparison.Ordinal))
        {
            remainder = line[prefix.Length..];
            return true;
        }

        remainder = string.Empty;
        return false;
    }

    public void SetTarget(int singleModeCharaId, int turn)
    {
        var id = Activator.CreateInstance(IdType!, singleModeCharaId, turn);
        lock (gate)
        {
            if (Equals(targetId, id))
                return;
            targetId = id;
            lines.Clear();
            decisionsInRound = 0;
        }
    }

    public void Notify(string message, bool isError = false)
    {
        lock (gate)
        {
            lines.Clear();
            lines.Add(new UmaAiPanelLine(message, isError));
            decisionsInRound = 0;
        }

        // 立即重渲染（目标未设置或不含内容时 ApplyTargetDisplay 内部会返回 false，无副作用）。
        ApplyTargetDisplay(CancellationToken.None);
    }

    public bool ApplyTargetDisplay(CancellationToken cancellationToken)
    {
        object id;
        UmaAiPanelLine[] snapshot;
        lock (gate)
        {
            if (targetId is not { } target || lines.Count == 0)
                return false;
            id = target;
            snapshot = [.. lines];
        }

        var part = BuildPartDelegate(snapshot);
        Update!.Invoke(producer, [id, part]);
        // switchToWorkspace: true —— AI 更新 Ramen AI 面板内容时立即切回 Ramen 面板显示。
        return (bool)Show!.Invoke(null, [id, true, cancellationToken])!;
    }

    public void Dispose()
    {
        var producerType = ProducerType;
        var dispose = producerType?.GetMethod(nameof(IDisposable.Dispose));
        if (dispose is not null)
        {
            try
            {
                dispose.Invoke(producer, null);
            }
            catch
            {
                // 释放失败不影响进程清理主流程。
            }
        }
    }

    /// 构造 `Action<RamenTrainingDisplayContext, RamenTrainingDisplayEditor>` 委托：
    /// 普通行调用 `editor.Extra.AddText(line)`；error 行调用
    /// `editor.Extra.AddStyled(new RamenDisplaySegment(line, RamenDisplayColor.Red))`。
    static Delegate BuildPartDelegate(UmaAiPanelLine[] rows)
    {
        var contextParam = Expression.Parameter(ContextType!, "context");
        var editorParam = Expression.Parameter(EditorType!, "editor");
        var extra = Expression.Property(editorParam, ExtraProperty!);
        var calls = new List<Expression>(rows.Length);
        foreach (var row in rows)
        {
            Expression call;
            if (row.IsError)
            {
                var segment = Expression.New(
                    SegmentCtor!,
                    Expression.Constant(row.Text),
                    Expression.Constant(RedColorValue!, ColorType!));
                call = Expression.Call(
                    extra,
                    AddStyled!,
                    Expression.NewArrayInit(SegmentType!, segment));
            }
            else
            {
                call = Expression.Call(extra, AddText!, Expression.Constant(row.Text));
            }

            calls.Add(call);
        }

        var body = calls.Count switch
        {
            0 => Expression.Empty(),
            1 => calls[0],
            _ => Expression.Block(calls)
        };
        var actionType = typeof(Action<,>).MakeGenericType(ContextType!, EditorType!);
        return Expression.Lambda(actionType, body, contextParam, editorParam).Compile();
    }
}
using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Gallop;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using UmamusumeResponseAnalyzer.Plugin;
using UmamusumeResponseAnalyzer.TerminalGui;

namespace AIRedirector
{
    public class AIRedirector : IPlugin
    {
        const int ProcessExitWaitMilliseconds = 5000;

        static string DataDirectory => Path.Combine("PluginData", "AIRedirector");

        readonly List<Process> createdProcesses = [];
        readonly List<Process> startedProcesses = [];
        readonly Channel<ProcessOutput> processOutput = Channel.CreateUnbounded<ProcessOutput>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
            });
        ChildProcessManager? _childProcessManager;
        AIRedirectorConfig config = new();
        ILegendAiOutputBridge? legendOutput;
        IRamenAiOutputBridge? ramenOutput;
        readonly UmaAiRawOutputWorkspace rawOutput = new();
        bool gameStarted;
        readonly SemaphoreSlim processLock = new(1, 1);

        // 单选约束：当前只激活一个 AI，操作全部围绕选中的那一个进行。
        string? selectedAiName;
        string? selectedAiPath;
        bool selectedAiAppliesToLegend;
        bool selectedAiJsonMode;

        string ConfigPath => Path.Combine(DataDirectory, "settings.json");

        public void Initialize(IPluginContext context)
        {
            try
            {
                Directory.CreateDirectory(DataDirectory);
                config = AIRedirectorConfig.Load(ConfigPath);
                SyncSelectedAiFromConfig();
                // 面板底部操作按钮：重启 AI 进程 / 删除回合数据 / 独立窗口运行 AI
                rawOutput.RestartRequested = RestartAllProcesses;
                rawOutput.DeleteTurnDataRequested = DeleteTurnDataAndNotify;
                rawOutput.RunInSeparateWindowRequested = RunInSeparateWindow;
                context.Events.OnStarted(_ =>
                {
                    Volatile.Write(ref gameStarted, true);
                    return ValueTask.CompletedTask;
                });
                context.RunBackground(ConsumeProcessOutputAsync);
                
                if (context.IsPluginAvailable("LegendScenarioAnalyzer"))
                {
                    legendOutput = CreateLegendOutputBridge();
                    RegisterLegendDisplayIdAnalyzers(context);
                }

                if (context.IsPluginAvailable("RamenScenarioAnalyzer"))
                {
                    ramenOutput = CreateRamenOutputBridge();
                    RegisterRamenDisplayIdAnalyzers(context);
                }

                ValidateConfiguredPath(selectedAiName, selectedAiPath);

                if (selectedAiName is not null)
                    Trace.WriteLine($"{selectedAiName} Path: {selectedAiPath}");

                var manager = new ChildProcessManager();
                _childProcessManager = manager;

                if (selectedAiName is not null)
                    StartProcess(manager, selectedAiName, selectedAiPath!, selectedAiAppliesToLegend, selectedAiJsonMode);
            }
            catch (Exception initializationException)
            {
                try
                {
                    Dispose();
                }
                catch (Exception cleanupException)
                {
                    throw new AggregateException(
                        "AIRedirector 初始化与回滚均失败。",
                        initializationException,
                        cleanupException);
                }

                throw;
            }
        }

        /// 单选约束：从当前配置解析被选中的那一个 AI（名字 / 路径 / 输出标志），
        /// 其它 AI 一律不参与启动 / 重启等 binary 操作。
        void SyncSelectedAiFromConfig()
        {
            (selectedAiName, selectedAiPath, selectedAiAppliesToLegend, selectedAiJsonMode) =
                config.UAF ? ("UAF", config.UAF_Path, false, false)
                : config.Cook ? ("Cook", config.Cook_Path, false, false)
                : config.Mecha ? ("Mecha", config.Mecha_Path, false, false)
                : config.Legend ? ("Legend", config.Legend_Path, true, false)
                : config.Ramen ? ("Ramen", config.Ramen_Path, false, true)
                : (null, null, false, false);
        }

        void StartProcess(
            ChildProcessManager manager,
            string name,
            string path,
            bool applyToLegend = false,
            bool jsonMode = false)
        {
            var process = new Process
            {
                StartInfo = UmaAiProcessStartInfo.Create(path, jsonMode: jsonMode)
            };
            process.OutputDataReceived += (_, e) => HandleOutput(e.Data, applyToLegend, jsonMode);
            createdProcesses.Add(process);

            if (!process.Start())
                throw new InvalidOperationException($"无法启动 {name} AI 进程: {path}");

            startedProcesses.Add(process);

            manager.AddProcess(process);
            process.BeginOutputReadLine();
            LogToHost($"已启动 {name} AI: {path}");
        }

        /// 向宿主（UmamusumeResponseAnalyzer）日志面板写入一条提示。
        ///
        /// 宿主尚未准备好时（如离线 smoke、非 UI 上下文）静默跳过，绝不因日志失败而
        /// 中断 AI 进程的启动流程。
        internal static void LogToHost(string text)
        {
            try
            {
                TerminalUi.Log("[AIRedirector]", text);
            }
            catch (Exception)
            {
                // 宿主 UI 不可用：忽略提示，AI 进程照常拉起。
            }
        }

        /// <summary>面板按钮触发的「重启 AI 进程」：杀光现有子进程后按当前配置重建全部剧本。</summary>
        /// <remarks>在后台线程执行以避免 Kill/WaitForExit 阻塞 UI；与其它重启互斥（快速失败）。</remarks>
        void RestartAllProcesses()
        {
            if (!processLock.Wait(0))
            {
                rawOutput.PublishInfo("已有一次 AI 重启进行中，已忽略本次点击。");
                return;
            }

            _ = Task.Run(() =>
            {
                try
                {
                    var started = startedProcesses.ToArray();
                    foreach (var process in started)
                    {
                        if (!process.HasExited)
                            process.Kill();
                    }

                    foreach (var process in started)
                        process.WaitForExit(ProcessExitWaitMilliseconds);

                    startedProcesses.Clear();

                    var manager = _childProcessManager ?? new ChildProcessManager();
                    _childProcessManager = manager;
                    if (selectedAiName is not null)
                        StartProcess(manager, selectedAiName, selectedAiPath!, selectedAiAppliesToLegend, selectedAiJsonMode);

                    rawOutput.PublishInfo(selectedAiName is null ? "已重启 AI 进程。" : $"已重启 {selectedAiName} AI 进程。");
                    LogToHost(selectedAiName is null ? "已重启 AI 进程。" : $"已重启 {selectedAiName} AI 进程。");
                    Volatile.Read(ref ramenOutput)?.Notify(selectedAiName is null ? "已重启 AI 进程。" : $"已重启 {selectedAiName} AI 进程。");
                }
                catch (Exception ex)
                {
                    rawOutput.PublishError($"重启 AI 进程失败: {ex.Message}");
                    LogToHost($"重启 AI 进程失败: {ex.Message}");
                    Volatile.Read(ref ramenOutput)?.Notify($"重启 AI 进程失败: {ex.Message}", isError: true);
                }
                finally
                {
                    processLock.Release();
                }
            });
        }

        /// <summary>面板按钮触发的「在独立窗口运行AI」：终止当前受控 AI 进程（脱离插件控制），
        /// 再于新控制台窗口重新启动它——不带 --json，恢复 AI 正常的彩色 + 横幅显示模式。</summary>
        /// <remarks>与重启一致：后台线程执行避免阻塞 UI，与其它进程操作互斥（快速失败）。
        /// 新进程不加入 Job Object / 不受插件管理，插件退出时也不受影响。</remarks>
        void RunInSeparateWindow()
        {
            if (!processLock.Wait(0))
            {
                rawOutput.PublishInfo("已有一次 AI 进程操作进行中，已忽略本次点击。");
                return;
            }

            _ = Task.Run(() =>
            {
                try
                {
                    var name = selectedAiName;
                    var path = selectedAiPath;
                    if (name is null || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    {
                        rawOutput.PublishInfo("未启用 AI 或配置的程序路径不存在，无法在独立窗口运行。");
                        return;
                    }

                    // (1) 终止当前受插件控制的 AI 进程，先脱离插件控制。
                    var started = startedProcesses.ToArray();
                    foreach (var process in started)
                    {
                        if (!process.HasExited)
                            process.Kill();
                    }
                    foreach (var process in started)
                        process.WaitForExit(ProcessExitWaitMilliseconds);
                    startedProcesses.Clear();

                    // (2) 在新控制台窗口重新启动 AI（不带 --json），不再交给插件 / Job Object 管理。
                    var fullPath = Path.GetFullPath(path);
                    var workingDirectory = Path.GetDirectoryName(fullPath)
                        ?? throw new ArgumentException($"无法解析 UmaAI 程序所在目录: {path}", nameof(path));
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = fullPath,
                        WorkingDirectory = workingDirectory,
                        UseShellExecute = true,
                        CreateNoWindow = false,
                        WindowStyle = ProcessWindowStyle.Normal,
                    };
                    // 只为拿到启动结果：Dispose 只释放本地句柄，不影响新窗口里的进程继续运行。
                    using (Process.Start(startInfo))
                    {
                    }

                    var message = $"已终止 {name} AI 并在独立窗口重启（不带 --json，恢复正常显示）。";
                    rawOutput.PublishInfo(message);
                    LogToHost(message);
                }
                catch (Exception ex)
                {
                    rawOutput.PublishError($"在独立窗口运行 AI 失败: {ex.Message}");
                    LogToHost($"在独立窗口运行 AI 失败: {ex.Message}");
                }
                finally
                {
                    processLock.Release();
                }
            });
        }

        /// <summary>面板按钮触发的「删除回合数据」：只删除 .portable/PluginData/SendGameStatusPlugin/thisTurn.json 并提醒回主界面重新进入育成。</summary>
        void DeleteTurnDataAndNotify()
        {
            try
            {
                var turnDataFile = Path.Combine("PluginData", "SendGameStatusPlugin", "thisTurn.json");
                var removed = false;
                if (File.Exists(turnDataFile))
                {
                    File.Delete(turnDataFile);
                    removed = true;
                }

                var message = removed
                    ? "已删除回合数据文件 thisTurn.json。请从主页重新进入育成。"
                    : "未找到回合数据文件 thisTurn.json（请确认是否已开始育成）。";
                rawOutput.PublishInfo(message);
                LogToHost(message);
                Volatile.Read(ref ramenOutput)?.Notify(message);
            }
            catch (Exception ex)
            {
                rawOutput.PublishError($"删除回合数据失败: {ex.Message}");
                LogToHost($"删除回合数据失败: {ex.Message}");
                Volatile.Read(ref ramenOutput)?.Notify($"删除回合数据失败: {ex.Message}", isError: true);
            }
        }

        void HandleOutput(string? line, bool applyToLegend, bool jsonMode = false)
        {
            if (line is null)
                return;

            if (jsonMode)
            {
                // JSON 模式：info / error 处理成文字（info 亮黄、error 红）后进原始输出面板；
                // decision 等未处理的 JSON 保留原样输出（走下方兜底）。
                if (TryParseUmaAiInfo(line, out var infoEvent))
                {
                    processOutput.Writer.TryWrite(new($"info: {infoEvent}", applyToLegend, UmaAiMessageKind.Info));
                    return;
                }

                if (TryParseUmaAiError(line, out var errorMessage))
                {
                    processOutput.Writer.TryWrite(new($"error: {errorMessage}", applyToLegend, UmaAiMessageKind.Error));
                    return;
                }

                if (TryParseUmaAiDecision(line, out var decision))
                {
                    ApplyDecision(decision);
                    var text = FormatDecisionText(decision);
                    if (text.Count == 0)
                        return;

                    // 原始放入公共面板逐行展示。
                    foreach (var t in text)
                        processOutput.Writer.TryWrite(new(t, applyToLegend, null));
                    // 同时把原始 JSON 行交给各分析器桥（Ramen 桥需要 JSON 才能解析 target 与文本）。
                    processOutput.Writer.TryWrite(new(line, applyToLegend, null));
                    return;
                }
            }

            // 兜底（含 decision / 未识别 JSON / 非 JSON 模式 / 解析失败）：原样写入，
            // 在原始输出面板以默认色展示，用户始终能看到 AI 子进程的完整 stdout 流。
            processOutput.Writer.TryWrite(new(line, applyToLegend, null));
        }

        /// 尝试解析 UmaAI `--json` 模式输出的一行 `decision` JSON
        ///
        /// 2026-09 协议：以顶层 `type: "decision"` 作为消息类型标记（与 Rust 端
        /// `StdoutJsonSink::emit` 对齐）。解析失败（缺字段 / 非 JSON / type 不匹配）
        /// 一律返回 `false`，不抛异常。
        internal static bool TryParseUmaAiDecision(string line, out UmaAiDecision decision)
        {
            decision = default;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeProp)
                    || typeProp.ValueKind != JsonValueKind.String
                    || typeProp.GetString() != "decision")
                    return false;

                var actionIndex = root.TryGetProperty("action_index", out var ai) ? ai.GetInt32() : -1;
                var score = root.TryGetProperty("score", out var s) ? s.GetDouble() : 0.0;
                var scenario = root.TryGetProperty("scenario", out var sc) ? sc.GetString() ?? "" : "";
                var turn = root.TryGetProperty("turn", out var t) ? t.GetUInt32() : 0u;
                var decisionKind = root.TryGetProperty("decision_kind", out var dk)
                    ? dk.GetString() ?? ""
                    : "";

                var candidateScores = ParseDoubleArray(root, "candidate_scores");
                var candidateDescriptions = ParseStringArray(root, "candidate_descriptions");
                var candidateN = ParseUIntArray(root, "candidate_n");

                double? baseline = null, totalLuck = null, turnLuck = null;
                if (root.TryGetProperty("scenario_extra", out var extra)
                    && extra.ValueKind == JsonValueKind.Object)
                {
                    baseline = GetOptionalDouble(extra, "current_terminal_baseline");
                    totalLuck = GetOptionalDouble(extra, "total_luck_score");
                    turnLuck = GetOptionalDouble(extra, "last_turn_delta");
                }

                decision = new UmaAiDecision(
                    actionIndex, score, scenario, turn, decisionKind,
                    candidateScores, candidateDescriptions, candidateN,
                    baseline, totalLuck, turnLuck);
                return true;
            }
            catch
            {
                return false;
            }
        }

        static double? GetOptionalDouble(JsonElement obj, string name)
        {
            if (!obj.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.Number)
                return null;

            return prop.GetDouble();
        }

        static double[] ParseDoubleArray(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
                return [];

            var values = new List<double>(arr.GetArrayLength());
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Number)
                    values.Add(item.GetDouble());
            }

            return values.ToArray();
        }

        static string[] ParseStringArray(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
                return [];

            var values = new List<string>(arr.GetArrayLength());
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                    values.Add(item.GetString() ?? "");
            }

            return values.ToArray();
        }

        static uint[] ParseUIntArray(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
                return [];

            var values = new List<uint>(arr.GetArrayLength());
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Number)
                    values.Add(item.GetUInt32());
            }

            return values.ToArray();
        }

        /// 尝试解析 UmaAI `--json` 模式输出的一行 `info` JSON
        ///
        /// 已知 event 取值（与 Rust 端 `StdoutJsonSink::emit_info` 对齐）：
        /// - `connected`：AI 子进程已连接并就绪
        /// - `compute_start`：收到新回合 JSON、开始计算
        /// - `compute_next_step`：链式决策中间步骤
        /// - `new_game`：检测到切局 / 新一局
        ///
        /// 解析失败（非 JSON / 缺字段 / type 不为 `info` / event 为 null）一律返回 `false`。
        internal static bool TryParseUmaAiInfo(string line, [NotNullWhen(true)] out string? infoEvent)
        {
            infoEvent = null;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeProp)
                    || typeProp.ValueKind != JsonValueKind.String
                    || typeProp.GetString() != "info")
                    return false;

                infoEvent = root.TryGetProperty("event", out var e) && e.ValueKind != JsonValueKind.Null
                    ? e.GetString()
                    : null;
                return infoEvent is not null;
            }
            catch
            {
                return false;
            }
        }

        /// 尝试解析 UmaAI `--json` 模式输出的一行 `error` JSON
        ///
        /// 按 Rust 端约定：只带 `message` 字符串，错误事件不细分类型。
        /// 解析失败（非 JSON / 缺字段 / type 不为 `error` / message 为 null）一律返回 `false`。
        internal static bool TryParseUmaAiError(string line, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? errorMessage)
        {
            errorMessage = null;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeProp)
                    || typeProp.ValueKind != JsonValueKind.String
                    || typeProp.GetString() != "error")
                    return false;

                errorMessage = root.TryGetProperty("message", out var m) && m.ValueKind != JsonValueKind.Null
                    ? m.GetString()
                    : null;
                return errorMessage is not null;
            }
            catch
            {
                return false;
            }
        }

        /// 路由到对应剧本的 UI 渲染
        ///
        /// 当前仅在诊断输出记录决策（`FormatDecisionText` 已负责屏幕上的可读文本渲染）。
        /// 实际 UI 渲染（黑板 / 小黑板）待后续步骤按 `scenario` 路由到具体实现。
        void ApplyDecision(UmaAiDecision decision)
        {
            Trace.WriteLine(
                $"UmaAi 决策 [scenario={decision.Scenario} turn={decision.Turn} " +
                $"kind={decision.DecisionKind} action={decision.ActionIndex} score={decision.Score:F0}]");
            if (decision.Scenario != "ramen")
                return;

            // 方案(1)：把拉面 AI 决策经宿主日志面板展示（与「已启动 Ramen AI」同一条
            // TerminalUi.Log 通道），即使反射桥 / Extra / AI 面板不可用也能保证用户可见。
            foreach (var line in FormatDecisionText(decision))
                LogToHost($"拉面AI: {line}");
        }

        /// 把一条 decision 格式化为可读的多行文本（供公共面板逐行输出）。
        ///
        /// 内容顺序按需求：首选动作 → #2-#5 备选动作与说明 → 当前期望评分 → 运气分（本局/本回合）。
        /// 只有 1 个候选（如手写逻辑选择）时只返回首选动作行。
        internal static IReadOnlyList<string> FormatDecisionText(UmaAiDecision decision)
        {
            var primary = PrimaryDescription(decision);
            var lines = new List<string> { $"首选: {primary}" };

            // 候选总数取「描述数 / 评分数」较大者，用于判定是否单动作
            var total = Math.Max(decision.CandidateDescriptions.Length, decision.CandidateScores.Length);

            // 单动作（手写逻辑等）：只显示首选，跳过 #2-#5 与运气
            if (total <= 1)
                return lines;

            // #2-#5：按评分降序排列备选（排除首选下标），最多取 4 个
            var primaryIndex = decision.ActionIndex >= 0 && decision.ActionIndex < decision.CandidateDescriptions.Length
                ? decision.ActionIndex
                : -1;
            var alternatives = Enumerable.Range(0, decision.CandidateDescriptions.Length)
                .Where(i => i != primaryIndex)
                .Select(i => (Index: i, Desc: decision.CandidateDescriptions[i],
                    Score: i < decision.CandidateScores.Length ? decision.CandidateScores[i] : (double?)null))
                .OrderByDescending(x => x.Score ?? double.MinValue)
                .Take(4)
                .Select((x, k) => x.Score is { } sc
                    ? $"#{k + 2} {x.Desc}({sc:F0})"
                    : $"#{k + 2} {x.Desc}")
                .ToList();

            if (alternatives.Count > 0)
                lines.Add(string.Join(" | ", alternatives));

            lines.Add(
                $"期望评分 {FormatRound(decision.CurrentTerminalBaseline)} " +
                $"运气: 本局 {FormatSignedRound(decision.TotalLuckScore)} " +
                $"本回合 {FormatSignedRound(decision.LastTurnDelta)}");

            return lines;
        }

        /// 首选动作的可读描述：优先取 `action_index` 对应描述，缺省退到候选第 0 项，再无则显示下标。
        static string PrimaryDescription(UmaAiDecision decision)
        {
            if (decision.ActionIndex >= 0 && decision.ActionIndex < decision.CandidateDescriptions.Length)
                return decision.CandidateDescriptions[decision.ActionIndex];

            if (decision.CandidateDescriptions.Length >= 1)
                return decision.CandidateDescriptions[0];

            return $"动作#{decision.ActionIndex}";
        }

        /// 期望评分：保留整数（四舍五入），无值显示 `--`。
        static string FormatRound(double? value)
            => value is { } v ? Math.Round(v).ToString("F0") : "--";

        /// 运气分：带正负号（如 `+12` / `-3`），首回合无本回合运气时显示 `--`。
        static string FormatSignedRound(double? value)
            => value is { } v ? $"{v:+0;-0;0}" : "--";

        async ValueTask ConsumeProcessOutputAsync(CancellationToken cancellationToken)
        {
            try
            {
                await foreach (var output in processOutput.Reader.ReadAllAsync(cancellationToken))
                {
                    var published = output.Kind is { } kind
                        ? kind == UmaAiMessageKind.Info
                            ? rawOutput.PublishInfo(output.Line!)
                            : rawOutput.PublishError(output.Line!)
                        : rawOutput.PublishLine(output.Line);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!published || !Volatile.Read(ref gameStarted) || string.IsNullOrEmpty(output.Line))
                        continue;

                    // 各分析器桥自行过滤各自关心的行并决定是否刷新目标面板。
                    if (output.ApplyToLegend &&
                        Volatile.Read(ref legendOutput) is { } legend &&
                        legend.ProcessLine(output.Line))
                    {
                        _ = legend.ApplyTargetDisplay(cancellationToken);
                    }

                    if (Volatile.Read(ref ramenOutput) is { } ramen &&
                        ramen.ProcessLine(output.Line))
                    {
                        _ = ramen.ApplyTargetDisplay(cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            finally
            {
                while (processOutput.Reader.TryRead(out _))
                {
                }
            }
        }

        static void ValidateConfiguredPath(string? name, string? path)
        {
            if (name is null)
                return;

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new FileNotFoundException($"{name} AI 已启用，但配置的程序路径不存在: {path}", path);
        }
        
        [MethodImpl(MethodImplOptions.NoInlining)]
        static ILegendAiOutputBridge CreateLegendOutputBridge() => new LegendAiOutputBuffer();
        
        void RegisterLegendDisplayIdAnalyzers(IPluginContext context)
        {
            context.Analyzers.Register<SingleModeLegendCheckEventResponse>(
                AnalyzerKind.Response,
                [
                    EndpointPattern.Regex(
                        "/umamusume/single_mode_legend/(?:change_short_cut|check_event|cm_end|continue|exec_command|finish_claw_crane|gain_skills|legend_race_(?:continue|end|entry|out|start)|popularity_end|race_(?:end|entry|out))")
                ],
                invocation => SetLegendDisplayId(invocation.Payload.data?.chara_info),
                priority: 0);
            context.Analyzers.Register<SingleModeLegendLoadResponse>(
                AnalyzerKind.Response,
                [EndpointPattern.Exact("/umamusume/single_mode_legend/load")],
                invocation => SetLegendDisplayId(
                    invocation.Payload.data?.single_mode_load_common?.chara_info),
                priority: 0);
        }

        ValueTask SetLegendDisplayId(SingleModeChara? chara)
        {
            if (chara is not null)
            {
                Volatile.Read(ref legendOutput)?.SetTarget(
                    chara.single_mode_chara_id,
                    chara.turn);
            }
            return ValueTask.CompletedTask;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static IRamenAiOutputBridge CreateRamenOutputBridge() => new RamenAiOutputBuffer();

        void RegisterRamenDisplayIdAnalyzers(IPluginContext context)
        {
            context.Analyzers.Register<SingleModeRamenExecCommandResponse>(
                AnalyzerKind.Response,
                [
                    EndpointPattern.Regex(
                        "/umamusume/single_mode_ramen/(?:change_short_cut|check_event|check_point|continue|exec_command|finish_claw_crane|gain_skills|race_end|race_entry|race_out|ramen_live|uraf_effect_apply|select_region)")
                ],
                invocation => SetRamenDisplayId(invocation.Payload.data?.chara_info),
                priority: 0);
            context.Analyzers.Register<SingleModeRamenLoadResponse>(
                AnalyzerKind.Response,
                [EndpointPattern.Exact("/umamusume/single_mode_ramen/load")],
                invocation => SetRamenDisplayId(
                    invocation.Payload.data?.single_mode_load_common?.chara_info),
                priority: 0);
        }

        ValueTask SetRamenDisplayId(SingleModeChara? chara)
        {
            if (chara is not null)
            {
                Volatile.Read(ref ramenOutput)?.SetTarget(
                    chara.single_mode_chara_id,
                    chara.turn);
            }
            return ValueTask.CompletedTask;
        }

        public async Task ConfigPromptAsync(
            IApplication application,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(application);
            cancellationToken.ThrowIfCancellationRequested();
            if (application.TopRunnable is null &&
                Environment.CurrentManagedThreadId != application.MainThreadId)
                throw new InvalidOperationException(
                    "AIRedirector 无法从非 UI thread 启动配置：Terminal.Gui 当前没有正在运行的 session。");

            var draft = AIRedirectorConfig.Load(ConfigPath);
            AIRedirectorConfig saved;
            if (Environment.CurrentManagedThreadId == application.MainThreadId)
            {
                saved = RunConfigDialog(application, draft, cancellationToken);
            }
            else
            {
                var completion = new TaskCompletionSource<AIRedirectorConfig>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                application.Invoke(() =>
                {
                    try
                    {
                        completion.SetResult(RunConfigDialog(application, draft, cancellationToken));
                    }
                    catch (Exception ex)
                    {
                        completion.SetException(ex);
                    }
                });
                saved = await completion.Task;
            }

            cancellationToken.ThrowIfCancellationRequested();
            saved.Save(ConfigPath);
            config = saved;
            // 单选：保存后重新解析当前选中的 AI，使后续启动 / 重启围绕新选择进行。
            SyncSelectedAiFromConfig();
        }

        static AIRedirectorConfig RunConfigDialog(
            IApplication application,
            AIRedirectorConfig draft,
            CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var action = RunConfigMenu(application, draft, cancellationToken);
                if (action == ConfigAction.Save)
                    return draft;
                if (action == ConfigAction.Cancel)
                    throw new OperationCanceledException("AIRedirector 配置已取消。", cancellationToken);

                var (scenario, path) = action switch
                {
                    ConfigAction.EditUaf => ("UAF", draft.UAF_Path),
                    ConfigAction.EditCook => ("Cook", draft.Cook_Path),
                    ConfigAction.EditMecha => ("Mecha", draft.Mecha_Path),
                    ConfigAction.EditLegend => ("Legend", draft.Legend_Path),
                    ConfigAction.EditRamen => ("Ramen", draft.Ramen_Path),
                    _ => throw new UnreachableException(),
                };
                var edited = RunFilePicker(
                    application,
                    scenario,
                    path,
                    cancellationToken);
                if (edited is null)
                    continue;

                switch (action)
                {
                    case ConfigAction.EditUaf:
                        draft.UAF_Path = edited;
                        break;
                    case ConfigAction.EditCook:
                        draft.Cook_Path = edited;
                        break;
                    case ConfigAction.EditMecha:
                        draft.Mecha_Path = edited;
                        break;
                    case ConfigAction.EditLegend:
                        draft.Legend_Path = edited;
                        break;
                    case ConfigAction.EditRamen:
                        draft.Ramen_Path = edited;
                        break;
                }
            }
        }

        static ConfigAction RunConfigMenu(
            IApplication application,
            AIRedirectorConfig draft,
            CancellationToken cancellationToken)
        {
            using var dialog = new Dialog
            {
                Title = "AIRedirector 配置",
                Width = 88,
                Height = 14,
            };
            var action = ConfigAction.Cancel;
            var items = new List<MenuItem>();
            // 场景开关为单选：勾选某一项时其它项自动取消
            var scenarioBoxes = new List<CheckBox>();

            void AddScenario(
                string name,
                bool enabled,
                string path,
                Action<bool> setEnabled,
                ConfigAction editAction)
            {
                var checkBox = new CheckBox
                {
                    Text = $"启用 {name}",
                    Value = enabled ? CheckState.Checked : CheckState.UnChecked,
                    RadioStyle = true,
                    CanFocus = false,
                };
                var pathItem = new MenuItem
                {
                    Title = $"{name} 路径",
                    HelpText = path,
                    Visible = enabled,
                    Action = () =>
                    {
                        action = editAction;
                        application.RequestStop(dialog);
                    },
                };
                checkBox.ValueChanged += (_, _) =>
                {
                    var isEnabled = checkBox.Value == CheckState.Checked;
                    // 单选互斥：勾选本项时取消其它已勾选项（其它项的 ValueChanged 幂等安全）
                    if (isEnabled)
                    {
                        foreach (var other in scenarioBoxes)
                        {
                            if (other != checkBox && other.Value == CheckState.Checked)
                                other.Value = CheckState.UnChecked;
                        }
                    }
                    setEnabled(isEnabled);
                    pathItem.Visible = isEnabled;
                };
                scenarioBoxes.Add(checkBox);
                items.Add(new MenuItem { CommandView = checkBox });
                items.Add(pathItem);
            }

            AddScenario("UAF", draft.UAF, draft.UAF_Path, value => draft.UAF = value, ConfigAction.EditUaf);
            AddScenario("Cook", draft.Cook, draft.Cook_Path, value => draft.Cook = value, ConfigAction.EditCook);
            AddScenario("Mecha", draft.Mecha, draft.Mecha_Path, value => draft.Mecha = value, ConfigAction.EditMecha);
            AddScenario("Legend", draft.Legend, draft.Legend_Path, value => draft.Legend = value, ConfigAction.EditLegend);
            AddScenario("Ramen", draft.Ramen, draft.Ramen_Path, value => draft.Ramen = value, ConfigAction.EditRamen);
            items.Add(new MenuItem("保存", action: () =>
            {
                action = ConfigAction.Save;
                application.RequestStop(dialog);
            }));
            items.Add(new MenuItem("取消", action: () => application.RequestStop(dialog)));

            var menu = new Menu(items)
            {
                Height = Dim.Fill(),
            };
            dialog.Add(menu);
            items[0].SetFocus();
            using (cancellationToken.Register(
                       () => application.Invoke(() => application.RequestStop(dialog))))
                application.Run(dialog);
            cancellationToken.ThrowIfCancellationRequested();
            return action;
        }

        static string? RunFilePicker(
            IApplication application,
            string scenario,
            string path,
            CancellationToken cancellationToken)
        {
            using var picker = new OpenDialog
            {
                Title = $"选择 {scenario} UmaAI.exe",
                OpenMode = OpenMode.File,
                MustExist = true,
                AllowsMultipleSelection = false,
                AllowedTypes = [new AllowedType("UmaAI.exe", [".exe"])],
            };
            if (!string.IsNullOrWhiteSpace(path))
                picker.Path = path;

            using (cancellationToken.Register(
                       () => application.Invoke(() => application.RequestStop(picker))))
                application.Run(picker);
            cancellationToken.ThrowIfCancellationRequested();
            return picker.Canceled ? null : picker.Path;
        }

        enum ConfigAction
        {
            Cancel,
            Save,
            EditUaf,
            EditCook,
            EditMecha,
            EditLegend,
            EditRamen,
        }

        public void Dispose()
        {
            _ = processOutput.Writer.TryComplete();
            var legendBridge = Interlocked.Exchange(ref legendOutput, null);
            var ramenBridge = Interlocked.Exchange(ref ramenOutput, null);
            var started = startedProcesses.ToArray();
            startedProcesses.Clear();
            var created = createdProcesses.ToArray();
            createdProcesses.Clear();
            var manager = _childProcessManager;
            _childProcessManager = null;
            var errors = new List<Exception>();
            bool Capture(Action action)
            {
                try
                {
                    action();
                    return true;
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                    return false;
                }
            }

            var managerDisposed = manager is null;
            try
            {
                foreach (var process in started)
                    Capture(() =>
                    {
                        if (!process.HasExited)
                            process.Kill();
                    });

                if (manager is not null)
                    managerDisposed = Capture(manager.Dispose);

                foreach (var process in started)
                    Capture(() =>
                    {
                        if (!process.WaitForExit(ProcessExitWaitMilliseconds))
                        {
                            throw new TimeoutException(
                                $"UmaAI 子进程 '{process.StartInfo.FileName}' 在终止请求后 " +
                                $"{ProcessExitWaitMilliseconds} ms 内未退出；未执行无界 WaitForExit，" +
                                "stdout handler 可能尚未排空。");
                        }

                        process.WaitForExit();
                    });
            }
            finally
            {
                foreach (var process in created)
                    Capture(process.Dispose);
                if (!managerDisposed && manager is not null)
                    Capture(manager.Dispose);
                if (legendBridge is not null)
                    Capture(legendBridge.Dispose);
                if (ramenBridge is not null)
                    Capture(ramenBridge.Dispose);
                Capture(rawOutput.Dispose);
            }

            Exception? failure = errors.Count == 0
                ? null
                : new AggregateException("AIRedirector 清理失败。", errors);
            if (failure is not null)
                throw failure;
        }

        readonly record struct ProcessOutput(string? Line, bool ApplyToLegend, UmaAiMessageKind? Kind);
    }

    internal interface ILegendAiOutputBridge : IDisposable
    {
        bool ProcessLine(string? rawLine);
        void SetTarget(int singleModeCharaId, int turn);
        bool ApplyTargetDisplay(CancellationToken cancellationToken);
    }

    internal interface IRamenAiOutputBridge : IDisposable
    {
        bool ProcessLine(string? rawLine);
        void SetTarget(int singleModeCharaId, int turn);
        bool ApplyTargetDisplay(CancellationToken cancellationToken);

        /// <summary>在 AI 面板打印一行用户操作提示（如「已重启 AI 进程」）；isError 用红色渲染。</summary>
        void Notify(string message, bool isError = false);
    }

    /// AIRedirector 解析后的 UmaAI 决策（详见集成文档 §3.2.6 / §4.3）
    ///
    /// 与 Rust 端 `StdoutJsonSink` 输出 schema 对齐（2026-09 协议）：
    /// - 顶层 `type: "decision"` 作为消息类型标记
    /// - `action_index` / `score` / `scenario` / `turn` / `decision_kind`
    /// - `candidate_scores` / `candidate_descriptions` / `candidate_n`：所有候选
    ///   的评分 / 可读描述 / 样本数（**完整保留**，用于屏幕输出 #2-#5 备选）
    /// - `scenario_extra.current_terminal_baseline`：当前期望评分
    /// - `scenario_extra.total_luck_score` / `last_turn_delta`：本局 / 本回合运气分
    internal readonly record struct UmaAiDecision(
        int ActionIndex,
        double Score,
        string Scenario,
        uint Turn,
        string DecisionKind,
        double[] CandidateScores,
        string[] CandidateDescriptions,
        uint[] CandidateN,
        double? CurrentTerminalBaseline,
        double? TotalLuckScore,
        double? LastTurnDelta);
}

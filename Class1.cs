using System;
using System.Diagnostics;
using LegendScenarioAnalyzer;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using UmamusumeResponseAnalyzer.Plugin;

[assembly: SharedContextWith("LegendScenarioAnalyzer")]

namespace AIRedirector
{
    public class AIRedirector : IPlugin
    {
        const int ProcessExitWaitMilliseconds = 5000;

        public string Name => "AIRedirector";
        public string Author => "离披";
        public string[] Targets => [];
        public string DataDirectory => Path.Combine("PluginData", Name);

        readonly List<Process> createdProcesses = [];
        readonly List<Process> startedProcesses = [];
        ChildProcessManager? _childProcessManager;
        AIRedirectorConfig config = new();
        IDisposable? startedSubscription;
        readonly object processOutputGate = new();
        readonly LegendAiOutputBuffer legendOutput = new();
        readonly UmaAiRawOutputWorkspace rawOutput = new();
        readonly Queue<(long Generation, string Line)> pendingLegendOutput = [];
        TaskCompletionSource<object?>? outputPublishingDrained;
        TaskCompletionSource<Exception?>? cleanupInProgress;
        long outputGeneration;
        int publishingOutput;
        bool legendWorkerScheduled;
        bool acceptingProcessOutput;
        bool gameStarted;

        string ConfigPath => Path.Combine(DataDirectory, "settings.json");

        public void Initialize(IPluginContext context)
        {
            lock (processOutputGate)
            {
                acceptingProcessOutput = true;
                outputGeneration++;
            }

            try
            {
                Directory.CreateDirectory(DataDirectory);
                config = AIRedirectorConfig.Load(ConfigPath);
                var subscription = context.Events.OnStarted(_ =>
                {
                    Volatile.Write(ref gameStarted, true);
                    return ValueTask.CompletedTask;
                });
                lock (processOutputGate)
                    startedSubscription = subscription;

                var sendGameStatusDataDirectory = Path.Combine("PluginData", "SendGameStatusPlugin");
                if (Directory.Exists(sendGameStatusDataDirectory))
                {
                    foreach (var i in Directory.EnumerateFiles(sendGameStatusDataDirectory, "*.json", SearchOption.AllDirectories))
                    {
                        if (Path.GetFileName(i) == "thisTurn.json")
                        {
                            File.WriteAllText(i, "{}");
                        }
                    }
                }

                ValidateConfiguredPath("UAF", config.UAF, config.UAF_Path);
                ValidateConfiguredPath("Cook", config.Cook, config.Cook_Path);
                ValidateConfiguredPath("Mecha", config.Mecha, config.Mecha_Path);
                ValidateConfiguredPath("Legend", config.Legend, config.Legend_Path);

                if (config.UAF)
                    Trace.WriteLine($"UAF Path: {config.UAF_Path}");
                if (config.Cook)
                    Trace.WriteLine($"Cook Path: {config.Cook_Path}");
                if (config.Mecha)
                    Trace.WriteLine($"Mecha Path: {config.Mecha_Path}");
                if (config.Legend)
                    Trace.WriteLine($"Legend Path: {config.Legend_Path}");

                var manager = new ChildProcessManager();
                lock (processOutputGate)
                    _childProcessManager = manager;

                if (config.UAF)
                    StartProcess(manager, "UAF", config.UAF_Path);
                if (config.Cook)
                    StartProcess(manager, "Cook", config.Cook_Path);
                if (config.Mecha)
                    StartProcess(manager, "Mecha", config.Mecha_Path);
                if (config.Legend)
                    StartProcess(manager, "Legend", config.Legend_Path, applyToLegend: true);
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

        void StartProcess(
            ChildProcessManager manager,
            string name,
            string path,
            bool applyToLegend = false)
        {
            var process = new Process
            {
                StartInfo = UmaAiProcessStartInfo.Create(path)
            };
            process.OutputDataReceived += (_, e) => HandleOutput(e.Data, applyToLegend);
            lock (processOutputGate)
                createdProcesses.Add(process);

            if (!process.Start())
                throw new InvalidOperationException($"无法启动 {name} AI 进程: {path}");

            lock (processOutputGate)
                startedProcesses.Add(process);

            manager.AddProcess(process);
            process.BeginOutputReadLine();
        }

        void HandleOutput(string? line, bool applyToLegend = false)
        {
            long callbackGeneration;
            lock (processOutputGate)
            {
                if (!acceptingProcessOutput)
                    return;

                callbackGeneration = outputGeneration;
            }

            var snapshot = rawOutput.AppendLine(line);
            var updateLegend = applyToLegend &&
                Volatile.Read(ref gameStarted) &&
                !string.IsNullOrEmpty(line);
            if (snapshot is null)
                return;

            rawOutput.WaitToPublish();
            try
            {
                if (!TryBeginOutputPublish(callbackGeneration))
                    return;

                var rawPublished = false;
                try
                {
                    rawPublished = rawOutput.Publish(snapshot.Value);
                }
                finally
                {
                    EndOutputPublish();
                }

                if (rawPublished && updateLegend)
                    EnqueueLegendOutput(callbackGeneration, line!);
            }
            finally
            {
                rawOutput.ReleasePublish();
            }
        }

        void EnqueueLegendOutput(long callbackGeneration, string line)
        {
            var scheduleWorker = false;
            lock (processOutputGate)
            {
                if (!acceptingProcessOutput || outputGeneration != callbackGeneration)
                    return;

                pendingLegendOutput.Enqueue((callbackGeneration, line));
                if (!legendWorkerScheduled)
                {
                    legendWorkerScheduled = true;
                    scheduleWorker = true;
                }
            }

            if (scheduleWorker)
                ScheduleLegendOutputWorker();
        }

        void ScheduleLegendOutputWorker()
        {
            if (ThreadPool.QueueUserWorkItem(
                    static state => ((AIRedirector)state!).DrainLegendOutput(),
                    this))
            {
                return;
            }

            lock (processOutputGate)
            {
                legendWorkerScheduled = false;
                pendingLegendOutput.Clear();
            }
            throw new InvalidOperationException("无法调度 AIRedirector Legend stdout FIFO worker。");
        }

        void DrainLegendOutput()
        {
            try
            {
                while (true)
                {
                    (long Generation, string Line) item;
                    lock (processOutputGate)
                    {
                        if (!acceptingProcessOutput)
                        {
                            pendingLegendOutput.Clear();
                            return;
                        }

                        if (!pendingLegendOutput.TryDequeue(out item))
                            return;
                        if (item.Generation != outputGeneration)
                            continue;
                    }

                    if (legendOutput.ProcessLine(item.Line) &&
                        IsOutputGenerationActive(item.Generation))
                    {
                        global::LegendScenarioAnalyzer.LegendScenarioAnalyzer.WithCurrentDisplayCommit(
                            () => TryBeginOutputPublish(item.Generation),
                            EndOutputPublish,
                            legendOutput.ApplyCurrentDisplay);
                    }
                }
            }
            finally
            {
                var scheduleWorker = false;
                lock (processOutputGate)
                {
                    legendWorkerScheduled = false;
                    if (!acceptingProcessOutput)
                    {
                        pendingLegendOutput.Clear();
                    }
                    else if (pendingLegendOutput.Count != 0)
                    {
                        legendWorkerScheduled = true;
                        scheduleWorker = true;
                    }
                }

                if (scheduleWorker)
                    ScheduleLegendOutputWorker();
            }
        }

        bool IsOutputGenerationActive(long callbackGeneration)
        {
            lock (processOutputGate)
                return acceptingProcessOutput && outputGeneration == callbackGeneration;
        }

        static void ValidateConfiguredPath(string name, bool enabled, string path)
        {
            if (!enabled)
                return;

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new FileNotFoundException($"{name} AI 已启用，但配置的程序路径不存在: {path}", path);
        }

        bool TryBeginOutputPublish(long callbackGeneration)
        {
            lock (processOutputGate)
            {
                if (!acceptingProcessOutput || outputGeneration != callbackGeneration)
                    return false;

                if (publishingOutput++ == 0)
                    outputPublishingDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
                return true;
            }
        }

        void EndOutputPublish()
        {
            TaskCompletionSource<object?>? drained = null;
            lock (processOutputGate)
            {
                if (--publishingOutput == 0)
                {
                    drained = outputPublishingDrained;
                    outputPublishingDrained = null;
                }
            }

            drained?.TrySetResult(null);
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
                    _ => throw new UnreachableException(),
                };
                var edited = RunPathDialog(
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
                    CanFocus = false,
                };
                var pathItem = new MenuItem
                {
                    Title = $"{name} 路径",
                    HelpText = path,
                    Enabled = enabled,
                    Action = () =>
                    {
                        action = editAction;
                        application.RequestStop(dialog);
                    },
                };
                checkBox.ValueChanged += (_, _) =>
                {
                    var isEnabled = checkBox.Value == CheckState.Checked;
                    setEnabled(isEnabled);
                    pathItem.Enabled = isEnabled;
                };
                items.Add(new MenuItem { CommandView = checkBox });
                items.Add(pathItem);
            }

            AddScenario("UAF", draft.UAF, draft.UAF_Path, value => draft.UAF = value, ConfigAction.EditUaf);
            AddScenario("Cook", draft.Cook, draft.Cook_Path, value => draft.Cook = value, ConfigAction.EditCook);
            AddScenario("Mecha", draft.Mecha, draft.Mecha_Path, value => draft.Mecha = value, ConfigAction.EditMecha);
            AddScenario("Legend", draft.Legend, draft.Legend_Path, value => draft.Legend = value, ConfigAction.EditLegend);
            items.Add(new MenuItem("保存", action: () =>
            {
                action = ConfigAction.Save;
                application.RequestStop(dialog);
            }));
            items.Add(new MenuItem("取消", action: () => application.RequestStop(dialog)));

            var menu = new Menu(items)
            {
                Width = Dim.Fill(),
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

        static string? RunPathDialog(
            IApplication application,
            string scenario,
            string path,
            CancellationToken cancellationToken)
        {
            using var dialog = new Dialog
            {
                Title = $"{scenario} 路径",
                Width = 88,
                Height = 7,
            };
            var label = new Label { Text = "UmaAI.exe 路径" };
            var field = new TextField
            {
                Y = 1,
                Width = Dim.Fill(),
                Text = path,
            };
            dialog.Add(label, field);

            var accepted = false;
            var save = new Button { Text = "确定", IsDefault = true };
            save.Accepting += (_, e) =>
            {
                accepted = true;
                application.RequestStop(dialog);
                e.Handled = true;
            };
            var cancel = new Button { Text = "取消" };
            cancel.Accepting += (_, e) =>
            {
                application.RequestStop(dialog);
                e.Handled = true;
            };
            dialog.AddButton(cancel);
            dialog.AddButton(save);
            field.SetFocus();
            using (cancellationToken.Register(
                       () => application.Invoke(() => application.RequestStop(dialog))))
                application.Run(dialog);
            cancellationToken.ThrowIfCancellationRequested();
            return accepted ? field.Text : null;
        }

        enum ConfigAction
        {
            Cancel,
            Save,
            EditUaf,
            EditCook,
            EditMecha,
            EditLegend,
        }

        public void Dispose()
        {
            Task<Exception?>? existingCleanup = null;
            TaskCompletionSource<Exception?>? ownedCleanup = null;
            Task? publishWait = null;
            Process[] started = [];
            Process[] created = [];
            IDisposable? subscription = null;
            ChildProcessManager? manager = null;
            lock (processOutputGate)
            {
                if (cleanupInProgress is { } currentCleanup)
                {
                    existingCleanup = currentCleanup.Task;
                }
                else
                {
                    acceptingProcessOutput = false;
                    outputGeneration++;
                    pendingLegendOutput.Clear();
                    ownedCleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    cleanupInProgress = ownedCleanup;

                    started = [.. startedProcesses];
                    startedProcesses.Clear();
                    created = [.. createdProcesses];
                    createdProcesses.Clear();
                    subscription = startedSubscription;
                    startedSubscription = null;
                    manager = _childProcessManager;
                    _childProcessManager = null;
                    publishWait = outputPublishingDrained?.Task;
                }
            }

            if (existingCleanup is not null)
            {
                var priorFailure = existingCleanup.GetAwaiter().GetResult();
                if (priorFailure is not null)
                    throw priorFailure;
                return;
            }

            publishWait?.GetAwaiter().GetResult();
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
                if (subscription is not null)
                    Capture(subscription.Dispose);

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
                Capture(rawOutput.Dispose);
            }

            Exception? failure = errors.Count == 0
                ? null
                : new AggregateException("AIRedirector 清理失败。", errors);
            ownedCleanup!.TrySetResult(failure);

            lock (processOutputGate)
            {
                if (ReferenceEquals(cleanupInProgress, ownedCleanup))
                    cleanupInProgress = null;
            }

            if (failure is not null)
                throw failure;
        }
    }
}

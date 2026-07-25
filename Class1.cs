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
        public string Name => "AIRedirector";
        public string Author => "离披";
        public string[] Targets => [];
        public string DataDirectory => Path.Combine("PluginData", Name);

        readonly Dictionary<ScenarioType, Process> processes = [];
        ChildProcessManager? _childProcessManager;
        AIRedirectorConfig config = new();
        IDisposable? startedSubscription;
        readonly LegendAiOutputBuffer legendOutput = new();
        readonly UmaAiRawOutputWorkspace rawOutput = new();
        bool gameStarted;

        string ConfigPath => Path.Combine(DataDirectory, "settings.json");

        public void Initialize(IPluginContext context)
        {
            Directory.CreateDirectory(DataDirectory);
            rawOutput.Initialize(context.LiveDisplay);
            config = AIRedirectorConfig.Load(ConfigPath);
            startedSubscription = context.Events.OnStarted(_ =>
            {
                gameStarted = true;
                return ValueTask.CompletedTask;
            });

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
            {
                var uaf = new Process
                {
                    StartInfo = UmaAiProcessStartInfo.Create(config.UAF_Path)
                };
                uaf.OutputDataReceived += (sender, e) =>
                {
                    rawOutput.AppendLine(e.Data);
                };
                processes.Add(ScenarioType.UAF, uaf);
                Trace.WriteLine($"UAF Path: {config.UAF_Path}");
            }
            if (config.Cook)
            {
                var cook = new Process
                {
                    StartInfo = UmaAiProcessStartInfo.Create(config.Cook_Path)
                };
                cook.OutputDataReceived += (sender, e) =>
                {
                    rawOutput.AppendLine(e.Data);
                };
                processes.Add(ScenarioType.Cook, cook);
                Trace.WriteLine($"Cook Path: {config.Cook_Path}");
            }
            if (config.Mecha)
            {
                var mecha = new Process
                {
                    StartInfo = UmaAiProcessStartInfo.Create(config.Mecha_Path)
                };
                mecha.OutputDataReceived += (sender, e) =>
                {
                    rawOutput.AppendLine(e.Data);
                };
                processes.Add(ScenarioType.Mecha, mecha);
                Trace.WriteLine($"Mecha Path: {config.Mecha_Path}");
            }
            if (config.Legend)
            {
                var legend = new Process
                {
                    StartInfo = UmaAiProcessStartInfo.Create(config.Legend_Path)
                };
                legend.OutputDataReceived += (sender, e) =>
                {
                    rawOutput.AppendLine(e.Data);
                    if (gameStarted && !string.IsNullOrEmpty(e.Data))
                    {
                        if (legendOutput.ProcessLine(e.Data))
                            legendOutput.ApplyCurrentDisplay();
                    }
                };
                processes.Add(ScenarioType.Legend, legend);
                Trace.WriteLine($"Legend Path: {config.Legend_Path}");
            }

            _childProcessManager = new ChildProcessManager();
            foreach (var (_, process) in processes)
            {
                process.Start();
                _childProcessManager.AddProcess(process);
                process.BeginOutputReadLine();
            }
        }

        static void ValidateConfiguredPath(string name, bool enabled, string path)
        {
            if (!enabled)
                return;

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new FileNotFoundException($"{name} AI 已启用，但配置的程序路径不存在: {path}", path);
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

            var saved = await completion.Task;
            cancellationToken.ThrowIfCancellationRequested();
            saved.Save(ConfigPath);
            config = saved;
        }

        static AIRedirectorConfig RunConfigDialog(
            IApplication application,
            AIRedirectorConfig draft,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var dialog = new Dialog
            {
                Title = "AIRedirector 配置",
                Width = 88,
                Height = 16,
            };
            var scenarios = new[]
            {
                CreateScenarioRow("UAF", draft.UAF, draft.UAF_Path, 1),
                CreateScenarioRow("Cook", draft.Cook, draft.Cook_Path, 3),
                CreateScenarioRow("Mecha", draft.Mecha, draft.Mecha_Path, 5),
                CreateScenarioRow("Legend", draft.Legend, draft.Legend_Path, 7),
            };
            foreach (var scenario in scenarios)
                dialog.Add(scenario.Enabled, scenario.Path);

            var accepted = false;
            var save = new Button { Text = "保存", IsDefault = true };
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
            using (cancellationToken.Register(
                       () => application.Invoke(() => application.RequestStop(dialog))))
                application.Run(dialog);
            cancellationToken.ThrowIfCancellationRequested();
            if (!accepted)
                throw new OperationCanceledException("AIRedirector 配置已取消。", cancellationToken);

            return new()
            {
                UAF = scenarios[0].IsEnabled,
                UAF_Path = scenarios[0].Path.Text,
                Cook = scenarios[1].IsEnabled,
                Cook_Path = scenarios[1].Path.Text,
                Mecha = scenarios[2].IsEnabled,
                Mecha_Path = scenarios[2].Path.Text,
                Legend = scenarios[3].IsEnabled,
                Legend_Path = scenarios[3].Path.Text,
            };
        }

        static ScenarioRow CreateScenarioRow(string name, bool enabled, string path, int y)
        {
            var enabledView = new CheckBox
            {
                X = 1,
                Y = y,
                Text = $"启用 {name}",
                Value = enabled ? CheckState.Checked : CheckState.UnChecked,
            };
            var pathView = new TextField
            {
                X = 18,
                Y = y,
                Width = Dim.Fill(1),
                Text = path,
                Enabled = enabled,
            };
            pathView.MouseHighlightStates |= MouseState.In;
            enabledView.ValueChanged += (_, _) => pathView.Enabled = enabledView.Value == CheckState.Checked;
            return new(enabledView, pathView);
        }

        sealed record ScenarioRow(CheckBox Enabled, TextField Path)
        {
            public bool IsEnabled => Enabled.Value == CheckState.Checked;
        }

        public void Dispose()
        {
            startedSubscription?.Dispose();
            startedSubscription = null;
            rawOutput.Dispose();
            foreach (var (_, process) in processes)
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
                process.Dispose();
            }
            processes.Clear();
            _childProcessManager?.Dispose();
            _childProcessManager = null;
        }
    }
}

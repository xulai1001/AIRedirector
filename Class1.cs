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

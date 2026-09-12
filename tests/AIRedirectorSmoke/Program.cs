using System.Collections.Concurrent;
using System.Drawing;
using System.Text;
using AIRedirector;
using Gallop;
using Gallop.Endpoints;
using LegendScenarioAnalyzer;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Terminal.Gui.Text;
using Terminal.Gui.Time;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using UmamusumeResponseAnalyzer.TerminalGui;
using UmamusumeResponseAnalyzer.Plugin;
using LegendPlugin = LegendScenarioAnalyzer.LegendScenarioAnalyzer;
using TAttribute = Terminal.Gui.Drawing.Attribute;

AssertProjectMetadata();
TestUmaAiStdoutEncodingUsesUtf8CodePage65001();
TestUmaAiProcessStartInfoUsesExecutableDirectory();
await TestConfigPromptRunsOnOwnerWithoutOuterRunnable();
await TestConfigMenuUsesNativeControlsAndSavesDraft();
await TestConfigMenuCancellationLeavesFileUnchanged();
using var ui = new WorkspaceSmokeSession();
TestRawUmaAiOutputWorkspaceRendersStdoutWithoutTerminalControlSequences(ui);
TestRawUmaAiOutputWorkspaceRendersFullBufferAndDisposesPanel(ui);
await TestLegendTrainingOutputPatchesCurrentWorkspaceAndClearsStaleOutput(ui);
await TestLegendNonTrainingRecommendationsRenderInImportant(ui);
await TestLegendBuffSelectionOutputUsesObtainableBuffContext(ui);
TestTryParseUmaAiDecisionAcceptsRamenJsonLine();
TestTryParseUmaAiDecisionAcceptsOnsenJsonLine();
TestTryParseUmaAiDecisionRejectsNonJsonLine();
TestTryParseUmaAiDecisionRejectsJsonWithoutTypeDecision();
TestTryParseUmaAiInfoAcceptsAllFourEvents();
TestTryParseUmaAiErrorAcceptsChineseMessage();
TestTryParseUmaAiErrorAcceptsEnglishMessage();
TestTryParseUmaAiDecisionRejectsInfoLine();
TestTryParseUmaAiInfoRejectsErrorLine();

Console.WriteLine("PASS AIRedirector smoke");

static void AssertProjectMetadata()
{
    var projectPath = Path.Combine(FindRepositoryRoot(), "AIRedirector.csproj");
    var project = File.ReadAllText(projectPath);
    if (!project.Contains("<PluginDependencies>LegendScenarioAnalyzer</PluginDependencies>", StringComparison.Ordinal))
        throw new InvalidOperationException("AIRedirector manifest metadata must depend on LegendScenarioAnalyzer.");
}

static void TestUmaAiStdoutEncodingUsesUtf8CodePage65001()
{
    const string expected = "找不到配置文件，已使用默认配置";
    var startInfo = UmaAiProcessStartInfo.Create(Path.Combine(Path.GetTempPath(), "UmaAI", "UmaAI.exe"));
    var encoding = startInfo.StandardOutputEncoding
        ?? throw new InvalidOperationException("UmaAI stdout encoding was not configured.");
    var bytes = Encoding.UTF8.GetBytes(expected);
    var decoded = encoding.GetString(bytes);

    RequireEqual(expected, decoded, "UmaAI stdout UTF-8 decoding");
    if (decoded.Contains("鎵句笉鍒", StringComparison.Ordinal))
        throw new InvalidOperationException("UmaAI stdout must not decode UTF-8 Chinese as GBK mojibake.");
}

static void TestUmaAiProcessStartInfoUsesExecutableDirectory()
{
    var executablePath = Path.Combine(Path.GetTempPath(), "UmaAI With Db", "UmaAI.exe");
    var startInfo = UmaAiProcessStartInfo.Create(executablePath);
    var fullPath = Path.GetFullPath(executablePath);
    var expectedDirectory = Path.GetDirectoryName(fullPath);

    RequireEqual(fullPath, startInfo.FileName, "UmaAI process file name");
    RequireEqual(expectedDirectory, startInfo.WorkingDirectory, "UmaAI process working directory");
    RequireEqual(Encoding.UTF8, startInfo.StandardOutputEncoding, "UmaAI stdout encoding");
    RequireEqual(Encoding.UTF8, startInfo.StandardErrorEncoding, "UmaAI stderr encoding");
}

static void TestRawUmaAiOutputWorkspaceRendersStdoutWithoutTerminalControlSequences(WorkspaceSmokeSession ui)
{
    using var output = new UmaAiRawOutputWorkspace();

    ui.Bootstrap.SwitchTo();
    var screenBefore = ui.CaptureScreen();
    if (output.PublishLine(null))
        throw new InvalidOperationException("Null stdout must not publish output.");
    if (!ReferenceEquals(Workspace.Current, ui.Bootstrap)
        || !string.Equals(screenBefore, ui.CaptureScreen(), StringComparison.Ordinal))
        throw new InvalidOperationException("Null stdout must not change the visible Workspace or framebuffer.");
    output.PublishLine("\u001b[31m速 : +123\u001b[0m");
    output.PublishLine("\u001b[2K\rAI建议：速");
    output.PublishLine("[yellow]literal markup[/]");

    var target = Workspace.Create("AIRedirector");
    if (!ReferenceEquals(Workspace.Current, ui.Bootstrap))
        throw new InvalidOperationException("Raw UmaAI output should not switch workspace focus.");

    target.SwitchTo();
    var rendered = ui.CaptureScreen(140, 48);
    foreach (var expected in new[] { "速 : +123", "AI建议：速", "[yellow]literal markup[/]" })
    {
        if (!rendered.Contains(expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"Raw UmaAI output should render visible stdout text: {expected}");
    }
    if (rendered.Contains('\u001b'))
        throw new InvalidOperationException("Raw UmaAI output must not replay ANSI escape sequences into the workspace panel.");
    if (rendered.Replace("\r\n", "\n").Contains('\r'))
        throw new InvalidOperationException("Raw UmaAI output must not replay carriage return control characters into the workspace panel.");

    for (var i = 0; i < UmaAiRawOutputWorkspace.MaxLines + 5; i++)
        output.PublishLine($"raw-line-{i:D3}");

    var tail = ui.CaptureScreen(140, UmaAiRawOutputWorkspace.MaxLines + 12);
    if (tail.Contains("raw-line-000", StringComparison.Ordinal))
        throw new InvalidOperationException("Raw UmaAI output should drop old lines after reaching the display buffer limit.");
    if (!tail.Contains($"raw-line-{UmaAiRawOutputWorkspace.MaxLines + 4:D3}", StringComparison.Ordinal))
        throw new InvalidOperationException("Raw UmaAI output should keep the newest line after reaching the display buffer limit.");
    ui.Bootstrap.SwitchTo();
}

static void TestRawUmaAiOutputWorkspaceRendersFullBufferAndDisposesPanel(WorkspaceSmokeSession ui)
{
    var output = new UmaAiRawOutputWorkspace();

    for (var i = 0; i < 50; i++)
        output.PublishLine($"full-log-line-{i:D2}");

    var target = Workspace.Create("AIRedirector");
    target.SwitchTo();
    var rendered = ui.CaptureScreen(140, 72);
    foreach (var i in Enumerable.Range(0, 50))
    {
        if (!rendered.Contains($"full-log-line-{i:D2}", StringComparison.Ordinal))
            throw new InvalidOperationException($"Raw UmaAI output should hand the full buffered content to the host viewport: line {i:D2} is missing.");
    }

    output.Dispose();
    target.SwitchTo();
    var disposed = ui.CaptureScreen(140, 72);
    if (disposed.Contains("UmaAI.exe 原始输出", StringComparison.Ordinal)
        || disposed.Contains("full-log-line-49", StringComparison.Ordinal))
        throw new InvalidOperationException("Raw output Dispose left umaai-raw-output visible.");
    if (!ReferenceEquals(Workspace.Create("AIREDIRECTOR"), target))
        throw new InvalidOperationException("Raw output Dispose removed the shared Workspace generation.");
    ui.Bootstrap.SwitchTo();
}

static async Task TestConfigPromptRunsOnOwnerWithoutOuterRunnable()
{
    var originalCwd = Directory.GetCurrentDirectory();
    var workspace = Path.Combine(Path.GetTempPath(), "ura-air-config-owner-smoke-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(workspace);
    Directory.SetCurrentDirectory(workspace);
    using var application = Terminal.Gui.App.Application.Create(new VirtualTimeProvider())
        .Init(DriverRegistry.Names.ANSI);
    application.Driver!.SetScreenSize(100, 24);
    var plugin = new AIRedirector.AIRedirector();
    var dialogOpened = false;
    application.AddTimeout(TimeSpan.Zero, () =>
    {
        var dialog = application.TopRunnable;
        if (dialog is null)
            return true;

        dialogOpened = true;
        application.RequestStop(dialog);
        return false;
    });

    try
    {
        await RequireCanceled(
            plugin.ConfigPromptAsync(application).WaitAsync(TimeSpan.FromSeconds(5)),
            "AIR config owner thread without outer runnable");
        if (!dialogOpened)
            throw new InvalidOperationException("AIR config owner-thread path did not open its dialog.");
        if (application.TopRunnable is not null)
            throw new InvalidOperationException("AIR config owner-thread path leaked a runnable.");
    }
    finally
    {
        plugin.Dispose();
        Directory.SetCurrentDirectory(originalCwd);
        Directory.Delete(workspace, recursive: true);
    }
}

static async Task TestConfigMenuUsesNativeControlsAndSavesDraft()
{
    using var terminal = new TerminalGuiSmokeApp();
    var originalCwd = Directory.GetCurrentDirectory();
    var workspace = Path.Combine(Path.GetTempPath(), "ura-air-config-smoke-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(workspace);
    Directory.SetCurrentDirectory(workspace);
    var plugin = new AIRedirector.AIRedirector();
    try
    {
        var run = await terminal.StartAsync(() => plugin.ConfigPromptAsync(terminal.Application));
        await terminal.WaitForScreenAsync("启用 Legend");

        var initialScreen = await terminal.CaptureScreenAsync();
        if (!initialScreen.Contains('☐') || !initialScreen.Contains("启用 UAF", StringComparison.Ordinal))
            throw new InvalidOperationException("AIR config must render a user-visible unchecked UAF checkbox.");
        foreach (var hiddenPath in new[] { "UAF 路径", "Cook 路径", "Mecha 路径", "Legend 路径" })
        {
            if (initialScreen.Contains(hiddenPath, StringComparison.Ordinal))
                throw new InvalidOperationException($"Disabled AIR scenario must hide its path setting: {hiddenPath}.");
        }
        var focusedUaf = await terminal.GetTextAttributeAsync("UAF", occurrence: 0);
        var unfocusedCook = await terminal.GetTextAttributeAsync("Cook", occurrence: 0);
        if (focusedUaf.Equals(unfocusedCook))
            throw new InvalidOperationException("AIR config must render a distinct focus attribute on the selected checkbox row.");

        var cook = await terminal.FindTextAsync("启用 Cook");
        await terminal.InjectMouseAsync(cook, MouseFlags.PositionReport);
        var hoveredCook = await terminal.GetTextAttributeAsync("Cook", occurrence: 0);
        var unhoveredUaf = await terminal.GetTextAttributeAsync("UAF", occurrence: 0);
        if (hoveredCook.Equals(unfocusedCook) || unhoveredUaf.Equals(focusedUaf))
            throw new InvalidOperationException("Mouse hover must move the visible focus attribute to the hovered config row.");

        var uaf = await terminal.FindTextAsync("启用 UAF");
        await terminal.InjectMouseAsync(uaf, MouseFlags.PositionReport);
        await terminal.InjectMouseAsync(uaf, MouseFlags.LeftButtonClicked);
        await terminal.WaitForScreenAsync("UAF 路径");
        var toggledScreen = await terminal.CaptureScreenAsync();
        if (!toggledScreen.Contains('☑') ||
            !toggledScreen.Contains("启用 UAF", StringComparison.Ordinal) ||
            !toggledScreen.Contains("UAF 路径", StringComparison.Ordinal))
            throw new InvalidOperationException("Enabling UAF must check the checkbox and reveal its path setting.");
        foreach (var hiddenPath in new[] { "Cook 路径", "Mecha 路径", "Legend 路径" })
        {
            if (toggledScreen.Contains(hiddenPath, StringComparison.Ordinal))
                throw new InvalidOperationException($"Enabling UAF must not reveal another scenario path: {hiddenPath}.");
        }

        var uafRow = await terminal.FindTextAsync("启用 UAF");
        var uafPathRow = await terminal.FindTextAsync("UAF 路径");
        var cookRow = await terminal.FindTextAsync("启用 Cook");
        if (!(uafRow.Y < uafPathRow.Y && uafPathRow.Y < cookRow.Y))
            throw new InvalidOperationException("Enabling UAF must insert its path row between UAF and Cook without overlap.");

        uaf = await terminal.FindTextAsync("启用 UAF");
        await terminal.InjectMouseAsync(uaf, MouseFlags.PositionReport);
        await terminal.InjectMouseAsync(uaf, MouseFlags.LeftButtonClicked);
        await terminal.WaitForAsync(async () =>
            !(await terminal.CaptureScreenAsync()).Contains("UAF 路径", StringComparison.Ordinal));
        uaf = await terminal.FindTextAsync("启用 UAF");
        await terminal.InjectMouseAsync(uaf, MouseFlags.PositionReport);
        await terminal.InjectMouseAsync(uaf, MouseFlags.LeftButtonClicked);
        await terminal.WaitForScreenAsync("UAF 路径");
        uafPathRow = await terminal.FindTextAsync("UAF 路径");

        var executablePath = Path.Combine(workspace, "UmaAI-smoke.exe");
        File.WriteAllText(executablePath, string.Empty);
        await terminal.InjectMouseAsync(uafPathRow, MouseFlags.PositionReport);
        await terminal.InjectMouseAsync(uafPathRow, MouseFlags.LeftButtonPressed);
        await terminal.InjectMouseAsync(uafPathRow, MouseFlags.LeftButtonReleased);
        await terminal.WaitForScreenAsync("选择 UAF UmaAI.exe");
        if (await terminal.InvokeAsync(() => terminal.Application.TopRunnable is OpenDialog) is false)
            throw new InvalidOperationException("AIR path setting must open Terminal.Gui OpenDialog FilePicker.");
        var executable = await terminal.FindTextAsync("UmaAI-smoke.exe");
        await terminal.InjectMouseAsync(executable, MouseFlags.PositionReport);
        await terminal.InjectMouseAsync(executable, MouseFlags.LeftButtonClicked);
        await terminal.InjectAsync(Key.Enter);
        await terminal.WaitForScreenAsync("UAF 路径");
        var save = await terminal.FindTextAsync("保存");
        await terminal.InjectMouseAsync(save, MouseFlags.PositionReport);
        await terminal.InjectMouseAsync(save, MouseFlags.LeftButtonPressed);
        await terminal.InjectMouseAsync(save, MouseFlags.LeftButtonReleased);
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        var saved = AIRedirectorConfig.Load(Path.Combine("PluginData", "AIRedirector", "settings.json"));
        RequireEqual(true, saved.UAF, "AIR config saved UAF state");
        RequireEqual(executablePath, saved.UAF_Path, "AIR config saved UAF path");
    }
    finally
    {
        plugin.Dispose();
        Directory.SetCurrentDirectory(originalCwd);
        Directory.Delete(workspace, recursive: true);
    }
}

static async Task TestConfigMenuCancellationLeavesFileUnchanged()
{
    using var terminal = new TerminalGuiSmokeApp();
    var originalCwd = Directory.GetCurrentDirectory();
    var workspace = Path.Combine(Path.GetTempPath(), "ura-air-config-cancel-smoke-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(workspace);
    Directory.SetCurrentDirectory(workspace);
    var plugin = new AIRedirector.AIRedirector();
    try
    {
        var configPath = Path.Combine("PluginData", "AIRedirector", "settings.json");
        var unchangedPath = Path.Combine(workspace, "unchanged.exe");
        var rejectedPath = Path.Combine(workspace, "rejected.exe");
        File.WriteAllText(unchangedPath, string.Empty);
        File.WriteAllText(rejectedPath, string.Empty);
        new AIRedirectorConfig { Cook = true, Cook_Path = unchangedPath }.Save(configPath);
        var baseline = File.ReadAllBytes(configPath);
        var pickerCanceled = await terminal.StartAsync(() => plugin.ConfigPromptAsync(terminal.Application));
        await terminal.WaitForScreenAsync("Cook 路径");
        var cookPath = await terminal.FindTextAsync("Cook 路径");
        await terminal.InjectMouseAsync(cookPath, MouseFlags.PositionReport);
        await terminal.InjectMouseAsync(cookPath, MouseFlags.LeftButtonPressed);
        await terminal.InjectMouseAsync(cookPath, MouseFlags.LeftButtonReleased);
        await terminal.WaitForScreenAsync("选择 Cook UmaAI.exe");
        var rejected = await terminal.FindTextAsync("rejected.exe");
        await terminal.InjectMouseAsync(rejected, MouseFlags.PositionReport);
        await terminal.InjectMouseAsync(rejected, MouseFlags.LeftButtonClicked);
        await terminal.InjectAsync(Key.Esc);
        await terminal.WaitForScreenAsync("Cook 路径");
        var save = await terminal.FindTextAsync("保存");
        await terminal.InjectMouseAsync(save, MouseFlags.PositionReport);
        await terminal.InjectMouseAsync(save, MouseFlags.LeftButtonPressed);
        await terminal.InjectMouseAsync(save, MouseFlags.LeftButtonReleased);
        await pickerCanceled.WaitAsync(TimeSpan.FromSeconds(5));
        var afterPickerCancel = AIRedirectorConfig.Load(configPath);
        RequireEqual(unchangedPath, afterPickerCancel.Cook_Path, "AIR FilePicker Esc kept the saved Cook path");
        baseline = File.ReadAllBytes(configPath);

        var escaped = await terminal.StartAsync(() => plugin.ConfigPromptAsync(terminal.Application));
        await terminal.InjectAsync(Key.Enter);
        await terminal.InjectAsync(Key.Esc);
        await RequireCanceled(escaped, "AIR config Esc");
        if (!baseline.SequenceEqual(File.ReadAllBytes(configPath)))
            throw new InvalidOperationException("AIR config Esc must not persist draft changes.");

        var canceled = await terminal.StartAsync(() => plugin.ConfigPromptAsync(terminal.Application));
        await terminal.InjectAsync(Key.Enter);
        var unfocusedCancel = await terminal.GetTextAttributeAsync("取消");
        var cancelFocused = false;
        for (var i = 0; i < 10; i++)
        {
            await terminal.InjectAsync(Key.CursorDown);
            if (!(await terminal.GetTextAttributeAsync("取消")).Equals(unfocusedCancel))
            {
                cancelFocused = true;
                break;
            }
        }
        if (!cancelFocused)
            throw new InvalidOperationException("Keyboard navigation did not visibly focus AIR config Cancel.");
        await terminal.InjectAsync(Key.Enter);
        await RequireCanceled(canceled, "AIR config Cancel");
        if (!baseline.SequenceEqual(File.ReadAllBytes(configPath)))
            throw new InvalidOperationException("AIR config Cancel must not persist draft changes.");

        var closed = await terminal.StartAsync(() => plugin.ConfigPromptAsync(terminal.Application));
        await terminal.InjectAsync(Key.Enter);
        await terminal.InvokeAsync(() => terminal.Application.RequestStop(
            terminal.Application.TopRunnable
                ?? throw new InvalidOperationException("AIR config dialog is not running.")));
        await RequireCanceled(closed, "AIR config close");
        if (!baseline.SequenceEqual(File.ReadAllBytes(configPath)))
            throw new InvalidOperationException("AIR config close must not persist draft changes.");

        using var cancellation = new CancellationTokenSource();
        var cancelled = await terminal.StartAsync(() =>
            plugin.ConfigPromptAsync(terminal.Application, cancellation.Token));
        await terminal.InjectAsync(Key.Enter);
        var cancellationCookPath = await terminal.FindTextAsync("Cook 路径");
        await terminal.InjectMouseAsync(cancellationCookPath, MouseFlags.PositionReport);
        await terminal.InjectMouseAsync(cancellationCookPath, MouseFlags.LeftButtonPressed);
        await terminal.InjectMouseAsync(cancellationCookPath, MouseFlags.LeftButtonReleased);
        await terminal.WaitForScreenAsync("选择 Cook UmaAI.exe");
        cancellation.Cancel();
        await RequireCanceled(cancelled, "AIR config cancellation token");
        if (!baseline.SequenceEqual(File.ReadAllBytes(configPath)))
            throw new InvalidOperationException("AIR config cancellation token must not persist draft changes.");
    }
    finally
    {
        plugin.Dispose();
        Directory.SetCurrentDirectory(originalCwd);
        Directory.Delete(workspace, recursive: true);
    }
}

static async Task RequireCanceled(Task task, string name)
{
    try
    {
        await task.WaitAsync(TimeSpan.FromSeconds(5));
        throw new InvalidOperationException($"{name}: expected OperationCanceledException.");
    }
    catch (OperationCanceledException)
    {
    }
}

static async ValueTask TestLegendTrainingOutputPatchesCurrentWorkspaceAndClearsStaleOutput(WorkspaceSmokeSession ui)
{
    var plugin = new LegendPlugin();

    try
    {
        plugin.Initialize(new RecordingPluginContext(ui.Application));
        await plugin.Analyze(CreateLegendCheckEventResponse());
        var target = Workspace.Create("LegendScenarioAnalyzer");

        var id = new LegendTrainingDisplayId(0, 2);
        using var externalModifier = LegendTrainingDisplay.RegisterPartProducer("External");
        externalModifier.Update(
            id,
            (_, display) => display.Extra.AddText("external persistent modifier"));
        using var buffer = new LegendAiOutputBuffer();
        buffer.SetTarget(id.SingleModeCharaId, id.Turn);
        ui.Bootstrap.SwitchTo();
        buffer.ProcessLine("\u001b[31m-------------------------------------------------------------------------------------------\u001b[0m");
        if (!buffer.ApplyTargetDisplay(CancellationToken.None))
            throw new InvalidOperationException("Legend AI output should refresh the current Legend workspace.");
        if (!ReferenceEquals(Workspace.Current, ui.Bootstrap))
            throw new InvalidOperationException("Legend AI separator refresh should not switch workspace focus.");

        buffer.ProcessLine("\u001b[33m速 : *   162\u001b[0m");
        buffer.ProcessLine("耐 : -456");
        buffer.ProcessLine("休息 :    -55");
        buffer.ProcessLine("外出 :   -103");
        buffer.ProcessLine("AI建议：速");
        buffer.ProcessLine("运气指标： | 本局：+1 | 本回合：2 | 评分预测: 35000");
        buffer.ProcessLine("比赛亏损：15");
        if (!buffer.ApplyTargetDisplay(CancellationToken.None))
            throw new InvalidOperationException("Legend AI output should refresh after parsed score lines.");
        if (!ReferenceEquals(Workspace.Current, ui.Bootstrap))
            throw new InvalidOperationException("Legend AI training patch should not switch workspace focus.");

        var rendered = CapturePanel(ui, target, 200, 80);
        foreach (var expected in new[] { "external persistent modifier", "AI评分", "*   162", "-456", "休息", "-55", "外出", "-103", "AI建议：速", "运气指标", "比赛亏损" })
        {
            if (!rendered.Contains(expected, StringComparison.Ordinal))
                throw new InvalidOperationException($"Rendered Legend panel does not contain '{expected}'.");
        }
        var externalTitleIndex = rendered.IndexOf("External", StringComparison.Ordinal);
        var externalBodyIndex = rendered.IndexOf("external persistent modifier", StringComparison.Ordinal);
        var aiTitleIndex = rendered.IndexOf("AI", externalBodyIndex + "external persistent modifier".Length, StringComparison.Ordinal);
        var aiBodyIndex = rendered.IndexOf("AI评分", StringComparison.Ordinal);
        if (externalTitleIndex < 0 || externalBodyIndex <= externalTitleIndex
            || aiTitleIndex <= externalBodyIndex || aiBodyIndex <= aiTitleIndex)
        {
            throw new InvalidOperationException("AIRedirector output was not isolated in the AI Extra section.");
        }
        if (rendered.Contains('\u001b'))
            throw new InvalidOperationException("AIRedirector must strip ANSI escape sequences before patching the workspace panel.");

        buffer.ProcessLine("\u001b[31m-------------------------------------------------------------------------------------------\u001b[0m");
        if (!buffer.ApplyTargetDisplay(CancellationToken.None))
            throw new InvalidOperationException("Legend AI output should refresh when a new turn clears stale output.");
        if (!ReferenceEquals(Workspace.Current, ui.Bootstrap))
            throw new InvalidOperationException("Legend AI stale-clear refresh should not switch workspace focus.");

        var cleared = CapturePanel(ui, target, 200, 80);
        if (!cleared.Contains("External", StringComparison.Ordinal)
            || !cleared.Contains("external persistent modifier", StringComparison.Ordinal))
            throw new InvalidOperationException("Refreshing AIRedirector removed another registered modifier.");
        if (cleared.Contains("AI", StringComparison.Ordinal))
            throw new InvalidOperationException("Clearing AIRedirector retained its empty AI Extra section.");
        foreach (var stale in new[] { "AI评分", "*   162", "-456", "休息", "-55", "外出", "-103", "AI建议：速", "运气指标", "比赛亏损" })
        {
            if (cleared.Contains(stale, StringComparison.Ordinal))
                throw new InvalidOperationException($"New turn should clear stale Legend AI output '{stale}'.");
        }
    }
    finally
    {
        plugin.Dispose();
    }
}

static async ValueTask TestLegendNonTrainingRecommendationsRenderInImportant(WorkspaceSmokeSession ui)
{
    foreach (var recommendation in new[]
    {
        "AI建议：休息",
        "AI建议：外出",
        "AI建议：比赛",
        "AI建议：普通外出",
        "AI建议：团队外出1",
        "AI建议：团队外出4选绿",
        "AI建议：选红",
        "AI建议：不选"
    })
    {
        var plugin = new LegendPlugin();
        try
        {
            plugin.Initialize(new RecordingPluginContext(ui.Application));
            var response = CreateLegendCheckEventResponse();
            response.data.chara_info.skill_point = 9600;
            response.data.chara_info.chara_effect_id_array = [104];
            await plugin.Analyze(response);
            var target = Workspace.Create("LegendScenarioAnalyzer");

            using var buffer = new LegendAiOutputBuffer();
            buffer.SetTarget(0, 2);
            ui.Bootstrap.SwitchTo();
            buffer.ProcessLine(recommendation);
            if (!buffer.ApplyTargetDisplay(CancellationToken.None))
                throw new InvalidOperationException("Legend AI output should refresh the current Legend workspace.");
            if (!ReferenceEquals(Workspace.Current, ui.Bootstrap))
                throw new InvalidOperationException("Legend AI recommendation refresh should not switch workspace focus.");

            target.SwitchTo();
            ui.SendKey(Key.Home);
            var rendered = ui.CaptureScreen(200, 120);
            ui.Bootstrap.SwitchTo();
            var recommendationColumn = FirstColumnOfLineContaining(rendered, recommendation);
            var importantColumn = FirstColumnOfLineContaining(rendered, "剩余PT>9500");
            var extraColumn = FirstColumnOfLineContaining(rendered, "团卡彩圈生效中");
            if (recommendationColumn < 0)
                throw new InvalidOperationException($"Rendered Legend panel does not contain '{recommendation}'.");
            if (importantColumn < 0)
                throw new InvalidOperationException("Legend panel did not render its public Important warning row.");
            if (extraColumn < 0)
                throw new InvalidOperationException("Legend panel did not render its public Extras status row.");
            if (recommendationColumn != importantColumn || extraColumn <= importantColumn)
                throw new InvalidOperationException($"{recommendation} should render in the Important section, not Extras.");
        }
        finally
        {
            plugin.Dispose();
        }
    }
}

static async ValueTask TestLegendBuffSelectionOutputUsesObtainableBuffContext(WorkspaceSmokeSession ui)
{
    var originalCwd = Directory.GetCurrentDirectory();
    var workspace = Path.Combine(Path.GetTempPath(), "ura-air-legend-smoke-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(workspace);
    Directory.SetCurrentDirectory(workspace);

    var plugin = new LegendPlugin();

    try
    {
        WriteLegendBuffCsv();
        plugin.Initialize(new RecordingPluginContext(ui.Application));
        await plugin.Analyze(CreateLegendCheckEventResponse(
            playingState: 5,
            uncheckedEvents: [new() { story_id = 400010112 }],
            obtainableBuffIds: [1001, 1002, 2001, 2002, 3001, 3002]));
        var target = Workspace.Create("LegendScenarioAnalyzer");

        var id = new LegendTrainingDisplayId(0, 2);
        using var buffer = new LegendAiOutputBuffer();
        buffer.SetTarget(id.SingleModeCharaId, id.Turn);
        ui.Bootstrap.SwitchTo();
        buffer.ProcessLine("速 : *   162");
        buffer.ProcessLine("休息 :    -55");
        buffer.ProcessLine("选择心得中：");
        buffer.ProcessLine("选蓝色第 1 个 :   -121");
        buffer.ProcessLine("选蓝色第 2 个 :   -594");
        buffer.ProcessLine("选绿色第 1 个 :   -547");
        buffer.ProcessLine("选绿色第 2 个 : *    -8");
        buffer.ProcessLine("选红色第 1 个 :   -278");
        buffer.ProcessLine("选红色第 2 个 :   -121");
        buffer.ProcessLine("AI建议：选绿色第 2 个 (绿1)（交渉術）羁绊+2");
        buffer.ProcessLine("运气指标： | 本局：95 | 本回合：41（训练：148 | 评分预测: 40906（乐观+2766）");
        if (!buffer.ApplyTargetDisplay(CancellationToken.None))
            throw new InvalidOperationException("Legend buff selection AI output should refresh the current Legend workspace.");
        if (!ReferenceEquals(Workspace.Current, ui.Bootstrap))
            throw new InvalidOperationException("Legend AI selection patch should not switch workspace focus.");

        var rendered = CapturePanel(ui, target, 200, 80);
        foreach (var expected in new[]
                 {
                     "选蓝色第 1 个",
                     "AI评分",
                     "-121",
                     "选绿色第 2 个",
                     "-8",
                     "AI建议",
                     "交渉術",
                     "羁绊+2",
                     "运气指标",
                     "评分预测"
                 })
        {
            if (!rendered.Contains(expected, StringComparison.Ordinal))
                throw new InvalidOperationException($"Rendered Legend buff selection panel does not contain '{expected}'.");
        }
        if (!ContainsRenderedLine(rendered, "选绿色第 2 个", "AI评分", "-8"))
            throw new InvalidOperationException("Legend buff selection AI score should stay on the same rendered line as the candidate.");

        foreach (var staleTraining in new[] { "选择心得中", "*   162", "休息", "-55" })
        {
            if (rendered.Contains(staleTraining, StringComparison.Ordinal))
                throw new InvalidOperationException($"Buff selection output should not show stale training score '{staleTraining}'.");
        }

        buffer.ProcessLine("\u001b[31m-------------------------------------------------------------------------------------------\u001b[0m");
        if (!buffer.ApplyTargetDisplay(CancellationToken.None))
            throw new InvalidOperationException("Legend buff selection AI output should refresh when a new turn clears stale output.");
        if (!ReferenceEquals(Workspace.Current, ui.Bootstrap))
            throw new InvalidOperationException("Legend AI selection stale-clear refresh should not switch workspace focus.");

        var cleared = CapturePanel(ui, target, 200, 80);
        foreach (var stale in new[] { "AI评分", "-121", "*    -8", "AI建议", "运气指标", "评分预测" })
        {
            if (cleared.Contains(stale, StringComparison.Ordinal))
                throw new InvalidOperationException($"New turn should clear stale Legend buff selection AI output '{stale}'.");
        }
    }
    finally
    {
        plugin.Dispose();
        Directory.SetCurrentDirectory(originalCwd);
    }
}

static SingleModeLegendCheckEventResponse CreateLegendCheckEventResponse(
    int[]? obtainableBuffIds = null,
    int playingState = 1,
    SingleModeEventInfo[]? uncheckedEvents = null)
    => new()
    {
        data = new()
        {
            chara_info = new()
            {
                speed = 100,
                stamina = 110,
                power = 120,
                guts = 130,
                wiz = 140,
                max_speed = 1500,
                max_stamina = 1500,
                max_power = 1500,
                max_guts = 1500,
                max_wiz = 1500,
                vital = 80,
                max_vital = 100,
                motivation = 5,
                turn = 2,
                skill_point = 100,
                state = 1,
                playing_state = playingState,
                support_card_array = [],
                evaluation_info_array = [],
                training_level_info_array = [.. BaseTrainIds().Select(commandId => new TrainingLevelInfo { command_id = commandId, level = 1 })],
                chara_effect_id_array = []
            },
            home_info = new() { command_info_array = BaseTrainingCommands() },
            unchecked_event_array = uncheckedEvents ?? [],
            legend_data_set = new()
            {
                command_info_array = [.. BaseTrainIds().Select((commandId, index) => new SingleModeLegendCommandInfo
                {
                    command_type = 1,
                    command_id = commandId,
                    legend_id = 9046 + index % 3,
                    gain_gauge = index + 1,
                    params_inc_dec_info_array = TrainingParams(index + 10, includeVital: false),
                    friend_gauge_gain_array = []
                })],
                evaluation_info_array = [],
                gauge_count_array =
                [
                    new() { legend_id = 9046, count = 2 },
                    new() { legend_id = 9047, count = 4 },
                    new() { legend_id = 9048, count = 6 },
                ],
                buff_info_array =
                [
                    new() { buff_id = 1001, is_active = 1 },
                    new() { buff_id = 2001, is_active = 1 },
                    new() { buff_id = 3001, is_active = 0 },
                ],
                obtainable_buff_id_array = obtainableBuffIds ?? [],
                activated_buff_id_array = [],
                masterly_bonus_info = new(),
                race_history_array = [],
            },
            select_index_info_array = []
        }
    };

static SingleModeCommandInfo[] BaseTrainingCommands()
    => [.. BaseTrainIds().Select((commandId, index) => new SingleModeCommandInfo
    {
        command_type = 1,
        command_id = commandId,
        is_enable = 1,
        training_partner_array = [],
        tips_event_partner_array = [],
        params_inc_dec_info_array = TrainingParams(index, includeVital: true),
        failure_rate = index * 5,
        sub_command_partner_array = []
    })];

static SingleModeParamsIncDecInfo[] TrainingParams(int seed, bool includeVital)
{
    var targetTypes = includeVital ? new[] { 1, 2, 3, 4, 5, 30, 10 } : new[] { 1, 2, 3, 4, 5, 30 };
    return [.. targetTypes.Select((targetType, index) => new SingleModeParamsIncDecInfo
    {
        target_type = targetType,
        value = targetType == 10 ? -10 : seed + index + 1
    })];
}

static int[] BaseTrainIds() => [101, 105, 102, 103, 106];

static void WriteLegendBuffCsv()
{
    var dataDirectory = Path.Combine("PluginData", "LegendScenarioAnalyzer");
    Directory.CreateDirectory(dataDirectory);
    File.WriteAllText(
        Path.Combine(dataDirectory, "legend_buff.csv"),
        """
        cn_effect,name,rank,color,condition,buffId,isTrigger,isPerson,youQing,ganJing,xunLian,hintLv,hintCount,deYiLv,buZaiLv,jiBan,vitalCostDrop,fenShen,mood,note
        测试蓝效果,测试蓝心得,1,0,201,1001,false,false,0,0,0,0,0,0,0,0,0,0,0,smoke
        测试蓝2效果,测试蓝2心得,2,0,201,1002,false,false,0,0,0,0,0,0,0,0,0,0,0,smoke
        测试绿效果,测试绿心得,1,1,201,2001,false,false,0,0,0,0,0,0,0,0,0,0,0,smoke
        测试绿2效果,测试绿2心得,2,1,201,2002,false,false,0,0,0,0,0,0,0,0,0,0,0,smoke
        测试红效果,测试红心得,1,2,201,3001,false,false,0,0,0,0,0,0,0,0,0,0,0,smoke
        测试红2效果,测试红2心得,2,2,201,3002,false,false,0,0,0,0,0,0,0,0,0,0,0,smoke
        """);
}

static string CapturePanel(WorkspaceSmokeSession ui, Workspace workspace, int width, int height)
{
    workspace.SwitchTo();
    var screen = ui.CaptureScreen(width, height);
    ui.Bootstrap.SwitchTo();
    return screen;
}

static string FindRepositoryRoot()
    => Environment.GetEnvironmentVariable("URA_TEST_PLUGIN_ROOT")
       ?? throw new InvalidOperationException("URA_TEST_PLUGIN_ROOT must identify the plugin checkout.");

static void RequireEqual<T>(T expected, T actual, string name)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{name}: expected {expected}, got {actual}.");
}

static bool ContainsRenderedLine(string rendered, params string[] expectedParts)
    => rendered
        .Split(["\r\n", "\n"], StringSplitOptions.None)
        .Any(line => expectedParts.All(part => line.Contains(part, StringComparison.Ordinal)));

static int FirstColumnOfLineContaining(string rendered, string text)
{
    foreach (var line in rendered.Split(["\r\n", "\n"], StringSplitOptions.None))
    {
        var index = line.IndexOf(text, StringComparison.Ordinal);
        if (index >= 0)
            return index;
    }
    return -1;
}

sealed class RecordingPluginContext(IApplication application) : IPluginContext
{
    public IApplication Application { get; } = application;
    public IPluginHostEvents Events { get; } = new ThrowingPluginHostEvents();
    public IPluginAnalyzerRegistry Analyzers { get; } = new RecordingPluginAnalyzerRegistry();
    public bool IsPluginAvailable(string internalName) => false;

    public void RunBackground(Func<CancellationToken, ValueTask> handler)
        => throw new NotSupportedException("AIRedirector smoke does not start background work through this context.");
}

sealed class ThrowingPluginHostEvents : IPluginHostEvents
{
    public void OnStarted(Func<CancellationToken, ValueTask> handler)
        => throw new NotSupportedException("AIRedirector smoke does not use host events.");
}

sealed class RecordingPluginAnalyzerRegistry : IPluginAnalyzerRegistry
{
    public void Register<TPayload>(
        AnalyzerKind kind,
        IReadOnlyList<EndpointPattern> patterns,
        Func<AnalyzerInvocation<TPayload>, ValueTask> handler,
        int priority = 0)
    {
    }
}

sealed class TerminalGuiSmokeApp : IDisposable
{
    readonly BlockingCollection<RunRequest> runs = [];
    readonly TaskCompletionSource<IApplication> applicationReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly Thread uiThread;
    readonly VirtualTimeProvider time = new();

    public TerminalGuiSmokeApp()
    {
        uiThread = new Thread(RunUiThread)
        {
            IsBackground = true,
            Name = "AIRedirector Terminal.Gui smoke",
        };
        uiThread.Start();
        Application = applicationReady.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
    }

    public IApplication Application { get; }

    public async Task<Task> StartAsync(Func<Task> run)
    {
        var owner = Application.TopRunnableView;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runs.Add(new(run, completion));
        await WaitForAsync(() =>
            !ReferenceEquals(Application.TopRunnableView, owner) || completion.Task.IsCompleted);
        if (completion.Task.IsCompleted)
            return completion.Task;

        await InvokeAsync(() =>
        {
            var driver = Application.Driver
                ?? throw new InvalidOperationException("Terminal.Gui ANSI driver was not initialized.");
            driver.SetScreenSize(100, 24);
            Application.LayoutAndDraw(forceRedraw: true);
        });
        return completion.Task;
    }

    public Task InjectAsync(Key key) => InvokeAsync(() => Application.InjectKey(key));

    public Task InjectMouseAsync(Point position, MouseFlags flags)
        => InvokeAsync(() =>
        {
            Application.InjectMouse(new()
            {
                ScreenPosition = position,
                Flags = flags,
                Timestamp = time.Now
            });
            Application.LayoutAndDraw(forceRedraw: true);
        });

    public Task<string> CaptureScreenAsync()
        => InvokeAsync(() =>
        {
            Application.LayoutAndDraw(forceRedraw: true);
            return Application.Driver!.ToString();
        });

    public Task<Point> FindTextAsync(string text, int occurrence = 0)
        => InvokeAsync(() => FindText(Application.Driver!.ToString(), text, occurrence));

    public Task<TAttribute> GetTextAttributeAsync(string text, int occurrence = 0)
        => InvokeAsync(() =>
        {
            Application.LayoutAndDraw(forceRedraw: true);
            var point = FindText(Application.Driver!.ToString(), text, occurrence);
            return Application.Driver.Contents[point.Y, point.X].Attribute
                ?? throw new InvalidOperationException($"'{text}' has no rendered attribute.");
        });

    public async Task WaitForScreenAsync(string text)
        => await WaitForAsync(async () =>
            (await InvokeAsync(() => Application.Driver!.ToString())).Contains(text, StringComparison.Ordinal));

    public async Task InvokeAsync(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Application.Invoke(() =>
        {
            try
            {
                action();
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    public async Task<T> InvokeAsync<T>(Func<T> action)
    {
        T result = default!;
        await InvokeAsync(() =>
        {
            result = action();
        });
        return result;
    }

    public async Task WaitForAsync(Func<bool> condition)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(10, cancellation.Token);
    }

    public async Task WaitForAsync(Func<Task<bool>> condition)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!await condition())
            await Task.Delay(10, cancellation.Token);
    }

    static Point FindText(string screen, string text, int occurrence)
    {
        foreach (var (line, y) in screen
                     .ReplaceLineEndings("\n")
                     .Split('\n')
                     .Select((line, y) => (line, y)))
        {
            var from = 0;
            while (true)
            {
                var index = line.IndexOf(text, from, StringComparison.Ordinal);
                if (index < 0)
                    break;
                if (occurrence-- == 0)
                    return new(line[..index].GetColumns(), y);
                from = index + text.Length;
            }
        }

        throw new InvalidOperationException($"Visible text '{text}' was not found.");
    }

    public void Dispose()
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (Application.TopRunnableView is { } runnable)
        {
            InvokeAsync(Application.RequestStop).GetAwaiter().GetResult();
            while (ReferenceEquals(Application.TopRunnableView, runnable) && DateTime.UtcNow < deadline)
                Thread.Sleep(10);
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("Terminal.Gui smoke session did not stop.");
        }
        runs.CompleteAdding();
        if (!uiThread.Join(TimeSpan.FromSeconds(5)))
            throw new TimeoutException("Terminal.Gui smoke thread did not stop.");
        runs.Dispose();
    }

    void RunUiThread()
    {
        IApplication? application = null;
        try
        {
            application = Terminal.Gui.App.Application.Create(time).Init(DriverRegistry.Names.ANSI);
            application.Driver!.SetScreenSize(100, 24);
            application.Iteration += ApplicationIteration;
            applicationReady.SetResult(application);
            using var window = new Window
            {
                Width = Dim.Fill(),
                Height = Dim.Fill(),
            };
            application.Run(window);
        }
        catch (Exception ex)
        {
            applicationReady.TrySetException(ex);
            while (runs.TryTake(out var request))
                request.Completion.TrySetException(ex);
        }
        finally
        {
            if (application is not null)
                application.Iteration -= ApplicationIteration;
            application?.Dispose();
        }
    }

    void ApplicationIteration(object? sender, Terminal.Gui.App.EventArgs<IApplication?> e)
    {
        while (runs.TryTake(out var request))
            request.Execute();
    }

    sealed class RunRequest(Func<Task> run, TaskCompletionSource completion)
    {
        int started;

        public TaskCompletionSource Completion { get; } = completion;

        public void Execute()
        {
            if (Interlocked.Exchange(ref started, 1) != 0)
                return;

            try
            {
                var task = run();
                if (task.IsCompleted)
                {
                    Complete(task);
                    return;
                }

                _ = task.ContinueWith(
                    static (completed, state) => ((RunRequest)state!).Complete(completed),
                    this,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            catch (Exception ex)
            {
                Completion.SetException(ex);
            }
        }

        void Complete(Task task)
        {
            try
            {
                task.GetAwaiter().GetResult();
                Completion.SetResult();
            }
            catch (Exception ex)
            {
                Completion.SetException(ex);
            }
        }
    }
}

// ===========================================================================
// UmaAI --json 解析（Step 8 + 2026-09 三类型扩展）
//
// 2026-09 协议升级：顶层 `type` 字段（`decision` / `info` / `error`）替代原
// `schema_version` 协议标记。HandleOutput 按 type 分发到 ApplyDecision /
// ApplyInfo / ApplyError（Trace 占位）。原始 stdout 仍写到 raw output workspace。
// ===========================================================================

/// 解析合法的拉面剧本决策 JSON：scenario="ramen"，新协议完整字段透传
static void TestTryParseUmaAiDecisionAcceptsRamenJsonLine()
{
    const string line = """{"type":"decision","turn":42,"scenario":"ramen","action_index":0,"score":56712.3,"decision_kind":"ramen_select","candidate_scores":[56712.3,56100.5,55234.1],"candidate_descriptions":["吃面A","训练/耐","智力","速度"],"candidate_n":[1024,800,256,512],"scenario_extra":{"current_terminal_baseline":56000.0,"total_luck_score":712.3,"last_turn_delta":-3.5}}""";
    if (!AIRedirector.AIRedirector.TryParseUmaAiDecision(line, out var decision))
        throw new InvalidOperationException("拉面 JSON 行应解析成功");
    if (decision.ActionIndex != 0) throw new InvalidOperationException($"action_index={decision.ActionIndex}");
    if (decision.Score is not (> 56712.0 and < 56712.5)) throw new InvalidOperationException($"score={decision.Score}");
    if (decision.Scenario != "ramen") throw new InvalidOperationException($"scenario={decision.Scenario}");
    if (decision.Turn != 42) throw new InvalidOperationException($"turn={decision.Turn}");
    if (decision.DecisionKind != "ramen_select") throw new InvalidOperationException($"decision_kind={decision.DecisionKind}");
    if (decision.CandidateDescriptions.Length != 4) throw new InvalidOperationException($"candidate_descriptions 长度={decision.CandidateDescriptions.Length}");
    if (decision.CandidateN.Length != 4) throw new InvalidOperationException($"candidate_n 长度={decision.CandidateN.Length}");
    if (decision.CurrentTerminalBaseline != 56000.0) throw new InvalidOperationException($"current_terminal_baseline={decision.CurrentTerminalBaseline}");
    if (decision.TotalLuckScore != 712.3) throw new InvalidOperationException($"total_luck_score={decision.TotalLuckScore}");
    if (decision.LastTurnDelta != -3.5) throw new InvalidOperationException($"last_turn_delta={decision.LastTurnDelta}");
}

/// 解析温泉剧本决策 JSON：scenario="onsen"，scenario_extra / 候选缺失时运气字段为 null
static void TestTryParseUmaAiDecisionAcceptsOnsenJsonLine()
{
    const string line = """{"type":"decision","turn":12,"scenario":"onsen","action_index":0,"score":54321.0}""";
    if (!AIRedirector.AIRedirector.TryParseUmaAiDecision(line, out var decision))
        throw new InvalidOperationException("温泉 JSON 行应解析成功");
    if (decision.ActionIndex != 0) throw new InvalidOperationException($"action_index={decision.ActionIndex}");
    if (decision.Scenario != "onsen") throw new InvalidOperationException($"scenario={decision.Scenario}");
    if (decision.Turn != 12) throw new InvalidOperationException($"turn={decision.Turn}");
    if (decision.CandidateDescriptions.Length != 0) throw new InvalidOperationException($"candidate_descriptions 应为空");
    if (decision.CurrentTerminalBaseline is not null) throw new InvalidOperationException($"current_terminal_baseline 应为 null");
    if (decision.TotalLuckScore is not null) throw new InvalidOperationException($"total_luck_score 应为 null");
    if (decision.LastTurnDelta is not null) throw new InvalidOperationException($"last_turn_delta 应为 null");
}

/// 玩家模式 stdout 行（非 JSON）应被拒绝，不抛异常
static void TestTryParseUmaAiDecisionRejectsNonJsonLine()
{
    const string line = "AI 选择: 第 2 个动作（评分: 1234）";
    if (AIRedirector.AIRedirector.TryParseUmaAiDecision(line, out _))
        throw new InvalidOperationException("非 JSON 行应被拒绝");
}

/// 旧版 / 缺 type 字段的 JSON 应被拒绝（避免误识别）
static void TestTryParseUmaAiDecisionRejectsJsonWithoutTypeDecision()
{
    const string line = """{"turn":5,"scenario":"onsen","action_index":1,"score":1000.0}""";
    if (AIRedirector.AIRedirector.TryParseUmaAiDecision(line, out _))
        throw new InvalidOperationException("缺 type 字段的 JSON 应被拒绝");
}

/// info 行（含 4 种已知 event）应被识别
static void TestTryParseUmaAiInfoAcceptsAllFourEvents()
{
    foreach (var expected in new[] { "connected", "compute_start", "compute_next_step", "new_game" })
    {
        var line = $$"""{"type":"info","event":"{{expected}}"}""";
        if (!AIRedirector.AIRedirector.TryParseUmaAiInfo(line, out var infoEvent))
            throw new InvalidOperationException($"info 行应解析成功: event={expected}");
        if (infoEvent != expected)
            throw new InvalidOperationException($"info event 不匹配: 期望 {expected}, 得到 {infoEvent}");
    }
}

/// error 行（中文 message）应被识别
static void TestTryParseUmaAiErrorAcceptsChineseMessage()
{
    const string line = """{"type":"error","message":"小黑板目录不存在"}""";
    if (!AIRedirector.AIRedirector.TryParseUmaAiError(line, out var errorMessage))
        throw new InvalidOperationException("error 行应解析成功");
    if (errorMessage != "小黑板目录不存在")
        throw new InvalidOperationException($"error message 不匹配: 得到 {errorMessage}");
}

/// error 行（英文 / 含特殊字符 message）应被识别
static void TestTryParseUmaAiErrorAcceptsEnglishMessage()
{
    const string line = """{"type":"error","message":"parse failed: bad json"}""";
    if (!AIRedirector.AIRedirector.TryParseUmaAiError(line, out var errorMessage))
        throw new InvalidOperationException("error 行应解析成功");
    if (errorMessage != "parse failed: bad json")
        throw new InvalidOperationException($"error message 不匹配: 得到 {errorMessage}");
}

/// info 行不应被 decision 解析器误识别（避免 type 字段互窜）
static void TestTryParseUmaAiDecisionRejectsInfoLine()
{
    const string line = """{"type":"info","event":"connected"}""";
    if (AIRedirector.AIRedirector.TryParseUmaAiDecision(line, out _))
        throw new InvalidOperationException("info 行不应被 decision 解析器识别");
}

/// error 行不应被 info 解析器误识别
static void TestTryParseUmaAiInfoRejectsErrorLine()
{
    const string line = """{"type":"error","message":"x"}""";
    if (AIRedirector.AIRedirector.TryParseUmaAiInfo(line, out _))
        throw new InvalidOperationException("error 行不应被 info 解析器识别");
}

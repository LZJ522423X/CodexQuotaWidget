using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexQuotaWidget.Core;
using CodexQuotaWidget.Core.Automation;
using CodexQuotaWidget.Core.ControlCenter;

namespace CodexQuotaWidget;

/// <summary>Real product panel and real provider connections. Sends no task and uses no external UI automation.</summary>
public static class WorkflowLiveConnectionCheckRunner
{
    const string OutputDirectory = @"D:\CodexQuotaWidget-GitHub\artifacts\workflow-live-preview";
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static async Task<int> RunAsync()
    {
        if (!AutomationStore.IsSafeDPath(OutputDirectory) || !AutomationStore.IsSafeDPath(Paths.Data))
            throw new InvalidDataException("Live product checks require safe D data and artifact paths");
        Directory.CreateDirectory(OutputDirectory);
        var reportPath = Path.Combine(OutputDirectory, "check-result.json");
        if (!AutomationStore.IsSafeDPath(reportPath)) throw new InvalidDataException("Live report path is unsafe");
        var started = DateTimeOffset.UtcNow;
        var checks = new List<Observation>(); var attempts = new List<Attempt>(); var captures = new List<string>();
        var preserved = new[] { "settings.json", "settings.json.bak", Path.Combine("workflow", "loop-state.json"), Path.Combine("workflow", "loop-state.json.bak") };
        var before = preserved.ToDictionary(name => name, name => Hash(Path.Combine(Paths.Data, name)));
        WorkflowLoopRuntime? runtime = null; Window? window = null;
        WorkflowProviderConnection? initialGpt = null, initialCodex = null, finalGpt = null, finalCodex = null;
        WorkflowLoopState? initialTask = null;
        string failure = "", cleanup = "NotStarted";
        WorkflowConnectionCandidate[] gptCandidates = [];
        bool connectionsPassed = false;
        try
        {
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new { passed = false, status = "running", syntheticAi = false, modelTasksDispatched = false }, Options));
            var theme = AtomicStore.Read<Settings>(Path.Combine(Paths.Data, "settings.json")) ?? new Settings();
            theme.Validate(); ((App)Application.Current).ConfigurePreviewTheme(theme);
            runtime = new WorkflowLoopRuntime(Paths.Data); initialGpt = runtime.GptConnection; initialCodex = runtime.CodexConnection; initialTask = runtime.State;
            var panel = new WorkflowLoopPanel(runtime);
            var scroll = new ScrollViewer { Content = panel, Margin = new Thickness(20), VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            window = new Window { Title = "AI Control Center：真实连接检查（不启动任务）", Content = scroll,
                Width = Math.Clamp(theme.Width, 520, 760), Height = Math.Clamp(theme.Height, 580, 900),
                ShowInTaskbar = false, ShowActivated = false };
            window.SetResourceReference(Window.BackgroundProperty, "Glass"); window.SetResourceReference(Window.ForegroundProperty, "Ink");
            window.Show(); await SettleAsync(); panel.Refresh();
            var advanced = Named<Expander>(panel, "WorkflowLoopAdvanced");
            checks.Add(new("RealWorkflowRuntimeAttached", true, "实际WorkflowLoopPanel使用产品WorkflowLoopRuntime；未使用FakeController。"));
            checks.Add(new("NoMainTaskTextBox", !Descendants<TextBox>(panel).Any(), "主区不默认展示完整任务输入框。"));
            checks.Add(new("AdvancedInitiallyCollapsed", !advanced.IsExpanded, "完整任务、历史、协议和诊断默认折叠。"));
            checks.Add(new("InitiallyNoTaskRunning", !runtime.IsRunning, "连接检查没有启动工作流任务。"));
            captures.Add(Capture(window, "workflow-live-initial.png"));

            var gptButton = Named<Button>(panel, "WorkflowLoopReconnectGpt");
            var codexButton = Named<Button>(panel, "WorkflowLoopConnectCodex");
            attempts.Add(await ClickConnectionAsync("GPT", gptButton, codexButton, panel, () => runtime.GptConnection));
            captures.Add(Capture(window, "workflow-live-after-gpt.png"));
            if (gptButton.IsEnabled && codexButton.IsEnabled)
                attempts.Add(await ClickConnectionAsync("Codex", codexButton, gptButton, panel, () => runtime.CodexConnection));
            else attempts.Add(new("Codex", false, false, false, 0, runtime.CodexConnection, "GPT连接操作尚未结束，未创建竞争连接。"));
            panel.Refresh(); await SettleAsync();
            finalGpt = runtime.GptConnection; finalCodex = runtime.CodexConnection;
            gptCandidates = runtime.GptCandidates.ToArray();
            checks.Add(new("SeparateGptStatusMatchesRuntime", Named<TextBlock>(panel, "WorkflowLoopGptState").Text.EndsWith(Status(finalGpt.State), StringComparison.Ordinal),
                "GPT显示取自其真实独立连接状态。"));
            checks.Add(new("SeparateCodexStatusMatchesRuntime", Named<TextBlock>(panel, "WorkflowLoopCodexState").Text.EndsWith(Status(finalCodex.State), StringComparison.Ordinal),
                "Codex显示取自其真实独立连接状态。"));
            checks.Add(new("NoTaskDispatchOrRecoveryMutation", !runtime.IsRunning && runtime.State == initialTask,
                "未点击开始、新建、编辑或恢复；产品任务状态保持原样。"));
            checks.Add(new("ConnectionButtonsGuarded", attempts.Count == 2 && attempts.All(value => value.Clicked && value.DuplicateGuardObserved),
                "每次真实连接尝试期间两个连接按钮同时禁用；没有主动创建重复连接。"));
            checks.Add(new("AttemptsWithinBound", attempts.Count == 2 && attempts.All(value => value.FinishedWithinBound),
                "每个连接按钮最多等待40秒；超时保留任务状态。"));
            connectionsPassed = finalGpt.State == WorkflowLinkState.Connected && finalCodex.State == WorkflowLinkState.Connected;
            checks.Add(new("RealGptConnected", finalGpt.State == WorkflowLinkState.Connected, Safe(finalGpt.Message)));
            checks.Add(new("RealCodexConnected", finalCodex.State == WorkflowLinkState.Connected, Safe(finalCodex.Message)));
            captures.Add(Capture(window, "workflow-live-final.png"));
        }
        catch (Exception error)
        {
            failure = error.GetType().Name;
            checks.Add(new("RunnerCompleted", false, "真实连接检查未完成：" + failure));
            if (runtime is not null) { finalGpt = runtime.GptConnection; finalCodex = runtime.CodexConnection; }
        }
        finally
        {
            window?.Close();
            if (runtime is not null)
            {
                try { await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20)); cleanup = "Completed"; }
                catch (Exception error) { cleanup = error.GetType().Name; checks.Add(new("OwnedConnectionCleanup", false, "仅本检查创建的连接未能确认清理完成：" + cleanup)); }
            }
        }
        var after = preserved.ToDictionary(name => name, name => Hash(Path.Combine(Paths.Data, name)));
        var unchanged = before.All(item => after[item.Key] == item.Value);
        checks.Add(new("ProductionStateBytesUnchanged", unchanged, "settings与loop-state主记录/备记录SHA前后比对，不写生产任务或启动设置。"));
        var passed = connectionsPassed && checks.All(value => value.Passed) && cleanup == "Completed";
        File.WriteAllText(reportPath, JsonSerializer.Serialize(new
        {
            passed, status = passed ? "completed" : "failed-or-blocked", startedAt = started, finishedAt = DateTimeOffset.UtcNow,
            realWpfControls = true, realProductPanel = true, realWorkflowRuntime = true, fullMainWindow = false,
            syntheticAi = false, externalUiAutomation = false, nativeDesktopInput = false, modelTasksDispatched = false,
            newOrCompletedTasksExecuted = false, productionDataRoot = Paths.Data, initialGpt, initialCodex, finalGpt, finalCodex, gptCandidates,
            initialTaskIdentity = initialTask is null ? null : new { initialTask.RunId, initialTask.Phase, initialTask.Round, initialTask.CurrentRequestId },
            attempts, checks, cleanup, error = failure, preservedStateHashesBefore = before, preservedStateHashesAfter = after, captures,
            note = "这是真实产品控件与连接检查；未启动自动循环，不能据此声称GPT建议/Codex执行/GPT复核循环已验收。"
        }, Options));
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")} workflow-live-connection-check: GPT={finalGpt?.State}, Codex={finalCodex?.State}; tasks not started; report={reportPath}");
        return passed ? 0 : 1;
    }

    static async Task<Attempt> ClickConnectionAsync(string provider, Button button, Button other, WorkflowLoopPanel panel,
        Func<WorkflowProviderConnection> readState)
    {
        if (!button.IsEnabled) return new(provider, false, false, false, 0, readState(), "按钮当前不可用，未触发连接。");
        var watch = Stopwatch.StartNew(); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
        var guarded = !button.IsEnabled && !other.IsEnabled;
        await SettleAsync();
        while ((!button.IsEnabled || !other.IsEnabled) && watch.Elapsed < TimeSpan.FromSeconds(40))
        { await Task.Delay(100); await SettleAsync(); }
        var settled = button.IsEnabled && other.IsEnabled && watch.Elapsed <= TimeSpan.FromSeconds(40);
        panel.Refresh(); await SettleAsync();
        return new(provider, true, guarded, settled, Math.Round(watch.Elapsed.TotalMilliseconds), readState(),
            settled ? "已通过产品连接按钮取得实际结果；没有启动任务。" : "本次连接未在40秒内结束，未重复连接或启动任务。");
    }
    static string Hash(string path)
    {
        if (!AutomationStore.IsSafeDPath(path)) throw new InvalidDataException("Protected state hash path is unsafe");
        if (!File.Exists(path)) return "MISSING";
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    static string Safe(string text) => WorkflowText.Safe(text, 400);
    static string Status(WorkflowLinkState value) => value switch
    { WorkflowLinkState.Connected => "已连接", WorkflowLinkState.Connecting => "连接中", WorkflowLinkState.Conflict => "连接冲突", WorkflowLinkState.Failed => "连接失败", _ => "未连接" };
    static T Named<T>(DependencyObject root, string name) where T : FrameworkElement
        => Descendants<T>(root).SingleOrDefault(value => value.Name == name) ?? throw new InvalidOperationException("Missing live product control: " + name);
    static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T value) yield return value;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
    static async Task SettleAsync() => await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    static string Capture(Window window, string name)
    {
        var width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth)); var height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
        var pixels = new byte[width * height * 4]; bitmap.CopyPixels(pixels, width * 4, 0);
        if (!Enumerable.Range(0, width * height).Any(index => pixels[index * 4 + 3] != 0)) throw new InvalidDataException("Live WPF capture is blank");
        var path = Path.Combine(OutputDirectory, name);
        if (!AutomationStore.IsSafeDPath(path)) throw new InvalidDataException("Live capture path is unsafe");
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write); encoder.Save(stream); return path;
    }
    sealed record Observation(string Name, bool Passed, string Detail);
    sealed record Attempt(string Provider, bool Clicked, bool DuplicateGuardObserved, bool FinishedWithinBound, double ElapsedMs,
        WorkflowProviderConnection State, string Message);
}

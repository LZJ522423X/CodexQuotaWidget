using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexQuotaWidget.Core;
using CodexQuotaWidget.Core.Automation;
using CodexQuotaWidget.Core.ControlCenter;

namespace CodexQuotaWidget;

/// <summary>Actual WPF controls with an in-memory fake controller. Never creates an AI connector or executor.</summary>
public static class WorkflowLoopCheckRunner
{
    const string PreviewDirectory = @"D:\CodexQuotaWidget-GitHub\artifacts\loop-ui-build\workflow-preview";
    static int assertions;

    public static async Task<int> RunAsync()
    {
        assertions = 0;
        if (!AutomationStore.IsSafeDPath(PreviewDirectory)) throw new InvalidDataException("Workflow preview requires a safe D directory");
        Directory.CreateDirectory(PreviewDirectory);
        var resultPath = Path.Combine(PreviewDirectory, "check-result.json");
        File.WriteAllText(resultPath, "{\"passed\":false,\"status\":\"running\",\"syntheticAi\":true}");
        var app = (App)Application.Current;
        var settings = new Settings { FollowCodex = false, EnableAnimations = false, Theme = "Dark", Width = 640, Height = 880 };
        app.ConfigurePreviewTheme(settings);
        var fake = new FixtureController();
        var panel = new WorkflowLoopPanel(fake);
        var scroller = new ScrollViewer { Content = panel, Margin = new Thickness(16), VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var window = new Window { Title = "自动工作流离线UI验收", Content = scroller, Width = 640, Height = 880, ShowInTaskbar = false, ShowActivated = false };
        window.SetResourceReference(Window.BackgroundProperty, "Glass");
        var captures = new List<string>();
        try
        {
            window.Show(); await SettleAsync();
            var start = Named<Button>(panel, "WorkflowLoopStart");
            var pause = Named<Button>(panel, "WorkflowLoopPause");
            var stop = Named<Button>(panel, "WorkflowLoopStop");
            var authorize = Named<CheckBox>(panel, "WorkflowLoopAuthorize");
            var reconnect = Named<Button>(panel, "WorkflowLoopReconnectGpt");
            var connectCodex = Named<Button>(panel, "WorkflowLoopConnectCodex");
            var create = Named<Button>(panel, "WorkflowLoopNewTask");
            var edit = Named<Button>(panel, "WorkflowLoopEditTask");
            var advanced = Named<Expander>(panel, "WorkflowLoopAdvanced");
            var connections = Named<Expander>(panel, "WorkflowLoopConnections");
            var historySection = Named<Expander>(panel, "WorkflowLoopHistorySection");
            Check(fake.StartCalls == 0 && fake.PauseCalls == 0 && fake.StopCalls == 0 && fake.RecoveryCalls == 0, "opening panel performs no model dispatch or recovery acknowledgement");
            Check(!start.IsEnabled && !pause.IsEnabled && create.IsEnabled, "empty task offers a new task entry and cannot start");
            Check(!advanced.IsExpanded && !Descendants<TextBox>(panel).Any(), "main page has no large task input and advanced details start collapsed");
            Check(create.IsVisible && !start.IsVisible && !pause.IsVisible && !edit.IsVisible,
                "empty task has a single visible new-task action, with no disabled start/pause clutter");
            Check(connections.IsExpanded && !historySection.IsExpanded, "first-time connection guidance opens while history remains separate and collapsed");
            Check(fake.HistoryCalls == 0, "collapsed advanced page does not repeatedly read history");
            Check(Named<TextBlock>(panel, "WorkflowLoopGptState").Text.Contains("未连接") && Named<TextBlock>(panel, "WorkflowLoopCodexState").Text.Contains("未连接"),
                "GPT and Codex initially expose separate disconnected states");
            Check(AutomationProperties.GetName(start).Contains("GPT") && AutomationProperties.GetName(pause).Contains("暂停"), "start and pause expose accessible names");
            Click(reconnect); await SettleAsync();
            Check(fake.GptConnectCalls == 1 && !reconnect.IsEnabled && !connectCodex.IsEnabled, "GPT button invokes connector logic and guards both buttons during an attempt");
            reconnect.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, reconnect)); panel.Refresh();
            Check(fake.GptConnectCalls == 1 && !reconnect.IsEnabled, "duplicate click and polling never start a competing GPT connection");
            Check(Named<TextBlock>(panel, "WorkflowLoopGptState").Text.Contains("连接中") && Named<TextBlock>(panel, "WorkflowLoopCodexState").Text.Contains("未连接"),
                "connecting GPT does not pretend Codex is connected");
            fake.FinishGptConnection(); await SettleAsync(); panel.Refresh();
            Click(connectCodex); await SettleAsync();
            Check(fake.CodexConnectCalls == 1 && Named<TextBlock>(panel, "WorkflowLoopCodexState").Text.Contains("已连接"), "Codex button calls its own connector and renders its result");
            Check(!connections.IsExpanded && Named<TextBlock>(panel, "WorkflowLoopGptState").IsVisible
                && Named<TextBlock>(panel, "WorkflowLoopCodexState").IsVisible, "ready providers collapse connection controls while both status badges remain visible");
            Check(!start.IsEnabled, "ready connectors alone cannot start an empty task");

            var task = "只验证本轮界面按钮；不启动真实模型。";
            panel.TaskEditor = (configuration, isNew) => Task.FromResult<WorkflowLoopConfiguration?>(configuration with {
                TaskId = Guid.NewGuid(), Goal = task, AuthorizedWritePaths = new[] { "ui-check-only.txt" },
                BuildProjectPath = "UiCheck.Build.csproj", ChecksProjectPath = "UiCheck.Checks.csproj", LocalExecutionAuthorized = true });
            Click(create); await SettleAsync();
            Check(fake.SaveCalls == 1 && fake.StartCalls == 0 && !fake.Configuration.LocalExecutionAuthorized, "new task saves a draft and strips dispatch authorization");
            Check(Named<TextBlock>(panel, "WorkflowLoopTaskSummary").Text == task && !start.IsEnabled && authorize.IsChecked == false,
                "saved draft shows a summary and requires a separate explicit local authorization");
            var savedConfiguration = fake.Configuration;
            fake.PresentConfiguration(savedConfiguration with { AuthorizedWritePaths = Array.Empty<string>() }); panel.Refresh(); authorize.IsChecked = true;
            Check(!start.IsEnabled && !authorize.IsEnabled, "empty write scope stays read-only even if authorization is assigned programmatically");
            fake.PresentConfiguration(savedConfiguration with { TaskId = Guid.Empty }); panel.Refresh(); authorize.IsChecked = true;
            Check(!start.IsEnabled && !authorize.IsEnabled, "legacy task without persistent identity cannot dispatch before explicit saving");
            fake.PresentConfiguration(savedConfiguration with { ChecksProjectPath = "" }); panel.Refresh(); authorize.IsChecked = true;
            Check(!start.IsEnabled && !authorize.IsEnabled, "missing validation project never falls back to a test project");
            fake.PresentConfiguration(savedConfiguration); panel.Refresh();
            authorize.IsChecked = true; await SettleAsync();
            Check(start.IsEnabled, "explicit authorization enables start only with a task identity, write scope and two ready providers");
            Check(start.IsVisible && !pause.IsVisible && edit.IsVisible, "saved task exposes start as its primary action with edit secondary");
            panel.TaskEditor = (_, _) => Task.FromResult<WorkflowLoopConfiguration?>(null);
            Click(edit); await SettleAsync();
            Check(fake.SaveCalls == 1 && fake.Configuration.Goal == task && fake.StartCalls == 0, "cancelling task editing preserves the saved draft and sends no task");
            fake.FailCodexConnection(); panel.Refresh();
            Check(!start.IsEnabled && Named<TextBlock>(panel, "WorkflowLoopCodexState").Text.Contains("连接失败")
                && Named<TextBlock>(panel, "WorkflowLoopGptState").Text.Contains("已连接"), "Codex connection failure does not overwrite the independent GPT status");
            Check(connections.IsExpanded && Named<TextBlock>(panel, "WorkflowLoopAnomaly").Visibility == Visibility.Visible,
                "new connection failure reveals recovery controls and leaves its explanation visible");
            Click(connectCodex); await SettleAsync(); panel.Refresh();
            Click(start); await SettleAsync();
            Check(fake.StartCalls == 1 && fake.LastConfiguration is { } submitted && submitted.Goal == task && submitted.LocalExecutionAuthorized,
                "start forwards explicit edited goal and authorization only to fake controller");
            Check(!start.IsEnabled && pause.IsEnabled && !create.IsEnabled && !edit.IsEnabled, "active fake run blocks duplicate start and task editing");
            Check(pause.IsVisible && stop.IsVisible && !start.IsVisible && !create.IsVisible && !edit.IsVisible,
                "running task exposes pause and stop while start and task replacement are hidden");
            Check(Named<TextBlock>(panel, "WorkflowLoopStatus").Text.Contains("运行中")
                && Named<TextBlock>(panel, "WorkflowLoopCompletedRounds").Text == "已完成 0 轮", "current round one is never presented as one completed round");
            Check(!reconnect.IsEnabled && !connectCodex.IsEnabled, "active fake run cannot replace its provider connections");
            panel.Refresh();
            Check(fake.StartCalls == 1, "poll refresh does not dispatch another task");
            Click(pause); await SettleAsync();
            Check(fake.PauseCalls == 1 && !fake.IsRunning && fake.State.NeedsRecoveryConfirmation, "pause leaves uncertain fake request behind explicit recovery gate");
            Check(!start.IsEnabled && !pause.IsEnabled, "unconfirmed interrupted request cannot be restarted from UI");
            var recover = Named<Button>(panel, "WorkflowLoopRecover");
            Check(recover.Visibility == Visibility.Visible, "interrupted delivery exposes explicit acknowledgement");
            Check(!start.IsVisible && !create.IsVisible && !edit.IsVisible && !pause.IsVisible,
                "unconfirmed delivery keeps recovery visible without competing start or replacement actions");
            Check(!recover.IsVisible && Named<TextBlock>(panel, "WorkflowLoopStatus").Text.Contains("已阻断"),
                "unconfirmed delivery stays blocked and keeps recovery evidence inside advanced details");
            advanced.IsExpanded = true; await SettleAsync(); Click(recover); await SettleAsync();
            Check(fake.RecoveryCalls == 1 && !fake.State.NeedsRecoveryConfirmation && start.IsEnabled, "recovery acknowledgement unlocks start without replaying prior request");
            Check(fake.StartCalls == 1, "recovery acknowledgement itself sends no task");

            fake.PresentConflict(); panel.Refresh(); await SettleAsync();
            var choices = Named<ComboBox>(panel, "WorkflowLoopGptCandidates");
            var choose = Named<Button>(panel, "WorkflowLoopChooseGpt");
            Check(choices.SelectedItem is null && choices.Items.Count == 2 && !choose.IsEnabled, "conflict excludes unsafe candidate and never auto-selects an eligible one");
            Check(Named<TextBlock>(panel, "WorkflowLoopGptState").Text.Contains("连接冲突") && Named<TextBlock>(panel, "WorkflowLoopAnomaly").Text.Contains("多个桌面连接"),
                "desktop_pipe_not_unique exposes a precise conflict rather than a network failure");
            Check(connections.IsExpanded && choices.IsVisible, "new multi-session conflict offers reconnection and advanced verified candidate selection");
            connections.IsExpanded = false; panel.Refresh();
            Check(!connections.IsExpanded, "polling respects manual collapse of an unchanged connection problem");
            connections.IsExpanded = true; await SettleAsync();
            choices.SelectedIndex = 1; await SettleAsync(); Click(choose); await SettleAsync();
            Check(fake.LastCandidateId == "fixture-b" && fake.GptConnectCalls == 2, "explicit candidate selection passes exactly its verified identity to reconnect");
            fake.FinishGptConnection(); await SettleAsync(); panel.Refresh();
            fake.Present("completed"); panel.Refresh();
            Check(Named<TextBlock>(panel, "WorkflowLoopTaskSummary").Text == task && !start.IsEnabled && edit.Visibility == Visibility.Collapsed
                && Named<TextBlock>(panel, "WorkflowLoopStatus").Text.Contains("已完成"), "completed task preserves its summary and cannot be replayed by start");
            advanced.IsExpanded = true; await SettleAsync(); panel.Refresh();
            Check(Descendants<TextBlock>(panel).Any(block => block.Text.Contains("不会自动重发")), "safety limits and no-replay explanation remain available in advanced details");
            Check(fake.HistoryCalls == 0 && !historySection.IsExpanded, "opening technical diagnostics does not load or expand task history");
            historySection.IsExpanded = true; await SettleAsync(); panel.Refresh();
            var history = Named<ListBox>(panel, "WorkflowLoopHistory");
            Check(history.Items.Count == 1 && fake.HistoryCalls > 0, "completed history remains accessible through explicit advanced disclosure");
            history.SelectedIndex = 0; await SettleAsync();
            Check(Descendants<TextBlock>(panel).Any(block => block.Text.Contains("完整目标") && block.Text.Contains(task)), "history selection reveals full read-only task details");
            Check(fake.StartCalls == 1, "opening completed history never replays the task");
            advanced.IsExpanded = false; historySection.IsExpanded = false;

            var freshEditor = new TaskEditorDialog(fake.Configuration, true) { Owner = window, ShowActivated = false };
            freshEditor.Show(); await SettleAsync();
            Check(Named<TextBox>(freshEditor, "WorkflowTaskGoal").Text.Length == 0, "new task dialog never preloads a completed goal for accidental replay");
            Check(Named<TextBox>(freshEditor, "WorkflowTaskWritePaths").Text.Length == 0,
                "new task never inherits broad or previous write scope");
            Named<TextBox>(freshEditor, "WorkflowTaskGoal").Text = "离线新草稿";
            Check(freshEditor.TrySaveDraft() && freshEditor.Result is { } freshDraft && freshDraft.TaskId != Guid.Empty
                && freshDraft.TaskId != fake.Configuration.TaskId && freshDraft.AuthorizedWritePaths.Length == 0 && !freshDraft.LocalExecutionAuthorized,
                "saving an empty-scope new task creates a distinct read-only identity without authorization");
            freshEditor.Close();

            var missingSessionEditor = new TaskEditorDialog(fake.Configuration with { ChatGptThreadId = "", LocalExecutionAuthorized = true }, false)
                { Owner = window, ShowActivated = false };
            missingSessionEditor.Show(); await SettleAsync();
            Check(Named<TextBox>(missingSessionEditor, "WorkflowTaskProject").IsVisible
                && Named<Button>(missingSessionEditor, "WorkflowTaskBrowseProject").IsVisible,
                "task project and native folder-picker entry are visible outside advanced session settings");
            Check(!missingSessionEditor.TrySaveDraft() && missingSessionEditor.Result is null, "missing required session cannot create a task draft");
            await SettleAsync();
            Check(Named<Expander>(missingSessionEditor, "WorkflowTaskAdvanced").IsExpanded
                && Named<TextBox>(missingSessionEditor, "WorkflowTaskGptSession").IsVisible
                && Named<TextBlock>(missingSessionEditor, "WorkflowTaskFeedback").Text.Contains("GPT"),
                "missing session reveals the exact field and a specific inline error");
            Named<TextBox>(missingSessionEditor, "WorkflowTaskGptSession").Text = fake.Configuration.ChatGptThreadId;
            Check(missingSessionEditor.TrySaveDraft() && missingSessionEditor.Result is { LocalExecutionAuthorized: false } savedDraft
                && savedDraft.Goal == fake.Configuration.Goal && savedDraft.ProjectRoot == fake.Configuration.ProjectRoot
                && savedDraft.CallerThreadId == fake.Configuration.CallerThreadId && savedDraft.TaskId == fake.Configuration.TaskId
                && savedDraft.AuthorizedWritePaths.SequenceEqual(fake.Configuration.AuthorizedWritePaths),
                "editing preserves task identity and its explicit scope while stripping execution authorization");
            missingSessionEditor.Close();

            var legacyEditor = new TaskEditorDialog(fake.Configuration with { TaskId = Guid.Empty, AuthorizedWritePaths = Array.Empty<string>() }, false)
                { Owner = window, ShowActivated = false };
            legacyEditor.Show(); await SettleAsync();
            Named<TextBox>(legacyEditor, "WorkflowTaskWritePaths").Text = "..\\outside.txt";
            Check(!legacyEditor.TrySaveDraft() && legacyEditor.Result is null, "task editor rejects a write path outside the project");
            Named<TextBox>(legacyEditor, "WorkflowTaskWritePaths").Text = "src\\Explicit.cs";
            Check(legacyEditor.TrySaveDraft() && legacyEditor.Result is { } legacyDraft && legacyDraft.TaskId != Guid.Empty
                && legacyDraft.AuthorizedWritePaths.SequenceEqual(new[] { "src/Explicit.cs" }) && !legacyDraft.LocalExecutionAuthorized,
                "legacy task receives an identity only after its scope is explicitly saved");
            Named<TextBox>(legacyEditor, "WorkflowTaskWritePaths").Text = "src/Scoped/";
            Check(legacyEditor.TrySaveDraft() && legacyEditor.Result is { } directoryDraft
                && directoryDraft.AuthorizedWritePaths.SequenceEqual(new[] { "src/Scoped/" }),
                "task editor preserves an explicitly entered directory subtree grant");
            legacyEditor.Close();

            var missingProjectEditor = new TaskEditorDialog(fake.Configuration with { ProjectRoot = "" }, false) { Owner = window, ShowActivated = false };
            missingProjectEditor.Show(); await SettleAsync();
            Check(!missingProjectEditor.TrySaveDraft() && !Named<Expander>(missingProjectEditor, "WorkflowTaskAdvanced").IsExpanded
                && Named<TextBlock>(missingProjectEditor, "WorkflowTaskFeedback").Text.Contains("项目"),
                "missing project points to its basic field without revealing unrelated session settings");
            missingProjectEditor.Close();

            foreach (var theme in new[] { "Dark", "Light" })
            {
                settings.Theme = theme; app.ConfigurePreviewTheme(settings);
                var editor = new TaskEditorDialog(fake.Configuration, false) { Owner = window, ShowActivated = false };
                editor.Show(); await SettleAsync();
                Check(Named<TextBox>(editor, "WorkflowTaskGoal").Text == task, "task modal preserves the complete goal for editing");
                Check(!Named<Expander>(editor, "WorkflowTaskAdvanced").IsExpanded && Named<Button>(editor, "WorkflowTaskSave").IsDefault,
                    "modal keeps connection fields advanced and supports keyboard save");
                Check(AutomationProperties.GetName(Named<TextBox>(editor, "WorkflowTaskGoal")) == "任务目标", "task editor has a visible label and accessible name");
                captures.Add(Capture(editor, $"workflow-{theme.ToLowerInvariant()}-task-editor.png")); editor.Close();
            }

            foreach (var theme in new[] { "Dark", "Light" })
            {
                settings.Theme = theme; app.ConfigurePreviewTheme(settings);
                foreach (var width in new[] { 400d, 640d })
                {
                    window.Width = width;
                    foreach (var name in new[] { "idle", "executing", "recovery", "completed" })
                    {
                        fake.Present(name); panel.Refresh(); if (name == "idle") authorize.IsChecked = true;
                        scroller.ScrollToTop(); await SettleAsync();
                        Check(panel.ActualWidth <= scroller.ViewportWidth + 1, $"{theme}/{width}/{name} panel fits available width");
                        Check(Descendants<TextBlock>(panel).Where(block => block.Parent is StackPanel).All(block => block.TextWrapping == TextWrapping.Wrap),
                            $"{theme}/{width}/{name} messages remain wrapped");
                        Check(name == "executing" ? !start.IsEnabled && pause.IsEnabled : name is "recovery" or "completed" ? !start.IsEnabled && !pause.IsEnabled : start.IsEnabled && !pause.IsEnabled,
                            $"{theme}/{width}/{name} buttons reflect observed fixture phase");
                        captures.Add(Capture(window, $"workflow-{theme.ToLowerInvariant()}-{width:0}-{name}.png"));
                    }
                }
            }
            foreach (var name in new[] { "paused", "blocked", "stopped" })
            {
                fake.Present(name); panel.Refresh(); await SettleAsync();
                var expected = name switch { "paused" => "已暂停", "blocked" => "已阻断", _ => "已停止" };
                Check(Named<TextBlock>(panel, "WorkflowLoopStatus").Text.Contains(expected)
                    && Named<TextBlock>(panel, "WorkflowLoopCompletedRounds").Text == "已完成 1 轮",
                    "persisted completed count and " + name + " status are shown independently of current round");
                captures.Add(Capture(window, "workflow-light-640-" + name + ".png"));
            }
            fake.Present("executing"); panel.Refresh(); await SettleAsync(); Click(stop); await SettleAsync();
            Check(fake.StopCalls == 1 && !fake.IsRunning && fake.State.Phase == WorkflowLoopPhase.Stopped
                && Named<TextBlock>(panel, "WorkflowLoopStatus").Text.Contains("已停止") && authorize.IsChecked == false
                && !start.IsVisible && !start.IsEnabled && !edit.IsVisible && create.IsVisible,
                "stop calls the dedicated controller operation and removes local execution authorization");
            Check(fake.StartCalls == 1 && fake.PauseCalls == 1 && fake.RecoveryCalls == 1, "rendering all phases never triggers additional operations");
            Check(fake.ModelCalls == 0 && fake.NetworkCalls == 0 && fake.ProcessCloseCalls == 0 && fake.PowerCalls == 0,
                "fixture has no real model, networking, process closure, or power action implementation");
            File.WriteAllText(resultPath, JsonSerializer.Serialize(new
            {
                passed = true, assertions, renders = captures.Count, realWpfControls = true, syntheticAi = true,
                realModelCalls = false, networkActions = false, processClosure = false, powerActions = false, captures
            }, new JsonSerializerOptions { WriteIndented = true }));
            assertions += await SiwcUiChecks.RunAsync(window);
            File.WriteAllText(Path.Combine(PreviewDirectory, "siwc-extended-result.json"), JsonSerializer.Serialize(new { passed = true, assertions, realWpf = true, syntheticAi = true }));
            Console.WriteLine($"PASS workflow-check: {assertions} assertions, {captures.Count} renders; fake controller, no models/network/process/power actions");
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(resultPath, JsonSerializer.Serialize(new { passed = false, assertions, error = error.GetType().Name, syntheticAi = true }));
            throw;
        }
        finally { window.Close(); }
    }

    static T Named<T>(DependencyObject root, string name) where T : FrameworkElement
        => Descendants<T>(root).SingleOrDefault(item => item.Name == name) ?? throw new InvalidOperationException("Missing workflow control " + name);
    static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var visited = new HashSet<DependencyObject>(); var pending = new Stack<DependencyObject>(); pending.Push(root);
        while (pending.Count > 0)
        {
            var item = pending.Pop(); if (!visited.Add(item)) continue;
            if (item is T match) yield return match;
            foreach (var child in LogicalTreeHelper.GetChildren(item).OfType<DependencyObject>()) pending.Push(child);
            if (item is Visual || item is System.Windows.Media.Media3D.Visual3D)
                for (var index = 0; index < VisualTreeHelper.GetChildrenCount(item); index++) pending.Push(VisualTreeHelper.GetChild(item, index));
        }
    }
    static void Click(Button button)
    {
        Check(button.IsEnabled, "clicked control is enabled");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
    }
    static async Task SettleAsync() => await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    static string Capture(Window window, string name)
    {
        var width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth)); var height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
        var pixels = new byte[width * height * 4]; bitmap.CopyPixels(pixels, width * 4, 0);
        Check(Enumerable.Range(0, width * height).Any(index => pixels[index * 4 + 3] != 0), "nonblank workflow render " + name);
        var path = Path.Combine(PreviewDirectory, name); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write); encoder.Save(stream); return path;
    }
    static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException("Workflow UI: " + description);
        assertions++; Console.WriteLine("  PASS " + description);
    }

    sealed class FixtureController : IWorkflowLoopUiController
    {
        public WorkflowLoopConfiguration Configuration { get; private set; } = new()
        {
            Goal = "", ProjectRoot = @"D:\CodexQuotaWidget-GitHub",
            ChatGptThreadId = "00000000-0000-0000-0000-000000000001", CallerThreadId = "00000000-0000-0000-0000-000000000002"
        };
        public WorkflowLoopState State { get; private set; } = new();
        public bool IsRunning { get; private set; }
        public string ConnectionDescription => "离线fixture；未创建真实GPT/Codex连接。";
        public WorkflowProviderConnection GptConnection { get; private set; } = new(WorkflowLinkState.Disconnected, "离线fixture");
        public WorkflowProviderConnection CodexConnection { get; private set; } = new(WorkflowLinkState.Disconnected, "离线fixture");
        public IReadOnlyList<WorkflowConnectionCandidate> GptCandidates { get; private set; } = Array.Empty<WorkflowConnectionCandidate>();
        public int StartCalls { get; private set; }
        public int PauseCalls { get; private set; }
        public int StopCalls { get; private set; }
        public int RecoveryCalls { get; private set; }
        public int SaveCalls { get; private set; }
        public int GptConnectCalls { get; private set; }
        public int CodexConnectCalls { get; private set; }
        public int HistoryCalls { get; private set; }
        public string? LastCandidateId { get; private set; }
        public int ModelCalls => 0;
        public int NetworkCalls => 0;
        public int ProcessCloseCalls => 0;
        public int PowerCalls => 0;
        public WorkflowLoopConfiguration? LastConfiguration { get; private set; }
        TaskCompletionSource<string>? completion;
        TaskCompletionSource<string>? connectionCompletion;
        public Task<string> ReconnectGptAsync(string? candidateId = null)
        {
            GptConnectCalls++; LastCandidateId = candidateId; GptConnection = new(WorkflowLinkState.Connecting, "模拟身份核验");
            connectionCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously); return connectionCompletion.Task;
        }
        public void FinishGptConnection()
        {
            GptConnection = new(WorkflowLinkState.Connected, "模拟连接就绪"); GptCandidates = Array.Empty<WorkflowConnectionCandidate>();
            connectionCompletion?.TrySetResult("模拟GPT连接已核验；没有创建真实连接。");
        }
        public Task<string> ConnectCodexAsync()
        {
            CodexConnectCalls++; CodexConnection = new(WorkflowLinkState.Connected, "模拟官方连接就绪"); return Task.FromResult("模拟Codex已连接。");
        }
        public void FailCodexConnection() => CodexConnection = new(WorkflowLinkState.Failed, "模拟身份核验失败", "identity_invalid");
        public Task<string> SaveTaskAsync(WorkflowLoopConfiguration configuration)
        { SaveCalls++; Configuration = configuration; return Task.FromResult("模拟草稿已保存，没有派发任务。"); }
        public void PresentConfiguration(WorkflowLoopConfiguration configuration) => Configuration = configuration;
        public IReadOnlyList<WorkflowLoopState> ReadHistory()
        { HistoryCalls++; return State.Phase == WorkflowLoopPhase.Completed ? new[] { State } : Array.Empty<WorkflowLoopState>(); }
        public void PresentConflict()
        {
            GptConnection = new(WorkflowLinkState.Conflict, "模拟多个桌面通道", "desktop_pipe_not_unique");
            GptCandidates = new[] { new WorkflowConnectionCandidate("fixture-a", "已验证会话 A", true),
                new WorkflowConnectionCandidate("fixture-b", "已验证会话 B", true), new WorkflowConnectionCandidate("unsafe", "未验证身份", false) };
        }
        public Task<string> StartAsync(WorkflowLoopConfiguration configuration)
        {
            StartCalls++; LastConfiguration = configuration; IsRunning = true;
            State = new() { Phase = WorkflowLoopPhase.Executing, Goal = configuration.Goal, ProjectRoot = configuration.ProjectRoot,
                RunId = configuration.TaskId, Round = 1, Checkpoint = WorkflowCheckpoint.Execution, CompletedRounds = 0, Message = "模拟执行等待；未调用模型。" };
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously); return completion.Task;
        }
        public void Pause()
        {
            PauseCalls++; IsRunning = false;
            State = State with { Phase = WorkflowLoopPhase.Interrupted, NeedsRecoveryConfirmation = true, Message = "模拟外部请求结果未确认；禁止自动重发。" };
            completion?.TrySetResult("模拟暂停已保存；未取消或关闭真实程序。");
        }
        public string AcknowledgeRecovery()
        {
            RecoveryCalls++; State = State with { Phase = WorkflowLoopPhase.Paused, NeedsRecoveryConfirmation = false, Message = "模拟恢复确认；没有重发任何任务。" };
            return State.Message;
        }
        public void Stop()
        {
            StopCalls++; IsRunning = false;
            State = State with { Phase = WorkflowLoopPhase.Stopped, NeedsRecoveryConfirmation = false, Message = "模拟停止并保留进度；未操作真实程序。" };
            completion?.TrySetResult(State.Message);
        }
        public void Present(string name)
        {
            IsRunning = name == "executing";
            State = new()
            {
                Phase = name switch { "executing" => WorkflowLoopPhase.Executing, "recovery" => WorkflowLoopPhase.Interrupted,
                    "paused" => WorkflowLoopPhase.Paused, "blocked" => WorkflowLoopPhase.Blocked, "stopped" => WorkflowLoopPhase.Stopped,
                    "completed" => WorkflowLoopPhase.Completed, _ => WorkflowLoopPhase.Idle },
                RunId = Configuration.TaskId, Goal = Configuration.Goal, ProjectRoot = Configuration.ProjectRoot,
                Round = name == "idle" ? 0 : name is "paused" or "blocked" or "stopped" ? 3 : 1, NeedsRecoveryConfirmation = name == "recovery",
                CompletedRounds = name is "paused" or "blocked" or "stopped" or "completed" ? 1 : 0,
                Checkpoint = name == "completed" ? WorkflowCheckpoint.Completed : WorkflowCheckpoint.Execution,
                Message = name switch { "executing" => "模拟等待匹配的本轮Turn；不是根据窗口或进程推断AI工作。",
                    "recovery" => "模拟上轮请求中断，结果未知；明确核验前不自动重发。",
                    "completed" => "模拟本轮完成；不能当作真实GPT/Codex验收通过。", _ => "尚未运行；连接来源核验不代表AI正在工作。" },
                ValidationReport = name == "completed" ? "模拟验证通过；真实模型执行未验证。" : "",
                Review = name == "completed" ? "ACC_REVIEW: COMPLETE\n离线fixture结果。" : ""
            };
        }
    }
}

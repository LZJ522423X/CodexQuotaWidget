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

namespace CodexQuotaWidget;

/// <summary>UI fixture only: no observer, executor, networking, process closure, service control or power implementation is instantiated.</summary>
internal static class ReliabilityCheckRunner
{
    const string PreviewDirectory = @"D:\CodexQuotaWidget-GitHub\artifacts\reliability-preview";
    static int assertions;
    public static async Task<int> RunAsync()
    {
        assertions = 0; Directory.CreateDirectory(PreviewDirectory);
        File.WriteAllText(Path.Combine(PreviewDirectory, "check-result.json"), "{\"passed\":false,\"status\":\"running\"}");
        var settings = new Settings { FollowCodex = false, EnableAnimations = false, Theme = "Dark", Width = 640, Height = 800 };
        var app = (App)Application.Current; app.ConfigurePreviewTheme(settings);
        var controller = new FixtureController(); var panel = new ReliabilityPanel(controller);
        var scroller = new ScrollViewer { Content = panel, Margin = new Thickness(16), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var window = new Window { Title = "可靠性与恢复离线检查", Content = scroller, Width = 640, Height = 800, ShowInTaskbar = false };
        window.SetResourceReference(Window.BackgroundProperty, "Glass");
        var captures = new List<string>();
        try
        {
            window.Show(); await SettleAsync();
            Check(Control<TextBlock>(panel, "ReliabilityCodexState").Text.Contains("未知"), "unknown is displayed without inferring execution from process presence");
            Check(controller.ContinueCalls == 0 && controller.LogCalls == 0 && controller.DiagnosticsCalls == 0, "opening recovery UI performs no operations");
            Check(!Control<Button>(panel, "ReliabilityContinue").IsEnabled, "continue is disabled when no recovery issue exists");
            Check(AutomationProperties.GetName(Control<Button>(panel, "ReliabilityDiagnostics")).Contains("不关闭程序"), "diagnostics exposes its dry-run safety boundary accessibly");
            Check(Control<Button>(panel, "ReliabilityDiagnostics").Content!.ToString()!.Contains("Dry Run"), "diagnostics default is visibly dry run");

            controller.State = controller.State with
            {
                RecoveryNeedsAttention = true, RecoverySummary = "发现需要确认的恢复状态。",
                RecoveryDetails = "上次额度小组件未记录正常退出；无法据此判断 Codex 是否异常退出。\n有2项未完成本地任务。\n发现未完成收尾记录；不会自动重新执行。"
            };
            panel.RefreshState();
            Check(Control<Button>(panel, "ReliabilityContinue").IsEnabled, "startup recovery issue enables explicit context acknowledgement");
            Check(Control<TextBlock>(panel, "ReliabilityRecoveryDetail").Text.Contains("无法据此判断 Codex"), "recovery display distinguishes widget failure from Codex failure");
            Click(panel, "ReliabilityContinue");
            Check(controller.ContinueCalls == 1 && controller.State.RecoveryAcknowledged, "continue invokes only acknowledgement");
            Check(!Control<Button>(panel, "ReliabilityContinue").IsEnabled, "acknowledged recovery cannot be repeatedly continued");
            Check(controller.TaskDispatchCalls == 0 && controller.ExecutorCalls == 0 && controller.PowerCalls == 0, "recovery neither dispatches a task nor replays executor or power actions");
            Click(panel, "ReliabilityLogs");
            Check(controller.LogCalls == 1 && Control<TextBlock>(panel, "ReliabilityFeedback").Text.Contains(@"D:\CodexQuotaWidgetData\logs"), "logs uses only the fixed local path callback");

            controller.State = controller.State with { CodexState = "未知", StateSource = "网络断开；本地观察证据不足", StateDetail = "无法确认 Codex 是否仍在执行任务。" };
            panel.RefreshState();
            Check(Control<TextBlock>(panel, "ReliabilityCodexState").Text.Contains("未知") && Control<TextBlock>(panel, "ReliabilityStateSource").Text.Contains("网络断开"), "network-unavailable evidence remains unknown rather than completed or exhausted");
            Click(panel, "ReliabilityDiagnostics");
            Check(panel.BusyForCheck && controller.DiagnosticsCalls == 1, "diagnostics starts one asynchronous dry-run callback");
            Check(!Control<Button>(panel, "ReliabilityDiagnostics").IsEnabled && !Control<Button>(panel, "ReliabilityLogs").IsEnabled && !Control<Button>(panel, "ReliabilityContinue").IsEnabled,
                "diagnostics blocks concurrent clicks and recovery changes");
            controller.State = controller.State with { CodexState = "运行中", StateSource = "可信 session 事件", StateDetail = "观察到任务开始事件。", ObservedAt = "2026-10-03T15:00:00Z" };
            panel.RefreshState();
            Check(panel.BusyForCheck && Control<TextBlock>(panel, "ReliabilityCodexState").Text.Contains("运行中"), "live state refresh does not cancel or restart diagnostics");
            controller.Completion.SetResult("真实只读：目标识别和恢复文件校验通过。\n模拟通过：额度异常、计划解析、独立 Executor。\n未验证：真实关闭服务与电源操作。\nDry Run：没有真实关闭或关机。");
            await SettleAsync();
            Check(!panel.BusyForCheck && Control<Button>(panel, "ReliabilityDiagnostics").IsEnabled, "completed diagnostics restores availability");
            Check(Control<TextBlock>(panel, "ReliabilityFeedback").Text.Contains("模拟通过") && Control<TextBlock>(panel, "ReliabilityFeedback").Text.Contains("未验证"), "diagnostic evidence types remain visibly distinct");
            Check(controller.PowerCalls == 0 && controller.ProcessCloseCalls == 0 && controller.ServiceStopCalls == 0, "the fixture has no destructive operation calls");

            var disconnected = new ReliabilityPanel(null);
            Check(!Control<Button>(disconnected, "ReliabilityDiagnostics").IsEnabled && !Control<Button>(disconnected, "ReliabilityLogs").IsEnabled, "disconnected previews cannot test or open paths");
            disconnected.Dispose();
            controller.ThrowOnRead = true; panel.RefreshState();
            Check(Control<TextBlock>(panel, "ReliabilityCodexState").Text.Contains("未知") && Control<TextBlock>(panel, "ReliabilityStateSource").Text.Contains("读取失败"), "observer exceptions revert display to unknown");
            controller.ThrowOnRead = false;
            controller.State = controller.State with { RecoveryAcknowledged = false, RecoveryNeedsAttention = true, CodexState = "未知",
                StateSource = "额度异常；未获得一致的任务状态", StateDetail = "异常额度不能确认任务停止，自动收尾未触发。" };
            panel.RefreshState();

            panel.ShowStateDiagnostics(); await SettleAsync();
            var binding = panel.BindingPanelForCheck;
            var project = BindingControl<TextBox>(binding, "BindingProjectInput");
            var selector = BindingControl<ComboBox>(binding, "BindingSessions");
            Check(controller.CatalogCalls == 0 && controller.BindingSaveCalls == 0 && !BindingControl<Button>(binding, "BindingSave").IsEnabled,
                "opening diagnostics reads saved metadata but never scans or selects a session");
            project.Text = @"D:\CodexQuotaWidget-GitHub";
            Check(controller.CatalogCalls == 0 && selector.SelectedItem is null, "entering project alone does not find or bind the newest session");
            var choiceA = new SessionChoiceUi("session-a", @"C:\Users\FixtureUser\.codex\sessions\2026\10\04\rollout-session-a.jsonl", project.Text, "2026-10-04T00:30:00+08:00", "task_started");
            var choiceB = new SessionChoiceUi("session-b", @"C:\Users\FixtureUser\.codex\sessions\2026\10\04\rollout-session-b.jsonl", project.Text, "2026-10-04T00:35:00+08:00", "task_complete");
            BindingClick(binding, "BindingRefresh");
            Check(binding.BusyForCheck && controller.CatalogCalls == 1 && !project.IsEnabled && !BindingControl<Button>(binding, "BindingRefresh").IsEnabled,
                "catalog refresh blocks duplicate clicks and project edits");
            Check(!Control<Button>(panel, "ReliabilityDiagnostics").IsEnabled, "catalog and dry diagnostics cannot start concurrently");
            controller.CatalogCompletion.SetResult(new(true, "读取2个项目 Session。", new[] { choiceA, choiceB })); await SettleAsync();
            Check(!binding.BusyForCheck && selector.Items.Count == 2 && selector.SelectedIndex == -1 && controller.BindingSaveCalls == 0,
                "catalog success does not automatically select newest or oldest session");
            selector.SelectedIndex = 0;
            Check(BindingControl<TextBlock>(binding, "BindingSelectedMetadata").Text.Contains(choiceA.MetadataPath)
                && BindingControl<TextBlock>(binding, "BindingSelectedMetadata").Text.Contains(choiceA.LastActivity), "manual selection displays real candidate metadata path and activity time");
            BindingClick(binding, "BindingSave");
            Check(controller.BindingSaveCalls == 1 && controller.Binding.SessionId == "session-a" && controller.LastSavedSessionId == "session-a",
                "save submits only the explicitly selected session ID with the project");
            selector.SelectedIndex = 1; panel.RefreshState();
            Check((selector.SelectedItem as SessionChoiceUi)?.SessionId == "session-b" && controller.Binding.SessionId == "session-a",
                "poll refresh preserves manual session draft without silently switching saved binding");
            controller.RejectSave = true; BindingClick(binding, "BindingSave");
            Check(controller.Binding.SessionId == "session-a" && (selector.SelectedItem as SessionChoiceUi)?.SessionId == "session-b"
                && BindingControl<TextBlock>(binding, "BindingFeedback").Text.Contains("原绑定保留"), "failed save keeps stable binding and explicit selection draft");
            controller.RejectSave = false; BindingClick(binding, "BindingSave");
            Check(controller.Binding.SessionId == "session-b", "changing session requires another explicit successful save");
            project.Text = @"D:\AnotherProject"; panel.RefreshState();
            Check(project.Text == @"D:\AnotherProject" && controller.Binding.ProjectRoot == choiceA.ProjectRoot && !BindingControl<Button>(binding, "BindingSave").IsEnabled,
                "polling never replaces unsaved project input or binds stale catalog to a new project");
            project.Text = choiceA.ProjectRoot; BindingClick(binding, "BindingRefresh");
            controller.CatalogCompletion.SetResult(new(true, "没有候选 Session。", Array.Empty<SessionChoiceUi>())); await SettleAsync();
            Check(selector.Items.Count == 0 && !BindingControl<Button>(binding, "BindingSave").IsEnabled && controller.Binding.SessionId == "session-b",
                "empty catalog cannot save and does not clear an existing binding");
            BindingClick(binding, "BindingRefresh"); controller.CatalogCompletion.SetResult(new(false, "本地日志暂不可读取。", Array.Empty<SessionChoiceUi>())); await SettleAsync();
            Check(controller.Binding.SessionId == "session-b" && BindingControl<TextBlock>(binding, "BindingFeedback").Text.Contains("暂不可读取"),
                "disconnected catalog leaves prior stable binding unchanged");
            controller.State = controller.State with { CodexState = "Unknown", SessionId = "session-b", TurnId = "turn-b", BindingProject = choiceA.ProjectRoot,
                LastActivity = choiceB.LastActivity, LastEvent = "task_complete", TrustLevel = "Unknown", UnknownReason = "绑定日志暂不可读；没有任务停止确认。",
                ProjectMatchDescription = "不一致：原日志项目声明与当前绑定项目不匹配。" };
            panel.RefreshState();
            Check(DiagnosticControl(panel, "SessionDiagnosticSession").Text.Contains("session-b") && DiagnosticControl(panel, "SessionDiagnosticUnknownReason").Text.Contains("任务停止确认"),
                "unknown state keeps bound session identity and specific evidence failure reason");
            Check(DiagnosticControl(panel, "SessionDiagnosticProjectMatch").Text.Contains("不一致") && DiagnosticControl(panel, "SessionDiagnosticTrust").Text.Contains("Unknown"),
                "diagnostics exposes project declaration mismatch and trust level rather than hiding them");
            controller.ThrowOnRead = true; panel.RefreshState();
            Check(DiagnosticControl(panel, "SessionDiagnosticSession").Text.Contains("session-b") && DiagnosticControl(panel, "SessionDiagnosticState").Text.Contains("Unknown"),
                "read exception retains last known binding while rendering unknown");
            controller.ThrowOnRead = false;
            foreach (var stateName in new[] { "Working", "Waiting", "Completed", "Failed", "Exhausted", "Unknown" })
            {
                controller.State = controller.State with { CodexState = stateName }; panel.RefreshState();
                Check(DiagnosticControl(panel, "SessionDiagnosticState").Text.Contains(stateName), "state has a visible Chinese explanation: " + stateName);
            }
            controller.State = controller.State with { EvidenceDescription = "4 · LifecycleConfirmed · task_complete", EventSequence = "18",
                StateTimeline = "#16 Working\n#17 Waiting\n#18 Completed", AgentAcknowledgement = "external-agent 未经认证地声明已接收，不授权动作。" };
            panel.RefreshState();
            Check(DiagnosticControl(panel, "SessionDiagnosticEvidence").Text.Contains("LifecycleConfirmed")
                && DiagnosticControl(panel, "SessionDiagnosticSequence").Text.Contains("18"), "evidence and persistent sequence are displayed independently");
            Check(DiagnosticControl(panel, "SessionDiagnosticTimeline").Text.Contains("#17 Waiting")
                && DiagnosticControl(panel, "SessionDiagnosticAgentAck").Text.Contains("不授权"), "observed history and passive acknowledgement do not imply execution");
            controller.ThrowOnRead = true; panel.RefreshState();
            Check(DiagnosticControl(panel, "SessionDiagnosticEvidence").Text.Contains("0 · None")
                && DiagnosticControl(panel, "SessionDiagnosticAgentAck").Text.Contains("未核实"), "read failure cannot retain previous high evidence or a current acknowledgement claim");
            controller.ThrowOnRead = false;
            BindingClick(binding, "BindingClear");
            Check(controller.BindingClearCalls == 1 && !controller.Binding.IsBound && !BindingControl<Button>(binding, "BindingClear").IsEnabled,
                "clear binding is explicit and disables repeated clearing");
            Check(controller.TaskDispatchCalls == 0 && controller.ExecutorCalls == 0 && controller.PowerCalls == 0,
                "session selection, save, and clear never dispatch work or OS actions");
            BindingClick(binding, "BindingRefresh"); controller.CatalogCompletion.SetResult(new(true, "恢复连接，读取2个项目 Session。", new[] { choiceA, choiceB })); await SettleAsync();
            Check(selector.SelectedIndex == -1 && controller.BindingClearCalls == 1 && !controller.Binding.IsBound,
                "reconnected catalog does not silently rebind the prior or newest session");
            selector.SelectedIndex = 1; BindingClick(binding, "BindingSave");
            controller.State = controller.State with { CodexState = "Unknown", StateSource = "绑定 Session 的本地事件记录", TrustLevel = "Unknown",
                UnknownReason = "最后活动证据已过期，尚无新的任务状态。", ProjectMatchDescription = "一致：原日志项目声明与绑定项目相同。",
                EvidenceDescription = "2 · SessionMatched · 尚无新鲜生命周期", EventSequence = "19", LastEvent = "#19 Unknown",
                StateTimeline = "#16 Working\n#17 Waiting\n#18 Completed\n#19 Unknown", AgentAcknowledgement = "当前事件尚未收到回执；历史回执不表示本次已接收。" };
            panel.RefreshState();

            foreach (var theme in new[] { "Dark", "Light" })
            foreach (var width in new[] { 400d, 640d })
            {
                settings.Theme = theme; app.ConfigurePreviewTheme(settings);
                Control<TabControl>(panel, "ReliabilityTabs").SelectedIndex = 0;
                window.Width = width; scroller.ScrollToTop(); await SettleAsync(); window.UpdateLayout();
                captures.Add(Capture(window, $"reliability-{theme.ToLowerInvariant()}-{width:0}-top.png"));
                Check(panel.ActualWidth <= scroller.ViewportWidth + 1 && scroller.ExtentWidth <= scroller.ViewportWidth + 1,
                    $"reliability display fits {width:0}px {theme} viewport");
                Check(Control<Button>(panel, "ReliabilityDiagnostics").ActualWidth <= panel.ActualWidth - 24 + 1,
                    $"dry-run button fits {width:0}px {theme} panel");
                Check(Control<Button>(panel, "ReliabilityContinue").ActualHeight >= 40, "recovery has an adequate click target");
                scroller.ScrollToBottom(); await SettleAsync();
                captures.Add(Capture(window, $"reliability-{theme.ToLowerInvariant()}-{width:0}-results.png"));
                panel.ShowStateDiagnostics(); scroller.ScrollToTop(); await SettleAsync(); window.UpdateLayout();
                Check(panel.ActualWidth <= scroller.ViewportWidth + 1 && scroller.ExtentWidth <= scroller.ViewportWidth + 1 && project.ActualWidth <= binding.ActualWidth + 1,
                    $"session binding and evidence fit {width:0}px {theme} viewport");
                captures.Add(Capture(window, $"session-binding-{theme.ToLowerInvariant()}-{width:0}-top.png"));
                scroller.ScrollToBottom(); await SettleAsync();
                captures.Add(Capture(window, $"session-diagnostics-{theme.ToLowerInvariant()}-{width:0}-evidence.png"));
            }
            var lateController = new FixtureController(); var latePanel = new ReliabilityPanel(lateController);
            Click(latePanel, "ReliabilityDiagnostics"); latePanel.Dispose();
            var beforeLate = Control<TextBlock>(latePanel, "ReliabilityFeedback").Text;
            lateController.Completion.SetResult("late result"); await SettleAsync();
            Check(Control<TextBlock>(latePanel, "ReliabilityFeedback").Text == beforeLate, "late callback cannot mutate a disposed recovery panel");
            Check(!Control<Button>(latePanel, "ReliabilityDiagnostics").IsEnabled, "disposed recovery panel disables actions");
            var lateBindingController = new FixtureController(); var lateBinding = new SessionBindingPanel(lateBindingController);
            BindingControl<TextBox>(lateBinding, "BindingProjectInput").Text = choiceA.ProjectRoot; BindingClick(lateBinding, "BindingRefresh");
            var lateText = BindingControl<TextBlock>(lateBinding, "BindingFeedback").Text; lateBinding.Dispose();
            lateBindingController.CatalogCompletion.SetResult(new(true, "late catalog", new[] { choiceA })); await SettleAsync();
            Check(BindingControl<TextBlock>(lateBinding, "BindingFeedback").Text == lateText && BindingControl<ComboBox>(lateBinding, "BindingSessions").Items.Count == 0,
                "disposed binding panel ignores late catalog results and cannot create a selection");
            panel.Dispose();
            Check(!Control<Button>(panel, "ReliabilityContinue").IsEnabled && !Control<Button>(panel, "ReliabilityLogs").IsEnabled, "disposed recovery panel disables all path and context callbacks");
        }
        finally { panel.Dispose(); window.Close(); }
        File.WriteAllText(Path.Combine(PreviewDirectory, "check-result.json"), JsonSerializer.Serialize(new
        {
            passed = true, assertions, renders = captures.Count, testedAt = DateTimeOffset.UtcNow,
            evidence = "offline UI fixture; state inputs are synthetic, controls/rendering are real",
            networkRequests = 0, realUserStateUsed = false, powerActions = 0, processCloseActions = 0, serviceStopActions = 0,
            captures = captures.Select(Path.GetFileName).ToArray()
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS reliability-check: {assertions} assertions, {captures.Count} renders; fixture only, no network/process/service/power actions");
        return 0;
    }
    static T Control<T>(ReliabilityPanel panel, string name) where T : FrameworkElement
        => panel.FindName(name) as T ?? throw new InvalidOperationException("Missing reliability control " + name);
    static void Click(ReliabilityPanel panel, string name)
    {
        var button = Control<Button>(panel, name); Check(button.IsEnabled, name + " is enabled"); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
    }
    static T BindingControl<T>(SessionBindingPanel panel, string name) where T : FrameworkElement
        => panel.FindName(name) as T ?? throw new InvalidOperationException("Missing binding control " + name);
    static void BindingClick(SessionBindingPanel panel, string name)
    {
        var button = BindingControl<Button>(panel, name); Check(button.IsEnabled, name + " is enabled"); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
    }
    static TextBlock DiagnosticControl(ReliabilityPanel panel, string name)
        => panel.SessionDiagnosticsForCheck.FindName(name) as TextBlock ?? throw new InvalidOperationException("Missing state evidence " + name);
    static async Task SettleAsync() => await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    static string Capture(Window window, string name)
    {
        var width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth)); var height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
        var pixels = new byte[width * height * 4]; bitmap.CopyPixels(pixels, width * 4, 0);
        Check(Enumerable.Range(0, width * height).Any(index => pixels[index * 4 + 3] != 0), "nonblank reliability render " + name);
        var path = Path.Combine(PreviewDirectory, name); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write); encoder.Save(stream); return path;
    }
    static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException("Reliability UI: " + description);
        assertions++; Console.WriteLine("  PASS " + description);
    }
    sealed class FixtureController : IReliabilityUiController
    {
        public ReliabilityUiState State { get; set; } = new();
        public bool ThrowOnRead { get; set; }
        public int ContinueCalls { get; private set; }
        public int LogCalls { get; private set; }
        public int DiagnosticsCalls { get; private set; }
        public int CatalogCalls { get; private set; }
        public int BindingSaveCalls { get; private set; }
        public int BindingClearCalls { get; private set; }
        public string LastSavedSessionId { get; private set; } = "";
        public SessionBindingUiState Binding { get; private set; } = new();
        public bool RejectSave { get; set; }
        public TaskCompletionSource<SessionCatalogUiResult> CatalogCompletion { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int TaskDispatchCalls => 0;
        public int ExecutorCalls => 0;
        public int PowerCalls => 0;
        public int ProcessCloseCalls => 0;
        public int ServiceStopCalls => 0;
        public TaskCompletionSource<string> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ReliabilityUiState GetState() => ThrowOnRead ? throw new IOException("fixture read unavailable") : State;
        public string ContinueRecovery()
        {
            ContinueCalls++; State = State with { RecoveryAcknowledged = true, RecoverySummary = "已确认恢复信息；任务与收尾均不会自动续跑。" };
            return "已恢复显示上下文，未开始任务或执行收尾操作。";
        }
        public string OpenLogs() { LogCalls++; return @"日志目录：D:\CodexQuotaWidgetData\logs"; }
        public Task<string> RunDiagnosticsAsync() { DiagnosticsCalls++; return Completion.Task; }
        public SessionBindingUiState GetBindingState() => Binding;
        public Task<SessionCatalogUiResult> RefreshSessionsAsync(string projectRoot)
        {
            CatalogCalls++; CatalogCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously); return CatalogCompletion.Task;
        }
        public SessionBindingUiSaveResult SaveBinding(string projectRoot, string sessionId)
        {
            BindingSaveCalls++; LastSavedSessionId = sessionId;
            if (RejectSave) return new(false, "Session 元数据变化，原绑定保留。", Binding);
            Binding = new() { IsBound = true, ProjectRoot = projectRoot, SessionId = sessionId,
                MetadataPath = $@"C:\Users\FixtureUser\.codex\sessions\2026\10\04\rollout-{sessionId}.jsonl", Status = "已显式保存绑定；不运行任务。" };
            State = State with { BindingProject = projectRoot, SessionId = sessionId, UnknownReason = "等待绑定 Session 的可核验状态证据。" };
            return new(true, "绑定已保存。", Binding);
        }
        public SessionBindingUiSaveResult ClearBinding()
        {
            BindingClearCalls++; Binding = new(); State = State with { BindingProject = "", SessionId = "", TurnId = "", CodexState = "Unknown", UnknownReason = "尚未绑定真实 Session。" };
            return new(true, "绑定已解除，未操作任何程序。", Binding);
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexQuotaWidget.Core;
using CodexQuotaWidget.Core.ControlCenter;

namespace CodexQuotaWidget;

/// <summary>Offline integration checks using private disposable state and rendered WPF controls.</summary>
internal static class ControlCenterCheckRunner
{
    const string PreviewDirectory = @"D:\CodexQuotaWidget-GitHub\artifacts\control-center-preview";
    static int assertions;

    public static async Task<int> RunAsync()
    {
        assertions = 0;
        Directory.CreateDirectory(PreviewDirectory);
        File.WriteAllText(Path.Combine(PreviewDirectory, "check-result.json"), "{\"passed\":false,\"status\":\"running\"}");
        var runDirectory = Path.Combine(PreviewDirectory, "fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runDirectory);
        var previousData = Environment.GetEnvironmentVariable("CODEX_QUOTA_WIDGET_DATA");
        Environment.SetEnvironmentVariable("CODEX_QUOTA_WIDGET_DATA", runDirectory);
        // Fail before the monitor reads anything if some earlier code initialized the real data root.
        if (!string.Equals(Path.GetFullPath(Paths.Data), runDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Control-center preview requires isolated test data before Paths is initialized");

        var statePath = Path.Combine(runDirectory, "state.json");
        var now = DateTimeOffset.UtcNow;
        var center = new ControlCenterService(statePath, () => now);
        var settings = new Settings { FollowCodex = false, EnableAnimations = false, Width = 640, Height = 880, Theme = "Dark" };
        var app = (App)Application.Current;
        app.ConfigurePreviewTheme(settings);
        var monitor = new QuotaMonitor(settings, () => false, () => false);
        var window = new StatusWindow(monitor, () => Task.CompletedTask, () => { }, () => { }, () => { }, center);
        var captures = new List<string>();
        bool focusAvailable = false;
        try
        {
            window.Show();
            window.ShowAdvancedWorkflowForLayout();
            await SettleAsync();
            var panel = window.ControlPanelForLayout ?? throw new InvalidOperationException("Workflow panel not integrated");
            var title = Control<TextBox>(panel, "ControlTaskTitle");
            var notes = Control<TextBox>(panel, "ControlTaskNotes");
            var list = Control<ListBox>(panel, "ControlTaskList");
            Check(!panel.StartNextEnabledForLayout, "unknown quota prevents starting next task");
            Check(center.Snapshot.ConnectionState == WorkflowConnectionState.NotConnected && panel.RuntimeTextForLayout.Contains("尚未连接"), "quota state is distinct from AI executor state");

            center.UpdateQuota(Quota(now, 80, 90), MonitorStatus.Live, false);
            title.Text = "实现 AI 控制中心任务队列";
            notes.Text = "记录本地任务状态并验证额度保护；不执行模型调用。";
            Click(panel, "ControlTaskAdd");
            var first = center.Snapshot.Tasks.Single().Id;
            Check(panel.TaskCountForLayout == 1 && title.Text.Length == 0 && notes.Text.Length == 0, "task form adds a persisted task and clears only submitted input");
            Check(File.Exists(statePath), "submitted task is durable in private fixture storage");
            title.Text = "验证胶囊与托盘兼容性";
            notes.Text = "保持现有额度、动画和单实例能力。";
            Click(panel, "ControlTaskAdd");
            var second = center.Snapshot.Tasks.Single(t => t.Id != first).Id;
            panel.SelectTaskForLayout(first);
            title.Text = "未提交的任务草稿";
            window.Activate();
            Keyboard.Focus(title);
            focusAvailable = title.IsKeyboardFocused;
            var selectedRow = list.SelectedItem;
            center.UpdateQuota(Quota(now, 79, 90), MonitorStatus.Live, false);
            await SettleAsync();
            Check(panel.SelectedTaskIdForLayout == first && ReferenceEquals(list.SelectedItem, selectedRow), "quota refresh preserves selected row identity");
            Check(title.Text == "未提交的任务草稿" && (!focusAvailable || title.IsKeyboardFocused), "quota refresh preserves draft and available keyboard focus");
            await Task.Run(() => center.UpdateQuota(Quota(now, 78, 90), MonitorStatus.Live, false));
            await SettleAsync();
            Check(panel.SelectedTaskIdForLayout == first && title.Text == "未提交的任务草稿", "worker-thread updates safely reach the UI without replacing input");

            Click(panel, "ControlTaskPrimaryAction");
            Check(State(center, first) == WorkItemState.Running, "start button marks selected task running");
            Check(center.Snapshot.ConnectionState == WorkflowConnectionState.NotConnected && panel.RuntimeTextForLayout.Contains("本地任务跟踪"), "local running never claims an AI executor is running");
            Click(panel, "ControlTaskPrimaryAction");
            Check(State(center, first) == WorkItemState.Paused, "pause button records task pause");
            Click(panel, "ControlTaskPrimaryAction");
            Check(State(center, first) == WorkItemState.Running, "resume button records task resumption");

            center.UpdateQuota(Quota(now, 4, 85), MonitorStatus.Live, false);
            Check(center.Snapshot.Guard.State == QuotaGuardState.Protected && center.Snapshot.SafeStopRequested, "low quota requests safe stop");
            Check(!panel.StartNextEnabledForLayout && State(center, first) == WorkItemState.Running, "protection blocks new tasks without inventing completion");
            Click(panel, "ControlTaskPrimaryAction");
            center.UpdateQuota(Quota(now, 70, 85), MonitorStatus.Live, false);
            Check(!Control<Button>(panel, "ControlTaskPrimaryAction").IsEnabled, "quota recovery never silently resumes a protected task");
            Click(panel, "ControlQuotaAcknowledge");
            Click(panel, "ControlTaskPrimaryAction");
            Click(panel, "ControlTaskComplete");
            Check(State(center, first) == WorkItemState.Completed && center.Snapshot.Tasks.Count == 2, "completion updates only selected task");

            Click(panel, "ControlQueueToggle");
            Check(center.Snapshot.DispatchPaused && !panel.StartNextEnabledForLayout, "queue pause blocks dispatch");
            Click(panel, "ControlQueueToggle");
            Check(!center.Snapshot.DispatchPaused && panel.StartNextEnabledForLayout, "manual queue resume enables eligible next task");
            panel.SelectTaskForLayout(second);
            Click(panel, "ControlQueueStartNext");
            Click(panel, "ControlTaskFail");
            Check(State(center, second) == WorkItemState.Failed, "failure button updates selected task");
            Click(panel, "ControlTaskPrimaryAction");
            Check(State(center, second) == WorkItemState.Queued, "retry returns failed task to queue");
            Click(panel, "ControlTaskCancel");
            Check(State(center, second) == WorkItemState.Cancelled, "cancel button records cancellation");
            Click(panel, "ControlTaskPrimaryAction");
            Check(State(center, second) == WorkItemState.Queued && panel.SelectedTaskIdForLayout == second, "retry keeps selection intact");

            center.UpdateQuota(Quota(now, 75, 90), MonitorStatus.Offline, false);
            Check(center.Snapshot.Guard.State == QuotaGuardState.Unknown && !panel.StartNextEnabledForLayout, "offline quota cannot authorize a new task");
            Check(center.Snapshot.RuntimeState == AiRuntimeState.NotConnected, "offline with no running task does not imply an active agent");

            center.UpdateQuota(Quota(now, 15, 85), MonitorStatus.Live, false);
            Check(center.RequestSafeExit().Success, "safe exit can checkpoint local work without closing this preview");
            Check(!Control<Button>(panel, "ControlQueueToggle").IsEnabled && !panel.StartNextEnabledForLayout, "safe exit prevents dispatch controls from reopening the queue");
            Check(center.CancelSafeExit().Success, "safe exit request can be cancelled without automatic dispatch");
            Click(panel, "ControlQueueToggle");

            // Priority, editing and filters use real controls, keeping new-task input independent.
            title.Text = "优先验证安全保护";
            notes.Text = "优先级测试任务";
            Control<ComboBox>(panel, "ControlTaskPriority").SelectedIndex = 2;
            Click(panel, "ControlTaskAdd");
            var urgent = center.Snapshot.Tasks.Single(t => t.Title == "优先验证安全保护").Id;
            Check(center.Snapshot.Tasks.First().Id == urgent && center.Snapshot.Tasks.First().Priority == 100, "priority choice affects the core queue ordering");
            title.Text = "独立的新任务草稿";
            notes.Text = "新任务目标未提交";
            Control<Expander>(panel, "ControlTaskEditor").IsExpanded = true;
            Control<TextBox>(panel, "ControlEditTitle").Text = "已编辑的优先任务";
            Control<TextBox>(panel, "ControlEditNotes").Text = "仅更新已存在的任务";
            Control<ComboBox>(panel, "ControlEditPriority").SelectedIndex = 1;
            center.UpdateQuota(Quota(now, 16, 85), MonitorStatus.Live, false);
            Check(Control<TextBox>(panel, "ControlEditTitle").Text == "已编辑的优先任务", "quota refresh retains an unsaved task edit");
            Click(panel, "ControlEditSave");
            var edited = center.Snapshot.Tasks.Single(t => t.Id == urgent);
            Check(edited.Title == "已编辑的优先任务" && edited.Priority == 50 && title.Text == "独立的新任务草稿" && notes.Text == "新任务目标未提交", "task editor saves only selected task and preserves new-task draft");
            var filter = Control<ComboBox>(panel, "ControlTaskFilter");
            filter.SelectedIndex = 2;
            Check(list.Items.Count == 1 && panel.SelectedTaskIdForLayout == first, "ended filter displays only ended records");
            Click(panel, "ControlQueueStartNext");
            Check(State(center, urgent) == WorkItemState.Running && State(center, second) == WorkItemState.Queued, "list filter does not affect highest-priority dispatch");
            filter.SelectedIndex = 1;
            panel.SelectTaskForLayout(urgent);
            Check(!Control<Button>(panel, "ControlEditSave").IsEnabled && !Control<Button>(panel, "ControlTaskRemove").IsEnabled, "running task cannot be edited or removed from UI");
            Click(panel, "ControlTaskPrimaryAction");
            Control<TextBox>(panel, "ControlEditTitle").Text = "暂停期间编辑";
            Click(panel, "ControlEditSave");
            Check(State(center, urgent) == WorkItemState.Paused && center.Snapshot.Tasks.Single(t => t.Id == urgent).Title == "暂停期间编辑", "paused task can be edited without resuming it");
            Click(panel, "ControlTaskCancel");
            filter.SelectedIndex = 0;
            panel.SelectTaskForLayout(urgent);
            Click(panel, "ControlTaskRemove");
            Check(center.Snapshot.Tasks.Any(t => t.Id == urgent), "first remove click only asks for inline confirmation");
            Click(panel, "ControlTaskKeepRecord");
            Check(center.Snapshot.Tasks.Any(t => t.Id == urgent), "cancelled removal leaves local record intact");
            Click(panel, "ControlTaskRemove");
            Click(panel, "ControlTaskConfirmRemove");
            Check(center.Snapshot.Tasks.All(t => t.Id != urgent) && center.Snapshot.Tasks.Count == 2, "confirmed removal affects one non-running record only");

            var originalPolicy = center.Snapshot.Policy;
            Control<Expander>(panel, "ControlPolicyEditor").IsExpanded = true;
            Check(center.UpdatePolicy(originalPolicy with { Enabled = false }).Success, "fixture can opt out of percentage thresholds");
            var warning = Control<TextBox>(panel, "ControlPolicyWarning");
            var protection = Control<TextBox>(panel, "ControlPolicyProtection");
            warning.Text = "35";
            protection.Text = "8";
            Click(panel, "ControlPolicyApply");
            Check(center.Snapshot.Policy.WarningPercent == 35 && center.Snapshot.Policy.ProtectionPercent == 8 && !center.Snapshot.Policy.Enabled
                && center.Snapshot.Policy.MaximumQuotaAgeSeconds == originalPolicy.MaximumQuotaAgeSeconds, "threshold editor preserves enabled and host freshness settings");
            center.UpdateQuota(Quota(now, 0, 85), MonitorStatus.Live, false);
            Check(!panel.StartNextEnabledForLayout, "disabled percentage policy still cannot authorize exhausted quota");
            center.UpdateQuota(Quota(now, 70, 85), MonitorStatus.Offline, false);
            Check(!panel.StartNextEnabledForLayout, "threshold change cannot authorize unknown quota");
            center.UpdateQuota(Quota(now, 70, 85), MonitorStatus.Live, false);
            warning.Text = "36";
            protection.Text = "9";
            var beforeWriteFailure = center.Snapshot.Revision;
            using (var blockedState = new FileStream(statePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Click(panel, "ControlPolicyApply");
                Check(center.Snapshot.Revision == beforeWriteFailure && center.Snapshot.StorageError is not null &&
                    Control<TextBlock>(panel, "ControlStorageStatus").Visibility == Visibility.Visible, "failed atomic write refreshes storage warning without a task revision");
            }
            Click(panel, "ControlPolicyApply");
            Check(center.Snapshot.StorageError is null && Control<TextBlock>(panel, "ControlStorageStatus").Visibility == Visibility.Collapsed, "successful retry clears stale storage warning");
            Check(center.UpdatePolicy(originalPolicy).Success, "fixture restores normal protection policy");
            center.UpdateQuota(Quota(now, 15, 85), MonitorStatus.Live, false);
            Control<Expander>(panel, "ControlTaskEditor").IsExpanded = false;
            Control<Expander>(panel, "ControlPolicyEditor").IsExpanded = false;
            CheckStartupArguments();
            var scroller = ParentScroller(panel);
            foreach (var theme in new[] { "Dark", "Light" })
            foreach (var width in new[] { 480d, 640d })
            {
                settings.Theme = theme;
                app.ConfigurePreviewTheme(settings);
                window.Width = width;
                window.Height = 880;
                window.ShowAdvancedWorkflowForLayout();
                scroller.ScrollToTop();
                await SettleAsync();
                window.UpdateLayout();
                Check(Math.Abs(window.ActualWidth - width) < 1 && panel.ActualWidth <= window.ActualWidth, $"workflow fits {width:0}px {theme} window");
                captures.Add(Capture(window, $"workflow-{theme.ToLowerInvariant()}-{width:0}-top.png"));
                scroller.ScrollToBottom();
                await SettleAsync();
                captures.Add(Capture(window, $"workflow-{theme.ToLowerInvariant()}-{width:0}-queue.png"));
            }
        }
        finally
        {
            try { window.StopTimers(); }
            finally
            {
                try { window.Hide(); await monitor.DisposeAsync(); }
                finally
                {
                    Environment.SetEnvironmentVariable("CODEX_QUOTA_WIDGET_DATA", previousData);
                    // Only this run's known fixture files are removed; screenshots and summary remain.
                    foreach (var file in new[] { statePath, statePath + ".bak" })
                        if (File.Exists(file)) File.Delete(file);
                    if (Directory.Exists(runDirectory) && !Directory.EnumerateFileSystemEntries(runDirectory).Any()) Directory.Delete(runDirectory);
                }
            }
        }
        File.WriteAllText(Path.Combine(PreviewDirectory, "check-result.json"), JsonSerializer.Serialize(new
        {
            passed = true, assertions, renders = captures.Count, keyboardFocusAvailable = focusAvailable,
            testedAt = DateTimeOffset.UtcNow, networkRequests = 0, realUserStateUsed = false,
            captures = captures.Select(Path.GetFileName).ToArray()
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS control-center-check: {assertions} assertions, {captures.Count} renders; private state, no network, keyboard focus available={focusAvailable}");
        Console.WriteLine("  previews=" + PreviewDirectory);
        return 0;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CommandLineToArgvW(string commandLine, out int count);
    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr memory);

    static void CheckStartupArguments()
    {
        foreach (var value in new[] { @"D:\Codex 数据\", @"D:\plain", "D:\\A \"quoted\"\\", "", @"\\server\share\尾巴\", "slashes\\\\\"quote" })
        {
            var arguments = CommandLineToArgvW("CodexQuotaWidget.exe --data-dir " + StartupIntegration.QuoteArgument(value), out var count);
            if (arguments == IntPtr.Zero) throw new InvalidOperationException("Windows argument parser unavailable");
            try
            {
                Check(count == 3 && Marshal.PtrToStringUni(Marshal.ReadIntPtr(arguments, 2 * IntPtr.Size)) == value,
                    "startup data argument round-trips via Windows native parser");
            }
            finally { LocalFree(arguments); }
        }
    }

    static QuotaSnapshot Quota(DateTimeOffset now, double five, double week) => new(now, "Test only", new List<QuotaWindow>
    {
        new("codex", "Codex", "primary", 100 - five, 300, now.AddHours(4).ToUnixTimeSeconds()),
        new("codex", "Codex", "secondary", 100 - week, 10080, now.AddDays(5).ToUnixTimeSeconds())
    }, false, null, new List<long>());

    static WorkItemState State(ControlCenterService center, Guid id) => center.Snapshot.Tasks.Single(t => t.Id == id).State;
    static T Control<T>(ControlCenterPanel panel, string name) where T : FrameworkElement
        => panel.FindName(name) as T ?? throw new InvalidOperationException("Missing workflow control " + name);
    static void Click(ControlCenterPanel panel, string name)
    {
        var button = Control<Button>(panel, name);
        Check(button.IsEnabled, name + " is enabled for requested action");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
    }

    static async Task SettleAsync()
        => await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

    static ScrollViewer ParentScroller(DependencyObject child)
    {
        for (var parent = VisualTreeHelper.GetParent(child); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is ScrollViewer viewer) return viewer;
        throw new InvalidOperationException("Workflow needs its own scroll viewer");
    }

    static string Capture(Window window, string filename)
    {
        var width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        var nontransparent = false;
        for (var i = 3; i < pixels.Length; i += 4)
            if (pixels[i] != 0) { nontransparent = true; break; }
        Check(nontransparent, "nonblank workflow render " + filename);
        var path = Path.Combine(PreviewDirectory, filename);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        encoder.Save(stream);
        return path;
    }

    static void Check(bool condition, string name)
    {
        if (!condition)
        {
            Console.WriteLine("  FAIL " + name);
            throw new InvalidOperationException(name);
        }
        assertions++;
        Console.WriteLine("  PASS " + name);
    }
}

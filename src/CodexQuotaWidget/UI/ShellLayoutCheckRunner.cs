using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexQuotaWidget.Core;
using CodexQuotaWidget.Core.ControlCenter;

namespace CodexQuotaWidget;

/// <summary>Real WPF navigation/rendering with isolated state and inert workflow/recovery fixtures.</summary>
internal static class ShellLayoutCheckRunner
{
    const string Output = @"D:\CodexQuotaWidget-GitHub\artifacts\loop-ui-build\shell-preview";
    static readonly string[] PageNames = ["QuotaPage", "LoopPage", "SkillsPage", "SettingsPage"];
    static readonly string[] SectionNames = ["AppearanceSettingsSection", "StartupSettingsSection", "AutomationSettingsSection",
        "AccountSettingsSection", "DiagnosticsSettingsSection", "LocalTasksSettingsSection", "AboutSettingsSection"];

    internal static async Task<int> RunAsync()
    {
        Directory.CreateDirectory(Output);
        var fixture = Path.Combine(Output, "fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        var previousData = Environment.GetEnvironmentVariable("CODEX_QUOTA_WIDGET_DATA");
        Environment.SetEnvironmentVariable("CODEX_QUOTA_WIDGET_DATA", fixture);
        if (!string.Equals(Path.GetFullPath(Paths.Data), fixture, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Shell check must initialize private D-drive state before Paths.");
        var resultPath = Path.Combine(Output, "shell-check-result.json");
        File.WriteAllText(resultPath, "{\"passed\":false,\"status\":\"running\"}");
        var protectedPaths = new[] {
            @"D:\CodexQuotaWidgetData\workflow\loop-state.json",
            @"D:\CodexQuotaWidgetData\workflow\loop-state.json.bak",
            @"D:\CodexQuotaWidgetData\workflow\settings.json",
            @"D:\Codex\SkillUpdater\updater.ps1",
            @"D:\Codex\SkillUpdater\config\provenance.json",
            @"D:\Codex\SkillUpdater\logs\latest-result.json",
            @"C:\Users\FixtureUser\.codex\skills\skill-router-lite\SKILL.md"
        };
        var before = protectedPaths.Select(Hash).ToArray();
        var settings = new Settings { Theme = "Dark", FollowCodex = false, EnableAnimations = false, Width = 640, Height = 880, WindowMode = "Full" };
        var app = (App)Application.Current; app.ConfigurePreviewTheme(settings);
        var monitor = new QuotaMonitor(settings, () => false, () => false);
        var queue = new ControlCenterService(Path.Combine(fixture, "queue.json"));
        var workflow = new InertWorkflow(); var reliability = new InertReliability();
        var saves = 0; var modeCalls = 0; var diagnosticCalls = 0; var checks = 0; var updateCheckCalls = 0;
        var updateCompletion = new TaskCompletionSource<UnifiedUpdateCheck>(TaskCreationOptions.RunContinuationsAsynchronously);
        var captures = new List<string>();
        var window = new StatusWindow(monitor, () => { diagnosticCalls++; return Task.CompletedTask; }, () => saves++, () => { }, () => modeCalls++,
            queue, reliability: reliability, workflow: workflow, updateCheck: () => { updateCheckCalls++; return updateCompletion.Task; }) { ShowActivated = false, Topmost = false, IsHitTestVisible = false };
        try
        {
            window.Show(); await Settle();
            Check(monitor.AttemptCount == 0 && monitor.ServerPid is null, "monitor was never started");
            Check(window.CurrentPageForLayout == "quota", "first launch opens quota");
            AssertSinglePage("quota");
            var tabs = new[] { Named<Button>(window, "QuotaPageTab"), Named<Button>(window, "LoopPageTab"), Named<Button>(window, "SkillsPageTab") };
            Check(tabs.Select(button => button.Content?.ToString()).SequenceEqual(new[] { "额度", "循环任务", "Skills" }), "three plain-language primary destinations");
            foreach (var button in tabs) Check(!string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)) && button.Focusable, "navigation has accessible keyboard target " + button.Name);
            var settingsButton = window.SettingsButtonForLayout;
            var capsuleButton = window.SmallWindowButtonForLayout;
            Click(tabs[2]); await Settle(); AssertSinglePage("skills");
            var skillPanel = Named<SkillUpdateButton>(window, "SkillUpdateAction");
            var updaterState = SkillUpdateController.Shared.State;
            Check(!SkillUpdateController.Shared.IsRunning, "opening Skills does not start an update");
            Click(settingsButton); await Settle(); AssertSinglePage("settings");
            foreach (var section in SectionNames) Check(!Named<Expander>(window, section).IsExpanded, "settings starts collapsed: " + section);
            Click(settingsButton); await Settle(); AssertSinglePage("skills");
            Check(ReferenceEquals(skillPanel, Named<SkillUpdateButton>(window, "SkillUpdateAction")) && SkillUpdateController.Shared.State == updaterState,
                "settings round trip preserves the same update control and job state");
            Click(tabs[1]); await Settle(); AssertSinglePage("workflow");
            var loop = Named<WorkflowLoopPanel>(window, "WorkflowLoopPanel");
            Check(!Named<Expander>(loop, "WorkflowLoopAdvanced").IsExpanded, "technical details are initially collapsed");
            Check(!Named<Expander>(loop, "WorkflowLoopHistorySection").IsExpanded && workflow.HistoryReads == 0, "history remains opt-in without background reads");
            Check(!Descendants<TextBox>(loop).Any(box => box.IsVisible), "main loop page has no persistent large task input");
            Check(Named<TextBlock>(loop, "WorkflowLoopCompletedRounds").Text == "已完成 0 轮"
                && Named<Button>(loop, "WorkflowLoopStop").Visibility == Visibility.Collapsed
                && Named<CheckBox>(loop, "WorkflowLoopAuthorize").Visibility == Visibility.Collapsed,
                "empty loop page shows honest progress without stop or authorization clutter");
            Click(settingsButton); await Settle(); AssertSinglePage("settings");
            Click(settingsButton); await Settle(); AssertSinglePage("workflow");
            var beforeMode = modeCalls; var beforeSave = saves;
            Click(capsuleButton); await Settle();
            Check(modeCalls == beforeMode + 1 && saves == beforeSave + 1 && settings.WindowMode == "Capsule", "capsule shortcut uses the existing save and mode callback once");
            Check(window.IsVisible, "fixture callback did not manipulate an external capsule or app");

            foreach (var theme in new[] { "Dark", "Light" })
            foreach (var size in new[] { new Size(480, 580), new Size(640, 880) })
            {
                settings.Theme = theme; app.ConfigurePreviewTheme(settings);
                window.Width = size.Width; window.Height = size.Height;
                foreach (var (button, name) in new[] { (tabs[0], "quota"), (tabs[1], "workflow"), (tabs[2], "skills") })
                {
                    Click(button); await Settle(); AssertSinglePage(name);
                    var page = CurrentScroller(); page.ScrollToTop(); await Settle();
                    CheckViewport(page, theme + "/" + size.Width + "/" + name);
                    foreach (var tab in tabs) CheckInside(tab, window, "fixed navigation stays visible: " + tab.Name);
                    CheckInside(settingsButton, window, "settings remains visible"); CheckInside(capsuleButton, window, "capsule remains visible");
                    if (name == "workflow") CheckInside(Named<Button>(loop, "WorkflowLoopNewTask"), page, "new-user primary action stays in viewport");
                    if (name == "skills") CheckInside(skillPanel, page, "update action stays in viewport");
                    Capture($"shell-{theme.ToLowerInvariant()}-{size.Width:0}-{size.Height:0}-{name}.png");
                }
                Click(settingsButton); await Settle(); AssertSinglePage("settings");
                foreach (var section in SectionNames) Named<Expander>(window, section).IsExpanded = false;
                CurrentScroller().ScrollToTop(); await Settle(); CheckViewport(CurrentScroller(), theme + "/" + size.Width + "/settings");
                CheckInside(Named<Expander>(window, "AppearanceSettingsSection"), CurrentScroller(), "first settings group stays visible");
                Capture($"shell-{theme.ToLowerInvariant()}-{size.Width:0}-{size.Height:0}-settings.png");
                var appearance = Named<Expander>(window, "AppearanceSettingsSection"); appearance.IsExpanded = true; await Settle();
                CheckViewport(CurrentScroller(), theme + "/" + size.Width + "/appearance-expanded");
                Capture($"shell-{theme.ToLowerInvariant()}-{size.Width:0}-{size.Height:0}-appearance.png"); appearance.IsExpanded = false;
            }

            window.ShowReliability(); await Settle(); AssertSinglePage("settings");
            Check(Named<Expander>(window, "DiagnosticsSettingsSection").IsExpanded && Named<ReliabilityPanel>(window, "ReliabilityPanel").IsVisible,
                "startup recovery shortcut navigates and expands the real recovery panel");
            window.ShowStatusDiagnostics(); await Settle();
            Check(Named<TabControl>(window, "ReliabilityTabs").SelectedIndex == 1, "session shortcut selects state diagnostics without scanning sessions");
            window.ShowAdvancedWorkflowForLayout(); await Settle();
            Check(Named<Expander>(window, "LocalTasksSettingsSection").IsExpanded && window.ControlPanelForLayout?.IsVisible == true,
                "local queue remains reachable under its own settings disclosure");
            window.ShowAutomationForLayout(); await Settle();
            Check(Named<Expander>(window, "AutomationSettingsSection").IsExpanded && window.AutomationPanelForLayout?.IsVisible == true,
                "automation remains reachable without enabling or running it");
            Check(workflow.ActionCalls == 0 && reliability.ActionCalls == 0 && diagnosticCalls == 0, "navigation never connects, dispatches, scans sessions or runs diagnostics");
            Check(monitor.AttemptCount == 0 && monitor.ServerPid is null && !SkillUpdateController.Shared.IsRunning, "no account request, Codex app-server or Skill update was launched");
            Check(before.SequenceEqual(protectedPaths.Select(Hash)), "production workflow, updater and Router bytes remain unchanged");
            var updateButton = Named<Button>(window, "UnifiedUpdateCheckAction");
            Check(Descendants<Button>(window).Count(b => b.Content?.ToString() == "检查更新") == 1
                && !Descendants<Button>(window).Any(b => b.Content?.ToString() == "检查 Skill 更新"), "one existing unified check entry, no duplicate Skill check button");
            Click(updateButton); await Settle(); Click(updateButton); await Settle();
            Check(updateCheckCalls == 1 && !updateButton.IsEnabled, "existing routed button starts the real handler and prevents repeat checks");
            updateCompletion.SetResult(new([new("AI Control Center", "组件", UpdateCheckStatus.Unavailable, "1.12.7", null, "offline fixture"),
                new("fixture-unknown", "Skill", UpdateCheckStatus.UnknownSource, null, null, "unknown fixture source")], null));
            await Settle(); await Settle();
            Check(window.CurrentPageForLayout == "skills" && updateButton.IsEnabled && !skillPanel.IsEnabled,
                "unified check shows results in existing Skills page; failures cannot enable installation");
            Check(before.SequenceEqual(protectedPaths.Select(Hash)), "routed unified check fixture cannot modify production or installed Skills");
            WriteResult(true, null);
            Console.WriteLine($"PASS shell-layout-check: {checks} assertions, {captures.Count} renders; real WPF routed events; synthetic state; no external mouse/model/network/update/system actions");
            return 0;
        }
        catch (Exception error) { WriteResult(false, error.Message); throw; }
        finally { window.StopTimers(); window.Hide(); await monitor.DisposeAsync(); Environment.SetEnvironmentVariable("CODEX_QUOTA_WIDGET_DATA", previousData); }

        void Check(bool valid, string message)
        {
            if (!valid) throw new InvalidOperationException("Shell layout: " + message);
            checks++; Console.WriteLine("  PASS " + message);
        }
        ScrollViewer CurrentScroller() => Named<ScrollViewer>(window, window.CurrentPageForLayout switch { "quota" => "QuotaPage", "workflow" => "LoopPage", "skills" => "SkillsPage", _ => "SettingsPage" });
        void AssertSinglePage(string name)
        {
            Check(window.CurrentPageForLayout == name && PageNames.Select(page => Named<ScrollViewer>(window, page)).Count(page => page.Visibility == Visibility.Visible) == 1
                && CurrentScroller().IsVisible, "only the selected " + name + " page is shown");
        }
        void CheckViewport(ScrollViewer page, string label)
        {
            window.UpdateLayout();
            Check(page.ViewportWidth > 0 && page.ViewportHeight > 0 && page.ExtentWidth <= page.ViewportWidth + 1, label + " has no horizontal scroll overflow");
            var content = (FrameworkElement)page.Content;
            Check(content.ActualWidth <= page.ViewportWidth + 1, label + " content fits the allocated viewport");
            foreach (var button in Descendants<Button>(content).Where(item => item.IsVisible && item.ActualWidth > 0))
            {
                var bounds = button.TransformToAncestor(page).TransformBounds(new Rect(new Point(), button.RenderSize));
                Check(bounds.Left >= -1 && bounds.Right <= page.ActualWidth + 1, label + " button stays inside horizontal bounds: " + button.Name);
            }
        }
        void CheckInside(FrameworkElement element, FrameworkElement container, string label)
        {
            var bounds = element.TransformToAncestor(container).TransformBounds(new Rect(new Point(), element.RenderSize));
            Check(element.IsVisible && element.ActualWidth > 0 && element.ActualHeight > 0 && bounds.Left >= -1 && bounds.Top >= -1
                && bounds.Right <= container.ActualWidth + 1 && bounds.Bottom <= container.ActualHeight + 1, label);
        }
        void Capture(string name)
        {
            window.UpdateLayout(); var width = (int)Math.Ceiling(window.ActualWidth); var height = (int)Math.Ceiling(window.ActualHeight);
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
            var pixels = new byte[width * height * 4]; bitmap.CopyPixels(pixels, width * 4, 0);
            Check(Enumerable.Range(0, width * height).Any(index => pixels[index * 4 + 3] != 0), "nonblank real WPF render: " + name);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(Output, name)); encoder.Save(stream); captures.Add(name);
        }
        void WriteResult(bool passed, string? error) => File.WriteAllText(resultPath, JsonSerializer.Serialize(new {
            passed, error, assertions = checks, renders = captures.Count, testedAt = DateTimeOffset.UtcNow, realWpfRendering = true,
            realWpfRoutedEvents = true, syntheticTaskAndQuotaState = true, externalMouseAutomation = false, realModelCalls = false,
            realSkillUpdates = false, realNetworkActions = false, startupRegistrationChanges = false, processClosure = false, powerActions = false,
            productionHashesUnchanged = before.SequenceEqual(protectedPaths.Select(Hash)), captures
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    static string Hash(string path) => File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : "missing";
    static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
    static async Task Settle() { await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); await Task.Delay(30); }
    static T Named<T>(DependencyObject root, string name) where T : FrameworkElement
        => Descendants<T>(root).SingleOrDefault(item => item.Name == name) ?? throw new InvalidOperationException("Missing shell control " + name);
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

    sealed class InertWorkflow : IWorkflowLoopUiController
    {
        public WorkflowLoopConfiguration Configuration { get; } = new() { ProjectRoot = @"D:\CodexQuotaWidget-GitHub" };
        public WorkflowLoopState State { get; } = new();
        public bool IsRunning => false;
        public string ConnectionDescription => "Shell layout fixture only; no real connector.";
        public int ActionCalls { get; private set; }
        public int HistoryReads { get; private set; }
        public IReadOnlyList<WorkflowLoopState> ReadHistory() { HistoryReads++; return Array.Empty<WorkflowLoopState>(); }
        Exception Unexpected() { ActionCalls++; return new InvalidOperationException("Shell navigation must not dispatch workflow actions."); }
        public Task<string> StartAsync(WorkflowLoopConfiguration configuration) => throw Unexpected();
        public Task<string> ReconnectGptAsync(string? candidateId = null) => throw Unexpected();
        public Task<string> ConnectCodexAsync() => throw Unexpected();
        public Task<string> SaveTaskAsync(WorkflowLoopConfiguration configuration) => throw Unexpected();
        public void Pause() => throw Unexpected();
        public void Stop() => throw Unexpected();
        public string AcknowledgeRecovery() => throw Unexpected();
    }
    sealed class InertReliability : IReliabilityUiController
    {
        public int ActionCalls { get; private set; }
        public ReliabilityUiState GetState() => new() { StateSource = "离线界面检查，尚无真实 Codex 证据。" };
        public SessionBindingUiState GetBindingState() => new();
        Exception Unexpected() { ActionCalls++; return new InvalidOperationException("Shell navigation must not run recovery/session actions."); }
        public string ContinueRecovery() => throw Unexpected();
        public string OpenLogs() => throw Unexpected();
        public Task<string> RunDiagnosticsAsync() => throw Unexpected();
        public Task<SessionCatalogUiResult> RefreshSessionsAsync(string projectRoot) => throw Unexpected();
        public SessionBindingUiSaveResult SaveBinding(string projectRoot, string sessionId) => throw Unexpected();
        public SessionBindingUiSaveResult ClearBinding() => throw Unexpected();
    }
}

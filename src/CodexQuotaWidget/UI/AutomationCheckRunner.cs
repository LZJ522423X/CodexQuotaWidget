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

/// <summary>Offline real-control checks. No process/service/power operation is available to this fixture.</summary>
internal static class AutomationCheckRunner
{
    const string PreviewDirectory = @"D:\CodexQuotaWidget-GitHub\artifacts\automation-preview";
    static int assertions;
    public static async Task<int> RunAsync()
    {
        assertions = 0;
        Directory.CreateDirectory(PreviewDirectory);
        File.WriteAllText(Path.Combine(PreviewDirectory, "check-result.json"), "{\"passed\":false,\"status\":\"running\"}");
        var privateData = Path.Combine(PreviewDirectory, "fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(privateData);
        var previousData = Environment.GetEnvironmentVariable("CODEX_QUOTA_WIDGET_DATA");
        Environment.SetEnvironmentVariable("CODEX_QUOTA_WIDGET_DATA", privateData);
        if (!string.Equals(Path.GetFullPath(Paths.Data), privateData, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Automation checks require isolated data before Paths is initialized");
        var settings = new Settings { FollowCodex = false, EnableAnimations = false, Theme = "Dark", Width = 640, Height = 880 };
        var app = (App)Application.Current;
        app.ConfigurePreviewTheme(settings);
        var controller = new FixtureController();
        var panel = new AutomationSettingsPanel(controller);
        var scroller = new ScrollViewer { Content = panel, Margin = new Thickness(16), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var window = new Window { Title = "自动化设置离线检查", Content = scroller, Width = 640, Height = 880, ShowInTaskbar = false };
        window.SetResourceReference(Window.BackgroundProperty, "Glass");
        var captures = new List<string>();
        QuotaMonitor? monitor = null;
        StatusWindow? integrated = null;
        try
        {
            window.Show(); await SettleAsync();
            var input = Control<TextBox>(panel, "AutomationPlanText");
            var enabled = Control<CheckBox>(panel, "AutomationEnabled");
            Check(input.AcceptsReturn && input.TextWrapping == TextWrapping.Wrap, "action text is a multiline accessible field");
            Check(!enabled.IsChecked.GetValueOrDefault() && controller.SaveCount == 0, "opening settings does not arm or execute automation");
            Check(AutomationProperties.GetName(input).Contains("关机前操作"), "action input exposes an accessible name");
            Check(!Control<Button>(panel, "AutomationReset").IsEnabled, "restore is disabled until draft changes");

            input.Text = "关闭Clash和mihomo，然后关机"; enabled.IsChecked = true;
            Check(panel.DirtyForCheck && controller.State.Enabled == false, "editing enabled checkbox is a draft until explicit save");
            controller.State = controller.State with { Status = "等待新的有效额度耗尽事件" };
            panel.RefreshState();
            Check(input.Text == "关闭Clash和mihomo，然后关机" && enabled.IsChecked == true && panel.DirtyForCheck, "runtime refresh preserves action and enabled drafts");
            Check(Control<TextBlock>(panel, "AutomationStatus").Text == controller.State.Status, "runtime status updates alongside unsaved draft");
            Click(panel, "AutomationSave");
            Check(controller.SaveCount == 1 && controller.State.Enabled && !panel.DirtyForCheck && panel.StepCountForCheck == 5, "explicit save commits parsed plan and renders step preview");

            var saved = controller.State;
            input.Text = "运行任意脚本并清空文件";
            enabled.IsChecked = false;
            Click(panel, "AutomationSave");
            Check(controller.State == saved && panel.DirtyForCheck, "unsupported input preserves previous saved plan");
            Check(Control<TextBlock>(panel, "AutomationFeedback").Text.Contains("不支持的操作"), "unsupported input is visibly reported next to field");
            Check(enabled.IsChecked == false && Control<TextBlock>(panel, "AutomationFeedback").Text.Contains("仍开启"), "failed save explicitly distinguishes the disabled draft from the still-enabled saved switch");
            panel.RefreshState();
            Check(input.Text == "运行任意脚本并清空文件", "refresh does not hide failed draft");
            Click(panel, "AutomationReset");
            Check(input.Text == saved.Text && enabled.IsChecked == saved.Enabled && !panel.DirtyForCheck, "restore returns to saved text and enabled state");

            input.Text = "关闭Clash和mihomo，然后关机";
            Click(panel, "AutomationTest");
            Check(panel.BusyForCheck && controller.TestCount == 1, "test starts exactly one dry callback");
            Check(!input.IsEnabled && !enabled.IsEnabled && !Control<Button>(panel, "AutomationSave").IsEnabled && !Control<Button>(panel, "AutomationTest").IsEnabled, "async test blocks duplicate clicks and concurrent edits");
            controller.State = controller.State with { Status = "Executor 模拟验证中" }; panel.RefreshState();
            Check(panel.BusyForCheck && input.Text == saved.Text, "refresh cannot replace active test draft");
            controller.TestCompletion.SetResult("模拟通过：计划、目标、恢复文件与Executor可用；未关闭程序，未停止服务，未执行关机。");
            await SettleAsync();
            Check(!panel.BusyForCheck && input.IsEnabled && Control<Button>(panel, "AutomationTest").IsEnabled, "test completion restores controls");
            Check(controller.SaveCount == 2 && controller.PowerCalls == 0 && controller.ProcessCloseCalls == 0 && controller.ServiceStopCalls == 0, "test neither saves nor performs process service power actions");
            Check(Control<TextBlock>(panel, "AutomationFeedback").Text.Contains("模拟通过"), "dry result is visible");
            Click(panel, "AutomationHistory");
            Check(Control<Expander>(panel, "AutomationHistoryPanel").IsExpanded && Control<TextBox>(panel, "AutomationHistoryText").IsReadOnly, "last history is a read-only expandable local view");
            Check(Control<TextBox>(panel, "AutomationHistoryText").Text.Contains("ShutdownWindows: DryRun"), "history contains explicit dry power outcome");

            var previewOnly = new AutomationSettingsPanel(null);
            Check(!Control<Button>(previewOnly, "AutomationSave").IsEnabled && !Control<Button>(previewOnly, "AutomationTest").IsEnabled && !Control<CheckBox>(previewOnly, "AutomationEnabled").IsEnabled, "disconnected previews cannot enable or test automation");
            previewOnly.Dispose();
            monitor = new QuotaMonitor(settings, () => false, () => false);
            integrated = new StatusWindow(monitor, () => Task.CompletedTask, () => { }, () => { }, () => { }, automation: controller);
            integrated.Show(); integrated.ShowPreferencesForLayout(); await SettleAsync();
            Check(integrated.AutomationPanelForLayout is not null, "main settings integrates the independent local automation panel");
            Check(!VisualDescendants(integrated).OfType<CheckBox>().Any(box => box.Content?.ToString()?.Contains("3%") == true), "main settings contains no legacy percent-shutdown checkbox");
            integrated.StopTimers(); integrated.Hide();

            foreach (var theme in new[] { "Dark", "Light" })
            foreach (var width in new[] { 400d, 640d })
            {
                settings.Theme = theme; app.ConfigurePreviewTheme(settings);
                window.Width = width; window.Height = 880; window.Show(); scroller.ScrollToTop(); await SettleAsync(); window.UpdateLayout();
                captures.Add(Capture(window, $"automation-{theme.ToLowerInvariant()}-{width:0}-top.png"));
                Console.WriteLine($"  layout {theme} {width:0}px: window={window.ActualWidth:0.##}, viewport={scroller.ViewportWidth:0.##}, extent={scroller.ExtentWidth:0.##}, panel={panel.ActualWidth:0.##}, input={input.ActualWidth:0.##}");
                Check(panel.ActualWidth <= scroller.ViewportWidth + 1 && scroller.ExtentWidth <= scroller.ViewportWidth + 1
                    && input.ActualWidth <= panel.ActualWidth, $"action form fits {width:0}px {theme} viewport");
                Check(Control<Button>(panel, "AutomationTest").ActualHeight >= 40, "test has an adequate click target");
                scroller.ScrollToBottom(); await SettleAsync();
                captures.Add(Capture(window, $"automation-{theme.ToLowerInvariant()}-{width:0}-history.png"));
            }
            panel.Dispose();
            Check(!Control<Button>(panel, "AutomationSave").IsEnabled, "disposed panel disables actions");
        }
        finally
        {
            panel.Dispose(); window.Close();
            integrated?.StopTimers(); integrated?.Hide();
            if (monitor is not null) await monitor.DisposeAsync();
            Environment.SetEnvironmentVariable("CODEX_QUOTA_WIDGET_DATA", previousData);
            if (!Directory.EnumerateFileSystemEntries(privateData).Any()) Directory.Delete(privateData);
        }
        File.WriteAllText(Path.Combine(PreviewDirectory, "check-result.json"), JsonSerializer.Serialize(new
        {
            passed = true, assertions, renders = captures.Count, testedAt = DateTimeOffset.UtcNow,
            networkRequests = 0, realUserStateUsed = false, powerActions = 0, processCloseActions = 0, serviceStopActions = 0,
            captures = captures.Select(Path.GetFileName).ToArray()
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS automation-check: {assertions} assertions, {captures.Count} renders; private data, no network/process/service/power actions");
        Console.WriteLine("  previews=" + PreviewDirectory); return 0;
    }
    static T Control<T>(AutomationSettingsPanel panel, string name) where T : FrameworkElement
        => panel.FindName(name) as T ?? throw new InvalidOperationException("Missing automation control " + name);
    static void Click(AutomationSettingsPanel panel, string name)
    {
        var button = Control<Button>(panel, name); Check(button.IsEnabled, name + " is enabled");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
    }
    static async Task SettleAsync() => await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    static IEnumerable<DependencyObject> VisualDescendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        { var child = VisualTreeHelper.GetChild(parent, i); yield return child; foreach (var nested in VisualDescendants(child)) yield return nested; }
    }
    static string Capture(Window window, string filename)
    {
        var width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth)); var height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
        var pixels = new byte[width * height * 4]; bitmap.CopyPixels(pixels, width * 4, 0);
        var nonblank = false; for (var i = 3; i < pixels.Length; i += 4) if (pixels[i] != 0) { nonblank = true; break; }
        Check(nonblank, "nonblank automation render " + filename);
        var path = Path.Combine(PreviewDirectory, filename); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write); encoder.Save(stream); return path;
    }
    static void Check(bool value, string message)
    { if (!value) { Console.WriteLine("  FAIL " + message); throw new InvalidOperationException(message); } assertions++; Console.WriteLine("  PASS " + message); }
    sealed class FixtureController : IAutomationUiController
    {
        public AutomationUiState State { get; set; } = new(false, "保存恢复记录", "本地收尾已关闭。", new[] { "保存恢复记录" });
        public int SaveCount { get; private set; }
        public int TestCount { get; private set; }
        public int PowerCalls => 0;
        public int ProcessCloseCalls => 0;
        public int ServiceStopCalls => 0;
        public TaskCompletionSource<string> TestCompletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public AutomationUiState GetState() => State;
        public AutomationUiSaveResult Save(bool enabled, string text)
        {
            SaveCount++;
            if (text != "关闭Clash和mihomo，然后关机") return new(false, "不支持的操作：请使用已支持的本地操作。原计划保留。", Array.Empty<string>());
            var steps = new[] { "创建恢复快照", "保存恢复记录", "关闭Clash界面并等待退出", "停止并验证mihomo相关进程 / 服务", "Windows正常关机" };
            State = new(enabled, text, enabled ? "等待新的有效额度耗尽事件。" : "本地收尾已关闭。", steps);
            return new(true, "操作计划已保存，未执行。", steps);
        }
        public Task<string> TestAsync(string text) { TestCount++; return TestCompletion.Task; }
        public string ReadLastHistory() => "上次执行：模拟验证\nCreateRecoverySnapshot: DryRun\nCloseApplicationGracefully: DryRun\nShutdownWindows: DryRun\n未关闭程序、服务或Windows。";
    }
}

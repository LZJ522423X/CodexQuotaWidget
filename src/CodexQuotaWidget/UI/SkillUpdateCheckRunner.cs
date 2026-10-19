using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexQuotaWidget.Core;

namespace CodexQuotaWidget;

internal static class SkillUpdateCheckRunner
{
    internal static async Task<int> RunAsync()
    {
        const string output = @"D:\CodexQuotaWidget-GitHub\artifacts\skill-update-1.11.1\preview";
        Directory.CreateDirectory(output);
        var checks = 0; var renders = 0; var realBusy = false; UnifiedUpdateCheck? liveCheck = null;
        void Check(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); checks++; Console.WriteLine("PASS " + message); }
        var completion = new TaskCompletionSource<SkillUpdateResult>(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0; var animations = true;
        SkillUpdateController Fixture(Func<Task<SkillUpdateResult>> execute) => new(execute, _ => Task.FromResult(FixtureCheck()));
        async Task Prepare(SkillUpdateController value) { await value.CheckAsync(); value.Select("humanizer", true); }
        var controller = Fixture(() => { calls++; return completion.Task; });
        var panel = new SkillUpdatePanel(() => animations, controller);
        var window = new Window { Content = panel, Width = 460, SizeToContent = SizeToContent.Height, ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None };
        var app = (App)Application.Current; var settings = new Settings { Theme = "Dark", Accent = "Violet", EnableAnimations = true };
        app.ConfigurePreviewTheme(settings); window.SetResourceReference(Window.BackgroundProperty, "Glass");
        string Hash(string path) => File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : "missing";
        var protectedPaths = new[] { SkillUpdateController.Script, SkillUpdateController.Root + @"\config\settings.json", SkillUpdateController.Root + @"\config\provenance.json",
            SkillUpdateController.Root + @"\logs\latest-result.json", @"C:\Users\FixtureUser\.codex\skills\skill-router-lite\SKILL.md" };
        var before = protectedPaths.Select(Hash).ToArray();
        try
        {
            window.Show(); await Settle();
            var button = Find<SkillUpdateButton>(panel); var status = Find<TextBlock>(panel, "SkillUpdateStatus");
            Check(!button.IsEnabled && button.ActualHeight == 52, "unconfirmed updates cannot start; stable 52 DIP target");
            await controller.RunAsync(); Check(calls == 0, "update button never checks or installs an unconfirmed update");
            await controller.CheckAsync(); await Settle();
            Check(calls == 0 && !button.IsEnabled && controller.CheckResult?.Items.Count == 2, "unified check displays component and Skill without installing or preselecting");
            controller.Select("humanizer", true); await Settle(); Check(button.IsEnabled, "explicit selection enables existing install button");
            var mirror = new SkillUpdatePanel(() => false, controller); controller.Select("humanizer", false); mirror.Refresh();
            Check(Find<CheckBox>(mirror).IsChecked == false && !Find<SkillUpdateButton>(mirror).IsEnabled, "shared settings surfaces reflect selection changes without another check");
            controller.Select("humanizer", true); await Settle();
            Check(System.Windows.Automation.AutomationProperties.GetName(button) == "更新 Codex Skills", "accessible action label");
            Check(!button.HasRotationAnimation, "idle has no permanent animation"); Capture("dark-idle");
            var task = controller.RunAsync(); await Settle();
            Check(calls == 1 && controller.IsRunning && !button.IsEnabled, "one job starts and disables repeated clicks");
            await controller.RunAsync(); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
            Check(calls == 1, "duplicate routed events never start another job");
            var width = button.ActualWidth; var height = button.ActualHeight;
            await Task.Delay(160); var angle = button.RotationAngle; await Task.Delay(140);
            Check(button.HasRotationAnimation && Math.Abs(button.RotationAngle - angle) > 5, "real WPF progress rotation advances");
            Check(button.ActualWidth == width && button.ActualHeight == height, "motion never resizes layout");
            Check(status.Text.Contains("正在更新"), "running status is visible"); Capture("dark-running");
            panel.Visibility = Visibility.Collapsed; await Settle();
            Check(!button.HasRotationAnimation && controller.IsRunning, "hidden settings stop motion but keep transaction running");
            panel.Visibility = Visibility.Visible; await Settle(); Check(button.HasRotationAnimation, "progress resumes when visible");
            animations = false; panel.Refresh();
            Check(!button.HasRotationAnimation && button.RotationAngle == 0 && !button.IsEnabled, "reduced motion retains duplicate protection"); Capture("dark-running-reduced-motion");
            animations = true; panel.Refresh(); Check(button.HasRotationAnimation, "animations can be restored during a job");
            completion.SetResult(SkillUpdateController.ParseResult("Codex Skill 更新完成\n\n成功更新：1\n已是最新：3\n跳过：0\n失败：0", 0));
            await task; await Settle();
            Check(!button.IsEnabled && !button.HasRotationAnimation && !controller.IsRunning, "completion invalidates consumed selection and removes clock");
            Check(status.Text.Contains("更新 1") && controller.State.Phase == SkillUpdatePhase.Completed, "success shows current run counts");
            Check(Find<Expander>(panel).Visibility == Visibility.Visible && !Find<Expander>(panel).IsExpanded, "details remain available and collapsed"); Capture("dark-completed");
            var partial = SkillUpdateController.ParseResult("Codex Skill 更新完成\n成功更新：0\n已是最新：2\n跳过：1\n失败：1\nui-ux-pro-max：模板验证失败", 1);
            Check(partial.Phase == SkillUpdatePhase.Attention && partial.Summary.Contains("失败 1") && partial.Details.Contains("模板验证失败"), "failure never becomes full success");
            var partialController = Fixture(() => Task.FromResult(partial)); await Prepare(partialController);
            var partialPanel = new SkillUpdatePanel(() => true, partialController); window.Content = partialPanel; await Settle(); await partialController.RunAsync(); await Settle(); Capture("dark-attention");
            Check(!button.HasRotationAnimation, "unloaded old panel has no clock");
            Check(SkillUpdateController.ParseResult("old result", 0).Phase == SkillUpdatePhase.Failed, "zero exit alone is not success");
            Check(SkillUpdateController.ParseResult("Codex Skill 更新完成\n成功更新：1", 0).Phase == SkillUpdatePhase.Failed, "partial output is rejected");
            Check(SkillUpdateController.ParseResult("另一次更新正在运行，请等待其完成。", 1).Phase == SkillUpdatePhase.Busy, "desktop concurrency feedback is explicit");
            var failing = Fixture(() => throw new IOException("fixture")); await Prepare(failing); await failing.RunAsync();
            Check(failing.State.Phase == SkillUpdatePhase.Failed && !failing.IsRunning, "launch failure releases gate");
            var closing = Fixture(() => Task.FromResult(new SkillUpdateResult(SkillUpdatePhase.Completed, "fixture"))); await Prepare(closing); var notified = 0;
            closing.Changed += () => throw new InvalidOperationException("fixture dispatcher shutdown"); closing.Changed += () => notified++;
            await closing.RunAsync(); Check(!closing.IsRunning && closing.State.Phase == SkillUpdatePhase.Completed && notified >= 3, "closing view cannot interrupt transaction, leak gate or block other observers");
            settings.Theme = "Light"; app.ConfigurePreviewTheme(settings); await Settle(); Capture("light-attention"); window.Content = panel; await Settle(); Capture("light-completed");
            using (var mutex = new Mutex(false, @"Local\CodexSkillUpdater-OneClick"))
            {
                bool owns; try { owns = mutex.WaitOne(0); } catch (AbandonedMutexException) { owns = true; }
                if (owns)
                {
                    try {
                        Check(SkillUpdateController.PowerShellPath() is not null, "existing Microsoft-signed PowerShell resolved without PATH");
                        try { await SkillUpdateController.CheckInstalledAsync(CancellationToken.None); throw new InvalidOperationException("busy check unexpectedly succeeded"); }
                        catch (IOException) { Check(true, "REAL read-only updater refuses mutex before scanning or network access"); realBusy = true; }
                    } finally { mutex.ReleaseMutex(); }
                }
                else Console.WriteLine("NOT VERIFIED real busy probe: another updater owns mutex");
            }
            Check(before.SequenceEqual(protectedPaths.Select(Hash)), "busy probe leaves updater/config/result/router bytes unchanged");
            if (Environment.GetCommandLineArgs().Contains("--include-live-update-check"))
            {
                var live = new SkillUpdateController(); window.Content = new SkillUpdatePanel(() => false, live);
                await live.CheckAsync(); await Settle(); liveCheck = live.CheckResult;
                Check(liveCheck?.Skills is not null, "REAL product controller reads supported read-only Skill results");
                Check(!live.CanInstall && !live.IsBusy && !Find<SkillUpdateButton>(window).IsEnabled, "REAL check does not select or install updates");
                Capture("live-readonly-results");
                File.WriteAllText(Path.Combine(output, "live-readonly-results.json"), System.Text.Json.JsonSerializer.Serialize(liveCheck));
                Check(before.SequenceEqual(protectedPaths.Select(Hash)), "REAL check leaves updater/config/prior update result/Router unchanged");
            }
            File.WriteAllText(Path.Combine(output, "check-result.json"), System.Text.Json.JsonSerializer.Serialize(new { Checks = checks, Renders = renders, RealUpdaterBusyProbe = realBusy,
                RealReadOnlyCheck = liveCheck is not null, RealSuccessfulSkillUpdate = false, SyntheticController = true, RealWpfMotion = true, ExternalUiAutomation = false, SkillFilesUpdated = false }));
            Console.WriteLine($"PASS skill-update-check: {checks} assertions, {renders} renders; real busy={realBusy}; no successful update claimed"); return 0;
        }
        finally { window.Hide(); window.Close(); }
        void Capture(string name)
        {
            window.UpdateLayout(); var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(Path.Combine(output, name + ".png")); encoder.Save(stream); renders++;
        }
    }
    static UnifiedUpdateCheck FixtureCheck()
    {
        var item = new SkillCheckItem { Name = "humanizer", Path = @"C:\Users\FixtureUser\.codex\skills\humanizer\SKILL.md", Status = UpdateCheckStatus.Available,
            CurrentVersion = "提交 " + new string('a', 12), LatestVersion = "提交 " + new string('b', 12), Reason = "离线 fixture",
            FromCommit = new string('a', 40), ToCommit = new string('b', 40), Repo = "blader/humanizer", RepoId = "123", Branch = "main" };
        var snapshot = new SkillCheckSnapshot { SchemaVersion = 1, CheckId = Guid.NewGuid(), CheckedAt = DateTimeOffset.UtcNow,
            ProvenanceHash = new string('c', 64), Items = [item] };
        return new([new("AI Control Center", "组件", UpdateCheckStatus.UpToDate, "1.12.7", "1.12.7", "离线 fixture"),
            new(item.Name, "Skill", item.Status, item.CurrentVersion, item.LatestVersion, item.Reason)], snapshot);
    }
    static T Find<T>(DependencyObject parent, string? name = null) where T : FrameworkElement => FindOrDefault<T>(parent, name) ?? throw new InvalidOperationException(typeof(T).Name + " missing");
    static T? FindOrDefault<T>(DependencyObject parent, string? name) where T : FrameworkElement
    {
        if (parent is T match && (name is null || match.Name == name)) return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) { var result = FindOrDefault<T>(VisualTreeHelper.GetChild(parent, i), name); if (result is not null) return result; }
        return null;
    }
    static async Task Settle() { await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); await Task.Delay(30); }
}

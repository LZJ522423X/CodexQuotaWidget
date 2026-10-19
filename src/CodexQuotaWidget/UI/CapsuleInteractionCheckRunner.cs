using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexQuotaWidget.Core;
using CodexQuotaWidget.Core.Automation;
using CodexQuotaWidget.Core.ControlCenter;

namespace CodexQuotaWidget;

/// <summary>Owned WPF/native fixtures only. AI, quota, pointer and fullscreen input are explicitly synthetic.</summary>
internal static class CapsuleInteractionCheckRunner
{
    static string PreviewDirectory => Environment.GetEnvironmentVariable("CQW_CAPSULE_CHECK_DIR")
        ?? @"D:\CodexQuotaWidget-GitHub\artifacts\capsule-interaction-preview-1.9.1";
    static int assertions;

    public static async Task<int> RunAsync()
    {
        assertions = 0;
        if (!AutomationStore.IsSafeDPath(PreviewDirectory) || !AutomationStore.IsSafeDPath(Paths.Data))
            throw new InvalidDataException("Capsule UI checks require isolated D-drive data and artifacts.");
        Directory.CreateDirectory(PreviewDirectory);
        var fixtureDirectory = Path.Combine(PreviewDirectory, "fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtureDirectory);
        var resultPath = Path.Combine(PreviewDirectory, "check-result.json");
        File.WriteAllText(resultPath, "{\"passed\":false,\"status\":\"running\",\"syntheticAi\":true}");
        var settingsPath = Path.Combine(fixtureDirectory, "position.json");
        var settings = new Settings { FollowCodex = false, EnableAnimations = false, Theme = "Dark", WindowMode = "Full", Width = 640, Height = 880 };
        var app = (App)Application.Current;
        app.ConfigurePreviewTheme(settings);
        var monitor = new QuotaMonitor(settings, () => false, () => false);
        // Set only the fixture's in-memory presentation; no server, cache write or account request.
        var now = DateTimeOffset.UtcNow;
        var quota = new QuotaSnapshot(now, "离线fixture", [new("codex", "5h", "primary", 30, 300, now.AddHours(1).ToUnixTimeSeconds()),
            new("codex", "weekly", "secondary", 44, 10080, now.AddDays(2).ToUnixTimeSeconds())], true, 2, []);
        typeof(QuotaMonitor).GetProperty(nameof(QuotaMonitor.Snapshot))!.SetValue(monitor, quota);
        typeof(QuotaMonitor).GetProperty(nameof(QuotaMonitor.Status))!.SetValue(monitor, MonitorStatus.Live);
        var center = new ControlCenterService(Path.Combine(fixtureDirectory, "queue.json"));
        center.UpdateQuota(quota, MonitorStatus.Live, false);
        var workflow = new FixtureWorkflow();
        var saveCalls = 0; var modeCalls = 0; var openCalls = 0; var refreshCalls = 0; var appearanceCalls = 0; var diagnosticCalls = 0;
        void Save() { saveCalls++; AtomicStore.Write(settingsPath, settings); }
        var capsule = new CapsuleWindow(monitor, () => openCalls++, () => refreshCalls++, center, workflow, Save, pointerProvider: () => null) { ShowActivated = false, Topmost = false };
        var full = new StatusWindow(monitor, () => { diagnosticCalls++; return Task.CompletedTask; }, Save, () => appearanceCalls++, () => modeCalls++, center, workflow: workflow) { ShowActivated = false, Topmost = false };
        // Route only the selected fixture click events; incidental user input cannot reach
        // the product's real refresh/login/update controls while this preview is visible.
        capsule.IsHitTestVisible = false;
        full.IsHitTestVisible = false;
        CapsuleWindow? restored = null;
        QuotaMonitor? restoredMonitor = null;
        var captures = new List<string>();
        var tooltipEvidence = new List<object>();
        var nativeEvidence = new List<object>();
        var fullscreenSimulationEvidence = new List<object>();
        try
        {
            capsule.ShowPanel();
            capsule.SetFullscreenForCheck(false);
            using (var baselineDisplays = new CapsuleDisplayEnvironment(Application.Current.Dispatcher))
            {
                var baselineScreen = baselineDisplays.GetMonitorForWindow(new WindowInteropHelper(capsule).Handle)
                    ?? throw new InvalidOperationException("Initial fixture monitor unavailable.");
                capsule.SetPositionForCheck(baselineScreen.WorkArea.Left + baselineScreen.WorkArea.Width / 2, baselineScreen.WorkArea.Top + 140 * baselineScreen.ScaleY);
            }
            await SettleAsync();
            Check(capsule.DockSideForCheck == CapsuleDockSide.None && !capsule.EdgeHiddenForCheck, "fixture's explicit interior position starts without docking");
            Check(monitor.AttemptCount == 0 && monitor.ServerPid is null, "in-memory quota fixture has not started Codex or account requests");

            // Exercise real WPF text layout at the longest normal five-hour countdown,
            // shorter countdowns and absent data, without touching account/session state.
            foreach (var theme in new[] { "Dark", "Light" })
            {
                settings.Theme = theme; app.ConfigurePreviewTheme(settings);
                foreach (var (name, resetAt) in new (string, long?)[]
                {
                    ("hours", now.AddHours(4).AddMinutes(55).ToUnixTimeSeconds()),
                    ("minutes", now.AddMinutes(48).ToUnixTimeSeconds()),
                    ("soon", now.AddSeconds(-1).ToUnixTimeSeconds()),
                    ("unknown", null)
                })
                {
                    var countdownQuota = quota with { Windows = [quota.Windows[0] with { ResetsAt = resetAt }, quota.Windows[1]] };
                    typeof(QuotaMonitor).GetProperty(nameof(QuotaMonitor.Snapshot))!.SetValue(monitor, countdownQuota);
                    capsule.RefreshView(); capsule.SetExpandedForLayout(true); await SettleAsync();
                    VerifyResetLayout(capsule, theme + "/" + name);
                    captures.Add(Capture(capsule, $"capsule-{theme.ToLowerInvariant()}-reset-{name}.png"));
                }
            }
            typeof(QuotaMonitor).GetProperty(nameof(QuotaMonitor.Snapshot))!.SetValue(monitor, quota);
            capsule.RefreshView();
            if (Environment.GetEnvironmentVariable("CQW_CAPSULE_RESET_LAYOUT_ONLY") == "1")
            {
                Check(openCalls == 0 && refreshCalls == 0 && workflow.OperationCalls == 0 && monitor.AttemptCount == 0,
                    "reset-layout observation performs no user callbacks or model/account requests");
                File.WriteAllText(resultPath, JsonSerializer.Serialize(new
                {
                    passed = true, assertions, captures, scope = "Reset label/readability only",
                    realWpfRendering = true, syntheticQuota = true,
                    existingNativeInteractionTested = false, realModelCalls = false,
                    processClosure = false, networkActions = false, powerActions = false
                }, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"PASS capsule-reset-layout-check: {assertions} assertions, {captures.Count} real WPF renders; synthetic quota; no model/network/system actions");
                return 0;
            }

            foreach (var state in new[] { "idle", "queued", "executing", "completed", "unconfirmed" })
            {
                if (state == "queued") Check(center.AddTask("验证胶囊任务提示与按钮，不派发AI请求。", "离线验收资料。").Success, "local fixture queue is written only under D artifacts");
                workflow.Present(state);
                foreach (var theme in new[] { "Dark", "Light" })
                {
                    settings.Theme = theme; app.ConfigurePreviewTheme(settings); capsule.RefreshView(); await SettleAsync();
                    Check(capsule.ActivityToolTipForCheck is null, $"{theme}/{state} activity has no hover tooltip");
                    CheckNoToolTips(capsule, $"{theme}/{state} capsule");
                    tooltipEvidence.Add(new { theme, state, hoverToolTips = 0, accessibilityHelpAvailable = !string.IsNullOrWhiteSpace(capsule.ActivityHelpForCheck) });
                    if (state == "idle")
                    {
                        Check(capsule.ActivitySummaryForCheck == "空闲", theme + " empty task state reports idle");
                        Check(capsule.ActivityToolTipForCheck is null, theme + " empty task state has no empty tooltip");
                    }
                    else
                    {
                        Check(!string.IsNullOrWhiteSpace(capsule.ActivityHelpForCheck), $"{theme}/{state} activity details remain available to accessibility without popup");
                        if (state == "queued") Check(capsule.ActivitySummaryForCheck.Contains("等待任务"), theme + " local queue is labelled pending rather than AI execution");
                        if (state == "executing") Check(capsule.ActivitySummaryForCheck.Contains("Codex 执行"), theme + " fake native-session workflow is displayed as fixture execution");
                        if (state == "unconfirmed") Check(capsule.ActivitySummaryForCheck.Contains("待确认"), theme + " persisted active phase without a running controller stays unconfirmed");
                    }
                    foreach (var expanded in new[] { false, true })
                    {
                        capsule.SetExpandedForLayout(expanded); await SettleAsync();
                        Check(capsule.IsSettledForAnimationCheck(expanded), $"{theme}/{state}/{expanded} fixture settles in requested state");
                        var visual = capsule.VisualBoundsForCheck;
                        Check(Math.Abs(visual.Width - (expanded ? 520 : 400)) < .6 && Math.Abs(visual.Height - (expanded ? 166 : 56)) < .6,
                            $"{theme}/{state}/{expanded} actual rounded visual has expected dimensions");
                        Check(visual.Left >= 0 && visual.Top >= 0 && visual.Right <= capsule.ActualWidth && visual.Bottom <= capsule.ActualHeight,
                            $"{theme}/{state}/{expanded} visual stays inside fixed transparent host");
                        captures.Add(Capture(capsule, $"capsule-{theme.ToLowerInvariant()}-{state}-{(expanded ? "expanded" : "collapsed")}.png"));
                    }
                }
            }

            workflow.Present("idle");
            foreach (var theme in new[] { "Dark", "Light" })
            {
                settings.Theme = theme; app.ConfigurePreviewTheme(settings); full.ShowPanel(); await SettleAsync();
                var header = new[] { "外观设置", "小窗显示", "隐藏到托盘" }.Select(name => AccessibleButton(full, name)).ToArray();
                Check(header.Select(button => button.TranslatePoint(new Point(), full).X).Zip(header.Skip(1).Select(button => button.TranslatePoint(new Point(), full).X), (left, right) => left < right).All(value => value),
                    theme + " header buttons are ordered settings, small window, hide");
                Check(header[0].Parent is StackPanel parent && parent.Children.OfType<Button>().SequenceEqual(header), theme + " header contains exactly the three requested buttons");
                var beforeMode = modeCalls; var beforeSave = saveCalls; var beforeAppearance = appearanceCalls;
                var initialPreferences = full.PreferencesVisibleForLayout;
                CheckNoToolTips(full, theme + " complete status window");
                Click(header[0]); await SettleAsync();
                Check(full.PreferencesVisibleForLayout != initialPreferences, theme + " settings button toggles the real preferences panel");
                CheckNoToolTips(full, theme + " visible appearance settings");
                Click(header[0]); await SettleAsync();
                Check(full.PreferencesVisibleForLayout == initialPreferences && appearanceCalls == beforeAppearance, theme + " toggling preferences alone does not mutate appearance");
                captures.Add(Capture(full, "status-" + theme.ToLowerInvariant() + "-header.png"));
                Click(header[1]); await SettleAsync();
                Check(modeCalls == beforeMode + 1 && saveCalls >= beforeSave + 1 && settings.WindowMode == "Capsule", theme + " small-window button saves mode and invokes exactly one mode callback");
                var closed = false; EventHandler onClosed = (_, _) => closed = true; full.Closed += onClosed;
                Click(header[2]); await SettleAsync();
                Check(!full.IsVisible && !closed, theme + " X hides to tray without closing the window or application");
                full.Closed -= onClosed;
            }

            capsule.SetFullscreenForCheck(false); capsule.SetExpandedForLayout(false); await SettleAsync();
            var beforeDragSaves = saveCalls;
            capsule.DragForCheck(61, 37); await SettleAsync();
            Check(saveCalls > beforeDragSaves, "drag completion invokes the injected real persistence callback");
            var persisted = AtomicStore.Read<Settings>(settingsPath) ?? throw new InvalidOperationException("Missing fixture settings.");
            var persistedCenter = persisted.CapsuleCenterX; var persistedTop = persisted.CapsuleTopY;
            Check(persisted.CapsuleCenterX.HasValue && persisted.CapsuleTopY.HasValue && !string.IsNullOrWhiteSpace(persisted.CapsuleMonitorDevice), "drag saves physical position and monitor identity under D artifacts");
            Check(persisted.CapsuleCenterX == settings.CapsuleCenterX && persisted.CapsuleTopY == settings.CapsuleTopY && persisted.CapsuleMonitorDevice == settings.CapsuleMonitorDevice,
                "saved physical position reads back exactly");
            var oldNative = NativeBounds(capsule);
            capsule.HideImmediately();
            restoredMonitor = new QuotaMonitor(persisted, () => false, () => false);
            restored = new CapsuleWindow(restoredMonitor, () => { }, () => { }, pointerProvider: () => null) { ShowActivated = false, Topmost = false };
            restored.IsHitTestVisible = false;
            restored.ShowPanel(); restored.SetFullscreenForCheck(false); await SettleAsync();
            var newNative = NativeBounds(restored);
            Check(Math.Abs(oldNative.X - newNative.X) <= 2 && Math.Abs(oldNative.Y - newNative.Y) <= 2,
                "new real WPF capsule restores saved position without mouse-dependent recentering");
            nativeEvidence.Add(VerifyTransparentInputMargins(restored));
            using (var displays = new CapsuleDisplayEnvironment(Application.Current.Dispatcher))
            {
                var ownHwnd = new WindowInteropHelper(restored).Handle;
                var monitorInfo = displays.GetMonitorForWindow(ownHwnd) ?? throw new InvalidOperationException("Native fixture monitor unavailable.");
                Check(newNative.Left >= monitorInfo.WorkArea.Left - 2 && newNative.Right <= monitorInfo.WorkArea.Right + 2
                    && newNative.Top >= monitorInfo.WorkArea.Top - 2 && newNative.Bottom <= monitorInfo.WorkArea.Bottom + 2, "restored native host fits its current monitor work area");
                nativeEvidence.AddRange(await VerifyNativeClassificationAsync(displays, monitorInfo, ownHwnd));
                restoredMonitor.Settings.CapsuleStaySeconds = .5;
                restored.SetExpandedForLayout(true);
                restored.SetPositionForCheck(monitorInfo.WorkArea.Left + monitorInfo.WorkArea.Width / 2, monitorInfo.WorkArea.Top + 10 * monitorInfo.ScaleY);
                restored.SetPresentationForCheck(CapsulePresentationMode.Edge); await SettleAsync();
                Check(restored.DockSideForCheck == CapsuleDockSide.Top && restored.EdgeHiddenForCheck && restored.IsSettledForAnimationCheck(false), "explicit top position collapses and docks independently of foreground mode");
                var hiddenBounds = NativeBounds(restored);
                var dpi = VisualTreeHelper.GetDpi(restored);
                var visibleStripPhysicalPixels = restored.VisibleEdgeStripForCheck.Height;
                Check(Math.Abs(visibleStripPhysicalPixels - 3 * dpi.DpiScaleY) <= 1,
                    "top-position hidden chrome exposes only the configured 3-DIP edge strip");
                fullscreenSimulationEvidence.Add(new { phase = "hidden", syntheticPosition = true, bounds = hiddenBounds,
                    logicalStripDips = 3, visibleStripPhysicalPixels, scaleY = dpi.DpiScaleY, actualForegroundBehaviorTested = false });
                nativeEvidence.Add(VerifyEdgeStripInput(restored, monitorInfo.Bounds.Top));
                Pointer(restored, true); await SettleAsync();
                Check(restored.DockSideForCheck == CapsuleDockSide.Top && !restored.EdgeHiddenForCheck && restored.IsSettledForAnimationCheck(true),
                    "synthetic hover reveals the edge-hidden capsule and expands its visual without activation");
                var revealBounds = NativeBounds(restored);
                Check(revealBounds.Top + restored.VisualBoundsForCheck.Top * dpi.DpiScaleY >= monitorInfo.Bounds.Top - 1,
                    "synthetic hover reveal places the expanded chrome inside the monitor");
                fullscreenSimulationEvidence.Add(new { phase = "hover-reveal", simulatedPointer = true, bounds = revealBounds, actualForegroundBehaviorTested = false });
                Pointer(restored, false);
                Check(restored.CollapsePending, "synthetic leave after position-dock reveal starts its dwell timer");
                await Task.Delay(700); await SettleAsync();
                Check(restored.DockSideForCheck == CapsuleDockSide.Top && restored.EdgeHiddenForCheck && restored.IsSettledForAnimationCheck(false),
                    "synthetic leave and dwell expiry return the top-docked capsule to the same strip");
                Check(Math.Abs(NativeBounds(restored).Top - hiddenBounds.Top) <= 2, "position-dock hide-reveal-hide returns to the original edge coordinates");
                restored.SetPresentationForCheck(CapsulePresentationMode.Normal); await SettleAsync();
                Check(restored.DockSideForCheck == CapsuleDockSide.Top && restored.EdgeHiddenForCheck, "ordinary foreground cannot undock a saved top position");
                restored.SetPositionForCheck(persistedCenter!.Value, persistedTop!.Value); await SettleAsync();
                Check(restored.DockSideForCheck == CapsuleDockSide.None && !restored.EdgeHiddenForCheck, "explicitly restoring the saved center position removes position docking");
                Check(Math.Abs(NativeBounds(restored).Y - newNative.Y) <= 2, "center-position restoration preserves the original physical coordinates");
            }
            Check(openCalls == 0 && refreshCalls == 0 && diagnosticCalls == 0 && workflow.OperationCalls == 0 && monitor.AttemptCount == 0 && monitor.ServerPid is null,
                "UI observation, rendering and header interaction perform no AI, quota refresh, diagnostic or model dispatch");
            var report = new
            {
                passed = true, assertions, captures, tooltipEvidence, nativeEvidence, fullscreenSimulationEvidence, fixtureDirectory,
                realWpf = new[] { "rendering", "no hover tooltips after refresh and opening settings", "accessibility help without popup", "header buttons and callback counts", "fixed native host", "drag persistence and reload", "owned native HWND classification", "native WindowFromPoint transparent margin pass-through" },
                simulated = new[] { "quota", "local queued task", "AI workflow phase and session", "button routed click input", "drag input", "explicit top position and hover-reveal-dwell-return" },
                notVerified = new[] { "subjective animation smoothness", "actual external foreground suppression and recovery", "physical multi-monitor dragging" },
                realModelCalls = false, processClosure = false, networkActions = false, powerActions = false
            };
            File.WriteAllText(resultPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"PASS capsule-interaction-check: {assertions} assertions; real WPF/native fixtures, synthetic AI/position input; no models/network/process/power actions");
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(resultPath, JsonSerializer.Serialize(new { passed = false, assertions, error = error.GetType().Name, syntheticAi = true }));
            throw;
        }
        finally
        {
            full.Hide(); full.StopTimers();
            capsule.HideImmediately(); capsule.StopTimers(); capsule.Close();
            if (restored is not null) { restored.HideImmediately(); restored.StopTimers(); restored.Close(); }
            await monitor.DisposeAsync();
            if (restoredMonitor is not null) await restoredMonitor.DisposeAsync();
        }
    }

    static void CheckNoToolTips(DependencyObject root, string description)
    {
        Check(Descendants<FrameworkElement>(root).All(element => element.ToolTip is null), description + " exposes no tooltip-bearing controls");
    }

    static void VerifyResetLayout(CapsuleWindow capsule, string description)
    {
        var label = Descendants<TextBlock>(capsule).Single(block => block.Name == "CapsuleResetLabel");
        var tag = Descendants<Border>(capsule).Single(border => border.Name == "CapsuleResetTag");
        var countdown = Descendants<TextBlock>(capsule).Single(block => block.Name == "CapsuleResetCountdown");
        var quota = Descendants<TextBlock>(capsule).Single(block => block.Text.StartsWith("外环 · 5h", StringComparison.Ordinal));
        Rect Bounds(FrameworkElement element) => element.TransformToAncestor(capsule).TransformBounds(new Rect(new Point(), element.RenderSize));
        var tagBounds = Bounds(tag); var countdownBounds = Bounds(countdown); var visual = capsule.VisualBoundsForCheck;
        Check(tag.IsVisible && countdown.IsVisible && tagBounds.Right + 7.5 <= countdownBounds.Left,
            description + " reset label and value have separate columns and visible spacing");
        Check(quota.FontSize > countdown.FontSize && quota.FontWeight == FontWeights.SemiBold && countdown.FontWeight == FontWeights.Normal,
            description + " quota percentage and reset countdown have distinct typographic hierarchy");
        Check(tagBounds.Left >= visual.Left && countdownBounds.Right <= visual.Right && countdownBounds.Bottom <= visual.Bottom,
            description + " reset row stays inside the rounded capsule");
        foreach (var block in new[] { label, countdown })
        {
            var text = new FormattedText(block.Text, System.Globalization.CultureInfo.CurrentUICulture, block.FlowDirection,
                new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch), block.FontSize,
                block.Foreground, VisualTreeHelper.GetDpi(block).PixelsPerDip);
            Check(text.WidthIncludingTrailingWhitespace <= block.ActualWidth + .5 && text.Height <= block.ActualHeight + .5,
                description + " reset text is fully readable without ellipsis or clipping: " + block.Name);
        }
        Check(AutomationProperties.GetName(countdown).StartsWith("5 小时额度重置倒计时：", StringComparison.Ordinal),
            description + " countdown retains its complete accessible meaning");
    }

    static void Pointer(CapsuleWindow capsule, bool inside)
    {
        var strip = capsule.VisibleEdgeStripForCheck; var shape = capsule.VisualBoundsForCheck;
        var point = capsule.EdgeHiddenForCheck && !strip.IsEmpty
            ? new Point(strip.Left + strip.Width / 2, strip.Top + strip.Height / 2)
            : capsule.PointToScreen(new Point(shape.Left + shape.Width / 2, shape.Top + shape.Height / 2));
        capsule.HandlePointerAtForCheck(inside, point.X, point.Y);
    }

    static async Task<IReadOnlyList<object>> VerifyNativeClassificationAsync(CapsuleDisplayEnvironment displays, CapsuleMonitorInfo monitor, IntPtr ownHwnd)
    {
        var observations = new List<object>();
        var desktop = GetDesktopWindow();
        Check(desktop != IntPtr.Zero && !displays.IsWindowFullscreen(desktop, ownHwnd, monitor), "real desktop HWND is not fullscreen content");
        Check(displays.GetWindowCoverage(desktop, ownHwnd, monitor) == CapsuleWindowCoverageKind.None, "desktop has no maximized/fullscreen coverage; capsule docking depends on its own position");
        observations.Add(new { kind = "desktop", classification = false, actualForegroundConfirmed = desktop == GetForegroundWindow() });
        var ordinary = new Window { Title = "胶囊原生窗口分类fixture", Width = 320, Height = 220, ShowActivated = false, ShowInTaskbar = false,
            Left = (monitor.WorkArea.Left + 90) / monitor.ScaleX, Top = (monitor.WorkArea.Top + 120) / monitor.ScaleY, Opacity = .01 };
        var borderless = new Window { Title = "胶囊全屏几何fixture", Width = monitor.Bounds.Width / monitor.ScaleX, Height = monitor.Bounds.Height / monitor.ScaleY,
            Left = monitor.Bounds.Left / monitor.ScaleX, Top = monitor.Bounds.Top / monitor.ScaleY, ShowActivated = false, ShowInTaskbar = false,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, AllowsTransparency = true, Background = Brushes.Transparent, Opacity = .01 };
        try
        {
            ordinary.Show(); await SettleAsync();
            var hwnd = new WindowInteropHelper(ordinary).Handle;
            Check(!displays.IsWindowFullscreen(hwnd, ownHwnd, monitor), "owned real decorated normal window is not fullscreen");
            Check(displays.GetWindowCoverage(hwnd, ownHwnd, monitor) == CapsuleWindowCoverageKind.None, "ordinary native window does not trigger edge hiding");
            observations.Add(new { kind = "ordinary", classification = false, bounds = NativeBounds(ordinary), actualForegroundConfirmed = hwnd == GetForegroundWindow() });
            ordinary.WindowState = WindowState.Maximized; await SettleAsync();
            Check(!displays.IsWindowFullscreen(hwnd, ownHwnd, monitor), "owned real decorated maximized window is not fullscreen");
            Check(displays.GetWindowCoverage(hwnd, ownHwnd, monitor) == CapsuleWindowCoverageKind.Maximized, "decorated native maximized window triggers edge hiding as maximized");
            observations.Add(new { kind = "maximized", classification = "Maximized", hidesAtEdge = true, bounds = NativeBounds(ordinary), actualForegroundConfirmed = hwnd == GetForegroundWindow() });
            ordinary.Hide();
            borderless.Show(); await SettleAsync();
            var fullHwnd = new WindowInteropHelper(borderless).Handle;
            Check(SetWindowPos(fullHwnd, IntPtr.Zero, (int)Math.Round(monitor.Bounds.Left), (int)Math.Round(monitor.Bounds.Top),
                (int)Math.Round(monitor.Bounds.Width), (int)Math.Round(monitor.Bounds.Height), 0x0004 | 0x0010), "only owned borderless fixture receives exact native monitor bounds without activation");
            await SettleAsync();
            Check(displays.IsWindowFullscreen(fullHwnd, ownHwnd, monitor), "owned real borderless monitor-sized HWND classifies as fullscreen geometry");
            Check(displays.GetWindowCoverage(fullHwnd, ownHwnd, monitor) == CapsuleWindowCoverageKind.Fullscreen, "borderless native monitor-sized window triggers edge hiding as fullscreen");
            observations.Add(new { kind = "borderless", classification = true, bounds = NativeBounds(borderless), actualForegroundConfirmed = fullHwnd == GetForegroundWindow(), automaticForegroundBehaviorTested = false });
            Check(!displays.IsWindowFullscreen(ownHwnd, ownHwnd, monitor), "capsule's own HWND is excluded from fullscreen classification");
            Check(displays.GetWindowCoverage(ownHwnd, ownHwnd, monitor) == CapsuleWindowCoverageKind.None, "capsule's own HWND never triggers edge hiding");
            return observations;
        }
        finally { ordinary.Close(); borderless.Close(); }
    }

    static object VerifyTransparentInputMargins(CapsuleWindow capsule)
    {
        Check(capsule.IsSettledForAnimationCheck(false), "native input pass-through fixture is stably collapsed");
        var hwnd = new WindowInteropHelper(capsule).Handle;
        var bounds = NativeBounds(capsule); var chrome = capsule.VisualBoundsForCheck; var dpi = VisualTreeHelper.GetDpi(capsule);
        bool OwnAt(Point local)
        {
            var point = new NativePoint { X = (int)Math.Round(bounds.Left + local.X * dpi.DpiScaleX), Y = (int)Math.Round(bounds.Top + local.Y * dpi.DpiScaleY) };
            var hit = WindowFromPoint(point);
            return hit == hwnd || hit != IntPtr.Zero && GetAncestor(hit, 2) == hwnd;
        }
        var chromeOwned = OwnAt(new Point(chrome.Left + chrome.Width / 2, chrome.Top + chrome.Height / 2));
        var topOwned = OwnAt(new Point(capsule.ActualWidth / 2, chrome.Top / 2));
        var leftOwned = OwnAt(new Point(chrome.Left / 2, chrome.Top + chrome.Height / 2));
        var bottomOwned = OwnAt(new Point(capsule.ActualWidth / 2, chrome.Bottom + (capsule.ActualHeight - chrome.Bottom) / 2));
        Check(chromeOwned, "real WindowFromPoint routes visible chrome pixels to the owned capsule HWND");
        Check(!topOwned && !leftOwned && !bottomOwned, "real WindowFromPoint lets alpha-zero top, left and bottom host margins pass through");
        return new { kind = "transparent-input-margins", nativePointQueries = true, chromeOwned, transparentTopOwned = topOwned,
            transparentLeftOwned = leftOwned, transparentBottomOwned = bottomOwned, pointerMoved = false, foregroundActivated = false };
    }

    static object VerifyEdgeStripInput(CapsuleWindow capsule, double monitorTop)
    {
        Check(capsule.EdgeHiddenForCheck && capsule.IsSettledForAnimationCheck(false), "edge-strip native input fixture is stably hidden and collapsed");
        var hwnd = new WindowInteropHelper(capsule).Handle;
        var bounds = NativeBounds(capsule); var chrome = capsule.VisualBoundsForCheck; var dpi = VisualTreeHelper.GetDpi(capsule);
        var stripTop = Math.Max(monitorTop, bounds.Top + chrome.Top * dpi.DpiScaleY);
        var stripBottom = bounds.Top + chrome.Bottom * dpi.DpiScaleY;
        var centerX = bounds.Left + (chrome.Left + chrome.Width / 2) * dpi.DpiScaleX;
        bool OwnAt(double x, double y)
        {
            var hit = WindowFromPoint(new NativePoint { X = (int)Math.Round(x), Y = (int)Math.Round(y) });
            return hit == hwnd || hit != IntPtr.Zero && GetAncestor(hit, 2) == hwnd;
        }
        var stripOwned = OwnAt(centerX, stripTop + (stripBottom - stripTop) / 2);
        var belowOwned = OwnAt(centerX, Math.Ceiling(stripBottom) + Math.Max(2, 2 * dpi.DpiScaleY));
        var leftOwned = OwnAt(bounds.Left + chrome.Left / 2 * dpi.DpiScaleX, stripTop + (stripBottom - stripTop) / 2);
        Check(stripOwned, "real WindowFromPoint routes only the visible edge strip to the owned capsule HWND");
        Check(!belowOwned && !leftOwned, "real WindowFromPoint lets alpha-zero pixels below and beside the edge strip pass through");
        return new { kind = "edge-strip-input", syntheticTopPosition = true, nativePointQueries = true, stripOwned,
            transparentBelowOwned = belowOwned, transparentLeftOwned = leftOwned, pointerMoved = false, foregroundActivated = false };
    }

    static Rect NativeBounds(Window window)
    {
        Check(GetWindowRect(new WindowInteropHelper(window).Handle, out var rectangle), "owned fixture native bounds are readable");
        return new Rect(rectangle.Left, rectangle.Top, rectangle.Right - rectangle.Left, rectangle.Bottom - rectangle.Top);
    }
    static Button AccessibleButton(DependencyObject root, string name)
        => Descendants<Button>(root).Single(button => AutomationProperties.GetName(button) == name);
    static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        if (root is not Visual && root is not System.Windows.Media.Media3D.Visual3D) yield break;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
    static void Click(Button button) { Check(button.IsEnabled, "owned fixture button is enabled"); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button)); }
    static async Task SettleAsync() { await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); await Task.Delay(80); }
    static string Capture(Window window, string name)
    {
        var width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth)); var height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
        var pixels = new byte[width * height * 4]; bitmap.CopyPixels(pixels, width * 4, 0);
        Check(Enumerable.Range(0, width * height).Any(index => pixels[index * 4 + 3] != 0), "nonblank actual WPF render " + name);
        var path = Path.Combine(PreviewDirectory, name); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write); encoder.Save(stream); return path;
    }
    static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException("Capsule interaction: " + description);
        assertions++; Console.WriteLine("  PASS " + description);
    }
    [StructLayout(LayoutKind.Sequential)] struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rectangle);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);

    sealed class FixtureWorkflow : IWorkflowLoopUiController
    {
        public WorkflowLoopConfiguration Configuration { get; } = new() { ProjectRoot = @"D:\CodexQuotaWidget-GitHub", Goal = "离线胶囊验收，不调用真实模型。" };
        public WorkflowLoopState State { get; private set; } = new();
        public bool IsRunning { get; private set; }
        public string ConnectionDescription => "离线fixture；没有真实AI连接。";
        public int OperationCalls { get; private set; }
        public Task<string> StartAsync(WorkflowLoopConfiguration configuration) { OperationCalls++; return Task.FromResult("离线fixture禁止派发模型。"); }
        public void Pause() => OperationCalls++;
        public string AcknowledgeRecovery() { OperationCalls++; return "离线fixture没有恢复动作。"; }
        public void Present(string state)
        {
            IsRunning = state == "executing";
            State = state is "idle" or "queued" ? new() : new()
            {
                RunId = Guid.Parse("00000000-0000-0000-0000-000000000001"), Phase = state == "completed" ? WorkflowLoopPhase.Completed : WorkflowLoopPhase.Executing,
                Round = 1, CurrentRequestId = IsRunning ? Guid.Parse("00000000-0000-0000-0000-000000000002") : Guid.Empty,
                CodexSessionId = "offline-native-session-fixture", Goal = string.Concat(Enumerable.Repeat("仅验收本地WPF呈现与可读性，禁止网络模型和系统动作。", 12)),
                Message = string.Concat(Enumerable.Repeat("这是明确标记的模拟工作流，不是真实模型运行证据。", 12))
            };
        }
    }
}

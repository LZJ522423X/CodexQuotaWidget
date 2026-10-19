using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CodexQuotaWidget.Core;
using CodexQuotaWidget.Core.Automation;
using CodexQuotaWidget.Core.ControlCenter;

namespace CodexQuotaWidget;

/// <summary>Real owned WPF animations/timers; synthetic physical cursor and foreground, never user input.</summary>
internal static class CapsuleHoverCheckRunner
{
    const string ArtifactPrefix = @"D:\CodexQuotaWidget-GitHub\artifacts\";
    static int assertions;

    public static async Task<int> RunAsync()
    {
        assertions = 0;
        var directory = RequireIsolatedPaths();
        Directory.CreateDirectory(directory);
        var report = Path.Combine(directory, "check-result.json");
        File.WriteAllText(report, "{\"passed\":false,\"status\":\"running\",\"syntheticPointer\":true}");
        using var displays = new CapsuleDisplayEnvironment(Application.Current.Dispatcher);
        var display = displays.Monitors.FirstOrDefault() ?? throw new InvalidOperationException("Hover fixture monitor unavailable.");
        var centerX = display.WorkArea.Left + display.WorkArea.Width / 2;
        var middleTop = display.WorkArea.Top + display.WorkArea.Height / 2;
        var settings = new Settings
        {
            FollowCodex = false, StartWithCodex = false, AutoStart = false, EnableAnimations = false,
            Theme = "Dark", WindowMode = "Capsule", CapsuleStaySeconds = .5,
            CapsuleMonitorDevice = display.DeviceName, CapsuleCenterX = centerX, CapsuleTopY = middleTop
        };
        ((App)Application.Current).ConfigurePreviewTheme(settings);
        var monitor = new QuotaMonitor(settings, () => false, () => false);
        Point? pointer = null;
        var openCalls = 0; var refreshCalls = 0; var saveCalls = 0;
        var capsule = new CapsuleWindow(monitor, () => openCalls++, () => refreshCalls++,
            savePosition: () => saveCalls++, pointerProvider: () => pointer)
        { ShowActivated = false, Topmost = false, IsHitTestVisible = false };
        var evidence = new List<object>();
        void Hover(Point point)
        {
            pointer = point;
            capsule.HandlePointerAtForCheck(true, point.X, point.Y);
            capsule.ObservePointerForCheck();
        }
        void Observe(Point point)
        {
            pointer = point;
            capsule.ObservePointerForCheck();
        }
        try
        {
            capsule.SetPresentationForCheck(CapsulePresentationMode.Suppressed);
            capsule.ShowPanel(); await SettleAsync(50);
            capsule.SetPresentationForCheck(CapsulePresentationMode.Normal); await SettleAsync(50);
            Check(monitor.AttemptCount == 0 && monitor.ServerPid is null, "fixture starts without account or model requests");
            foreach (var animations in new[] { false, true })
            {
                settings.EnableAnimations = animations;
                ((App)Application.Current).ConfigurePreviewTheme(settings);
                capsule.RefreshView();
                foreach (var side in new[] { CapsuleDockSide.Top, CapsuleDockSide.Left, CapsuleDockSide.Right })
                {
                    pointer = null;
                    capsule.SetPositionForCheck(side == CapsuleDockSide.Left ? display.WorkArea.Left
                        : side == CapsuleDockSide.Right ? display.WorkArea.Right - 1 : centerX,
                        side == CapsuleDockSide.Top ? display.WorkArea.Top + 20 * display.ScaleY : middleTop);
                    await SettleAsync(animations ? 750 : 60);
                    var label = side + "/animations=" + animations;
                    Check(capsule.DockSideForCheck == side && capsule.EdgeHiddenForCheck
                        && capsule.IsSettledForAnimationCheck(false), label + " hidden fixture starts at its dock");
                    var strip = capsule.VisibleEdgeStripForCheck;
                    var stripPoint = Center(strip);
                    var gap = EdgeGap(strip, side, display);
                    Hover(gap); await SettleAsync(120);
                    Check(capsule.EdgeHiddenForCheck && !capsule.ExpandedForCheck && !capsule.CollapsePending,
                        label + " 3–10 DIP edge gap does not reveal or expand");

                    Hover(stripPoint);
                    Check(!capsule.EdgeHiddenForCheck && capsule.ExpandedForCheck,
                        label + " one genuine strip visit reveals once");
                    await WaitUntilAsync(() => capsule.EdgeHiddenForCheck, 3000, label + " stationary strip cursor expires its dwell");
                    Check(!capsule.ExpandedForCheck, label + " expiry starts the actual collapse");
                    for (var repeat = 0; repeat < 5; repeat++)
                    {
                        Hover(stripPoint); await SettleAsync(75);
                        Check(capsule.EdgeHiddenForCheck && !capsule.ExpandedForCheck,
                            label + " same strip point cannot reverse retreat #" + (repeat + 1));
                    }
                    await SettleAsync(animations ? 650 : 50);
                    Check(capsule.IsSettledForAnimationCheck(false), label + " collapse animation settles while cursor stays put");
                    for (var repeat = 0; repeat < 5; repeat++)
                    {
                        Hover(stripPoint); await SettleAsync(70);
                        Check(capsule.EdgeHiddenForCheck && !capsule.ExpandedForCheck,
                            label + " repeated synthetic enter at stationary strip remains hidden #" + (repeat + 1));
                    }

                    Observe(gap);
                    Hover(stripPoint);
                    Check(!capsule.EdgeHiddenForCheck && capsule.ExpandedForCheck,
                        label + " physical leave of resting strip rearms next visit");
                    // Follow the visible body during its slide so the independent dwell check
                    // is not conflated with the intentionally stationary strip case above.
                    var start = DateTime.UtcNow;
                    do
                    {
                        Observe(BodyPoint(capsule)); await SettleAsync(40);
                    } while (DateTime.UtcNow - start < TimeSpan.FromMilliseconds(animations ? 800 : 80));
                    Check(!capsule.EdgeHiddenForCheck && capsule.IsSettledForAnimationCheck(true),
                        label + " visible body settles expanded");
                    var body = BodyPoint(capsule);
                    Observe(body); await SettleAsync(750);
                    Check(!capsule.EdgeHiddenForCheck && capsule.ExpandedForCheck && !capsule.CollapsePending,
                        label + " stationary body hover cancels collapse");

                    // This is inside the fixed transparent HWND, above the rounded chrome.
                    var hostPadding = capsule.PointToScreen(new Point(capsule.ActualWidth / 2, 4));
                    Hover(hostPadding);
                    Check(capsule.CollapsePending, label + " transparent 8-DIP host padding starts dwell");
                    for (var repeat = 0; repeat < 7 && !capsule.EdgeHiddenForCheck; repeat++)
                    {
                        Hover(hostPadding); await SettleAsync(100);
                    }
                    Check(capsule.EdgeHiddenForCheck && !capsule.ExpandedForCheck,
                        label + " repeated transparent-host movement does not restart dwell");
                    await SettleAsync(animations ? 700 : 60);
                    evidence.Add(new { side = side.ToString(), animations, strip, stripPoint, gap, body,
                        transparentHostPoint = hostPadding, repeatsDuringRetreat = 5, repeatsAfterRetreat = 5,
                        physicalPointerMoved = false });
                }
            }

            // Ordinary, undocked capsule: the host margin must not imply visible hover.
            pointer = null; settings.EnableAnimations = true; capsule.RefreshView();
            capsule.SetPositionForCheck(centerX, middleTop); await SettleAsync(750);
            Check(capsule.DockSideForCheck == CapsuleDockSide.None && !capsule.EdgeHiddenForCheck,
                "interior fixture has no edge docking");
            var normalGap = capsule.PointToScreen(new Point(capsule.ActualWidth / 2, 4));
            Hover(normalGap); await SettleAsync(180);
            Check(!capsule.ExpandedForCheck && !capsule.CollapsePending, "ordinary transparent gap cannot expand collapsed capsule");
            Hover(BodyPoint(capsule));
            for (var repeat = 0; repeat < 20; repeat++) { Observe(BodyPoint(capsule)); await SettleAsync(40); }
            Check(capsule.IsSettledForAnimationCheck(true), "ordinary visible body can expand without an edge visit");
            Hover(normalGap); await SettleAsync(1200);
            Check(capsule.IsSettledForAnimationCheck(false) && !capsule.EdgeHiddenForCheck,
                "ordinary body exit into transparent host collapses without creating a dock");

            Hover(BodyPoint(capsule));
            for (var repeat = 0; repeat < 20; repeat++) { Observe(BodyPoint(capsule)); await SettleAsync(40); }
            capsule.BeginDragForCheck();
            Hover(normalGap); capsule.HandlePointerAtForCheck(false, normalGap.X, normalGap.Y);
            await SettleAsync(750);
            Check(capsule.ExpandedForCheck && !capsule.CollapsePending, "drag lifecycle rejects hover and does not schedule a collapse");
            pointer = BodyPoint(capsule); capsule.EndDragForCheck(); await SettleAsync(80);
            Check(capsule.DockSideForCheck == CapsuleDockSide.None, "ending fixture drag preserves interior placement");

            pointer = null; capsule.SetPositionForCheck(centerX, display.WorkArea.Top + 20 * display.ScaleY);
            await SettleAsync(750); var gameStrip = Center(capsule.VisibleEdgeStripForCheck);
            capsule.SetPresentationForCheck(CapsulePresentationMode.Suppressed); await SettleAsync(80);
            Hover(gameStrip); await SettleAsync(700);
            Check(capsule.SuppressedForCheck && capsule.EffectiveOpacityForCheck <= .001
                && !capsule.ExpandedForCheck && !capsule.CollapsePending,
                "synthetic game suppression refuses edge hover and remains invisible");
            Check(openCalls == 0 && refreshCalls == 0 && monitor.AttemptCount == 0 && monitor.ServerPid is null,
                "all fixtures performed no callbacks, quota requests or AI operations");
            File.WriteAllText(report, JsonSerializer.Serialize(new
            {
                passed = true, assertions, evidence, saveCalls,
                realChecks = new[] { "owned nonactivating WPF window geometry", "real DispatcherTimer dwell", "actual morph/position animations", "isolated D artifacts output" },
                syntheticChecks = new[] { "physical-coordinate cursor visits", "mouse enter/leave events", "Normal/Suppressed foreground", "drag lifecycle" },
                notVerified = new[] { "physical user hover", "multiple-monitor and changed-DPI hover", "actual game transition", "native input ownership under other windows" },
                pointerMoved = false, userWindowActivation = false, models = false, network = false, processClosure = false, powerActions = false
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"PASS capsule-hover-check: {assertions} assertions; real WPF/timers/animations, synthetic pointer/foreground; no user input or AI/system actions");
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(report, JsonSerializer.Serialize(new
            { passed = false, assertions, error = error.GetType().Name, message = error.Message, evidence, syntheticPointer = true },
                new JsonSerializerOptions { WriteIndented = true }));
            throw;
        }
        finally { capsule.HideImmediately(); capsule.StopTimers(); capsule.Close(); await monitor.DisposeAsync(); }
    }

    static string RequireIsolatedPaths()
    {
        var output = Environment.GetEnvironmentVariable("CQW_CAPSULE_HOVER_CHECK_DIR");
        var configured = Environment.GetEnvironmentVariable("CODEX_QUOTA_WIDGET_DATA");
        static string Validate(string? value)
        {
            if (string.IsNullOrWhiteSpace(value) || !AutomationStore.IsSafeDPath(value))
                throw new InvalidDataException("Hover checks require explicit isolated D artifacts paths.");
            var full = Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar);
            if (full.Length <= ArtifactPrefix.Length || !full.StartsWith(ArtifactPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Hover checks refuse production or non-artifacts paths.");
            return full;
        }
        var directory = Validate(output); var data = Validate(configured);
        if (!string.Equals(data, Validate(Paths.Data), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Hover checks refuse mismatched data paths.");
        return directory;
    }

    static Point BodyPoint(CapsuleWindow capsule) => capsule.PointToScreen(Center(capsule.VisualBoundsForCheck));
    static Point Center(Rect rectangle)
    {
        if (rectangle.IsEmpty || rectangle.Width <= 0 || rectangle.Height <= 0)
            throw new InvalidOperationException("Hover fixture visible geometry unavailable.");
        return new(rectangle.Left + rectangle.Width / 2, rectangle.Top + rectangle.Height / 2);
    }
    static Point EdgeGap(Rect strip, CapsuleDockSide side, CapsuleMonitorInfo monitor) => side switch
    {
        CapsuleDockSide.Top => new(strip.Left + strip.Width / 2, monitor.Bounds.Top + 6 * monitor.ScaleY),
        CapsuleDockSide.Left => new(monitor.Bounds.Left + 6 * monitor.ScaleX, strip.Top + strip.Height / 2),
        _ => new(monitor.Bounds.Right - 6 * monitor.ScaleX, strip.Top + strip.Height / 2)
    };
    static async Task SettleAsync(int milliseconds)
    {
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(milliseconds);
    }
    static async Task WaitUntilAsync(Func<bool> condition, int timeoutMilliseconds, string description)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (!condition() && DateTime.UtcNow < deadline) await SettleAsync(35);
        Check(condition(), description);
    }
    static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException("Capsule hover fixture: " + description);
        assertions++; Console.WriteLine("  PASS " + description);
    }
}

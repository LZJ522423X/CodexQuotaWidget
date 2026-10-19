using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CodexQuotaWidget.Core;
using CodexQuotaWidget.Core.Automation;
using CodexQuotaWidget.Core.ControlCenter;

namespace CodexQuotaWidget;

/// <summary>
/// Owned real foreground windows exercise Normal/Edge/Suppressed policy, observer/fallback and native movement.
/// Pointer enter/leave are synthetic. No fullscreen override, AI, account, network or power operations.
/// </summary>
internal static class CapsuleForegroundCheckRunner
{
    const string ArtifactsRoot = @"D:\CodexQuotaWidget-GitHub\artifacts";
    // Keep this expectation aligned with the user-confirmed product policy.
    const bool ExpectedMaximizedHidden = false;
    static int assertions;

    public static async Task<int> RunAsync()
    {
        assertions = 0;
        var configuredData = Environment.GetEnvironmentVariable("CODEX_QUOTA_WIDGET_DATA");
        if (string.IsNullOrWhiteSpace(configuredData))
            throw new InvalidDataException("Foreground checks require an explicit isolated artifacts data directory.");
        var data = Path.GetFullPath(configuredData).TrimEnd(Path.DirectorySeparatorChar);
        var prefix = Path.GetFullPath(ArtifactsRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!data.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !AutomationStore.IsSafeDPath(data)
            || !string.Equals(Path.GetFullPath(Paths.Data).TrimEnd(Path.DirectorySeparatorChar), data, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Foreground checks refuse data outside the project's D-drive artifacts.");
        Directory.CreateDirectory(data);
        var reportPath = Path.Combine(data, "capsule-foreground-check.json");
        var evidence = new List<object>();
        var motion = new List<object>();
        var watch = Stopwatch.StartNew();
        var originalForeground = GetForegroundWindow();
        var settings = new Settings { FollowCodex = false, EnableAnimations = true, Theme = "Dark", WindowMode = "Capsule", CapsuleStaySeconds = .5 };
        ((App)Application.Current).ConfigurePreviewTheme(settings);
        var monitor = new QuotaMonitor(settings, () => false, () => false);
        var openCalls = 0; var refreshCalls = 0;
        var capsule = new CapsuleWindow(monitor, () => openCalls++, () => refreshCalls++, pointerProvider: () => null) { ShowActivated = false, Topmost = false };
        // Prevent incidental real pointer/button input from starting fixture operations.
        // The explicitly synthetic pointer methods remain available to the runner.
        capsule.IsHitTestVisible = false;
        Window? ordinary = null, borderless = null;
        IntPtr capsuleHwnd = IntPtr.Zero, ordinaryHwnd = IntPtr.Zero, borderlessHwnd = IntPtr.Zero;
        var status = "Passed";
        var resultCode = 0;
        string? diagnostic = null;
        var restoreAttempted = false;
        var restoreAccepted = false;
        var restoreConfirmed = false;
        NativeRect? lastMotion = null;
        try
        {
            capsule.ShowPanel();
            await SettleAsync(150);
            capsuleHwnd = new WindowInteropHelper(capsule).Handle;
            using var display = new CapsuleDisplayEnvironment(Application.Current.Dispatcher);
            var screen = display.GetMonitorForWindow(capsuleHwnd)
                ?? throw new InvalidOperationException("Fixture monitor could not be read.");
            var centerX = screen.WorkArea.Left + screen.WorkArea.Width / 2;
            var centerTop = screen.WorkArea.Top + screen.WorkArea.Height / 2;
            capsule.SetPositionForCheck(centerX, centerTop);
            ordinary = FixtureWindow("AI Control Center 普通/最大化前台验收", false);
            ordinary.Width = 340; ordinary.Height = 240;
            ordinary.Left = (screen.WorkArea.Left + 80 * screen.ScaleX) / screen.ScaleX;
            ordinary.Top = (screen.WorkArea.Top + 100 * screen.ScaleY) / screen.ScaleY;
            ordinary.Show();
            ordinaryHwnd = new WindowInteropHelper(ordinary).Handle;
            Require(SetWindowPos(ordinaryHwnd, IntPtr.Zero, Pixel(screen.WorkArea.Left + 80 * screen.ScaleX),
                Pixel(screen.WorkArea.Top + 100 * screen.ScaleY), Pixel(340 * screen.ScaleX), Pixel(240 * screen.ScaleY), 0x0004 | 0x0010),
                "only owned ordinary fixture is placed on the capsule monitor");
            await ActivateAsync(ordinary, "ordinary");
            await WaitAsync(ordinaryHwnd, "ordinary-initial-restore", () => capsule.DockSideForCheck == CapsuleDockSide.None
                && !capsule.EdgeHiddenForCheck && !capsule.SuppressedForCheck
                && capsule.EffectiveOpacityForCheck >= .99 && capsule.IsSettledForAnimationCheck(false), 4500);
            await RemainOrdinaryAsync(ordinaryHwnd, "ordinary", 2200);
            Require(!display.IsForegroundFullscreen(capsuleHwnd, screen), "real ordinary foreground is classified as ordinary");
            // A maximized user's window may already hide the capsule at startup. Capture the
            // ordinary baseline only after the owned ordinary foreground has actually restored it.
            var baseline = Bounds(capsule);
            evidence.Add(new { step = "ordinary", actualForegroundConfirmed = true, edgeHidden = capsule.EdgeHiddenForCheck,
                capsuleBounds = Bounds(capsule), fixtureBounds = Bounds(ordinary) });

            ordinary.WindowState = WindowState.Maximized;
            await ActivateAsync(ordinary, "maximized");
            Require(IsZoomed(ordinaryHwnd), "owned foreground fixture is truly maximized in Windows");
            await WaitAsync(ordinaryHwnd, "maximized-center-preserved", OrdinaryAtBaseline, 4500);
            Require(!display.IsForegroundFullscreen(capsuleHwnd, screen),
                "real decorated maximized window remains distinct from true fullscreen");
            Require(display.GetForegroundPresentation(capsuleHwnd, screen) == CapsulePresentationMode.Edge,
                "ordinary maximized foreground is classified without requesting complete suppression");
            Require(!capsule.EdgeHiddenForCheck && capsule.DockSideForCheck == CapsuleDockSide.None,
                "maximizing the owned foreground cannot move or dock a center-position capsule");
            evidence.Add(new { step = "maximized", actualForegroundConfirmed = true, expectedHidden = ExpectedMaximizedHidden,
                edgeHidden = capsule.EdgeHiddenForCheck, suppressed = capsule.SuppressedForCheck,
                positionPolicy = "center remains undocked", fixtureBounds = Bounds(ordinary) });

            capsule.SetPositionForCheck(centerX, screen.WorkArea.Top + 10 * screen.ScaleY);
            await WaitAsync(ordinaryHwnd, "explicit-top-position-dock", HiddenAtEdge, 2200);

            // Hover/re-leave/dwell belong to Edge mode. A non-browser fullscreen fixture below
            // now requests complete suppression, where synthetic enter must never reveal it.
            Pointer(capsule, true);
            AssertForeground(ordinaryHwnd, "synthetic-enter-dispatch");
            await WaitAsync(ordinaryHwnd, "synthetic-enter-real-reveal", () => !capsule.EdgeHiddenForCheck
                && !capsule.SuppressedForCheck && capsule.DockSideForCheck == CapsuleDockSide.Top && capsule.IsSettledForAnimationCheck(true), 1600);
            evidence.Add(new { step = "maximized-reveal", pointerInput = "synthetic enter", actualForegroundConfirmed = true,
                capsuleBounds = Bounds(capsule), visualHeight = capsule.VisualBoundsForCheck.Height });

            Pointer(capsule, false);
            Require(capsule.CollapsePending, "synthetic leave arms the real half-second dwell timer in Edge mode");
            await SettleAsync(180);
            AssertForeground(ordinaryHwnd, "dwell-before-expiry");
            Require(!capsule.EdgeHiddenForCheck && !capsule.SuppressedForCheck,
                "the real dwell timer does not hide before its configured delay");
            await WaitAsync(ordinaryHwnd, "dwell-expiry-real-edge-hide", HiddenAtEdge, 2200);
            evidence.Add(new { step = "maximized-leave-dwell-hide", pointerInput = "synthetic leave", configuredDwellSeconds = .5,
                actualForegroundConfirmed = true, visibleStripPhysicalPixels = Strip(), capsuleBounds = Bounds(capsule) });

            ordinary.WindowState = WindowState.Normal;
            await ActivateAsync(ordinary, "ordinary-restored-before-fullscreen");
            await WaitAsync(ordinaryHwnd, "ordinary-preserves-position-dock", HiddenAtEdge, 4500);
            capsule.SetPositionForCheck(centerX, centerTop);
            await WaitAsync(ordinaryHwnd, "ordinary-restored-before-fullscreen", OrdinaryAtBaseline, 4500);
            borderless = FixtureWindow("AI Control Center 无边框真实前台验收", true);
            borderless.Left = screen.Bounds.Left / screen.ScaleX; borderless.Top = screen.Bounds.Top / screen.ScaleY;
            borderless.Width = screen.Bounds.Width / screen.ScaleX; borderless.Height = screen.Bounds.Height / screen.ScaleY;
            borderless.Show();
            borderlessHwnd = new WindowInteropHelper(borderless).Handle;
            await SettleAsync(100);
            Require(SetWindowPos(borderlessHwnd, IntPtr.Zero, Pixel(screen.Bounds.Left), Pixel(screen.Bounds.Top),
                Pixel(screen.Bounds.Width), Pixel(screen.Bounds.Height), 0x0004 | 0x0010), "only owned borderless fixture receives physical monitor bounds");
            await ActivateAsync(borderless, "borderless-fullscreen");
            Require(display.IsForegroundFullscreen(capsuleHwnd, screen), "actual owned borderless foreground covers the capsule monitor");
            Require(display.GetForegroundPresentation(capsuleHwnd, screen) == CapsulePresentationMode.Suppressed,
                "owned non-browser fullscreen foreground requests complete suppression");
            await WaitAsync(borderlessHwnd, "automatic-nonbrowser-fullscreen-suppression", FullySuppressed, 4500);
            Require(!capsule.EdgeHiddenForCheck && !capsule.FullscreenForCheck,
                "non-browser fullscreen suppression leaves no edge-strip state");
            evidence.Add(new { step = "automatic-nonbrowser-fullscreen-suppression", actualForegroundConfirmed = true,
                fullscreenOverrideUsed = false, presentation = "Suppressed",
                observer = "real native foreground with product timer fallback; own-process hook events are excluded",
                windowOpacity = capsule.Opacity, effectiveOpacity = capsule.EffectiveOpacityForCheck,
                hitTestVisible = capsule.IsHitTestVisible, observerRunning = capsule.DisplayObserverRunningForCheck });

            Pointer(capsule, true);
            await SettleAsync(300);
            AssertForeground(borderlessHwnd, "suppressed-synthetic-enter");
            Require(FullySuppressed(), "synthetic pointer enter cannot reveal a completely suppressed capsule");
            Pointer(capsule, false);
            Require(!capsule.CollapsePending, "suppressed pointer leave does not arm hover recovery or dwell actions");
            Require(FullySuppressed(), "suppression remains transparent, non-interactive and observed after synthetic leave");
            evidence.Add(new { step = "suppressed-hover-does-not-reveal", pointerInput = "synthetic enter and leave",
                actualForegroundConfirmed = true, windowOpacity = capsule.Opacity,
                effectiveOpacity = capsule.EffectiveOpacityForCheck, hitTestVisible = capsule.IsHitTestVisible,
                observerRunning = capsule.DisplayObserverRunningForCheck });

            borderless.Hide();
            ordinary.WindowState = WindowState.Normal;
            await ActivateAsync(ordinary, "ordinary-after-fullscreen");
            await WaitAsync(ordinaryHwnd, "automatic-ordinary-restore", OrdinaryAtBaseline, 4500);
            Require(!display.IsForegroundFullscreen(capsuleHwnd, screen), "real foreground classification returns to ordinary");
            evidence.Add(new { step = "automatic-ordinary-restore", actualForegroundConfirmed = true,
                originalCapsuleTop = baseline.Top, restoredCapsuleTop = Bounds(capsule).Top,
                suppressed = capsule.SuppressedForCheck, effectiveOpacity = capsule.EffectiveOpacityForCheck,
                observerRunning = capsule.DisplayObserverRunningForCheck });
            Require(openCalls == 0 && refreshCalls == 0 && monitor.AttemptCount == 0 && monitor.ServerPid is null,
                "foreground observation and synthetic hover dispatch no account/model/network requests");

            bool HiddenAtEdge() => capsule.DockSideForCheck == CapsuleDockSide.Top && capsule.EdgeHiddenForCheck
                && !capsule.SuppressedForCheck && capsule.EffectiveOpacityForCheck >= .99
                && capsule.IsSettledForAnimationCheck(false) && Math.Abs(Strip() - 3 * DpiY()) <= 2;
            bool FullySuppressed() => capsule.SuppressedForCheck && capsule.Opacity <= .001
                && capsule.EffectiveOpacityForCheck <= .001 && !capsule.IsHitTestVisible
                && capsule.DisplayObserverRunningForCheck;
            bool OrdinaryAtBaseline() => capsule.DockSideForCheck == CapsuleDockSide.None && !capsule.EdgeHiddenForCheck
                && !capsule.SuppressedForCheck && capsule.EffectiveOpacityForCheck >= .99
                && capsule.IsSettledForAnimationCheck(false) && Math.Abs(Bounds(capsule).Top - baseline.Top) <= 2;
            double DpiY() => VisualTreeHelper.GetDpi(capsule).DpiScaleY;
            double Strip() => capsule.VisibleEdgeStripForCheck.Height;
        }
        catch (ForegroundNotVerifiedException error)
        {
            status = "NotVerified"; resultCode = 2; diagnostic = error.Message;
            evidence.Add(new { step = "foreground-permission-or-user-interruption", status, reason = diagnostic });
        }
        catch (Exception error)
        {
            status = "Failed"; resultCode = 1; diagnostic = error.GetType().Name;
            Console.WriteLine("FAIL capsule-foreground-check: " + diagnostic);
        }
        finally
        {
            var foregroundBeforeCleanup = GetForegroundWindow();
            var ownsForeground = foregroundBeforeCleanup != IntPtr.Zero
                && new[] { ordinaryHwnd, borderlessHwnd, capsuleHwnd }.Contains(foregroundBeforeCleanup);
            // Close only the windows created by this run. Never hide, close or reposition the user's windows.
            borderless?.Close(); ordinary?.Close();
            capsule.HideImmediately(); capsule.StopTimers(); capsule.Close();
            await monitor.DisposeAsync();
            if (ownsForeground && originalForeground != IntPtr.Zero && IsWindow(originalForeground))
            {
                restoreAttempted = true; restoreAccepted = SetForegroundWindow(originalForeground);
                await SettleAsync(60); restoreConfirmed = GetForegroundWindow() == originalForeground;
            }
            // If the user deliberately changed focus during the check, keep their new choice.
            var report = new
            {
                status, passed = status == "Passed", assertions, elapsedSeconds = watch.Elapsed.TotalSeconds,
                expectedMaximizedHidden = ExpectedMaximizedHidden, evidence, nativeMovementSamples = motion,
                expectedNonBrowserFullscreenPresentation = "Suppressed",
                foregroundRestore = new { attempted = restoreAttempted, nativeCallAccepted = restoreAccepted,
                    confirmed = restoreConfirmed, preservedNewUserFocus = !ownsForeground },
                real = new[] { "owned foreground HWND", "native foreground classification", "visible product timer fallback",
                    "maximized center-position preservation", "explicit top-position docking and real dwell timer",
                    "non-browser fullscreen zero-opacity suppression", "observer running during suppression", "ordinary-position restoration" },
                synthetic = new[] { "pointer enter and leave" },
                notVerified = new[] { "physical pointer hover", "external browser maximized/F11 foreground", "identified real game foreground", "exclusive fullscreen game",
                    "external-process WinEvent event delivery", "cross-monitor real dragging", "subjective smoothness" },
                fullscreenOverrideUsed = false, models = false, accountReads = false, network = false,
                userProcessClosure = false, userWindowMoves = false, powerActions = false, diagnostic
            };
            File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"{(status == "Passed" ? "PASS" : status)} capsule-foreground-check: {assertions} assertions; report {reportPath}");
        }
        return resultCode;

        async Task ActivateAsync(Window window, string step)
        {
            window.Activate();
            var hwnd = new WindowInteropHelper(window).Handle;
            var nativeAccepted = SetForegroundWindow(hwnd);
            var until = watch.ElapsedMilliseconds + 700;
            while (GetForegroundWindow() != hwnd && watch.ElapsedMilliseconds < until) await SettleAsync(40);
            var actual = GetForegroundWindow() == hwnd;
            evidence.Add(new { step = step + "-activation", requestedOwnWindow = true, nativeCallAccepted = nativeAccepted, actualForegroundConfirmed = actual });
            if (!actual) throw new ForegroundNotVerifiedException("Windows did not grant the owned fixture foreground at " + step + "; automatic foreground behavior not verified.");
            Require(GetForegroundWindow() != capsuleHwnd, "capsule did not steal foreground at " + step);
        }

        void AssertForeground(IntPtr expected, string step)
        {
            var actual = GetForegroundWindow();
            if (actual == capsuleHwnd && capsuleHwnd != IntPtr.Zero)
                throw new InvalidOperationException("Capsule stole foreground at " + step);
            if (actual != expected)
                throw new ForegroundNotVerifiedException("Foreground changed outside the owned fixture at " + step + "; remaining steps not verified.");
        }

        void Sample(string step, IntPtr expected)
        {
            var bounds = Bounds(capsule);
            if (lastMotion is { } previous && previous.Left == bounds.Left && previous.Top == bounds.Top
                && previous.Right == bounds.Right && previous.Bottom == bounds.Bottom) return;
            lastMotion = bounds;
            if (motion.Count < 160)
                motion.Add(new { step, elapsedMilliseconds = watch.ElapsedMilliseconds, bounds,
                    actualForegroundConfirmed = GetForegroundWindow() == expected });
        }

        async Task RemainOrdinaryAsync(IntPtr expected, string step, int milliseconds)
        {
            var until = watch.ElapsedMilliseconds + milliseconds;
            do
            {
                AssertForeground(expected, step); Sample(step, expected);
                Require(capsule.DockSideForCheck == CapsuleDockSide.None && !capsule.EdgeHiddenForCheck && !capsule.SuppressedForCheck
                    && capsule.EffectiveOpacityForCheck >= .99, "ordinary foreground remains visible and unhidden: " + step);
                await SettleAsync(60);
            } while (watch.ElapsedMilliseconds < until);
        }

        async Task WaitAsync(IntPtr expected, string step, Func<bool> condition, int timeout)
        {
            var until = watch.ElapsedMilliseconds + timeout;
            do
            {
                AssertForeground(expected, step); Sample(step, expected);
                if (condition()) { Require(true, "real product foreground transition settles: " + step); return; }
                if (watch.ElapsedMilliseconds >= 29000)
                    throw new ForegroundNotVerifiedException("Foreground fixture reached its time bound; remaining steps not verified.");
                await SettleAsync(35);
            } while (watch.ElapsedMilliseconds < until);
            throw new InvalidOperationException("Product foreground transition did not settle: " + step);
        }
    }

    static Window FixtureWindow(string title, bool fullscreen)
        => new()
        {
            Title = title, ShowInTaskbar = false, ShowActivated = false, Topmost = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            WindowStyle = fullscreen ? WindowStyle.None : WindowStyle.SingleBorderWindow,
            ResizeMode = fullscreen ? ResizeMode.NoResize : ResizeMode.CanResize,
            Opacity = .12, Background = Brushes.Black,
            Content = new TextBlock { Text = "仅临时前台验收，不访问账号、网络或执行系统动作。", Foreground = Brushes.White, Margin = new Thickness(20) }
        };
    static async Task SettleAsync(int milliseconds)
    {
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(milliseconds);
    }
    static void Pointer(CapsuleWindow capsule, bool inside)
    {
        var strip = capsule.VisibleEdgeStripForCheck; var shape = capsule.VisualBoundsForCheck;
        var point = capsule.EdgeHiddenForCheck && !strip.IsEmpty
            ? new Point(strip.Left + strip.Width / 2, strip.Top + strip.Height / 2)
            : capsule.PointToScreen(new Point(shape.Left + shape.Width / 2, shape.Top + shape.Height / 2));
        capsule.HandlePointerAtForCheck(inside, point.X, point.Y);
    }
    static void Require(bool value, string name)
    {
        if (!value) throw new InvalidOperationException("Capsule foreground: " + name);
        assertions++;
    }
    static NativeRect Bounds(Window window)
    {
        if (!GetWindowRect(new WindowInteropHelper(window).Handle, out var result))
            throw new InvalidOperationException("Owned fixture native bounds are unavailable.");
        return result;
    }
    static int Pixel(double value) => (int)Math.Clamp(Math.Round(value), int.MinValue, int.MaxValue);
    sealed class ForegroundNotVerifiedException(string message) : Exception(message) { }
    [StructLayout(LayoutKind.Sequential)] struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        // Properties ensure useful bounds are included in System.Text.Json without globally serializing fields.
        public int X => Left;
        public int Y => Top;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rectangle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
}

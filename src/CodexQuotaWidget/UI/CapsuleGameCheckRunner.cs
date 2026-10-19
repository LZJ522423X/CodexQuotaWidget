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
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CodexQuotaWidget.Core;
using CodexQuotaWidget.Core.Automation;
using CodexQuotaWidget.Core.ControlCenter;

namespace CodexQuotaWidget;

/// <summary>Own nonactivating windows only. Game/scene and pointer input are synthetic; never launches a model or touches a user window.</summary>
internal static class CapsuleGameCheckRunner
{
    const string DirectoryPath = @"D:\CodexQuotaWidget-GitHub\artifacts\capsule-game-preview";
    static int assertions;

    public static async Task<int> RunAsync()
    {
        assertions = 0;
        var isolatedData = Environment.GetEnvironmentVariable("CODEX_QUOTA_WIDGET_DATA");
        if (!AutomationStore.IsSafeDPath(DirectoryPath) || !IsIsolatedArtifactData(isolatedData) || !IsIsolatedArtifactData(Paths.Data)
            || !string.Equals(Path.GetFullPath(isolatedData!).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(Paths.Data).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Game UI checks require D-drive artifacts and isolated data.");
        Directory.CreateDirectory(DirectoryPath);
        var resultPath = Path.Combine(DirectoryPath, "check-result.json");
        File.WriteAllText(resultPath, "{\"passed\":false,\"status\":\"running\",\"syntheticGame\":true}");
        var nativeEvidence = new List<object>(); var sceneEvidence = new List<object>();
        using var displays = new CapsuleDisplayEnvironment(Application.Current.Dispatcher);
        var display = displays.Monitors.FirstOrDefault() ?? throw new InvalidOperationException("Native fixture monitor unavailable.");
        try
        {
            foreach (var priorInput in new[] { false, true })
            {
                var settings = new Settings
                {
                    FollowCodex = false, EnableAnimations = false, Theme = "Dark", WindowMode = "Capsule", CapsuleStaySeconds = .5,
                    CapsuleMonitorDevice = display.DeviceName, CapsuleCenterX = display.WorkArea.Left + display.WorkArea.Width / 2,
                    CapsuleTopY = display.WorkArea.Top + 10 * display.ScaleY
                };
                ((App)Application.Current).ConfigurePreviewTheme(settings);
                var monitor = new QuotaMonitor(settings, () => false, () => false);
                var openCalls = 0; var refreshCalls = 0; var saveCalls = 0;
                var fixturePath = Path.Combine(DirectoryPath, "fixture-" + Guid.NewGuid().ToString("N"), "settings.json");
                var capsule = new CapsuleWindow(monitor, () => openCalls++, () => refreshCalls++, savePosition: () => { saveCalls++; AtomicStore.Write(fixturePath, settings); }, pointerProvider: () => null)
                { ShowActivated = false, Topmost = false, IsHitTestVisible = priorInput };
                try
                {
                    // Supply suppression before first Show so this fixture never flashes above a user game.
                    capsule.SetPresentationForCheck(CapsulePresentationMode.Suppressed);
                    capsule.ShowPanel(); await SettleAsync();
                    CheckSuppressed(capsule, "initial hidden fixture");
                    Check(monitor.AttemptCount == 0 && monitor.ServerPid is null, "fixture quota monitor was never started");

                    capsule.SetPresentationForCheck(CapsulePresentationMode.Normal); await SettleAsync();
                    Check(!capsule.SuppressedForCheck && capsule.EffectiveOpacityForCheck > .99 && capsule.IsHitTestVisible == priorInput,
                        "initial suppression restores the exact prior input setting");
                    var normalBounds = Bounds(capsule); var normalSettings = PositionSettings(settings);
                    var originalTransparentBit = TransparentStyle(capsule);
                    capsule.SetPresentationForCheck(CapsulePresentationMode.Edge); await SettleAsync();
                    Check(capsule.DockSideForCheck == CapsuleDockSide.Top && capsule.EdgeHiddenForCheck && !capsule.SuppressedForCheck && capsule.IsSettledForAnimationCheck(false), "top-docked position leaves a strip independently of the synthetic foreground mode");
                    var edgeBounds = Bounds(capsule); var edgeChrome = capsule.VisualBoundsForCheck;
                    Pointer(capsule, true); await SettleAsync();
                    Check(!capsule.EdgeHiddenForCheck && capsule.IsSettledForAnimationCheck(true), "synthetic Edge hover can reveal and expand before suppression");

                    capsule.SetPresentationForCheck(CapsulePresentationMode.Suppressed); await SettleAsync();
                    CheckSuppressed(capsule, "synthetic game entry");
                    Check(TransparentStyle(capsule), "suppressed layered HWND has native transparent-input style");
                    var shape = capsule.VisualBoundsForCheck;
                    var dpi = VisualTreeHelper.GetDpi(capsule);
                    var hiddenBounds = Bounds(capsule);
                    Check(!OwnsPoint(capsule, normalBounds.Left + (shape.Left + shape.Width / 2) * dpi.DpiScaleX,
                        normalBounds.Top + (shape.Top + shape.Height / 2) * dpi.DpiScaleY), "real WindowFromPoint no longer hits the usual visible capsule area");
                    Check(!OwnsPoint(capsule, hiddenBounds.Left + (shape.Left + shape.Width / 2) * dpi.DpiScaleX,
                        hiddenBounds.Top + (shape.Top + shape.Height / 2) * dpi.DpiScaleY), "real WindowFromPoint does not hit any current suppressed chrome area");
                    Check(!OwnsPoint(capsule, edgeBounds.Left + (edgeChrome.Left + edgeChrome.Width / 2) * dpi.DpiScaleX,
                        display.Bounds.Top + dpi.DpiScaleY), "real WindowFromPoint cannot hit the old top-edge strip during game suppression");

                    Pointer(capsule, true); Pointer(capsule, false);
                    foreach (var button in Descendants<Button>(capsule)) button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
                    capsule.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
                    var source = PresentationSource.FromVisual(capsule) ?? throw new InvalidOperationException("Fixture presentation source unavailable.");
                    capsule.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                    await SettleAsync();
                    Check(openCalls == 0 && refreshCalls == 0, "synthetic hover, button clicks and container click cannot dispatch callbacks while suppressed");
                    Check(!capsule.CollapsePending && Same(shape, capsule.VisualBoundsForCheck), "suppressed pointer input cannot expand or start dwell");
                    CheckSuppressed(capsule, "after synthetic pointer and Escape events");
                    for (var repeat = 0; repeat < 3; repeat++)
                    {
                        capsule.SetPresentationForCheck(CapsulePresentationMode.Suppressed); capsule.RefreshView(); await SettleAsync();
                        CheckSuppressed(capsule, "idempotent suppression/refresh " + repeat);
                        Check(Same(shape, capsule.VisualBoundsForCheck), "repeated game presentation never retargets capsule geometry");
                    }
                    sceneEvidence.Add(new { priorInput, forcedScene = "Suppressed", syntheticGame = true, visible = capsule.IsVisible,
                        effectiveOpacity = capsule.EffectiveOpacityForCheck, input = capsule.IsHitTestVisible,
                        displayObserverRunning = capsule.DisplayObserverRunningForCheck, oldEdgeStripHit = false, callbackCount = openCalls + refreshCalls });

                    capsule.SetPresentationForCheck(CapsulePresentationMode.Edge); await SettleAsync();
                    Check(!capsule.SuppressedForCheck && capsule.EdgeHiddenForCheck && capsule.EffectiveOpacityForCheck > .99,
                        "synthetic game end restores Edge strip instead of remaining transparent");
                    Check(capsule.IsHitTestVisible == priorInput && TransparentStyle(capsule) == originalTransparentBit, "restoration preserves original false/true input and original native transparent style");
                    Check(capsule.DisplayObserverRunningForCheck, "display observation continues after Edge restoration");
                    Pointer(capsule, true); await SettleAsync();
                    Check(capsule.EdgeHiddenForCheck && !capsule.ExpandedForCheck,
                        "game restoration does not treat the same stationary strip cursor as a fresh visit");
                    capsule.HandlePointerAtForCheck(false, display.WorkArea.Left + display.WorkArea.Width / 2,
                        display.WorkArea.Top + display.WorkArea.Height / 2);
                    Pointer(capsule, true); await SettleAsync();
                    Check(!capsule.EdgeHiddenForCheck && capsule.IsSettledForAnimationCheck(true), "restored Edge accepts explicitly synthetic hover again");
                    Pointer(capsule, false); await Task.Delay(575); await SettleAsync();
                    Check(capsule.EdgeHiddenForCheck && capsule.IsSettledForAnimationCheck(false), "restored Edge returns to strip after synthetic leave dwell");
                    capsule.SetPresentationForCheck(CapsulePresentationMode.Normal); await SettleAsync();
                    Check(!capsule.SuppressedForCheck && capsule.DockSideForCheck == CapsuleDockSide.Top && capsule.EdgeHiddenForCheck && capsule.EffectiveOpacityForCheck > .99, "Normal foreground preserves the top-position strip instead of forcing ordinary presentation");
                    var restoredBounds = Bounds(capsule);
                    Check(Math.Abs(normalBounds.Left - restoredBounds.Left) <= 2 && Math.Abs(normalBounds.Top - restoredBounds.Top) <= 2, "game and edge scenes restore the normal physical coordinates");
                    Check(PositionSettings(settings) == normalSettings && saveCalls == 0, "game and edge presentation never persist hidden/negative coordinates");
                    capsule.SetPositionForCheck(display.WorkArea.Left + display.WorkArea.Width / 2, display.WorkArea.Top + display.WorkArea.Height / 2);
                    await SettleAsync();
                    Check(capsule.DockSideForCheck == CapsuleDockSide.None && !capsule.EdgeHiddenForCheck, "explicitly moving to the center restores the ordinary position without a foreground override");
                    if (!priorInput)
                    {
                        capsule.SetPresentationForCheck(CapsulePresentationMode.Suppressed); await SettleAsync();
                        nativeEvidence.AddRange(await CheckNativePresentationAsync(displays, display, new WindowInteropHelper(capsule).Handle));
                    }
                    capsule.HideImmediately(); await SettleAsync();
                    Check(!capsule.IsVisible && !capsule.DisplayObserverRunningForCheck, "manual hide stops native observation and remains hidden");
                    capsule.SetPresentationForCheck(CapsulePresentationMode.Edge);
                    capsule.SetPresentationForCheck(CapsulePresentationMode.Normal);
                    await Task.Delay(2100); await SettleAsync();
                    Check(!capsule.IsVisible && !capsule.DisplayObserverRunningForCheck, "synthetic scene changes after manual hide never auto-show or restart observation");
                    Check(openCalls == 0 && refreshCalls == 0 && monitor.AttemptCount == 0 && monitor.ServerPid is null, "fixture performs no model or quota requests");
                }
                finally { capsule.HideImmediately(); capsule.StopTimers(); capsule.Close(); await monitor.DisposeAsync(); }
            }
            File.WriteAllText(resultPath, JsonSerializer.Serialize(new
            {
                passed = true, assertions, nativeEvidence, sceneEvidence,
                realChecks = new[] { "actual owned WPF opacity/input/visibility", "native extended style", "WindowFromPoint pass-through", "observer start/stop state", "native desktop/maximized/non-browser-full HWND policy classification" },
                syntheticChecks = new[] { "explicit Normal/Edge/Suppressed mode input", "pointer hover/leave", "button/container click and Escape routed events", "game repeated-entry and exit" },
                notVerified = new[] { "real game process identity", "actual Unity/Unreal foreground switch", "user Alt-Tab recovery", "exclusive fullscreen capture behavior" },
                realModelCalls = false, userWindowActivation = false, pointerMoved = false, networkActions = false, processClosure = false, powerActions = false
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"PASS capsule-game-check: {assertions} assertions; real owned native/WPF checks, synthetic game/mode/input; no user windows/models/network/power actions");
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(resultPath, JsonSerializer.Serialize(new { passed = false, assertions, error = error.GetType().Name, syntheticGame = true }));
            throw;
        }
    }

    static bool IsIsolatedArtifactData(string? path)
    {
        if (!AutomationStore.IsSafeDPath(path)) return false;
        const string artifactRoot = @"D:\CodexQuotaWidget-GitHub\artifacts\";
        var full = Path.GetFullPath(path!).TrimEnd(Path.DirectorySeparatorChar);
        return full.Length > artifactRoot.Length && full.StartsWith(artifactRoot, StringComparison.OrdinalIgnoreCase);
    }

    static async Task<IReadOnlyList<object>> CheckNativePresentationAsync(CapsuleDisplayEnvironment displays, CapsuleMonitorInfo display, IntPtr own)
    {
        var evidence = new List<object>(); var desktop = GetDesktopWindow();
        Check(desktop != IntPtr.Zero && displays.GetWindowPresentation(desktop, own, display) == CapsulePresentationMode.Edge,
            "real desktop HWND classifies as Edge without becoming foreground");
        Check(displays.GetWindowPresentation(IntPtr.Zero, own, display) is null, "missing native HWND remains unknown rather than a fabricated presentation");
        evidence.Add(new { kind = "desktop", mode = "Edge", actualForegroundConfirmed = desktop == GetForegroundWindow() });
        var ordinary = new Window { Title = "游戏隐藏原生分类fixture", ShowActivated = false, ShowInTaskbar = false, Topmost = false, Opacity = .01,
            Width = 320, Height = 240, Left = (display.WorkArea.Left + 100) / display.ScaleX, Top = (display.WorkArea.Top + 100) / display.ScaleY };
        var full = new Window { Title = "非浏览器全屏原生分类fixture", ShowActivated = false, ShowInTaskbar = false, Topmost = false, Opacity = .01,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, AllowsTransparency = true, Background = Brushes.Transparent,
            Width = display.Bounds.Width / display.ScaleX, Height = display.Bounds.Height / display.ScaleY, Left = display.Bounds.Left / display.ScaleX, Top = display.Bounds.Top / display.ScaleY };
        try
        {
            ordinary.Show(); ordinary.WindowState = WindowState.Maximized; await SettleAsync();
            var ordinaryHwnd = new WindowInteropHelper(ordinary).Handle;
            Check(displays.GetWindowPresentation(ordinaryHwnd, own, display) == CapsulePresentationMode.Edge, "owned decorated maximized HWND uses Edge presentation without activation");
            evidence.Add(new { kind = "owned-maximized", mode = "Edge", actualForegroundConfirmed = ordinaryHwnd == GetForegroundWindow() });
            ordinary.Hide(); full.Show(); await SettleAsync();
            var fullHwnd = new WindowInteropHelper(full).Handle;
            Check(SetWindowPos(fullHwnd, IntPtr.Zero, (int)Math.Round(display.Bounds.Left), (int)Math.Round(display.Bounds.Top),
                (int)Math.Round(display.Bounds.Width), (int)Math.Round(display.Bounds.Height), 0x0004 | 0x0010), "only owned non-browser fixture receives native monitor bounds without activation");
            await SettleAsync();
            Check(displays.GetWindowPresentation(fullHwnd, own, display) == CapsulePresentationMode.Suppressed, "owned non-browser borderless monitor-sized HWND classifies as Suppressed");
            evidence.Add(new { kind = "owned-nonbrowser-full", mode = "Suppressed", actualForegroundConfirmed = fullHwnd == GetForegroundWindow(), actualGame = false });
            return evidence;
        }
        finally { ordinary.Close(); full.Close(); }
    }

    static void CheckSuppressed(CapsuleWindow capsule, string context)
    {
        Check(capsule.IsVisible && capsule.SuppressedForCheck && capsule.EffectiveOpacityForCheck <= .001, context + " keeps native visibility but renders fully invisible");
        Check(!capsule.IsHitTestVisible && capsule.DisplayObserverRunningForCheck, context + " blocks input while retaining display observation");
    }
    static string PositionSettings(Settings settings) => JsonSerializer.Serialize(new { settings.CapsuleCenterX, settings.CapsuleTopY, settings.CapsuleMonitorDevice });
    static void Pointer(CapsuleWindow capsule, bool inside)
    {
        var strip = capsule.VisibleEdgeStripForCheck;
        var shape = capsule.VisualBoundsForCheck;
        var point = capsule.EdgeHiddenForCheck && !strip.IsEmpty
            ? new Point(strip.Left + strip.Width / 2, strip.Top + strip.Height / 2)
            : capsule.PointToScreen(new Point(shape.Left + shape.Width / 2, shape.Top + shape.Height / 2));
        capsule.HandlePointerAtForCheck(inside, point.X, point.Y);
    }
    static bool Same(Rect a, Rect b) => Math.Abs(a.X - b.X) < .5 && Math.Abs(a.Y - b.Y) < .5 && Math.Abs(a.Width - b.Width) < .5 && Math.Abs(a.Height - b.Height) < .5;
    static Rect Bounds(Window window)
    {
        Check(GetWindowRect(new WindowInteropHelper(window).Handle, out var value), "owned HWND bounds are readable");
        return new(value.Left, value.Top, value.Right - value.Left, value.Bottom - value.Top);
    }
    static bool OwnsPoint(Window own, double x, double y)
    {
        var hwnd = new WindowInteropHelper(own).Handle; var hit = WindowFromPoint(new NativePoint { X = (int)Math.Round(x), Y = (int)Math.Round(y) });
        return hit == hwnd || hit != IntPtr.Zero && GetAncestor(hit, 2) == hwnd;
    }
    static bool TransparentStyle(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var style = IntPtr.Size == 8 ? GetWindowLongPtr(hwnd, -20).ToInt64() : GetWindowLong(hwnd, -20);
        return (style & 0x20) != 0;
    }
    static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T item) yield return item;
        if (root is not Visual && root is not System.Windows.Media.Media3D.Visual3D) yield break;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
    static async Task SettleAsync() { await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); await Task.Delay(25); }
    static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException("Capsule game fixture: " + description);
        assertions++; Console.WriteLine("  PASS " + description);
    }
    [StructLayout(LayoutKind.Sequential)] struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
}

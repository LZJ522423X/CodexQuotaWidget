using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using CodexQuotaWidget.Core;
using CodexQuotaWidget.Core.Automation;
using CodexQuotaWidget.Core.ControlCenter;

namespace CodexQuotaWidget;

/// <summary>Owned nonactivating HWND only. Position, pointer, drag and game inputs are explicitly synthetic.</summary>
internal static class CapsuleDockCheckRunner
{
    const string PreviewDirectory = @"D:\CodexQuotaWidget-GitHub\artifacts\capsule-dock-preview";
    static int assertions;

    public static async Task<int> RunAsync()
    {
        assertions = 0;
        RequireIsolatedData();
        Directory.CreateDirectory(PreviewDirectory);
        var reportPath = Path.Combine(PreviewDirectory, "check-result.json");
        File.WriteAllText(reportPath, "{\"passed\":false,\"status\":\"running\",\"syntheticInput\":true}");
        using var displays = new CapsuleDisplayEnvironment(Application.Current.Dispatcher);
        var display = displays.Monitors.FirstOrDefault() ?? throw new InvalidOperationException("Dock fixture monitor unavailable.");
        var settings = new Settings
        {
            FollowCodex = false, EnableAnimations = false, Theme = "Dark", WindowMode = "Capsule", CapsuleStaySeconds = .5,
            CapsuleMonitorDevice = display.DeviceName, CapsuleCenterX = display.WorkArea.Left + display.WorkArea.Width / 2,
            CapsuleTopY = display.WorkArea.Top + display.WorkArea.Height / 2
        };
        ((App)Application.Current).ConfigurePreviewTheme(settings);
        var monitor = new QuotaMonitor(settings, () => false, () => false);
        var openCalls = 0; var refreshCalls = 0; var saveCalls = 0;
        var settingsPath = Path.Combine(PreviewDirectory, "fixture-" + Guid.NewGuid().ToString("N"), "settings.json");
        var capsule = new CapsuleWindow(monitor, () => openCalls++, () => refreshCalls++,
            savePosition: () => { saveCalls++; AtomicStore.Write(settingsPath, settings); }, pointerProvider: () => null)
        { ShowActivated = false, Topmost = false };
        var nativeEvidence = new List<object>(); var syntheticEvidence = new List<object>();
        var unverified = new List<string> { "physical user hover and drag", "actual QQ comparison", "actual game/Alt-Tab transition", "subjective animation smoothness" };
        try
        {
            capsule.SetPresentationForCheck(CapsulePresentationMode.Suppressed);
            capsule.ShowPanel(); await SettleAsync();
            capsule.SetPresentationForCheck(CapsulePresentationMode.Normal); await SettleAsync();
            var centerX = display.WorkArea.Left + display.WorkArea.Width / 2;
            var middleTop = display.WorkArea.Top + display.WorkArea.Height / 2;
            foreach (var side in new[] { CapsuleDockSide.Top, CapsuleDockSide.Left, CapsuleDockSide.Right })
            {
                capsule.SetPositionForCheck(side == CapsuleDockSide.Left ? display.WorkArea.Left
                    : side == CapsuleDockSide.Right ? display.WorkArea.Right - 1 : centerX,
                    side == CapsuleDockSide.Top ? display.WorkArea.Top + 20 * display.ScaleY : middleTop);
                await SettleAsync();
                Check(capsule.DockSideForCheck == side && capsule.EdgeHiddenForCheck && capsule.IsSettledForAnimationCheck(false), side + " position selects its own collapsed dock");
                var strip = capsule.VisibleEdgeStripForCheck;
                Check(StripMatches(strip, side, display), side + " native strip touches only its selected screen edge and is 3 DIPs thick");
                var saved = Position(settings); var saves = saveCalls; var hidden = Bounds(capsule);
                var stripPoint = Center(strip);
                var outside = OutsideStrip(strip, side, display);
                var corner = side == CapsuleDockSide.Top
                    ? new Point(strip.Left + .2 * display.ScaleX, strip.Top + strip.Height / 2)
                    : new Point(strip.Left + strip.Width / 2, strip.Top + .2 * display.ScaleY);
                Check(!capsule.ContextAtForCheck(stripPoint.X, stripPoint.Y) && !capsule.ClickAtForCheck(corner.X, corner.Y), side + " hidden right-click and rounded alpha-zero strip corner remain blocked");
                foreach (var mode in new[] { CapsulePresentationMode.Normal, CapsulePresentationMode.Edge })
                {
                    capsule.SetPresentationForCheck(mode);
                    capsule.HandlePointerAtForCheck(true, outside.X, outside.Y);
                }
                await SettleAsync();
                Check(capsule.EdgeHiddenForCheck && capsule.IsSettledForAnimationCheck(false) && !capsule.CollapsePending && Near(Bounds(capsule), hidden), side + " non-strip synthetic hover and foreground hints cannot reveal or reposition the capsule");
                Check(!capsule.ClickAtForCheck(outside.X, outside.Y) && !capsule.ContextAtForCheck(outside.X, outside.Y), side + " non-strip synthetic click and context are rejected");
                Check(!OwnsPoint(capsule, outside), side + " actual WindowFromPoint passes through the invisible area next to the strip");
                var nativeStripOwned = OwnsPoint(capsule, stripPoint);
                nativeEvidence.Add(new { side = side.ToString(), strip, nativeBounds = hidden, stripOwned = nativeStripOwned,
                    stripOwnershipStatus = nativeStripOwned ? "Verified" : "NotVerifiedPossibleOcclusion", outsideOwned = false,
                    pointerMoved = false, foregroundActivated = false });
                if (!nativeStripOwned) unverified.Add(side + " native strip ownership: nonactivating/non-topmost fixture may be occluded");
                if (side == CapsuleDockSide.Right)
                    Check(capsule.ClickAtForCheck(stripPoint.X, stripPoint.Y), "valid Right strip synthetic click is accepted only as a reveal");
                else capsule.HandlePointerAtForCheck(true, stripPoint.X, stripPoint.Y);
                await SettleAsync();
                Check(!capsule.EdgeHiddenForCheck && capsule.IsSettledForAnimationCheck(true) && openCalls == 0, side + " genuine strip-coordinate synthetic input reveals without opening the full panel");
                capsule.HandlePointerAtForCheck(false, outside.X, outside.Y); await Task.Delay(650); await SettleAsync();
                Check(capsule.EdgeHiddenForCheck && Near(Bounds(capsule), hidden) && Position(settings) == saved && saveCalls == saves, side + " dwell restores the same strip without saving hidden coordinates");
                syntheticEvidence.Add(new { side = side.ToString(), forcedForegroundModes = new[] { "Normal", "Edge" },
                    stripPointer = stripPoint, outsidePointer = outside, realPointerMovement = false });
            }

            foreach (var position in new[] { "Center", "Bottom" })
            {
                capsule.SetPositionForCheck(centerX, position == "Center" ? middleTop : display.WorkArea.Bottom - 1); await SettleAsync();
                var baseline = Bounds(capsule); var saved = Position(settings);
                Check(capsule.DockSideForCheck == CapsuleDockSide.None && !capsule.EdgeHiddenForCheck && capsule.VisibleEdgeStripForCheck.IsEmpty, position + " position has no docking, including no bottom dock");
                var screenTop = new Point(centerX, display.Bounds.Top + display.ScaleY);
                foreach (var mode in new[] { CapsulePresentationMode.Edge, CapsulePresentationMode.Normal })
                {
                    capsule.SetPresentationForCheck(mode);
                    capsule.HandlePointerAtForCheck(true, screenTop.X, screenTop.Y);
                }
                Check(!capsule.ClickAtForCheck(screenTop.X, screenTop.Y) && !capsule.ContextAtForCheck(screenTop.X, screenTop.Y), position + " unrelated screen-edge pointer/click/context cannot act on the capsule");
                await SettleAsync();
                Check(capsule.DockSideForCheck == CapsuleDockSide.None && capsule.IsSettledForAnimationCheck(false)
                    && Near(Bounds(capsule), baseline) && Position(settings) == saved, position + " foreground changes and unrelated edge input preserve the actual native position");
            }

            // A saved top position at 20 DIPs reveals at 10 DIPs: the drag anchor must
            // come from the displayed native window, rather than the old saved top.
            capsule.SetPositionForCheck(centerX, display.WorkArea.Top + 20 * display.ScaleY); await SettleAsync();
            var topStrip = Center(capsule.VisibleEdgeStripForCheck);
            capsule.HandlePointerAtForCheck(true, topStrip.X, topStrip.Y); await SettleAsync();
            var beforeDrag = Bounds(capsule);
            capsule.BeginDragForCheck();
            Check(Near(Bounds(capsule), beforeDrag), "begin drag freezes the displayed native position without a zero-delta jump");
            var dx = 37 * display.ScaleX; var dy = 67 * display.ScaleY;
            capsule.UpdateDragForCheck(dx, dy); await SettleAsync();
            var moved = Bounds(capsule);
            Check(Math.Abs(moved.Left - beforeDrag.Left - dx) <= 2 && Math.Abs(moved.Top - beforeDrag.Top - dy) <= 2, "first synthetic drag increment equals its requested physical delta rather than the stale saved anchor");
            var dragShape = capsule.VisualBoundsForCheck;
            capsule.HandlePointerAtForCheck(false, 0, 0); capsule.HandlePointerAtForCheck(true, topStrip.X, topStrip.Y);
            Check(!capsule.CollapsePending && Near(capsule.VisualBoundsForCheck, dragShape), "synthetic hover/leave during dragging cannot retarget or start dwell");
            capsule.EndDragForCheck(); await SettleAsync();
            var reloaded = AtomicStore.Read<Settings>(settingsPath);
            Check(capsule.DockSideForCheck == CapsuleDockSide.None && Near(Bounds(capsule), moved) && reloaded is not null && Position(reloaded) == Position(settings), "ending drag neither jumps nor loses the saved normal position");

            foreach (var side in new[] { CapsuleDockSide.Left, CapsuleDockSide.Right })
            {
                settings.EnableAnimations = false; ((App)Application.Current).ConfigurePreviewTheme(settings); capsule.RefreshView();
                capsule.SetPositionForCheck(side == CapsuleDockSide.Left ? display.WorkArea.Left + 1 : display.WorkArea.Right - 1, middleTop);
                await SettleAsync();
                var revealPoint = Center(capsule.VisibleEdgeStripForCheck);
                settings.EnableAnimations = true; ((App)Application.Current).ConfigurePreviewTheme(settings); capsule.RefreshView();
                capsule.HandlePointerAtForCheck(true, revealPoint.X, revealPoint.Y);
                await Task.Delay(65);
                var partialBounds = Bounds(capsule);
                var partialAnchor = capsule.PointToScreen(new Point(capsule.ActualWidth / 2, capsule.VisualBoundsForCheck.Top));
                capsule.BeginDragForCheck();
                Check(Near(Bounds(capsule), partialBounds), side + " mid-reveal animation freezes its partial native location when dragging begins");
                var firstX = (side == CapsuleDockSide.Left ? 12 : -12) * display.ScaleX;
                var firstY = 15 * display.ScaleY;
                capsule.UpdateDragForCheck(firstX, firstY); await SettleAsync();
                var partialMoved = Bounds(capsule);
                Check(Math.Abs(partialMoved.Left - partialBounds.Left - firstX) <= 2 && Math.Abs(partialMoved.Top - partialBounds.Top - firstY) <= 2,
                    side + " first mid-reveal drag increment follows the pointer without snapping to the expanded clamp");
                capsule.UpdateDragForCheck(centerX - partialAnchor.X, middleTop - partialAnchor.Y); await SettleAsync();
                var interiorBounds = Bounds(capsule);
                capsule.EndDragForCheck(); await Task.Delay(450); await SettleAsync();
                Check(capsule.DockSideForCheck == CapsuleDockSide.None && Near(Bounds(capsule), interiorBounds), side + " moving the interrupted reveal into the center ends without native position snapping");
                syntheticEvidence.Add(new { side = side.ToString(), operation = "mid-reveal-drag", animationDelayMilliseconds = 65,
                    partialBounds, firstDelta = new Point(firstX, firstY), partialMoved, restoredCenterBounds = interiorBounds, actualPhysicalDrag = false });
            }
            settings.EnableAnimations = false; ((App)Application.Current).ConfigurePreviewTheme(settings); capsule.RefreshView();

            capsule.SetPositionForCheck(display.WorkArea.Left, middleTop); await SettleAsync();
            var leftStrip = capsule.VisibleEdgeStripForCheck; var beforeGame = Bounds(capsule); var beforeGameSettings = Position(settings); var beforeGameSaves = saveCalls;
            capsule.SetPresentationForCheck(CapsulePresentationMode.Suppressed); await SettleAsync();
            Check(capsule.IsVisible && capsule.SuppressedForCheck && capsule.EffectiveOpacityForCheck <= .001 && !capsule.IsHitTestVisible && capsule.DisplayObserverRunningForCheck, "synthetic game completely suppresses the capsule while retaining observation");
            var gamePoint = Center(leftStrip);
            capsule.HandlePointerAtForCheck(true, gamePoint.X, gamePoint.Y);
            Check(!capsule.ClickAtForCheck(gamePoint.X, gamePoint.Y) && !capsule.ContextAtForCheck(gamePoint.X, gamePoint.Y)
                && !OwnsPoint(capsule, gamePoint) && capsule.SuppressedForCheck && !capsule.CollapsePending, "game blocks the former side strip, native input and synthetic hover/click/context recovery");
            capsule.SetPresentationForCheck(CapsulePresentationMode.Normal); await SettleAsync();
            Check(capsule.DockSideForCheck == CapsuleDockSide.Left && capsule.EdgeHiddenForCheck && Near(Bounds(capsule), beforeGame)
                && Position(settings) == beforeGameSettings && saveCalls == beforeGameSaves && capsule.IsHitTestVisible, "game exit restores the saved Left dock and original input without persisting suppression coordinates");
            capsule.HideImmediately();
            capsule.SetPresentationForCheck(CapsulePresentationMode.Edge); await Task.Delay(2100); await SettleAsync();
            Check(!capsule.IsVisible && !capsule.DisplayObserverRunningForCheck, "manual hide remains hidden and stops observation across subsequent foreground hints");
            Check(openCalls == 0 && refreshCalls == 0 && monitor.AttemptCount == 0 && monitor.ServerPid is null, "fixture never opens a panel, starts quota requests or dispatches a model");
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new
            {
                passed = true, assertions, nativeEvidence, syntheticEvidence,
                realChecks = new[] { "owned WPF/native coordinates", "actual three-DIP strips", "WindowFromPoint invisible-area pass-through", "isolated D settings persistence/readback", "observer start/stop flags", "owned native drag displacement" },
                syntheticChecks = new[] { "position placement", "Normal/Edge/Suppressed hints", "physical-coordinate hover/click/context guards", "drag lifecycle and delta", "game exit" },
                notVerified = unverified, userWindowActivation = false, pointerMoved = false, models = false, network = false, processClosure = false, powerActions = false
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"PASS capsule-dock-check: {assertions} assertions; real owned WPF/native IO, synthetic position/input; no user-window/model/network/power actions");
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new { passed = false, assertions, error = error.GetType().Name,
                nativeEvidence, syntheticEvidence, syntheticInput = true }));
            throw;
        }
        finally { capsule.HideImmediately(); capsule.StopTimers(); capsule.Close(); await monitor.DisposeAsync(); }
    }

    static void RequireIsolatedData()
    {
        var configured = Environment.GetEnvironmentVariable("CODEX_QUOTA_WIDGET_DATA");
        const string prefix = @"D:\CodexQuotaWidget-GitHub\artifacts\";
        if (!AutomationStore.IsSafeDPath(PreviewDirectory) || !AutomationStore.IsSafeDPath(configured) || !AutomationStore.IsSafeDPath(Paths.Data))
            throw new InvalidDataException("Dock checks require explicit isolated D artifacts data.");
        var full = Path.GetFullPath(configured!).TrimEnd(Path.DirectorySeparatorChar);
        if (full.Length <= prefix.Length || !full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(full, Path.GetFullPath(Paths.Data).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Dock checks refuse production or mismatched data directories.");
    }
    static bool StripMatches(Rect strip, CapsuleDockSide side, CapsuleMonitorInfo monitor)
        => !strip.IsEmpty && strip.Width > 0 && strip.Height > 0 && (side switch
        {
            CapsuleDockSide.Top => Math.Abs(strip.Top - monitor.Bounds.Top) <= 2 && Math.Abs(strip.Height - 3 * monitor.ScaleY) <= 1,
            CapsuleDockSide.Left => Math.Abs(strip.Left - monitor.Bounds.Left) <= 2 && Math.Abs(strip.Width - 3 * monitor.ScaleX) <= 1,
            CapsuleDockSide.Right => Math.Abs(strip.Right - monitor.Bounds.Right) <= 2 && Math.Abs(strip.Width - 3 * monitor.ScaleX) <= 1,
            _ => false
        });
    static Point OutsideStrip(Rect strip, CapsuleDockSide side, CapsuleMonitorInfo monitor)
        => side switch
        {
            CapsuleDockSide.Top => new(strip.Left + strip.Width / 2, strip.Bottom + 12 * monitor.ScaleY),
            CapsuleDockSide.Left => new(strip.Right + 12 * monitor.ScaleX, strip.Top + strip.Height / 2),
            _ => new(strip.Left - 12 * monitor.ScaleX, strip.Top + strip.Height / 2)
        };
    static Point Center(Rect rectangle)
    {
        if (rectangle.IsEmpty || rectangle.Width <= 0 || rectangle.Height <= 0) throw new InvalidOperationException("Visible fixture strip unavailable.");
        return new(rectangle.Left + rectangle.Width / 2, rectangle.Top + rectangle.Height / 2);
    }
    static string Position(Settings settings) => JsonSerializer.Serialize(new { settings.CapsuleCenterX, settings.CapsuleTopY, settings.CapsuleMonitorDevice });
    static Rect Bounds(Window window)
    {
        if (!GetWindowRect(new WindowInteropHelper(window).Handle, out var value)) throw new InvalidOperationException("Owned native bounds unavailable.");
        return new(value.Left, value.Top, value.Right - value.Left, value.Bottom - value.Top);
    }
    static bool Near(Rect a, Rect b) => Math.Abs(a.Left - b.Left) <= 2 && Math.Abs(a.Top - b.Top) <= 2 && Math.Abs(a.Width - b.Width) <= 2 && Math.Abs(a.Height - b.Height) <= 2;
    static bool OwnsPoint(Window own, Point point)
    {
        var hwnd = new WindowInteropHelper(own).Handle;
        var hit = WindowFromPoint(new NativePoint { X = (int)Math.Round(point.X), Y = (int)Math.Round(point.Y) });
        return hit == hwnd || hit != IntPtr.Zero && GetAncestor(hit, 2) == hwnd;
    }
    static async Task SettleAsync() { await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); await Task.Delay(25); }
    static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException("Capsule dock fixture: " + description);
        assertions++; Console.WriteLine("  PASS " + description);
    }
    [StructLayout(LayoutKind.Sequential)] struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetWindowRect(IntPtr hwnd, out NativeRect value);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
}

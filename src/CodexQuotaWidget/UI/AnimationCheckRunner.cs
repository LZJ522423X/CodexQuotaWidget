using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Threading;
using CodexQuotaWidget.Core;

namespace CodexQuotaWidget;

internal static class AnimationCheckRunner
{
    public static async Task<int> RunAsync()
    {
        var settings = new Settings { FollowCodex = false, EnableAnimations = true, WindowMode = "Capsule" };
        ((App)Application.Current).ConfigurePreviewTheme(settings);
        var monitor = new QuotaMonitor(settings, () => false);
        Point? pointer = null;
        var capsule = new CapsuleWindow(monitor, () => { }, () => { }, pointerProvider: () => pointer);
        void SetPresence(bool inside)
        {
            var bounds = capsule.VisualBoundsForCheck;
            pointer = capsule.PointToScreen(inside
                ? new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2)
                : new Point(-20, -20));
            capsule.HandlePointerPresence(inside);
        }
        capsule.IsHitTestVisible = false; // Pointer dwell is supplied explicitly by this fixture.
        try
        {
            capsule.ShowPanel();
            capsule.SetFullscreenForCheck(false);
            await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(300);
            capsule.SetExpandedForLayout(false);
            // Place only this fixture away from the live top-edge capsule and finish the native
            // collapsed layout before measuring. Do not alter the product's sizing or curves.
            var dpi = VisualTreeHelper.GetDpi(capsule);
            var work = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(capsule).Handle).WorkingArea;
            // Use the same physical anchor as real dragging; direct Left/Top changes leave
            // dock state at the old edge and can make BeginDragForCheck refuse the fixture.
            capsule.SetPositionForCheck(work.Left + work.Width / 2d, work.Top + work.Height / 3d);
            await Application.Current.Dispatcher.InvokeAsync(capsule.UpdateLayout, DispatcherPriority.Render);
            await Task.Delay(120);
            Check(capsule.IsSettledForAnimationCheck(false), "collapsed visual baseline settles before animation observation");
            var fixedWidth = capsule.ActualWidth; var fixedHeight = capsule.ActualHeight;
            Check(Math.Abs(fixedWidth - 536) < 1.1 && Math.Abs(fixedHeight - 182) < 1.1, "native host provides fixed rebound padding");
            Console.WriteLine($"fixture baseline: visual={capsule.VisualBoundsForCheck}; fixedHost={fixedWidth:0.##}x{fixedHeight:0.##}; position={capsule.Left:0.##},{capsule.Top:0.##}; dpi={dpi.DpiScaleX:0.##},{dpi.DpiScaleY:0.##}; work={work}");

            var expand = await ObserveAsync(capsule, () => capsule.SetExpandedForAnimationCheck(true), (int)AnimationDurations.CapsuleExpand.TotalMilliseconds + 120,
                () => Check(capsule.ExpandedOpacityForAnimationCheck > .05, "expanded content begins during visual expansion"));
            Check(capsule.IsSettledForAnimationCheck(true), "expand settles correctly");
            Console.WriteLine($"expand observed: visualPeak={expand.MaxWidth:0.##}x{expand.MaxHeight:0.##}; nativeRange={expand.NativeMinWidth:0.##}..{expand.NativeMaxWidth:0.##} x {expand.NativeMinHeight:0.##}..{expand.NativeMaxHeight:0.##}; visualSettled={capsule.VisualBoundsForCheck}; frames={expand.Frames}");
            Check(expand.MaxWidth > capsule.VisualBoundsForCheck.Width + 2 && expand.MaxHeight > capsule.VisualBoundsForCheck.Height + 2, "expand visual rebound is visible and not clipped");
            CheckNativeStable(expand, fixedWidth, fixedHeight, "expand");

            var collapse = await ObserveAsync(capsule, () => capsule.SetExpandedForAnimationCheck(false), (int)AnimationDurations.CapsuleCollapse.TotalMilliseconds + 120);
            Check(capsule.IsSettledForAnimationCheck(false), "collapse settles correctly");
            Console.WriteLine($"collapse observed: visualMinimum={collapse.MinWidth:0.##}x{collapse.MinHeight:0.##}; nativeRange={collapse.NativeMinWidth:0.##}..{collapse.NativeMaxWidth:0.##} x {collapse.NativeMinHeight:0.##}..{collapse.NativeMaxHeight:0.##}; visualSettled={capsule.VisualBoundsForCheck}; frames={collapse.Frames}");
            if (collapse.MinWidth >= capsule.VisualBoundsForCheck.Width - 2 || collapse.MinHeight >= capsule.VisualBoundsForCheck.Height - 2)
                Console.WriteLine("collapse render samples: " + string.Join("; ", collapse.Geometry));
            Check(collapse.MinWidth < capsule.VisualBoundsForCheck.Width - 2 && collapse.MinHeight < capsule.VisualBoundsForCheck.Height - 2, "collapse visual rebound is visible and not clipped");
            CheckNativeStable(collapse, fixedWidth, fixedHeight, "collapse");

            capsule.SetExpandedForAnimationCheck(true);
            await Task.Delay(160);
            var before = capsule.VisualBoundsForCheck;
            capsule.SetExpandedForAnimationCheck(false);
            CheckSameVisual(capsule.VisualBoundsForCheck, before, "collapse retarget preserves current visual geometry");
            await Task.Delay(80);
            before = capsule.VisualBoundsForCheck;
            capsule.SetExpandedForAnimationCheck(true);
            CheckSameVisual(capsule.VisualBoundsForCheck, before, "expand retarget preserves current visual geometry");
            await Task.Delay(AnimationDurations.CapsuleExpand + TimeSpan.FromMilliseconds(120));
            Check(capsule.IsSettledForAnimationCheck(true), "interrupted transition settles correctly");

            capsule.SetExpandedForLayout(false);
            capsule.SetExpandedForAnimationCheck(true);
            await Task.Delay(160);
            before = capsule.VisualBoundsForCheck;
            var opacityBeforeDrag = capsule.ExpandedOpacityForAnimationCheck;
            capsule.BeginDragForCheck();
            CheckSameVisual(capsule.VisualBoundsForCheck, before, "drag begins without jumping the in-flight visual geometry");
            Check(Math.Abs(capsule.ExpandedOpacityForAnimationCheck - opacityBeforeDrag) < .01, "drag begins without jumping current content opacity");
            pointer = null; // Explicitly exercise a temporarily unavailable cursor sample.
            capsule.HandlePointerPresence(false);
            Check(!capsule.CollapsePending, "synthetic pointer leave during drag cannot start a dwell timer");
            SetPresence(true);
            CheckSameVisual(capsule.VisualBoundsForCheck, before, "synthetic pointer enter during drag cannot retarget geometry");
            await Task.Delay(120);
            CheckSameVisual(capsule.VisualBoundsForCheck, before, "real WPF visual clock remains frozen throughout simulated drag");
            Check(Math.Abs(capsule.ExpandedOpacityForAnimationCheck - opacityBeforeDrag) < .01, "real WPF content clock remains frozen throughout simulated drag");
            capsule.EndDragForCheck();
            CheckSameVisual(capsule.VisualBoundsForCheck, before, "drag end resumes from current visual without a jump");
            await Task.Delay(AnimationDurations.CapsuleExpand + TimeSpan.FromMilliseconds(120));
            Check(capsule.IsSettledForAnimationCheck(true), "interrupted expansion resumes and settles after simulated drag ends");

            settings.CapsuleStaySeconds = .5;
            SetPresence(false);
            Check(capsule.CollapsePending, "pointer leaving starts dwell timer");
            await Task.Delay(200);
            Check(capsule.IsSettledForAnimationCheck(true), "capsule stays expanded during dwell");
            SetPresence(true);
            Check(!capsule.CollapsePending, "pointer returning cancels dwell timer");
            await Task.Delay(600);
            Check(capsule.IsSettledForAnimationCheck(true), "cancelled dwell does not collapse");
            SetPresence(false);
            await Task.Delay(1200);
            Check(capsule.IsSettledForAnimationCheck(false), "dwell expires and collapses");
            SetPresence(true);
            await Task.Delay(720);
            Check(capsule.IsSettledForAnimationCheck(true), "returning reopens collapsed capsule");

            settings.EnableAnimations = false;
            AnimationHelper.Enabled = false;
            capsule.SetExpandedForAnimationCheck(false);
            capsule.UpdateLayout();
            Check(capsule.IsSettledForAnimationCheck(false), "animations-off collapses immediately");
            capsule.SetExpandedForAnimationCheck(true);
            capsule.UpdateLayout();
            Check(capsule.IsSettledForAnimationCheck(true), "animations-off expands immediately");

            Console.WriteLine($"expand: {expand.Frames} distinct render callbacks, p95 gap {expand.P95Milliseconds:0.#} ms; peak {expand.MaxWidth:0.#}x{expand.MaxHeight:0.#}");
            Console.WriteLine($"collapse: {collapse.Frames} distinct render callbacks, p95 gap {collapse.P95Milliseconds:0.#} ms; minimum {collapse.MinWidth:0.#}x{collapse.MinHeight:0.#}");
            Console.WriteLine("PASS animation-check: actual WPF visual rebound, fixed native host, content, bounds, interrupted transitions, synthetic drag lifecycle/frozen clocks and pointer dwell/cancel/reopen, animations-off; subjective smoothness not verified");
            return 0;
        }
        finally
        {
            capsule.HideImmediately();
            capsule.Close();
            await monitor.DisposeAsync();
        }
    }

    private sealed record FrameStats(int Frames, double P95Milliseconds, double MinWidth, double MaxWidth, double MinHeight, double MaxHeight,
        double RequestedMaxWidth, double RequestedMaxHeight, double NativeMaxWidth, double NativeMaxHeight,
        double RequestedMinWidth, double RequestedMinHeight, double NativeMinWidth, double NativeMinHeight, int NativeFrames, IReadOnlyList<string> Geometry);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rectangle);

    private static async Task<FrameStats> ObserveAsync(CapsuleWindow capsule, Action start, int milliseconds, Action? midpoint = null)
    {
        var gaps = new List<double>();
        long previous = 0;
        TimeSpan? previousRender = null;
        double minWidth = capsule.VisualBoundsForCheck.Width, maxWidth = minWidth, minHeight = capsule.VisualBoundsForCheck.Height, maxHeight = minHeight;
        double requestedMaxWidth = capsule.Width, requestedMaxHeight = capsule.Height, nativeMaxWidth = 0, nativeMaxHeight = 0;
        double requestedMinWidth = capsule.Width, requestedMinHeight = capsule.Height, nativeMinWidth = double.PositiveInfinity, nativeMinHeight = double.PositiveInfinity;
        var geometry = new List<string>(); var elapsed = Stopwatch.StartNew(); var nativeFrames = 0; var visualContained = true;
        var hwnd = new WindowInteropHelper(capsule).Handle;
        var dpi = VisualTreeHelper.GetDpi(capsule);
        EventHandler rendering = (_, args) =>
        {
            var time = ((RenderingEventArgs)args).RenderingTime;
            if (previousRender == time) return;
            previousRender = time;
            var now = Stopwatch.GetTimestamp();
            if (previous != 0)
                gaps.Add((now - previous) * 1000d / Stopwatch.Frequency);
            previous = now;
            var visual = capsule.VisualBoundsForCheck;
            minWidth = Math.Min(minWidth, visual.Width);
            maxWidth = Math.Max(maxWidth, visual.Width);
            minHeight = Math.Min(minHeight, visual.Height);
            maxHeight = Math.Max(maxHeight, visual.Height);
            visualContained &= visual.Left >= -.5 && visual.Top >= -.5 && visual.Right <= capsule.ActualWidth + .5
                && visual.Bottom <= capsule.ActualHeight + .5;
            requestedMaxWidth = Math.Max(requestedMaxWidth, capsule.Width);
            requestedMaxHeight = Math.Max(requestedMaxHeight, capsule.Height);
            requestedMinWidth = Math.Min(requestedMinWidth, capsule.Width);
            requestedMinHeight = Math.Min(requestedMinHeight, capsule.Height);
            if (GetWindowRect(hwnd, out var rectangle))
            {
                nativeFrames++;
                var nativeWidth = (rectangle.Right - rectangle.Left) / dpi.DpiScaleX;
                var nativeHeight = (rectangle.Bottom - rectangle.Top) / dpi.DpiScaleY;
                nativeMaxWidth = Math.Max(nativeMaxWidth, nativeWidth); nativeMaxHeight = Math.Max(nativeMaxHeight, nativeHeight);
                nativeMinWidth = Math.Min(nativeMinWidth, nativeWidth); nativeMinHeight = Math.Min(nativeMinHeight, nativeHeight);
                if (geometry.Count < 160)
                    geometry.Add($"{elapsed.Elapsed.TotalMilliseconds:0}ms visual={visual.Width:0.##}x{visual.Height:0.##} host={capsule.ActualWidth:0.##}x{capsule.ActualHeight:0.##} native={nativeWidth:0.##}x{nativeHeight:0.##}");
            }
        };
        CompositionTarget.Rendering += rendering;
        try
        {
            start();
            await Task.Delay(90);
            midpoint?.Invoke();
            await Task.Delay(milliseconds - 90);
        }
        finally
        {
            CompositionTarget.Rendering -= rendering;
        }
        Check(gaps.Count > 1, "render callbacks observed");
        Check(visualContained, "animated visual remains inside transparent native host");
        gaps.Sort();
        return new(gaps.Count + 1, gaps[(int)Math.Ceiling(.95 * (gaps.Count - 1))], minWidth, maxWidth, minHeight, maxHeight,
            requestedMaxWidth, requestedMaxHeight, nativeMaxWidth, nativeMaxHeight,
            requestedMinWidth, requestedMinHeight, nativeMinWidth, nativeMinHeight, nativeFrames, geometry);
    }

    static void CheckSameVisual(Rect actual, Rect expected, string name)
        => Check(Math.Abs(actual.X - expected.X) < 1.1 && Math.Abs(actual.Y - expected.Y) < 1.1
            && Math.Abs(actual.Width - expected.Width) < 1.1 && Math.Abs(actual.Height - expected.Height) < 1.1, name);

    static void CheckNativeStable(FrameStats sample, double width, double height, string name)
    {
        Check(sample.NativeFrames == sample.Frames, name + " measures native bounds on every rendering callback");
        Check(Math.Abs(sample.NativeMaxWidth - sample.NativeMinWidth) < 1.1 && Math.Abs(sample.NativeMaxHeight - sample.NativeMinHeight) < 1.1,
            name + " native host never resizes during visual animation");
        Check(Math.Abs(sample.NativeMaxWidth - width) < 1.1 && Math.Abs(sample.NativeMaxHeight - height) < 1.1,
            name + " native host remains at the settled fixture dimensions");
        Check(Math.Abs(sample.RequestedMaxWidth - sample.RequestedMinWidth) < .1
            && Math.Abs(sample.RequestedMaxHeight - sample.RequestedMinHeight) < .1, name + " WPF requested window dimensions stay fixed");
    }

    private static void Check(bool condition, string name)
    {
        if (!condition)
            throw new InvalidOperationException(name);
    }
}

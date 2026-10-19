using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using CodexQuotaWidget.Core.ControlCenter;
using Microsoft.Win32;

namespace CodexQuotaWidget;

internal sealed record CapsuleMonitorInfo(string DeviceName, Rect Bounds, Rect WorkArea, double ScaleX, double ScaleY)
{
    internal IntPtr Handle { get; init; }
    internal bool IsPrimary { get; init; }
    public CapsulePlacementMonitor ToPlacementMonitor() => new(DeviceName,
        new(Bounds.Left, Bounds.Top, Bounds.Width, Bounds.Height),
        new(WorkArea.Left, WorkArea.Top, WorkArea.Width, WorkArea.Height), ScaleX, ScaleY);
}

internal enum CapsuleWindowCoverageKind { None, Maximized, Fullscreen }
internal enum CapsulePresentationMode { Normal, Edge, Suppressed }

/// <summary>
/// Visible-capsule-only foreground/display observation. All rectangles are physical pixels.
/// Observes native events; never activates, raises, hides, or moves any window itself.
/// </summary>
internal sealed class CapsuleDisplayEnvironment : IDisposable
{
    const uint MonitorDefaultToNearest = 2;
    const uint EventSystemForeground = 0x0003;
    const uint EventObjectLocationChange = 0x800B;
    const uint WinEventSkipOwnProcess = 0x0002;
    const int DwmExtendedFrameBounds = 9;
    const int DwmCloaked = 14;
    const int GwlStyle = -16;
    const long WsCaption = 0x00C00000;
    readonly Dispatcher dispatcher;
    readonly DispatcherTimer fallback;
    readonly WinEventProc callback;
    GCHandle callbackHandle;
    CapsuleMonitorInfo[] monitors = Array.Empty<CapsuleMonitorInfo>();
    IntPtr foregroundHook;
    IntPtr locationHook;
    int refreshQueued;
    volatile bool started;
    volatile bool disposed;

    public event Action? Changed;
    public IReadOnlyList<CapsuleMonitorInfo> Monitors => monitors;
    internal bool IsRunning => started && !disposed;

    public CapsuleDisplayEnvironment(Dispatcher? dispatcher = null)
    {
        this.dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;
        callback = OnWinEvent;
        fallback = new DispatcherTimer(DispatcherPriority.Background, this.dispatcher) { Interval = TimeSpan.FromSeconds(2) };
        fallback.Tick += (_, _) => QueueRefresh();
        Refresh();
    }

    public void Start()
    {
        dispatcher.VerifyAccess();
        if (disposed || started) return;
        started = true;
        callbackHandle = GCHandle.Alloc(callback);
        foregroundHook = SetWinEventHook(EventSystemForeground, EventSystemForeground, IntPtr.Zero, callback, 0, 0, WinEventSkipOwnProcess);
        locationHook = SetWinEventHook(EventObjectLocationChange, EventObjectLocationChange, IntPtr.Zero, callback, 0, 0, WinEventSkipOwnProcess);
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        fallback.Start();
        Refresh();
    }

    public void Stop()
    {
        dispatcher.VerifyAccess();
        if (!started) return;
        started = false;
        fallback.Stop();
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        if (foregroundHook != IntPtr.Zero) { UnhookWinEvent(foregroundHook); foregroundHook = IntPtr.Zero; }
        if (locationHook != IntPtr.Zero) { UnhookWinEvent(locationHook); locationHook = IntPtr.Zero; }
        if (callbackHandle.IsAllocated) callbackHandle.Free();
    }

    public void Refresh()
    {
        dispatcher.VerifyAccess();
        if (disposed) return;
        var current = new List<CapsuleMonitorInfo>();
        MonitorEnumProc enumerate = (IntPtr handle, IntPtr _, ref NativeRect __, IntPtr ___) =>
        {
            var info = new NativeMonitorInfo { Size = (uint)Marshal.SizeOf<NativeMonitorInfo>(), DeviceName = string.Empty };
            if (!GetMonitorInfo(handle, ref info)) return true;
            var bounds = ToRect(info.Bounds);
            var work = ToRect(info.WorkArea);
            if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) return true;
            if (work.IsEmpty || work.Width <= 0 || work.Height <= 0) work = bounds;
            var (sx, sy) = DpiScale(handle);
            current.Add(new(info.DeviceName, bounds, work, sx, sy) { Handle = handle, IsPrimary = (info.Flags & 1) != 0 });
            return true;
        };
        // Preserve the last usable snapshot on a transient enumeration failure, rather than inventing a virtual monitor.
        if (EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, enumerate, IntPtr.Zero) && current.Count > 0)
            monitors = current.OrderByDescending(m => m.IsPrimary).ThenBy(m => m.Bounds.Left).ThenBy(m => m.Bounds.Top).ToArray();
        Changed?.Invoke();
    }

    public CapsuleMonitorInfo? GetMonitorForPoint(double centerX, double topY)
    {
        dispatcher.VerifyAccess();
        if (!double.IsFinite(centerX) || !double.IsFinite(topY)) return monitors.FirstOrDefault();
        var handle = MonitorFromPoint(new NativePoint { X = Pixel(centerX), Y = Pixel(topY) }, MonitorDefaultToNearest);
        return FindMonitor(handle);
    }

    public CapsuleMonitorInfo? GetMonitorForWindow(IntPtr hwnd)
    {
        dispatcher.VerifyAccess();
        // Return target-monitor DPI, not the HWND's current DPI. During WM_DPICHANGED the HWND
        // can already be on a new monitor while its WPF/current-window scale still belongs to
        // the old monitor. The window consumer handles that actual-coordinate scale separately.
        return hwnd == IntPtr.Zero ? monitors.FirstOrDefault() : FindMonitor(MonitorFromWindow(hwnd, MonitorDefaultToNearest));
    }

    public bool IsForegroundFullscreen(IntPtr ownHwnd, CapsuleMonitorInfo monitor)
        => IsWindowFullscreen(GetForegroundWindow(), ownHwnd, monitor);

    public bool IsForegroundCoveringDisplay(IntPtr ownHwnd, CapsuleMonitorInfo monitor)
        => GetWindowCoverage(GetForegroundWindow(), ownHwnd, monitor) != CapsuleWindowCoverageKind.None;

    public CapsulePresentationMode? GetForegroundPresentation(IntPtr ownHwnd, CapsuleMonitorInfo monitor)
        => GetWindowPresentation(GetForegroundWindow(), ownHwnd, monitor);

    /// <summary>Null means transiently unknown: retain the current presentation, particularly game suppression.</summary>
    public CapsulePresentationMode? GetWindowPresentation(IntPtr candidate, IntPtr ownHwnd, CapsuleMonitorInfo monitor)
    {
        dispatcher.VerifyAccess();
        if (disposed || !monitors.Any(m => m.Handle == monitor.Handle && m.DeviceName == monitor.DeviceName && m.Bounds == monitor.Bounds)) return null;
        if (candidate != IntPtr.Zero && (candidate == GetDesktopWindow() || candidate == GetShellWindow())) return CapsulePresentationMode.Edge;
        if (candidate == IntPtr.Zero || !IsWindowVisible(candidate) || IsIconic(candidate)) return null;
        var root = GetAncestor(candidate, 2);
        if (root != IntPtr.Zero) candidate = root;
        if (!IsWindowVisible(candidate) || IsIconic(candidate)) return null;
        if (candidate == ownHwnd || (ownHwnd != IntPtr.Zero && candidate == GetAncestor(ownHwnd, 2))) return null;
        var name = new StringBuilder(256);
        if (GetClassName(candidate, name, name.Capacity) == 0) return null;
        var windowClass = name.ToString();
        if (candidate == GetDesktopWindow() || candidate == GetShellWindow()
            || windowClass is "#32769" or "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            return CapsulePresentationMode.Edge;
        if (DwmGetWindowAttribute(candidate, DwmCloaked, out int cloaked, sizeof(int)) != 0 || cloaked != 0) return null;
        if (!TryGetWindowStyle(candidate, out _) || !GetWindowRect(candidate, out _)) return null;
        // Engine window classes are foreground evidence, not a process-exists guess. They
        // also protect a windowed game and a game active on another monitor.
        if (windowClass is "UnityWndClass" or "UnrealWindow") return CapsulePresentationMode.Suppressed;
        var candidateMonitor = monitors.FirstOrDefault(m => m.Handle == MonitorFromWindow(candidate, MonitorDefaultToNearest));
        if (candidateMonitor is null) return null;
        var candidateCoverage = GetWindowCoverage(candidate, ownHwnd, candidateMonitor);
        // Unknown non-browser fullscreen content is conservatively unobstructed. Only
        // the foreground process's exact executable name is read; no process/file scan.
        if (candidateCoverage == CapsuleWindowCoverageKind.Fullscreen && !IsBrowserWindow(candidate))
            return CapsulePresentationMode.Suppressed;
        var coverage = candidateMonitor.Handle == monitor.Handle ? candidateCoverage : CapsuleWindowCoverageKind.None;
        return coverage == CapsuleWindowCoverageKind.None ? CapsulePresentationMode.Normal : CapsulePresentationMode.Edge;
    }

    static bool IsBrowserWindow(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return false;
        var process = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if (process == IntPtr.Zero) return false;
        try
        {
            var image = new StringBuilder(32768);
            var size = (uint)image.Capacity;
            if (!QueryFullProcessImageName(process, 0, image, ref size)) return false;
            var executable = Path.GetFileName(image.ToString()).ToLowerInvariant();
            return executable is "msedge.exe" or "chrome.exe" or "firefox.exe" or "brave.exe" or "vivaldi.exe" or "opera.exe";
        }
        finally { CloseHandle(process); }
    }

    /// <summary>Classify a supplied HWND without moving focus; useful for native fixture verification.</summary>
    public bool IsWindowFullscreen(IntPtr candidate, IntPtr ownHwnd, CapsuleMonitorInfo monitor)
        => GetWindowCoverage(candidate, ownHwnd, monitor) == CapsuleWindowCoverageKind.Fullscreen;

    /// <summary>Keep native browser maximization distinct from F11 fullscreen; either may merit reducing an overlay.</summary>
    public CapsuleWindowCoverageKind GetWindowCoverage(IntPtr candidate, IntPtr ownHwnd, CapsuleMonitorInfo monitor)
    {
        dispatcher.VerifyAccess();
        if (disposed || !monitors.Any(m => m.Handle == monitor.Handle && m.DeviceName == monitor.DeviceName && m.Bounds == monitor.Bounds)) return CapsuleWindowCoverageKind.None;
        var foreground = candidate;
        if (foreground == IntPtr.Zero || foreground == ownHwnd || !IsWindowVisible(foreground) || IsIconic(foreground)) return CapsuleWindowCoverageKind.None;
        var root = GetAncestor(foreground, 2);
        if (root != IntPtr.Zero) foreground = root;
        if (foreground == ownHwnd || (ownHwnd != IntPtr.Zero && foreground == GetAncestor(ownHwnd, 2))) return CapsuleWindowCoverageKind.None;
        if (foreground == GetDesktopWindow() || foreground == GetShellWindow()) return CapsuleWindowCoverageKind.None;
        var className = new StringBuilder(256);
        if (GetClassName(foreground, className, className.Capacity) == 0) return CapsuleWindowCoverageKind.None;
        if (className.ToString() is "#32769" or "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "DV2ControlHost") return CapsuleWindowCoverageKind.None;
        if (MonitorFromWindow(foreground, MonitorDefaultToNearest) != monitor.Handle) return CapsuleWindowCoverageKind.None;
        if (DwmGetWindowAttribute(foreground, DwmCloaked, out int cloaked, sizeof(int)) != 0 || cloaked != 0) return CapsuleWindowCoverageKind.None;
        if (!TryGetWindowStyle(foreground, out var style) || !GetWindowRect(foreground, out var actual)) return CapsuleWindowCoverageKind.None;
        var visual = actual;
        if (DwmGetWindowAttribute(foreground, DwmExtendedFrameBounds, out NativeRect frame, Marshal.SizeOf<NativeRect>()) == 0
            && frame.Right > frame.Left && frame.Bottom > frame.Top) visual = frame;
        var tolerance = Math.Clamp(2 * Math.Max(monitor.ScaleX, monitor.ScaleY), 2, 8);
        // WS_CAPTION is the combined WS_BORDER | WS_DLGFRAME mask. A border bit alone is
        // not a title bar. Browser F11 normally removes the complete caption and frame.
        var hasCaption = (style & WsCaption) == WsCaption;
        if (!hasCaption && (CapsulePlacement.CoversMonitor(ToPhysicalRect(actual), ToPhysicalRect(monitor.Bounds), tolerance)
            || CapsulePlacement.CoversMonitor(ToPhysicalRect(visual), ToPhysicalRect(monitor.Bounds), tolerance)))
            return CapsuleWindowCoverageKind.Fullscreen;
        // Maximization is a separate native state. A large ordinary window is never enough;
        // its verified visual/raw bounds must also cover this monitor's actual working area.
        if (IsZoomed(foreground) && (CoversWorkingArea(ToPhysicalRect(visual), monitor, tolerance)
            || CoversWorkingArea(ToPhysicalRect(actual), monitor, tolerance)))
            return CapsuleWindowCoverageKind.Maximized;
        return CapsuleWindowCoverageKind.None;
    }

    CapsuleMonitorInfo? FindMonitor(IntPtr handle)
    {
        var match = monitors.FirstOrDefault(m => m.Handle == handle);
        if (match is not null) return match;
        // Do not raise Changed recursively from a Changed consumer. Display changes and the visible
        // two-second fallback refresh the snapshot; a transient unknown handle uses primary safely.
        return monitors.FirstOrDefault();
    }

    void OnWinEvent(IntPtr _, uint kind, IntPtr hwnd, int objectId, int childId, uint __, uint ___)
    {
        if (disposed || !started) return;
        if (kind == EventObjectLocationChange && (objectId != 0 || childId != 0 || hwnd != GetForegroundWindow())) return;
        QueueRefresh();
    }

    void OnDisplaySettingsChanged(object? sender, EventArgs args) => QueueRefresh();

    void QueueRefresh()
    {
        if (disposed || !started || Interlocked.Exchange(ref refreshQueued, 1) != 0) return;
        try
        {
            dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                Interlocked.Exchange(ref refreshQueued, 0);
                if (!disposed && started) Refresh();
            }));
        }
        catch (InvalidOperationException) { Interlocked.Exchange(ref refreshQueued, 0); }
    }

    public void Dispose()
    {
        dispatcher.VerifyAccess();
        if (disposed) return;
        Stop();
        disposed = true;
        Changed = null;
        GC.KeepAlive(callback);
    }

    static (double X, double Y) DpiScale(IntPtr monitor)
    {
        try
        {
            if (GetDpiForMonitor(monitor, 0, out var x, out var y) == 0 && x is >= 48 and <= 768 && y is >= 48 and <= 768)
                return (x / 96d, y / 96d);
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        return (1, 1);
    }

    static int Pixel(double value) => (int)Math.Clamp(Math.Round(value), int.MinValue, int.MaxValue);
    static Rect ToRect(NativeRect rect) => rect.Right > rect.Left && rect.Bottom > rect.Top
        ? new Rect(rect.Left, rect.Top, (double)rect.Right - rect.Left, (double)rect.Bottom - rect.Top) : Rect.Empty;
    static CapsulePhysicalRect ToPhysicalRect(NativeRect rect) => new(rect.Left, rect.Top, (double)rect.Right - rect.Left, (double)rect.Bottom - rect.Top);
    static CapsulePhysicalRect ToPhysicalRect(Rect rect) => new(rect.Left, rect.Top, rect.Width, rect.Height);
    static bool CoversWorkingArea(CapsulePhysicalRect window, CapsuleMonitorInfo monitor, double tolerance)
    {
        var area = ToPhysicalRect(monitor.WorkArea);
        if (!window.IsUsable || !area.IsUsable) return false;
        // Maximized raw HWND rectangles include invisible resize borders outside rcWork.
        // Bound that overhang instead of requiring exact raw coordinates or accepting any huge window.
        var frame = Math.Clamp(16 * Math.Max(monitor.ScaleX, monitor.ScaleY), 16, 128);
        return window.Left <= area.Left + tolerance && window.Top <= area.Top + tolerance
            && window.Right >= area.Right - tolerance && window.Bottom >= area.Bottom - tolerance
            && window.Left >= area.Left - frame && window.Top >= area.Top - frame
            && window.Right <= area.Right + frame && window.Bottom <= area.Bottom + frame;
    }
    static bool TryGetWindowStyle(IntPtr hwnd, out long style)
    {
        Marshal.SetLastPInvokeError(0);
        style = IntPtr.Size == 8 ? GetWindowLongPtr(hwnd, GwlStyle).ToInt64() : GetWindowLong(hwnd, GwlStyle);
        return style != 0 || Marshal.GetLastPInvokeError() == 0;
    }

    [StructLayout(LayoutKind.Sequential)] struct NativePoint { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NativeMonitorInfo
    {
        public uint Size;
        public NativeRect Bounds;
        public NativeRect WorkArea;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
    }
    delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, ref NativeRect rect, IntPtr data);
    delegate void WinEventProc(IntPtr hook, uint kind, IntPtr hwnd, int objectId, int childId, uint eventThread, uint eventTime);

    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")] static extern bool GetMonitorInfo(IntPtr monitor, ref NativeMonitorInfo info);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc callback, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder image, ref uint size);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("user32.dll")] static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")] static extern int GetClassName(IntPtr hwnd, StringBuilder value, int capacity);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)] static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)] static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out NativeRect value, int size);
}

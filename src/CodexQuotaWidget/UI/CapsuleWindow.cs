using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using CodexQuotaWidget.Core;
using CodexQuotaWidget.Core.ControlCenter;
using Forms = System.Windows.Forms;
using Microsoft.Win32;

namespace CodexQuotaWidget;

internal sealed class CapsuleWindow : Window
{
    private const double CollapsedWidth = 400;
    private const double CollapsedHeight = 56;
    private const double ExpandedWidth = 520;
    private const double ExpandedHeight = 166;
    private const double HostWidth = 536;
    private const double HostHeight = 182;
    private const double VisualPadding = 8;
    private const int WmDpiChanged = 0x02E0;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    private readonly QuotaMonitor monitor;
    private readonly ControlCenterService? center;
    private readonly IWorkflowLoopUiController? workflow;
    private readonly Action? savePosition;
    private readonly CapsuleDisplayEnvironment displays = new();
    private readonly RectangleGeometry capsuleShape;
    private readonly Action openFull;
    private readonly Action refresh;
    private readonly Func<Point?> pointerProvider;
    private readonly Border surface;
    private readonly Grid collapsedPanel;
    private readonly Grid expandedPanel;
    private readonly Grid quotaPanel;
    private readonly Grid resetPanel;
    private readonly Grid footerPanel;
    private readonly TextBlock stateText;
    private readonly TextBlock fiveText;
    private readonly TextBlock weekText;
    private readonly TextBlock expandedState;
    private readonly TextBlock expandedFive;
    private readonly TextBlock expandedWeek;
    private readonly TextBlock resetCountdown;
    private readonly TextBlock resetExact;
    private readonly TextBlock creditExpiry;
    private readonly TextBlock updated;
    private readonly ConcentricQuotaRing compactRings;
    private readonly ConcentricQuotaRing expandedRings;
    private readonly Ellipse workflowIndicator;
    private readonly ResetCreditBadge creditBadge;
    private readonly ResetCreditBadge expandedCreditBadge;
    private readonly DispatcherTimer collapseDelay = new() { Interval = AnimationDurations.CapsuleHoverDelay };
    private readonly DispatcherTimer countdownClock = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly DispatcherTimer pointerClock = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private HwndSource? source;
    private bool expanded;
    private bool hideInProgress;
    private bool closedForShutdown;
    private int transitionSerial;
    private int positionSerial;
    private DateTime expandedAtUtc;
    private CapsuleMonitorInfo? currentMonitor;
    private double normalCenterX, normalTopY;
    private bool fullscreen, edgeHidden, dragging;
    private bool edgeHoverConsumed;
    private CapsuleDockSide dockSide;
    private CapsulePresentationMode? presentationOverride;
    private bool foregroundSuppressed;
    private bool inputBeforeSuppression;
    private bool nativeTransparentBeforeSuppression;
    private bool nativeInputSnapshotTaken;
    private double opacityBeforeSuppression;
    private Point? dragStart;
    private Point dragAnchor;
    private string? lastDetail;
    private string? lastQuotaDetail;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X; public int Y; }

    public CapsuleWindow(QuotaMonitor monitor, Action openFull, Action refresh, ControlCenterService? center = null,
        IWorkflowLoopUiController? workflow = null, Action? savePosition = null, Func<Point?>? pointerProvider = null)
    {
        this.monitor = monitor;
        this.center = center;
        this.workflow = workflow;
        this.savePosition = savePosition;
        this.openFull = openFull;
        this.refresh = refresh;
        this.pointerProvider = pointerProvider ?? (() => GetCursorPos(out var cursor) ? new Point(cursor.X, cursor.Y) : null);
        Title = "AI 控制中心 · 额度胶囊";
        // A stable layered host avoids native resize/layout work on every animation frame.
        // Pixels outside the rounded visual remain transparent and pass through to other apps.
        Width = MinWidth = MaxWidth = HostWidth;
        Height = MinHeight = MaxHeight = HostHeight;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        SetResourceReference(ForegroundProperty, "Ink");
        capsuleShape = new RectangleGeometry(VisualRectangle(false), 15, 15);
        surface = new Border();
        var host = new Grid { Width = HostWidth, Height = HostHeight };
        var chrome = new System.Windows.Shapes.Path { Data = capsuleShape, StrokeThickness = 1 };
        chrome.SetResourceReference(Shape.FillProperty, "Glass");
        chrome.SetResourceReference(Shape.StrokeProperty, "Line");
        host.Children.Add(chrome);
        var root = new Grid { Width = HostWidth, Height = HostHeight, Clip = capsuleShape };
        host.Children.Add(root);
        surface.Child = host;
        Content = surface;

        collapsedPanel = new Grid { Width = CollapsedWidth - 30, Height = CollapsedHeight - 18,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, VisualPadding + 9, 0, 0) };
        collapsedPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        collapsedPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        collapsedPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        collapsedPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var statusRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var compactRingHost = new Grid { Width = 28, Height = 28, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        compactRings = new ConcentricQuotaRing { Width = 28, Height = 28, VerticalAlignment = VerticalAlignment.Center };
        compactRingHost.Children.Add(compactRings);
        workflowIndicator = new Ellipse
        {
            Width = 7, Height = 7, StrokeThickness = 1.2,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Visibility = center is null ? Visibility.Collapsed : Visibility.Visible
        };
        workflowIndicator.SetResourceReference(Shape.StrokeProperty, "Glass");
        workflowIndicator.SetResourceReference(Shape.FillProperty, "Muted");
        compactRingHost.Children.Add(workflowIndicator);
        stateText = Label("Codex", 12, true);
        statusRow.Children.Add(compactRingHost);
        statusRow.Children.Add(stateText);
        collapsedPanel.Children.Add(statusRow);
        fiveText = Metric("5h --", 12);
        Grid.SetColumn(fiveText, 1);
        collapsedPanel.Children.Add(fiveText);
        weekText = Metric("周 --", 12);
        Grid.SetColumn(weekText, 2);
        collapsedPanel.Children.Add(weekText);
        creditBadge = new ResetCreditBadge(true) { Margin = new Thickness(9, 0, 0, 0) };
        Grid.SetColumn(creditBadge, 3);
        collapsedPanel.Children.Add(creditBadge);
        root.Children.Add(collapsedPanel);

        expandedPanel = new Grid { Width = ExpandedWidth - 30, Height = ExpandedHeight - 18,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, VisualPadding + 9, 0, 0), Visibility = Visibility.Collapsed, Opacity = 0 };
        expandedPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
        expandedPanel.ColumnDefinitions.Add(new ColumnDefinition());
        expandedRings = new ConcentricQuotaRing { Width = 88, Height = 88, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        expandedPanel.Children.Add(expandedRings);
        var expandedContent = new Grid();
        Grid.SetColumn(expandedContent, 1);
        expandedPanel.Children.Add(expandedContent);
        expandedContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        expandedContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        expandedContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        expandedContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        expandedState = Label("Codex 状态", 12, true);
        header.Children.Add(expandedState);
        var openButton = new Button
        {
            Content = center is null ? "打开完整窗口  ↗" : "打开控制中心  ↗",
            Padding = new Thickness(7, 2, 7, 2),
            MinWidth = 0,
            MinHeight = 25,
            FontSize = 11,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
        };
        AutomationProperties.SetName(openButton, center is null ? "打开完整额度窗口" : "打开 AI 控制中心");
        openButton.Click += (_, _) => { if (UserInteractionAllowed() && !edgeHidden) openFull(); };
        Grid.SetColumn(openButton, 1);
        header.Children.Add(openButton);
        expandedContent.Children.Add(header);

        quotaPanel = new Grid { Margin = new Thickness(0, 1, 0, 2) };
        quotaPanel.ColumnDefinitions.Add(new ColumnDefinition());
        quotaPanel.ColumnDefinitions.Add(new ColumnDefinition());
        expandedFive = Label("5 小时  --", 15, true);
        expandedWeek = Label("每周  --", 15, true);
        Grid.SetColumn(expandedWeek, 1);
        quotaPanel.Children.Add(expandedFive);
        quotaPanel.Children.Add(expandedWeek);
        Grid.SetRow(quotaPanel, 1);
        expandedContent.Children.Add(quotaPanel);

        resetPanel = new Grid { Margin = new Thickness(0, 5, 0, 0) };
        resetPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        resetPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var resetCopy = new StackPanel();
        var resetRow = new Grid();
        resetRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        resetRow.ColumnDefinitions.Add(new ColumnDefinition());
        var resetLabel = Label("5h 重置", 10, false);
        resetLabel.Name = "CapsuleResetLabel";
        resetLabel.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var resetTag = new Border
        {
            Name = "CapsuleResetTag", Child = resetLabel,
            CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 2, 5, 2),
            BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        resetTag.SetResourceReference(Border.BackgroundProperty, "Surface");
        resetTag.SetResourceReference(Border.BorderBrushProperty, "Line");
        resetRow.Children.Add(resetTag);
        resetCountdown = Label("等待额度数据", 12, false);
        resetCountdown.Name = "CapsuleResetCountdown";
        Grid.SetColumn(resetCountdown, 1);
        resetRow.Children.Add(resetCountdown);
        resetExact = Label("精确重置时间 --", 10, false);
        resetExact.Margin = new Thickness(0, 4, 0, 0);
        resetExact.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        resetCopy.Children.Add(resetRow);
        resetCopy.Children.Add(resetExact);
        resetPanel.Children.Add(resetCopy);
        expandedCreditBadge = new ResetCreditBadge() { Margin = new Thickness(8, 0, 0, 0) };
        Grid.SetColumn(expandedCreditBadge, 1);
        resetPanel.Children.Add(expandedCreditBadge);
        Grid.SetRow(resetPanel, 2);
        expandedContent.Children.Add(resetPanel);

        footerPanel = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        footerPanel.ColumnDefinitions.Add(new ColumnDefinition());
        footerPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footerPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        updated = Label("尚无成功读数", 10, false);
        footerPanel.Children.Add(updated);
        creditExpiry = Label("重置机会到期 --", 10, false);
        Grid.SetColumn(creditExpiry, 1);
        footerPanel.Children.Add(creditExpiry);
        var refreshButton = new Button
        {
            Content = "↻",
            Width = 25,
            Height = 25,
            MinWidth = 25,
            MinHeight = 25,
            Padding = new Thickness(0),
            Margin = new Thickness(5, 0, 0, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
        };
        AutomationProperties.SetName(refreshButton, "手动刷新额度");
        refreshButton.Click += (_, _) => { if (!UserInteractionAllowed() || edgeHidden) return; AnimationHelper.RotateOnce(refreshButton); this.refresh(); };
        Grid.SetColumn(refreshButton, 2);
        footerPanel.Children.Add(refreshButton);
        Grid.SetRow(footerPanel, 3);
        expandedContent.Children.Add(footerPanel);
        root.Children.Add(expandedPanel);

        collapseDelay.Tick += (_, _) =>
        {
            collapseDelay.Stop();
            if (!foregroundSuppressed && !PointerOverVisibleSurface() && !dragging) { if (fullscreen) HideAtEdge(); else Collapse(); }
        };
        pointerClock.Tick += (_, _) => ObservePointer();
        countdownClock.Tick += (_, _) =>
        {
            if (expanded && IsVisible)
                UpdateCountdown(QuotaPresentation.From(monitor).FiveHour);
            if (IsVisible) RefreshActivity();
        };
        MouseEnter += (_, _) => HandlePointerPresence(true);
        MouseLeave += (_, _) => HandlePointerPresence(false);
        MouseMove += (_, _) => HandlePointerPresence(true);
        LocationChanged += (_, _) => ClipToDockMonitor();
        ContextMenuOpening += (_, e) =>
        {
            if (!GetCursorPos(out var cursor) || !ContextAtForCheck(cursor.X, cursor.Y)) e.Handled = true;
        };
        MouseLeftButtonUp += OnMouseLeftButtonUp;
        PreviewMouseLeftButtonDown += BeginDrag;
        PreviewMouseMove += UpdateDrag;
        PreviewMouseLeftButtonUp += EndDrag;
        LostMouseCapture += (_, _) => FinishDrag();
        PreviewKeyDown += (_, e) =>
        {
            if (!UserInteractionAllowed()) return;
            if (e.Key != System.Windows.Input.Key.Escape) return;
            if (expanded) Collapse(); else HidePanel();
            e.Handled = true;
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) { countdownClock.Start(); pointerClock.Start(); displays.Start(); }
            else { countdownClock.Stop(); pointerClock.Stop(); collapseDelay.Stop(); displays.Stop(); }
        };
        SourceInitialized += (_, _) =>
        {
            source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            source?.AddHook(WindowProc);
            ApplyMaterial();
            PositionAtActiveDisplay();
            if (foregroundSuppressed) SetNativeSuppressedInput(true);
        };
        Closed += (_, _) =>
        {
            closedForShutdown = true;
            SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;
            collapseDelay.Stop();
            countdownClock.Stop();
            pointerClock.Stop();
            displays.Changed -= DisplayEnvironmentChanged;
            displays.Dispose();
            source?.RemoveHook(WindowProc);
            source = null;
        };
        SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
        displays.Changed += DisplayEnvironmentChanged;
        var menu = new ContextMenu();
        var resetPosition = new MenuItem { Header = "回到屏幕顶部" };
        resetPosition.Click += (_, _) => ResetPosition();
        menu.Items.Add(resetPosition);
        ContextMenu = menu;
        AnimationHelper.AttachButtonHover(surface);
        RefreshView();
    }

    public void ShowPanel()
    {
        if (closedForShutdown) return;
        transitionSerial++;
        AnimationHelper.StopWindowBounds(this);
        expandedPanel.BeginAnimation(OpacityProperty, null);
        collapsedPanel.BeginAnimation(OpacityProperty, null);
        hideInProgress = false;
        collapseDelay.Stop();
        expanded = false;
        collapsedPanel.Visibility = Visibility.Visible;
        collapsedPanel.Opacity = 1;
        expandedPanel.Visibility = Visibility.Collapsed;
        expandedPanel.Opacity = 0;
        StopMorph();
        capsuleShape.Rect = VisualRectangle(false);
        fullscreen = edgeHidden = false;
        dockSide = CapsuleDockSide.None;
        PositionAtActiveDisplay();
        dockSide = GetPositionDockSide();
        fullscreen = edgeHidden = dockSide != CapsuleDockSide.None;
        ApplyNormalPosition();
        RefreshView();
        var beforeShow = presentationOverride ?? (currentMonitor is null ? null
            : displays.GetForegroundPresentation(new WindowInteropHelper(this).Handle, currentMonitor));
        // A missing foreground during startup/switching is not permission to flash over a game.
        if (beforeShow == CapsulePresentationMode.Suppressed || beforeShow is null && !IsVisible) SuppressForForeground();
        if (!IsVisible) Show();
        ApplyMaterial();
        countdownClock.Start();
        pointerClock.Start();
        AnimationHelper.StopAnimations(surface);
        surface.Opacity = foregroundSuppressed ? 0 : 1;
        if (!foregroundSuppressed)
        {
            surface.Opacity = 0;
            AnimationHelper.FadeTo(surface, 1, AnimationDurations.WindowShow);
        }
        DisplayEnvironmentChanged();
    }

    public void TogglePanel()
    {
        if (IsVisible) HidePanel(); else ShowPanel();
    }

    public void HidePanel()
    {
        if (!IsVisible || hideInProgress) return;
        transitionSerial++;
        hideInProgress = true;
        collapseDelay.Stop();
        countdownClock.Stop();
        pointerClock.Stop();
        StopMorph();
        AnimationHelper.StopAnimations(expandedPanel);
        AnimationHelper.Conceal(surface, () =>
        {
            if (!hideInProgress) return;
            hideInProgress = false;
            Hide();
            surface.Opacity = 1;
        }, AnimationDurations.WindowHide);
    }

    public void HideImmediately()
    {
        transitionSerial++;
        AnimationHelper.StopWindowBounds(this);
        StopMorph();
        hideInProgress = false;
        collapseDelay.Stop();
        countdownClock.Stop();
        pointerClock.Stop();
        AnimationHelper.StopAnimations(surface);
        Hide();
        surface.Opacity = 1;
    }

    public void RefreshView()
    {
        if (monitor is null) return;
        AnimationHelper.Enabled = monitor.Settings.EnableAnimations;
        creditBadge.EnableAnimations = monitor.Settings.EnableAnimations;
        expandedCreditBadge.EnableAnimations = monitor.Settings.EnableAnimations;
        var data = QuotaPresentation.From(monitor);
        var control = center?.Snapshot;
        var five = data.FiveHour;
        var week = data.Weekly;
        var reserve = data.Snapshot?.Windows.FirstOrDefault(x => x.LimitId == "base_model_inference" || x.Name.Contains("reserve", StringComparison.OrdinalIgnoreCase));
        fiveText.Text = "5h " + QuotaPresentation.Remaining(five?.Remaining);
        weekText.Text = "周 " + QuotaPresentation.Remaining(week?.Remaining);
        expandedFive.Text = "外环 · 5h  " + QuotaPresentation.Remaining(five?.Remaining);
        expandedWeek.Text = "内环 · 周  " + QuotaPresentation.Remaining(week?.Remaining);
        compactRings.Update(five?.Remaining, week?.Remaining, monitor.IsStale);
        expandedRings.Update(five?.Remaining, week?.Remaining, monitor.IsStale);
        creditBadge.AvailableCount = data.AvailableCount;
        expandedCreditBadge.AvailableCount = data.AvailableCount;
        var online = monitor.Status == MonitorStatus.Live && !monitor.IsStale;
        var state = monitor.Status switch
        {
            MonitorStatus.Live when !monitor.IsStale => "Codex 在线",
            MonitorStatus.Live => "数据已过期",
            MonitorStatus.DesktopClosed => "Codex 未运行",
            MonitorStatus.NeedsLogin => "未登录 / 认证失败",
            MonitorStatus.CliMissing => "未找到 Codex CLI",
            MonitorStatus.Offline => "网络不可用",
            MonitorStatus.Sleeping => "系统已暂停",
            MonitorStatus.Starting => "正在连接",
            _ => "读取失败 · 数据已过期"
        };
        if (control is not null)
        {
            state = control.ExitRequested ? "安全退出中"
                : control.Guard.State == QuotaGuardState.Exhausted ? "额度已耗尽"
                : control.Guard.State == QuotaGuardState.Protected ? "额度保护中"
                : control.SafeStopRequested ? "恢复待确认"
                : control.Guard.State == QuotaGuardState.Warning ? "Codex 低额度"
                : control.Guard.State == QuotaGuardState.Unknown && online ? "额度待验证"
                : state;
        }
        stateText.Text = state;
        expandedState.Text = "●  " + state + (reserve is null ? "" : "  ·  GPT reserve " + QuotaPresentation.Remaining(reserve.Remaining));
        var stateBrush = online ? "Accent"
            : monitor.Status is MonitorStatus.Live or MonitorStatus.DesktopClosed or MonitorStatus.Sleeping or MonitorStatus.Stopped ? "Muted"
            : monitor.Status is MonitorStatus.NeedsLogin or MonitorStatus.ProtocolError or MonitorStatus.Offline ? "Danger"
            : "Warning";
        if (control is not null)
        {
            if (control.SafeStopRequested || control.Guard.State is QuotaGuardState.Protected or QuotaGuardState.Exhausted)
                stateBrush = "Danger";
            else if (control.ExitRequested || control.Guard.State == QuotaGuardState.Warning)
                stateBrush = "Warning";
            else if (control.Guard.State == QuotaGuardState.Unknown && online)
                stateBrush = "Muted";
        }
        stateText.SetResourceReference(TextBlock.ForegroundProperty, stateBrush);
        expandedState.SetResourceReference(TextBlock.ForegroundProperty, stateBrush);
        UpdateCountdown(five);
        resetExact.Text = week?.ResetsAt is { } weekReset ? "周重置 " + QuotaPresentation.ResetTime(weekReset) : "周重置时间 --";
        var quotaUpdated = (monitor.IsStale ? "旧数据 · " : "") + QuotaPresentation.LastUpdated(data.Snapshot);
        updated.Text = quotaUpdated;
        creditExpiry.Text = data.Snapshot?.CreditExpirations.Count > 0
            ? "到期 " + QuotaPresentation.ResetTime(data.Snapshot.CreditExpirations.Min())
            : "重置机会到期 --";
        RefreshActivity(quotaUpdated);
        AutomationProperties.SetName(this, $"AI 控制中心额度胶囊，{state}，5 小时 {QuotaPresentation.Remaining(five?.Remaining)}，每周 {QuotaPresentation.Remaining(week?.Remaining)}，重置次数 {QuotaPresentation.Credits(data.AvailableCount)}，{updated.Text}");
    }

    private void RefreshActivity(string? quotaDetail = null)
    {
        if (center is null && workflow is null) return;
        lastQuotaDetail = quotaDetail ?? lastQuotaDetail;
        var activity = CapsuleActivityPresentation.From(center?.Snapshot, workflow?.State, workflow?.IsRunning == true, lastQuotaDetail);
        updated.Text = activity.Summary;
        workflowIndicator.SetResourceReference(Shape.FillProperty, activity.Tone);
        AutomationProperties.SetName(workflowIndicator, activity.Summary);
        if (lastDetail == activity.Detail) return;
        lastDetail = activity.Detail;
        foreach (var target in new FrameworkElement[] { updated, stateText, expandedState, compactRings, expandedRings, workflowIndicator })
        {
            target.ToolTip = null;
            AutomationProperties.SetHelpText(target, activity.Detail ?? string.Empty);
        }
    }

    public void ApplyMaterial()
    {
        // The layered host uses the theme's Glass fill. DWM backdrop/rounding applies to
        // the whole HWND and would make its transparent input margins opaque.
    }

    public void StopTimers()
    {
        collapseDelay.Stop();
        countdownClock.Stop();
        pointerClock.Stop();
        AnimationHelper.StopWindowBounds(this);
        StopMorph();
        displays.Stop();
        AnimationHelper.StopAnimations(surface);
        surface.Opacity = 1;
    }

    internal void SetExpandedForLayout(bool value)
    {
        transitionSerial++;
        AnimationHelper.StopWindowBounds(this);
        expanded = value;
        expandedAtUtc = DateTime.UtcNow - TimeSpan.FromSeconds(1);
        StopMorph();
        capsuleShape.Rect = VisualRectangle(value);
        collapsedPanel.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
        collapsedPanel.Opacity = value ? 0 : 1;
        expandedPanel.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        expandedPanel.Opacity = value ? 1 : 0;
        UpdateLayout();
    }

    internal void SetExpandedForAnimationCheck(bool value)
    {
        if (value) Expand(); else Collapse();
    }

    internal double ExpandedOpacityForAnimationCheck => expandedPanel.Opacity;

    internal bool CollapsePending => collapseDelay.IsEnabled;

    internal Rect VisualBoundsForCheck => capsuleShape.Rect;
    internal bool EdgeHiddenForCheck => edgeHidden;
    internal bool ExpandedForCheck => expanded;
    internal void ObservePointerForCheck() => ObservePointer();
    internal bool FullscreenForCheck => fullscreen;
    internal string ActivitySummaryForCheck => updated.Text;
    internal FrameworkElement? ActivityToolTipForCheck => (updated.ToolTip as ToolTip)?.Content as FrameworkElement;
    internal string ActivityHelpForCheck => AutomationProperties.GetHelpText(updated);
    internal bool SuppressedForCheck => foregroundSuppressed;
    internal double EffectiveOpacityForCheck => Opacity * surface.Opacity;
    internal bool DisplayObserverRunningForCheck => displays.IsRunning;
    internal CapsuleDockSide DockSideForCheck => dockSide;
    internal Rect VisibleEdgeStripForCheck => VisibleEdgeStrip();
    internal void SetFullscreenForCheck(bool value) => SetPresentationForCheck(value ? CapsulePresentationMode.Edge : CapsulePresentationMode.Normal);
    internal void SetPresentationForCheck(CapsulePresentationMode? value) { presentationOverride = value; DisplayEnvironmentChanged(); }
    internal void DragForCheck(double deltaPhysicalX, double deltaPhysicalY)
    {
        ApplyDrag(normalCenterX + deltaPhysicalX, normalTopY + deltaPhysicalY);
        PersistPosition();
    }
    internal void SetPositionForCheck(double centerX, double topY)
    {
        dockSide = CapsuleDockSide.None;
        fullscreen = edgeHidden = false;
        ApplyDrag(centerX, topY);
        PersistPosition();
        DisplayEnvironmentChanged();
    }
    internal void BeginDragForCheck()
    {
        if (foregroundSuppressed || edgeHidden) return;
        StopPositionAnimation();
        dragAnchor = CurrentPhysicalAnchor();
        dragStart = dragAnchor;
        StartDragging();
    }
    internal void UpdateDragForCheck(double deltaPhysicalX, double deltaPhysicalY)
    {
        if (dragging) ApplyDrag(dragAnchor.X + deltaPhysicalX, dragAnchor.Y + deltaPhysicalY);
    }
    internal void EndDragForCheck() => FinishDrag();

    internal void HandlePointerPresence(bool inside)
    {
        if (pointerProvider() is { } cursor) HandlePointerAtForCheck(inside, cursor.X, cursor.Y);
        else if (!inside) QueueCollapse();
    }

    internal void HandlePointerAtForCheck(bool inside, double physicalX, double physicalY)
    {
        if (!UserInteractionAllowed() || dragging) return;
        RearmEdgeHover(physicalX, physicalY);
        if (inside && HitVisibleSurface(physicalX, physicalY))
        {
            if (edgeHidden && (edgeHoverConsumed || !HitRestingEdgeStrip(physicalX, physicalY))) return;
            collapseDelay.Stop();
            if (edgeHidden) { edgeHoverConsumed = true; RevealFromEdge(); }
            if (!expanded) Expand();
        }
        else QueueCollapse();
    }

    private void QueueCollapse()
    {
        // Cursor sampling can temporarily fail during capture. Never schedule a retreat
        // through that fallback while a drag or foreground suppression is active.
        if (dragging || foregroundSuppressed || !IsVisible || hideInProgress) return;
        // Repeated mouse moves/observations in a transparent margin must not restart dwell.
        if (!collapseDelay.IsEnabled && (expanded || fullscreen && !edgeHidden))
        {
            collapseDelay.Interval = TimeSpan.FromSeconds(monitor.Settings.CapsuleStaySeconds);
            collapseDelay.Start();
        }
    }

    private bool PointerOverVisibleSurface() => pointerProvider() is { } cursor && HitVisibleSurface(cursor.X, cursor.Y);

    private void ObservePointer()
    {
        if (!IsVisible || hideInProgress || foregroundSuppressed || dragging || pointerProvider() is not { } cursor) return;
        RearmEdgeHover(cursor.X, cursor.Y);
        // Observation only maintains/rearms hover. It never initiates a reveal or expansion.
        if (HitVisibleSurface(cursor.X, cursor.Y)) collapseDelay.Stop();
        else QueueCollapse();
    }

    private void RearmEdgeHover(double physicalX, double physicalY)
    {
        if (!double.IsFinite(physicalX) || !double.IsFinite(physicalY)) return;
        // The fixed resting strip does not move with the HWND animation. Window-induced
        // MouseLeave/MouseEnter at the same physical point cannot count as a new visit.
        if (edgeHoverConsumed && !HitRestingEdgeStrip(physicalX, physicalY)) edgeHoverConsumed = false;
    }

    private bool HitRestingEdgeStrip(double physicalX, double physicalY)
    {
        if (dockSide == CapsuleDockSide.None || currentMonitor is not { } screen) return false;
        var point = new Point(physicalX, physicalY);
        if (!screen.Bounds.Contains(point)) return false;
        var atEdge = dockSide switch
        {
            CapsuleDockSide.Top => physicalY < screen.Bounds.Top + 3 * screen.ScaleY,
            CapsuleDockSide.Left => physicalX < screen.Bounds.Left + 3 * screen.ScaleX,
            CapsuleDockSide.Right => physicalX >= screen.Bounds.Right - 3 * screen.ScaleX,
            _ => false
        };
        if (!atEdge) return false;
        var resting = HostPosition(true, false);
        var local = new Point((physicalX - resting.X) / screen.ScaleX, (physicalY - resting.Y) / screen.ScaleY);
        return new RectangleGeometry(VisualRectangle(false), 15, 15).FillContains(local);
    }

    internal bool IsSettledForAnimationCheck(bool value) =>
        expanded == value &&
        Math.Abs(capsuleShape.Rect.Width - (value ? ExpandedWidth : CollapsedWidth)) < .5 &&
        Math.Abs(capsuleShape.Rect.Height - (value ? ExpandedHeight : CollapsedHeight)) < .5 &&
        Math.Abs(ActualWidth - HostWidth) < 1.1 &&
        Math.Abs(ActualHeight - HostHeight) < 1.1 &&
        expandedPanel.Visibility == (value ? Visibility.Visible : Visibility.Collapsed) &&
        collapsedPanel.Visibility == (value ? Visibility.Collapsed : Visibility.Visible);

    private void OnMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!UserInteractionAllowed() || dragging || FindButton(e.OriginalSource as DependencyObject) is not null) return;
        if (!GetCursorPos(out var cursor) || !ClickAtForCheck(cursor.X, cursor.Y)) return;
        e.Handled = true;
    }

    internal bool ClickAtForCheck(double physicalX, double physicalY)
    {
        if (!UserInteractionAllowed() || dragging || !HitVisibleSurface(physicalX, physicalY)) return false;
        if (edgeHidden) { edgeHoverConsumed = true; RevealFromEdge(); Expand(); return true; }
        if (!expanded) Expand();
        else if (DateTime.UtcNow - expandedAtUtc >= AnimationDurations.CapsuleClickGrace) openFull();
        return true;
    }

    internal bool ContextAtForCheck(double physicalX, double physicalY)
        => UserInteractionAllowed() && !edgeHidden && HitVisibleSurface(physicalX, physicalY);

    private static Button? FindButton(DependencyObject? item)
    {
        while (item is not null)
        {
            if (item is Button button) return button;
            item = item is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(item)
                : LogicalTreeHelper.GetParent(item);
        }
        return null;
    }

    private bool UserInteractionAllowed()
    {
        // Input may arrive before the queued foreground notification. Recheck on entry
        // so touching the top edge cannot briefly reveal the capsule over a newly focused game.
        if (presentationOverride is null && IsVisible) DisplayEnvironmentChanged();
        return !foregroundSuppressed && !hideInProgress;
    }

    private void Expand(bool resume = false)
    {
        if (foregroundSuppressed || expanded && !resume || !IsVisible) return;
        var serial = ++transitionSerial;
        expanded = true;
        expandedAtUtc = DateTime.UtcNow;
        RefreshView();
        if (!dragging && fullscreen && !edgeHidden) MoveToPosition(HostPosition(false, true));
        expandedPanel.Visibility = Visibility.Visible;
        collapsedPanel.Visibility = Visibility.Visible;
        AnimateMorph(VisualRectangle(true), AnimationDurations.CapsuleExpand, () =>
        {
            if (serial == transitionSerial && expanded)
                collapsedPanel.Visibility = Visibility.Collapsed;
        });
        AnimationHelper.SlideCapsuleContent(expandedPanel, true);
        AnimationHelper.FadeTo(expandedPanel, 1, AnimationDurations.CapsuleContent);
        AnimationHelper.FadeTo(collapsedPanel, 0, AnimationDurations.CapsuleContent);
    }

    private void Collapse(bool resume = false)
    {
        if (!expanded && !resume) return;
        var serial = ++transitionSerial;
        collapseDelay.Stop();
        expanded = false;
        if (!dragging && fullscreen && !edgeHidden) MoveToPosition(HostPosition(false, false));
        collapsedPanel.Visibility = Visibility.Visible;
        AnimationHelper.SlideCapsuleContent(expandedPanel, false);
        AnimationHelper.FadeTo(collapsedPanel, 1, AnimationDurations.CapsuleContent);
        AnimationHelper.FadeTo(expandedPanel, 0, AnimationDurations.CapsuleContent);
        AnimateMorph(VisualRectangle(false), AnimationDurations.CapsuleCollapse, () =>
        {
            if (serial == transitionSerial && !expanded)
                expandedPanel.Visibility = Visibility.Collapsed;
        });
    }

    private void UpdateCountdown(QuotaWindow? five)
    {
        resetCountdown.Text = five?.ResetsAt is { } reset ? QuotaPresentation.Countdown(reset) : "时间未知";
        AutomationProperties.SetName(resetCountdown, "5 小时额度重置倒计时：" + resetCountdown.Text);
    }

    private void PositionAtActiveDisplay()
    {
        var settings = monitor.Settings;
        var preferred = settings.CapsuleMonitorDevice;
        if (currentMonitor is null && settings.CapsuleCenterX is null && GetCursorPos(out var cursor))
            preferred ??= displays.GetMonitorForPoint(cursor.X, cursor.Y)?.DeviceName;
        var result = CapsulePlacement.Restore(displays.Monitors.Select(m => m.ToPlacementMonitor()).ToArray(),
            preferred ?? currentMonitor?.DeviceName, settings.CapsuleCenterX ?? (currentMonitor is null ? null : normalCenterX),
            settings.CapsuleTopY ?? (currentMonitor is null ? null : normalTopY), ExpandedWidth, ExpandedHeight, 10);
        if (result is not { } placement) return;
        currentMonitor = displays.Monitors.First(m => m.DeviceName == placement.MonitorDevice);
        normalCenterX = placement.CenterX;
        normalTopY = placement.TopY;
        ApplyNormalPosition();
    }

    private static Rect VisualRectangle(bool opened)
    {
        var width = opened ? ExpandedWidth : CollapsedWidth;
        return new Rect((HostWidth - width) / 2, VisualPadding, width, opened ? ExpandedHeight : CollapsedHeight);
    }

    private void StopMorph()
    {
        var current = capsuleShape.Rect;
        capsuleShape.BeginAnimation(RectangleGeometry.RectProperty, null);
        capsuleShape.Rect = current;
    }

    private void AnimateMorph(Rect target, TimeSpan duration, Action completed)
    {
        var from = capsuleShape.Rect;
        StopMorph();
        if (!AnimationHelper.Enabled || !IsVisible) { capsuleShape.Rect = target; completed(); return; }
        Rect Along(double fraction)
        {
            var width = from.Width + (target.Width - from.Width) * fraction;
            var height = from.Height + (target.Height - from.Height) * fraction;
            return new Rect((HostWidth - width) / 2, VisualPadding, width, height);
        }
        var serial = transitionSerial;
        var animation = new RectAnimationUsingKeyFrames { Duration = duration, FillBehavior = FillBehavior.HoldEnd };
        animation.KeyFrames.Add(new DiscreteRectKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new SplineRectKeyFrame(Along(1.06), KeyTime.FromTimeSpan(TimeSpan.FromTicks((long)(duration.Ticks * .62))), new KeySpline(.32, 0, .20, 1)));
        animation.KeyFrames.Add(new SplineRectKeyFrame(Along(.991), KeyTime.FromTimeSpan(TimeSpan.FromTicks((long)(duration.Ticks * .82))), new KeySpline(.38, 0, .62, 1)));
        animation.KeyFrames.Add(new SplineRectKeyFrame(target, KeyTime.FromTimeSpan(duration), new KeySpline(.38, 0, .62, 1)));
        animation.Completed += (_, _) =>
        {
            if (serial != transitionSerial) return;
            StopMorph(); capsuleShape.Rect = target;
            completed();
        };
        capsuleShape.BeginAnimation(RectangleGeometry.RectProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private void StopPositionAnimation()
    {
        positionSerial++;
        var left = Left; var top = Top;
        BeginAnimation(LeftProperty, null); BeginAnimation(TopProperty, null);
        Left = left; Top = top;
    }

    private void ApplyNormalPosition()
    {
        if (currentMonitor is not { } screen) return;
        StopPositionAnimation();
        var position = HostPosition(edgeHidden, expanded);
        var x = position.X; var y = position.Y;
        Left = x / screen.ScaleX; Top = y / screen.ScaleY;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
            _ = SetWindowPos(hwnd, IntPtr.Zero, (int)Math.Round(x), (int)Math.Round(y),
                (int)Math.Round(HostWidth * screen.ScaleX), (int)Math.Round(HostHeight * screen.ScaleY), SwpNoZOrder | SwpNoActivate);
        ClipToDockMonitor();
    }

    private CapsuleDockSide GetPositionDockSide() => currentMonitor is null ? CapsuleDockSide.None
        : CapsulePlacement.GetDockSide(currentMonitor.ToPlacementMonitor(), normalCenterX, normalTopY, ExpandedWidth, ExpandedHeight);

    private Point HostPosition(bool hidden, bool opened)
    {
        if (currentMonitor is not { } screen) return new Point(Left, Top);
        var x = normalCenterX - HostWidth * screen.ScaleX / 2;
        var y = normalTopY - VisualPadding * screen.ScaleY;
        if (!dragging)
        {
            var shape = VisualRectangle(hidden ? false : opened);
            switch (dockSide)
            {
                case CapsuleDockSide.Top:
                    y = screen.Bounds.Top + (hidden ? 3 - VisualPadding - CollapsedHeight : 10 - VisualPadding) * screen.ScaleY;
                    break;
                case CapsuleDockSide.Left:
                    x = screen.Bounds.Left + (hidden ? 3 - shape.Right : 10 - shape.Left) * screen.ScaleX;
                    break;
                case CapsuleDockSide.Right:
                    x = screen.Bounds.Right - (hidden ? 3 + shape.Left : 10 + shape.Right) * screen.ScaleX;
                    break;
            }
        }
        return new Point(x, y);
    }

    private void MoveToPosition(Point physical)
    {
        var scale = VisualTreeHelper.GetDpi(this);
        var from = new Point(Left, Top);
        StopPositionAnimation();
        var target = new Point(physical.X / scale.DpiScaleX, physical.Y / scale.DpiScaleY);
        if (!AnimationHelper.Enabled) { Left = target.X; Top = target.Y; ClipToDockMonitor(); return; }
        var serial = positionSerial;
        DoubleAnimation Slide(double start, double end) => new(start, end, TimeSpan.FromMilliseconds(280))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.HoldEnd };
        var horizontal = Slide(from.X, target.X); var vertical = Slide(from.Y, target.Y);
        vertical.Completed += (_, _) =>
        {
            if (serial != positionSerial) return;
            StopPositionAnimation(); Left = target.X; Top = target.Y; ClipToDockMonitor();
        };
        BeginAnimation(LeftProperty, horizontal, HandoffBehavior.SnapshotAndReplace);
        BeginAnimation(TopProperty, vertical, HandoffBehavior.SnapshotAndReplace);
    }

    private Point CurrentPhysicalAnchor() => PointToScreen(new Point(HostWidth / 2, VisualPadding));

    private void ClipToDockMonitor()
    {
        // Sliding past a monitor boundary must not draw onto an adjacent display.
        if (surface is null) return;
        if (currentMonitor is not { } screen || dockSide == CapsuleDockSide.None || dragging
            || PresentationSource.FromVisual(this) is null) { surface.Clip = null; return; }
        surface.Clip = new RectangleGeometry(new Rect(PointFromScreen(screen.Bounds.TopLeft), PointFromScreen(screen.Bounds.BottomRight)));
    }

    private Rect VisibleEdgeStrip()
    {
        if (!edgeHidden || foregroundSuppressed || currentMonitor is not { } screen || PresentationSource.FromVisual(this) is null) return Rect.Empty;
        var rectangle = capsuleShape.Rect;
        var visible = new Rect(PointToScreen(rectangle.TopLeft), PointToScreen(rectangle.BottomRight));
        visible.Intersect(screen.Bounds);
        var band = dockSide switch
        {
            CapsuleDockSide.Top => new Rect(screen.Bounds.Left, screen.Bounds.Top, screen.Bounds.Width, 3 * screen.ScaleY),
            CapsuleDockSide.Left => new Rect(screen.Bounds.Left, screen.Bounds.Top, 3 * screen.ScaleX, screen.Bounds.Height),
            CapsuleDockSide.Right => new Rect(screen.Bounds.Right - 3 * screen.ScaleX, screen.Bounds.Top, 3 * screen.ScaleX, screen.Bounds.Height),
            _ => Rect.Empty
        };
        visible.Intersect(band);
        return visible;
    }

    private bool HitVisibleSurface(double physicalX, double physicalY)
    {
        if (!IsVisible || foregroundSuppressed || !double.IsFinite(physicalX) || !double.IsFinite(physicalY)
            || PresentationSource.FromVisual(this) is null) return false;
        var point = new Point(physicalX, physicalY);
        if (edgeHidden && !VisibleEdgeStrip().Contains(point)) return false;
        if (dockSide != CapsuleDockSide.None && !dragging && currentMonitor is { } screen && !screen.Bounds.Contains(point)) return false;
        return capsuleShape.FillContains(PointFromScreen(point));
    }

    private void DisplayEnvironmentChanged()
    {
        if (!IsVisible || hideInProgress) return;
        if (currentMonitor is null) PositionAtActiveDisplay();
        if (currentMonitor is null) return;
        var updatedMonitor = displays.Monitors.FirstOrDefault(m => m.DeviceName == currentMonitor.DeviceName);
        if (updatedMonitor is null || updatedMonitor.Bounds != currentMonitor.Bounds || updatedMonitor.WorkArea != currentMonitor.WorkArea
            || updatedMonitor.ScaleX != currentMonitor.ScaleX || updatedMonitor.ScaleY != currentMonitor.ScaleY)
        {
            // A display notification during a drag must never restore last saved coordinates.
            // Keep observing suppression, and reconcile live geometry when the drag finishes.
            if (!dragging) { PositionAtActiveDisplay(); PersistPosition(); }
        }
        if (currentMonitor is null) return;
        var presentation = presentationOverride ?? displays.GetForegroundPresentation(new WindowInteropHelper(this).Handle, currentMonitor);
        if (presentation is null) return; // Do not flash the capsule on a transient missing foreground window.
        if (presentation == CapsulePresentationMode.Suppressed) { SuppressForForeground(); return; }
        if (foregroundSuppressed) RestoreAfterSuppression(presentation.Value);
        if (dragging) return;
        // Normal/Edge foreground hints never move or reveal a position-docked capsule.
        // Only explicit proximity to an upper/side screen edge selects docking.
        var next = GetPositionDockSide();
        if (next == dockSide) return;
        edgeHoverConsumed = false; // A different dock is a new activation surface.
        dockSide = next;
        fullscreen = next != CapsuleDockSide.None;
        edgeHidden = false;
        if (fullscreen) HideAtEdge();
        else
        {
            edgeHidden = false;
            collapseDelay.Stop();
            ClipToDockMonitor();
            MoveToPosition(HostPosition(false, expanded));
            if (!PointerOverVisibleSurface()) Collapse();
        }
    }

    private void HideAtEdge()
    {
        if (foregroundSuppressed || currentMonitor is null || !fullscreen || edgeHidden || dragging) return;
        edgeHidden = true;
        collapseDelay.Stop();
        Collapse();
        MoveToPosition(HostPosition(true, false));
    }

    private void SuppressForForeground()
    {
        if (foregroundSuppressed) return;
        foregroundSuppressed = true;
        inputBeforeSuppression = IsHitTestVisible;
        opacityBeforeSuppression = Opacity;
        // Keep the HWND/observer alive, but remove every rendered pixel and input target.
        // WS_EX_TRANSPARENT on a layered HWND passes mouse events to windows underneath.
        // https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features#layered-windows
        SetNativeSuppressedInput(true);
        BeginAnimation(OpacityProperty, null); Opacity = 0;
        IsHitTestVisible = false;
        ContextMenu?.SetCurrentValue(System.Windows.Controls.ContextMenu.IsOpenProperty, false);
        collapseDelay.Stop();
        transitionSerial++;
        dragStart = null; dragging = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        StopPositionAnimation();
        StopMorph();
        AnimationHelper.StopAnimations(surface);
        surface.Opacity = 0;
        expanded = fullscreen = edgeHidden = false;
        capsuleShape.Rect = VisualRectangle(false);
        collapsedPanel.Visibility = Visibility.Visible; collapsedPanel.Opacity = 1;
        expandedPanel.Visibility = Visibility.Collapsed; expandedPanel.Opacity = 0;
    }

    private void RestoreAfterSuppression(CapsulePresentationMode mode)
    {
        // Position while fully transparent; never flash a full capsule over the old game.
        dockSide = GetPositionDockSide();
        fullscreen = edgeHidden = dockSide != CapsuleDockSide.None;
        ApplyNormalPosition();
        AnimationHelper.StopAnimations(surface);
        surface.Opacity = 1;
        SetNativeSuppressedInput(false);
        foregroundSuppressed = false;
        Opacity = opacityBeforeSuppression;
        IsHitTestVisible = inputBeforeSuppression;
    }

    private void SetNativeSuppressedInput(bool suppressed)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        const int exStyleIndex = -20;
        const long transparent = 0x20;
        var current = GetWindowLongPtr(hwnd, exStyleIndex).ToInt64();
        long next;
        if (suppressed)
        {
            if (!nativeInputSnapshotTaken)
            {
                nativeTransparentBeforeSuppression = (current & transparent) != 0;
                nativeInputSnapshotTaken = true;
            }
            next = current | transparent;
        }
        else
        {
            if (!nativeInputSnapshotTaken) return;
            next = nativeTransparentBeforeSuppression ? current | transparent : current & ~transparent;
            nativeInputSnapshotTaken = false;
        }
        if (next != current) SetWindowLongPtr(hwnd, exStyleIndex, new IntPtr(next));
    }

    private void RevealFromEdge()
    {
        if (foregroundSuppressed || currentMonitor is null || !edgeHidden) return;
        edgeHidden = false;
        MoveToPosition(HostPosition(false, true));
    }

    private void BeginDrag(object sender, MouseButtonEventArgs e)
    {
        if (!UserInteractionAllowed() || FindButton(e.OriginalSource as DependencyObject) is not null || edgeHidden
            || !GetCursorPos(out var cursor) || !HitVisibleSurface(cursor.X, cursor.Y)) return;
        collapseDelay.Stop();
        StopPositionAnimation();
        dragStart = new Point(cursor.X, cursor.Y);
        // This is the current rendered location, including an interrupted slide. Saved
        // normal coordinates can be far away and must never be a drag's starting anchor.
        dragAnchor = CurrentPhysicalAnchor();
        CaptureMouse();
    }

    private void UpdateDrag(object sender, MouseEventArgs e)
    {
        if (foregroundSuppressed || dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed || !GetCursorPos(out var cursor)) return;
        var dx = cursor.X - start.X; var dy = cursor.Y - start.Y;
        var scale = VisualTreeHelper.GetDpi(this);
        if (!dragging && Math.Abs(dx) < SystemParameters.MinimumHorizontalDragDistance * scale.DpiScaleX
            && Math.Abs(dy) < SystemParameters.MinimumVerticalDragDistance * scale.DpiScaleY) return;
        if (!dragging) StartDragging();
        ApplyDrag(dragAnchor.X + dx, dragAnchor.Y + dy, cursor.X, cursor.Y);
        e.Handled = true;
    }

    private void ApplyDrag(double centerX, double topY, double? cursorX = null, double? cursorY = null)
    {
        var screen = displays.GetMonitorForPoint(cursorX ?? centerX, cursorY ?? topY);
        if (screen is null) return;
        if (!double.IsFinite(centerX) || !double.IsFinite(topY) || Math.Abs(centerX) > int.MaxValue || Math.Abs(topY) > int.MaxValue) return;
        var clamped = dragging ? null : CapsulePlacement.Clamp(screen.ToPlacementMonitor(), centerX, topY, ExpandedWidth, ExpandedHeight, 10);
        if (!dragging && clamped is null) return;
        currentMonitor = screen;
        // During an interrupted slide a portion can still be outside the screen. Follow
        // the actual pointer delta first; enforce the restorable footprint only on release.
        normalCenterX = clamped?.CenterX ?? centerX; normalTopY = clamped?.TopY ?? topY;
        // Drag directly; an old dock must not snap this new position back to its edge.
        var wasDragging = dragging;
        dragging = true;
        ApplyNormalPosition();
        dragging = wasDragging;
        ClipToDockMonitor();
    }

    private void EndDrag(object sender, MouseButtonEventArgs e)
    {
        var moved = dragging;
        FinishDrag();
        if (moved) e.Handled = true;
    }

    private void FinishDrag()
    {
        if (dragStart is null) return;
        var moved = dragging;
        dragStart = null; dragging = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (moved)
        {
            var screen = displays.GetMonitorForPoint(normalCenterX, normalTopY);
            var placement = screen is null ? null : CapsulePlacement.Clamp(screen.ToPlacementMonitor(), normalCenterX, normalTopY, ExpandedWidth, ExpandedHeight, 10);
            if (placement is { } position)
            {
                currentMonitor = screen;
                normalCenterX = position.CenterX; normalTopY = position.TopY;
            }
            dockSide = GetPositionDockSide();
            fullscreen = dockSide != CapsuleDockSide.None;
            edgeHidden = false;
            ClipToDockMonitor();
            PersistPosition();
            MoveToPosition(HostPosition(false, expanded));
            if (expanded) Expand(resume: true); else Collapse(resume: true);
        }
        DisplayEnvironmentChanged();
        HandlePointerPresence(true);
    }

    private void StartDragging()
    {
        dragging = true;
        edgeHoverConsumed = false; // Explicit placement must not inherit the old dock's visit.
        collapseDelay.Stop();
        transitionSerial++;
        StopMorph();
        StopPositionAnimation();
        // Adopt the frozen, physically rendered anchor without moving the HWND.
        var anchor = CurrentPhysicalAnchor();
        normalCenterX = anchor.X; normalTopY = anchor.Y;
        edgeHidden = false;
        ClipToDockMonitor();
        foreach (var panel in new[] { expandedPanel, collapsedPanel })
        {
            var opacity = panel.Opacity;
            panel.BeginAnimation(OpacityProperty, null); panel.Opacity = opacity;
            if (panel.RenderTransform is TransformGroup transforms)
                foreach (var translate in transforms.Children.OfType<TranslateTransform>())
                { var y = translate.Y; translate.BeginAnimation(TranslateTransform.YProperty, null); translate.Y = y; }
        }
    }

    private void PersistPosition()
    {
        if (currentMonitor is null) return;
        monitor.Settings.CapsuleCenterX = normalCenterX;
        monitor.Settings.CapsuleTopY = normalTopY;
        monitor.Settings.CapsuleMonitorDevice = currentMonitor.DeviceName;
        savePosition?.Invoke();
    }

    private void ResetPosition()
    {
        if (foregroundSuppressed) return;
        dockSide = CapsuleDockSide.None;
        fullscreen = edgeHidden = false;
        monitor.Settings.CapsuleCenterX = monitor.Settings.CapsuleTopY = null;
        monitor.Settings.CapsuleMonitorDevice = null;
        currentMonitor = null;
        PositionAtActiveDisplay();
        PersistPosition();
        fullscreen = false; edgeHidden = false;
        DisplayEnvironmentChanged();
    }

    private void DisplaySettingsChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(displays.Refresh);

    private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmDpiChanged)
            Dispatcher.BeginInvoke(new Action(() => { if (!dragging) { ApplyNormalPosition(); DisplayEnvironmentChanged(); } }), DispatcherPriority.Loaded);
        // Never activate the capsule on hover or dragging above a fullscreen app.
        if (message == 0x0021) { handled = true; return new IntPtr(3); } // MA_NOACTIVATE
        if (message == 0x0084)
        {
            if (foregroundSuppressed) { handled = true; return new IntPtr(-1); }
            var packed = lParam.ToInt64();
            if (!HitVisibleSurface((short)(packed & 0xffff), (short)((packed >> 16) & 0xffff)))
            { handled = true; return new IntPtr(-1); } // HTTRANSPARENT, signed screen coordinates
        }
        return IntPtr.Zero;
    }

    private static TextBlock Label(string text, double size, bool bold)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        return block;
    }

    private static TextBlock Metric(string text, double size)
    {
        var block = Label(text, size, true);
        block.Margin = new Thickness(9, 0, 0, 0);
        block.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        AutomationProperties.SetName(block, text);
        return block;
    }
}

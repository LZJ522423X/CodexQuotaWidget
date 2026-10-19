using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Documents;
using CodexQuotaWidget.Core;

namespace CodexQuotaWidget;

internal sealed class SkillUpdatePanel : Border
{
    readonly SkillUpdateController controller;
    readonly Func<bool> animations;
    readonly SkillUpdateButton action = new();
    readonly TextBlock status = new() { Name = "SkillUpdateStatus", FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new(0, 10, 0, 0) };
    readonly TextBlock detail = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 0) };
    readonly Expander details;
    readonly StackPanel results = new() { Margin = new(0, 8, 0, 4) };
    UnifiedUpdateCheck? displayedCheck;
    readonly List<(string Name, CheckBox Box)> choices = [];
    bool subscribed;
    internal SkillUpdatePanel(Func<bool> animations, SkillUpdateController? controller = null)
    {
        this.animations = animations; this.controller = controller ?? SkillUpdateController.Shared;
        Margin = new(0, 16, 0, 16); Padding = new(16); CornerRadius = new(14); BorderThickness = new(1);
        SetResourceReference(BackgroundProperty, "Surface"); SetResourceReference(BorderBrushProperty, "Line");
        var body = new StackPanel(); Child = body;
        var heading = new TextBlock { Text = "更新结果与安装", FontSize = 14, FontWeight = FontWeights.SemiBold };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "Ink"); body.Children.Add(heading);
        var help = new TextBlock { Text = "使用现有「检查更新」统一检查。勾选确认的 Skill 后，点击下方按钮安装；检查本身不会安装。", FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new(0, 4, 0, 12) };
        help.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); body.Children.Add(help);
        body.Children.Add(results);
        var row = new Grid(); row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); body.Children.Add(row);
        row.Children.Add(action);
        var logs = new Button { Content = "查看日志", Margin = new(10, 0, 0, 0), Padding = new(12, 0, 12, 0), Height = 52 };
        AutomationProperties.SetName(logs, "查看 Codex Skills 更新日志"); Grid.SetColumn(logs, 1); row.Children.Add(logs);
        logs.Click += (_, _) => {
            try { var path = Path.Combine(SkillUpdateController.Root, "logs"); if (!Directory.Exists(path)) { status.Text = "暂无更新日志"; return; }
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException) { status.Text = "无法打开日志目录"; }
        };
        status.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite); body.Children.Add(status);
        detail.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        details = new Expander { Header = "查看结果详情", IsExpanded = false, Content = detail, Margin = new(0, 6, 0, 0), Visibility = Visibility.Collapsed };
        body.Children.Add(details);
        action.Click += async (_, _) => await this.controller.RunAsync();
        Loaded += (_, _) => { Subscribe(); Refresh(); }; Unloaded += (_, _) => { Unsubscribe(); action.StopMotion(); };
        Refresh();
    }
    void Subscribe() { if (subscribed) return; subscribed = true; controller.Changed += OnChanged; }
    void Unsubscribe() { if (!subscribed) return; subscribed = false; controller.Changed -= OnChanged; }
    void OnChanged()
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        try { Dispatcher.BeginInvoke(new Action(Refresh)); }
        catch (InvalidOperationException) { /* The dispatcher may stop between the guard and enqueue. */ }
    }
    internal void Refresh()
    {
        var current = controller.State;
        action.Present(controller.IsRunning ? SkillUpdatePhase.Running : current.Phase, animations());
        action.IsEnabled = controller.CanInstall;
        action.Opacity = action.IsEnabled || controller.IsRunning ? 1 : .5;
        status.Text = current.Summary; detail.Text = current.Details;
        if (!ReferenceEquals(displayedCheck, controller.CheckResult))
        {
            displayedCheck = controller.CheckResult; results.Children.Clear(); choices.Clear();
            if (displayedCheck is not null)
            {
                var other = new StackPanel(); var count = 0;
                foreach (var item in displayedCheck.Items)
                {
                    var primary = item.Kind == "组件" || item.Status is UpdateCheckStatus.Available or UpdateCheckStatus.UpToDate;
                    var target = primary ? results : other;
                    var row = new StackPanel { Margin = new(0, 4, 0, 8) };
                    var label = item.Name + " · " + UnifiedUpdateCheck.Label(item.Status);
                    if (item.Kind == "Skill" && item.Status == UpdateCheckStatus.Available)
                    {
                        var box = new CheckBox { Content = label, IsChecked = controller.IsSelected(item.Name), MinHeight = 36 };
                        choices.Add((item.Name, box));
                        box.SetResourceReference(Control.ForegroundProperty, "Ink"); AutomationProperties.SetName(box, "选择更新 " + item.Name);
                        box.Checked += (_, _) => controller.Select(item.Name, true); box.Unchecked += (_, _) => controller.Select(item.Name, false); row.Children.Add(box);
                    }
                    else row.Children.Add(Copy(label, false));
                    row.Children.Add(Copy("本地：" + (item.CurrentVersion ?? "未知") + " → 上游：" + (item.LatestVersion ?? "未知"), true));
                    row.Children.Add(Copy(item.Reason, true));
                    if (item.Kind == "组件" && item.Status == UpdateCheckStatus.Available && item.Page is { } page
                        && page.AbsoluteUri.StartsWith(ReleaseUpdates.RepositoryPage + "/releases/", StringComparison.Ordinal))
                    {
                        var link = new Hyperlink(new Run("查看发布页，手动选择安装"));
                        link.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(page.AbsoluteUri) { UseShellExecute = true }); } catch (System.ComponentModel.Win32Exception) { status.Text = "无法打开发布页"; } };
                        var text = Copy("", false); text.Inlines.Add(link); row.Children.Add(text);
                    }
                    target.Children.Add(row); if (!primary) count++;
                }
                if (count > 0) results.Children.Add(new Expander { Header = $"其他检查结果（{count}）", Content = other, IsExpanded = false });
                results.Children.Add(Copy("其他工具：现有更新器未登记可检查的工具来源。", true));
            }
        }
        foreach (var (name, box) in choices)
            if (box.IsChecked != controller.IsSelected(name)) box.SetCurrentValue(CheckBox.IsCheckedProperty, controller.IsSelected(name));
        details.Visibility = !controller.IsRunning && !string.IsNullOrWhiteSpace(current.Details) ? Visibility.Visible : Visibility.Collapsed;
        if (controller.IsRunning) details.IsExpanded = false;
    }
    static TextBlock Copy(string text, bool muted)
    {
        var block = new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new(0, 3, 0, 0) };
        block.SetResourceReference(TextBlock.ForegroundProperty, muted ? "Muted" : "Ink"); return block;
    }
}

// Transform/opacity-only motion. No timers, layout resizing, popup ToolTips or global style changes.
internal sealed class SkillUpdateButton : Button
{
    readonly ScaleTransform scale = new(1, 1);
    readonly RotateTransform rotation = new();
    readonly TextBlock glyph = new() { Text = "\uE72C", FontFamily = new("Segoe Fluent Icons"), FontSize = 20, Width = 24, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock caption = new() { Text = "更新 Codex Skills", FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    readonly Border tint = new() { CornerRadius = new(11), Opacity = .08, IsHitTestVisible = false };
    readonly Grid visual = new();
    bool motion = true, spinning;
    SkillUpdatePhase phase;
    internal SkillUpdateButton()
    {
        Name = "SkillUpdateAction"; MinWidth = 190; Height = 52; Padding = new(0); BorderThickness = new(1);
        SetResourceReference(BackgroundProperty, "Surface"); SetResourceReference(BorderBrushProperty, "Accent");
        Template = (ControlTemplate)XamlReader.Parse("<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'><Border CornerRadius='12' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}'><ContentPresenter HorizontalAlignment='Stretch' VerticalAlignment='Stretch'/></Border><ControlTemplate.Triggers><Trigger Property='IsKeyboardFocused' Value='True'><Setter Property='BorderThickness' Value='2'/></Trigger></ControlTemplate.Triggers></ControlTemplate>");
        visual.RenderTransform = scale; visual.RenderTransformOrigin = new(.5, .5); Content = visual;
        tint.SetResourceReference(Border.BackgroundProperty, "Accent"); visual.Children.Add(tint);
        var content = new Grid { Margin = new(14, 0, 14, 0) }; content.ColumnDefinitions.Add(new() { Width = new(34) }); content.ColumnDefinitions.Add(new()); visual.Children.Add(content);
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "Accent"); glyph.RenderTransform = rotation; glyph.RenderTransformOrigin = new(.5, .5); content.Children.Add(glyph);
        caption.SetResourceReference(TextBlock.ForegroundProperty, "Ink"); Grid.SetColumn(caption, 1); content.Children.Add(caption);
        AutomationProperties.SetName(this, "更新 Codex Skills"); AutomationProperties.SetHelpText(this, "自动检查、验证、更新并清理；不会修改系统或本地自建 Skill。");
        MouseEnter += (_, _) => Feedback(); MouseLeave += (_, _) => Feedback();
        Loaded += (_, _) => { Feedback(); Spin(); }; Unloaded += (_, _) => StopMotion();
        IsVisibleChanged += (_, _) => { if (IsVisible) { Feedback(); Spin(); } else StopMotion(); };
    }
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (scale is not null && (e.Property == IsPressedProperty || e.Property == IsKeyboardFocusedProperty)) Feedback();
    }
    internal void Present(SkillUpdatePhase value, bool enableAnimations)
    {
        var changed = phase != value; var motionChanged = motion != enableAnimations;
        phase = value; motion = enableAnimations;
        IsEnabled = phase != SkillUpdatePhase.Running;
        caption.Text = phase == SkillUpdatePhase.Running ? "正在更新已选 Skills" : "更新 Skill";
        glyph.Text = phase switch { SkillUpdatePhase.Completed => "\uE73E", SkillUpdatePhase.Attention or SkillUpdatePhase.Failed or SkillUpdatePhase.Unavailable => "\uE7BA", _ => "\uE72C" };
        glyph.SetResourceReference(TextBlock.ForegroundProperty, phase is SkillUpdatePhase.Attention or SkillUpdatePhase.Failed ? "Ink" : "Accent");
        AutomationProperties.SetItemStatus(this, phase == SkillUpdatePhase.Running ? "更新中" : caption.Text);
        if (changed || motionChanged) { Feedback(); Spin();
            if (!motion) glyph.BeginAnimation(OpacityProperty, null);
            if (changed && motion && phase is SkillUpdatePhase.Completed or SkillUpdatePhase.Attention)
                glyph.BeginAnimation(OpacityProperty, new DoubleAnimation(.3, 1, AnimationDurations.Success) { FillBehavior = FillBehavior.Stop });
        }
    }
    void Feedback()
    {
        var target = IsPressed && IsEnabled ? .97 : (IsMouseOver || IsKeyboardFocused) && IsEnabled ? 1.012 : 1;
        var opacity = phase == SkillUpdatePhase.Running ? .14 : IsMouseOver || IsKeyboardFocused ? .18 : .08;
        if (!motion) { scale.BeginAnimation(ScaleTransform.ScaleXProperty, null); scale.BeginAnimation(ScaleTransform.ScaleYProperty, null); scale.ScaleX = scale.ScaleY = 1;
            tint.BeginAnimation(OpacityProperty, null); tint.Opacity = opacity; return; }
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(target, AnimationDurations.ButtonHover) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(target, AnimationDurations.ButtonHover) { EasingFunction = ease });
        tint.BeginAnimation(OpacityProperty, new DoubleAnimation(opacity, AnimationDurations.ButtonHover));
    }
    void Spin()
    {
        if (phase == SkillUpdatePhase.Running && motion && IsLoaded && IsVisible)
        {
            if (spinning) return; spinning = true;
            rotation.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever });
        }
        else { spinning = false; rotation.BeginAnimation(RotateTransform.AngleProperty, null); rotation.Angle = 0; }
    }
    internal void StopMotion()
    {
        spinning = false; rotation.BeginAnimation(RotateTransform.AngleProperty, null); rotation.Angle = 0;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null); scale.BeginAnimation(ScaleTransform.ScaleYProperty, null); scale.ScaleX = scale.ScaleY = 1;
        tint.BeginAnimation(OpacityProperty, null); glyph.BeginAnimation(OpacityProperty, null);
    }
    internal double RotationAngle => rotation.Angle;
    internal bool HasRotationAnimation => rotation.HasAnimatedProperties;
}

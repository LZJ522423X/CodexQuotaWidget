using System;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;

namespace CodexQuotaWidget;

internal sealed class ConcentricQuotaRing : FrameworkElement
{
    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(nameof(Accent), typeof(Brush), typeof(ConcentricQuotaRing), new FrameworkPropertyMetadata(Brushes.MediumSpringGreen, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(nameof(Track), typeof(Brush), typeof(ConcentricQuotaRing), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty InkProperty = DependencyProperty.Register(nameof(Ink), typeof(Brush), typeof(ConcentricQuotaRing), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush Accent { get => (Brush)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public Brush Track { get => (Brush)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }
    public Brush Ink { get => (Brush)GetValue(InkProperty); set => SetValue(InkProperty, value); }
    private double? fiveHour, weekly;
    private bool stale;

    public ConcentricQuotaRing()
    {
        SetResourceReference(AccentProperty, "Accent");
        SetResourceReference(TrackProperty, "Line");
        SetResourceReference(InkProperty, "Ink");
        IsHitTestVisible = true;
    }

    public void Update(double? five, double? week, bool isStale)
    {
        static double? Clean(double? value) => value is { } n && double.IsFinite(n) ? Math.Clamp(n, 0, 100) : null;
        five = Clean(five); week = Clean(week);
        if (fiveHour == five && weekly == week && stale == isStale) return;
        fiveHour = five; weekly = week; stale = isStale;
        var label = $"外环：5 小时剩余 {QuotaPresentation.Remaining(five)}；内环：每周剩余 {QuotaPresentation.Remaining(week)}" + (stale ? "；旧数据" : "");
        AutomationProperties.SetName(this, label);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size < 12) return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var stroke = Math.Clamp(size * .065, 2.5, 6);
        var outerRadius = size / 2 - stroke / 2 - 1;
        var innerRadius = outerRadius - stroke - (size < 60 ? 2 : 4);
        var outerColor = (Accent as SolidColorBrush)?.Color ?? Colors.MediumSpringGreen;
        var blueAccent = outerColor.B > outerColor.G && outerColor.G > outerColor.R;
        var dark = ((App)Application.Current).IsDark;
        var innerColor = (Color)ColorConverter.ConvertFromString(blueAccent ? dark ? "#68E7AA" : "#168451" : dark ? "#74BAFF" : "#1365B5");
        var innerBrush = new SolidColorBrush(innerColor); innerBrush.Freeze();
        dc.PushOpacity(stale ? .55 : 1);
        DrawRing(dc, center, outerRadius, stroke, fiveHour, Accent);
        DrawRing(dc, center, innerRadius, stroke, weekly, innerBrush);
        dc.Pop();
        if (size >= 60)
        {
            DrawText(dc, center, QuotaPresentation.Remaining(weekly), 15, -15, FontWeights.SemiBold);
            DrawText(dc, center, "每周", 10, 4, FontWeights.Normal);
        }
    }

    private void DrawRing(DrawingContext dc, Point center, double radius, double stroke, double? value, Brush accent)
    {
        dc.DrawEllipse(null, new Pen(Track, stroke), center, radius, radius);
        if (value is not > 0) return;
        var color = value <= 10 ? (Brush)FindResource("Danger") : value <= 25 ? (Brush)FindResource("Warning") : accent;
        var pen = new Pen(color, stroke) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (value >= 100) { dc.DrawEllipse(null, pen, center, radius, radius); return; }
        var angle = value.Value / 100 * Math.PI * 2;
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(new Point(center.X, center.Y - radius), false, false);
            path.ArcTo(new Point(center.X + Math.Sin(angle) * radius, center.Y - Math.Cos(angle) * radius), new Size(radius, radius), 0, value > 50, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    private void DrawText(DrawingContext dc, Point center, string value, double size, double offset, FontWeight weight)
    {
        var text = new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI, Microsoft YaHei UI"), FontStyles.Normal, weight, FontStretches.Normal), size, Ink, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(text, new Point(center.X - text.Width / 2, center.Y + offset));
    }
}

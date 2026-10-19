using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace CodexQuotaWidget;

internal sealed class ResetCreditBadge : UserControl
{
    private readonly Border border;
    private readonly TextBlock countText;
    private int? availableCount;
    private bool enableAnimations = true;

    public ResetCreditBadge(bool compact = false)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var symbol = new TextBlock
        {
            Text = "↻",
            FontFamily = new FontFamily("Segoe UI Symbol"),
            FontSize = 14,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        var label = new TextBlock
        {
            Text = compact ? "重置" : "重置机会",
            FontSize = 11,
            Margin = new Thickness(0, 0, 5, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        label.SetResourceReference(TextBlock.ForegroundProperty,"Muted");
        countText = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(symbol);
        row.Children.Add(label);
        row.Children.Add(countText);
        border = new Border
        {
            CornerRadius = new CornerRadius(15),
            Padding = new Thickness(10, 5, 10, 5),
            BorderThickness = new Thickness(1),
            Child = row
        };
        border.SetResourceReference(Border.BackgroundProperty,"Surface");
        border.SetResourceReference(Border.BorderBrushProperty, "Accent");
        Content = border;
        VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(this, "可用额度重置次数：--");
        UpdateCount();
    }

    public int? AvailableCount
    {
        get => availableCount;
        set
        {
            if (availableCount == value)
                return;
            var previous = availableCount;
            availableCount = value;
            UpdateCount();
            if (previous is not null)
                AnimationHelper.AnimateBadge(countText, enableAnimations, border);
        }
    }

    public bool EnableAnimations
    {
        get => enableAnimations;
        set => enableAnimations = value;
    }

    private void UpdateCount()
    {
        countText.Text = availableCount?.ToString() ?? "--";
        AutomationProperties.SetName(this, $"可用额度重置次数：{QuotaPresentation.Credits(availableCount)}");
    }
}

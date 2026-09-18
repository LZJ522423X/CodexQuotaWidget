using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace CodexQuotaWidget;
public sealed class Ring : FrameworkElement
{
    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer()=>new System.Windows.Automation.Peers.FrameworkElementAutomationPeer(this);
    public static readonly DependencyProperty ValueProperty=DependencyProperty.Register(nameof(Value),typeof(double?),typeof(Ring),new FrameworkPropertyMetadata(null,FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty AccentProperty=DependencyProperty.Register(nameof(Accent),typeof(Brush),typeof(Ring),new FrameworkPropertyMetadata(Brushes.MediumSpringGreen,FrameworkPropertyMetadataOptions.AffectsRender));
    public double? Value{get=>(double?)GetValue(ValueProperty);set=>SetValue(ValueProperty,value);}
    public Brush Accent{get=>(Brush)GetValue(AccentProperty);set=>SetValue(AccentProperty,value);}
    public static string Health(double? value)=>value is null?"未提供":value<=10?"额度偏低":value<=25?"注意用量":"额度充足";
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var center=new Point(ActualWidth/2,ActualHeight/2);
        double radius=Math.Max(0,Math.Min(ActualWidth,ActualHeight)/2-14),stroke=14;
        var track=(Brush)FindResource("Line");
        dc.DrawEllipse(null,new Pen(track,stroke),center,radius,radius);
        dc.DrawEllipse(null,new Pen(track,1),center,radius-18,radius-18);
        if(Value is {} value && value>0)
        {
            Brush color=value<=10?new SolidColorBrush(Color.FromRgb(255,106,112)):value<=25?new SolidColorBrush(Color.FromRgb(243,200,85)):Accent;
            var geometry=new StreamGeometry();
            using(var c=geometry.Open())
            {
                double angle=Math.Min(value,99.999)/100*Math.PI*2;
                c.BeginFigure(new(center.X,center.Y-radius),false,false);
                c.ArcTo(new(center.X+Math.Sin(angle)*radius,center.Y-Math.Cos(angle)*radius),new(radius,radius),0,value>50,SweepDirection.Clockwise,true,false);
            }
            geometry.Freeze();
            var pen=new Pen(color,stroke){StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round};
            dc.PushOpacity(.10);dc.DrawGeometry(null,new Pen(color,stroke+8),geometry);dc.Pop();
            dc.DrawGeometry(null,pen,geometry);
        }
        Draw(dc,Value is {} n?$"{n:0.#}%":"--",Math.Min(40,ActualWidth*.22),(Brush)FindResource("Ink"),center.Y-36,true);
        Draw(dc,"剩余额度",12,(Brush)FindResource("Muted"),center.Y+15,false);
    }
    void Draw(DrawingContext dc,string value,double size,Brush ink,double y,bool bold)
    {
        var text=new FormattedText(value,CultureInfo.CurrentCulture,FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI, Microsoft YaHei UI"),FontStyles.Normal,bold?FontWeights.Bold:FontWeights.Normal,FontStretches.Normal),size,ink,VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(text,new Point((ActualWidth-text.Width)/2,y));
    }
}

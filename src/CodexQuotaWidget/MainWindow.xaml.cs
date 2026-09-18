using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexQuotaWidget.Core;
using Forms=System.Windows.Forms;

namespace CodexQuotaWidget;
public partial class MainWindow : Window
{
    readonly QuotaMonitor monitor;readonly Settings settings;readonly Action save;readonly Func<Task> exit;
    readonly DispatcherTimer clock=new(){Interval=TimeSpan.FromSeconds(1)};
    readonly DispatcherTimer outside=new(){Interval=TimeSpan.FromMilliseconds(100)};
    bool pinned,dialog,layoutCheck;DateTimeOffset opened;short previousMouse;
    [DllImport("user32.dll")]static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")]static extern bool SetWindowPos(IntPtr h,IntPtr after,int x,int y,int cx,int cy,uint flags);
    [DllImport("user32.dll")]static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")]static extern IntPtr GetForegroundWindow();
    IntPtr initialForeground;
    public MainWindow(QuotaMonitor monitor,Settings settings,Action save,Func<Task> exit)
    {
        InitializeComponent();this.monitor=monitor;this.settings=settings;this.save=save;this.exit=exit;
        clock.Tick+=(_,_)=>RefreshView();outside.Tick+=(_,_)=>CheckOutside();
        IsVisibleChanged+=(_,_)=>{if(IsVisible){clock.Start();outside.Start();}else StopTimers();};
        PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Escape){HidePanel();e.Handled=true;}};
        Closing+=(_,e)=>{if(!Application.Current.Dispatcher.HasShutdownStarted){e.Cancel=true;HidePanel();}};
        RefreshView();
    }
    public void StopTimers(){clock.Stop();outside.Stop();}
    public void TogglePanel(){if(IsVisible)HidePanel();else ShowPanel();}
    public void ShowPanel()
    {
        initialForeground=GetForegroundWindow();opened=DateTimeOffset.UtcNow;previousMouse=GetAsyncKeyState(1);
        RefreshView();Show();
        if(pinned)return;
        var handle=new WindowInteropHelper(this).Handle;var cursor=Forms.Cursor.Position;var screen=Forms.Screen.FromPoint(cursor);var area=screen.WorkingArea;
        double scale=GetDpiForWindow(handle)/96.0;int width=(int)(440*scale),height=Math.Min((int)(610*scale),area.Height-16);
        Width=440;Height=height/scale;
        int x=Math.Clamp(cursor.X-width/2,area.Left+8,Math.Max(area.Left+8,area.Right-width-8));
        int y=area.Top>screen.Bounds.Top?area.Top+8:area.Bottom-height-8;
        if(area.Left>screen.Bounds.Left)x=area.Left+8;
        if(area.Right<screen.Bounds.Right)x=area.Right-width-8;
        SetWindowPos(handle,IntPtr.Zero,x,y,width,height,0x0010|0x0004);
    }
    void CheckOutside()
    {
        if(pinned||dialog||layoutCheck)return;
        var mouse=GetAsyncKeyState(1);var current=GetForegroundWindow();
        bool pressed=(mouse&0x8000)!=0&&(previousMouse&0x8000)==0;previousMouse=mouse;
        if(DateTimeOffset.UtcNow-opened<TimeSpan.FromMilliseconds(350))return;
        if(pressed){var p=Forms.Cursor.Position;var local=PointFromScreen(new(p.X,p.Y));if(local.X<0||local.Y<0||local.X>ActualWidth||local.Y>ActualHeight)HidePanel();}
        else if(current!=initialForeground && current!=new WindowInteropHelper(this).Handle)HidePanel();
    }
    void HidePanel()
    {
        if(pinned && IsVisible){settings.Left=Left;settings.Top=Top;settings.Width=Width;settings.Height=Height;save();}
        Hide();
    }
    public void RefreshView()
    {
        var q=monitor.Snapshot;
        StatusText.Text=monitor.Status switch{MonitorStatus.Live=>monitor.IsStale?"旧数据 · 等待刷新":"已连接",MonitorStatus.Starting=>"正在连接",MonitorStatus.DesktopClosed=>"Codex 未运行 · 保留最后读数",MonitorStatus.NeedsLogin=>"需要重新登录",MonitorStatus.LoggingIn=>"等待浏览器授权",MonitorStatus.Sleeping=>"已暂停",MonitorStatus.Offline=>"网络不可用 · 旧数据",MonitorStatus.ProtocolError=>"接口不兼容 · 旧数据",MonitorStatus.CliMissing=>"未找到有效的官方 Codex CLI",_=>"读取失败 · 等待重试"};
        PlanText.Text=$"小组件独立登录 · {q?.Plan??"未确认套餐"}";
        var five=q?.Windows.FirstOrDefault(w=>w.LimitId=="codex"&&w.WindowDurationMins==300);
        var week=q?.Windows.FirstOrDefault(w=>w.LimitId=="codex"&&w.WindowDurationMins==10080);
        FiveRing.Value=five?.Remaining;WeekRing.Value=week?.Remaining;
        FiveCountdown.Text=Countdown(five?.ResetsAt);WeekCountdown.Text=Countdown(week?.ResetsAt);
        FiveReset.Text=ResetText(five?.ResetsAt);WeekReset.Text=ResetText(week?.ResetsAt);
        ExtraWindows.Children.Clear();
        if(q is not null)foreach(var w in q.Windows.Where(w=>w!=five&&w!=week))
        {
            ExtraWindows.Children.Add(new TextBlock{Text=$"{w.Name} · {(w.Remaining is { } r?$"剩余 {r:0.#}%":"未提供")}",FontWeight=FontWeights.SemiBold,Margin=new(0,0,0,4)});
            var t=new TextBlock{Text=$"{w.WindowDurationMins?.ToString("0.##")??"未知"} 分钟窗口 · {Countdown(w.ResetsAt)}\n{ResetText(w.ResetsAt)}",FontSize=11,Margin=new(0,0,0,12)};t.SetResourceReference(TextBlock.ForegroundProperty,"Muted");ExtraWindows.Children.Add(t);
        }
        CreditCount.Text=q?.AvailableCount?.ToString()??"未提供";
        CreditExpiry.Text=q is null?"等待额度数据":!q.ResetCreditsProvided?"服务端未提供重置次数":q.CreditExpirations.Count==0?"服务端未提供到期时间":string.Join("\n",q.CreditExpirations.Select(t=>"到期："+DateTimeOffset.FromUnixTimeSeconds(t).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz")));
        UpdatedText.Text=q is null?"尚无成功读数":$"{(monitor.IsStale?"旧数据":"更新")}\n{q.UpdatedAt.ToLocalTime():MM-dd HH:mm:ss}";
        ErrorText.Text=monitor.ErrorField is null?"":$"{monitor.CliVersion}\n{monitor.ErrorField}";
        RefreshButton.IsEnabled=!monitor.IsBusy;
    }
    static string Countdown(long? t)
    {
        if(t is null)return "重置时间未提供";var left=DateTimeOffset.FromUnixTimeSeconds(t.Value)-DateTimeOffset.UtcNow;
        if(left<=TimeSpan.Zero)return "等待服务器确认重置";
        return left.TotalDays>=1?$"{left.Days}天 {left.Hours}小时后重置":$"{left.Hours:00}:{left.Minutes:00}:{left.Seconds:00} 后重置";
    }
    static string ResetText(long? t)=>t is null?"":DateTimeOffset.FromUnixTimeSeconds(t.Value).ToLocalTime().ToString("yyyy-MM-dd\nHH:mm:ss zzz");
    void HideClick(object sender,RoutedEventArgs e)=>HidePanel();
    async void RefreshClick(object sender,RoutedEventArgs e)=>await monitor.RefreshAsync();
    void SettingsClick(object sender,RoutedEventArgs e)=>OpenSettings();
    void PinClick(object sender,RoutedEventArgs e)
    {
        pinned=!pinned;WindowStyle=pinned?WindowStyle.SingleBorderWindow:WindowStyle.None;ResizeMode=pinned?ResizeMode.CanResize:ResizeMode.NoResize;ShowInTaskbar=pinned;
        if(pinned)
        {
            Width=settings.Width;Height=settings.Height;
            if(settings.Left is { } x && settings.Top is { } y){Left=x;Top=y;}
            var screen=Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle);double scale=GetDpiForWindow(new WindowInteropHelper(this).Handle)/96.0;
            Left=Math.Clamp(Left,screen.WorkingArea.Left/scale,Math.Max(screen.WorkingArea.Left/scale,screen.WorkingArea.Right/scale-Width));
            Top=Math.Clamp(Top,screen.WorkingArea.Top/scale,Math.Max(screen.WorkingArea.Top/scale,screen.WorkingArea.Bottom/scale-Height));Activate();
        }
        else ShowPanel();
    }
    public void OpenSettings()
    {
        dialog=true;try{var window=new SettingsWindow(settings,monitor,save);window.ShowDialog();}finally{dialog=false;initialForeground=GetForegroundWindow();}
    }
    public async Task RunLayoutChecks()
    {
        layoutCheck=true;Show();RefreshView();await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
        var reports=new List<object>();var dir=Path.Combine(Paths.Data,"checks");Directory.CreateDirectory(dir);
        foreach(double scale in new[]{1.0,1.5,2.0})
        {
            Width=440;Height=610;UpdateLayout();
            var bitmap=new RenderTargetBitmap((int)(ActualWidth*scale),(int)(ActualHeight*scale),96*scale,96*scale,PixelFormats.Pbgra32);bitmap.Render(this);
            using(var f=File.Create(Path.Combine(dir,$"layout-{scale*100:0}.png"))){var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));encoder.Save(f);}
            reports.Add(new{Scale=scale,Five=FiveRing.Value,Week=WeekRing.Value,FiveWidth=FiveRing.ActualWidth,WeekWidth=WeekRing.ActualWidth,PanelWidth=ActualWidth,PanelHeight=ActualHeight});
        }
        AtomicStore.Write(Path.Combine(dir,"layout.json"),reports);Hide();
    }
}

using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using CodexQuotaWidget.Core;

namespace CodexQuotaWidget;
public sealed class StatusWindow:Window
{
    readonly QuotaMonitor monitor;
    readonly Action save,appearance;
    readonly TextBlock status=Text("正在连接",12,true),plan=Text("Codex Quota",13),updated=Text("尚未刷新",11);
    readonly TextBlock countdown=Text("--",25),resetTime=Text("等待服务器提供重置时间",12,true);
    readonly TextBlock credits=Text("--",26),creditExpiry=Text("服务端未提供到期时间",11,true);
    readonly TextBlock details=Text("",11,true);
    readonly StackPanel extra=new(),preferences=new(),diagnostics=new();
    readonly Ring primary=new(){Width=182,Height=182},weekly=new(){Width=164,Height=164};
    readonly TextBlock primaryHealth=Text("未提供",11,true),weeklyHealth=Text("未提供",11,true);
    readonly TextBlock primaryReset=Text("--",11,true),weeklyReset=Text("--",11,true);
    readonly DispatcherTimer clock=new(){Interval=TimeSpan.FromSeconds(1)};
    readonly Button refresh=IconButton("\uE72C","手动刷新");
    QuotaSnapshot? displayed;
    bool pinned,positionReady,choosingFile;
    IntPtr foregroundAtShow;
    [DllImport("user32.dll")]static extern IntPtr GetForegroundWindow();
    [DllImport("dwmapi.dll")]static extern int DwmSetWindowAttribute(IntPtr hwnd,int attribute,ref int value,int size);
    public StatusWindow(QuotaMonitor monitor,Func<Task> diagnose,Action save,Action appearance)
    {
        this.monitor=monitor;this.save=save;this.appearance=appearance;
        Title="Codex Quota Widget";Width=Math.Clamp(monitor.Settings.Width,520,760);Height=Math.Clamp(monitor.Settings.Height,740,1000);
        MinWidth=480;MinHeight=580;ShowInTaskbar=false;ShowActivated=false;WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.CanResizeWithGrip;
        SetResourceReference(BackgroundProperty,"Glass");SetResourceReference(ForegroundProperty,"Ink");
        WindowChrome.SetWindowChrome(this,new WindowChrome{CaptionHeight=56,ResizeBorderThickness=new Thickness(5),GlassFrameThickness=new Thickness(-1),UseAeroCaptionButtons=false,CornerRadius=new CornerRadius(8)});
        var border=new Border{BorderThickness=new Thickness(1),Padding=new Thickness(24,18,24,16),CornerRadius=new CornerRadius(8)};
        border.SetResourceReference(Border.BorderBrushProperty,"Line");Content=border;
        var root=new Grid();root.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});root.RowDefinitions.Add(new RowDefinition());root.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});border.Child=root;
        var header=new Grid();header.ColumnDefinitions.Add(new());header.ColumnDefinitions.Add(new(){Width=GridLength.Auto});root.Children.Add(header);
        var title=new StackPanel();title.Children.Add(Text("Codex 用量",23));title.Children.Add(plan);header.Children.Add(title);
        var tools=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Top};Grid.SetColumn(tools,1);header.Children.Add(tools);
        var pin=IconButton("\uE718","固定为独立窗口");var settings=IconButton("\uE713","外观设置");var hide=IconButton("\uE8BB","隐藏到托盘");
        foreach(var b in new[]{pin,settings,hide}){tools.Children.Add(b);WindowChrome.SetIsHitTestVisibleInChrome(b,true);}
        pin.Click+=(_,_)=>{pinned=!pinned;pin.Opacity=pinned?1:.5;pin.ToolTip=pinned?"取消固定，恢复托盘弹窗":"固定为独立窗口";};pin.Opacity=.5;
        hide.Click+=(_,_)=>Hide();settings.Click+=(_,_)=>preferences.Visibility=preferences.IsVisible?Visibility.Collapsed:Visibility.Visible;
        var body=new StackPanel{Margin=new Thickness(0,18,0,14)};var scroll=new ScrollViewer{Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};Grid.SetRow(scroll,1);root.Children.Add(scroll);
        body.Children.Add(status);
        var rings=new Grid{Margin=new Thickness(0,14,0,16)};rings.ColumnDefinitions.Add(new());rings.ColumnDefinitions.Add(new());body.Children.Add(rings);
        rings.Children.Add(QuotaColumn("5 小时",primary,primaryHealth,primaryReset));var weekColumn=QuotaColumn("每周",weekly,weeklyHealth,weeklyReset);Grid.SetColumn(weekColumn,1);rings.Children.Add(weekColumn);
        body.Children.Add(Divider());
        var next=new StackPanel{Margin=new Thickness(0,16,0,16)};next.Children.Add(Text("最近一次额度重置",12,true));next.Children.Add(countdown);next.Children.Add(resetTime);body.Children.Add(next);
        body.Children.Add(extra);body.Children.Add(Divider());
        var creditRow=new Grid{Margin=new Thickness(0,16,0,16)};creditRow.ColumnDefinitions.Add(new());creditRow.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        var creditCopy=new StackPanel();creditCopy.Children.Add(Text("可用额度重置次数",13));creditCopy.Children.Add(creditExpiry);creditRow.Children.Add(creditCopy);Grid.SetColumn(credits,1);creditRow.Children.Add(credits);body.Children.Add(creditRow);
        BuildPreferences();body.Children.Add(preferences);preferences.Visibility=Visibility.Collapsed;
        diagnostics.Children.Add(Divider());
        var closeDiagnostics=IconButton("\uE8BB","收起诊断");closeDiagnostics.HorizontalAlignment=HorizontalAlignment.Right;
        closeDiagnostics.Click+=(_,_)=>diagnostics.Visibility=Visibility.Collapsed;
        diagnostics.Children.Add(closeDiagnostics);diagnostics.Children.Add(details);diagnostics.Visibility=Visibility.Collapsed;body.Children.Add(diagnostics);
        var footer=new Grid();footer.ColumnDefinitions.Add(new());footer.ColumnDefinitions.Add(new(){Width=GridLength.Auto});Grid.SetRow(footer,2);root.Children.Add(footer);
        footer.Children.Add(updated);updated.VerticalAlignment=VerticalAlignment.Center;
        var commands=new StackPanel{Orientation=Orientation.Horizontal};Grid.SetColumn(commands,1);footer.Children.Add(commands);commands.Children.Add(refresh);
        var diagnostic=IconButton("\uE9D9","运行诊断");commands.Children.Add(diagnostic);
        refresh.Click+=async(_,_)=>await monitor.RefreshAsync();diagnostic.Click+=async(_,_)=>await diagnose();
        Closing+=(_,e)=>{if(!Application.Current.Dispatcher.HasShutdownStarted){e.Cancel=true;Hide();}};
        PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Escape){Hide();e.Handled=true;}};
        Deactivated+=(_,_)=>{if(!pinned && !choosingFile && IsVisible)Hide();};
        IsVisibleChanged+=(_,_)=>{if(IsVisible)clock.Start();else{clock.Stop();SavePosition();}};
        clock.Tick+=(_,_)=>{RefreshTimes();var foreground=GetForegroundWindow();if(!pinned && !choosingFile && foreground!=foregroundAtShow && foreground!=new WindowInteropHelper(this).Handle && !IsKeyboardFocusWithin)Hide();};
        SourceInitialized+=(_,_)=>{ApplyMaterial();PositionWindow();};
        RefreshView();
    }
    static TextBlock Text(string text,double size,bool muted=false)
    {
        var block=new TextBlock{Text=text,FontSize=size,FontFamily=new FontFamily("Segoe UI, Microsoft YaHei UI"),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,2,0,2)};
        block.SetResourceReference(TextBlock.ForegroundProperty,muted?"Muted":"Ink");return block;
    }
    static Button IconButton(string glyph,string name)
    {
        var button=new Button{Content=glyph,ToolTip=name,Style=(Style)Application.Current.FindResource("Icon"),Margin=new Thickness(4,0,0,0)};
        System.Windows.Automation.AutomationProperties.SetName(button,name);return button;
    }
    static Border Divider(){var b=new Border{Height=1};b.SetResourceReference(Border.BackgroundProperty,"Line");return b;}
    static StackPanel QuotaColumn(string label,Ring ring,TextBlock health,TextBlock reset)
    {
        var p=new StackPanel{HorizontalAlignment=HorizontalAlignment.Center};var heading=Text(label,15);heading.TextAlignment=TextAlignment.Center;p.Children.Add(heading);
        var frame=new Grid{Height=190,Width=190};frame.Children.Add(ring);ring.VerticalAlignment=VerticalAlignment.Center;ring.HorizontalAlignment=HorizontalAlignment.Center;p.Children.Add(frame);
        health.TextAlignment=TextAlignment.Center;reset.TextAlignment=TextAlignment.Center;p.Children.Add(health);p.Children.Add(reset);return p;
    }
    void BuildPreferences()
    {
        preferences.Children.Add(Divider());preferences.Children.Add(Text("外观",13));
        var row=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,6,0,10)};preferences.Children.Add(row);
        var themes=new ComboBox{Width=124,Height=32,ItemsSource=new[]{"深色","浅色","跟随系统"},SelectedIndex=monitor.Settings.Theme=="Light"?1:monitor.Settings.Theme=="System"?2:0};row.Children.Add(themes);
        System.Windows.Automation.AutomationProperties.SetName(themes,"主题");
        themes.SelectionChanged+=(_,_)=>{monitor.Settings.Theme=themes.SelectedIndex==1?"Light":themes.SelectedIndex==2?"System":"Dark";appearance();ApplyMaterial();save();};
        foreach(var (id,color,label) in new[]{("Mint","#68E7AA","薄荷绿"),("Blue","#74BAFF","晴空蓝"),("Violet","#BAA0FF","紫罗兰")})
        {
            var swatch=new Button{Width=32,Height=32,MinWidth=32,Padding=new Thickness(5),Margin=new Thickness(8,0,0,0),ToolTip=label,Content=new System.Windows.Shapes.Ellipse{Width=16,Height=16,Fill=new SolidColorBrush((Color)ColorConverter.ConvertFromString(color))}};
            System.Windows.Automation.AutomationProperties.SetName(swatch,label);
            swatch.Click+=(_,_)=>{monitor.Settings.Accent=id;appearance();save();RefreshView();};row.Children.Add(swatch);
        }
        var locate=new Button{Content="定位官方 codex.exe",HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,0,0,12)};
        locate.Click+=(_,_)=>{choosingFile=true;try{var picker=new Microsoft.Win32.OpenFileDialog{Filter="Codex CLI|codex.exe",Title="选择官方 Codex CLI"};if(picker.ShowDialog(this)==true){monitor.Settings.ManualCli=picker.FileName;save();monitor.SettingsChanged();}}finally{choosingFile=false;}};preferences.Children.Add(locate);
        var login=new Button{Content="登录 / 重新绑定独立账户",HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,0,0,12)};
        login.Click+=async(_,_)=>
        {
            if(MessageBox.Show(this,"将通过官方浏览器登录为本小组件创建独立认证，不会读取或修改 Codex Desktop 的认证。继续吗？","Codex Quota Widget",MessageBoxButton.OKCancel,MessageBoxImage.Information)!=MessageBoxResult.OK)return;
            login.IsEnabled=false;pinned=true;
            try{await monitor.LoginAsync();}
            finally{pinned=false;login.IsEnabled=true;RefreshView();}
        };
        preferences.Children.Add(login);
    }
    public void ApplyMaterial()
    {
        var handle=new WindowInteropHelper(this).Handle;if(handle==IntPtr.Zero)return;
        int backdrop=3,dark=((App)Application.Current).IsDark?1:0,rounded=2;
        DwmSetWindowAttribute(handle,38,ref backdrop,4);DwmSetWindowAttribute(handle,20,ref dark,4);DwmSetWindowAttribute(handle,33,ref rounded,4);
        primary.InvalidateVisual();weekly.InvalidateVisual();
    }
    void PositionWindow()
    {
        var area=SystemParameters.WorkArea;
        Left=Math.Clamp(monitor.Settings.Left??area.Right-Width-14,area.Left,Math.Max(area.Left,area.Right-Width));
        Top=Math.Clamp(monitor.Settings.Top??area.Bottom-Height-14,area.Top,Math.Max(area.Top,area.Bottom-Height));positionReady=true;
    }
    public void SavePosition()
    {
        if(!positionReady)return;var s=monitor.Settings;
        if(s.Left==Left && s.Top==Top && s.Width==Width && s.Height==Height)return;
        s.Left=Left;s.Top=Top;s.Width=Width;s.Height=Height;save();
    }
    public void ShowPanel(){foregroundAtShow=GetForegroundWindow();RefreshView();Show();ApplyMaterial();}
    public void TogglePanel(){if(IsVisible)Hide();else ShowPanel();}
    public void ShowDiagnostics(){diagnostics.Visibility=Visibility.Visible;ShowPanel();}
    public void StopTimers(){clock.Stop();SavePosition();}
    public void RefreshView()
    {
        status.Text=monitor.Status switch{
            MonitorStatus.Live=>monitor.IsStale?"数据已过期":"●  Codex 运行中 · 数据已更新",
            MonitorStatus.DesktopClosed=>"○  Codex 未运行 · 数据已过期",MonitorStatus.Starting=>"正在连接 Codex",
            MonitorStatus.NeedsLogin=>"!  认证失败或未登录 · 数据已过期",MonitorStatus.Offline=>"!  网络不可用 · 数据已过期",
            MonitorStatus.Sleeping=>"已暂停 · 数据已过期",MonitorStatus.ProtocolError=>"!  接口不兼容 · 数据已过期",
            MonitorStatus.CliMissing=>"!  未找到官方 CLI，请在设置中定位",MonitorStatus.Stopped=>"已停止",_=>"!  读取失败，等待重试 · 数据已过期"};
        status.SetResourceReference(TextBlock.ForegroundProperty,monitor.Status==MonitorStatus.Live&&!monitor.IsStale?"Accent":"Muted");
        var q=monitor.Snapshot;plan.Text=q is null?"独立账户 · 等待额度":"ChatGPT "+q.Plan+" · 独立账户";
        var first=q?.Windows.Find(w=>w.LimitId=="codex"&&w.WindowDurationMins==300);var week=q?.Windows.Find(w=>w.LimitId=="codex"&&w.WindowDurationMins==10080);
        primary.Value=first?.Remaining;weekly.Value=week?.Remaining;primary.SetResourceReference(Ring.AccentProperty,"Accent");weekly.SetResourceReference(Ring.AccentProperty,"Accent");
        System.Windows.Automation.AutomationProperties.SetName(primary,$"5 小时剩余 {first?.Remaining?.ToString("0.#")??"未提供"}%");
        System.Windows.Automation.AutomationProperties.SetName(weekly,$"每周剩余 {week?.Remaining?.ToString("0.#")??"未提供"}%");
        primaryHealth.Text=Ring.Health(first?.Remaining);weeklyHealth.Text=Ring.Health(week?.Remaining);
        primaryReset.Text=ShortReset(first?.ResetsAt);weeklyReset.Text=ShortReset(week?.ResetsAt);
        primary.ToolTip=first?.ResetsAt is {} f?HeadlessCommand.Local(f):"服务端未提供";weekly.ToolTip=week?.ResetsAt is {} w?HeadlessCommand.Local(w):"服务端未提供";
        credits.Text=q?.AvailableCount is {} n?$"{n} 次":"未提供";
        creditExpiry.Text=q?.CreditExpirations.Count>0?string.Join("\n",q.CreditExpirations.Select(t=>"到期 "+HeadlessCommand.Local(t))):"服务端未提供到期时间";
        if(!ReferenceEquals(displayed,q))
        {
            displayed=q;extra.Children.Clear();
            foreach(var item in q?.Windows.Where(x=>x!=first&&x!=week)??Enumerable.Empty<QuotaWindow>())
            {
                var row=new StackPanel{Margin=new Thickness(0,0,0,14)};
                var label=item.LimitId=="base_model_inference"||item.Name.Contains("reserve",StringComparison.OrdinalIgnoreCase)?"GPT reserve":item.Name+" · "+item.Slot;
                row.Children.Add(Text(label+"     "+(item.Remaining is {} r?$"{r:0.#}% 剩余":"未提供"),14));
                row.Children.Add(Text(item.ResetsAt is {} t?"重置 "+HeadlessCommand.Local(t):"服务端未提供重置时间",11,true));extra.Children.Add(row);
            }
        }
        refresh.IsEnabled=!monitor.IsBusy;refresh.Opacity=monitor.IsBusy?.5:1;
        details.Text=$"CLI  {monitor.CliVersion}\n{monitor.CliPath}\nCODEX_HOME  {Paths.Home}\nApp Server  {monitor.ServerVersion}\nPID  {monitor.ServerPid?.ToString()??"无"} · 成功 {monitor.SuccessCount} · 请求 {monitor.AttemptCount}\n手动 {monitor.ManualRefreshCount} · 自动 {monitor.AutomaticRefreshCount}\n退出码 {monitor.LastServerExitCode?.ToString()??"待退出"} · 残留 {monitor.LastResidualProcesses?.ToString()??"待检查"}";
        RefreshTimes();
    }
    static string ShortReset(long? time)=>time is {} t?DateTimeOffset.FromUnixTimeSeconds(t).ToLocalTime().ToString("MM-dd HH:mm 重置"):"重置时间未提供";
    void RefreshTimes()
    {
        var q=monitor.Snapshot;updated.Text=q is null?"尚无成功读数":$"{(monitor.IsStale?"旧数据":"已更新")} {q.UpdatedAt.ToLocalTime():HH:mm:ss} · {monitor.Settings.RefreshSeconds} 秒刷新";
        var next=q?.Windows.Where(w=>w.ResetsAt.HasValue).Select(w=>w.ResetsAt!.Value).Order().FirstOrDefault();
        if(next is not >0){countdown.Text="--";return;}
        var span=DateTimeOffset.FromUnixTimeSeconds(next.Value)-DateTimeOffset.UtcNow;
        countdown.Text=span<=TimeSpan.Zero?"等待额度刷新":$"{(span.Days>0?$"{span.Days} 天 ":"")}{span.Hours:00}:{span.Minutes:00}:{span.Seconds:00}";
        resetTime.Text=HeadlessCommand.Local(next.Value);
    }
}

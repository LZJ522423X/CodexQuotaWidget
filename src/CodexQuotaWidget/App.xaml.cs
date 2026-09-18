using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using System.Security.Principal;
using CodexQuotaWidget.Core;
using Microsoft.Win32;
using Forms=System.Windows.Forms;

namespace CodexQuotaWidget;
public partial class App : Application
{
    Mutex? mutex;
    EventWaitHandle? openEvent,quitEvent;
    RegisteredWaitHandle? openWait,quitWait;
    Forms.NotifyIcon? tray;
    System.Drawing.Icon? quotaIcon;
    string iconKey="";
    StatusWindow? view;
    QuotaMonitor? monitor;
    Settings? settings;
    bool exiting;
    public static string InstanceName=>@"Local\CodexQuotaWidget."+WindowsIdentity.GetCurrent().User!.Value;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        mutex=new Mutex(true,InstanceName,out bool first);
        if(!first)
        {
            try{using var signal=EventWaitHandle.OpenExisting(InstanceName+(Array.IndexOf(e.Args,"--quit")>=0?".quit":".open"));signal.Set();}catch(WaitHandleCannotBeOpenedException){}
            Shutdown();return;
        }
        if(Array.IndexOf(e.Args,"--quit")>=0){Shutdown();return;}
        try
        {
            settings=AtomicStore.Read<Settings>(Path.Combine(Paths.Data,"settings.json"))??new();settings.Validate();
            UpdateTheme();SystemEvents.UserPreferenceChanged+=ThemeChanged;
            monitor=new QuotaMonitor(settings);view=new StatusWindow(monitor,RunDiagnostics,()=>SaveSettings(false),UpdateTheme);MainWindow=view;
            tray=new Forms.NotifyIcon{Icon=System.Drawing.SystemIcons.Information,Text="Codex Quota Widget",Visible=true};
            var menu=new Forms.ContextMenuStrip();
            menu.Items.Add("打开/显示窗口",null,(_,_)=>Dispatcher.Invoke(()=>view.ShowPanel()));
            menu.Items.Add("手动刷新",null,async(_,_)=>await Dispatcher.InvokeAsync(monitor.RefreshAsync).Task.Unwrap());
            menu.Items.Add("运行诊断",null,async(_,_)=>await Dispatcher.InvokeAsync(RunDiagnostics).Task.Unwrap());
            menu.Items.Add(new Forms.ToolStripSeparator());menu.Items.Add("退出",null,async(_,_)=>await Dispatcher.InvokeAsync(ExitAsync).Task.Unwrap());tray.ContextMenuStrip=menu;
            tray.MouseClick+=(_,a)=>{if(a.Button==Forms.MouseButtons.Left)Dispatcher.Invoke(()=>view.TogglePanel());};
            monitor.Changed+=OnMonitorChanged;
            openEvent=new(false,EventResetMode.AutoReset,InstanceName+".open");quitEvent=new(false,EventResetMode.AutoReset,InstanceName+".quit");
            openWait=ThreadPool.RegisterWaitForSingleObject(openEvent,(_,_)=>Dispatcher.BeginInvoke(()=>view.ShowPanel()),null,Timeout.Infinite,false);
            quitWait=ThreadPool.RegisterWaitForSingleObject(quitEvent,(_,_)=>Dispatcher.BeginInvoke(async()=>await ExitAsync()),null,Timeout.Infinite,false);
            SystemEvents.PowerModeChanged+=PowerChanged;
            SafeLog.Write(LogEvent.Started);
            monitor.Start();
            if(Array.IndexOf(e.Args,"--show")>=0)view.ShowPanel();
            int soak=Array.IndexOf(e.Args,"--soak-minutes");
            if(soak>=0 && soak+1<e.Args.Length && int.TryParse(e.Args[soak+1],out int minutes))
                _=Soak.Run(monitor,Math.Clamp(minutes,1,180),ExitAsync);
        }
        catch(Exception e2)
        {
            SafeLog.Write(LogEvent.ProtocolFailure,e2.HResult);
            MessageBox.Show("启动未完成。请查看脱敏日志中的错误编号。", "Codex Quota Widget",MessageBoxButton.OK,MessageBoxImage.Error);
            await ExitAsync();
        }
    }
    string MakeTrayText()
    {
        var w=monitor?.Snapshot?.Windows.Find(w=>w.LimitId=="codex"&&w.WindowDurationMins==300);
        return w?.Remaining is { } n ? $"Codex 5小时剩余 {n:0.#}%" : "Codex Quota Widget";
    }
    void OnMonitorChanged()=>Dispatcher.BeginInvoke(()=>{if(!exiting){if(view?.IsVisible==true)view.RefreshView();UpdateTray();}});
    void UpdateTray()
    {
        if(tray is null || monitor is null)return;
        var value=monitor.Snapshot?.Windows.Find(w=>w.LimitId=="codex"&&w.WindowDurationMins==300)?.Remaining;
        var week=monitor.Snapshot?.Windows.Find(w=>w.LimitId=="codex"&&w.WindowDurationMins==10080)?.Remaining;
        tray.Text=$"Codex · 5h {value?.ToString("0.#")??"--"}% · 每周 {week?.ToString("0.#")??"--"}%"+(monitor.IsStale?" · 旧数据":"");
        var key=$"{value:0}:{monitor.IsStale}:{settings?.Accent}";
        if(key==iconKey)return;
        iconKey=key;var old=quotaIcon;quotaIcon=TrayGlyph.Create(value,monitor.IsStale);tray.Icon=quotaIcon;old?.Dispose();
    }
    async Task RunDiagnostics()
    {
        if(monitor is null||exiting)return;
        await monitor.RefreshAsync();view?.ShowDiagnostics();
        try{AtomicStore.Write(Path.Combine(Paths.Data,"checks","diagnostic.json"),MonitorDiagnostics.Snapshot(monitor));}
        catch(Exception e)when(e is IOException or UnauthorizedAccessException){SafeLog.Write(LogEvent.StorageFailure);}
    }
    public void SaveSettings(bool refresh=true)
    {
        if(settings is null)return;
        try{AtomicStore.Write(Path.Combine(Paths.Data,"settings.json"),settings);if(refresh)monitor?.SettingsChanged();}
        catch(Exception e)when(e is IOException or UnauthorizedAccessException){SafeLog.Write(LogEvent.StorageFailure);MessageBox.Show("设置保存失败，原有文件已保留。","Codex Quota Widget");}
    }
    void ThemeChanged(object sender,UserPreferenceChangedEventArgs e)=>Dispatcher.BeginInvoke(UpdateTheme);
    public bool IsDark {get;private set;}=true;
    void UpdateTheme()
    {
        bool dark=settings?.Theme=="Dark" || settings?.Theme=="System" && (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize","AppsUseLightTheme",1) as int?)==0;
        IsDark=dark;
        foreach(var (key,color) in new[]{("Glass",dark?"#F210151A":"#F2F7F9FB"),("Surface",dark?"#20262D":"#FFFFFF"),("Ink",dark?"#F2F5FA":"#161C25"),("Muted",dark?"#A1AAB8":"#526174"),("Line",dark?"#303B47":"#D8DFE6"),("Hover",dark?"#35414D":"#E3EAF0"),("Accent",settings?.Accent=="Blue"?(dark?"#74BAFF":"#1365B5"):settings?.Accent=="Violet"?(dark?"#BAA0FF":"#7141C4"):(dark?"#68E7AA":"#168451"))})
            Resources[key]=new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        view?.ApplyMaterial();UpdateTray();
    }
    async void PowerChanged(object sender,PowerModeChangedEventArgs e)
    {
        if(monitor is null)return;
        try{await Dispatcher.InvokeAsync(()=>monitor.SetSuspended(e.Mode==PowerModes.Suspend)).Task.Unwrap();}catch(OperationCanceledException){}
    }
    public async Task ExitAsync()
    {
        if(exiting)return;exiting=true;
        SystemEvents.PowerModeChanged-=PowerChanged;SystemEvents.UserPreferenceChanged-=ThemeChanged;
        openWait?.Unregister(null);quitWait?.Unregister(null);openEvent?.Dispose();quitEvent?.Dispose();
        view?.StopTimers();
        if(monitor is not null)monitor.Changed-=OnMonitorChanged;
        if(tray is not null){tray.Visible=false;tray.ContextMenuStrip?.Dispose();tray.Dispose();tray=null;}
        quotaIcon?.Dispose();quotaIcon=null;
        try
        {
            if(monitor is not null){await monitor.DisposeAsync();AtomicStore.Write(Path.Combine(Paths.Data,"checks","exit.json"),MonitorDiagnostics.Snapshot(monitor));}
        }
        catch(Exception e){SafeLog.Write(LogEvent.ServerForcedStop,e.HResult);}
        finally{SafeLog.Write(LogEvent.Stopped);Shutdown();}
    }
    protected override void OnExit(ExitEventArgs e){mutex?.Dispose();base.OnExit(e);}
}

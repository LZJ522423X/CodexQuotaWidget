using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using System.Security.Principal;
using CodexQuotaWidget.Core;
using CodexQuotaWidget.Core.ControlCenter;
using CodexQuotaWidget.Core.Automation;
using CodexQuotaWidget.Core.Observation;
using CodexQuotaWidget.Core.Startup;
using CodexQuotaWidget.StartupWatcher;
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
    CapsuleWindow? capsule;
    QuotaMonitor? monitor;
    ControlCenterService? controlCenter;
    readonly DispatcherTimer controlClock=new(){Interval=TimeSpan.FromSeconds(5)};
    QuotaGuardState? lastNotifiedGuard;
    bool exitPreparing;
    bool safeFinishing;
    AutomationRuntime? automation;
    ExhaustionDetector? exhaustion;
    ExhaustionReceipt? pendingQuotaExhaustion;
    ReliabilityRuntime? reliability;
    StartupRecoveryStore? startupRecovery;
    CodexExhaustedEventStore? eventOutput;
    readonly CancellationTokenSource statusLifetime = new();
    Task? statusWork;
    bool statusBusy;
    DateTimeOffset lastStatusAttempt;
    RecoveryJournal? recovery;
    DateTimeOffset lastRecoveryAttempt=DateTimeOffset.MinValue;
    bool recoveryBusy;
    Task<bool>? recoveryWork;
    string recoveryTaskFingerprint="";
    Settings? settings;
    WorkflowLoopRuntime? workflowLoop;
    bool exiting;
    DesktopCloseController? followClose;
    EventWaitHandle? followCloseExitNotice;
    public static string InstanceName=>@"Local\CodexQuotaWidget."+WindowsIdentity.GetCurrent().User!.Value;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if(Array.IndexOf(e.Args,"--siwc-acceptance")>=0)
        {
            try
            {
                ConfigurePreviewTheme(new Settings{Theme="Dark",EnableAnimations=true,FollowCodex=false});
                ShutdownMode=ShutdownMode.OnMainWindowClose;
                MainWindow=new SiwcAcceptanceWindow(); MainWindow.Show();
            }
            catch(Exception error){MessageBox.Show("验收窗口未启动："+error.GetType().Name+"；没有发起登录或模型请求。","AI Control Center");Shutdown(2);}
            return;
        }
        if(Array.IndexOf(e.Args,"--capsule-hover-check")>=0)
        {
            try{Environment.ExitCode=await CapsuleHoverCheckRunner.RunAsync();}
            catch(Exception ex){Console.WriteLine($"FAIL capsule-hover-check: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");Environment.ExitCode=1;}
            Shutdown(Environment.ExitCode);return;
        }
        if(Array.IndexOf(e.Args,"--watcher-lifetime-check")>=0)
        {
            try{Environment.ExitCode=await WatcherLifetimeCheckRunner.RunAsync();}
            catch(Exception ex){Console.WriteLine($"FAIL watcher-lifetime-check: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");Environment.ExitCode=1;}
            Shutdown(Environment.ExitCode);return;
        }
        if(Array.IndexOf(e.Args,"--shell-layout-check")>=0)
        {
            try{Environment.ExitCode=await ShellLayoutCheckRunner.RunAsync();}
            catch(Exception ex){Console.WriteLine($"FAIL shell-layout-check: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");Environment.ExitCode=1;}
            Shutdown(Environment.ExitCode);return;
        }
        if(Array.IndexOf(e.Args,"--skill-update-check")>=0)
        {
            try{Environment.ExitCode=await SkillUpdateCheckRunner.RunAsync();}
            catch(Exception ex){Console.WriteLine($"FAIL skill-update-check: {ex.GetType().Name}: {ex.Message}");Environment.ExitCode=1;}
            Shutdown(Environment.ExitCode);return;
        }
        if(Array.IndexOf(e.Args,"--workflow-live-connection-check")>=0)
        {
            try{Environment.ExitCode=await WorkflowLiveConnectionCheckRunner.RunAsync();}
            catch(Exception ex){Console.WriteLine($"FAIL workflow-live-connection-check: {ex.GetType().Name}: {ex.Message}");Environment.ExitCode=1;}
            Shutdown(Environment.ExitCode);return;
        }
        if(Array.IndexOf(e.Args,"--startup-watcher-native-check")>=0)
        {
            try{Environment.ExitCode=await StartupWatcherNativeCheckRunner.RunAsync(e.Args);}
            catch(Exception ex){Console.WriteLine($"FAIL startup-watcher-native-check: {ex.GetType().Name}: {ex.Message}");Environment.ExitCode=1;}
            Shutdown(Environment.ExitCode);return;
        }
        if(Array.IndexOf(e.Args,"--startup-behavior-check")>=0)
        {
            try{Environment.ExitCode=await StartupBehaviorCheckRunner.RunAsync();}
            catch(Exception ex){Console.WriteLine($"FAIL startup-behavior-check: {ex.GetType().Name}: {ex.Message}");Environment.ExitCode=1;}
            Shutdown(Environment.ExitCode);return;
        }
        if(Array.IndexOf(e.Args,"--capsule-dock-check")>=0)
        {
            try{Environment.ExitCode=await CapsuleDockCheckRunner.RunAsync();}
            catch(Exception ex){Console.WriteLine($"FAIL capsule-dock-check: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");Environment.ExitCode=1;}
            Shutdown(Environment.ExitCode);return;
        }
        if(Array.IndexOf(e.Args,"--capsule-game-check")>=0)
        {
            try{Environment.ExitCode=await CapsuleGameCheckRunner.RunAsync();}
            catch(Exception ex){Console.WriteLine($"FAIL capsule-game-check: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");Environment.ExitCode=1;}
            Shutdown(Environment.ExitCode);return;
        }
        if(Array.IndexOf(e.Args,"--capsule-foreground-check")>=0)
        {
            try{Environment.ExitCode=await CapsuleForegroundCheckRunner.RunAsync();}
            catch(Exception ex){Console.WriteLine($"FAIL capsule-foreground-check: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");Environment.ExitCode=1;}
            Shutdown(Environment.ExitCode);return;
        }
        if(Array.IndexOf(e.Args,"--capsule-interaction-check")>=0)
        {
            try{Environment.ExitCode=await CapsuleInteractionCheckRunner.RunAsync();}
            catch(Exception ex){Console.WriteLine($"FAIL capsule-interaction-check: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");Environment.ExitCode=1;}
            Shutdown(Environment.ExitCode);return;
        }
        if(Array.IndexOf(e.Args,"--workflow-check")>=0)
        {
            try{Environment.ExitCode=await WorkflowLoopCheckRunner.RunAsync();}
            catch(Exception ex){Console.WriteLine($"FAIL workflow-check: {ex.GetType().Name}: {ex.Message}");Environment.ExitCode=1;}
            Shutdown(Environment.ExitCode);return;
        }
        if(Array.IndexOf(e.Args,"--reliability-check")>=0)
        {
            try{Environment.ExitCode=await ReliabilityCheckRunner.RunAsync();}
            catch(Exception ex){Console.WriteLine($"FAIL reliability-check: {ex.GetType().Name}: {ex.Message}");Environment.ExitCode=1;}
            Shutdown(Environment.ExitCode);return;
        }
        if(Array.IndexOf(e.Args,"--layout-check")>=0)
        {
            try{Environment.ExitCode=await LayoutCheckRunner.RunAsync();}
            catch(Exception ex){Console.WriteLine($"FAIL layout-check: {ex.GetType().Name}");Environment.ExitCode=1;}
            Shutdown(Environment.ExitCode);return;
        }
        if(Array.IndexOf(e.Args,"--animation-check")>=0)
        {
            try{Environment.ExitCode=await AnimationCheckRunner.RunAsync();}
            catch(Exception ex){Console.WriteLine($"FAIL animation-check: {ex.GetType().Name}: {ex.Message}");Environment.ExitCode=1;}
            Shutdown(Environment.ExitCode);return;
        }
        if(Array.IndexOf(e.Args,"--control-center-check")>=0)
        {
            try{Environment.ExitCode=await ControlCenterCheckRunner.RunAsync();}
            catch(Exception ex){Console.WriteLine($"FAIL control-center-check: {ex.GetType().Name}\n{ex.StackTrace}");Environment.ExitCode=1;}
            Shutdown(Environment.ExitCode);return;
        }
        if(Array.IndexOf(e.Args,"--automation-check")>=0)
        {
            try{Environment.ExitCode=await AutomationCheckRunner.RunAsync();}
            catch(Exception ex){Console.WriteLine($"FAIL automation-check: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");Environment.ExitCode=1;}
            Shutdown(Environment.ExitCode);return;
        }
        var smoke=Array.IndexOf(e.Args,"--startup-smoke-check")>=0;
        if(smoke && (Array.IndexOf(e.Args,"--workflow-start")>=0 || Array.IndexOf(e.Args,"--quit")>=0
            || Environment.GetEnvironmentVariable("CODEX_QUOTA_WIDGET_DATA") is null)) { Shutdown(2);return; }
        if(smoke)
        {
            try
            {
                if(!AutomationStore.IsSafeLocalPath(Paths.Data) || File.Exists(Paths.Data)
                    || Directory.Exists(Paths.Data) && Directory.EnumerateFileSystemEntries(Paths.Data).Any())
                { Shutdown(2);return; }
            }
            catch(Exception error) when(error is IOException or UnauthorizedAccessException or ArgumentException)
            { Shutdown(2);return; }
        }
        mutex=new Mutex(true,smoke?InstanceName+".smoke."+Environment.ProcessId:InstanceName,out bool first);
        if(!first)
        {
            if(Array.IndexOf(e.Args,"--startup")<0 || Array.IndexOf(e.Args,"--quit")>=0)
                try{using var signal=EventWaitHandle.OpenExisting(InstanceName+(Array.IndexOf(e.Args,"--quit")>=0?".quit":".open"));signal.Set();}catch(WaitHandleCannotBeOpenedException){}
            Shutdown();return;
        }
        if(Array.IndexOf(e.Args,"--quit")>=0){Shutdown();return;}
        try
        {
            settings=AtomicStore.Read<Settings>(Path.Combine(Paths.Data,"settings.json"))??new();settings.Validate();
            try{if(!smoke)StartupIntegration.Reconcile(settings);}
            catch(Exception error)when(error is IOException or UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException or System.ComponentModel.Win32Exception or System.Security.SecurityException)
            {SafeLog.Write(LogEvent.StorageFailure);}
            UpdateTheme();SystemEvents.UserPreferenceChanged+=ThemeChanged;
            monitor=new QuotaMonitor(settings);controlCenter=new ControlCenterService();
            var project=settings.SafeFinishProjectPath ?? "";
            if(AutomationStore.IsSafeDPath(Paths.Data))
            {
            startupRecovery=new StartupRecoveryStore(Paths.Data);
            startupRecovery.BeginSession(new ControlCenterSnapshot());

            automation=new AutomationRuntime(project);
            reliability=new ReliabilityRuntime(project,startupRecovery,monitor);
            reliability.BindingChanged+=()=>pendingQuotaExhaustion=null;
            eventOutput=new CodexExhaustedEventStore(Paths.Data);
            if(startupRecovery.GetState().StorageBlocked)automation.DisableForStorageError();
            try{exhaustion=new ExhaustionDetector(automation.Store.LoadHandledCycles());}
            catch(Exception error)when(error is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {automation.DisableForStorageError();SafeLog.Write(LogEvent.StorageFailure);}
            if(Directory.Exists(project) && !startupRecovery.GetState().StorageBlocked)
            {
                try{recovery=new RecoveryJournal(project,Paths.Data);}
                catch(Exception error)when(error is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException)
                {SafeLog.Write(LogEvent.StorageFailure);}
            }
            }
            SyncControlQuota();
            workflowLoop=new WorkflowLoopRuntime(Paths.Data);
            view=new StatusWindow(monitor,RunDiagnostics,()=>SaveSettings(false),UpdateTheme,SwitchWindowMode,controlCenter,automation,reliability,workflowLoop);
            capsule=new CapsuleWindow(monitor,OpenFullFromCapsule,()=>_ = monitor.RefreshAsync(),controlCenter,workflowLoop,()=>SaveSettings(false));MainWindow=view;
            workflowLoop.Changed+=OnWorkflowChanged;
            tray=new Forms.NotifyIcon{Icon=System.Drawing.SystemIcons.Information,Text="Codex AI 控制中心",Visible=true};
            var menu=new Forms.ContextMenuStrip();
            menu.Items.Add("打开/显示窗口",null,(_,_)=>Dispatcher.Invoke(ShowCurrentPresentation));
            menu.Items.Add("切换完整/胶囊模式",null,(_,_)=>Dispatcher.Invoke(ToggleWindowMode));
            menu.Items.Add("循环任务",null,(_,_)=>Dispatcher.Invoke(()=>{capsule?.HidePanel();view?.ShowWorkflow();}));
            menu.Items.Add("Skills 更新",null,(_,_)=>Dispatcher.Invoke(()=>{capsule?.HidePanel();view?.ShowSkills();}));
            menu.Items.Add("设置",null,(_,_)=>Dispatcher.Invoke(()=>{capsule?.HidePanel();view?.ShowSettings();}));
            var advancedMenu=new Forms.ToolStripMenuItem("更多");menu.Items.Add(advancedMenu);
            advancedMenu.DropDownItems.Add("状态诊断与 Session 绑定",null,(_,_)=>Dispatcher.Invoke(()=>{capsule?.HidePanel();view?.ShowStatusDiagnostics();}));
            advancedMenu.DropDownItems.Add("暂停本地任务清单",null,(_,_)=>Dispatcher.Invoke(()=>controlCenter.SetDispatchPaused(true)));
            advancedMenu.DropDownItems.Add("使用一次重置机会...",null,(_,_)=>Dispatcher.Invoke(()=>{view?.ShowPanel();view?.BeginResetCreditUse();}));
            advancedMenu.DropDownItems.Add("手动刷新额度",null,async(_,_)=>await Dispatcher.InvokeAsync(monitor.RefreshAsync).Task.Unwrap());
            advancedMenu.DropDownItems.Add("运行诊断",null,async(_,_)=>await Dispatcher.InvokeAsync(RunDiagnostics).Task.Unwrap());
            menu.Items.Add(new Forms.ToolStripSeparator());menu.Items.Add("安全退出",null,async(_,_)=>await Dispatcher.InvokeAsync(ExitAsync).Task.Unwrap());tray.ContextMenuStrip=menu;
            tray.MouseClick+=(_,a)=>{if(a.Button==Forms.MouseButtons.Left)Dispatcher.Invoke(ToggleCurrentPresentation);};
            monitor.Changed+=OnMonitorChanged;
            controlCenter.Changed+=OnControlChanged;
            followClose=new DesktopCloseController(()=>settings.CloseWithCodex && !exiting,
                ()=>Task.Run(DesktopIdentity.ReadPresence),()=>ExitCoreAsync(CanFollowCodexExit));
            controlClock.Tick+=(_,_)=>{if(!exiting){SyncControlQuota();RefreshForAutomation();_=UpdateRecoveryAsync();_=UpdateCodexStatusAsync();_=followClose.PollAsync();}};controlClock.Start();
            var signalName=smoke?InstanceName+".smoke."+Environment.ProcessId:InstanceName;
            openEvent=new(false,EventResetMode.AutoReset,signalName+".open");quitEvent=new(false,EventResetMode.AutoReset,signalName+".quit");
            openWait=ThreadPool.RegisterWaitForSingleObject(openEvent,(_,_)=>Dispatcher.BeginInvoke(ShowCurrentPresentation),null,Timeout.Infinite,false);
            quitWait=ThreadPool.RegisterWaitForSingleObject(quitEvent,(_,_)=>Dispatcher.BeginInvoke(async()=>await ExitAsync()),null,Timeout.Infinite,false);
            SystemEvents.PowerModeChanged+=PowerChanged;
            SafeLog.Write(LogEvent.Started);
            if(smoke)
            {
                view.ShowPanel(); view.UpdateLayout();
                var smokeTimer=new DispatcherTimer { Interval=TimeSpan.FromSeconds(2) };
                smokeTimer.Tick+=async (_,_)=>
                {
                    smokeTimer.Stop();
                    var passed=view.IsLoaded && view.ActualWidth>0 && !workflowLoop.IsRunning
                        && workflowLoop.State.CurrentRequestId==Guid.Empty;
                    AtomicStore.Write(Path.Combine(Paths.Data,"startup-smoke.json"),new {
                        Passed=passed, Version=ReleaseUpdates.CurrentLabel, WindowLoaded=view.IsLoaded,
                        QueueAvailable=controlCenter is not null, WorkflowRunning=workflowLoop.IsRunning,
                        AccountRequests=false, StartupRegistrationChanged=false, PowerActions=false });
                    await ExitAsync();
                    Environment.ExitCode=passed?0:1;
                };
                smokeTimer.Start();return;
            }
            monitor.Start();
            _=followClose.PollAsync();
            _=UpdateCodexStatusAsync();
            if(Array.IndexOf(e.Args,"--workflow-live-inspect")>=0)
            {
                // Explicit local GUI inspection uses the real runtime, never a fake connector.
                // Ordinary tray/capsule behavior and persisted presentation remain unchanged.
                view.ShowInTaskbar=true;view.ShowActivated=true;view.ShowWorkflow();
            }
            else if(startupRecovery?.GetState().NeedsAttention==true)view.ShowReliability();
            else if(workflowLoop.State.NeedsRecoveryConfirmation)view.ShowWorkflow();
            else if(Array.IndexOf(e.Args,"--show")>=0)ShowCurrentPresentation();
            if(Array.IndexOf(e.Args,"--workflow-start")>=0 && Array.IndexOf(e.Args,"--startup")<0)
            {
                // Only an explicit one-time launch starts the saved task; ordinary startup never does.
                view.ShowWorkflow();
                _ = Dispatcher.BeginInvoke(async () =>
                {
                    try { await workflowLoop.StartAsync(workflowLoop.Configuration with { LocalExecutionAuthorized = true }); }
                    catch (Exception error) { SafeLog.Write(LogEvent.ProtocolFailure, error.HResult); }
                });
            }
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
    void OnMonitorChanged()=>Dispatcher.BeginInvoke(()=>{if(!exiting){SyncControlQuota();if(view?.IsVisible==true)view.RefreshView();if(capsule?.IsVisible==true)capsule.RefreshView();UpdateTray();}});
    void SyncControlQuota()
    {
        if(controlCenter is null || monitor is null)return;
        var policy=controlCenter.Snapshot.Policy;
        int maximumAge=Math.Clamp(monitor.Settings.RefreshSeconds*2+15,30,900);
        if(policy.MaximumQuotaAgeSeconds!=maximumAge)controlCenter.UpdatePolicy(policy with{MaximumQuotaAgeSeconds=maximumAge});
        controlCenter.UpdateQuota(monitor.Snapshot,monitor.Status,monitor.IsStale);
        NotifyQuotaProtection();
        TryExhaustionFinish();
    }
    void OnControlChanged()=>Dispatcher.BeginInvoke(()=>
    {
        if(exiting)return;
        if(view?.IsVisible==true)view.RefreshView();if(capsule?.IsVisible==true)capsule.RefreshView();NotifyQuotaProtection();
        var fingerprint=string.Join("|",controlCenter?.Snapshot.Tasks.Select(t=>$"{t.Id}:{t.State}:{t.UpdatedAt:O}") ?? []);
        if(fingerprint!=recoveryTaskFingerprint){recoveryTaskFingerprint=fingerprint;_=UpdateRecoveryAsync(force:true);}
    });
    void OnWorkflowChanged()
    {
        if(exiting || Dispatcher.HasShutdownStarted)return;
        Dispatcher.BeginInvoke(()=>
        {
            if(exiting)return;
            if(view?.IsVisible==true)view.RefreshView();
            if(capsule?.IsVisible==true)capsule.RefreshView();
        });
    }
    void NotifyQuotaProtection()
    {
        if(controlCenter is null || tray is null)return;
        var guard=controlCenter.Snapshot.Guard;
        // Do not repeat notifications on every poll or expose task text in a balloon.
        if(guard.State==lastNotifiedGuard)return;
        if(guard.State==QuotaGuardState.Unknown)return;
        lastNotifiedGuard=guard.State;
        if(guard.Warning)
            tray.ShowBalloonTip(6000,"Codex 额度保护",guard.Reason+"。任务队列已保留。",guard.CanStart?Forms.ToolTipIcon.Warning:Forms.ToolTipIcon.Error);
    }
    void RefreshForAutomation()
    {
        if(automation?.Configuration.Enabled!=true || monitor?.Snapshot is not { } quota || monitor.IsBusy || safeFinishing)return;
        var readings=quota.Windows.Where(w=>w.LimitId.Equals("codex",StringComparison.OrdinalIgnoreCase) && w.WindowDurationMins==300).Select(w=>w.Remaining).ToArray();
        if(readings.Length>0 && readings.Min() is <=1 && DateTimeOffset.UtcNow-quota.UpdatedAt>=TimeSpan.FromSeconds(15))_=monitor.RefreshForSafetyAsync();
    }
    void TryExhaustionFinish()
    {
        if(exiting || exitPreparing || safeFinishing || automation is null || exhaustion is null || controlCenter is null || monitor is null || reliability is null
            || startupRecovery?.GetState().StorageBlocked==true)return;
        var now=DateTimeOffset.UtcNow;
        var candidate=exhaustion.Observe(monitor.Snapshot,monitor.Status,monitor.IsStale,now,controlCenter.Snapshot.Policy.MaximumQuotaAgeSeconds);
        if(candidate is not null){pendingQuotaExhaustion=candidate;_=UpdateCodexStatusAsync(force:true);return;}
        if(pendingQuotaExhaustion is null)return;
        if(!ExhaustionDetector.ValidateReceipt(pendingQuotaExhaustion,now))
        {pendingQuotaExhaustion=null;automation.SetExecutionStatus("耗尽候选已过期；未执行自动收尾");return;}
        if(!ExhaustionDetector.IsValidLiveQuota(monitor.Snapshot,monitor.Status,monitor.IsStale,now,out var remaining,out var reset,
            controlCenter.Snapshot.Policy.MaximumQuotaAgeSeconds))return;
        if(remaining!=0 || reset!=pendingQuotaExhaustion.ResetAt){pendingQuotaExhaustion=null;return;}
        if(!reliability.HasCurrentBoundObservation)return;
        var receipt=CodexExhaustedEventFactory.Confirm(pendingQuotaExhaustion,reliability.Observation,now,
            automation.Configuration.ProjectRoot.Length>0?automation.Configuration.ProjectRoot:settings?.SafeFinishProjectPath ?? "",Paths.Data);
        if(receipt is null)
        {automation.SetExecutionStatus("额度候选已归零；真实任务停止未确认，不执行自动收尾");return;}
        try
        {
            if(eventOutput?.Publish(receipt,now)!=true){pendingQuotaExhaustion=null;return;}
        }
        catch(Exception error)when(error is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
        {automation.DisableForStorageError();pendingQuotaExhaustion=null;SafeLog.Write(LogEvent.StorageFailure);return;}
        pendingQuotaExhaustion=null;
        if(!automation.Configuration.Enabled)
        {automation.SetExecutionStatus("真实耗尽与任务停止事件已输出；自动收尾未开启");return;}
        safeFinishing=true;
        try
        {
            var checkpoint=controlCenter.RequestSafeExit();
            foreach(var task in controlCenter.Snapshot.Tasks)
                if(checkpoint.Success && task.State==WorkItemState.Running)checkpoint=controlCenter.PauseTask(task.Id);
            if(!checkpoint.Success)throw new IOException("任务状态保存失败");
            var id=automation.LaunchConfirmed(receipt);
            tray?.ShowBalloonTip(7000,"额度耗尽：本地收尾已接管","已确认本轮5小时额度耗尽及真实任务停止。独立Executor正在执行已保存操作。",Forms.ToolTipIcon.Info);
            _=ObserveExecutorAsync(Guid.ParseExact(id,"N"));
        }
        catch(Exception e)when(e is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException)
        {
            SafeLog.Write(LogEvent.StorageFailure,e.HResult);
            automation.SetExecutionStatus("独立执行器未启动；该额度周期不会重复自动触发");
            tray?.ShowBalloonTip(8000,"自动收尾未启动","持久保存或启动独立Executor失败；任务保持暂停，未请求关机。该额度周期不会重复自动触发。",Forms.ToolTipIcon.Error);
        }
    }
    Task UpdateCodexStatusAsync(bool force=false)
    {
        if(statusBusy || exiting || exitPreparing || reliability is null
            || (!force && DateTimeOffset.UtcNow-lastStatusAttempt<TimeSpan.FromSeconds(5)))return Task.CompletedTask;
        statusBusy=true;lastStatusAttempt=DateTimeOffset.UtcNow;
        return statusWork=ReadStatusAsync();
    }
    async Task ReadStatusAsync()
    {
        try
        {
            var observation=await reliability!.ReadObservationAsync(statusLifetime.Token);
            if(exiting || exitPreparing || observation is null)return;
            reliability!.Update(observation);
            TryExhaustionFinish();
            if(view?.IsVisible==true)view.RefreshView();
        }
        catch(OperationCanceledException){}
        catch(Exception error)when(error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {reliability?.ReportReadFailure();SafeLog.Write(LogEvent.StorageFailure);}
        finally{statusBusy=false;}
    }
    async Task ObserveExecutorAsync(Guid id)
    {
        var result=await AutomationRuntime.WaitForResultAsync(id);
        automation?.SetExecutionStatus(result is null?"执行结果等待超时；请查看上次执行记录":result.Success?"本地执行计划已完成":result.Message);
        if(!exiting)view?.RefreshView();
        if(exiting || result is null || result.Success)return;
        tray?.ShowBalloonTip(8000,"本地收尾已停止",result.Message+"。请查看设置中的上次执行记录。",Forms.ToolTipIcon.Warning);
    }
    async Task UpdateRecoveryAsync(bool force=false)
    {
        if(recoveryBusy)
        {
            if(!force || recoveryWork is null)return;
            await recoveryWork;
            if(recoveryBusy)return;
        }
        if(recovery is null || controlCenter is null || safeFinishing || startupRecovery?.GetState().StorageBlocked==true
            || (!force && DateTimeOffset.UtcNow-lastRecoveryAttempt<TimeSpan.FromSeconds(30)))return;
        recoveryBusy=true;lastRecoveryAttempt=DateTimeOffset.UtcNow;
        try
        {
            recoveryWork=recovery.RefreshAsync(controlCenter.Snapshot);
            if(!await recoveryWork)SafeLog.Write(LogEvent.StorageFailure);
        }
        catch(Exception e)when(e is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException)
        {SafeLog.Write(LogEvent.StorageFailure,e.HResult);}
        finally{recoveryBusy=false;recoveryWork=null;}
    }
    void ShowCurrentPresentation()
    {
        if(settings?.WindowMode=="Capsule")capsule?.ShowPanel();else view?.ShowPanel();
    }
    void ToggleCurrentPresentation()
    {
        if(view?.IsVisible==true)
        {
            view.HidePanel(()=>{if(settings?.WindowMode=="Capsule")capsule?.ShowPanel();});
            return;
        }
        if(settings?.WindowMode=="Capsule")capsule?.TogglePanel();else view?.TogglePanel();
    }
    void OpenFullFromCapsule()
    {
        capsule?.HidePanel();
        view?.ShowPanel();
    }
    void SwitchWindowMode()
    {
        if(settings?.WindowMode=="Capsule")
            view?.HidePanel(()=>capsule?.ShowPanel());
        else
        {
            capsule?.HidePanel();
            view?.ShowPanel();
        }
    }
    void ToggleWindowMode()
    {
        if(settings is null)return;
        settings.WindowMode=settings.WindowMode=="Capsule"?"Full":"Capsule";
        SaveSettings(false);SwitchWindowMode();
    }
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
        try{AtomicStore.Write(Path.Combine(Paths.Data,"settings.json"),settings);SyncControlQuota();if(refresh)monitor?.SettingsChanged();}
        catch(Exception e)when(e is IOException or UnauthorizedAccessException){SafeLog.Write(LogEvent.StorageFailure);MessageBox.Show("设置保存失败，原有文件已保留。","Codex Quota Widget");}
    }
    void ThemeChanged(object sender,UserPreferenceChangedEventArgs e)=>Dispatcher.BeginInvoke(UpdateTheme);
    public bool IsDark {get;private set;}=true;
    void UpdateTheme()
    {
        bool dark=settings?.Theme=="Dark" || settings?.Theme=="System" && (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize","AppsUseLightTheme",1) as int?)==0;
        IsDark=dark;
        var accent=settings?.Accent=="Blue"?(dark?"#74BAFF":"#1365B5"):settings?.Accent=="Violet"?(dark?"#BAA0FF":"#7141C4"):(dark?"#68E7AA":"#168451");
        var ink=settings?.TextColor switch {"Contrast"=>dark?"#FFFFFF":"#000000","Accent"=>accent,_=>dark?"#F2F5FA":"#161C25"};
        foreach(var (key,color) in new[]{("Glass",dark?"#F210151A":"#F2F7F9FB"),("Surface",dark?"#20262D":"#FFFFFF"),("Ink",ink),("Muted",dark?"#A1AAB8":"#526174"),("Line",dark?"#303B47":"#D8DFE6"),("Hover",dark?"#35414D":"#E3EAF0"),("Accent",accent),("Warning",dark?"#F3C955":"#916800"),("Danger",dark?"#FF6A70":"#C02A3C")})
            Resources[key]=new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        view?.ApplyMaterial();capsule?.ApplyMaterial();UpdateTray();
    }
    internal void ConfigurePreviewTheme(Settings previewSettings)
    {
        settings=previewSettings;
        UpdateTheme();
    }
    async void PowerChanged(object sender,PowerModeChangedEventArgs e)
    {
        if(monitor is null)return;
        try{await Dispatcher.InvokeAsync(()=>monitor.SetSuspended(e.Mode==PowerModes.Suspend)).Task.Unwrap();}catch(OperationCanceledException){}
    }
    public async Task ExitAsync()
        => await ExitCoreAsync();
    bool CanFollowCodexExit()
    {
        if(settings?.CloseWithCodex != true) return false;
        try { return DesktopIdentity.ReadPresence().State == DesktopPresenceState.Absent; }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {return false;}
    }
    async Task<bool> ExitCoreAsync(Func<bool>? mayExit = null)
    {
        if(exiting || exitPreparing || mayExit is not null && !mayExit())return false;exitPreparing=true;
        if(controlCenter is not null && controlCenter.Snapshot.StorageError is null)
        {
            var checkpoint=controlCenter.RequestSafeExit();
            foreach(var task in controlCenter.Snapshot.Tasks)
                if(checkpoint.Success && task.State==WorkItemState.Running)checkpoint=controlCenter.PauseTask(task.Id);
            if(!checkpoint.Success)
            {
                exitPreparing=false;view?.ShowWorkflow();
                MessageBox.Show("任务状态保存失败，尚未退出。请检查数据目录后重试。","AI 控制中心",MessageBoxButton.OK,MessageBoxImage.Warning);
                return false;
            }
        }
        await UpdateRecoveryAsync(force:true);
        if(mayExit is not null)
        {
            try { followCloseExitNotice=StartupWatcherControl.BeginWidgetFollowingClose(); }
            catch(Exception error)when(error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {exitPreparing=false;return false;}
        }
        // Saving can take time. Recheck the option and Desktop identity immediately before committing exit.
        // Automatic follow-close fails closed on unavailable recovery storage; manual exit keeps its original behavior.
        if(mayExit is not null && (!mayExit() || recovery?.LastError is not null
            || startupRecovery?.GetState().StorageBlocked==true || controlCenter?.Snapshot.StorageError is not null))
        {followCloseExitNotice?.Dispose();followCloseExitNotice=null;exitPreparing=false;return false;}
        exiting=true;controlClock.Stop();
        statusLifetime.Cancel();
        if(statusWork is not null)try{await statusWork.WaitAsync(TimeSpan.FromSeconds(5));}catch(OperationCanceledException){}catch(TimeoutException){}
        if(controlCenter is not null)controlCenter.Changed-=OnControlChanged;
        if(workflowLoop is not null)workflowLoop.Changed-=OnWorkflowChanged;
        SystemEvents.PowerModeChanged-=PowerChanged;SystemEvents.UserPreferenceChanged-=ThemeChanged;
        openWait?.Unregister(null);quitWait?.Unregister(null);openEvent?.Dispose();quitEvent?.Dispose();
        view?.StopTimers();capsule?.StopTimers();
        if(monitor is not null)monitor.Changed-=OnMonitorChanged;
        if(tray is not null){tray.Visible=false;tray.ContextMenuStrip?.Dispose();tray.Dispose();tray=null;}
        quotaIcon?.Dispose();quotaIcon=null;
        try
        {
            if(workflowLoop is not null)await workflowLoop.DisposeAsync();
            if(monitor is not null){await monitor.DisposeAsync();AtomicStore.Write(Path.Combine(Paths.Data,"checks","exit.json"),MonitorDiagnostics.Snapshot(monitor));}
            if(recovery?.LastError is null && controlCenter?.Snapshot.StorageError is null)startupRecovery?.MarkCleanExit();
        }
        catch(Exception e){SafeLog.Write(LogEvent.ServerForcedStop,e.HResult);}
        finally{SafeLog.Write(LogEvent.Stopped);Shutdown();}
        return true;
    }
    protected override void OnExit(ExitEventArgs e){mutex?.Dispose();followCloseExitNotice?.Dispose();base.OnExit(e);}
}

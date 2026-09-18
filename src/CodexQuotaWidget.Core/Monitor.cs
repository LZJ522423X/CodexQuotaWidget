using System.Net.NetworkInformation;

namespace CodexQuotaWidget.Core;

public enum MonitorStatus { Starting, Live, DesktopClosed, Offline, NeedsLogin, ProtocolError, CliMissing, Retrying, Sleeping, LoggingIn, Stopped }
public sealed class QuotaMonitor : IAsyncDisposable
{
    readonly SemaphoreSlim gate=new(1,1);
    readonly CancellationTokenSource lifetime=new();
    readonly Func<bool> desktopRunning;
    readonly Func<bool> networkAvailable;
    Task? loop;
    AppServer? server;
    DateTimeOffset nextRefresh=DateTimeOffset.MinValue,lastAttempt=DateTimeOffset.MinValue;
    int failures;
    bool suspended,blocked,wasRunning;
    bool detectedRunning;
    DateTimeOffset lastDesktopCheck=DateTimeOffset.MinValue;
    public long AttemptCount {get;private set;}
    public int? LastServerExitCode {get;private set;}
    public uint? LastResidualProcesses {get;private set;}
    public long ManualRefreshCount {get;private set;}
    public long AutomaticRefreshCount {get;private set;}
    public Settings Settings {get;}
    public QuotaSnapshot? Snapshot {get;private set;}
    public MonitorStatus Status {get;private set;}=MonitorStatus.Starting;
    public string? ErrorField {get;private set;}
    public string CliVersion {get;private set;}="";
    public string CliPath {get;private set;}="";
    public string ServerVersion {get;private set;}="";
    public int? ServerPid=>server?.Pid;
    public long SuccessCount {get;private set;}
    public bool IsBusy {get;private set;}
    public bool IsStale => Snapshot is not null && (Status!=MonitorStatus.Live || DateTimeOffset.UtcNow-Snapshot.UpdatedAt>TimeSpan.FromSeconds(Settings.RefreshSeconds*2+15));
    public event Action? Changed;
    public QuotaMonitor(Settings settings,Func<bool>? detector=null,Func<bool>? network=null)
    {
        Settings=settings;Settings.Validate();desktopRunning=detector??DesktopState.IsRunning;networkAvailable=network??NetworkInterface.GetIsNetworkAvailable;
        Snapshot=AtomicStore.Read<QuotaSnapshot>(Path.Combine(Paths.Data,"cache","last-quota.json"));
    }
    public void Start()=>loop??=RunLoop();
    void Publish(MonitorStatus value){if(Status==value)return;Status=value;Changed?.Invoke();}
    async Task RunLoop()
    {
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            do { await Tick(); } while(await timer.WaitForNextTickAsync(lifetime.Token));
        }
        catch(OperationCanceledException) { }
    }
    async Task Tick()
    {
        if(!await gate.WaitAsync(0)) return;
        try
        {
            if(suspended){await StopServer();Publish(MonitorStatus.Sleeping);return;}
            bool running=!Settings.FollowCodex || CheckDesktop();
            if(!running){wasRunning=false;await StopServer();Publish(MonitorStatus.DesktopClosed);return;}
            if(!wasRunning){nextRefresh=DateTimeOffset.MinValue;wasRunning=true;}
            // A server-provided reset crossed since the last attempt; refresh once, then normal cadence.
            bool resetDue=Snapshot?.Windows.Any(w=>w.ResetsAt is { } t && t>lastAttempt.ToUnixTimeSeconds() && t<=DateTimeOffset.UtcNow.ToUnixTimeSeconds())??false;
            if(!blocked && (DateTimeOffset.UtcNow>=nextRefresh || resetDue)){AutomaticRefreshCount++;await RefreshCore();}
        }
        catch(Exception e) { Handle(e); await StopServer(); }
        finally { gate.Release(); }
    }
    public async Task RefreshAsync()
    {
        if(!await gate.WaitAsync(0))return;
        try
        {
            if(suspended || lifetime.IsCancellationRequested)return;
            if(Settings.FollowCodex && !CheckDesktop(true)){await StopServer();Publish(MonitorStatus.DesktopClosed);return;}
            wasRunning=true;ManualRefreshCount++;
            blocked=false;
            await RefreshCore();
        }
        catch(Exception e){Handle(e);await StopServer();}
        finally{gate.Release();}
    }
    async Task EnsureServer()
    {
        if(server?.Pid is not null)return;
        await StopServer();server=new AppServer(lifetime.Token);await server.StartAsync(Settings.ManualCli);
        CliVersion=server.CliVersion;CliPath=server.CliPath;ServerVersion=server.ServerVersion;
    }
    async Task RefreshCore()
    {
        if(!networkAvailable()){nextRefresh=DateTimeOffset.UtcNow.AddSeconds(60);Publish(MonitorStatus.Offline);return;}
        IsBusy=true;Changed?.Invoke();lastAttempt=DateTimeOffset.UtcNow;
        try
        {
            await EnsureServer();AttemptCount++;var snapshot=await server!.ReadQuota();
            Snapshot=snapshot;SuccessCount++;failures=0;ErrorField=null;
            nextRefresh=DateTimeOffset.UtcNow.AddSeconds(Settings.RefreshSeconds);
            try{AtomicStore.Write(Path.Combine(Paths.Data,"cache","last-quota.json"),snapshot);}
            catch(Exception e)when(e is IOException or UnauthorizedAccessException){SafeLog.Write(LogEvent.StorageFailure);}
            SafeLog.Write(LogEvent.ReadSucceeded);Publish(MonitorStatus.Live);
        }
        finally{IsBusy=false;Changed?.Invoke();}
    }
    void Handle(Exception e)
    {
        if(e is RpcException{Authentication:true}){blocked=true;SafeLog.Write(LogEvent.AuthenticationRequired);Publish(MonitorStatus.NeedsLogin);}
        else if(e is ProtocolException){blocked=true;ErrorField=e.Message;SafeLog.Write(LogEvent.ProtocolFailure);Publish(MonitorStatus.ProtocolError);}
        else if(e is FileNotFoundException){blocked=true;Publish(MonitorStatus.CliMissing);}
        else{failures=Math.Min(failures+1,5);nextRefresh=DateTimeOffset.UtcNow.AddSeconds(Math.Min(600,30*Math.Pow(2,failures)));SafeLog.Write(LogEvent.NetworkFailure,e is RpcException rpc?rpc.Code:0);Publish(MonitorStatus.Retrying);}
    }
    public async Task LoginAsync()
    {
        if(!await gate.WaitAsync(0))return;
        try
        {
            IsBusy=true;Publish(MonitorStatus.LoggingIn);await EnsureServer();SafeLog.Write(LogEvent.LoginStarted);
            // Old-account quota must never be displayed as belonging to a newly bound account.
            Snapshot=null;
            AtomicStore.Write<QuotaSnapshot?>(Path.Combine(Paths.Data,"cache","last-quota.json"),null);
            if(!await server!.LoginAsync()){SafeLog.Write(LogEvent.LoginFailed);blocked=true;Publish(MonitorStatus.NeedsLogin);return;}
            SafeLog.Write(LogEvent.LoginSucceeded);blocked=false;await RefreshCore();
        }
        catch(Exception e){Handle(e);await StopServer();}
        finally{IsBusy=false;Changed?.Invoke();gate.Release();}
    }
    public async Task SetSuspended(bool value)
    {
        suspended=value;
        if(!value)nextRefresh=DateTimeOffset.MinValue;
        await Tick();
    }
    public void SettingsChanged(){blocked=Status==MonitorStatus.NeedsLogin;nextRefresh=DateTimeOffset.MinValue;Changed?.Invoke();}
    bool CheckDesktop(bool force=false)
    {
        if(force || DateTimeOffset.UtcNow-lastDesktopCheck>=TimeSpan.FromSeconds(10))
        {detectedRunning=desktopRunning();lastDesktopCheck=DateTimeOffset.UtcNow;}
        return detectedRunning;
    }
    public async Task RecheckDesktopAsync(){lastDesktopCheck=DateTimeOffset.MinValue;await Tick();}
    async Task StopServer()
    {
        if(server is null)return;
        var stopped=server;server=null;
        await stopped.DisposeAsync();LastServerExitCode=stopped.LastExitCode;LastResidualProcesses=stopped.RemainingOwnedProcesses;
        SafeLog.Write(LogEvent.ServerStopped,LastServerExitCode??-1);
    }
    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();await gate.WaitAsync();
        try{await StopServer();Publish(MonitorStatus.Stopped);}
        finally{gate.Release();}
        if(loop is not null)await loop;
        lifetime.Dispose();
    }
}

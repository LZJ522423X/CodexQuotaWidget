using System.Diagnostics;
using System.Text.Json;
using CodexQuotaWidget.Core;
using CodexQuotaWidget.Core.Startup;

static class FollowCodexCloseChecks
{
    const string Prefix = @"D:\CodexQuotaWidget-GitHub\artifacts\";
    sealed record Lifetime(int Pid, long Ticks);
    sealed record Presence(bool Unknown, Lifetime[] Instances);
    sealed class Clock : TimeProvider
    {
        public long Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Ticks;
        public void At(int seconds) => Ticks = TimeSpan.FromSeconds(seconds).Ticks;
    }
    static string Validate(string path)
    {
        var full=Path.GetFullPath(path);
        if(!full.StartsWith(Prefix,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Only isolated D artifact fixtures allowed.");
        return full;
    }
    static bool Alive(Lifetime item)
    {
        try { using var process=Process.GetProcessById(item.Pid);return !process.HasExited && process.StartTime.ToUniversalTime().Ticks==item.Ticks; }
        catch(ArgumentException){return false;}catch(InvalidOperationException){return false;}
    }
    public static async Task<int> RunChildAsync(string[] args)
    {
        string Value(string key)=>args[Array.IndexOf(args,key)+1];
        var dir=Validate(Value("--fixture-dir"));var role=Value("--fixture-role");
        using var stop=EventWaitHandle.OpenExisting(Value("--fixture-stop"));
        if(role!="widget") { stop.WaitOne(TimeSpan.FromSeconds(90));return 0; }
        using var mutex=new Mutex(true,Value("--fixture-mutex"),out var first);
        if(!first)return 7;
        try
        {
            bool exit=false;
            var settings=AtomicStore.Read<Settings>(Path.Combine(dir,"settings.json"))!;
            var controller=new DesktopCloseController(()=>settings.CloseWithCodex,
                ()=>
                {
                    // The fixture host atomically replaces this file while the child polls it.
                    // Share deletion so a read handle cannot make File.Replace fail nondeterministically.
                    Presence presence;
                    try { using var file=new FileStream(Path.Combine(dir,"presence.json"),FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
                        presence=JsonSerializer.Deserialize<Presence>(file)??new(true,[]); }
                    catch(IOException){presence=new(true,[]);}
                    var count=presence.Instances.Count(Alive);
                    return Task.FromResult(presence.Unknown ? new DesktopPresenceObservation(DesktopPresenceState.Unknown)
                        : new(count>0?DesktopPresenceState.Running:DesktopPresenceState.Absent,count));
                },()=>
                {
                    File.WriteAllText(Path.Combine(dir,"codex_resume_state.md"),"Isolated fixture normal exit; current task saved; no model or user programs touched.");
                    exit=true;return Task.FromResult(true);
                },new DesktopClosePolicy(TimeSpan.FromMilliseconds(180),3));
            File.WriteAllText(Path.Combine(dir,"widget-ready.json"),JsonSerializer.Serialize(new { Pid=Environment.ProcessId }));
            while(!stop.WaitOne(20) && !exit)await controller.PollAsync();
            return 0;
        }
        finally { mutex.ReleaseMutex(); }
    }
    public static async Task<int> RunAsync()
    {
        var output=Validate(Environment.GetEnvironmentVariable("CQW_FOLLOW_CLOSE_CHECK_DIR")
            ?? Path.Combine(Prefix,"follow-close-1.12.6","fixture-"+Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(output);
        var checks=new List<string>();
        void Check(bool condition,string label){if(!condition)throw new InvalidOperationException(label);checks.Add(label);}
        var running=new DesktopPresenceObservation(DesktopPresenceState.Running,1);
        var absent=new DesktopPresenceObservation(DesktopPresenceState.Absent);
        var unknown=new DesktopPresenceObservation(DesktopPresenceState.Unknown);
        var policy=new DesktopClosePolicy();
        Check(!policy.Evaluate(true,absent,TimeSpan.Zero).ShouldClose,"no observed Desktop lifetime cannot cause startup exit");
        policy.Evaluate(true,running,TimeSpan.Zero);
        Check(!policy.Evaluate(true,absent,TimeSpan.FromSeconds(1)).ShouldClose,"first absence does not close");
        Check(!policy.Evaluate(true,absent,TimeSpan.FromSeconds(2)).ShouldClose,"short Desktop process restart does not close");
        policy.Evaluate(true,running,TimeSpan.FromSeconds(3));
        Check(!policy.Evaluate(true,absent,TimeSpan.FromSeconds(16)).ShouldClose,"running again resets earlier absence window");
        policy.Evaluate(true,unknown,TimeSpan.FromSeconds(30));
        Check(!policy.Evaluate(true,absent,TimeSpan.FromSeconds(50)).ShouldClose,"unknown identity interrupts absence confirmation");
        policy.Evaluate(true,absent,TimeSpan.FromSeconds(56));
        Check(policy.Evaluate(true,absent,TimeSpan.FromSeconds(63)).ShouldClose,"12 second grace and three known samples permit close");
        Check(!policy.Evaluate(false,absent,TimeSpan.FromSeconds(100)).ShouldClose,"disabled close never exits");
        Check(!policy.Evaluate(true,absent,TimeSpan.FromSeconds(200)).ShouldClose,"reenabling while Desktop absent requires a new observed lifetime");
        Check(!new DesktopClosePolicy().Evaluate(true,new(DesktopPresenceState.Running,0),TimeSpan.Zero).ShouldClose,"invalid positive evidence remains unknown");
        foreach(var logon in new[]{false,true})foreach(var start in new[]{false,true})foreach(var close in new[]{false,true})
        {
            var settings=new Settings{AutoStart=logon,StartWithCodex=start,CloseWithCodex=close,FollowCodex=false};settings.Validate();
            var path=Path.Combine(output,$"settings-{logon}-{start}-{close}.json");AtomicStore.Write(path,settings);
            var read=AtomicStore.Read<Settings>(path)!;
            Check(read.AutoStart==logon && read.StartWithCodex==start && read.CloseWithCodex==close && !read.FollowCodex,"independent persisted switch combination "+logon+"/"+start+"/"+close);
            var p=new DesktopClosePolicy();p.Evaluate(close,running,TimeSpan.Zero);p.Evaluate(close,absent,TimeSpan.FromSeconds(1));p.Evaluate(close,absent,TimeSpan.FromSeconds(10));
            Check(p.Evaluate(close,absent,TimeSpan.FromSeconds(20)).ShouldClose==close,"only close switch governs exit combination "+logon+"/"+start+"/"+close);
        }
        var legacy=JsonSerializer.Deserialize<Settings>("{\"FollowCodex\":true,\"StartWithCodex\":true,\"AutoStart\":false}")!;
        legacy.Validate();Check(!legacy.CloseWithCodex && legacy.StartWithCodex && legacy.FollowCodex && !legacy.AutoStart,"legacy missing close preference preserves all prior options and stays off");
        var time=new Clock();int calls=0;int attempts=0;bool confirmedRunning=false;bool failSave=false;bool enabled=true;
        var controller=new DesktopCloseController(()=>enabled,()=>Task.FromResult(++calls==1?running:confirmedRunning?running:absent),
            ()=>{attempts++;return Task.FromResult(!failSave);},time:time);
        await controller.PollAsync();time.At(1);await controller.PollAsync();time.At(8);await controller.PollAsync();
        confirmedRunning=true;time.At(16);await controller.PollAsync();
        Check(attempts==0,"a Desktop still running does not reach exit callback");
        confirmedRunning=false;time.At(17);await controller.PollAsync();time.At(25);await controller.PollAsync();
        failSave=true;time.At(30);await controller.PollAsync();
        Check(attempts==1 && controller.LastDecision.Reason=="ExitDeferred","save failure defers normal close");
        failSave=false;time.At(31);await controller.PollAsync();await controller.PollAsync();
        Check(attempts==2 && controller.LastDecision.Reason=="ExitedNormally","successful retry exits once and subsequent polls do not repeat");
        var raceTime=new Clock();int observations=0;int raceExit=0;
        var race=new DesktopCloseController(()=>true,()=>Task.FromResult(++observations==1||observations==5?running:absent),
            ()=>{raceExit++;return Task.FromResult(true);},time:raceTime);
        await race.PollAsync();raceTime.At(1);await race.PollAsync();raceTime.At(8);await race.PollAsync();raceTime.At(16);await race.PollAsync();
        Check(raceExit==0 && race.LastDecision.Reason=="DesktopRunning","second identity confirmation catches a just restarted Desktop");
        var gate=new TaskCompletionSource<DesktopPresenceObservation>(TaskCreationOptions.RunContinuationsAsynchronously);int concurrentCalls=0;
        var concurrent=new DesktopCloseController(()=>true,()=>{concurrentCalls++;return gate.Task;},()=>Task.FromResult(true));
        var waiting=concurrent.PollAsync();await concurrent.PollAsync();
        Check(concurrentCalls==1,"overlapping timer ticks do not duplicate observations or exit");gate.SetResult(running);await waiting;
        enabled=false;await controller.PollAsync();Check(attempts==2,"disabled/completed controller never repeats exit");

        using var host=new FixtureHost(Path.Combine(output,"five-cycles"));
        using var stop=new ManualResetEvent(false);Exception? watcherError=null;
        var thread=new Thread(()=>{try{StartupWatchLoop.Run(host,stop,TimeSpan.FromMilliseconds(25));}catch(Exception e){watcherError=e;}}){IsBackground=true};
        thread.Start();var initialThread=thread.ManagedThreadId;var rounds=new List<object>();
        try
        {
            Wait(()=>host.Polls>0,"watcher begins listening");
            for(var round=1;round<=5;round++)
            {
                using var main=host.Child("desktop");host.SetMain(main);
                Wait(()=>host.LaunchCount==round && host.WidgetIsRunning(),"launch widget round "+round);
                var widget=host.LastWidget!;
                Wait(()=>File.Exists(Path.Combine(host.CurrentDirectory,"widget-ready.json")),"widget ready round "+round);
                Check(host.LastReports.Count(item=>item.Result=="Launched")==round,"one widget launched each Desktop lifetime round "+round);
                Thread.Sleep(300);
                Check(!widget.Process.HasExited,"window/connection state cannot close a running Desktop fixture round "+round);
                if(round==1)
                {
                    using var helper=host.Child("helper");host.SetHelper(helper);main.Stop();Thread.Sleep(350);
                    Check(!widget.Process.HasExited,"verified Desktop helper keeps widget alive after main exits");
                    helper.Stop();host.SetHelper(null);
                }
                else if(round==2)
                {
                    host.SetUnknown(true);main.Stop();Thread.Sleep(350);
                    Check(!widget.Process.HasExited,"unreadable Desktop identity cannot close widget");host.SetUnknown(false);
                }
                else main.Stop();
                Wait(()=>widget.Process.HasExited,"normal follow-close round "+round);
                Check(widget.Process.ExitCode==0 && File.Exists(Path.Combine(host.CurrentDirectory,"codex_resume_state.md")),"normal child exit and D resume file round "+round);
                Check(thread.IsAlive && thread.ManagedThreadId==initialThread,"same watcher continues after root and widget exit round "+round);
                rounds.Add(new { round, mainPid=main.Process.Id,widgetPid=widget.Process.Id,widgetExit=widget.Process.ExitCode,watcherThread=initialThread });
                host.SetMain(null);
            }
            Check(host.LaunchCount==5,"five complete cycles without duplicate widget instances");
            using(var main=host.Child("desktop"))
            {
                host.SetMain(main);Wait(()=>host.LaunchCount==6 && host.WidgetIsRunning(),"manual-close cycle starts");
                host.LastWidget!.Stop();var polls=host.Polls;Wait(()=>host.Polls>=polls+8,"manual-close observation window");
                Check(host.LaunchCount==6 && !host.WidgetIsRunning(),"manually closing widget does not respawn it in same Desktop lifetime");main.Stop();host.SetMain(null);
            }
            using(var oldMain=host.Child("desktop"))
            {
                host.SetMain(oldMain);Wait(()=>host.LaunchCount==7 && host.WidgetIsRunning(),"exit handoff starts");
                Wait(()=>File.Exists(Path.Combine(host.CurrentDirectory,"widget-ready.json")),"exit handoff widget ready");
                var oldWidget=host.LastWidget!;host.ExitPending=true;oldMain.Stop();
                using var nextMain=host.Child("desktop");host.SetMain(nextMain);
                var polls=host.Polls;Wait(()=>host.Polls>=polls+8,"pending normal-exit listener continues");
                Check(thread.IsAlive && host.LaunchCount==7,"watcher stays alive and does not consume new main during normal exit handoff");
                oldWidget.Stop();host.ExitPending=false;
                Wait(()=>host.LaunchCount==8 && host.WidgetIsRunning(),"new main launches widget after old mutex release");
                Check(host.LastWidget!.Process.Id!=oldWidget.Process.Id,"fresh Desktop lifetime is not lost during previous widget teardown");
                Wait(()=>File.Exists(Path.Combine(host.CurrentDirectory,"widget-ready.json")),"replacement widget ready");
                Thread.Sleep(250);nextMain.Stop();Wait(()=>host.LastWidget!.Process.HasExited,"replacement follows close");host.SetMain(null);
            }
            host.CloseEnabled=false;
            using(var main=host.Child("desktop"))
            {
                host.SetMain(main);Wait(()=>host.LaunchCount==9 && host.WidgetIsRunning(),"close-disabled widget starts");
                Wait(()=>File.Exists(Path.Combine(host.CurrentDirectory,"widget-ready.json")),"close-disabled widget ready");
                main.Stop();Thread.Sleep(450);
                Check(!host.LastWidget!.Process.HasExited && thread.IsAlive,"disabled follow-close keeps widget alive after complete Desktop exit");
                host.LastWidget.Stop();host.SetMain(null);
            }
            host.Enabled=false;Wait(()=>!thread.IsAlive,"disable follow-start ends its own watcher normally");
            Check(watcherError is null,"watcher completed without an unhandled failure");
        }
        finally { stop.Set();if(!thread.Join(3000))throw new TimeoutException("Fixture watcher stop timed out."); }
        File.WriteAllText(Path.Combine(output,"check-result.json"),JsonSerializer.Serialize(new
        {
            passed=true,assertions=checks.Count,checks,rounds,
            realChecks=new[]{"shared production startup/close controllers","real isolated process lifetimes and normal exits","native fixture mutex/events","D persistence and recovery callback"},
            simulatedChecks=new[]{"Desktop identity supplied by fixture", "180ms grace instead of production 12s", "manual clock policy edge cases", "fixture recovery callback", "normal-exit pending signal"},
            notVerified=new[]{"actual user Codex full exit/restart", "live GUI ExitCoreAsync against an exited real Desktop", "next Windows login"},
            userProgramsClosed=false,models=false,networkChanges=false,powerActions=false
        },new JsonSerializerOptions{WriteIndented=true}));
        return checks.Count;
    }
    static void Wait(Func<bool> condition,string name)
    {
        var deadline=DateTime.UtcNow.AddSeconds(8);while(!condition() && DateTime.UtcNow<deadline)Thread.Sleep(15);
        if(!condition())throw new TimeoutException(name);
    }
    sealed class Child : IDisposable
    {
        public readonly Process Process;readonly EventWaitHandle stop;
        public Lifetime Identity=>new(Process.Id,Process.StartTime.ToUniversalTime().Ticks);
        public Child(string dir,string role,string mutex)
        {
            var name=@"Local\CQW.CloseFixture.Stop."+Guid.NewGuid().ToString("N");stop=new(false,EventResetMode.ManualReset,name);
            var info=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true};
            if(Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet",StringComparison.OrdinalIgnoreCase))info.ArgumentList.Add(typeof(FollowCodexCloseChecks).Assembly.Location);
            foreach(var arg in new[]{"--follow-close-fixture-child","--fixture-dir",dir,"--fixture-role",role,"--fixture-stop",name,"--fixture-mutex",mutex})info.ArgumentList.Add(arg);
            info.Environment["TEMP"]=Path.Combine(dir,"temp");info.Environment["TMP"]=info.Environment["TEMP"];Directory.CreateDirectory(info.Environment["TEMP"]!);
            Process=Process.Start(info)!;
        }
        public void Stop(){stop.Set();if(!Process.WaitForExit(5000))throw new TimeoutException("Private fixture failed to exit normally.");}
        public void Dispose(){try{Stop();}finally{Process.Dispose();stop.Dispose();}}
    }
    sealed class FixtureHost : IStartupWatchHost,IDisposable
    {
        readonly object sync=new();readonly string dir;readonly string mutex=@"Local\CQW.CloseFixture.Widget."+Guid.NewGuid().ToString("N");
        readonly List<Child> children=[];Child? main,helper;bool unknown;volatile bool enabled=true;volatile bool exitPending;int polls,launches;
        public bool ExitPending{get=>exitPending;set=>exitPending=value;}public bool CloseEnabled{get;set;}=true;
        public bool Enabled{get=>enabled;set=>enabled=value;}public int Polls=>Volatile.Read(ref polls);public int LaunchCount=>Volatile.Read(ref launches);
        public Child? LastWidget{get;private set;}public string CurrentDirectory{get;private set;}="";
        readonly List<StartupWatchReport> reports=[];
        public List<StartupWatchReport> LastReports{get{lock(sync)return reports.ToList();}}
        public FixtureHost(string dir){this.dir=dir;Directory.CreateDirectory(dir);}
        public Child Child(string role){var c=new Child(dir,role,mutex);lock(sync)children.Add(c);return c;}
        public void SetMain(Child? value){lock(sync){main=value;WritePresence();}}
        public void SetHelper(Child? value){lock(sync){helper=value;WritePresence();}}
        public void SetUnknown(bool value){lock(sync){unknown=value;WritePresence();}}
        void WritePresence()
        {
            if(CurrentDirectory.Length==0)return;
            var list=new List<Lifetime>();if(main is not null && !main.Process.HasExited)list.Add(main.Identity);if(helper is not null && !helper.Process.HasExited)list.Add(helper.Identity);
            AtomicStore.Write(Path.Combine(CurrentDirectory,"presence.json"),new Presence(unknown,list.ToArray()));
        }
        public bool IsEnabled()=>enabled;
        public bool WidgetExitInProgress()=>exitPending;
        public IReadOnlyList<DesktopRunIdentity> ObserveMainProcesses()
        {
            lock(sync){WritePresence();return main is not null && !main.Process.HasExited?[new(main.Identity.Pid,main.Identity.Ticks)]:[];}
        }
        public bool WidgetIsRunning()
        {
            try{using var gate=Mutex.OpenExisting(mutex);try{if(!gate.WaitOne(0))return true;gate.ReleaseMutex();return false;}catch(AbandonedMutexException){gate.ReleaseMutex();return false;}}
            catch(WaitHandleCannotBeOpenedException){return false;}
        }
        public int? LaunchWidget()
        {
            lock(sync)
            {
                CurrentDirectory=Path.Combine(dir,"round-"+(launches+1));Directory.CreateDirectory(CurrentDirectory);
                AtomicStore.Write(Path.Combine(CurrentDirectory,"settings.json"),new Settings{CloseWithCodex=CloseEnabled});WritePresence();
                var child=new Child(CurrentDirectory,"widget",mutex);children.Add(child);LastWidget=child;Interlocked.Increment(ref launches);return child.Process.Id;
            }
        }
        public void Report(StartupWatchReport value){lock(sync)reports.Add(value);Interlocked.Increment(ref polls);}
        public void Dispose(){foreach(var child in children)try{child.Dispose();}catch(InvalidOperationException){} }
    }
}

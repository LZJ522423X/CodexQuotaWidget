using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using CodexQuotaWidget.Core.Startup;

/// <summary>
/// Shared production loop, real isolated child processes and native mutexes. Main-process identities
/// are explicitly synthetic inputs; this mode neither authenticates nor launches/closes real Codex.
/// </summary>
static class StartupWatchLoopChecks
{
    const string ArtifactRoot = @"D:\CodexQuotaWidget-GitHub\artifacts\watcher-repeat-1.12.1";

    public static int RunChild(string[] args)
    {
        string Value(string key)
        {
            var index=Array.IndexOf(args,key);
            if(index<0||index+1>=args.Length)throw new ArgumentException(key);
            return args[index+1];
        }
        var statePath=Path.GetFullPath(Value("--fixture-state"));
        if(!statePath.StartsWith(ArtifactRoot+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only isolated D fixture state is allowed.");
        using var stop=EventWaitHandle.OpenExisting(Value("--fixture-stop"));
        Mutex? owned=null;
        var role=Value("--fixture-role");
        try
        {
            if(role=="widget")
            {
                owned=new Mutex(true,Value("--fixture-mutex"),out var created);
                if(!created)return 7;
            }
            File.WriteAllText(statePath,JsonSerializer.Serialize(new{ProcessId=Environment.ProcessId,Role=role,StartedUtc=Process.GetCurrentProcess().StartTime.ToUniversalTime()}));
            if(role=="widget")
            {
                var rootPid=int.Parse(Value("--fixture-root-pid"));
                var ticks=long.Parse(Value("--fixture-root-ticks"));
                while(!stop.WaitOne(20))
                {
                    try
                    {
                        using var root=Process.GetProcessById(rootPid);
                        if(root.HasExited||root.StartTime.ToUniversalTime().Ticks!=ticks)break;
                    }
                    catch(ArgumentException){break;}
                    catch(InvalidOperationException){break;}
                }
            }
            else stop.WaitOne(TimeSpan.FromMinutes(5));
            return 0;
        }
        finally
        {
            if(owned is not null){owned.ReleaseMutex();owned.Dispose();}
        }
    }

    public static int Run()
    {
        var directory=Path.Combine(ArtifactRoot,"fixture-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var checks=new List<string>();
        void Check(bool value,string label){if(!value)throw new InvalidOperationException(label);checks.Add(label);}
        using var host=new FixtureHost(directory);
        using var stop=new ManualResetEvent(false);
        Exception? loopError=null;
        var thread=new Thread(()=>
        {
            try{StartupWatchLoop.Run(host,stop,TimeSpan.FromMilliseconds(25));}
            catch(Exception error){loopError=error;}
        }){IsBackground=true,Name="Isolated shared StartupWatchLoop"};
        var watcherPid=Environment.ProcessId;
        thread.Start();
        try
        {
            WaitFor(()=>host.ReportCount>0,"watcher initial poll");
            Check(host.LaunchCount==0,"no verified main identity does not launch");
            for(var round=1;round<=5;round++)
            {
                using var root=host.StartRoot();
                host.SetRoot(root);
                WaitFor(()=>host.LaunchCount==round&&host.WidgetIsRunning()&&host.Reports.Any(item=>item.Result=="Launched"&&item.Cycle==round),"widget launch round "+round);
                var child=host.LastWidget!;
                Check(!root.Process.HasExited&&!child.Process.HasExited,"round "+round+": real root/widget child processes running");
                Check(host.Reports.Any(item=>item.Result=="Launched"&&item.LaunchedPid==child.Process.Id),"round "+round+": real launch PID reported");
                Check(thread.IsAlive&&Environment.ProcessId==watcherPid,"round "+round+": same watcher loop continues");
                root.Stop();
                WaitFor(()=>child.Process.HasExited&&!host.WidgetIsRunning(),"fixture following root exit round "+round);
                Check(child.Process.ExitCode==0,"round "+round+": fixture follows root exit normally");
                Check(thread.IsAlive,"round "+round+": watcher survives root/widget exit");
                if(round<=2||round==5)
                {
                    host.SetRoot(null);
                    WaitFor(()=>host.Reports.LastOrDefault()?.CodexVerified==false,"absent identity sample");
                }
                // Rounds 3 -> 4 and 4 -> 5 deliberately keep the prior synthetic identity snapshot.
                // A replacement is therefore observed without an absent sample, as polling can miss a gap.
            }
            Check(host.LaunchCount==5,"five root lifetimes produce exactly five widget launches");
            Check(host.Reports.Count(item=>item.Result=="Launched")==5,"no duplicate process launches across five cycles");
            Check(host.Reports.Where(item=>item.Result=="Launched").Select(item=>item.Cycle).SequenceEqual(new long[]{1,2,3,4,5}),"monotonic cycle identities across missed absence samples");

            using(var root=host.StartRoot())
            {
                host.SetRoot(root);
                WaitFor(()=>host.LaunchCount==6&&host.WidgetIsRunning(),"manual-close lifetime launch");
                host.LastWidget!.Stop();
                var before=host.ReportCount;
                WaitFor(()=>host.ReportCount>=before+6,"manual close observation window");
                Check(host.LaunchCount==6&&!host.WidgetIsRunning(),"user-close fixture stays closed during same main lifetime");
                using(var helper=host.StartRoot("helper"))
                {
                    before=host.ReportCount;
                    WaitFor(()=>host.ReportCount>=before+4,"helper process observation window");
                    Check(host.LaunchCount==6,"unrelated helper identity does not become a main-process restart");
                    helper.Stop();
                }
                foreach(var fault in new[]{"registration","observation","mutex","report"})
                {
                    var errors=host.Reports.Count(item=>item.Result=="ObservationUnknown");
                    host.InjectFault(fault);
                    WaitFor(()=>!host.HasFault,"recoverable "+fault+" fault consumed");
                    before=host.ReportCount;
                    WaitFor(()=>host.ReportCount>=before+3,"recoverable fault continuation");
                    Check(thread.IsAlive&&host.LaunchCount==6,"transient "+fault+" failure does not stop listening or relaunch consumed lifetime");
                    if(fault!="report")Check(host.Reports.Count(item=>item.Result=="ObservationUnknown")>errors,"transient "+fault+" failure recorded as Unknown");
                }
                root.Stop();
            }
            host.SetRoot(null);
            WaitFor(()=>host.Reports.LastOrDefault()?.CodexVerified==false,"clear manual-close lifetime");

            using(var root=host.StartRoot())
            {
                var existing=host.StartExistingWidget(root);
                Check(host.WidgetIsRunning(),"existing startup instance holds real isolated single-instance mutex");
                host.SetRoot(root);
                WaitFor(()=>host.Reports.Any(item=>item.Result=="ExistingWidget"&&item.Cycle==7),"existing startup widget consumes new lifetime");
                Check(host.LaunchCount==6,"Windows startup plus following Codex does not create second widget");
                existing.Stop();
                var before=host.ReportCount;
                WaitFor(()=>host.ReportCount>=before+5,"existing widget manual close observation");
                Check(host.LaunchCount==6&&!host.WidgetIsRunning(),"existing-widget lifetime remains consumed after user close");
                root.Stop();
            }
            host.SetRoot(null);
            WaitFor(()=>host.Reports.LastOrDefault()?.CodexVerified==false,"clear existing-widget lifetime");
            using(var failedRoot=host.StartRoot())
            {
                var errors=host.Reports.Count(item=>item.Result=="ObservationUnknown");
                host.InjectFault("launch");
                host.SetRoot(failedRoot);
                WaitFor(()=>host.Reports.Count(item=>item.Result=="ObservationUnknown")>errors,"recoverable launch failure");
                var before=host.ReportCount;
                WaitFor(()=>host.ReportCount>=before+5,"failed launch continuation");
                Check(thread.IsAlive&&host.LaunchCount==6,"launch exception leaves watcher listening and prevents repeated launch attempts in same lifetime");
                failedRoot.Stop();
            }
            using(var nextRoot=host.StartRoot())
            {
                host.SetRoot(nextRoot);
                WaitFor(()=>host.LaunchCount==7&&host.WidgetIsRunning(),"new lifetime after launch failure");
                Check(thread.IsAlive,"a later main lifetime remains launchable after prior launch failure");
                nextRoot.Stop();
                WaitFor(()=>!host.WidgetIsRunning(),"later fixture following root exit");
            }
            host.SetRoot(null);
            host.Enabled=false;
            Check(thread.Join(TimeSpan.FromSeconds(10)),"disabled registration stops watcher normally");
            Check(host.Reports.Any(item=>item.Result=="RegistrationDisabled")&&host.Reports.Last().Result=="StoppedNormally","disabled exit emits normal stop result");
            Check(loopError is null,"no unhandled watcher loop exception");

            using var explicitStop=new ManualResetEvent(false);
            using var stopHost=new FixtureHost(Path.Combine(directory,"stop-probe"));
            var stopThread=new Thread(()=>StartupWatchLoop.Run(stopHost,explicitStop,TimeSpan.FromMilliseconds(25))){IsBackground=true};
            stopThread.Start();
            WaitFor(()=>stopHost.ReportCount>0,"explicit stop probe started");
            explicitStop.Set();
            Check(stopThread.Join(TimeSpan.FromSeconds(10))&&stopHost.Reports.Last().Result=="StoppedNormally","native stop event exits same shared loop normally");

            File.WriteAllText(Path.Combine(directory,"result.json"),JsonSerializer.Serialize(new
            {
                Passed=true,Checks=checks,Count=checks.Count,WatcherProcessId=watcherPid,
                RepeatedMainLifetimes=5,SharedProductionLoop=true,RealChildProcesses=true,RealSingleInstanceMutex=true,
                SyntheticVerifiedIdentityInputs=true,SyntheticFollowExitWidget=true,RealCodexIdentityVerification=false,
                RealUserCodexExited=false,RealWindowsStartupTouched=false,RealModelCalls=false,
                LaunchCount=host.LaunchCount,CreatedProcessIds=host.ChildIds,Reports=host.Reports
            },new JsonSerializerOptions{WriteIndented=true}));
            Console.WriteLine("Evidence: "+Path.Combine(directory,"result.json"));
            return checks.Count;
        }
        finally
        {
            stop.Set();
            if(!thread.Join(TimeSpan.FromSeconds(10)))throw new TimeoutException("Owned watcher fixture failed to stop.");
        }
    }

    static void WaitFor(Func<bool> predicate,string label)
    {
        var timer=Stopwatch.StartNew();
        while(!predicate())
        {
            if(timer.Elapsed>TimeSpan.FromSeconds(15))throw new TimeoutException(label);
            Thread.Sleep(20);
        }
    }

    sealed class FixtureHost : IStartupWatchHost,IDisposable
    {
        readonly object gate=new();
        readonly string directory,mutexName;
        readonly List<FixtureChild> children=new();
        readonly ConcurrentQueue<StartupWatchReport> reports=new();
        DesktopRunIdentity[] snapshot=Array.Empty<DesktopRunIdentity>();
        FixtureChild? root,lastWidget;
        string? fault;
        int launches;
        volatile bool enabled=true;
        public FixtureHost(string path)
        {
            directory=path;Directory.CreateDirectory(path);
            mutexName="Local\\CQW-Watcher-Test-Widget-"+Guid.NewGuid().ToString("N");
        }
        public bool Enabled{get=>enabled;set=>enabled=value;}
        public int LaunchCount=>Volatile.Read(ref launches);
        public int ReportCount=>reports.Count;
        public IReadOnlyList<StartupWatchReport> Reports=>reports.ToArray();
        public FixtureChild? LastWidget{get{lock(gate)return lastWidget;}}
        public int[] ChildIds{get{lock(gate)return children.Select(item=>item.Identity.ProcessId).ToArray();}}
        public bool HasFault{get{lock(gate)return fault is not null;}}
        public void InjectFault(string value){lock(gate)fault=value;}
        void ThrowFault(string point)
        {
            lock(gate)
            {
                if(fault!=point)return;
                fault=null;
                throw new IOException("Synthetic transient "+point+" fault.");
            }
        }
        public bool IsEnabled(){ThrowFault("registration");return enabled;}
        public IReadOnlyList<DesktopRunIdentity> ObserveMainProcesses(){ThrowFault("observation");lock(gate)return snapshot.ToArray();}
        public bool WidgetIsRunning()
        {
            ThrowFault("mutex");
            if(!Mutex.TryOpenExisting(mutexName,out var mutex))return false;
            using(mutex)
            {
                var acquired=false;
                try{acquired=mutex.WaitOne(0);}
                catch(AbandonedMutexException){acquired=true;}
                if(acquired)mutex.ReleaseMutex();
                return !acquired;
            }
        }
        public int? LaunchWidget()
        {
            ThrowFault("launch");
            FixtureChild current;
            lock(gate)current=root??throw new InvalidOperationException("Fixture main process absent.");
            var child=StartExistingWidget(current);
            Interlocked.Increment(ref launches);
            return child.Process.Id;
        }
        public void Report(StartupWatchReport report)
        {
            ThrowFault("report");
            reports.Enqueue(report);
            File.AppendAllText(Path.Combine(directory,"loop.jsonl"),JsonSerializer.Serialize(report)+Environment.NewLine);
        }
        public void SetRoot(FixtureChild? child)
        {
            lock(gate)
            {
                root=child;
                snapshot=child is null?Array.Empty<DesktopRunIdentity>():new[]{child.Identity};
            }
        }
        public FixtureChild StartRoot(string role="main")=>StartChild(role,null);
        public FixtureChild StartExistingWidget(FixtureChild main)
        {
            var child=StartChild("widget",main.Identity);
            lock(gate)lastWidget=child;
            return child;
        }
        FixtureChild StartChild(string role,DesktopRunIdentity? main)
        {
            var child=new FixtureChild(directory,role,mutexName,main);
            lock(gate)children.Add(child);
            return child;
        }
        public void Dispose()
        {
            FixtureChild[] owned;
            lock(gate)owned=children.ToArray();
            foreach(var child in owned)child.Stop();
            foreach(var child in owned)child.Dispose();
        }
    }

    sealed class FixtureChild : IDisposable
    {
        readonly EventWaitHandle stop;
        bool disposed;
        public Process Process{get;}
        public DesktopRunIdentity Identity{get;}
        public FixtureChild(string directory,string role,string mutex,DesktopRunIdentity? main)
        {
            var id=Guid.NewGuid().ToString("N");
            var stopName="Local\\CQW-Watcher-Test-Stop-"+id;
            stop=new EventWaitHandle(false,EventResetMode.ManualReset,stopName);
            var ready=Path.Combine(directory,id+"-ready.json");
            var processPath=Environment.ProcessPath??throw new InvalidOperationException("Current executable unavailable.");
            var info=new ProcessStartInfo(processPath){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=directory};
            if(string.Equals(Path.GetFileNameWithoutExtension(processPath),"dotnet",StringComparison.OrdinalIgnoreCase))
                info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            foreach(var item in new[]{"--startup-watch-fixture-child","--fixture-role",role,"--fixture-stop",stopName,"--fixture-state",ready,"--fixture-mutex",mutex})info.ArgumentList.Add(item);
            if(main is not null)
            {
                info.ArgumentList.Add("--fixture-root-pid");info.ArgumentList.Add(main.ProcessId.ToString());
                info.ArgumentList.Add("--fixture-root-ticks");info.ArgumentList.Add(main.StartTimeUtcTicks.ToString());
            }
            Process=Process.Start(info)??throw new InvalidOperationException("Fixture launch failed.");
            Identity=new(Process.Id,Process.StartTime.ToUniversalTime().Ticks);
            try
            {
                WaitFor(()=>File.Exists(ready)||Process.HasExited,"fixture readiness");
                if(Process.HasExited)throw new InvalidOperationException("Fixture exited before ready: "+Process.ExitCode);
            }
            catch{Stop();throw;}
        }
        public void Stop()
        {
            if(disposed||Process.HasExited)return;
            stop.Set();
            if(!Process.WaitForExit(10000))throw new TimeoutException("Owned fixture did not exit normally.");
            if(Process.ExitCode!=0)throw new InvalidOperationException("Owned fixture failed: "+Process.ExitCode);
        }
        public void Dispose()
        {
            if(disposed)return;
            Stop();disposed=true;stop.Dispose();Process.Dispose();
        }
    }
}

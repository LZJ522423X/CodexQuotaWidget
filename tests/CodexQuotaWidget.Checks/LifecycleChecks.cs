using CodexQuotaWidget.Core;
using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;

public static class LifecycleChecks
{
    public static async Task<int> Run()
    {
        using var mutex=new Mutex(true,@"Local\CodexQuotaWidget."+WindowsIdentity.GetCurrent().User!.Value,out bool first);
        if(!first){Console.WriteLine("Widget running; lifecycle check not started.");return 1;}
        bool desktop=true,network=true;
        var checks=new List<object>();
        var monitor=new QuotaMonitor(new Settings(),()=>desktop,()=>network);
        void Check(bool passed,string name)
        {checks.Add(new{Name=name,Passed=passed});if(!passed)throw new InvalidOperationException(name);}
        try
        {
            await monitor.RefreshAsync();
            Check(monitor.Status==MonitorStatus.Live,"initial_real_quota");
            var attempts=monitor.AttemptCount;
            await Task.WhenAll(Enumerable.Range(0,20).Select(_=>monitor.RefreshAsync()));
            Check(monitor.AttemptCount==attempts+1,"20_overlapping_requests_coalesced_to_one");
            var quota=monitor.Snapshot;attempts=monitor.AttemptCount;
            desktop=false;await monitor.RecheckDesktopAsync();
            Check(monitor.Status==MonitorStatus.DesktopClosed && monitor.IsStale && ReferenceEquals(quota,monitor.Snapshot),"injected_desktop_unavailable_preserves_stale_data");
            Check(monitor.ServerPid is null && monitor.LastServerExitCode==0 && monitor.LastResidualProcesses==0,"unavailable_graceful_child_cleanup");
            monitor.Start();
            await Task.Delay(TimeSpan.FromSeconds(65));
            await monitor.RefreshAsync();
            Check(monitor.AttemptCount==attempts && monitor.ServerPid is null,"65_seconds_unavailable_no_requests_including_manual");
            desktop=true;await monitor.RecheckDesktopAsync();
            Check(monitor.Status==MonitorStatus.Live && monitor.AttemptCount==attempts+1,"availability_restored_real_reconnect");
            quota=monitor.Snapshot;attempts=monitor.AttemptCount;
            network=false;await monitor.RefreshAsync();
            Check(monitor.Status==MonitorStatus.Offline && monitor.AttemptCount==attempts && ReferenceEquals(quota,monitor.Snapshot),"injected_network_unavailable_no_request_no_crash");
            network=true;await monitor.RefreshAsync();
            Check(monitor.Status==MonitorStatus.Live,"network_available_refresh_recovers");
            await monitor.DisposeAsync();
            Check(monitor.LastServerExitCode==0 && monitor.LastResidualProcesses==0,"final_graceful_cleanup");
            // A uniquely named test Job Object cannot match Desktop or production jobs.
            var name=@"Local\CQW.LifecycleTest."+Guid.NewGuid().ToString("N");
            using var oldJob=new ProcessJob(name);
            using var child=new Process{StartInfo=new("powershell.exe","-NoProfile -NonInteractive -Command Start-Sleep -Seconds 60"){UseShellExecute=false,CreateNoWindow=true}};
            child.Start();oldJob.Assign(child);
            using var recovered=new ProcessJob(name);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Check(recovered.ActiveProcesses==0 && child.HasExited,"startup_named_job_cleans_own_stale_process");
            var report=new{Passed=true,Timestamp=DateTimeOffset.UtcNow,DesktopAvailability="injected detector, not physical Desktop shutdown",NetworkAvailability="injected detector, no OS network change",RealQuota=true,MainDesktopRunning=DesktopState.IsRunning(),Checks=checks};
            AtomicStore.Write(Path.Combine(Paths.Data,"checks","P2-C","lifecycle.json"),report);
            Console.WriteLine(JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));return 0;
        }
        catch(Exception e)
        {
            try{if(monitor.Status!=MonitorStatus.Stopped)await monitor.DisposeAsync();}catch{}
            var report=new{Passed=false,Error=e.GetType().Name,Checks=checks};AtomicStore.Write(Path.Combine(Paths.Data,"checks","P2-C","lifecycle.json"),report);
            Console.WriteLine(JsonSerializer.Serialize(report));return 1;
        }
    }
}

using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using CodexQuotaWidget.Core;

namespace CodexQuotaWidget;
public static class Soak
{
    public static async Task Run(QuotaMonitor monitor,int minutes,Func<Task> exit)
    {
        var began=DateTimeOffset.UtcNow;var samples=new List<object>();
        try
        {
            for(int i=0;i<=minutes;i++)
            {
                using var p=Process.GetCurrentProcess();p.Refresh();long? childMemory=null;int? childHandles=null;
                if(monitor.ServerPid is { } id)try{using var c=Process.GetProcessById(id);childMemory=c.WorkingSet64;childHandles=c.HandleCount;}catch(ArgumentException){}
                samples.Add(new{Time=DateTimeOffset.UtcNow,MemoryBytes=p.WorkingSet64,PrivateBytes=p.PrivateMemorySize64,CpuSeconds=p.TotalProcessorTime.TotalSeconds,Handles=p.HandleCount,ServerPid=monitor.ServerPid,ServerMemoryBytes=childMemory,ServerHandles=childHandles,monitor.SuccessCount,Status=monitor.Status.ToString()});
                AtomicStore.Write(Path.Combine(Paths.Data,"checks","soak.json"),new{Version="1.0.0",Began=began,Minutes=minutes,Completed=i==minutes,monitor.CliPath,monitor.CliVersion,monitor.ServerVersion,Runtime=Environment.Version.ToString(),Samples=samples});
                if(i<minutes)await Task.Delay(TimeSpan.FromMinutes(1));
            }
        }
        catch(Exception e){SafeLog.Write(LogEvent.StorageFailure,e.HResult);}
        finally{await exit();}
    }
}

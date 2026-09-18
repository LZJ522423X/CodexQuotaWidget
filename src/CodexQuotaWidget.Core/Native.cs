using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CodexQuotaWidget.Core;

public sealed class ProcessJob : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] struct Basic { public long A,B; public uint Flags; public UIntPtr Min,Max; public uint Count; public UIntPtr Affinity; public uint Priority,Scheduling; }
    [StructLayout(LayoutKind.Sequential)] struct Io { public ulong A,B,C,D,E,F; }
    [StructLayout(LayoutKind.Sequential)] struct Extended { public Basic Basic; public Io Io; public UIntPtr ProcessMemory,JobMemory,PeakProcess,PeakJob; }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern SafeFileHandle CreateJobObject(IntPtr security,string? name);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool SetInformationJobObject(SafeFileHandle job,int type,ref Extended info,uint length);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool AssignProcessToJobObject(SafeFileHandle job,IntPtr process);
    readonly SafeFileHandle handle;
    [StructLayout(LayoutKind.Sequential)] struct Accounting { public long User,Kernel,PeriodUser,PeriodKernel; public uint Faults,Total,Active,Terminated; }
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool QueryInformationJobObject(SafeFileHandle job,int type,out Accounting info,uint size,IntPtr returned);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool TerminateJobObject(SafeFileHandle job,uint code);
    public uint ActiveProcesses => QueryInformationJobObject(handle,1,out var info,(uint)Marshal.SizeOf<Accounting>(),IntPtr.Zero) ? info.Active : throw new Win32Exception();
    public void Terminate(){if(!TerminateJobObject(handle,1))throw new Win32Exception();}
    public ProcessJob(string? name=null)
    {
        handle=CreateJobObject(IntPtr.Zero,name);
        var info=new Extended{Basic=new Basic{Flags=0x2000}};
        if(handle.IsInvalid || !SetInformationJobObject(handle,9,ref info,(uint)Marshal.SizeOf<Extended>()))
        { handle.Dispose(); throw new Win32Exception(); }
        if(name is not null && ActiveProcesses>0)
        {
            Terminate();
            for(int i=0;i<40 && ActiveProcesses>0;i++)Thread.Sleep(50);
            if(ActiveProcesses>0){handle.Dispose();throw new Win32Exception("Owned job cleanup failed");}
        }
    }
    public void Assign(Process process) { if(!AssignProcessToJobObject(handle,process.Handle)) throw new Win32Exception(); }
    public void Dispose()=>handle.Dispose();
}
public static class DesktopState
{
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern int GetCurrentPackageFullName(ref uint length,IntPtr name);
    public static bool HasPackageIdentity {get{uint length=0;return GetCurrentPackageFullName(ref length,IntPtr.Zero)!=15700;}}
    public static bool IsRunning()
    {
        foreach(var name in new[]{"ChatGPT","Codex"})
        {
            var processes=Process.GetProcessesByName(name);
            try
            {
            foreach(var p in processes)
                try
                {
                    var path=p.MainModule?.FileName;
                    if(path is not null && (path.Contains(@"\WindowsApps\OpenAI.Codex_",StringComparison.OrdinalIgnoreCase)
                        || path.Contains(@"\OpenAI\Codex\app\",StringComparison.OrdinalIgnoreCase))) return true;
                }
                catch(Exception e) when(e is Win32Exception or InvalidOperationException) { }
            }
            finally { foreach(var p in processes)p.Dispose(); }
        }
        return false;
    }
}

using System.Security.Principal;

namespace CodexQuotaWidget.Core.Startup;

public static class StartupWatcherControl
{
    public static string WidgetMutexName => @"Local\CodexQuotaWidget." + WindowsIdentity.GetCurrent().User!.Value;
    public static string WatcherMutexName => @"Local\AIControlCenter.StartupWatcher." + WindowsIdentity.GetCurrent().User!.Value;
    public static bool IsRunning => IsOwned(WatcherMutexName);
    public static bool WidgetIsRunning() => IsOwned(WidgetMutexName);
    public static EventWaitHandle BeginWidgetFollowingClose() => new(true, EventResetMode.ManualReset, WidgetMutexName + ".following-close");
    public static bool WidgetExitInProgress()
    {
        try { using var notice = EventWaitHandle.OpenExisting(WidgetMutexName + ".following-close"); return notice.WaitOne(0); }
        catch(WaitHandleCannotBeOpenedException) { return false; }
    }
    public static void Stop()
    {
        try { using var handle = EventWaitHandle.OpenExisting(WatcherMutexName + ".stop"); handle.Set(); }
        catch(WaitHandleCannotBeOpenedException) { }
    }
    public static bool WaitForStopped(TimeSpan timeout)
    {
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while(IsRunning)
        {
            if(deadline.Elapsed >= timeout) return false;
            Thread.Sleep(50);
        }
        return true;
    }
    public static bool IsOwned(string name)
    {
        try
        {
            using var mutex = Mutex.OpenExisting(name);
            bool acquired;
            try { acquired = mutex.WaitOne(0); } catch(AbandonedMutexException) { acquired = true; }
            if(acquired) mutex.ReleaseMutex();
            return !acquired;
        }
        catch(WaitHandleCannotBeOpenedException) { return false; }
    }
}

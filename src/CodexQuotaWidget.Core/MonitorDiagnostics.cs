namespace CodexQuotaWidget.Core;
public static class MonitorDiagnostics
{
    public static object Snapshot(QuotaMonitor m)=>new
    {
        Version="1.0.0",Time=DateTimeOffset.UtcNow,Status=m.Status.ToString(),m.IsStale,m.IsBusy,
        m.SuccessCount,m.AttemptCount,m.ManualRefreshCount,m.AutomaticRefreshCount,
        m.ServerPid,m.LastServerExitCode,m.LastResidualProcesses,m.CliPath,m.CliVersion,m.ServerVersion,
        RequestedCodexHome=Paths.Home,RefreshSeconds=m.Settings.RefreshSeconds,FollowCodex=m.Settings.FollowCodex,
        DesktopRunning=DesktopState.IsRunning(),Quota=m.Snapshot
    };
}

using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CodexQuotaWidget.Core.Startup;

public sealed record StartupWatchReport(string Result, long Cycle = 0, bool NewCycle = false,
    bool? CodexVerified = null, bool? WidgetRunning = null, int? LaunchedPid = null, string? Error = null);

public interface IStartupWatchHost
{
    bool IsEnabled();
    IReadOnlyList<DesktopRunIdentity> ObserveMainProcesses();
    bool WidgetIsRunning();
    bool WidgetExitInProgress() => false;
    int? LaunchWidget();
    void Report(StartupWatchReport report);
}

/// <summary>The production watcher loop. A failed observation is Unknown, never a new or absent lifetime.</summary>
public static class StartupWatchLoop
{
    public static void Run(IStartupWatchHost host, WaitHandle stop, TimeSpan interval, int? maximumPolls = null)
    {
        var policy = new StartupWatchPolicy();
        var polls = 0;
        do
        {
            if(stop.WaitOne(0)) break;
            try
            {
                if(!host.IsEnabled()) { TryReport(host, new("RegistrationDisabled")); break; }
                // Do not consume a newly started Desktop lifetime while its old widget is still saving/exiting.
                // The independent watcher keeps polling and will launch once the old widget releases its mutex.
                if(host.WidgetExitInProgress())
                {
                    TryReport(host, new("WidgetPreparingNormalExit", WidgetRunning: true));
                    polls++;
                    if(maximumPolls is { } closingLimit && polls >= closingLimit) break;
                    if(stop.WaitOne(interval)) break;
                    continue;
                }
                var roots = host.ObserveMainProcesses();
                var running = host.WidgetIsRunning();
                var decision = policy.Evaluate(roots, running);
                int? launched = null;
                var result = decision.NewCycle && running ? "ExistingWidget" : "Observing";
                if(decision.ShouldLaunch)
                {
                    launched = host.LaunchWidget();
                    result = launched is null ? "LaunchFailed" : "Launched";
                }
                TryReport(host, new(result, decision.Cycle, decision.NewCycle, roots.Count > 0, running, launched));
            }
            catch(Exception error) when(IsRecoverable(error))
            {
                // Keep the policy's consumed identities, including after a launch exception. Never restart a
                // user-closed widget just because a registration, process or mutex observation briefly failed.
                TryReport(host, new("ObservationUnknown", Error: error.GetType().Name));
            }
            polls++;
            if(maximumPolls is { } limit && polls >= limit) break;
        } while(!stop.WaitOne(interval));
        TryReport(host, new("StoppedNormally"));
    }

    static bool IsRecoverable(Exception error) => error is IOException or UnauthorizedAccessException
        or InvalidOperationException or COMException or Win32Exception or TimeoutException;
    static void TryReport(IStartupWatchHost host, StartupWatchReport report)
    {
        try { host.Report(report); }
        catch(Exception error) when(IsRecoverable(error)) { /* Diagnostic storage failure cannot end listening. */ }
    }
}

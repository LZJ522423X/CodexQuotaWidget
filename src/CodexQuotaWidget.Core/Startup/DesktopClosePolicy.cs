namespace CodexQuotaWidget.Core.Startup;

public enum DesktopPresenceState { Running, Absent, Unknown }
public sealed record DesktopPresenceObservation(DesktopPresenceState State, int VerifiedInstances = 0);
public sealed record DesktopCloseDecision(bool ShouldClose, string Reason);

/// <summary>Complete verified Desktop absence, never quota, connectivity or a window-close signal.</summary>
public sealed class DesktopClosePolicy
{
    readonly TimeSpan grace;
    readonly int minimumSamples;
    bool observedRunning;
    TimeSpan? absentSince;
    int absentSamples;

    public DesktopClosePolicy(TimeSpan? grace = null, int minimumSamples = 3)
    {
        this.grace = grace ?? TimeSpan.FromSeconds(12);
        if (this.grace < TimeSpan.Zero || minimumSamples < 2) throw new ArgumentOutOfRangeException();
        this.minimumSamples = minimumSamples;
    }
    public DesktopCloseDecision Evaluate(bool enabled, DesktopPresenceObservation observation, TimeSpan elapsed)
    {
        if (!enabled)
        {
            observedRunning = false; ResetAbsence(); return new(false, "Disabled");
        }
        if (observation.State == DesktopPresenceState.Running && observation.VerifiedInstances > 0)
        {
            observedRunning = true; ResetAbsence(); return new(false, "DesktopRunning");
        }
        if (observation.State != DesktopPresenceState.Absent || observation.VerifiedInstances != 0)
        {
            ResetAbsence(); return new(false, "DesktopUnknown");
        }
        if (!observedRunning) return new(false, "AwaitingDesktopLifetime");
        if (absentSince is null || elapsed < absentSince) { absentSince = elapsed; absentSamples = 0; }
        absentSamples++;
        return absentSamples >= minimumSamples && elapsed - absentSince.Value >= grace
            ? new(true, "DesktopCompletelyExited") : new(false, "ConfirmingCompleteExit");
    }
    void ResetAbsence() { absentSince = null; absentSamples = 0; }
}

/// <summary>Shared poll controller. The existing GUI clock and isolated lifecycle fixture use this same code.</summary>
public sealed class DesktopCloseController
{
    readonly Func<bool> enabled;
    readonly Func<Task<DesktopPresenceObservation>> observe;
    readonly Func<Task<bool>> normalExit;
    readonly TimeProvider time;
    readonly long origin;
    readonly DesktopClosePolicy policy;
    int busy;
    bool finished;
    public DesktopCloseDecision LastDecision { get; private set; } = new(false, "NotObserved");
    public DesktopCloseController(Func<bool> enabled, Func<Task<DesktopPresenceObservation>> observe,
        Func<Task<bool>> normalExit, DesktopClosePolicy? policy = null, TimeProvider? time = null)
    {
        this.enabled = enabled; this.observe = observe; this.normalExit = normalExit;
        this.policy = policy ?? new(); this.time = time ?? TimeProvider.System; origin = this.time.GetTimestamp();
    }
    TimeSpan Elapsed => time.GetElapsedTime(origin, time.GetTimestamp());
    public async Task PollAsync()
    {
        if (finished || Interlocked.CompareExchange(ref busy, 1, 0) != 0) return;
        try
        {
            if (!enabled()) { LastDecision = policy.Evaluate(false, new(DesktopPresenceState.Unknown), Elapsed); return; }
            var observation = await observe();
            LastDecision = policy.Evaluate(enabled(), observation, Elapsed);
            if (!LastDecision.ShouldClose) return;
            // A second actual identity observation is required before saving/normal exit.
            var confirmation = await observe();
            LastDecision = policy.Evaluate(enabled(), confirmation, Elapsed);
            if (!LastDecision.ShouldClose) return;
            finished = await normalExit();
            LastDecision = new(finished, finished ? "ExitedNormally" : "ExitDeferred");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException
            or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
        {
            LastDecision = policy.Evaluate(enabled(), new(DesktopPresenceState.Unknown), Elapsed);
        }
        finally { Volatile.Write(ref busy, 0); }
    }
}

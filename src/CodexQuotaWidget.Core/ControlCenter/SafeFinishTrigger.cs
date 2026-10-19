namespace CodexQuotaWidget.Core.ControlCenter;

public static class SafeFinishTrigger
{
    // Legacy predicate is retained for snapshot tests only. Automatic execution uses ExhaustionDetector.
    public const double ThresholdPercent=0;
    public static bool IsDue(QuotaSnapshot? quota,MonitorStatus status,bool stale,DateTimeOffset now,int maximumAgeSeconds)
    {
        if(quota is null || status!=MonitorStatus.Live || stale || maximumAgeSeconds is <30 or >900)return false;
        var age=now-quota.UpdatedAt;
        if(age<TimeSpan.FromSeconds(-30) || age>TimeSpan.FromSeconds(maximumAgeSeconds))return false;
        var windows=quota.Windows.Where(w=>string.Equals(w.LimitId,"codex",StringComparison.OrdinalIgnoreCase)
            && w.WindowDurationMins is { } duration && double.IsFinite(duration) && Math.Abs(duration-300)<.01).ToArray();
        return windows.Length>0 && windows.All(w=>w.UsedPercent is { } used && double.IsFinite(used) && used is >=0 and <=100
            && (w.ResetsAt is null || w.ResetsAt>now.ToUnixTimeSeconds()))
            && windows.Min(w=>100-w.UsedPercent!.Value)<=ThresholdPercent;
    }
}

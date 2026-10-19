namespace CodexQuotaWidget.Core.Automation;

public sealed record ExhaustionReceipt
{
    public int SchemaVersion { get; init; } = 1;
    public Guid EventId { get; init; } = Guid.NewGuid();
    public string Source { get; init; } = "LiveCodexQuota";
    public string LimitId { get; init; } = "codex";
    public double WindowDurationMins { get; init; } = 300;
    public string CycleKey { get; init; } = "";
    public long ResetAt { get; init; }
    public DateTimeOffset PositiveObservedAt { get; init; }
    public double PositiveRemaining { get; init; }
    public DateTimeOffset FirstZeroObservedAt { get; init; }
    public DateTimeOffset SecondZeroObservedAt { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public double RemainingPercent { get; init; }
    public bool Synthetic { get; init; }
    public string PlanFingerprint { get; init; } = "";
    /// <summary>Quota evidence alone cannot authorize execution. A separately observed terminal task is required.</summary>
    public CodexExhaustedEvent? ExhaustedEvent { get; init; }
}

/// <summary>Reserved contract for a future authenticated task adapter. Arbitrary RPC/log/network failures are not signals.</summary>
public sealed record TrustedTaskExhaustionSignal(string TaskId, string Provider, string LimitId, double WindowDurationMins, DateTimeOffset ObservedAt);
public interface ITaskExhaustionSignalSource
{
    event Action<TrustedTaskExhaustionSignal>? FiveHourExhausted;
}

/// <summary>Requires a new positive-to-zero transition in this process. No startup-zero trigger and no percentage setting.</summary>
public sealed class ExhaustionDetector
{
    readonly object gate = new();
    readonly HashSet<string> handled;
    string? cycle;
    DateTimeOffset? positiveObservedAt;
    double positiveRemaining;
    DateTimeOffset? firstZeroAt;
    DateTimeOffset? lastObservation;
    bool issued;

    public ExhaustionDetector(IReadOnlyCollection<string>? handledCycles = null)
        => handled = new(handledCycles ?? [], StringComparer.Ordinal);

    public ExhaustionReceipt? Observe(QuotaSnapshot? quota, MonitorStatus status, bool stale, DateTimeOffset now, int maximumAgeSeconds = 180)
    {
        lock (gate)
        {
            if (!TryRead(quota, status, stale, now, maximumAgeSeconds, out var remaining, out var resetAt))
            { firstZeroAt = null; return null; }
            var key = CycleKey(resetAt);
            if (!string.Equals(cycle, key, StringComparison.Ordinal))
            {
                cycle = key; positiveObservedAt = null; firstZeroAt = null; lastObservation = null;
                positiveRemaining = 0; issued = handled.Contains(key);
            }
            var observedAt = quota!.UpdatedAt;
            // Re-evaluating the same cached snapshot is not independent confirmation.
            if (lastObservation is { } last && observedAt <= last) return null;
            lastObservation = observedAt;
            if (remaining > 0)
            {
                positiveObservedAt = observedAt; positiveRemaining = remaining; firstZeroAt = null;
                return null;
            }
            if (issued || positiveObservedAt is null || observedAt <= positiveObservedAt) return null;
            if (firstZeroAt is null) { firstZeroAt = observedAt; return null; }
            if (observedAt <= firstZeroAt) return null;
            issued = true;
            handled.Add(key);
            return new()
            {
                CycleKey = key, ResetAt = resetAt, PositiveObservedAt = positiveObservedAt.Value,
                PositiveRemaining = positiveRemaining, FirstZeroObservedAt = firstZeroAt.Value,
                SecondZeroObservedAt = observedAt, CreatedAt = now
            };
        }
    }

    public static string CycleKey(long resetAt) => $"codex:300:{resetAt}";

    public static bool IsValidLiveQuota(QuotaSnapshot? quota, MonitorStatus status, bool stale, DateTimeOffset now,
        out double remaining, out long resetAt, int maximumAgeSeconds = 180)
        => TryRead(quota, status, stale, now, maximumAgeSeconds, out remaining, out resetAt);

    static bool TryRead(QuotaSnapshot? quota, MonitorStatus status, bool stale, DateTimeOffset now, int maximumAgeSeconds,
        out double remaining, out long resetAt)
    {
        remaining = 0; resetAt = 0;
        if (quota is null || status != MonitorStatus.Live || stale || maximumAgeSeconds is <30 or >900) return false;
        var age = now - quota.UpdatedAt;
        if (age < TimeSpan.Zero || age > TimeSpan.FromSeconds(maximumAgeSeconds)) return false;
        var windows = quota.Windows?.Where(window => string.Equals(window.LimitId, "codex", StringComparison.OrdinalIgnoreCase)
            && window.WindowDurationMins is { } duration && double.IsFinite(duration) && Math.Abs(duration - 300) < .01).ToArray();
        if (windows is null || windows.Length == 0) return false;
        if (windows.Any(window => window.UsedPercent is not { } used || !double.IsFinite(used) || used is <0 or >100
            || window.ResetsAt is not { } reset || reset <= now.ToUnixTimeSeconds() || reset > now.AddHours(36).ToUnixTimeSeconds())) return false;
        var cycleReset = windows[0].ResetsAt!.Value;
        resetAt = cycleReset;
        if (windows.Any(window => window.ResetsAt != cycleReset)) return false;
        // Mixed duplicate windows are ambiguous; they cannot establish genuine exhaustion.
        if (windows.Any(window => window.UsedPercent != windows[0].UsedPercent)) return false;
        remaining = 100 - windows[0].UsedPercent!.Value;
        return true;
    }

    public static bool ValidateReceipt(ExhaustionReceipt? receipt, DateTimeOffset now, int maximumAgeSeconds = 900)
    {
        if (receipt is null || receipt.SchemaVersion != 1 || receipt.EventId == Guid.Empty
            || receipt.Source != "LiveCodexQuota" || receipt.LimitId != "codex" || receipt.WindowDurationMins != 300
            || receipt.CycleKey != CycleKey(receipt.ResetAt) || receipt.ResetAt <= now.ToUnixTimeSeconds()
            || receipt.ResetAt > now.AddHours(36).ToUnixTimeSeconds()
            || !double.IsFinite(receipt.RemainingPercent) || receipt.RemainingPercent != 0
            || !double.IsFinite(receipt.PositiveRemaining) || receipt.PositiveRemaining is <=0 or >100
            || maximumAgeSeconds is <30 or >900) return false;
        if (receipt.PositiveObservedAt >= receipt.FirstZeroObservedAt || receipt.FirstZeroObservedAt >= receipt.SecondZeroObservedAt
            || receipt.SecondZeroObservedAt > receipt.CreatedAt || receipt.CreatedAt > now
            || now - receipt.SecondZeroObservedAt > TimeSpan.FromSeconds(maximumAgeSeconds)
            || receipt.FirstZeroObservedAt.ToUnixTimeSeconds() >= receipt.ResetAt
            || receipt.PositiveObservedAt.ToUnixTimeSeconds() >= receipt.ResetAt) return false;
        return true;
    }
}

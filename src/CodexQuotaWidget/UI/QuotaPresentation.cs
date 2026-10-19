using System;
using System.Linq;
using CodexQuotaWidget.Core;

namespace CodexQuotaWidget;

internal sealed record QuotaPresentation(
    QuotaSnapshot? Snapshot,
    QuotaWindow? FiveHour,
    QuotaWindow? Weekly)
{
    public int? AvailableCount => Snapshot?.AvailableCount;

    public static QuotaPresentation From(QuotaMonitor monitor)
    {
        var snapshot = monitor.Snapshot;
        return new QuotaPresentation(
            snapshot,
            snapshot?.Windows.FirstOrDefault(x => x.LimitId == "codex" && x.WindowDurationMins == 300),
            snapshot?.Windows.FirstOrDefault(x => x.LimitId == "codex" && x.WindowDurationMins == 10080));
    }

    public static string Remaining(double? value) => value is { } amount ? $"{amount:0.#}%" : "--";

    public static string Credits(int? count) => count is { } number ? $"{number} 次" : "--";

    public static string Countdown(long? resetAt)
    {
        if (resetAt is null)
            return "重置时间未知";
        var remaining = DateTimeOffset.FromUnixTimeSeconds(resetAt.Value) - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero)
            return "即将重置";
        if (remaining.TotalDays >= 1)
            return $"{(int)remaining.TotalDays}天 {remaining.Hours}小时后重置";
        if (remaining.TotalHours >= 1)
            return $"{(int)remaining.TotalHours}小时 {remaining.Minutes}分钟后重置";
        return $"{Math.Max(0, remaining.Minutes)}分钟后重置";
    }

    public static string ResetTime(long? resetAt) => resetAt is { } timestamp
        ? DateTimeOffset.FromUnixTimeSeconds(timestamp).ToLocalTime().ToString("yyyy-MM-dd HH:mm")
        : "重置时间未知";

    public static string LastUpdated(QuotaSnapshot? snapshot) => snapshot is null
        ? "尚无成功读数"
        : $"更新于 {snapshot.UpdatedAt.ToLocalTime():HH:mm:ss}";

    public static bool IsReset(QuotaWindow? before, QuotaWindow? after, DateTimeOffset updatedAt) =>
        before?.ResetsAt is { } previousReset && after?.ResetsAt is { } nextReset &&
        previousReset <= updatedAt.ToUnixTimeSeconds() && nextReset > previousReset &&
        before.Remaining is <= 10 && after.Remaining is >= 80;
}

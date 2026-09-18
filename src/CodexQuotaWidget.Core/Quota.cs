using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexQuotaWidget.Core;

public sealed record QuotaWindow(string LimitId, string Name, string Slot, double? UsedPercent, double? WindowDurationMins, long? ResetsAt)
{
    public double? Remaining => UsedPercent is { } n ? Math.Clamp(100 - n, 0, 100) : null;
}
public sealed record QuotaSnapshot(DateTimeOffset UpdatedAt, string Plan, List<QuotaWindow> Windows, bool ResetCreditsProvided, int? AvailableCount, List<long> CreditExpirations);
public sealed class ProtocolException(string field) : Exception("Missing or invalid field: " + field);
public static class QuotaParser
{
    public static JsonElement Get(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;
    public static string? String(JsonElement e, string name) => Get(e,name) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;
    public static double? Number(JsonElement e, string name) => Get(e,name) is { ValueKind: JsonValueKind.Number } v && v.TryGetDouble(out var n) && double.IsFinite(n) ? n : null;
    public static long? Timestamp(JsonElement e, string name) => Get(e,name) is { ValueKind: JsonValueKind.Number } v && v.TryGetInt64(out var n) && n >= 0 && n <= 253402300799 ? n : null;
    public static string Label(string? value, string fallback = "Unknown") => value is not null && Regex.IsMatch(value, @"^[\p{L}\p{N} _./()\-]{1,100}$") ? value : fallback;
    public static QuotaSnapshot Parse(JsonElement result, string plan)
    {
        var windows = new List<QuotaWindow>();
        void Add(string id, JsonElement snapshot)
        {
            if (snapshot.ValueKind != JsonValueKind.Object) return;
            foreach (var property in snapshot.EnumerateObject())
            {
                var w = property.Value;
                if (w.ValueKind != JsonValueKind.Object) continue;
                if (property.Name is not ("primary" or "secondary") && Get(w,"usedPercent").ValueKind == JsonValueKind.Undefined) continue;
                windows.Add(new(Label(id), Label(String(snapshot,"limitName"),Label(id)), Label(property.Name),
                    Number(w,"usedPercent"), Number(w,"windowDurationMins"), Timestamp(w,"resetsAt")));
            }
        }
        var map = Get(result,"rateLimitsByLimitId");
        if (map.ValueKind == JsonValueKind.Object)
            foreach (var bucket in map.EnumerateObject()) Add(bucket.Name,bucket.Value);
        if (windows.Count == 0)
        {
            var legacy = Get(result,"rateLimits");
            Add(String(legacy,"limitId") ?? "codex",legacy);
        }
        if (windows.Count == 0) throw new ProtocolException("rateLimitsByLimitId/rateLimits.windows");
        if (windows.All(w => w.UsedPercent is null)) throw new ProtocolException("windows.usedPercent");
        var credits = Get(result,"rateLimitResetCredits");
        var provided = credits.ValueKind == JsonValueKind.Object;
        var count = Number(credits,"availableCount");
        int? available = count is >= 0 and <= int.MaxValue && count == Math.Truncate(count.Value) ? (int)count.Value : null;
        var expiry = new List<long>();
        var list = Get(credits,"credits");
        if (list.ValueKind == JsonValueKind.Array)
            foreach (var c in list.EnumerateArray())
                if (String(c,"status") == "available" && Timestamp(c,"expiresAt") is { } t) expiry.Add(t);
        return new(DateTimeOffset.UtcNow,Label(plan),windows.Distinct().ToList(),provided,available,expiry.Distinct().Order().ToList());
    }
}

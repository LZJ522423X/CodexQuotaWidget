using System.Text;
using System.Text.Json;

namespace CodexQuotaWidget.Core.ControlCenter;

/// <summary>Allowlisted response structure only. Never contains headers, raw JSON, deltas or credential values.</summary>
public sealed record SiwcResponseShape(string Kind, string[] Fields, string[] EventTypes, bool Truncated)
{
    public string? ObjectKind { get; init; }
    public string? ResponseStatus { get; init; }
    public string? ErrorCode { get; init; }
}
public sealed record SiwcHttpResponseEvidence(int StatusCode, string ContentType, SiwcResponseShape Structure)
{
    public bool MissingContentTypeValidated { get; init; }
    public SiwcRejectedEventDiagnostic? RejectedEvent { get; init; }
}
public sealed record SiwcDiagnosticField(string Name, string ValueType);
public sealed record SiwcRejectedEventDiagnostic(string? EventType, SiwcDiagnosticField[] Fields, int StreamPosition, string Reason, string Stage)
{
    public bool Truncated { get; init; }
    public bool Redacted { get; init; }
}
public interface ISiwcResponseEvidenceSource { SiwcHttpResponseEvidence? ResponseEvidence { get; } }

public static class SiwcResponseEvidence
{
    const int MaximumProbeBytes = 16 * 1024;
    static readonly object RejectionKey = new();
    static readonly HashSet<string> RejectionReasons = new(StringComparer.Ordinal)
    {
        "siwc_unsupported_event", "siwc_metadata_requires_handling", "siwc_event_schema_invalid", "siwc_event_out_of_order", "siwc_event_sequence_missing",
        "siwc_event_sequence_conflict", "siwc_duplicate_authoritative_event", "siwc_response_not_created", "siwc_response_identity_mismatch",
        "siwc_response_model_mismatch", "siwc_output_item_mismatch", "siwc_output_index_invalid", "siwc_text_snapshot_conflict",
        "siwc_completed_snapshot_conflict", "siwc_authoritative_snapshot_conflict", "siwc_completed_contains_failure",
        "siwc_completed_identity_or_status_mismatch", "siwc_response_failed", "siwc_response_incomplete", "siwc_invalid_text_body",
        "siwc_text_events_missing", "siwc_body_mismatch", "siwc_event_after_completed", "siwc_event_after_item_done",
        "siwc_event_after_part_done", "siwc_delta_after_text_done", "siwc_invalid_annotation_order", "siwc_unexpected_tool_output",
        "siwc_unexpected_tool_or_incomplete_output", "siwc_non_text_output", "siwc_event_without_data", "siwc_event_type_mismatch",
        "siwc_invalid_event_framing", "siwc_invalid_sse_field", "siwc_incomplete_sse_framing", "siwc_done_without_completed",
        "siwc_stream_ended_without_completed", "siwc_stream_size_limit", "siwc_event_size_limit", "siwc_event_count_limit",
        "siwc_text_size_limit", "siwc_strict_requirements_invalid", "subscription_sharing_usage_limit_exceeded", "subscription_sharing_usage_unavailable"
    };
    // Exception-local metadata only: preserves the original exception type, code and rejection behavior.
    public static SiwcRejectedEventDiagnostic? GetRejection(Exception error)
    {
        try { return error.Data[RejectionKey] as SiwcRejectedEventDiagnostic; }
        catch { return null; }
    }
    internal static SiwcRejectedEventDiagnostic RejectionContext(JsonElement value, int position)
    {
        const int maximumFields = 32;
        var fields = new List<SiwcDiagnosticField>(); bool redacted = false, truncated = false;
        string? type = null;
        if (value.ValueKind == JsonValueKind.Object)
        {
            int typeFields = 0;
            foreach (var property in value.EnumerateObject())
            {
                if (property.Name == "type")
                {
                    typeFields++;
                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        var name = property.Value.GetString()!;
                        bool safe = name.Length <= 128 && (name == "error" || name.StartsWith("response.", StringComparison.Ordinal))
                            && name.Split('.').Length <= 8 && name.Split('.').All(SafeIdentifier) && !SensitiveIdentifier(name);
                        type = safe ? name : "RedactedEventType"; redacted |= !safe;
                    }
                }
                if (fields.Count == maximumFields) { truncated = true; continue; }
                bool safeField = SafeIdentifier(property.Name) && !SensitiveIdentifier(property.Name);
                fields.Add(new(safeField ? property.Name : "RedactedField", property.Value.ValueKind.ToString()));
                redacted |= !safeField;
            }
            if (typeFields > 1) { type = "RedactedEventType"; redacted = true; }
        }
        return new(type, fields.ToArray(), position, "", "") { Redacted = redacted, Truncated = truncated };
    }
    static bool SafeIdentifier(string name) => name.Length is > 0 and <= 64 && name[0] is >= 'a' and <= 'z'
        && name.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');
    static bool SensitiveIdentifier(string name) => new[] { "token", "cookie", "authorization", "password", "secret", "credential", "key", "sk_" }
        .Any(part => name.Contains(part, StringComparison.OrdinalIgnoreCase));
    internal static void AttachRejection(Exception error, SiwcRejectedEventDiagnostic? context, int position, string stage)
    {
        try
        {
            var reason = error switch
            {
                SiwcProtocolException protocol => RejectionReasons.Contains(protocol.Code) ? protocol.Code : "siwc_protocol_rejected",
                JsonException or KeyNotFoundException or FormatException or InvalidOperationException => "siwc_response_structure_invalid",
                DecoderFallbackException => "siwc_invalid_utf8",
                InvalidDataException => "siwc_event_schema_invalid",
                OperationCanceledException => "siwc_cancelled_or_timeout",
                _ => "siwc_stream_io_failed"
            };
            error.Data[RejectionKey] = (context ?? new(null, [], position, "", "")) with { Reason = reason, Stage = stage };
        }
        catch { } // Diagnostic collection must never replace the original failure or turn it into success.
    }
    static readonly HashSet<string> Fields = new(StringComparer.Ordinal)
    { "type", "object", "status", "id", "response", "response_id", "output", "content", "role", "text", "delta", "item", "item_id",
      "output_index", "content_index", "sequence_number", "error", "code", "message", "detail", "usage", "model", "incomplete_details" };
    static readonly HashSet<string> Events = new(StringComparer.Ordinal)
    { "response.created", "response.in_progress", "response.metadata", "response.output_item.added", "response.output_item.done", "response.content_part.added",
      "response.content_part.done", "response.output_text.delta", "response.output_text.done", "response.output_text.annotation.added", "response.completed", "response.failed",
      "response.incomplete", "error", "response.refusal.delta", "response.refusal.done", "response.reasoning_summary_part.added",
      "response.reasoning_summary_part.done", "response.reasoning_summary_text.delta", "response.reasoning_summary_text.done" };
    public static string MediaType(string? value) => value?.ToLowerInvariant() is
        "text/event-stream" or "application/json" or "application/problem+json" or "text/html" or "text/plain" or "application/octet-stream"
        ? value.ToLowerInvariant() : value is null ? "Missing" : "Other";
    public static string EventType(string value) => Events.Contains(value) ? value : "OtherEvent";
    public static string[] ObjectFields(JsonElement value)
    {
        var fields = new List<string>();
        void Visit(JsonElement item, string prefix, int depth)
        {
            if (depth > 5 || fields.Count >= 48) return;
            if (item.ValueKind == JsonValueKind.Array)
            { foreach (var entry in item.EnumerateArray().Take(2)) Visit(entry, prefix + "[]", depth + 1); return; }
            if (item.ValueKind != JsonValueKind.Object) return;
            foreach (var p in item.EnumerateObject().Where(p => Fields.Contains(p.Name)).Take(24))
            {
                if (fields.Count >= 48) break;
                var path = prefix.Length == 0 ? p.Name : prefix + "." + p.Name;
                fields.Add(path + ":" + p.Value.ValueKind);
                if (p.Name is "response" or "output" or "content" or "item" or "error") Visit(p.Value, path, depth + 1);
            }
        }
        Visit(value, "", 0); return fields.Distinct(StringComparer.Ordinal).ToArray();
    }
    public static SiwcResponseShape Event(JsonElement value, string type) => Discriminators(new("SseEvent", ObjectFields(value), [EventType(type)], false), value);
    static SiwcResponseShape Discriminators(SiwcResponseShape shape, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return shape;
        var response = value.TryGetProperty("response", out var nested) && nested.ValueKind == JsonValueKind.Object ? nested : value;
        string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
        var objectKind = Text(response, "object"); var status = Text(response, "status");
        var error = response.TryGetProperty("error", out var er) && er.ValueKind == JsonValueKind.Object ? er : response;
        var code = Text(error, "code");
        return shape with { ObjectKind = objectKind == "response" ? "response" : null,
            ResponseStatus = status is "queued" or "in_progress" or "completed" or "failed" or "incomplete" or "cancelled" ? status : null,
            ErrorCode = code is "subscription_sharing_usage_limit_exceeded" or "subscription_sharing_usage_unavailable" or "invalid_request_error"
                or "insufficient_quota" or "model_not_found" or "invalid_api_key" or "permission_denied" ? code : null };
    }
    public static async Task<SiwcResponseShape> ProbeAsync(Stream stream, CancellationToken token)
    {
        var bytes = new byte[MaximumProbeBytes]; int count = 0;
        while (count < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(count), token).ConfigureAwait(false);
            if (read == 0) break;
            count += read;
        }
        return Inspect(bytes.AsSpan(0, count), count == bytes.Length);
    }
    public static SiwcResponseShape Inspect(ReadOnlySpan<byte> bytes, bool truncated = false)
    {
        var text = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
        if (text.Length == 0) return new("Empty", [], [], truncated);
        if (text.StartsWith("event:", StringComparison.Ordinal) || text.StartsWith("data:", StringComparison.Ordinal)
            || text.StartsWith("id:", StringComparison.Ordinal) || text.StartsWith("retry:", StringComparison.Ordinal) || text.StartsWith(':'))
            return new("SseFraming", [], [], truncated);
        if (text.StartsWith('<')) return new("HtmlOrXml", [], [], truncated);
        if (text[0] is not ('{' or '[')) return new("NonJsonText", [], [], truncated);
        try
        {
            using var doc = JsonDocument.Parse(bytes.ToArray());
            var root = doc.RootElement; var types = new List<string>();
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
                types.Add(EventType(type.GetString()!));
            return Discriminators(new(root.ValueKind == JsonValueKind.Object ? "JsonObject" : "JsonArray", ObjectFields(root), types.ToArray(), truncated), root);
        }
        catch (JsonException) { return new("IncompleteOrInvalidJson", [], [], truncated); }
    }
}

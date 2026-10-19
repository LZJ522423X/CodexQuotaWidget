using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexQuotaWidget.Core.ControlCenter;

public sealed class SiwcProtocolException(string code) : IOException(code) { public string Code { get; } = code; }
public sealed record SiwcTokenUsage(long? InputTokens, long? OutputTokens, long? TotalTokens);
public sealed record SiwcStrictSseRequirements(string Model, string ExpectedText);
public sealed record SiwcTextCompletion(string ResponseId, string Text)
{
    public SiwcTokenUsage? Usage { get; init; }
    public int TextDeltaEvents { get; init; }
}

/// <summary>Bounded UTF-8 SSE consumer. Partial text is never a successful workflow result.</summary>
public static class SiwcResponsesReader
{
    const int MaxChars = 256 * 1024, MaxText = 60000, MaxEvents = 12000;
    public static async Task<SiwcTextCompletion> ReadAsync(Stream stream, CancellationToken token = default,
        Action<SiwcResponseShape>? observe = null, string? expectedModel = null, SiwcStrictSseRequirements? strict = null)
    {
        if (strict is not null && (string.IsNullOrWhiteSpace(strict.Model) || string.IsNullOrWhiteSpace(strict.ExpectedText)
            || strict.ExpectedText.Length > MaxText || expectedModel is not null && expectedModel != strict.Model))
            throw new SiwcProtocolException("siwc_strict_requirements_invalid");
        expectedModel = strict?.Model ?? expectedModel;
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), false, 1024, true);
        var line = new StringBuilder(); var data = new StringBuilder(); var buffer = new char[1024];
        var seen = new Dictionary<int, string>(); int largestSequence = -1, total = 0, events = 0;
        var parts = new SortedDictionary<(int Output, int Content), StringBuilder>();
        var itemIds = new Dictionary<int, string>(); var finishedText = new HashSet<(int, int)>(); string? responseId = null;
        string eventName = ""; int deltas = 0;
        bool firstCharacter = true, previousCr = false, frameEvent = false, created = false;
        int textEvents = 0;
        SiwcTextCompletion? completed = null;
        var authorities = new HashSet<string>(StringComparer.Ordinal);
        var partSnapshots = new Dictionary<(int, int), string>();
        var itemSnapshots = new Dictionary<int, string[]>();
        SiwcRejectedEventDiagnostic? rejectionContext = null;
        string stage = "SseFraming";
        try
        {
        while (true)
        {
            stage = "Utf8Decoding"; rejectionContext = null;
            var count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (count == 0)
            {
                stage = "StreamEnd";
                if (strict is not null && (line.Length != 0 || data.Length != 0 || frameEvent)) throw new SiwcProtocolException("siwc_incomplete_sse_framing");
                if (strict is not null && completed is not null) return completed;
                throw new SiwcProtocolException("siwc_stream_ended_without_completed");
            }
            total += count; if (total > MaxChars) throw new SiwcProtocolException("siwc_stream_size_limit");
            for (var i = 0; i < count; i++)
            {
                stage = "SseFraming"; rejectionContext = null;
                token.ThrowIfCancellationRequested(); var c = buffer[i];
                if (firstCharacter) { firstCharacter = false; if (c == '\uFEFF') continue; }
                if (previousCr) { previousCr = false; if (c == '\n') continue; }
                if (c == '\r') previousCr = true;
                if (c is not ('\n' or '\r')) { line.Append(c); if (line.Length > MaxChars) throw new SiwcProtocolException("siwc_event_size_limit"); continue; }
                var value = line.ToString(); line.Clear();
                if (value.Length != 0)
                {
                    if (value.StartsWith(':')) continue;
                    var split = value.IndexOf(':'); var field = split < 0 ? value : value[..split];
                    var content = split < 0 ? "" : value[(split + 1)..]; if (content.StartsWith(' ')) content = content[1..];
                    if (field == "data") { data.Append(content).Append('\n'); if (data.Length > MaxChars) throw new SiwcProtocolException("siwc_event_size_limit"); }
                    else if (field == "event")
                    {
                        if (strict is not null && (frameEvent || content.Length == 0 || content.Any(char.IsControl))) throw new SiwcProtocolException("siwc_invalid_event_framing");
                        frameEvent = true; eventName = content;
                    }
                    else if (strict is not null && (field is not ("id" or "retry") || content.Contains('\0')
                        || field == "retry" && (content.Length == 0 || !content.All(char.IsAsciiDigit))))
                        throw new SiwcProtocolException("siwc_invalid_sse_field");
                    continue;
                }
                if (data.Length == 0)
                {
                    if (strict is not null && frameEvent) throw new SiwcProtocolException("siwc_event_without_data");
                    eventName = ""; frameEvent = false; continue;
                }
                if (++events > MaxEvents) throw new SiwcProtocolException("siwc_event_count_limit");
                var json = data.ToString().TrimEnd('\n'); data.Clear();
                if (json == "[DONE]") throw new SiwcProtocolException("siwc_done_without_completed");
                stage = "JsonDecoding";
                using var doc = JsonDocument.Parse(json); var e = doc.RootElement;
                try { rejectionContext = SiwcResponseEvidence.RejectionContext(e, events); }
                catch { rejectionContext = null; } // Metadata failures never alter the parser's existing decision.
                stage = "EventSchema"; SiwcIdToken.Unique(e);
                var type = SiwcAuthorizationAttempt.Required(e, "type");
                if (strict is not null && completed is not null) throw new SiwcProtocolException("siwc_event_after_completed");
                if (strict is not null) ValidateStrictEvent(e, type);
                observe?.Invoke(SiwcResponseEvidence.Event(e, type));
                stage = "EventFraming";
                if (eventName.Length > 0 && eventName != type) throw new SiwcProtocolException("siwc_event_type_mismatch"); eventName = ""; frameEvent = false;
                stage = "EventSequence";
                if (!e.TryGetProperty("sequence_number", out var seq) || seq.ValueKind != JsonValueKind.Number
                    || !seq.TryGetInt32(out var sequence) || sequence < 0)
                    throw new SiwcProtocolException("siwc_event_sequence_missing");
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
                if (seen.TryGetValue(sequence, out var prior))
                { if (prior != hash) throw new SiwcProtocolException("siwc_event_sequence_conflict"); if (strict is not null) throw new SiwcProtocolException("siwc_duplicate_authoritative_event"); continue; }
                if (sequence <= largestSequence) throw new SiwcProtocolException("siwc_event_out_of_order");
                largestSequence = sequence; seen.Add(sequence, hash);
                stage = "ResponseIdentity";
                if (e.TryGetProperty("response_id", out var reference) && reference.GetString() != responseId)
                    throw new SiwcProtocolException("siwc_response_identity_mismatch");
                stage = "EventLifecycle";
                if (type is "response.created" or "response.in_progress")
                {
                    var response = e.GetProperty("response"); var id = SiwcAuthorizationAttempt.Required(response, "id");
                    if (strict is not null)
                    {
                        if (type == "response.created" && created) throw new SiwcProtocolException("siwc_duplicate_authoritative_event");
                        if (type == "response.in_progress" && !created) throw new SiwcProtocolException("siwc_response_not_created");
                        if (response.GetProperty("model").GetString() != strict.Model) throw new SiwcProtocolException("siwc_response_model_mismatch");
                        if (type == "response.created") created = true;
                    }
                    if (!ValidResponseId(id) || responseId is not null && id != responseId) throw new SiwcProtocolException("siwc_response_identity_mismatch");
                    responseId = id; continue;
                }
                if (type is "response.failed" or "response.incomplete" or "error")
                {
                    var error = e.TryGetProperty("response", out var r) && r.TryGetProperty("error", out var er) ? er : e;
                    var code = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var codeValue) ? codeValue.GetString() : null;
                    throw new SiwcProtocolException(code is "subscription_sharing_usage_limit_exceeded" or "subscription_sharing_usage_unavailable"
                        ? code : type == "response.incomplete" ? "siwc_response_incomplete" : "siwc_response_failed");
                }
                if (responseId is null) throw new SiwcProtocolException("siwc_response_not_created");
                if (strict is not null && !created) throw new SiwcProtocolException("siwc_response_not_created");
                if (type is "response.output_item.added" or "response.output_item.done")
                {
                    var item = e.GetProperty("item"); var itemType = item.GetProperty("type").GetString();
                    if (itemType is not ("message" or "reasoning")) throw new SiwcProtocolException("siwc_unexpected_tool_output");
                    if (itemType == "message" || strict is not null) BindItem(Index(e, "output_index"), SiwcAuthorizationAttempt.Required(item, "id"));
                    if (strict is not null && authorities.Contains("response.output_item.done:" + Index(e, "output_index"))) throw new SiwcProtocolException("siwc_event_after_item_done");
                    if (strict is not null && !authorities.Add(type + ":" + Index(e, "output_index"))) throw new SiwcProtocolException("siwc_duplicate_authoritative_event");
                    if (strict is not null && type == "response.output_item.done" && itemType == "message")
                        itemSnapshots[Index(e, "output_index")] = item.GetProperty("content").EnumerateArray().Select(p => p.GetProperty("text").GetString()!).ToArray();
                }
                if (strict is not null && type is "response.content_part.added" or "response.content_part.done")
                {
                    var output = Index(e, "output_index"); var ci = Index(e, "content_index");
                    BindItem(output, SiwcAuthorizationAttempt.Required(e, "item_id"));
                    if (authorities.Contains("response.output_item.done:" + output) || authorities.Contains("response.content_part.done:" + output + ":" + ci))
                        throw new SiwcProtocolException("siwc_event_after_part_done");
                    if (!authorities.Add(type + ":" + output + ":" + ci)) throw new SiwcProtocolException("siwc_duplicate_authoritative_event");
                    if (type == "response.content_part.done") partSnapshots[(output, ci)] = e.GetProperty("part").GetProperty("text").GetString()!;
                }
                if (strict is not null && type == "response.output_text.annotation.added")
                {
                    var output = Index(e, "output_index"); var ci = Index(e, "content_index");
                    BindItem(output, SiwcAuthorizationAttempt.Required(e, "item_id"));
                    if (authorities.Contains("response.output_item.done:" + output) || authorities.Contains("response.content_part.done:" + output + ":" + ci)
                        || !authorities.Add(type + ":" + output + ":" + ci + ":" + Index(e, "annotation_index")))
                        throw new SiwcProtocolException("siwc_invalid_annotation_order");
                }
                if (type is "response.output_text.delta" or "response.output_text.done")
                {
                    var output = Index(e, "output_index"); var contentIndex = Index(e, "content_index"); var key = (output, contentIndex);
                    if (strict is not null && (authorities.Contains("response.output_item.done:" + output) || authorities.Contains("response.content_part.done:" + output + ":" + contentIndex)))
                        throw new SiwcProtocolException("siwc_event_after_part_done");
                    BindItem(output, SiwcAuthorizationAttempt.Required(e, "item_id"));
                    textEvents++;
                    if (!parts.TryGetValue(key, out var text)) parts[key] = text = new StringBuilder();
                    if (type.EndsWith(".delta", StringComparison.Ordinal))
                    {
                        if (finishedText.Contains(key)) throw new SiwcProtocolException("siwc_delta_after_text_done");
                        text.Append(e.GetProperty("delta").GetString());
                        deltas++;
                    }
                    else
                    {
                        if (strict is not null && finishedText.Contains(key)) throw new SiwcProtocolException("siwc_duplicate_authoritative_event");
                        var done = e.GetProperty("text").GetString() ?? "";
                        if (text.Length == 0 && !finishedText.Contains(key)) text.Append(done);
                        if (text.ToString() != done) throw new SiwcProtocolException("siwc_text_snapshot_conflict");
                        finishedText.Add(key);
                    }
                    if (parts.Values.Sum(p => p.Length) > MaxText) throw new SiwcProtocolException("siwc_text_size_limit");
                }
                if (type == "response.completed")
                {
                    stage = "Completion";
                    if (strict is not null && textEvents == 0) throw new SiwcProtocolException("siwc_text_events_missing");
                    var response = e.GetProperty("response"); SiwcIdToken.Unique(response);
                    if (SiwcAuthorizationAttempt.Required(response, "id") != responseId || response.GetProperty("status").GetString() != "completed")
                        throw new SiwcProtocolException("siwc_completed_identity_or_status_mismatch");
                    if (expectedModel is not null && (!response.TryGetProperty("model", out var model) || model.GetString() != expectedModel))
                        throw new SiwcProtocolException("siwc_response_model_mismatch");
                    if (response.TryGetProperty("error", out var failure) && failure.ValueKind != JsonValueKind.Null
                        || response.TryGetProperty("incomplete_details", out var incomplete) && incomplete.ValueKind != JsonValueKind.Null)
                        throw new SiwcProtocolException("siwc_completed_contains_failure");
                    var final = new SortedDictionary<(int, int), string>(); var outputIndex = 0;
                    foreach (var item in response.GetProperty("output").EnumerateArray())
                    {
                        SiwcIdToken.Unique(item);
                        var itemType = item.GetProperty("type").GetString();
                        if (itemType == "reasoning")
                        {
                            if (strict is not null) { ValidateReasoningItem(item); BindItem(outputIndex, SiwcAuthorizationAttempt.Required(item, "id")); }
                            outputIndex++; continue;
                        }
                        if (itemType != "message" || item.GetProperty("role").GetString() != "assistant" || item.GetProperty("status").GetString() != "completed")
                            throw new SiwcProtocolException("siwc_unexpected_tool_or_incomplete_output");
                        BindItem(outputIndex, SiwcAuthorizationAttempt.Required(item, "id")); var ci = 0;
                        foreach (var content in item.GetProperty("content").EnumerateArray())
                        {
                            SiwcIdToken.Unique(content);
                            if (content.GetProperty("type").GetString() != "output_text") throw new SiwcProtocolException("siwc_non_text_output");
                            if (strict is not null && content.GetProperty("text").ValueKind != JsonValueKind.String)
                                throw new SiwcProtocolException("siwc_event_schema_invalid");
                            final[(outputIndex, ci++)] = content.GetProperty("text").GetString() ?? "";
                        }
                        outputIndex++;
                    }
                    if (parts.Any(p => !final.TryGetValue(p.Key, out var text) || text != p.Value.ToString()))
                        throw new SiwcProtocolException("siwc_completed_snapshot_conflict");
                    if (strict is not null && final.Keys.Any(k => !parts.ContainsKey(k))) throw new SiwcProtocolException("siwc_text_events_missing");
                    if (strict is not null && (partSnapshots.Any(p => !final.TryGetValue(p.Key, out var text) || text != p.Value)
                        || itemSnapshots.Any(p => !final.Where(f => f.Key.Item1 == p.Key).Select(f => f.Value).SequenceEqual(p.Value))))
                        throw new SiwcProtocolException("siwc_authoritative_snapshot_conflict");
                    var body = string.Concat(final.Values);
                    if (string.IsNullOrWhiteSpace(body) || body.Length > MaxText || body.Contains("::chatgpt-content-reference", StringComparison.Ordinal)
                        || WorkflowText.HasSecret(body)) throw new SiwcProtocolException("siwc_invalid_text_body");
                    SiwcTokenUsage? usage = null;
                    if (response.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
                    {
                        long? Count(string name) => u.TryGetProperty(name, out var n) && n.ValueKind == JsonValueKind.Number
                            && n.TryGetInt64(out var number) && number >= 0 ? number : null;
                        usage = new(Count("input_tokens"), Count("output_tokens"), Count("total_tokens"));
                    }
                    if (strict is not null && body != strict.ExpectedText) throw new SiwcProtocolException("siwc_body_mismatch");
                    var result = new SiwcTextCompletion(responseId, body) { Usage = usage, TextDeltaEvents = deltas };
                    if (strict is null) return result;
                    completed = result; // Strict candidates must also reject trailing conflicting events and incomplete framing.
                }
            }
        }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or InvalidOperationException
            or KeyNotFoundException or FormatException or DecoderFallbackException or OperationCanceledException)
        {
            SiwcResponseEvidence.AttachRejection(error, rejectionContext, events, stage);
            throw;
        }
        void BindItem(int index, string item)
        {
            if (item.Length is <= 0 or > 128 || itemIds.TryGetValue(index, out var old) && old != item
                || itemIds.Any(p => p.Key != index && p.Value == item)) throw new SiwcProtocolException("siwc_output_item_mismatch");
            itemIds[index] = item;
        }
    }
    static void ValidateStrictEvent(JsonElement e, string type)
    {
        string Text(JsonElement value, string name)
            => value.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString()! : throw new SiwcProtocolException("siwc_event_schema_invalid");
        JsonElement Object(JsonElement value, string name)
        {
            if (!value.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Object) throw new SiwcProtocolException("siwc_event_schema_invalid");
            SiwcIdToken.Unique(p); return p;
        }
        void Array(JsonElement value, string name)
        { if (!value.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Array) throw new SiwcProtocolException("siwc_event_schema_invalid"); }
        void ItemReference(string contentIndex)
        {
            Index(e, "output_index"); Index(e, contentIndex);
            if (string.IsNullOrWhiteSpace(Text(e, "item_id"))) throw new SiwcProtocolException("siwc_event_schema_invalid");
        }
        void OutputItem(JsonElement item, bool requireCompleted)
        {
            SiwcIdToken.Unique(item);
            if (string.IsNullOrWhiteSpace(Text(item, "id"))) throw new SiwcProtocolException("siwc_event_schema_invalid");
            var itemType = Text(item, "type");
            if (itemType == "reasoning") { ValidateReasoningItem(item); return; }
            if (itemType != "message") throw new SiwcProtocolException("siwc_unexpected_tool_output");
            var itemStatus = Text(item, "status");
            if (Text(item, "role") != "assistant" || (requireCompleted ? itemStatus != "completed" : itemStatus is not ("in_progress" or "completed")))
                throw new SiwcProtocolException("siwc_event_schema_invalid");
            Array(item, "content");
            foreach (var content in item.GetProperty("content").EnumerateArray())
            {
                SiwcIdToken.Unique(content);
                if (Text(content, "type") != "output_text") throw new SiwcProtocolException("siwc_non_text_output");
                Text(content, "text");
            }
        }
        switch (type)
        {
            case "response.created": case "response.in_progress": case "response.completed":
                var response = Object(e, "response");
                if (Text(response, "object") != "response" || !ValidResponseId(Text(response, "id")) || string.IsNullOrWhiteSpace(Text(response, "model")))
                    throw new SiwcProtocolException("siwc_event_schema_invalid");
                var status = Text(response, "status");
                if (type != "response.completed" && status is not ("in_progress" or "queued")) throw new SiwcProtocolException("siwc_event_schema_invalid");
                Array(response, "output");
                foreach (var output in response.GetProperty("output").EnumerateArray()) OutputItem(output, type == "response.completed");
                if (response.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null
                    || response.TryGetProperty("incomplete_details", out var details) && details.ValueKind != JsonValueKind.Null)
                    throw new SiwcProtocolException("siwc_completed_contains_failure");
                break;
            case "response.output_text.delta": case "response.output_text.done":
                ItemReference("content_index"); Text(e, type.EndsWith(".delta", StringComparison.Ordinal) ? "delta" : "text"); break;
            case "response.output_item.added": case "response.output_item.done":
                Index(e, "output_index"); var item = Object(e, "item");
                OutputItem(item, type == "response.output_item.done");
                break;
            case "response.content_part.added": case "response.content_part.done":
                ItemReference("content_index"); var part = Object(e, "part");
                if (Text(part, "type") != "output_text") throw new SiwcProtocolException("siwc_non_text_output");
                Text(part, "text"); break;
            case "response.metadata":
                // Public Codex stream extension: only the no-action subset is understood here.
                // Common sequence/response identity/created/completed checks still run below the schema gate.
                if (e.EnumerateObject().Any(p => p.Name is not ("type" or "response_id" or "sequence_number" or "metadata")))
                    throw new SiwcProtocolException("siwc_event_schema_invalid");
                if (!ValidResponseId(Text(e, "response_id"))) throw new SiwcProtocolException("siwc_response_identity_mismatch");
                var metadata = Object(e, "metadata"); var properties = metadata.EnumerateObject().ToArray();
                if (properties.Length != 0 && !(properties.Length == 1 && properties[0].Name == "openai_verification_recommendation"
                    && properties[0].Value.ValueKind == JsonValueKind.Array && properties[0].Value.GetArrayLength() == 0))
                    throw new SiwcProtocolException("siwc_metadata_requires_handling");
                break; // Never body, completion, quota, permission, routing or a retry/model-switch instruction.
            case "response.output_text.annotation.added":
                ItemReference("content_index"); Index(e, "annotation_index"); var annotation = Object(e, "annotation");
                var annotationType = Text(annotation, "type");
                if (annotationType == "url_citation")
                {
                    Index(annotation, "start_index"); Index(annotation, "end_index"); Text(annotation, "title"); Text(annotation, "url");
                }
                else if (annotationType is "file_citation" or "container_file_citation") Text(annotation, "file_id");
                else throw new SiwcProtocolException("siwc_event_schema_invalid");
                break; // Typed annotation metadata is never assistant body text.
            case "response.reasoning_summary_text.delta": case "response.reasoning_summary_text.done":
                ItemReference("summary_index"); Text(e, type.EndsWith(".delta", StringComparison.Ordinal) ? "delta" : "text"); break;
            case "response.reasoning_summary_part.added": case "response.reasoning_summary_part.done":
                ItemReference("summary_index"); var summary = Object(e, "part");
                if (Text(summary, "type") != "summary_text") throw new SiwcProtocolException("siwc_event_schema_invalid");
                Text(summary, "text"); break;
            case "response.failed": case "response.incomplete": case "error": break; // Always fail in the terminal-error path.
            case "response.refusal.delta": case "response.refusal.done": throw new SiwcProtocolException("siwc_non_text_output");
            default: throw new SiwcProtocolException("siwc_unsupported_event");
        }
    }
    static void ValidateReasoningItem(JsonElement item)
    {
        SiwcIdToken.Unique(item);
        if (!item.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString())
            || !item.TryGetProperty("summary", out var summary) || summary.ValueKind != JsonValueKind.Array)
            throw new SiwcProtocolException("siwc_event_schema_invalid");
        foreach (var part in summary.EnumerateArray())
        {
            SiwcIdToken.Unique(part);
            if (!part.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "summary_text"
                || !part.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
                throw new SiwcProtocolException("siwc_event_schema_invalid");
        }
    }
    static int Index(JsonElement e, string name) => e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var i) && i is >= 0 and < 1000
        ? i : throw new SiwcProtocolException("siwc_output_index_invalid");
    static bool ValidResponseId(string id) => id.StartsWith("resp_", StringComparison.Ordinal) && id.Length is > 5 and <= 128
        && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
}

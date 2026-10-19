using System.Text.Json;

namespace CodexQuotaWidget.Core.ControlCenter;

/// <summary>
/// Product-owned transport invoked by a bound Codex turn. It carries the official projection
/// unchanged; a content reference is not an answer, and the transport never interprets it.
/// </summary>
public interface ICodexGptRelayTransport
{
    Task<GptRelaySubmission> SendFromCodexAsync(WorkflowExecutionRequest request, CancellationToken cancellationToken = default);
    Task<GptRelayObservation> ObserveForCodexAsync(Guid workflowTaskId, Guid requestId, CancellationToken cancellationToken = default);
    Task<GptRelayReply> ReadForCodexAsync(Guid workflowTaskId, Guid requestId, CancellationToken cancellationToken = default);
}

public sealed record GptRelaySubmission(bool Success, string Message, string MessageId);
public sealed record GptRelayObservation(bool Ready, string ThreadStatus, string TurnId, string SourceMessageId, string DiagnosticCode);
public sealed record GptRelayReply(bool Success, GptRelayObservation Observation, JsonElement Payload, bool ContainsContentReference, string Message);

/// <summary>Bounded, side-effect-free projection matching, also used by offline checks.</summary>
public static class CodexGptRelayProjection
{
    public static GptRelayObservation Observe(JsonElement snapshot, string targetThreadId, string marker,
        IReadOnlySet<string> previousTurnIds, string? sentMessageId, string? boundTurnId = null)
    {
        var status = DesktopChatGptWorkflowConnector.ReadThreadStatus(snapshot);
        GptRelayObservation Failure(string code, string turn = "") => new(false, status, turn, "", code);
        if (!DesktopChatGptWorkflowConnector.IsConfiguredTarget(snapshot, targetThreadId))
            return Failure("chatgpt_thread_not_verified");
        var turns = Value(snapshot, "turns");
        if (turns.GetArrayLength() > 10) return Failure("chatgpt_relay_projection_limit");
        var matches = turns.EnumerateArray().Where(turn => Text(turn, "id") is { Length: > 0 } id
            && !previousTurnIds.Contains(id) && Matches(turn, marker, sentMessageId)).ToArray();
        if (matches.Length > 1) return Failure("chatgpt_turn_ambiguous");
        if (matches.Length == 0) return Failure("chatgpt_relay_current_turn_missing");
        var matched = matches[0]; var turnId = Text(matched, "id")!;
        if (turnId.Length > 128) return Failure("chatgpt_relay_turn_id_invalid");
        if (boundTurnId is not null && boundTurnId != turnId) return Failure("chatgpt_relay_turn_changed", turnId);
        if (Text(matched, "status") is "failed" or "interrupted" or "cancelled" or "error")
            return Failure("chatgpt_current_turn_failed", turnId);
        if (!DesktopChatGptWorkflowConnector.HasConfirmedCompletion(snapshot, matched))
            return Failure(status == "unknown" ? "chatgpt_relay_status_unknown" : "chatgpt_relay_waiting", turnId);
        var items = Value(matched, "items");
        if (items.ValueKind != JsonValueKind.Array || !items.EnumerateArray().Any(item => Text(item, "type") == "agentMessage"
            && Text(item, "text") is { Length: > 0 })) return Failure("chatgpt_reply_missing", turnId);
        var referenceId = DesktopChatGptWorkflowConnector.ReadReferenceSourceMessageId(matched);
        var messageIds = items.EnumerateArray().Where(item => Text(item, "type") == "agentMessage")
            .Select(item => Text(item, "id")).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToArray();
        return new(true, status, turnId, referenceId.Length > 0 ? referenceId : messageIds.Length == 1 ? messageIds[0]! : "",
            "chatgpt_relay_projection_ready");
    }

    public static GptRelayReply Read(JsonElement snapshot, string targetThreadId, Guid workflowTaskId, Guid requestId,
        string marker, IReadOnlySet<string> previousTurnIds, string? sentMessageId, string? boundTurnId = null)
    {
        var observation = Observe(snapshot, targetThreadId, marker, previousTurnIds, sentMessageId, boundTurnId);
        if (!observation.Ready) return new(false, observation, default, false, "对应 GPT 回合尚未确认完成；未提供其他会话内容。");
        var turn = Value(snapshot, "turns").EnumerateArray().Single(item => Text(item, "id") == observation.TurnId);
        var items = Value(turn, "items");
        if (items.EnumerateArray().Any(item => Text(item, "type") == "agentMessage" && Value(item, "truncated").ValueKind == JsonValueKind.True))
            return new(false, observation with { Ready = false, DiagnosticCode = "chatgpt_reply_truncated" }, default, false, "对应 GPT 回复被截断；不能确认完整结果。");
        if (turn.GetRawText().Length > 131072)
            return new(false, observation with { Ready = false, DiagnosticCode = "chatgpt_relay_payload_limit" }, default, false, "对应 GPT 回合超过安全长度；未提供不完整结果。");
        var references = items.EnumerateArray().Any(item => Text(item, "type") == "agentMessage"
            && Text(item, "text")?.Contains("::chatgpt-content-reference{", StringComparison.Ordinal) == true);
        var payload = JsonSerializer.SerializeToElement(new
        {
            WorkflowTaskId = workflowTaskId.ToString("D"), RequestId = requestId.ToString("D"),
            TargetThreadId = targetThreadId, ThreadStatus = observation.ThreadStatus,
            TurnId = observation.TurnId, SourceMessageId = observation.SourceMessageId,
            EvidenceSource = "official_desktop_projection", ContainsContentReference = references,
            Turn = turn.Clone()
        });
        return new(true, observation, payload, references, references
            ? "官方投影已提供；其中正文引用仍是引用，请 Codex 判断能否可靠读取，不能把引用当作结果。"
            : "已提供唯一对应 GPT 回合的原始官方投影；由 Codex 读取结果。");
    }

    static bool Matches(JsonElement turn, string marker, string? messageId)
    {
        var items = Value(turn, "items");
        if (items.ValueKind != JsonValueKind.Array) return false;
        foreach (var item in items.EnumerateArray())
        {
            if (Text(item, "type") != "userMessage" || messageId is not null && Text(item, "id") != messageId) continue;
            var content = Value(item, "content");
            if (content.ValueKind == JsonValueKind.Array && content.EnumerateArray().Any(piece => Text(piece, "type") == "text"
                && Text(piece, "text") is { } text && text.StartsWith(marker + "\n", StringComparison.Ordinal))) return true;
        }
        return false;
    }
    static JsonElement Value(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var found) ? found : default;
    static string? Text(JsonElement value, string name) => Value(value, name) is { ValueKind: JsonValueKind.String } found ? found.GetString() : null;
}

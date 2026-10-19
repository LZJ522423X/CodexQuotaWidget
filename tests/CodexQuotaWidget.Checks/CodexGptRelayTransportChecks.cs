using System.Text.Json;
using CodexQuotaWidget.Core.ControlCenter;

/// <summary>Only synthetic official projections. No network, model request, file writes or desktop actions.</summary>
public static class CodexGptRelayTransportChecks
{
    public static int Run()
    {
        var count = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException("Codex GPT relay: " + label); count++; }
        const string target = "91fa9874-3b41-49a2-830f-9f1b16e9c935";
        const string source = "ed03f2f5-8491-4e39-8a54-167fb1d01c78";
        var task = Guid.NewGuid(); var request = Guid.NewGuid();
        var marker = DesktopChatGptWorkflowConnector.BuildRequestMarker(new(request, WorkflowProvider.ChatGPT, "测试") { WorkflowTaskId = task });
        var prior = new HashSet<string>(StringComparer.Ordinal) { "old-turn" };
        JsonElement Turn(string id = "this-turn", string status = "completed", string text = "3", string? otherMarker = null,
            string messageId = "sent-message", bool truncated = false)
            => JsonSerializer.SerializeToElement(new { id, status, items = new object[]
            {
                new { type = "userMessage", id = messageId, content = new[] { new { type = "text", text = (otherMarker ?? marker) + "\n2+1" } } },
                new { type = "agentMessage", id = "reply-message", text, truncated }
            } });
        JsonElement Snapshot(string? status = "idle", JsonElement[]? turns = null, string id = target)
            => JsonSerializer.SerializeToElement(new { thread = new { id, kind = "chatgpt", status = status is null ? null : new { type = status } }, turns = turns ?? [Turn()] });
        GptRelayObservation Observe(JsonElement snapshot, string? bound = null)
            => CodexGptRelayProjection.Observe(snapshot, target, marker, prior, "sent-message", bound);
        GptRelayReply Read(JsonElement snapshot, string? bound = null)
            => CodexGptRelayProjection.Read(snapshot, target, task, request, marker, prior, "sent-message", bound);

        Check(Observe(Snapshot()).Ready, "idle matched completed turn with agent projection is ready");
        Check(Observe(Snapshot()).SourceMessageId == "reply-message", "ordinary message identity retained without interpreting answer");
        Check(!Observe(Snapshot("running")).Ready, "running thread overrides completed projection");
        Check(!Observe(Snapshot(null)).Ready && Observe(Snapshot(null)).DiagnosticCode == "chatgpt_relay_status_unknown", "missing thread state stays unknown");
        Check(!Observe(Snapshot("unexpected")).Ready, "unknown discriminated status is not idle");
        Check(!Observe(Snapshot(turns: [Turn(status: "running")])).Ready, "incomplete turn cannot be ready");
        Check(Observe(Snapshot(turns: [Turn(status: "failed")])).DiagnosticCode == "chatgpt_current_turn_failed", "failed turn rejected");
        Check(!Observe(Snapshot(turns: [Turn(otherMarker: "[AIControlCenter task=other request=other]")])).Ready, "foreign task and request never match");
        Check(!Observe(Snapshot(turns: [Turn(messageId: "someone-else")])).Ready, "wrong sent-message identity rejected");
        Check(!Observe(Snapshot(turns: [Turn(id: "old-turn")])).Ready, "pre-existing turn excluded");
        Check(Observe(Snapshot(turns: [Turn(), Turn(id: "duplicate")])).DiagnosticCode == "chatgpt_turn_ambiguous", "two matching projections never chosen arbitrarily");
        Check(!Observe(Snapshot(id: source)).Ready, "different GPT thread rejected");
        Check(!Observe(Snapshot(turns: [])).Ready, "no turn remains unknown");
        Check(Observe(Snapshot(), "another-turn").DiagnosticCode == "chatgpt_relay_turn_changed", "bound turn cannot silently change");
        Check(!Observe(Snapshot(turns: [Turn(text: "")])).Ready, "empty agent projection rejected");
        Check(!Read(Snapshot("running")).Success, "read repeats completion checks");
        var plain = Read(Snapshot());
        Check(plain.Success && !plain.ContainsContentReference, "ordinary original projection readable by bound Codex");
        Check(plain.Payload.GetProperty("WorkflowTaskId").GetString() == task.ToString("D")
            && plain.Payload.GetProperty("RequestId").GetString() == request.ToString("D"), "export keeps task and request identities");
        Check(plain.Payload.GetProperty("Turn").GetProperty("items")[1].GetProperty("text").GetString() == "3", "agent text passes through unchanged");
        var reference = "::chatgpt-content-reference{source_message_id=\"" + source + "\" index=0}";
        var referenced = Read(Snapshot(turns: [Turn(text: reference)]));
        Check(referenced.Success && referenced.ContainsContentReference && referenced.Observation.Ready, "finished official reference remains a projection, not a fabricated result");
        Check(referenced.Payload.GetProperty("Turn").GetProperty("items")[1].GetProperty("text").GetString() == reference,
            "reference passed to Codex exactly with no private fetching");
        Check(referenced.Observation.SourceMessageId == source, "reference source identity is evidence only");
        Check(!Read(Snapshot(turns: [Turn(truncated: true)])).Success, "truncated projection cannot be used as complete body");
        Check(!Read(Snapshot(), "another-turn").Success, "read cannot substitute another bound turn");
        var literal = "NOT_A_NUMBER; never interpret here";
        Check(Read(Snapshot(turns: [Turn(text: literal)])).Payload.GetProperty("Turn").GetProperty("items")[1].GetProperty("text").GetString() == literal,
            "transport does not calculate or normalize GPT output");
        Check(!CodexGptRelayProjection.Observe(Snapshot(turns: [Turn(), Turn(id: "old-turn")]), target, marker, prior, "sent-message").DiagnosticCode.Contains("ambiguous"),
            "old projection cannot create false ambiguity");
        return count;
    }
}

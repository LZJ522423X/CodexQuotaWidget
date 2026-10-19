using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexQuotaWidget.Core.ControlCenter;

public sealed record CodexWorkflowSessionEvidence(string ThreadId, string SessionId, string TurnId,
    CodexWorkflowRunState State, bool SafeToContinue, string DiagnosticCode);

/// <summary>Typed, read-only app-server evidence. It grants neither ownership nor execution authorization.</summary>
public static class CodexWorkflowConnectionValidator
{
    public static void ValidateWorkflowOwner(Guid recordedTaskId, Guid requestedTaskId, bool allowLegacyRequest = false)
    {
        // Legacy compatibility is limited to a genuinely unbound legacy owner/request pair.
        // Neither a workflow request nor an empty-task request may adopt another workflow.
        if (requestedTaskId == Guid.Empty && recordedTaskId == Guid.Empty && allowLegacyRequest) return;
        if (requestedTaskId == Guid.Empty) throw new WorkflowRpcException("workflow_task_identity_missing");
        if (recordedTaskId == Guid.Empty || recordedTaskId != requestedTaskId)
            throw new WorkflowRpcException("foreign_workflow_continuation_rejected");
    }

    public static void ValidateSessionSelection(Guid workflowTaskId, string? continuationId, IReadOnlyDictionary<string, Guid> ownedWorkflowTasks)
    {
        if (workflowTaskId == Guid.Empty) throw new WorkflowRpcException("workflow_task_identity_missing");
        if (continuationId is null)
        {
            if (ownedWorkflowTasks.Values.Contains(workflowTaskId)) throw new WorkflowRpcException("session_continuation_required");
            return;
        }
        if (!CodexWorkflowConnector.ValidIdentifier(continuationId)) throw new WorkflowRpcException("session_identity_missing");
        if (!ownedWorkflowTasks.TryGetValue(continuationId, out var owner)) throw new WorkflowRpcException("foreign_continuation_rejected");
        ValidateWorkflowOwner(owner, workflowTaskId);
    }

    public static string AccountIdentity(JsonElement response)
    {
        var account = CodexWorkflowConnector.Get(response, "account");
        if (CodexWorkflowConnector.Text(account, "type") != "chatgpt") throw new WorkflowRpcException("chatgpt_sign_in_required");
        // account/read is not a credential export endpoint. Fail closed if an unexpected secret field appears.
        if (account.EnumerateObject().Any(field => field.Name is "accessToken" or "refreshToken" or "idToken" or "apiKey" or "token"))
            throw new WorkflowRpcException("unexpected_account_credentials");
        var email = CodexWorkflowConnector.Text(account, "email") ?? "";
        if (string.IsNullOrWhiteSpace(email)) throw new WorkflowRpcException("account_identity_unavailable");
        if (email.Length > 320 || email.Any(char.IsControl)) throw new WorkflowRpcException("account_identity_invalid");
        // Keep an opaque in-memory identity only; never log or persist account details or authentication data.
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("chatgpt\n" + email.Trim().ToLowerInvariant())));
    }

    public static string SelectModel(JsonElement response, string? requestedModel)
    {
        var models = CodexWorkflowConnector.Get(response, "data");
        if (models.ValueKind != JsonValueKind.Array || models.GetArrayLength() > 100) throw new WorkflowRpcException("model_catalog_invalid");
        var choices = models.EnumerateArray().Where(item => CodexWorkflowConnector.Get(item, "hidden").ValueKind != JsonValueKind.True
            && (requestedModel is null ? CodexWorkflowConnector.Get(item, "isDefault").ValueKind == JsonValueKind.True
                : CodexWorkflowConnector.Text(item, "model") == requestedModel)).ToArray();
        if (choices.Length != 1 || !CodexWorkflowConnector.ValidIdentifier(CodexWorkflowConnector.Text(choices[0], "model")))
            throw new WorkflowRpcException("model_not_unambiguously_available");
        return CodexWorkflowConnector.Text(choices[0], "model")!;
    }

    public static CodexWorkflowSessionEvidence ReadOwnedThread(JsonElement response, string projectRoot, string ownedThreadId, string expectedTurnId, string expectedSessionId = "")
    {
        if (!CodexWorkflowConnector.ValidIdentifier(ownedThreadId) || expectedTurnId.Length > 0 && !CodexWorkflowConnector.ValidIdentifier(expectedTurnId))
            throw new WorkflowRpcException("owned_binding_invalid");
        ValidateThreadIdentity(response, projectRoot, ownedThreadId, expectedSessionId);
        var thread = CodexWorkflowConnector.Get(response, "thread");
        var session = CodexWorkflowConnector.Text(thread, "sessionId") ?? "";
        var status = CodexWorkflowConnector.Text(CodexWorkflowConnector.Get(thread, "status"), "type");
        if (status == "active") return new(ownedThreadId, session, expectedTurnId, CodexWorkflowRunState.Unknown, false, "owned_thread_still_active");
        if (status == "systemError") return new(ownedThreadId, session, expectedTurnId, CodexWorkflowRunState.Unknown, false, "owned_thread_system_error");
        if (status is not ("idle" or "notLoaded")) return new(ownedThreadId, session, expectedTurnId, CodexWorkflowRunState.Unknown, false, "owned_thread_idle_not_verified");
        var turns = CodexWorkflowConnector.Get(thread, "turns");
        if (turns.ValueKind != JsonValueKind.Array || turns.GetArrayLength() > 2048) throw new WorkflowRpcException("read_thread_turns_invalid");
        if (turns.GetArrayLength() == 0)
            return new(ownedThreadId, session, "", CodexWorkflowRunState.Unknown, expectedTurnId.Length == 0,
                expectedTurnId.Length == 0 ? "owned_thread_no_turn" : "owned_turn_not_found");
        var lastTurn = turns[turns.GetArrayLength() - 1];
        var lastId = CodexWorkflowConnector.Text(lastTurn, "id");
        if (expectedTurnId.Length == 0 || lastId != expectedTurnId)
            return new(ownedThreadId, session, "", CodexWorkflowRunState.Unknown, false, "owned_latest_turn_not_confirmed");
        var terminal = CodexWorkflowConnector.Text(lastTurn, "status");
        var state = terminal switch
        {
            "completed" => CodexWorkflowRunState.Completed,
            "interrupted" => CodexWorkflowRunState.Interrupted,
            "failed" => IsUsageLimit(lastTurn) ? CodexWorkflowRunState.Exhausted : CodexWorkflowRunState.Failed,
            _ => CodexWorkflowRunState.Unknown
        };
        // Exhausted is a confirmed stopped turn, not a broken connection. The live quota guard
        // independently blocks dispatch; a historical quota error must not poison tomorrow's connection.
        return new(ownedThreadId, session, expectedTurnId, state, state != CodexWorkflowRunState.Unknown,
            state == CodexWorkflowRunState.Unknown ? "owned_turn_not_terminal" : state == CodexWorkflowRunState.Exhausted ? "owned_terminal_usage_limit" : "owned_terminal_verified");
    }
    public static void ValidateThreadIdentity(JsonElement response, string projectRoot, string expectedThreadId, string expectedSessionId = "")
    {
        if (!CodexWorkflowConnector.ValidIdentifier(expectedThreadId)) throw new WorkflowRpcException("thread_identity_invalid");
        var thread = CodexWorkflowConnector.Get(response, "thread");
        if (CodexWorkflowConnector.Text(thread, "id") != expectedThreadId) throw new WorkflowRpcException("read_thread_identity_mismatch");
        if (!SamePath(CodexWorkflowConnector.Text(thread, "cwd"), projectRoot)) throw new WorkflowRpcException("read_thread_cwd_mismatch");
        var session = CodexWorkflowConnector.Text(thread, "sessionId") ?? "";
        if (session.Length > 0 && !CodexWorkflowConnector.ValidIdentifier(session)) throw new WorkflowRpcException("session_identity_invalid");
        if (expectedSessionId.Length > 0 && session != expectedSessionId) throw new WorkflowRpcException("read_session_identity_mismatch");
    }
    static bool IsUsageLimit(JsonElement turn)
    {
        var info = CodexWorkflowConnector.Get(CodexWorkflowConnector.Get(turn, "error"), "codexErrorInfo");
        return info.ValueKind == JsonValueKind.String && info.GetString() == "usageLimitExceeded"
            || info.ValueKind == JsonValueKind.Object && info.EnumerateObject().Any(field => field.Name == "usageLimitExceeded");
    }
    static bool SamePath(string? first, string second)
    {
        try { return first is not null && Path.GetFullPath(first).TrimEnd('\\').Equals(Path.GetFullPath(second).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Reflection;
using CodexQuotaWidget.Core.ControlCenter;

/// <summary>Synthetic typed app-server lifecycle checks; no process, inference or system actions.</summary>
public static class CodexWorkflowConnectorChecks
{
    public static async Task<int> Run()
    {
        var count = 0;
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException("Codex workflow connector: " + name); count++; }
        static JsonElement Notification(string method, object parameters) => JsonSerializer.SerializeToElement(new { method, @params = parameters });
        static object Message(string id, string text, string? phase = "final_answer") => new { id, type = "agentMessage", text, phase };
        static JsonElement Item(string thread, string turn, object item) => Notification("item/completed", new { threadId = thread, turnId = turn, item });
        static JsonElement Terminal(string thread, string turn, string status, object[]? items = null, object? error = null)
            => Notification("turn/completed", new { threadId = thread, turn = new { id = turn, status, items = items ?? Array.Empty<object>(), error } });
        var capture = new CodexWorkflowTurnCapture("owned-thread");
        Check(capture.State == CodexWorkflowRunState.Unknown && !capture.HasTerminal && !capture.Completion.IsCompleted,
            "no process/message inference before an actual bound lifecycle");
        capture.Accept(Notification("turn/started", new { threadId = "owned-thread", turn = new { id = "owned-turn", status = "inProgress" } }));
        capture.Accept(Item("owned-thread", "owned-turn", Message("report-1", "actual fixture report")));
        Check(capture.State == CodexWorkflowRunState.Unknown && !capture.Completion.IsCompleted, "early notifications wait for returned turn identity");
        capture.BindTurn("owned-turn");
        Check(capture.State == CodexWorkflowRunState.Working && !capture.HasTerminal, "exact early started notification is replayed only after bind");
        Check(!capture.Completion.IsCompleted, "assistant report does not prove turn completion");
        capture.Accept(Terminal("foreign-thread", "owned-turn", "completed"));
        capture.Accept(Terminal("owned-thread", "foreign-turn", "completed"));
        Check(!capture.HasTerminal && !capture.Completion.IsCompleted, "foreign thread and foreign turn completions cannot stop owned execution");
        capture.Accept(Terminal("owned-thread", "owned-turn", "completed"));
        var result = await capture.Completion;
        Check(result.Success && result.Message == "actual fixture report" && result.ContinuationId == "owned-thread",
            "matching completed terminal plus own final report succeeds");
        Check(capture.HasTerminal && capture.State == CodexWorkflowRunState.Completed, "completed state retains actual terminal evidence");
        capture.Accept(Terminal("owned-thread", "owned-turn", "failed"));
        Check((await capture.Completion) == result && capture.State == CodexWorkflowRunState.Completed, "duplicate or conflicting post-terminal notifications do not rewrite committed result");

        var failed = new CodexWorkflowTurnCapture("thread-failed"); failed.BindTurn("turn-failed");
        failed.Accept(Item("thread-failed", "turn-failed", Message("report-f", "looks complete but failed")));
        failed.Accept(Terminal("thread-failed", "turn-failed", "failed"));
        Check(!(await failed.Completion).Success && failed.State == CodexWorkflowRunState.Failed && failed.HasTerminal,
            "assistant output does not turn failed lifecycle into success");
        var interrupted = new CodexWorkflowTurnCapture("thread-interrupted"); interrupted.BindTurn("turn-interrupted");
        interrupted.Accept(Terminal("thread-interrupted", "turn-interrupted", "interrupted", new[] { Message("report-i", "partial") }));
        Check(!(await interrupted.Completion).Success && interrupted.State == CodexWorkflowRunState.Interrupted && interrupted.HasTerminal,
            "interrupted is distinct from completed and has a real terminal");
        var exhausted = new CodexWorkflowTurnCapture("thread-exhausted"); exhausted.BindTurn("turn-exhausted");
        exhausted.Accept(Notification("error", new { threadId = "thread-exhausted", turnId = "turn-exhausted", willRetry = false,
            error = new { codexErrorInfo = "usageLimitExceeded" } }));
        Check(exhausted.State == CodexWorkflowRunState.Exhausted && !exhausted.HasTerminal && !exhausted.Completion.IsCompleted,
            "typed quota error does not assert turn stopped");
        exhausted.Accept(Terminal("thread-exhausted", "turn-exhausted", "failed", error: new { codexErrorInfo = "usageLimitExceeded" }));
        Check(!(await exhausted.Completion).Success && exhausted.State == CodexWorkflowRunState.Exhausted && exhausted.HasTerminal,
            "typed failed usage-limit terminal retains exhausted reason");
        var generic429 = new CodexWorkflowTurnCapture("thread-429"); generic429.BindTurn("turn-429");
        generic429.Accept(Terminal("thread-429", "turn-429", "failed", error: new { message = "429", codexErrorInfo = "rateLimitExceeded" }));
        Check(generic429.State == CodexWorkflowRunState.Failed && !(await generic429.Completion).Success,
            "generic rate limiting is not forged as five-hour quota exhaustion");
        var sessionBudget = new CodexWorkflowTurnCapture("thread-budget"); sessionBudget.BindTurn("turn-budget");
        sessionBudget.Accept(Terminal("thread-budget", "turn-budget", "failed", error: new { codexErrorInfo = "sessionBudgetExceeded" }));
        Check(sessionBudget.State == CodexWorkflowRunState.Failed, "a session budget is not confused with official usage quota exhaustion");

        var noReport = new CodexWorkflowTurnCapture("thread-empty"); noReport.BindTurn("turn-empty");
        noReport.Accept(Item("thread-empty", "turn-empty", Message("comment", "still working", "commentary")));
        noReport.Accept(Terminal("thread-empty", "turn-empty", "completed"));
        Check(!(await noReport.Completion).Success && noReport.State == CodexWorkflowRunState.Completed,
            "completed without final report is actual terminal with unusable execution result");
        var unknownPhase = new CodexWorkflowTurnCapture("thread-phase"); unknownPhase.BindTurn("turn-phase");
        unknownPhase.Accept(Terminal("thread-phase", "turn-phase", "completed", new[] { Message("unknown-phase", "not final", "other") }));
        Check(!(await unknownPhase.Completion).Success, "unknown assistant phase is not substituted for a final report");
        var embedded = new CodexWorkflowTurnCapture("thread-embedded"); embedded.BindTurn("turn-embedded");
        embedded.Accept(Terminal("thread-embedded", "turn-embedded", "completed", new[] { Message("report-e", "complete embedded report") }));
        Check((await embedded.Completion).Success && (await embedded.Completion).Message == "complete embedded report",
            "final terminal items can supply own full final report without deltas");
        var stream = new CodexWorkflowTurnCapture("thread-stream"); stream.BindTurn("turn-stream");
        stream.Accept(Notification("item/agentMessage/delta", new { threadId = "thread-stream", turnId = "turn-stream", delta = "partial" }));
        stream.Accept(Terminal("thread-stream", "turn-stream", "completed"));
        Check(!(await stream.Completion).Success, "delta text is not accepted as a full completed report");

        var detached = new CodexWorkflowTurnCapture("thread-disconnected"); detached.BindTurn("turn-disconnected");
        detached.Accept(Item("thread-disconnected", "turn-disconnected", Message("report-d", "has output")));
        detached.Disconnect("fixture disconnect");
        Check(!(await detached.Completion).Success && detached.State == CodexWorkflowRunState.Unknown && !detached.HasTerminal,
            "connection exit with output stays Unknown and fails rather than fabricating a terminal");
        var unknownStatus = new CodexWorkflowTurnCapture("thread-invalid"); unknownStatus.BindTurn("turn-invalid");
        unknownStatus.Accept(Terminal("thread-invalid", "turn-invalid", "inProgress"));
        Check(!(await unknownStatus.Completion).Success && !unknownStatus.HasTerminal && unknownStatus.State == CodexWorkflowRunState.Unknown,
            "malformed completed notification with non-terminal status fails closed");
        var waiting = new CodexWorkflowTurnCapture("thread-waiting"); waiting.BindTurn("turn-waiting"); waiting.MarkWaiting();
        Check(waiting.State == CodexWorkflowRunState.Waiting && !waiting.Completion.IsCompleted && !waiting.HasTerminal,
            "approval wait is not successful completion or a stopped task");

        var conflict = new CodexWorkflowTurnCapture("thread-conflict"); conflict.BindTurn("turn-conflict");
        conflict.Accept(Item("thread-conflict", "turn-conflict", Message("same-report", "first")));
        conflict.Accept(Item("thread-conflict", "turn-conflict", Message("same-report", "different")));
        Check(!(await conflict.Completion).Success && !conflict.HasTerminal, "same report identity changing full contents fails closed");
        var huge = new CodexWorkflowTurnCapture("thread-huge"); huge.BindTurn("turn-huge");
        huge.Accept(Item("thread-huge", "turn-huge", Message("huge-report", new string('z', CodexWorkflowTurnCapture.MaximumReportCharacters + 1))));
        Check(!(await huge.Completion).Success && !huge.HasTerminal, "oversized report is rejected without truncating it into success");
        var earlyOverflow = new CodexWorkflowTurnCapture("thread-overflow");
        for (var index = 0; index <= 64; index++) earlyOverflow.Accept(Item("thread-overflow", "turn-overflow", Message("report-" + index, "fixture")));
        Check(!(await earlyOverflow.Completion).Success && !earlyOverflow.HasTerminal, "pre-response notification queue is bounded");
        var byteOverflow = new CodexWorkflowTurnCapture("thread-bytes");
        byteOverflow.Accept(Item("thread-bytes", "turn-bytes", Message("report-bytes", new string('z', 1024 * 1024))));
        Check(!(await byteOverflow.Completion).Success && !byteOverflow.HasTerminal, "pre-response notification memory is bounded independently of message count");
        var duplicate = new CodexWorkflowTurnCapture("thread-duplicate"); duplicate.BindTurn("turn-duplicate");
        var one = Item("thread-duplicate", "turn-duplicate", Message("one", "same message")); duplicate.Accept(one); duplicate.Accept(one);
        duplicate.Accept(Terminal("thread-duplicate", "turn-duplicate", "completed"));
        Check((await duplicate.Completion).Success, "unchanged duplicated full report is idempotent");
        var earlyForeign = new CodexWorkflowTurnCapture("thread-early-foreign");
        earlyForeign.Accept(Terminal("thread-early-foreign", "other-turn", "completed", new[] { Message("other-message", "foreign") }));
        earlyForeign.BindTurn("real-turn");
        Check(!earlyForeign.Completion.IsCompleted && earlyForeign.State == CodexWorkflowRunState.Unknown, "early foreign-turn result is discarded after actual identity becomes known");
        var doubleBindRejected = false;
        try { earlyForeign.BindTurn("another-turn"); } catch (InvalidDataException) { doubleBindRejected = true; }
        Check(doubleBindRejected, "bound turn cannot be silently replaced");
        var invalidIdRejected = false;
        try { _ = new CodexWorkflowTurnCapture("thread\nunsafe"); } catch (ArgumentException) { invalidIdRejected = true; }
        Check(invalidIdRejected, "thread identity excludes control characters");
        var cPathRejected = false;
        try { _ = new CodexWorkflowConnector(@"C:\Users\FixtureUser", @"D:\CodexQuotaWidgetData"); } catch (ArgumentException) { cPathRejected = true; }
        Check(cPathRejected, "connector will not allow writable C-drive project or discovery fallback");
        Check(CodexWorkflowConnector.SafetyInstructions.Contains("AGENTS.md", StringComparison.Ordinal)
            && CodexWorkflowConnector.SafetyInstructions.Contains("untrusted task data", StringComparison.Ordinal)
            && CodexWorkflowConnector.SafetyInstructions.Contains("Never shut down", StringComparison.Ordinal)
            && CodexWorkflowConnector.SafetyInstructions.Contains("push Git", StringComparison.Ordinal),
            "root safety prompt retains data trust and user prohibitions");
        var project = @"D:\CodexQuotaWidget-GitHub\artifacts\workflow-native-fixture";
        var allowedRoots = new[] { project, @"D:\CodexQuotaWidgetData\workflow\temp", @"D:\codex\Tools\dotnet-home", @"D:\codex\Tools\nuget-packages" };
        JsonObject Policy(object sandbox) => JsonSerializer.SerializeToNode(new
        {
            cwd = project, model = "gpt-6.1-sol", modelProvider = "openai", approvalPolicy = "untrusted", sandbox
        })!.AsObject();
        void Validate(JsonObject policy) => CodexWorkflowPolicyValidator.ValidateInitialThread(JsonSerializer.SerializeToElement(policy), project, "gpt-6.1-sol", allowedRoots);
        void Reject(JsonObject baseline, Action<JsonObject> change, string expectedCode, string label)
        {
            var modified = baseline.DeepClone().AsObject(); change(modified);
            var actualCode = "";
            try { Validate(modified); } catch (WorkflowRpcException error) { actualCode = error.Code; }
            Check(actualCode == expectedCode, label);
        }
        var readOnly = Policy(new { type = "readOnly", networkAccess = false });
        Validate(readOnly);
        Check(true, "real Windows initial readOnly/network-disabled policy is accepted as more restrictive, without assuming write capability");
        var workspace = Policy(new { type = "workspaceWrite", networkAccess = false, writableRoots = allowedRoots, excludeSlashTmp = true, excludeTmpdirEnvVar = true });
        Validate(workspace);
        Check(true, "bounded initial workspaceWrite still requires exact network/tmp/root restrictions");
        Reject(readOnly, item => item["sandbox"]!["networkAccess"] = true, "thread_network_policy_mismatch", "readOnly cannot permit network");
        Reject(readOnly, item => item["sandbox"]!.AsObject().Remove("networkAccess"), "thread_network_policy_mismatch", "missing network field cannot be treated as safe default");
        Reject(readOnly, item => item["sandbox"]!["networkAccess"] = "false", "thread_network_policy_mismatch", "network false must be a typed boolean");
        Reject(readOnly, item => item["approvalPolicy"] = "never", "thread_approval_policy_mismatch", "readOnly does not weaken untrusted approval policy");
        Reject(readOnly, item => item["cwd"] = @"C:\Users\FixtureUser", "thread_cwd_mismatch", "initial policy cannot bind a different C-drive workspace");
        Reject(readOnly, item => item["modelProvider"] = "other", "thread_model_provider_mismatch", "initial policy cannot silently change provider");
        Reject(readOnly, item => item["model"] = "different-model", "thread_model_mismatch", "initial policy cannot silently change selected model");
        Reject(readOnly, item => item["sandbox"]!["type"] = "dangerFullAccess", "thread_sandbox_type_mismatch", "full-access policy remains forbidden");
        Reject(readOnly, item => item["sandbox"]!["type"] = "externalSandbox", "thread_sandbox_type_mismatch", "unverified external sandbox remains forbidden");
        Reject(readOnly, item => item["sandbox"]!["writableRoots"] = JsonSerializer.SerializeToNode(new[] { project }), "thread_writable_roots_mismatch", "readOnly policy with contradictory write grants is rejected");
        Reject(workspace, item => item["sandbox"]!["excludeSlashTmp"] = false, "thread_temp_policy_mismatch", "workspaceWrite cannot regain shared slash-temp writes");
        Reject(workspace, item => item["sandbox"]!.AsObject().Remove("excludeTmpdirEnvVar"), "thread_temp_policy_mismatch", "workspaceWrite requires explicit tmpdir exclusion");
        Reject(workspace, item => item["sandbox"]!["writableRoots"] = JsonSerializer.SerializeToNode(new[] { project, @"C:\Users\FixtureUser" }), "thread_writable_roots_mismatch", "workspaceWrite refuses an extra C-drive root");
        Reject(workspace, item => item["sandbox"]!["writableRoots"] = JsonSerializer.SerializeToNode(new[] { project, @"D:\" }), "thread_writable_roots_mismatch", "workspaceWrite refuses the entire D drive");
        Reject(workspace, item => item["sandbox"]!.AsObject().Remove("writableRoots"), "thread_writable_roots_mismatch", "workspaceWrite cannot omit root validation");
        var accountIdentity = CodexWorkflowConnectionValidator.AccountIdentity(JsonSerializer.SerializeToElement(new { account = new { type = "chatgpt", email = "fixture@example.invalid" } }));
        Check(accountIdentity.Length == 64 && !accountIdentity.Contains("fixture"), "account check retains only an opaque in-memory fingerprint");
        Check(accountIdentity == CodexWorkflowConnectionValidator.AccountIdentity(JsonSerializer.SerializeToElement(new { account = new { type = "chatgpt", email = " FIXTURE@example.invalid " } })),
            "equivalent native account identity is stable without recording credentials");
        var accountRejected = false;
        try { CodexWorkflowConnectionValidator.AccountIdentity(JsonSerializer.SerializeToElement(new { account = new { type = "apiKey" } })); }
        catch (WorkflowRpcException error) { accountRejected = error.Code == "chatgpt_sign_in_required"; }
        Check(accountRejected, "API-key provider cannot become signed-in ChatGPT execution evidence");
        accountRejected = false;
        try { CodexWorkflowConnectionValidator.AccountIdentity(JsonSerializer.SerializeToElement(new { account = new { type = "chatgpt" } })); }
        catch (WorkflowRpcException error) { accountRejected = error.Code == "account_identity_unavailable"; }
        Check(accountRejected, "login mode without a native account identity remains unverified");
        accountRejected = false;
        try { CodexWorkflowConnectionValidator.AccountIdentity(JsonSerializer.SerializeToElement(new { account = new { type = "chatgpt", accessToken = "synthetic-placeholder" } })); }
        catch (WorkflowRpcException error) { accountRejected = error.Code == "unexpected_account_credentials"; }
        Check(accountRejected, "unexpected credential-export fields fail closed without retaining payload");
        JsonElement Catalog(params object[] values) => JsonSerializer.SerializeToElement(new { data = values });
        Check(CodexWorkflowConnectionValidator.SelectModel(Catalog(new { model = "fixture-model", isDefault = true, hidden = false }), null) == "fixture-model",
            "one native default model is required; process existence is not sufficient");
        var ambiguousModel = false;
        try { CodexWorkflowConnectionValidator.SelectModel(Catalog(new { model = "one", isDefault = true }, new { model = "two", isDefault = true }), null); }
        catch (WorkflowRpcException error) { ambiguousModel = error.Code == "model_not_unambiguously_available"; }
        Check(ambiguousModel, "multiple default models cannot be selected randomly");
        JsonObject StoredThread(string turn = "owned-turn", string state = "completed") => JsonSerializer.SerializeToNode(new
        {
            thread = new { id = "owned-thread", sessionId = "owned-session", cwd = project, status = new { type = "notLoaded" },
                turns = new[] { new { id = turn, status = state } } }
        })!.AsObject();
        CodexWorkflowSessionEvidence Read(JsonObject item) => CodexWorkflowConnectionValidator.ReadOwnedThread(JsonSerializer.SerializeToElement(item), project, "owned-thread", "owned-turn");
        var terminalEvidence = Read(StoredThread());
        Check(terminalEvidence.SafeToContinue && terminalEvidence.State == CodexWorkflowRunState.Completed && terminalEvidence.TurnId == "owned-turn",
            "read-only matching native stored terminal can verify an owned interrupted connection");
        Check(terminalEvidence.SessionId == "owned-session" && terminalEvidence.ThreadId == "owned-thread", "session tree identity remains distinct from thread identity");
        Check(!Read(StoredThread("other-turn")).SafeToContinue, "a newer unmatched turn prevents continuation even in an owned thread");
        Check(!Read(StoredThread(state: "inProgress")).SafeToContinue, "an in-progress stored turn cannot be replaced by reconnect");
        var activeThread = StoredThread(); activeThread["thread"]!["status"]!["type"] = "active";
        Check(!Read(activeThread).SafeToContinue, "native active thread status blocks overwrite even if an older turn completed");
        var failedThread = StoredThread(state: "failed");
        Check(Read(failedThread).State == CodexWorkflowRunState.Failed, "a real failed terminal remains failed after reconnect");
        failedThread["thread"]!["turns"]![0]!["error"] = JsonSerializer.SerializeToNode(new { codexErrorInfo = "usageLimitExceeded" });
        Check(Read(failedThread).State == CodexWorkflowRunState.Exhausted && Read(failedThread).SafeToContinue, "confirmed exhausted terminal verifies stop without treating connection readiness as quota dispatch authorization");
        var rejected = false;
        var foreignThread = StoredThread(); foreignThread["thread"]!["id"] = "foreign-thread";
        try { Read(foreignThread); } catch (WorkflowRpcException error) { rejected = error.Code == "read_thread_identity_mismatch"; }
        Check(rejected, "read response cannot substitute a foreign thread identity");
        rejected = false; foreignThread = StoredThread(); foreignThread["thread"]!["cwd"] = @"D:\OtherProject";
        try { Read(foreignThread); } catch (WorkflowRpcException error) { rejected = error.Code == "read_thread_cwd_mismatch"; }
        Check(rejected, "a valid thread ID with a different project is rejected");
        var noTurns = StoredThread(); noTurns["thread"]!["turns"] = new JsonArray();
        Check(!Read(noTurns).SafeToContinue, "missing previously expected turn is not evidence of successful completion");
        var legacy = StoredThread(); legacy["thread"]!.AsObject().Remove("sessionId");
        Check(Read(legacy).SessionId.Length == 0, "absent session tree identity remains unknown instead of being fabricated from thread ID");
        rejected = false;
        try { CodexWorkflowConnectionValidator.ReadOwnedThread(JsonSerializer.SerializeToElement(StoredThread()), project, "owned-thread", "owned-turn", "other-session"); }
        catch (WorkflowRpcException error) { rejected = error.Code == "read_session_identity_mismatch"; }
        Check(rejected, "a session identity change cannot silently retarget an owned thread binding");
        var workflowTask = Guid.NewGuid();
        var priorRequest = new WorkflowExecutionRequest(Guid.NewGuid(), WorkflowProvider.Codex, "prior fixture turn") { WorkflowTaskId = workflowTask };
        var nextRequest = new WorkflowExecutionRequest(Guid.NewGuid(), WorkflowProvider.Codex, "next fixture turn", "owned-thread") { WorkflowTaskId = workflowTask };
        CodexWorkflowConnectionValidator.ValidateWorkflowOwner(priorRequest.WorkflowTaskId, nextRequest.WorkflowTaskId);
        Check(priorRequest.RequestId != nextRequest.RequestId && priorRequest.WorkflowTaskId == nextRequest.WorkflowTaskId,
            "stable workflow identity permits new per-turn RequestIds on its own thread");
        rejected = false;
        try { CodexWorkflowConnectionValidator.ValidateWorkflowOwner(workflowTask, Guid.NewGuid()); }
        catch (WorkflowRpcException error) { rejected = error.Code == "foreign_workflow_continuation_rejected"; }
        Check(rejected, "a different workflow cannot continue another owned workflow's thread");
        rejected = false;
        try { CodexWorkflowConnectionValidator.ValidateWorkflowOwner(Guid.Empty, workflowTask); }
        catch (WorkflowRpcException error) { rejected = error.Code == "foreign_workflow_continuation_rejected"; }
        Check(rejected, "a new workflow cannot adopt a legacy thread without stable workflow ownership");
        rejected = false;
        try { CodexWorkflowConnectionValidator.ValidateWorkflowOwner(workflowTask, Guid.Empty); }
        catch (WorkflowRpcException error) { rejected = error.Code == "workflow_task_identity_missing"; }
        Check(rejected, "task-bound read-only validation rejects an empty stable TaskId");
        rejected = false;
        try { CodexWorkflowConnectionValidator.ValidateWorkflowOwner(workflowTask, Guid.Empty, allowLegacyRequest: true); }
        catch (WorkflowRpcException error) { rejected = error.Code == "workflow_task_identity_missing"; }
        Check(rejected, "a legacy empty TaskId cannot continue a task-bound workflow thread");
        CodexWorkflowConnectionValidator.ValidateWorkflowOwner(Guid.Empty, Guid.Empty, allowLegacyRequest: true);
        Check(true, "legacy owner compatibility remains confined to an unbound legacy thread");

        var unknownIdle = StoredThread(); unknownIdle["thread"]!["status"]!["type"] = "unknown";
        Check(!Read(unknownIdle).SafeToContinue && Read(unknownIdle).DiagnosticCode == "owned_thread_idle_not_verified",
            "unknown native thread state cannot authorize continuation even with a completed historical turn");
        var missingIdle = StoredThread(); missingIdle["thread"]!.AsObject().Remove("status");
        Check(!Read(missingIdle).SafeToContinue, "missing native idle evidence blocks continuation");
        var firstStart = Policy(new { type = "readOnly", networkAccess = false });
        firstStart["thread"] = StoredThread()["thread"]!.DeepClone(); firstStart["thread"]!["cwd"] = @"D:\OtherProject";
        rejected = false;
        try { CodexWorkflowConnectionValidator.ValidateThreadIdentity(JsonSerializer.SerializeToElement(firstStart), project, "owned-thread"); }
        catch (WorkflowRpcException error) { rejected = error.Code == "read_thread_cwd_mismatch"; }
        Check(rejected, "new thread actual cwd must agree even when the top-level policy claims the expected project");
        rejected = false;
        try { CodexWorkflowPolicyValidator.ValidateInitialThread(JsonSerializer.SerializeToElement(workspace), project, "gpt-6.1-sol", allowedRoots, requireReadOnly: true); }
        catch (WorkflowRpcException error) { rejected = error.Code == "thread_read_only_policy_required"; }
        Check(rejected, "workflow native tools cannot gain workspace writes around the project capability scope");

        var scopedTask = Guid.NewGuid();
        var paths = new[] { "src/", "tests/Allowed.cs" };
        var authorizationId = WorkflowAuthorization.CreateAuthorizationId(scopedTask, project, "frozen original goal", paths);
        WorkflowExecutionRequest Authorized(string? continuation = null) => new(Guid.NewGuid(), WorkflowProvider.Codex, "wrapped per-turn goal", continuation)
        { WorkflowTaskId = scopedTask, AuthorizedProjectRoot = project, AuthorizedWritePaths = paths, StepWritePaths = paths, AuthorizationId = authorizationId };
        var binding = WorkflowAuthorization.ValidateRequest(Authorized(), project);
        Check(binding.WorkflowTaskId == scopedTask && binding.AuthorizedWritePaths.SequenceEqual(paths) && binding.AuthorizationId == authorizationId,
            "typed project authority is frozen independently of per-turn prompt text");
        Check(authorizationId == WorkflowAuthorization.CreateAuthorizationId(scopedTask, project.ToUpperInvariant(), "frozen original goal", new[] { "TESTS\\Allowed.cs", "SRC/", "src/" }),
            "equivalent root casing, separators, scope order and duplicates produce one authority identity");
        Check(authorizationId != WorkflowAuthorization.CreateAuthorizationId(scopedTask, project, "different original goal", paths)
            && authorizationId != WorkflowAuthorization.CreateAuthorizationId(Guid.NewGuid(), project, "frozen original goal", paths),
            "task and frozen goal changes cannot retain the old authority identity");
        void RejectAuthorization(WorkflowExecutionRequest request, string expectedCode, string label)
        {
            var code = "";
            try { WorkflowAuthorization.ValidateRequest(request, project); } catch (WorkflowRpcException error) { code = error.Code; }
            Check(code == expectedCode, label);
        }
        RejectAuthorization(Authorized() with { AuthorizedProjectRoot = @"D:\AI\ComfyUI\output\authorized-video" }, "authorization_project_mismatch", "video project authority cannot bind to a stale fixture connector");
        RejectAuthorization(Authorized() with { WorkflowTaskId = Guid.Empty }, "authorization_missing", "empty stable task identity blocks new dispatch");
        RejectAuthorization(Authorized() with { AuthorizationId = "" }, "authorization_missing", "missing frozen authority blocks new dispatch");
        RejectAuthorization(Authorized() with { AuthorizedWritePaths = [] }, "authorization_scope_invalid", "ordinary workflow cannot infer a write scope from its goal");
        foreach (var badPath in new[] { "*", ".", "./", "../src/", "src/../Code.cs", @"D:\OtherProject", "AGENTS.md", ".codex/", "src/AGENTS.md", "auth.json", "src/NUL.cs", "src//" })
            RejectAuthorization(Authorized() with { AuthorizedWritePaths = [badPath] }, "authorization_scope_invalid", "unsafe or protected authority scope refused: " + badPath);
        var readOnlyRequest = Authorized() with { ReadOnly = true, AuthorizedWritePaths = [], StepWritePaths = [],
            AuthorizationId = WorkflowAuthorization.CreateAuthorizationId(scopedTask, project, "frozen original goal", [], readOnly: true) };
        Check(WorkflowAuthorization.ValidateRequest(readOnlyRequest, project).ReadOnly, "explicit read-only task permits an empty write scope");
        RejectAuthorization(Authorized() with { ReadOnly = true }, "authorization_scope_invalid", "read-only request cannot smuggle write grants");
        WorkflowAuthorization.ValidateContinuation(binding, WorkflowAuthorization.ValidateRequest(Authorized("owned-thread"), project));
        Check(true, "same frozen authority supports a new request on its own continuation");
        rejected = false;
        try { WorkflowAuthorization.ValidateContinuation(binding, WorkflowAuthorization.ValidateRequest(Authorized("owned-thread") with { AuthorizedWritePaths = ["other/"] }, project)); }
        catch (WorkflowRpcException error) { rejected = error.Code == "session_authorization_binding_mismatch"; }
        Check(rejected, "an owned session cannot silently expand or replace its frozen write scope");
        rejected = false;
        try { WorkflowAuthorization.ValidateContinuation(binding, WorkflowAuthorization.ValidateRequest(Authorized("owned-thread") with { AuthorizationId = new string('A', 64) }, project)); }
        catch (WorkflowRpcException error) { rejected = error.Code == "session_authorization_binding_mismatch"; }
        Check(rejected, "an owned session cannot adopt another frozen goal authority");
        var ownedSessions = new Dictionary<string, Guid> { ["owned-thread"] = scopedTask };
        rejected = false;
        try { CodexWorkflowConnectionValidator.ValidateSessionSelection(scopedTask, null, ownedSessions); }
        catch (WorkflowRpcException error) { rejected = error.Code == "session_continuation_required"; }
        Check(rejected, "missing continuation on an already bound task cannot borrow the old session or create a replacement session");
        CodexWorkflowConnectionValidator.ValidateSessionSelection(scopedTask, "owned-thread", ownedSessions);
        CodexWorkflowConnectionValidator.ValidateSessionSelection(Guid.NewGuid(), null, ownedSessions);
        Check(true, "explicit owned continuation and a new independent task keep their separate session lifecycles");
        rejected = false;
        try { CodexWorkflowConnectionValidator.ValidateSessionSelection(scopedTask, "foreign-thread", ownedSessions); }
        catch (WorkflowRpcException error) { rejected = error.Code == "foreign_continuation_rejected"; }
        Check(rejected, "explicit session selection cannot adopt an unowned thread");
        var step = WorkflowAuthorization.ValidateStepWritePaths(binding, Authorized() with { StepWritePaths = ["src/Only.cs"] });
        Check(step.SequenceEqual(new[] { "src/Only.cs" }), "an advice-plan subset is separately frozen for this turn");
        rejected = false;
        try { WorkflowAuthorization.ValidateStepWritePaths(binding, Authorized() with { StepWritePaths = ["src-other/Outside.cs"] }); }
        catch (WorkflowRpcException error) { rejected = error.Code == "authorization_step_scope_invalid"; }
        Check(rejected, "step authorization cannot extend the task scope through a similarly named sibling");
        rejected = false;
        try { WorkflowAuthorization.ValidateStepWritePaths(binding, Authorized() with { StepWritePaths = [] }); }
        catch (WorkflowRpcException error) { rejected = error.Code == "authorization_step_scope_invalid"; }
        Check(rejected, "write workflow requires an explicit nonempty plan scope before dispatch");
        rejected = false;
        try { WorkflowAuthorization.ValidateStepWritePaths(WorkflowAuthorization.ValidateRequest(readOnlyRequest, project), readOnlyRequest with { StepWritePaths = ["src/Only.cs"] }); }
        catch (WorkflowRpcException error) { rejected = error.Code == "authorization_step_scope_invalid"; }
        Check(rejected, "read-only task cannot gain a per-turn write grant");
        WorkflowAuthorization.ValidateContinuation(binding, WorkflowAuthorization.ValidateRequest(Authorized() with { StepWritePaths = ["tests/Allowed.cs"] }, project));
        Check(true, "changing a step subset does not change the frozen task or create a new session authority");
        paths[0] = "mutated/";
        Check(binding.AuthorizedWritePaths[0] == "src/", "caller array mutation cannot rewrite an already validated scope snapshot");

        var isolatedRoot = Path.Combine(@"D:\CodexQuotaWidget-GitHub\artifacts\authorization-connector-checks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(isolatedRoot); var isolatedHome = Path.Combine(isolatedRoot, "auth-home"); Directory.CreateDirectory(isolatedHome);
        await using var connector = new CodexWorkflowConnector(isolatedRoot, isolatedRoot, authenticationHome: isolatedHome);
        var wrongProject = await connector.ExecuteAsync(Authorized());
        Check(!wrongProject.Success && wrongProject.DispatchPrevented && wrongProject.DiagnosticCode == "authorization_project_mismatch"
            && wrongProject.RequestId != Guid.Empty && wrongProject.WorkflowTaskId == scopedTask && wrongProject.ProjectRoot == isolatedRoot,
            "real connector rejects wrong-project authority before connection or any RPC and returns correlated prevention evidence");
        var emptyAuthority = await connector.ExecuteAsync(new(Guid.NewGuid(), WorkflowProvider.Codex, "unbound task"));
        Check(!emptyAuthority.Success && emptyAuthority.DispatchPrevented && emptyAuthority.DiagnosticCode == "authorization_missing",
            "real connector never falls back to its fixed root for an unbound request");
        var wrongExpectedSession = await connector.ExecuteAsync(Authorized("another-thread") with
        {
            AuthorizedProjectRoot = isolatedRoot, ExpectedSessionId = "expected-thread",
            AuthorizationId = WorkflowAuthorization.CreateAuthorizationId(scopedTask, isolatedRoot, "frozen original goal", paths)
        });
        Check(!wrongExpectedSession.Success && wrongExpectedSession.DispatchPrevented && wrongExpectedSession.DiagnosticCode == "session_expected_identity_mismatch",
            "explicit expected session cannot be replaced by a different continuation before dispatch");

        WorkflowExecutionRequest IsolatedRequest() => new(Guid.NewGuid(), WorkflowProvider.Codex, "fixture-goal-must-never-be-persisted")
        {
            WorkflowTaskId = scopedTask, AuthorizedProjectRoot = isolatedRoot, AuthorizedWritePaths = ["src/", "tests/"],
            StepWritePaths = ["src/Only.cs"], AuthorizationId = WorkflowAuthorization.CreateAuthorizationId(scopedTask, isolatedRoot,
                "frozen isolated original goal", ["src/", "tests/"])
        };
        string ClaimPath(Guid requestId) => Path.Combine(isolatedRoot, "workflow", "codex-dispatches", requestId.ToString("D") + ".json");
        var safeRequest = IsolatedRequest();
        var noTransport = await connector.ExecuteAsync(safeRequest);
        Check(!noTransport.Success && noTransport.DispatchPrevented && noTransport.DiagnosticCode == "connector_not_ready"
            && !File.Exists(ClaimPath(safeRequest.RequestId)), "not-ready local guard leaves no dispatch claim and proves this request was not sent");
        foreach (var invalidScope in new[] { Array.Empty<string>(), new[] { "other/Outside.cs" }, new[] { "../outside.cs" } })
        {
            var invalid = IsolatedRequest() with { StepWritePaths = invalidScope };
            var invalidScopeResult = await connector.ExecuteAsync(invalid);
            Check(!invalidScopeResult.Success && invalidScopeResult.DispatchPrevented && invalidScopeResult.DiagnosticCode is "authorization_step_scope_invalid" or "authorization_scope_invalid"
                && !File.Exists(ClaimPath(invalid.RequestId)), "invalid step scope is rejected before a durable dispatch claim");
        }
        var normalizedRequest = IsolatedRequest() with { StepWritePaths = ["src\\Only.cs", "src/Only.cs"] };
        var isolatedBinding = WorkflowAuthorization.ValidateRequest(normalizedRequest, isolatedRoot);
        var normalizedStep = WorkflowAuthorization.ValidateStepWritePaths(isolatedBinding, normalizedRequest);
        Check(normalizedStep.SequenceEqual(new[] { "src/Only.cs" }), "connector dispatch scope normalization removes aliases without expanding task authority");
        Check(Claim(connector, normalizedRequest, isolatedBinding, normalizedStep), "first offline dispatch claim is exclusively persisted on D drive");
        var claimBytes = await File.ReadAllBytesAsync(ClaimPath(normalizedRequest.RequestId));
        var persistedClaim = JsonSerializer.Deserialize<JsonElement>(claimBytes);
        Check(persistedClaim.GetProperty("TaskId").GetGuid() == scopedTask
            && persistedClaim.GetProperty("RequestId").GetGuid() == normalizedRequest.RequestId
            && persistedClaim.GetProperty("ProjectRoot").GetString() == isolatedRoot
            && persistedClaim.GetProperty("AuthorizationId").GetString() == isolatedBinding.AuthorizationId
            && persistedClaim.GetProperty("StepWritePaths").EnumerateArray().Select(item => item.GetString()).SequenceEqual(normalizedStep)
            && !System.Text.Encoding.UTF8.GetString(claimBytes).Contains(normalizedRequest.Goal, StringComparison.Ordinal),
            "claim stores frozen task request root authorization and normalized step paths without task text");
        Check(!Claim(connector, normalizedRequest, isolatedBinding, normalizedStep), "same RequestId cannot obtain a second claim in the same connector");
        var replayResult = await connector.ExecuteAsync(normalizedRequest);
        Check(!replayResult.Success && !replayResult.DispatchPrevented && replayResult.DiagnosticCode == "request_dispatch_already_claimed"
            && replayResult.RequestId == normalizedRequest.RequestId && replayResult.WorkflowTaskId == scopedTask
            && replayResult.CompletedTurnId.Length == 0 && replayResult.ContinuationId is null,
            "real execution entry rejects a claimed request before RPC and never fabricates cached success or session evidence");
        var changedReplay = await connector.ExecuteAsync(normalizedRequest with { Goal = "changed advice cannot create a second turn",
            ContinuationId = "foreign-thread", ExpectedSessionId = "foreign-thread" });
        Check(!changedReplay.Success && !changedReplay.DispatchPrevented && changedReplay.DiagnosticCode == "request_dispatch_already_claimed"
            && Private<HashSet<string>>(connector, "ownedThreads").Count == 0 && connector.LastThreadId.Length == 0,
            "changing goal or supplying a foreign continuation cannot bypass a claim or import its session");
        var changedTask = Guid.NewGuid();
        var changedAuthority = await connector.ExecuteAsync(normalizedRequest with { WorkflowTaskId = changedTask,
            AuthorizationId = WorkflowAuthorization.CreateAuthorizationId(changedTask, isolatedRoot, "different authority", ["src/", "tests/"]) });
        Check(!changedAuthority.Success && !changedAuthority.DispatchPrevented && changedAuthority.DiagnosticCode == "request_dispatch_already_claimed",
            "a reused RequestId cannot be adopted by another workflow or authorization");
        var bytesAfterReplay = await File.ReadAllBytesAsync(ClaimPath(normalizedRequest.RequestId));
        Check(claimBytes.SequenceEqual(bytesAfterReplay), "claim rejection preserves original identity and durable bytes exactly");
        await using var restartedConnector = new CodexWorkflowConnector(isolatedRoot, isolatedRoot, authenticationHome: isolatedHome);
        Check(!Claim(restartedConnector, normalizedRequest, isolatedBinding, normalizedStep), "reconstructed connector cannot replace an existing request claim");
        var restartedReplay = await restartedConnector.ExecuteAsync(normalizedRequest);
        Check(!restartedReplay.Success && !restartedReplay.DispatchPrevented && restartedReplay.DiagnosticCode == "request_dispatch_already_claimed"
            && restartedConnector.LastThreadId.Length == 0 && Private<HashSet<string>>(restartedConnector, "ownedThreads").Count == 0,
            "persistent replay guard survives connector reconstruction without restoring foreign ownership");
        var concurrentRequest = IsolatedRequest();
        var competing = await Task.WhenAll(Task.Run(() => Claim(connector, concurrentRequest, isolatedBinding, normalizedStep)),
            Task.Run(() => Claim(restartedConnector, concurrentRequest, isolatedBinding, normalizedStep)));
        Check(competing.Count(value => value) == 1, "two offline connector instances can acquire only one claim for the same RequestId");
        var separateRequest = IsolatedRequest();
        Check(Claim(restartedConnector, separateRequest, isolatedBinding, normalizedStep), "a distinct RequestId keeps its own independent dispatch claim");
        foreach (var partialContent in new[] { "", "{incomplete-claim" })
        {
            var partialRequest = IsolatedRequest(); await File.WriteAllTextAsync(ClaimPath(partialRequest.RequestId), partialContent);
            var partialReplay = await restartedConnector.ExecuteAsync(partialRequest);
            Check(!partialReplay.Success && !partialReplay.DispatchPrevented && partialReplay.DiagnosticCode == "request_dispatch_already_claimed"
                && await File.ReadAllTextAsync(ClaimPath(partialRequest.RequestId)) == partialContent,
                "an incomplete prior claim remains unknown and cannot be silently overwritten or replayed");
        }
        var unavailableRoot = Path.Combine(isolatedRoot, "unavailable-store");
        Directory.CreateDirectory(Path.Combine(unavailableRoot, "workflow"));
        var unavailableMarker = Path.Combine(unavailableRoot, "workflow", "codex-dispatches");
        await File.WriteAllTextAsync(unavailableMarker, "inert blocked-store fixture");
        await using var unavailableConnector = new CodexWorkflowConnector(isolatedRoot, unavailableRoot, authenticationHome: isolatedHome);
        var unavailableResult = await unavailableConnector.ExecuteAsync(IsolatedRequest());
        Check(!unavailableResult.Success && !unavailableResult.DispatchPrevented && unavailableResult.DiagnosticCode == "dispatch_claim_unavailable"
            && await File.ReadAllTextAsync(unavailableMarker) == "inert blocked-store fixture",
            "unverifiable prior-dispatch storage preserves uncertainty without resetting or replacing the blocked store");

        // Reflection changes only this unconnected fixture's memory; no real app-server or process is created.
        var operationGate = Private<SemaphoreSlim>(connector, "operations"); await operationGate.WaitAsync();
        try
        {
            var busyDisconnect = await connector.DisconnectForDiagnosticsAsync();
            Check(!busyDisconnect.Accepted && connector.ConnectionState == WorkflowConnectionState.NotConnected,
                "diagnostic disconnect refuses a busy connector immediately without waiting or changing connection state");
        }
        finally { operationGate.Release(); }
        var activeCapture = new CodexWorkflowTurnCapture("fixture-owned-thread"); SetPrivate(connector, "active", activeCapture);
        Check(!(await connector.DisconnectForDiagnosticsAsync()).Accepted && ReferenceEquals(Private<CodexWorkflowTurnCapture>(connector, "active"), activeCapture),
            "diagnostic disconnect never drops an active turn capture even before its turn identity is known");
        SetPrivate<CodexWorkflowTurnCapture?>(connector, "active", null);
        SetPrivate(connector, "lastThreadId", "fixture-owned-thread"); SetPrivate(connector, "lastTurnId", "fixture-owned-turn");
        SetPrivate(connector, "lastSessionId", "fixture-owned-session");
        Private<HashSet<string>>(connector, "ownedThreads").Add("fixture-owned-thread");
        Private<Dictionary<string, Guid>>(connector, "ownedWorkflowTasks")["fixture-owned-thread"] = scopedTask;
        Private<Dictionary<string, string>>(connector, "ownedLastTurns")["fixture-owned-thread"] = "fixture-owned-turn";
        Private<Dictionary<string, string>>(connector, "ownedSessionIds")["fixture-owned-thread"] = "fixture-owned-session";
        Private<Dictionary<string, WorkflowAuthorizationBinding>>(connector, "ownedAuthorizations")["fixture-owned-thread"] = isolatedBinding;
        var projectFiles = new CodexWorkflowProjectFiles(isolatedRoot, isolatedBinding.AuthorizedWritePaths);
        Private<Dictionary<string, CodexWorkflowProjectFiles>>(connector, "ownedProjectFiles")["fixture-owned-thread"] = projectFiles;
        var idleTransport = new WorkflowRpcProcess(new System.Diagnostics.ProcessStartInfo("inert-never-started-fixture"));
        SetPrivate(connector, "rpc", idleTransport);
        foreach (var uncertain in new[] { CodexWorkflowRunState.Unknown, CodexWorkflowRunState.Working, CodexWorkflowRunState.Waiting })
        {
            SetPrivate(connector, "lastRunState", uncertain); SetPrivate(connector, "connectionState", WorkflowConnectionState.Ready);
            Private<HashSet<string>>(connector, "loadedThreads").Add("fixture-owned-thread");
            Check(!(await connector.DisconnectForDiagnosticsAsync()).Accepted && connector.LastRunState == uncertain
                && connector.ConnectionState == WorkflowConnectionState.Ready && Private<HashSet<string>>(connector, "loadedThreads").Contains("fixture-owned-thread"),
                uncertain + " owned-session state prevents diagnostic transport reset and keeps loaded identity");
        }
        foreach (var terminalState in new[] { CodexWorkflowRunState.Completed, CodexWorkflowRunState.Failed, CodexWorkflowRunState.Interrupted, CodexWorkflowRunState.Exhausted })
        {
            SetPrivate(connector, "lastRunState", terminalState); SetPrivate(connector, "connectionState", WorkflowConnectionState.Ready);
            Private<HashSet<string>>(connector, "loadedThreads").Add("fixture-owned-thread");
            var idleDisconnect = await connector.DisconnectForDiagnosticsAsync();
            Check(idleDisconnect.Accepted && connector.ConnectionState == WorkflowConnectionState.NotConnected
                && Private<HashSet<string>>(connector, "loadedThreads").Count == 0 && connector.LastRunState == terminalState
                && connector.LastThreadId == "fixture-owned-thread" && connector.LastTurnId == "fixture-owned-turn" && connector.LastSessionId == "fixture-owned-session",
                terminalState + " idle disconnect clears only loaded transport state and preserves last native identity");
            Check(Private<HashSet<string>>(connector, "ownedThreads").SetEquals(new[] { "fixture-owned-thread" })
                && Private<Dictionary<string, Guid>>(connector, "ownedWorkflowTasks")["fixture-owned-thread"] == scopedTask
                && Private<Dictionary<string, string>>(connector, "ownedLastTurns")["fixture-owned-thread"] == "fixture-owned-turn"
                && Private<Dictionary<string, string>>(connector, "ownedSessionIds")["fixture-owned-thread"] == "fixture-owned-session"
                && Private<Dictionary<string, WorkflowAuthorizationBinding>>(connector, "ownedAuthorizations")["fixture-owned-thread"] == isolatedBinding
                && ReferenceEquals(Private<Dictionary<string, CodexWorkflowProjectFiles>>(connector, "ownedProjectFiles")["fixture-owned-thread"], projectFiles),
                terminalState + " idle disconnect preserves task authorization session and scoped file-tool ownership");
        }
        Check(Private<WorkflowRpcProcess?>(connector, "rpc") is null
            && (int)typeof(WorkflowRpcProcess).GetField("disposed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(idleTransport)! == 1,
            "idle diagnostic disconnect disposes only the injected unstarted transport object without launching any process");
        Check(!(await connector.ValidateOwnedSessionAsync("not-owned-thread", scopedTask)).Success
            && connector.LastThreadId == "fixture-owned-thread", "diagnostic reconnect state does not acquire an unrelated session");
        Check(connector.ConnectionDiagnostics.Length == 0, "execution guards and diagnostic disconnect do not fabricate connection-stage evidence");
        Trace(connector, "BeginConnectionDiagnostics");
        Trace(connector, "BeginConnectionStage", "cli_discovery", "stage_started");
        Trace(connector, "FinishConnectionStage", "succeeded", "candidates_discovered");
        Trace(connector, "BeginConnectionStage", "cli_signature_version", "candidate_1");
        Trace(connector, "FinishConnectionStage", "failed", "connection_timeout");
        Trace(connector, "CompleteConnectionDiagnostics", "failed", "connection_timeout");
        var trace = connector.ConnectionDiagnostics;
        Check(trace.Length == 6 && trace[0].Stage == "connection" && trace[0].Result == "started"
            && trace[1].Stage == "cli_discovery" && trace[2].Result == "succeeded" && trace[3].Code == "candidate_1"
            && trace[4].Stage == "cli_signature_version" && trace[4].Code == "connection_timeout"
            && trace[5].Stage == "connection" && trace[5].Result == "failed",
            "synthetic diagnostics preserve exact discovery verification timeout and total-stage order");
        Check(trace.All(item => item.At != default && item.ElapsedMilliseconds >= 0 && item.DurationMilliseconds >= 0
                && item.DurationMilliseconds <= item.ElapsedMilliseconds)
            && trace.Zip(trace.Skip(1)).All(pair => pair.First.ElapsedMilliseconds <= pair.Second.ElapsedMilliseconds)
            && trace[^1].DurationMilliseconds == trace[^1].ElapsedMilliseconds,
            "connection timing uses nonnegative monotonic elapsed and per-stage durations with UTC timestamps");
        trace[0] = trace[0] with { Stage = "external_mutation" };
        Check(connector.ConnectionDiagnostics[0].Stage == "connection", "connection diagnostics expose defensive array snapshots");
        var savedTrace = JsonSerializer.Serialize(connector.ConnectionDiagnostics);
        await operationGate.WaitAsync();
        try
        {
            Check(!(await connector.ConnectAsync()).Success && JsonSerializer.Serialize(connector.ConnectionDiagnostics) == savedTrace,
                "busy connection probe cannot reset or append another attempt's timing evidence");
        }
        finally { operationGate.Release(); }
        var rawFailure = new WorkflowRpcException("private-fixture-payload");
        var codeMapper = typeof(CodexWorkflowConnector).GetMethod("ConnectionTraceFailureCode", BindingFlags.Static | BindingFlags.NonPublic)!;
        Check((string)codeMapper.Invoke(null, [rawFailure, false])! == "rpc_or_binding_rejected"
            && (string)codeMapper.Invoke(null, [new InvalidOperationException("private-fixture-payload"), false])! == "connection_stage_failed"
            && (string)codeMapper.Invoke(null, [new OperationCanceledException(), true])! == "connection_cancelled"
            && (string)codeMapper.Invoke(null, [new OperationCanceledException(), false])! == "connection_timeout",
            "diagnostic failures use only controlled codes and distinguish caller cancellation from the unchanged total budget");
        Trace(restartedConnector, "BeginConnectionDiagnostics");
        await (Task)Trace(restartedConnector, "VerifyLastOwnedThreadAsync", null, CancellationToken.None)!;
        Check(restartedConnector.ConnectionDiagnostics[^1].Stage == "owned_session"
            && restartedConnector.ConnectionDiagnostics[^1].Result == "skipped" && restartedConnector.ConnectionDiagnostics[^1].Code == "no_owned_session"
            && restartedConnector.LastThreadId.Length == 0, "no owned session produces an explicit skip without issuing any RPC or manufacturing a thread");
        await connector.DisposeAsync();
        Check(!(await connector.ConnectAsync()).Success && connector.ConnectionDiagnostics.Length == 2
            && connector.ConnectionDiagnostics[0].Result == "started" && connector.ConnectionDiagnostics[1].Code == "connector_closed",
            "each accepted probe resets diagnostic attempt state and a disposed connector fails before CLI discovery");
        var diagnosticJson = JsonSerializer.Serialize(connector.ConnectionDiagnostics);
        Check(!diagnosticJson.Contains(isolatedRoot, StringComparison.Ordinal) && !diagnosticJson.Contains(isolatedHome, StringComparison.Ordinal)
            && !diagnosticJson.Contains("private-fixture-payload", StringComparison.Ordinal)
            && connector.ConnectionDiagnostics.All(item => item.Stage == "connection"),
            "public connection diagnostics contain stage codes and timing rather than paths or raw failure payloads");
        return count;
    }

    static FieldInfo Field(string name) => typeof(CodexWorkflowConnector).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Missing connector fixture field: " + name);
    static T Private<T>(CodexWorkflowConnector connector, string name) => (T)Field(name).GetValue(connector)!;
    static void SetPrivate<T>(CodexWorkflowConnector connector, string name, T value) => Field(name).SetValue(connector, value);
    static bool Claim(CodexWorkflowConnector connector, WorkflowExecutionRequest request, WorkflowAuthorizationBinding authorization, string[] stepWritePaths)
        => (bool)(typeof(CodexWorkflowConnector).GetMethod("TryClaimDispatch", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing dispatch claim fixture method")).Invoke(connector, [request, authorization, stepWritePaths])!;
    static object? Trace(CodexWorkflowConnector connector, string method, params object?[] arguments)
        => (typeof(CodexWorkflowConnector).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing connection diagnostic fixture method: " + method)).Invoke(connector, arguments);
}

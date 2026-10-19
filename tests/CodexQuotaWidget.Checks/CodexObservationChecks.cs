using System.Text;
using System.Text.Json;
using CodexQuotaWidget.Core.Observation;

public static class CodexObservationChecks
{
    public static async Task<int> RunAsync()
    {
        const string fixtureRoot = @"D:\CodexQuotaWidget-GitHub\artifacts\observation-checks";
        var root = Path.Combine(fixtureRoot, Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(project);
        var now = DateTimeOffset.UtcNow;
        var passed = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException("Codex observation: " + label);
            passed++;
        }
        string Record(string type, object payload, int seconds = 0)
            => JsonSerializer.Serialize(new { timestamp = now.AddSeconds(seconds).ToString("O"), type, payload });
        string Meta(string session, string? cwd = null) => Record("session_meta", new { id = session, cwd = cwd ?? project, originator = "Codex Desktop", cli_version = "0.159.0" });
        string Start(string turn = "turn-1", int seconds = -2) => Record("event_msg", new { type = "task_started", turn_id = turn }, seconds);
        string Complete(string turn = "turn-1", int seconds = -1) => Record("event_msg", new { type = "task_complete", turn_id = turn }, seconds);
        string Context(string turn, string cwd) => Record("turn_context", new { turn_id = turn, cwd }, -2);
        string Write(string home, string session, params string[] records)
        {
            var day = Path.Combine(home, "sessions", now.LocalDateTime.ToString("yyyy"), now.LocalDateTime.ToString("MM"), now.LocalDateTime.ToString("dd"));
            Directory.CreateDirectory(day);
            var path = Path.Combine(day, "rollout-" + session + ".jsonl");
            File.WriteAllText(path, string.Join('\n', records) + "\n", new UTF8Encoding(false));
            return path;
        }
        async Task<CodexStatusObservation> Read(string name, params string[] records)
        {
            var home = Path.Combine(root, name);
            Write(home, "session-1", records);
            return await new CodexStatusObserver(project, home, () => now).ReadAsync();
        }
        try
        {
            var missing = await new CodexStatusObserver(project, Path.Combine(root, "missing"), () => now).ReadAsync();
            Check(missing.State == CodexObservedState.Unknown && !missing.TaskStoppedConfirmed, "missing Codex home remains unknown; absence is not a crash");
            var metadataOnly = await Read("metadata-only", Meta("session-1"));
            Check(metadataOnly.State == CodexObservedState.Unknown && !metadataOnly.TaskStoppedConfirmed, "metadata/process presence does not mean running or waiting");
            var running = await Read("running", Meta("session-1"), Start());
            Check(running.State == CodexObservedState.Running && !running.TaskStoppedConfirmed && running.TurnId == "turn-1", "actual correlated task_started becomes running");
            var waiting = await Read("waiting", Meta("session-1"), Start(), Record("event_msg", new { type = "request_user_input", turn_id = "turn-1", call_id = "request-1" }, -1));
            Check(waiting.State == CodexObservedState.WaitingForInput && !waiting.TaskStoppedConfirmed, "explicit correlated input request is waiting, never stopped");
            var uncorrelatedWaiting = await Read("waiting-no-turn", Meta("session-1"), Start(), Record("event_msg", new { type = "request_user_input", call_id = "request-1" }, -1));
            Check(uncorrelatedWaiting.State == CodexObservedState.Unknown, "input request without turn correlation remains unknown");
            var completed = await Read("complete", Meta("session-1"), Start(), Complete());
            Check(completed.State == CodexObservedState.Completed && completed.TaskStoppedConfirmed && completed.CoverageComplete, "started and completed exact turn provides local terminal evidence");
            Check(completed.Synthetic && !StatusEvidence.ValidateTerminal(completed, now, project), "fixture homes/clocks are synthetic and cannot authorize formal automation");
            Check(StatusEvidence.ValidateTerminal(completed with { Synthetic = false }, now, project), "fresh canonical real terminal evidence validates");
            Check(!StatusEvidence.ValidateTerminal(completed with { Synthetic = false, CoverageComplete = false }, now, project), "incomplete coverage blocks formal terminal validation");
            Check(!StatusEvidence.ValidateTerminal(completed with { Synthetic = false }, now, project + "-other"), "different project cannot borrow stop evidence");
            Check(!StatusEvidence.ValidateTerminal(completed with { Synthetic = false, EvidenceAt = now.AddMinutes(-3) }, now, project), "poll timestamp cannot refresh stale stop evidence");
            Check(!StatusEvidence.ValidateTerminal(completed with { Synthetic = false, State = CodexObservedState.AbnormalExit }, now, project), "unconfirmed process crash never authorizes terminal automation");
            var terminalOnly = await Read("terminal-only", Meta("session-1"), Complete());
            Check(terminalOnly.State == CodexObservedState.Unknown && !terminalOnly.TaskStoppedConfirmed, "terminal without observed turn start is not joined to an old task");
            var noTurn = await Read("terminal-no-turn", Meta("session-1"), Start(), Record("event_msg", new { type = "task_complete" }, -1));
            Check(noTurn.State == CodexObservedState.Unknown && !noTurn.TaskStoppedConfirmed, "terminal without turn id remains unknown");
            var wrongTurn = await Read("terminal-wrong-turn", Meta("session-1"), Start(), Complete("turn-2"));
            Check(wrongTurn.State == CodexObservedState.Unknown && !wrongTurn.TaskStoppedConfirmed, "terminal for a different turn cannot stop the active turn");
            var stale = await Read("stale", Meta("session-1"), Start(seconds: -190), Complete(seconds: -180));
            Check(stale.State == CodexObservedState.Unknown && !stale.TaskStoppedConfirmed, "old completed turn is displayed unknown, not refreshed by reading");
            var future = await Read("future", Meta("session-1"), Start(), Complete(seconds: 20));
            Check(future.State == CodexObservedState.Unknown && !future.TaskStoppedConfirmed, "future timestamps cannot become trusted status evidence");
            var reverse = await Read("reverse-time", Meta("session-1"), Start(seconds: -1), Complete(seconds: -2));
            Check(reverse.State == CodexObservedState.Unknown, "backwards lifecycle timestamps fail closed");
            var nextTurn = await Read("next-turn", Meta("session-1"), Start("turn-1", -4), Complete("turn-1", -3), Start("turn-2", -1));
            Check(nextTurn.State == CodexObservedState.Running && !nextTurn.TaskStoppedConfirmed && nextTurn.TurnId == "turn-2", "new task_started invalidates prior completed turn");
            var newUser = await Read("new-user", Meta("session-1"), Start(), Complete(), Record("event_msg", new { type = "user_message", message = "private user text" }));
            Check(newUser.State == CodexObservedState.Unknown && !newUser.TaskStoppedConfirmed, "new submitted user message invalidates prior stop evidence");
            Check(!newUser.Message.Contains("private user text", StringComparison.Ordinal), "user/log text is never copied into status output");
            var responseUser = await Read("response-user", Meta("session-1"), Start(), Complete(), Record("response_item", new { type = "message", role = "user", content = new[] { new { text = "pending user input" } } }));
            Check(responseUser.State == CodexObservedState.Unknown && !responseUser.TaskStoppedConfirmed, "persisted user input before next start invalidates old completion without reading prose");
            var unknownEvent = await Read("unknown-new-event", Meta("session-1"), Start(), Complete(), Record("event_msg", new { type = "future_execution_event", turn_id = "turn-2" }));
            Check(unknownEvent.State == CodexObservedState.Unknown && !unknownEvent.TaskStoppedConfirmed, "unknown execution schema after terminal fails closed instead of assuming idle");
            var network = await Read("network", Meta("session-1"), Start(), Record("event_msg", new { type = "stream_error", message = "retrying private network detail", codex_error_info = "response_stream_connection_failed" }, -1));
            Check(network.State == CodexObservedState.Unknown && !network.TaskStoppedConfirmed, "network retry/disconnect does not imply task termination or exhaustion");
            Check(!network.Message.Contains("private network detail", StringComparison.Ordinal), "raw error text is not persisted in observation messages");
            var rpc429 = await Read("rpc-429", Meta("session-1"), JsonSerializer.Serialize(new { error = new { code = 429, message = "rate limit" } }));
            Check(rpc429.State == CodexObservedState.Unknown && !rpc429.TaskStoppedConfirmed, "generic RPC429 is not a task or quota event");
            var usage = await Read("usage-error", Meta("session-1"), Start(), Record("event_msg", new { type = "error", codex_error_info = "usage_limit_exceeded", message = "private quota text" }, -1));
            Check(usage.State == CodexObservedState.QuotaExhausted && !usage.TaskStoppedConfirmed, "structured usage limit is distinguishable but error alone does not stop a task");
            var usageComplete = await Read("usage-terminal", Meta("session-1"), Start(), Record("event_msg", new { type = "task_complete", turn_id = "turn-1", error = new { codex_error_info = "usage_limit_exceeded", message = "limit" } }, -1));
            Check(usageComplete.State == CodexObservedState.QuotaExhausted && usageComplete.TaskStoppedConfirmed, "explicit terminal usage limit confirms task exhaustion");
            var generic429 = await Read("generic-limit", Meta("session-1"), Start(), Record("event_msg", new { type = "error", codex_error_info = "rate_limit_exceeded" }, -1));
            Check(generic429.State == CodexObservedState.UnableToContinue && !generic429.TaskStoppedConfirmed, "generic provider rate limit is not Codex account exhaustion");
            var failedComplete = await Read("failed-terminal", Meta("session-1"), Start(), Record("event_msg", new { type = "task_complete", turn_id = "turn-1", error = new { codex_error_info = "unauthorized" } }, -1));
            Check(failedComplete.State == CodexObservedState.UnableToContinue && failedComplete.TaskStoppedConfirmed, "explicit failed terminal becomes unable to continue, never forged success");
            var abort = await Read("aborted", Meta("session-1"), Start(), Record("event_msg", new { type = "turn_aborted", turn_id = "turn-1", reason = "interrupted" }, -1));
            Check(abort.State == CodexObservedState.UnableToContinue && abort.TaskStoppedConfirmed, "correlated turn_aborted confirms the turn ended, not a Desktop crash");

            var foreignHome = Path.Combine(root, "foreign-running");
            Write(foreignHome, "target", Meta("target"), Start(), Complete());
            Write(foreignHome, "foreign", Meta("foreign", project + "-other"), Start("foreign-turn"));
            var foreignRunning = await new CodexStatusObserver(project, foreignHome, () => now).ReadAsync();
            Check(foreignRunning.State == CodexObservedState.Completed && !foreignRunning.TaskStoppedConfirmed && !foreignRunning.CoverageComplete, "unrelated running project prevents closing global Codex Desktop");
            Write(foreignHome, "foreign", Meta("foreign", project + "-other"), Start("foreign-turn"), Complete("foreign-turn"));
            var foreignStopped = await new CodexStatusObserver(project, foreignHome, () => now).ReadAsync();
            Check(foreignStopped.TaskStoppedConfirmed && foreignStopped.CoverageComplete, "another explicitly stopped session does not create a false running state");
            var disappearedHome = Path.Combine(root, "foreign-disappeared");
            Write(disappearedHome, "target", Meta("target"), Start(), Complete());
            var disappearedPath = Write(disappearedHome, "foreign", Meta("foreign", project + "-other"), Start("foreign-turn"));
            var disappearedObserver = new CodexStatusObserver(project, disappearedHome, () => now);
            _ = await disappearedObserver.ReadAsync();
            File.Delete(disappearedPath);
            var disappeared = await disappearedObserver.ReadAsync();
            Check(!disappeared.TaskStoppedConfirmed && !disappeared.CoverageComplete, "a previously running log disappearing/being archived is not treated as successful task stop");
            var parallelHome = Path.Combine(root, "parallel");
            Write(parallelHome, "target", Meta("target"), Start(), Complete());
            Write(parallelHome, "child", Meta("child"), Start("child-turn"));
            var parallel = await new CodexStatusObserver(project, parallelHome, () => now).ReadAsync();
            Check(parallel.State == CodexObservedState.Running && !parallel.TaskStoppedConfirmed, "parallel active agent wins over another completed project turn");
            var uncertaintyHome = Path.Combine(root, "parallel-unknown");
            Write(uncertaintyHome, "target", Meta("target"), Start(), Complete());
            Write(uncertaintyHome, "unknown", Meta("unknown"));
            var uncertain = await new CodexStatusObserver(project, uncertaintyHome, () => now).ReadAsync();
            Check(uncertain.State == CodexObservedState.Unknown && !uncertain.TaskStoppedConfirmed, "unresolved same-project session blocks old completion");
            var unrelated = await Read("unrelated", Meta("session-1", project + "-other"), Start(), Complete());
            Check(unrelated.State == CodexObservedState.Unknown && unrelated.ScopedSessionCount == 0, "unrelated session cannot bind itself using a similar prefix");
            var movedContext = await Read("context-moved", Meta("session-1"), Start(), Context("turn-1", project + "-other"), Complete());
            Check(movedContext.State == CodexObservedState.Unknown && !movedContext.TaskStoppedConfirmed, "turn context changing project overrides initial metadata scope");
            var roots = await Read("explicit-root", Record("session_meta", new { id = "session-1", cwd = project + "-other", runtime_workspace_roots = new[] { project } }), Start(), Complete());
            Check(roots.TaskStoppedConfirmed, "explicit exact runtime workspace root can bind a different cwd");
            var nested = await Read("nested-cwd", Meta("session-1", Path.Combine(project, "src")), Start(), Complete());
            Check(nested.TaskStoppedConfirmed, "cwd inside project is a valid explicit scope");

            var partialHome = Path.Combine(root, "partial");
            var partialPath = Write(partialHome, "session-1", Meta("session-1"), Start());
            File.AppendAllText(partialPath, Complete(), new UTF8Encoding(false));
            var partialObserver = new CodexStatusObserver(project, partialHome, () => now);
            var partial = await partialObserver.ReadAsync();
            Check(partial.State == CodexObservedState.Unknown && !partial.TaskStoppedConfirmed, "unterminated JSONL tail is not consumed as completed");
            File.AppendAllText(partialPath, "\n", new UTF8Encoding(false));
            var flushed = await partialObserver.ReadAsync();
            Check(flushed.State == CodexObservedState.Completed && flushed.TaskStoppedConfirmed, "completed partial record becomes usable only after full newline flush");
            var duplicate = await partialObserver.ReadAsync();
            Check(duplicate.EvidenceAt == flushed.EvidenceAt && duplicate.TurnId == flushed.TurnId, "unchanged reads retain original evidence time and turn identity");
            File.AppendAllText(partialPath, Start("turn-2", 0) + "\n", new UTF8Encoding(false));
            var appendedStart = await partialObserver.ReadAsync();
            Check(appendedStart.State == CodexObservedState.Running && !appendedStart.TaskStoppedConfirmed, "cached observer invalidates completed state when next task is appended");
            var malformed = await Read("malformed", Meta("session-1"), Start(), Complete(), "{broken JSON");
            Check(malformed.State == CodexObservedState.Unknown && !malformed.TaskStoppedConfirmed, "malformed tail after terminal cannot hide a new task");
            var hugeMetadata = await Read("metadata-limit", Record("session_meta", new { id = "session-1", cwd = project, extra = new string('x', CodexStatusObserver.MetadataBytes) }), Start(), Complete());
            Check(hugeMetadata.State == CodexObservedState.Unknown && !hugeMetadata.CoverageComplete, "oversized metadata is rejected instead of unbounded reading");
            var truncated = await Read("tail-truncated", Meta("session-1"), Start(), Record("response_item", new { type = "message", text = new string('x', CodexStatusObserver.MaximumReadBytes + 100) }), Complete());
            Check(truncated.State == CodexObservedState.Unknown && !truncated.TaskStoppedConfirmed, "tail-only completion without retained start is rejected");
            var boundedLifecycle = await Read("tail-recovered", Meta("session-1"), Record("response_item", new { text = new string('x', CodexStatusObserver.MaximumReadBytes + 100) }), Start(), Complete());
            Check(boundedLifecycle.TaskStoppedConfirmed, "bounded tail can confirm a complete new lifecycle when both start and terminal are present");
            var overflowHome = Path.Combine(root, "scan-limit");
            for (var index = 0; index <= CodexStatusObserver.MaximumSessionFiles; index++)
                Write(overflowHome, "session-" + index, Meta("session-" + index), Start(), Complete());
            var overflow = await new CodexStatusObserver(project, overflowHome, () => now).ReadAsync();
            Check(!overflow.CoverageComplete && !overflow.TaskStoppedConfirmed, "session count bound never pretends complete global coverage");
            var tokenAfterEnd = await Read("terminal-token", Meta("session-1"), Start(), Complete(), Record("event_msg", new { type = "token_count", info = new { value = 42 } }));
            Check(tokenAfterEnd.State == CodexObservedState.Completed && tokenAfterEnd.EvidenceAt == now.AddSeconds(-1), "token accounting after completion cannot fabricate new execution or refresh terminal evidence");
            var noLifecycleMessage = await Read("message-not-state", Meta("session-1"), Record("response_item", new { type = "message", role = "assistant", phase = "final_answer", content = new[] { new { text = "Done" } } }));
            Check(noLifecycleMessage.State == CodexObservedState.Unknown, "assistant final text is not a task_complete lifecycle marker");
            var cancellation = new CancellationToken(true);
            var cancelled = false;
            try { await partialObserver.ReadAsync(cancellation); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "background observer respects cancellation without manufacturing status");
            return passed;
        }
        finally
        {
            var absoluteRoot = Path.GetFullPath(root);
            if (!absoluteRoot.StartsWith(Path.GetFullPath(fixtureRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Observation fixture cleanup escaped its own directory.");
            if (Directory.Exists(absoluteRoot)) Directory.Delete(absoluteRoot, true);
        }
    }
}

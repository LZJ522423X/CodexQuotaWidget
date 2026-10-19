using System.Text;
using System.Text.Json;
using CodexQuotaWidget.Core.Automation;
using CodexQuotaWidget.Core.Observation;

/// <summary>
/// Native-shaped JSONL fixtures only. They verify local parsing and safe confirmation rules;
/// they do not establish that Desktop has emitted every tested lifecycle in a real session.
/// All writable fixture data is isolated on D and no production events or actions are emitted.
/// </summary>
public static class CodexStateLifecycleChecks
{
    public static async Task<int> RunAsync()
    {
        const string baseDirectory = @"D:\CodexQuotaWidget-GitHub\artifacts\state-lifecycle-checks";
        var root = Path.Combine(baseDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var passed = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException("Codex state lifecycle: " + label);
            passed++;
        }
        try
        {
            var lifecycle = new Fixture(root, "lifecycle");
            lifecycle.Start("turn-a", -20);
            var observer = lifecycle.Observer();
            var working = await observer.ReadAsync();
            Check(working.CanonicalState == CodexTaskState.Working, "explicit task_started establishes Working");
            Check(working.TurnId == "turn-a" && working.SessionId == lifecycle.Session,
                "Working identity belongs to exactly the selected native-shaped session and turn");
            Check(working.EvidenceLevel == CodexEvidenceLevel.LifecycleConfirmed,
                "verified project with fresh complete start reaches lifecycle evidence level");
            Check(working.EvidenceKind == "task_started" && working.EvidencePosition == lifecycle.Position,
                "start provenance identifies the actual typed record and its byte cursor");
            Check(working.Synthetic && !working.ActualTaskStopped && !working.TaskStoppedConfirmed,
                "fixture Working cannot imply that Desktop or its background commands stopped");

            lifecycle.Event(new { type = "request_user_input", turn_id = "turn-a", call_id = "input-a" }, -15);
            var waiting = await observer.ReadAsync();
            Check(waiting.CanonicalState == CodexTaskState.Waiting && waiting.TurnId == "turn-a",
                "explicit correlated input request transitions Working to Waiting");
            Check(waiting.EvidenceKind == "request_user_input" && waiting.EvidencePosition == lifecycle.Position,
                "Waiting is attributable to the request record rather than elapsed time");
            Check(waiting.LastActivity == lifecycle.Time(-15) && !waiting.ActualTaskStopped,
                "waiting for input is not a terminal event");

            lifecycle.Event(new { type = "item_started", turn_id = "turn-a", item = new { type = "CommandExecution" } }, -10);
            var resumed = await observer.ReadAsync();
            Check(resumed.CanonicalState == CodexTaskState.Working && resumed.TurnId == "turn-a",
                "explicit same-turn execution resumes Working without a new guessed turn");
            Check(resumed.EvidenceKind == "item_started" && resumed.EvidencePosition == lifecycle.Position,
                "resumed execution updates only to its actual typed evidence cursor");
            lifecycle.Event(new { type = "task_complete", turn_id = "turn-a" }, -5);
            var completed = await observer.ReadAsync();
            Check(completed.CanonicalState == CodexTaskState.Completed && completed.ActualTaskStopped,
                "matching explicit task_complete establishes completion of the observed turn");
            Check(completed.TurnId == "turn-a" && completed.EvidenceKind == "task_complete"
                && completed.EvidencePosition == lifecycle.Position,
                "terminal identity and byte cursor remain attached to the completed turn");
            Check(completed.CoverageComplete && completed.TaskStoppedConfirmed && completed.Synthetic,
                "the sole complete fixture lifecycle can satisfy only synthetic global coverage");
            Check(!StatusEvidence.ValidateTerminal(completed, lifecycle.Now, lifecycle.Project),
                "even complete synthetic turn evidence never passes the real-action terminal validator");
            var duplicate = await observer.ReadAsync();
            Check(duplicate.CanonicalState == completed.CanonicalState && duplicate.EvidencePosition == completed.EvidencePosition
                && duplicate.EvidenceAt == completed.EvidenceAt,
                "re-polling an unchanged file does not invent newer event evidence");

            lifecycle.Record("response_item", new { type = "message", role = "user", content = Array.Empty<object>() }, -4);
            var inputBeforeStart = await observer.ReadAsync();
            Check(inputBeforeStart.CanonicalState == CodexTaskState.Unknown && !inputBeforeStart.ActualTaskStopped
                && !inputBeforeStart.TaskStoppedConfirmed,
                "new submitted user input invalidates old completion before the next task starts");
            Check(inputBeforeStart.UnknownReason.Length > 0,
                "the gap between user input and task start has an explicit Unknown reason");
            lifecycle.Start("turn-b", -3);
            var nextTurn = await observer.ReadAsync();
            Check(nextTurn.CanonicalState == CodexTaskState.Working && nextTurn.TurnId == "turn-b",
                "the next explicit task_started establishes the new current turn");
            Check(nextTurn.EvidencePosition == lifecycle.Position && !nextTurn.TaskStoppedConfirmed,
                "new current turn cannot inherit old terminal authorization");

            var idle = new Fixture(root, "idle");
            idle.Start("turn-idle", -1);
            var idleObserver = idle.Observer();
            var beforeIdle = await idleObserver.ReadAsync();
            idle.Now = idle.Now.AddSeconds(60);
            var afterIdle = await idleObserver.ReadAsync();
            Check(afterIdle.CanonicalState == CodexTaskState.Working && afterIdle.EvidencePosition == beforeIdle.EvidencePosition,
                "elapsed time alone does not transition Working to Waiting");
            Check(afterIdle.LastActivity == beforeIdle.LastActivity,
                "poll time does not refresh last activity or falsely keep a task alive");
            idle.Now = idle.Now.AddSeconds(120);
            var stale = await idleObserver.ReadAsync();
            Check(stale.CanonicalState == CodexTaskState.Unknown && stale.UnknownReason.Length > 0,
                "expired lifecycle evidence is Unknown rather than inferred Waiting or Completed");
            Check(stale.EvidenceLevel != CodexEvidenceLevel.LifecycleConfirmed && !stale.TaskStoppedConfirmed,
                "expired evidence loses lifecycle confirmation and stop authorization");

            var error = new Fixture(root, "error");
            error.Start("turn-error", -10);
            error.Event(new { type = "error", turn_id = "turn-error", codex_error_info = "response_stream_connection_failed" }, -5);
            var transient = await error.Observer().ReadAsync();
            Check(transient.CanonicalState == CodexTaskState.Unknown && !transient.ActualTaskStopped,
                "an execution error without a terminal marker is Unknown, not Failed");
            Check(transient.UnknownReason.Length > 0 && transient.EvidenceKind == "error",
                "error diagnosis retains its typed source without asserting task failure");
            error.Event(new { type = "task_complete", turn_id = "turn-error", error = new { codex_error_info = "unauthorized" } }, -1);
            var failed = await error.Observer().ReadAsync();
            Check(failed.CanonicalState == CodexTaskState.Failed && failed.ActualTaskStopped,
                "an unsuccessful correlated terminal can establish Failed");
            Check(failed.EvidenceKind == "task_complete" && failed.EvidencePosition == error.Position,
                "Failed evidence comes from terminal lifecycle, not the earlier error");

            var stream = new Fixture(root, "stream");
            stream.Start("turn-stream", -10);
            stream.Event(new { type = "stream_error", turn_id = "turn-stream", codex_error_info = "response_stream_connection_failed" }, -5);
            var disconnected = await stream.Observer().ReadAsync();
            Check(disconnected.CanonicalState == CodexTaskState.Unknown && !disconnected.TaskStoppedConfirmed,
                "simulated stream disconnect does not assert exhaustion, failure or a stopped turn");
            stream.Event(new { type = "item_started", turn_id = "turn-stream" }, -1);
            var reconnected = await stream.Observer().ReadAsync();
            Check(reconnected.CanonicalState == CodexTaskState.Working && reconnected.TurnId == "turn-stream",
                "explicit activity after a stream error recovers the same observed turn");

            var exhaustion = new Fixture(root, "exhaustion");
            exhaustion.Start("turn-exhausted", -12);
            exhaustion.Event(new { type = "error", turn_id = "turn-exhausted", codex_error_info = "usage_limit_exceeded", http_status_code = 429 }, -6);
            var usageError = await exhaustion.Observer().ReadAsync();
            Check(usageError.CanonicalState == CodexTaskState.Exhausted && !usageError.ActualTaskStopped
                && !usageError.TaskStoppedConfirmed,
                "a typed usage-limit/429 error reports exhaustion without implying task stopped");
            var receipt = Receipt(exhaustion.Now);
            Check(CodexExhaustedEventFactory.Confirm(receipt, usageError, exhaustion.Now, exhaustion.Project, exhaustion.Data) is null,
                "even valid synthetic positive-to-zero quota evidence cannot confirm an un-stopped error");
            exhaustion.Event(new { type = "task_complete", turn_id = "turn-exhausted", error = new { codex_error_info = "usage_limit_exceeded" } }, -1);
            var terminalExhaustion = await exhaustion.Observer().ReadAsync();
            Check(terminalExhaustion.CanonicalState == CodexTaskState.Exhausted && terminalExhaustion.ActualTaskStopped,
                "correlated usage-limit terminal confirms the fixture turn stopped");
            Check(CodexExhaustedEventFactory.Confirm(null, terminalExhaustion, exhaustion.Now, exhaustion.Project, exhaustion.Data) is null,
                "an exhausted terminal without independent valid quota evidence cannot emit exhaustion confirmation");
            Check(CodexExhaustedEventFactory.Confirm(receipt with { PositiveRemaining = 0 }, terminalExhaustion,
                exhaustion.Now, exhaustion.Project, exhaustion.Data) is null,
                "startup-at-zero quota cannot authorize exhaustion confirmation");
            var confirmed = CodexExhaustedEventFactory.Confirm(receipt, terminalExhaustion,
                exhaustion.Now, exhaustion.Project, exhaustion.Data);
            Check(confirmed?.ExhaustedEvent is { Synthetic: true, TaskStoppedConfirmed: true },
                "independent valid synthetic quota and stopped-task evidence can be combined in memory only");
            Check(!Directory.Exists(Path.Combine(exhaustion.Data, "events")),
                "fixture confirmation does not output production events or dispatch external actions");

            var backdated = new Fixture(root, "backdated");
            backdated.Start("turn-time", -5);
            backdated.Event(new { type = "item_started", turn_id = "turn-time" }, -10);
            var backwards = await backdated.Observer().ReadAsync();
            Check(backwards.CanonicalState == CodexTaskState.Unknown && !backwards.TaskStoppedConfirmed,
                "time moving backwards inside typed lifecycle invalidates confidence");
            var future = new Fixture(root, "future");
            future.Start("turn-future", -5);
            future.Event(new { type = "task_complete", turn_id = "turn-future" }, 1);
            var ahead = await future.Observer().ReadAsync();
            Check(ahead.CanonicalState == CodexTaskState.Unknown && !ahead.ActualTaskStopped,
                "a future terminal timestamp cannot become a real stopped-task assertion");

            var subagent = new Fixture(root, "subagent");
            subagent.Start("turn-current", -5);
            var subagentObserver = subagent.Observer();
            var beforeNotification = await subagentObserver.ReadAsync();
            subagent.Event(new
            {
                type = "item_completed", thread_id = subagent.Session, turn_id = "turn-old",
                item = new { type = "SubAgentActivity", id = "old-child" }
            }, -10);
            var afterNotification = await subagentObserver.ReadAsync();
            Check(afterNotification.CanonicalState == CodexTaskState.Working && afterNotification.TurnId == "turn-current",
                "late old-turn SubAgentActivity cannot corrupt the selected current-turn display");
            Check(afterNotification.EvidencePosition == beforeNotification.EvidencePosition
                && afterNotification.EvidenceKind == beforeNotification.EvidenceKind
                && afterNotification.EvidenceAt == beforeNotification.EvidenceAt,
                "old-turn notification cannot refresh or advance current-turn evidence byte cursor");
            Check(afterNotification.EvidencePosition is { } currentEvidencePosition && subagent.Position > currentEvidencePosition
                && afterNotification.HasUnresolvedOtherTurnActivity,
                "read extent may advance while evidence provenance stays at the last current-turn record");
            subagent.Event(new { type = "task_complete", turn_id = "turn-current" }, -1);
            var stoppedWithOtherTurn = await subagentObserver.ReadAsync();
            Check(stoppedWithOtherTurn.CanonicalState == CodexTaskState.Completed && stoppedWithOtherTurn.ActualTaskStopped
                && !stoppedWithOtherTurn.TaskStoppedConfirmed,
                "unresolved old-turn activity prevents dangerous stop authorization after visible completion");

            var foreign = new Fixture(root, "foreign-project");
            foreign.Start("turn-foreign", -5);
            var anotherProject = Path.Combine(root, "another-project");
            Directory.CreateDirectory(anotherProject);
            var foreignBinding = foreign.Store.Save(anotherProject, foreign.Session);
            Check(foreignBinding.Success, "an explicit association can select a session whose native project differs");
            var associated = await new BoundCodexStatusObserver(foreignBinding.Binding, foreign.Home, () => foreign.Now).ReadAsync();
            Check(associated.CanonicalState == CodexTaskState.Working && !associated.ProjectAssociationVerified,
                "cross-project association may display observed session Working but does not verify the project");
            Check(associated.EvidenceLevel == CodexEvidenceLevel.SessionMatched && !associated.TaskStoppedConfirmed,
                "cross-project lifecycle remains session-level evidence and cannot inflate to project or lifecycle confirmation");

            var noLifecycle = new Fixture(root, "metadata-only");
            var metadataOnly = await noLifecycle.Observer().ReadAsync();
            Check(metadataOnly.CanonicalState == CodexTaskState.Unknown && metadataOnly.UnknownReason.Length > 0,
                "a valid session metadata file alone is not AI execution evidence");
            Check(metadataOnly.EvidenceLevel != CodexEvidenceLevel.LifecycleConfirmed
                && !metadataOnly.ActualTaskStopped && !metadataOnly.TaskStoppedConfirmed,
                "file presence and metadata cannot establish a lifecycle terminal");
            var empty = noLifecycle.Store.Clear().Binding;
            var noSession = await new BoundCodexStatusObserver(empty, noLifecycle.Home, () => noLifecycle.Now).ReadAsync();
            Check(noSession.CanonicalState == CodexTaskState.Unknown && noSession.EvidenceLevel == CodexEvidenceLevel.None,
                "no explicit session selection is Unknown with no evidence, never auto-latest");
            Check(noSession.EvidencePosition is null && noSession.UnknownReason.Length > 0,
                "unbound Unknown cannot claim an evidence byte cursor");
            return passed;
        }
        finally
        {
            var full = Path.GetFullPath(root);
            if (!full.StartsWith(Path.GetFullPath(baseDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Lifecycle fixture cleanup escaped its isolated D workspace.");
            if (Directory.Exists(full)) Directory.Delete(full, true);
        }
    }

    static ExhaustionReceipt Receipt(DateTimeOffset now)
    {
        var reset = now.AddHours(1).ToUnixTimeSeconds();
        return new()
        {
            CycleKey = ExhaustionDetector.CycleKey(reset), ResetAt = reset,
            PositiveObservedAt = now.AddSeconds(-10), PositiveRemaining = 1,
            FirstZeroObservedAt = now.AddSeconds(-8), SecondZeroObservedAt = now.AddSeconds(-5),
            CreatedAt = now, RemainingPercent = 0, Synthetic = true
        };
    }

    sealed class Fixture
    {
        static readonly UTF8Encoding Utf8 = new(false);
        readonly DateTimeOffset origin;
        public DateTimeOffset Now { get; set; }
        public string Project { get; }
        public string Home { get; }
        public string Data { get; }
        public string Session { get; }
        public string FilePath { get; }
        public CodexSessionBindingStore Store { get; }
        public CodexSessionBinding Binding { get; }
        public long Position => new FileInfo(FilePath).Length;

        public Fixture(string root, string name)
        {
            var directory = Path.Combine(root, name);
            Project = Path.Combine(directory, "project");
            Home = Path.Combine(directory, "codex-home");
            Data = Path.Combine(directory, "data");
            Session = "session-" + name;
            origin = Now = DateTimeOffset.UtcNow;
            Directory.CreateDirectory(Project);
            Directory.CreateDirectory(Data);
            var day = Path.Combine(Home, "sessions", origin.LocalDateTime.ToString("yyyy"),
                origin.LocalDateTime.ToString("MM"), origin.LocalDateTime.ToString("dd"));
            Directory.CreateDirectory(day);
            FilePath = Path.Combine(day, "rollout-" + Session + ".jsonl");
            Record("session_meta", new { id = Session, cwd = Project, originator = "Codex Desktop" }, -60);
            Store = new(Data, Home, () => Now);
            var save = Store.Save(Project, Session);
            if (!save.Success) throw new InvalidOperationException("Could not create isolated lifecycle fixture binding: " + save.Message);
            Binding = save.Binding;
        }

        public DateTimeOffset Time(int seconds) => origin.AddSeconds(seconds);
        public void Start(string turn, int seconds) => Event(new { type = "task_started", turn_id = turn }, seconds);
        public void Event(object payload, int seconds) => Record("event_msg", payload, seconds);
        public void Record(string type, object payload, int seconds)
            => File.AppendAllText(FilePath, JsonSerializer.Serialize(new { type, timestamp = Time(seconds).ToString("O"), payload }) + "\n", Utf8);
        public BoundCodexStatusObserver Observer() => new(Binding, Home, () => Now);
    }
}

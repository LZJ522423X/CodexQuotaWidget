using System.Security.Cryptography;
using System.Text.Json;
using CodexQuotaWidget.Core.Observation;

public static class CodexStateEventChecks
{
    public static int Run()
    {
        var count = 0;
        void Check(bool value, string name) { if (!value) throw new InvalidOperationException("Codex state event: " + name); count++; }
        void Reject(Action action, string name)
        {
            try { action(); throw new InvalidOperationException("Codex state event should reject: " + name); }
            catch (Exception error) when (error is InvalidDataException or JsonException or ArgumentException or IOException) { count++; }
        }
        var root = Path.Combine(@"D:\CodexQuotaWidget-GitHub\artifacts\state-event-checks", Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "project"); Directory.CreateDirectory(project);
        var now = DateTimeOffset.UtcNow;
        var binding = Guid.NewGuid();
        var running = new CodexStatusObservation
        {
            BindingId = binding, State = CodexObservedState.Running, Source = StatusEvidence.LocalSessionSource,
            ObservedAt = now, EvidenceAt = now.AddSeconds(-1), SessionId = "session-1", TurnId = "turn-1",
            ProjectRoot = project, ProjectAssociationVerified = false, TrustLevel = CodexObservationTrustLevel.ObservedSession,
            Message = "DO_NOT_EXPORT_TASK_BODY_OR_COMMAND_CANARY", Synthetic = true
        };
        CodexStateChangedEvent Event(CodexStatusObservation observation, DateTimeOffset? time = null)
            => CodexStateChangedEventFactory.FromObservation(observation, project, observation.BindingId, time ?? now);
        CodexStateChangedEventStore Store(string name) => new(Path.Combine(root, name), allowSynthetic: true);
        var empty = new CodexStatusObservation { ObservedAt = now, Synthetic = true, Message = "尚未绑定明确会话。" };
        var unknown = Event(empty);
        Check(unknown.State == CodexTaskState.Unknown && unknown.SessionId is null && unknown.BindingId == Guid.Empty, "unbound observation exports Unknown, not Working");
        Check(unknown.TrustLevel == CodexObservationTrustLevel.None && unknown.UnknownReason.Length > 0, "unknown contains reason and no observed-session trust");
        var store = Store("normal");
        Check(store.Publish(unknown), "first Unknown is published without requiring a session");
        Check(!File.Exists(store.WorkingPath) && !File.Exists(store.CompletedPath), "Unknown does not create typed working/completed notifications");
        var event1 = Event(running);
        Check(event1.State == CodexTaskState.Working && !event1.ProjectAssociationVerified && event1.TrustLevel == CodexObservationTrustLevel.ObservedSession,
            "explicit user binding can display Working even when metadata project association differs");
        Check(event1.PassiveNotification && !event1.GrantsActionAuthority, "working notification grants no action authority");
        Reject(() => CodexStateChangedEventFactory.FromObservation(running, project, Guid.NewGuid(), now), "old binding cannot populate a new selection");
        Reject(() => CodexStateChangedEventFactory.FromObservation(running with { ProjectRoot = root }, project, binding, now),
            "matching BindingId cannot relabel an observation from another associated project");
        Check(Event(running with { BindingId = Guid.Empty }).State == CodexTaskState.Unknown, "unbound Running downgraded to Unknown");
        Check(Event(running with { TurnId = null }).State == CodexTaskState.Unknown, "no task identity cannot become Working");
        Check(Event(running with { TrustLevel = CodexObservationTrustLevel.None }).State == CodexTaskState.Unknown, "no observed-session trust cannot become Working");
        Check(Event(running with { EvidenceAt = null }).State == CodexTaskState.Unknown, "poll timestamp cannot replace absent actual evidence");
        var futureEvidence = Event(running with { EvidenceAt = now.AddSeconds(1) });
        Check(futureEvidence.State == CodexTaskState.Unknown && futureEvidence.EvidenceAt is null && futureEvidence.LastActivity is null,
            "future actual evidence becomes Unknown with no substituted poll evidence");
        Check(Store("invalid-time-unknown").Publish(futureEvidence), "invalid time can publish honest Unknown instead of invented fresh task state");
        var productionPolicy = new CodexStateChangedEventStore(Path.Combine(root, "production-policy"));
        Reject(() => productionPolicy.Publish(event1), "synthetic status cannot write under production policy");
        Check(!Directory.Exists(Path.GetDirectoryName(productionPolicy.LatestPath)), "rejected synthetic payload creates no event directory");
        Reject(() => new CodexStateChangedEventStore(@"D:\CodexQuotaWidgetData", allowSynthetic: true), "synthetic override forbidden for production data directory");
        var cProject = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var cProjectMetadata = CodexStateChangedEventFactory.FromObservation(running with { ProjectRoot = cProject }, cProject, binding, now);
        var cProjectStore = Store("c-project-metadata");
        Check(cProjectStore.Publish(cProjectMetadata) && cProjectStore.ReadCurrent()!.ProjectRoot == cProject,
            "C project is metadata only; state event output remains in isolated D fixture");
        Check(cProjectStore.LatestPath.StartsWith(@"D:\", StringComparison.OrdinalIgnoreCase), "C project never moves event storage off D");

        var reserved = Path.Combine(Path.GetDirectoryName(store.LatestPath)!, "codex_exhausted.json");
        File.WriteAllText(reserved, "RESERVED_QUOTA_AND_STOPPED_WRITER_ONLY");
        Check(store.Publish(event1), "new selected working session published");
        var working1 = JsonSerializer.Deserialize<CodexStateChangedEvent>(File.ReadAllText(store.WorkingPath))!;
        Check(CodexStateChangedEventStore.IsCurrentTypedEvent(working1, store.ReadCurrent()), "working typed file matches authoritative primary");
        var before = Hash(store.LatestPath); var workingHash = Hash(store.WorkingPath);
        var poll = Event(running with { ObservedAt = now.AddSeconds(10) }, now.AddSeconds(10));
        Check(!store.Publish(poll), "fresh poll with same actual evidence produces no new event");
        Check(before == Hash(store.LatestPath) && workingHash == Hash(store.WorkingPath), "exact poll dedup leaves bytes unchanged");
        Check(!Store("normal").Publish(poll) && before == Hash(store.LatestPath), "restart dedup uses persisted evidence, not fresh process timestamp");
        Check(!File.ReadAllText(store.LatestPath).Contains("DO_NOT_EXPORT_TASK_BODY_OR_COMMAND_CANARY", StringComparison.Ordinal), "task/message/command content not exported");

        var completedObservation = running with { State = CodexObservedState.Completed, ObservedAt = now.AddSeconds(1), EvidenceAt = now,
            TaskStoppedConfirmed = false, ActualTaskStopped = true };
        var completed = Event(completedObservation, now.AddSeconds(1));
        Check(store.Publish(completed) && completed.State == CodexTaskState.Completed, "task-reported completion is passive metadata despite no dangerous-stop authorization");
        var completedFile = JsonSerializer.Deserialize<CodexStateChangedEvent>(File.ReadAllText(store.CompletedPath))!;
        Check(CodexStateChangedEventStore.IsCurrentTypedEvent(completedFile, store.ReadCurrent()), "completed typed file current only against primary");
        Check(!CodexStateChangedEventStore.IsCurrentTypedEvent(working1, store.ReadCurrent()), "old Working file cannot be interpreted as current Completed state");
        var disconnect = Event(completedObservation with { State = CodexObservedState.Unknown, ObservedAt = now.AddSeconds(2),
            Message = "会话读取暂时中断，当前状态未知。" }, now.AddSeconds(2));
        Check(store.Publish(disconnect), "disconnect publishes authoritative Unknown");
        Check(!CodexStateChangedEventStore.IsCurrentTypedEvent(completedFile, store.ReadCurrent()), "last Completed becomes historical after Unknown");
        Check(store.Publish(Event(completedObservation with { ObservedAt = now.AddSeconds(3) }, now.AddSeconds(3))), "same actual terminal evidence can restore Completed after explicit Unknown transition");

        var binding2 = Guid.NewGuid();
        var newBinding = Event(completedObservation with { BindingId = binding2, SessionId = "session-2", ObservedAt = now.AddSeconds(4) }, now.AddSeconds(4));
        Check(store.Publish(newBinding), "new session binding emits a new event even with same canonical state");
        Check(store.ReadCurrent()!.BindingId == binding2 && !CodexStateChangedEventStore.IsCurrentTypedEvent(completedFile, store.ReadCurrent()), "old binding typed evidence is not current");
        var changedTurn = Event(completedObservation with { BindingId = binding2, SessionId = "session-2", TurnId = "turn-2",
            State = CodexObservedState.WaitingForInput, EvidenceAt = now.AddSeconds(4) }, now.AddSeconds(5));
        Check(store.Publish(changedTurn) && store.ReadCurrent()!.State == CodexTaskState.Waiting, "turn change Waiting emitted without overwriting last-type semantics");
        var exhausted = Event(running with { BindingId = binding2, SessionId = "session-2", TurnId = "turn-2",
            State = CodexObservedState.QuotaExhausted, EvidenceAt = now.AddSeconds(5) }, now.AddSeconds(6));
        Check(store.Publish(exhausted) && store.ReadCurrent()!.State == CodexTaskState.Exhausted, "TaskReportedOnly Exhausted uses only generic state event");
        Check(File.ReadAllText(reserved) == "RESERVED_QUOTA_AND_STOPPED_WRITER_ONLY", "quota exhaustion writer's schema and file never touched");
        var retryableError = Event(running with { BindingId = binding2, SessionId = "session-2", TurnId = "turn-2",
            State = CodexObservedState.UnableToContinue, EvidenceAt = now.AddSeconds(6) }, now.AddSeconds(7));
        Check(store.Publish(retryableError) && store.ReadCurrent()!.State == CodexTaskState.Unknown,
            "error without a terminal event cannot publish Failed");
        var failed = Event(running with { BindingId = binding2, SessionId = "session-2", TurnId = "turn-2",
            State = CodexObservedState.UnableToContinue, ActualTaskStopped = true, EvidenceAt = now.AddSeconds(6) }, now.AddSeconds(7));
        Check(store.Publish(failed) && store.ReadCurrent()!.State == CodexTaskState.Failed, "Failed passive state emitted");
        Check(!Directory.GetFiles(Path.GetDirectoryName(store.LatestPath)!, "*.tmp").Any()
            && !File.Exists(Path.Combine(Path.GetDirectoryName(store.LatestPath)!, ".codex-state-event-write.lock")), "temporary write artifacts cleaned");
        Check(Directory.GetFiles(Path.GetDirectoryName(store.LatestPath)!, "codex_*.json").Length == 5, "fixed latest/type/timeline files remain bounded and reserved event is unchanged");
        Reject(() => store.Publish(failed with { GrantsActionAuthority = true }), "event cannot grant action authority");
        Reject(() => store.Publish(failed with { PassiveNotification = false }), "active event payload rejected");
        Reject(() => store.Publish(failed with { BindingId = Guid.Empty }), "nonunknown state requires explicit binding");

        var corrupted = Store("corrupted"); Check(corrupted.Publish(event1), "corruption fixture initialized");
        File.WriteAllText(corrupted.LatestPath, "{broken-primary");
        Reject(() => corrupted.Publish(completed), "corrupt primary does not restore old backup or overwrite evidence");
        Check(File.ReadAllText(corrupted.LatestPath) == "{broken-primary", "corrupt primary preserved");
        var missingPrimary = Store("missing-primary"); missingPrimary.Publish(event1); missingPrimary.Publish(completed);
        File.Delete(missingPrimary.LatestPath);
        Check(File.Exists(missingPrimary.LatestPath + ".bak"), "surviving backup fixture exists");
        Reject(() => missingPrimary.Publish(disconnect), "missing primary plus surviving backup cannot rearm notification");
        Check(!File.Exists(missingPrimary.LatestPath), "missing primary is not recreated from backup");

        var interruptedType = Store("partial-type"); interruptedType.Publish(event1);
        // Model an interruption after authoritative primary committed but before the type file replaced.
        var committedCompleted = completed with { SequenceNumber = interruptedType.ReadCurrent()!.SequenceNumber + 1 };
        File.WriteAllText(interruptedType.LatestPath, JsonSerializer.Serialize(committedCompleted));
        Check(!interruptedType.Publish(completed with { EventId = Guid.NewGuid(), CreatedAt = now.AddSeconds(3) }), "partial type write repaired without issuing new logical event");
        var repaired = JsonSerializer.Deserialize<CodexStateChangedEvent>(File.ReadAllText(interruptedType.CompletedPath))!;
        Check(repaired.EventId == completed.EventId && repaired.SequenceNumber == committedCompleted.SequenceNumber
            && CodexStateChangedEventStore.IsCurrentTypedEvent(repaired, interruptedType.ReadCurrent()), "repair retains committed EventId/sequence and current-state relationship");
        var corruptType = Store("corrupt-type"); corruptType.Publish(event1);
        var latestBefore = Hash(corruptType.LatestPath); File.WriteAllText(corruptType.WorkingPath, "{corrupt-type");
        Reject(() => corruptType.Publish(completed), "existing type corruption prevents overwriting primary or type evidence");
        Check(Hash(corruptType.LatestPath) == latestBefore && File.ReadAllText(corruptType.WorkingPath) == "{corrupt-type", "type corruption preflight is failclosed");
        var badShape = Store("bad-shape"); badShape.Publish(event1); File.WriteAllText(badShape.LatestPath, "{}");
        Reject(() => badShape.Publish(completed), "valid JSON with missing required event fields rejected");
        var missingIdentity = Store("missing-event-id"); missingIdentity.Publish(unknown);
        var noIdentityJson = JsonSerializer.SerializeToNode(unknown)!; noIdentityJson.AsObject().Remove("EventId");
        File.WriteAllText(missingIdentity.LatestPath, noIdentityJson.ToJsonString());
        Reject(() => missingIdentity.Publish(event1), "missing stored EventId cannot silently generate a new identity during deserialization");
        count += CodexTimelineChecks.Run();
        return count;
    }

    static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
}

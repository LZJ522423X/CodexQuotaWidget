using System.Security.Cryptography;
using System.Text.Json;
using CodexQuotaWidget.Core.Observation;

/// <summary>Isolated D-drive fixtures only. No external process, service or power actions.</summary>
public static class CodexTimelineChecks
{
    public static int Run()
    {
        var count = 0;
        void Check(bool value, string name) { if (!value) throw new InvalidOperationException("Codex timeline: " + name); count++; }
        void Reject(Action action, string name)
        {
            try { action(); throw new InvalidOperationException("Codex timeline should reject: " + name); }
            catch (Exception error) when (error is InvalidDataException or JsonException or ArgumentException or IOException or UnauthorizedAccessException) { count++; }
        }
        var root = Path.Combine(@"D:\CodexQuotaWidget-GitHub\artifacts\state-event-checks", Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "project"); Directory.CreateDirectory(project);
        var now = DateTimeOffset.UtcNow;
        var binding = Guid.NewGuid();
        var observation = new CodexStatusObservation
        {
            BindingId = binding, State = CodexObservedState.Running, ObservedAt = now,
            EvidenceAt = now.AddSeconds(-10), SessionId = "timeline-session", TurnId = "timeline-turn",
            ProjectRoot = project, ProjectAssociationVerified = false, TrustLevel = CodexObservationTrustLevel.ObservedSession,
            EvidenceLevel = CodexEvidenceLevel.SessionMatched, EvidenceKind = "task_started", EvidencePosition = 100,
            Synthetic = true
        };
        CodexStateChangedEvent Make(CodexStatusObservation value, DateTimeOffset? at = null)
            => CodexStateChangedEventFactory.FromObservation(value, project, value.BindingId, at ?? now);
        CodexStateChangedEventStore Store(string name) => new(Path.Combine(root, name), allowSynthetic: true);

        var store = Store("sequence"); var firstInput = Make(observation);
        Check(firstInput.SchemaVersion == 2 && firstInput.SequenceNumber == 0, "factory produces an uncommitted schema2 observation");
        Check(store.Publish(firstInput), "initial native-order sample committed");
        var first = store.ReadCurrent()!;
        Check(first.SequenceNumber == 1 && first.EvidenceLevel == CodexEvidenceLevel.SessionMatched
            && first.EvidenceKind == "task_started" && first.EvidencePosition == 100, "first persisted sequence/evidence fields map exactly");
        var currentHash = Hash(store.LatestPath); var historyHash = Hash(store.TimelinePath);
        Check(!Store("sequence").Publish(Make(observation, now.AddMinutes(1))), "restart exact duplicate returns false");
        Check(currentHash == Hash(store.LatestPath) && historyHash == Hash(store.TimelinePath), "duplicate neither increments sequence nor appends history");
        var nextObservation = observation with { State = CodexObservedState.WaitingForInput, EvidenceAt = now.AddSeconds(-9),
            EvidencePosition = 200, EvidenceKind = "request_user_input" };
        Check(store.Publish(Make(nextObservation) with { SequenceNumber = 9_999 }), "caller supplied sequence is ignored");
        Check(store.ReadCurrent()!.SequenceNumber == 2, "persistent sequence comes from locked commit marker only");
        var rollback = nextObservation with { State = CodexObservedState.Running, EvidenceAt = now.AddMinutes(-2),
            EvidencePosition = 300, EvidenceKind = "task_started" };
        Check(store.Publish(Make(rollback, now.AddMinutes(-1))), "newer log position accepted across system time rollback");
        var afterRollback = store.ReadCurrent()!;
        Check(afterRollback.SequenceNumber == 3 && afterRollback.CreatedAt < first.CreatedAt
            && store.ReadTimeline().Select(entry => entry.SequenceNumber).SequenceEqual(new long[] { 1, 2, 3 }),
            "timeline is ordered by persisted sequence, never creation/evidence wall clock");
        Reject(() => store.Publish(Make(nextObservation with { EvidenceAt = now.AddSeconds(-1) })), "older log position cannot overwrite newer evidence even with later timestamp");
        Check(store.ReadCurrent()!.EventId == afterRollback.EventId && store.ReadTimeline().Count == 3, "rejected stale cursor preserves current/history");
        var newBinding = Guid.NewGuid();
        Check(store.Publish(Make(observation with { BindingId = newBinding, SessionId = "other-session", EvidencePosition = 1 })), "binding switch can start a different log cursor");
        Check(store.ReadCurrent()!.SequenceNumber == 4 && store.ReadTimeline()[^1].BindingId == newBinding,
            "binding switch retains data-root-wide monotonic sequence");
        Check(store.ReadTimeline().All(entry => entry.PassiveNotification && !entry.GrantsActionAuthority), "no historical sample grants action authority");

        var highConfidence = observation with { EvidenceLevel = CodexEvidenceLevel.LifecycleConfirmed,
            ProjectAssociationVerified = true, TrustLevel = CodexObservationTrustLevel.ProjectVerified };
        Check(Make(highConfidence with { BindingId = Guid.Empty }).EvidenceLevel == CodexEvidenceLevel.None,
            "unbound downgrade strips lifecycle confidence");
        var noTurn = Make(highConfidence with { TurnId = null });
        Check(noTurn.State == CodexTaskState.Unknown && noTurn.EvidenceLevel == CodexEvidenceLevel.ProjectMatched,
            "missing Turn cannot keep lifecycle-confirmed level");
        var future = Make(highConfidence with { EvidenceAt = now.AddSeconds(1) });
        Check(future.State == CodexTaskState.Unknown && future.EvidenceLevel == CodexEvidenceLevel.ProjectMatched,
            "future evidence downgrade strips lifecycle-confirmed level");
        var unmatchedFuture = Make(highConfidence with { ProjectAssociationVerified = false, EvidenceAt = now.AddSeconds(1) });
        Check(unmatchedFuture.EvidenceLevel == CodexEvidenceLevel.SessionMatched, "unmatched project caps unknown at session level");
        var contradictory = Store("same-cursor"); contradictory.Publish(firstInput);
        var terminalObservation = highConfidence with { State = CodexObservedState.Completed, EvidenceKind = "task_complete",
            EvidencePosition = 200, EvidenceAt = now.AddSeconds(-9) };
        contradictory.Publish(Make(terminalObservation));
        var terminal = contradictory.ReadCurrent()!;
        Reject(() => contradictory.Publish(Make(terminalObservation with { State = CodexObservedState.Unknown, Message = "身份复用隔离测试。" })
            with { EventId = terminal.EventId }), "new logical state cannot reuse a committed historical identity");
        Reject(() => contradictory.Publish(Make(terminalObservation with { State = CodexObservedState.Running, EvidenceKind = "task_started" })),
            "same native cursor cannot rewrite terminal as Working");
        Reject(() => contradictory.Publish(Make(terminalObservation with { EvidenceKind = "turn_complete" })),
            "same native cursor cannot claim another typed evidence kind");
        Reject(() => contradictory.Publish(Make(terminalObservation with { TurnId = "other-turn" })),
            "same native cursor cannot claim a different Turn");
        Reject(() => contradictory.Publish(Make(terminalObservation with { EvidenceAt = now.AddSeconds(-1) })),
            "fresh poll clock cannot renew an unchanged native evidence line");
        Check(contradictory.ReadCurrent()!.EventId == terminal.EventId, "same-cursor contradictions preserve latest commit");
        Check(contradictory.Publish(Make(terminalObservation with { State = CodexObservedState.Unknown, Message = "证据暂时不可用。" })),
            "Unknown protective downgrade allowed at same cursor");
        Reject(() => contradictory.Publish(Make(terminalObservation with { State = CodexObservedState.Running, EvidenceKind = "task_started" })),
            "Unknown transition cannot launder same-cursor contradictory replay");
        Check(contradictory.Publish(Make(terminalObservation)), "exact native terminal evidence may recover after Unknown");
        contradictory.Publish(Make(terminalObservation with { State = CodexObservedState.Unknown, Message = "证据读取失败，位置未知。", EvidencePosition = null, EvidenceKind = "" }));
        Reject(() => contradictory.Publish(Make(observation with { EvidenceAt = now.AddSeconds(-1) })),
            "history only acts as anti-replay cursor floor when Unknown has no current cursor");
        Reject(() => contradictory.Publish(Make(observation with { EvidenceKind = "arbitrary_sensitive_text" })), "evidence kind is a controlled allowlist only");
        var noCursor = Store("same-time-without-cursor");
        noCursor.Publish(Make(terminalObservation with { EvidencePosition = null }));
        Reject(() => noCursor.Publish(Make(terminalObservation with { EvidencePosition = null, State = CodexObservedState.Running, EvidenceKind = "task_started" })),
            "equal timestamp without native position cannot order contradictory legacy states");

        var legacyStore = Store("schema1"); Directory.CreateDirectory(Path.GetDirectoryName(legacyStore.LatestPath)!);
        var legacyNode = JsonSerializer.SerializeToNode(firstInput with { SchemaVersion = 1 })!.AsObject();
        foreach (var field in new[] { "SequenceNumber", "EvidenceLevel", "EvidenceKind", "EvidencePosition" }) legacyNode.Remove(field);
        File.WriteAllText(legacyStore.LatestPath, legacyNode.ToJsonString());
        Check(legacyStore.ReadCurrent() is { SchemaVersion: 1, SequenceNumber: 0 }, "old schema1 files with no new fields remain readable");
        Reject(() => legacyStore.Publish(Make(observation with { State = CodexObservedState.Completed, EvidenceKind = "task_complete" })),
            "legacy missing kind does not permit same-time state reversal during migration");
        Reject(() => legacyStore.Publish(Make(observation with { TurnId = "other-legacy-turn" })),
            "legacy missing kind does not permit same-time Turn replacement during migration");
        // Missing schema1 optional metadata is not an explicit contradictory typed event. Only
        // the unchanged State/Turn/evidence time can gain the controlled kind during migration.
        var migrationInput = firstInput;
        Check(legacyStore.Publish(migrationInput) && legacyStore.ReadCurrent() is { SchemaVersion: 2, SequenceNumber: 1 }, "same-state schema2 observation supplements legacy metadata without invented prior sequence");
        Check(!legacyStore.Publish(migrationInput with { SchemaVersion = 1 }), "old caller's identical payload remains idempotent after migration");
        var missingField = Store("schema2-required"); missingField.Publish(firstInput);
        var missingNode = JsonSerializer.SerializeToNode(missingField.ReadCurrent())!.AsObject(); missingNode.Remove("EvidencePosition");
        File.WriteAllText(missingField.LatestPath, missingNode.ToJsonString());
        Reject(() => missingField.ReadCurrent(), "schema2 missing explicit evidence field is not silently defaulted");
        var duplicateField = Store("duplicate-json-field"); duplicateField.Publish(firstInput);
        var validJson = File.ReadAllText(duplicateField.LatestPath);
        File.WriteAllText(duplicateField.LatestPath, validJson.TrimEnd()[..^1] + ",\"SequenceNumber\":1}");
        Reject(() => duplicateField.ReadCurrent(), "duplicate JSON field cannot use implicit last-value parsing");

        var interrupted = Store("history-interruption"); interrupted.Publish(firstInput);
        // Occupying the history backup destination makes File.Replace fail only after the
        // primary's sequence2 commit. Its readable primary/history preflight still succeeds.
        Directory.CreateDirectory(interrupted.TimelinePath + ".bak");
        Reject(() => interrupted.Publish(Make(nextObservation)), "simulated history write failure reported after primary committed");
        var committed = interrupted.ReadCurrent()!;
        Check(committed.SequenceNumber == 2 && committed.State == CodexTaskState.Waiting, "history failure cannot roll primary or sequence backward");
        Directory.Delete(interrupted.TimelinePath + ".bak");
        Check(!interrupted.Publish(Make(nextObservation, now.AddMinutes(1))), "next duplicate repairs missing history without new sequence");
        Check(interrupted.ReadCurrent()!.EventId == committed.EventId && interrupted.ReadCurrent()!.SequenceNumber == 2
            && interrupted.ReadTimeline().Select(entry => entry.SequenceNumber).SequenceEqual(new long[] { 1, 2 }),
            "history repair retains committed identity/sequence exactly");

        var manuallyRolledBack = Store("rolledback-primary"); manuallyRolledBack.Publish(firstInput);
        var oldPrimary = File.ReadAllText(manuallyRolledBack.LatestPath); manuallyRolledBack.Publish(Make(nextObservation));
        File.WriteAllText(manuallyRolledBack.LatestPath, oldPrimary);
        Reject(() => manuallyRolledBack.Publish(Make(rollback)), "newer historical high-water mark rejects restored older primary");
        Check(manuallyRolledBack.ReadCurrent()!.SequenceNumber == 1 && File.ReadAllText(manuallyRolledBack.LatestPath) == oldPrimary,
            "inconsistent manually restored fixture preserved without counter reuse");
        Reject(() => manuallyRolledBack.ReadTimeline(), "UI history rejects a high-water mark inconsistent with reliable latest");

        var bounded = Store("bounded");
        for (var index = 0; index < CodexStateChangedEventStore.MaximumTimelineEntries + 8; index++)
            bounded.Publish(Make(observation with { State = CodexObservedState.Unknown, Message = "隔离状态采样 " + index, EvidencePosition = index + 1 }));
        var retained = bounded.ReadTimeline();
        Check(retained.Count == 256 && retained[0].SequenceNumber == 9 && retained[^1].SequenceNumber == 264,
            "history retains exactly latest256 samples and drops oldest8");
        Check(new FileInfo(bounded.TimelinePath).Length < 4 * 1024 * 1024
            && Directory.GetFiles(Path.GetDirectoryName(bounded.TimelinePath)!, "*.json").Length <= 4,
            "timeline storage has fixed file/byte limits");
        Check(!Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories).Any()
            && !Directory.GetFiles(root, ".codex-state-event-write.lock", SearchOption.AllDirectories).Any(),
            "successful and failed commits clean temporary output/leases");
        var concurrent = Store("concurrent");
        Parallel.For(0, 12, index =>
        {
            var competing = Store("concurrent");
            var input = Make(observation with { BindingId = Guid.NewGuid(), State = CodexObservedState.Unknown, Message = "并发隔离样本 " + index });
            for (var attempt = 0; ; attempt++)
            {
                try { competing.Publish(input); break; }
                catch (IOException) when (attempt < 100) { Thread.Sleep(5); }
            }
        });
        Check(concurrent.ReadCurrent()!.SequenceNumber == 12 && concurrent.ReadTimeline().Count == 12
            && concurrent.ReadTimeline().Select(entry => entry.SequenceNumber).SequenceEqual(Enumerable.Range(1, 12).Select(value => (long)value)),
            "cross-instance write lease serialises persistent sequence allocation");
        return count;
    }

    static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
}

using System.Text;
using System.Text.Json;
using CodexQuotaWidget.Core;
using CodexQuotaWidget.Core.Observation;

public static class CodexSessionBindingChecks
{
    public static async Task<int> RunAsync()
    {
        const string baseDirectory = @"D:\CodexQuotaWidget-GitHub\artifacts\session-binding-checks";
        var root = Path.Combine(baseDirectory, Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "project");
        var home = Path.Combine(root, "codex-home");
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(data);
        var now = DateTimeOffset.UtcNow;
        var passed = 0;
        void Check(bool condition, string label)
        { if (!condition) throw new InvalidOperationException("Session binding: " + label); passed++; }
        string Record(string type, object payload, int seconds = 0) => JsonSerializer.Serialize(new { type, timestamp = now.AddSeconds(seconds).ToString("O"), payload });
        string Meta(string id, string? cwd = null) => Record("session_meta", new { id, cwd = cwd ?? project, originator = "Codex Desktop", base_instructions = "do-not-return-prose" });
        string Start(string turn = "turn-a", int seconds = -2) => Record("event_msg", new { type = "task_started", turn_id = turn }, seconds);
        string Complete(string turn = "turn-a", int seconds = -1) => Record("event_msg", new { type = "task_complete", turn_id = turn }, seconds);
        string Write(string id, params string[] records)
        {
            var day = Path.Combine(home, "sessions", now.LocalDateTime.ToString("yyyy"), now.LocalDateTime.ToString("MM"), now.LocalDateTime.ToString("dd"));
            Directory.CreateDirectory(day);
            var path = Path.Combine(day, "rollout-" + id + ".jsonl");
            File.WriteAllText(path, string.Join('\n', records) + "\n", new UTF8Encoding(false));
            return path;
        }
        var store = new CodexSessionBindingStore(data, home, () => now);
        try
        {
            var initial = store.Load();
            Check(initial.Success && !initial.Binding.IsBound, "missing binding is empty and never auto-selects newest session");
            var path = Write("session-a", Meta("session-a"), Start());
            var catalog = await new CodexSessionCatalog(home, () => now).ReadAsync();
            Check(catalog.Success && catalog.CoverageComplete && catalog.Sessions.Length == 1, "catalog reads bounded native-shaped metadata");
            Check(catalog.Sessions[0].SessionId == "session-a" && catalog.Sessions[0].DeclaredProject == project, "catalog preserves declared project and native session id");
            Check(!JsonSerializer.Serialize(catalog).Contains("do-not-return-prose", StringComparison.Ordinal), "metadata prose/base instructions never enter catalog output");
            var saved = store.Save(project, "session-a");
            Check(saved.Success && saved.Binding.IsBound && saved.Binding.BindingId != Guid.Empty, "explicit project and session selection is persisted atomically");
            Check(saved.Binding.SessionFilePath == path && File.Exists(store.BindingPath), "only catalog path is persisted and binding data is on D");
            Check(store.Load().Binding.BindingId == saved.Binding.BindingId, "load preserves exact session selection rather than newest candidate");
            var working = await new BoundCodexStatusObserver(saved.Binding, home, () => now).ReadAsync();
            Check(working.CanonicalState == CodexTaskState.Working && working.SessionId == "session-a" && working.BindingId == saved.Binding.BindingId, "selected true start becomes Working with coherent binding identity");
            Check(working.ProjectAssociationVerified && working.TrustLevel == CodexObservationTrustLevel.ProjectVerified && !working.TaskStoppedConfirmed, "native project scope is verified without claiming running is stopped");
            Check(working.Synthetic, "fixture home/clock cannot masquerade as native real observation");
            var repeatedSave = store.Save(project, "session-a");
            Check(repeatedSave.Success && repeatedSave.Binding.BindingId != saved.Binding.BindingId, "every explicit save invalidates in-flight previous binding results");
            Write("session-new", Meta("session-new"), Start("turn-new", 0));
            var stillSelected = await new BoundCodexStatusObserver(repeatedSave.Binding, home, () => now).ReadAsync();
            Check(stillSelected.SessionId == "session-a" && stillSelected.TurnId == "turn-a", "newest unrelated candidate never replaces saved session");
            var missingSession = store.Save(project, "session-missing");
            Check(!missingSession.Success && store.Load().Binding.SessionId == "session-a", "failed selection does not overwrite prior binding");
            Check(!store.Save(project + ":stream", "session-a").Success, "alternate data stream project path rejected");
            Check(!store.Save(@"\\server\share\project", "session-a").Success, "UNC project rejected");
            Check(!store.Save(Path.Combine(root, "missing-project"), "session-a").Success, "nonexistent project rejected");
            var cProject = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var cSaved = store.Save(cProject, "session-a");
            Check(cSaved.Success && cSaved.Binding.ProjectRoot.StartsWith("C:", StringComparison.OrdinalIgnoreCase), "existing ordinary local C project is permitted without moving it");
            var manualAssociation = await new BoundCodexStatusObserver(cSaved.Binding, home, () => now).ReadAsync();
            Check(manualAssociation.CanonicalState == CodexTaskState.Working && !manualAssociation.ProjectAssociationVerified, "explicit association displays real session state even when native project differs");
            Check(manualAssociation.DeclaredProject == project && manualAssociation.ProjectRoot == cProject && manualAssociation.TrustLevel == CodexObservationTrustLevel.ObservedSession,
                "manual and declared projects remain distinguishable; trust is not inflated");
            Check(!manualAssociation.TaskStoppedConfirmed && !manualAssociation.CoverageComplete, "explicit association never bypasses dangerous-action project verification");
            Check(!(manualAssociation with { TaskStoppedConfirmed = true }).TaskStoppedConfirmed, "unverified association cannot expose a true dangerous stop flag even through object initialization");
            Write("session-a", Meta("session-a"), Start(), Complete());
            var associatedTerminal = await new BoundCodexStatusObserver(cSaved.Binding, home, () => now).ReadAsync();
            Check(associatedTerminal.CanonicalState == CodexTaskState.Completed && associatedTerminal.ActualTaskStopped && !associatedTerminal.TaskStoppedConfirmed,
                "actual selected task stopped can be shown while dangerous stop authorization stays false");
            Check(!StatusEvidence.ValidateTerminal(associatedTerminal with { Synthetic = false, CoverageComplete = true, TaskStoppedConfirmed = true }, now, cProject),
                "terminal validation explicitly rejects an unverified project association");
            var verifiedSave = store.Save(project, "session-a");
            var blockedByForeign = await new BoundCodexStatusObserver(verifiedSave.Binding, home, () => now).ReadAsync();
            Check(blockedByForeign.CanonicalState == CodexTaskState.Completed && blockedByForeign.ActualTaskStopped && !blockedByForeign.TaskStoppedConfirmed,
                "independent other-session running still blocks formal closure");
            Write("session-new", Meta("session-new"), Start("turn-new", -2), Complete("turn-new", -1));
            var nativeTerminal = await new BoundCodexStatusObserver(verifiedSave.Binding, home, () => now).ReadAsync();
            Check(nativeTerminal.CanonicalState == CodexTaskState.Completed && nativeTerminal.TaskStoppedConfirmed && nativeTerminal.CoverageComplete,
                "formal stop requires selected terminal plus unchanged global safety coverage");
            Write("session-a", Meta("session-a"), Start(), Record("event_msg", new { type = "request_user_input", turn_id = "turn-a", call_id = "request" }, -1));
            var waiting = await new BoundCodexStatusObserver(verifiedSave.Binding, home, () => now).ReadAsync();
            Check(waiting.CanonicalState == CodexTaskState.Waiting && !waiting.ActualTaskStopped, "explicit selected request becomes Waiting");
            Write("session-a", Meta("session-a"), Start(), Record("event_msg", new { type = "task_complete", turn_id = "turn-a", error = new { codex_error_info = "unauthorized" } }, -1));
            var failed = await new BoundCodexStatusObserver(verifiedSave.Binding, home, () => now).ReadAsync();
            Check(failed.CanonicalState == CodexTaskState.Failed && failed.ActualTaskStopped, "selected unsuccessful terminal becomes Failed");
            Write("session-a", Meta("session-a"), Start(), Record("event_msg", new { type = "task_complete", turn_id = "turn-a", error = new { codex_error_info = "usage_limit_exceeded" } }, -1));
            var exhausted = await new BoundCodexStatusObserver(verifiedSave.Binding, home, () => now).ReadAsync();
            Check(exhausted.CanonicalState == CodexTaskState.Exhausted && exhausted.ActualTaskStopped, "selected typed usage terminal becomes Exhausted");
            Write("session-a", Meta("session-a"), Start(), Record("event_msg", new { type = "stream_error", codex_error_info = "response_stream_connection_failed" }, -1));
            var network = await new BoundCodexStatusObserver(verifiedSave.Binding, home, () => now).ReadAsync();
            Check(network.CanonicalState == CodexTaskState.Unknown && network.UnknownReason.Length > 0 && !network.ActualTaskStopped,
                "network retry is Unknown with a reason, not Failed or Exhausted");
            Write("session-a", Meta("session-a"), Start(), Record("event_msg", new { type = "error", codex_error_info = "usage_limit_exceeded" }, -1));
            var usageWithoutTerminal = await new BoundCodexStatusObserver(verifiedSave.Binding, home, () => now).ReadAsync();
            Check(usageWithoutTerminal.CanonicalState == CodexTaskState.Exhausted && !usageWithoutTerminal.ActualTaskStopped && !usageWithoutTerminal.TaskStoppedConfirmed,
                "reported usage error cannot pretend task has stopped");
            Write("session-a", Meta("session-a"), Start());
            File.AppendAllText(path, Complete(), new UTF8Encoding(false));
            var partial = await new BoundCodexStatusObserver(verifiedSave.Binding, home, () => now).ReadAsync();
            Check(partial.CanonicalState == CodexTaskState.Unknown && partial.UnknownReason.Contains("JSONL", StringComparison.Ordinal), "unfinished selected JSONL never confirms a terminal");
            Write("session-a", Meta("session-a"), Start(seconds: -180), Complete(seconds: -170));
            var stale = await new BoundCodexStatusObserver(verifiedSave.Binding, home, () => now).ReadAsync();
            Check(stale.CanonicalState == CodexTaskState.Unknown && !stale.ActualTaskStopped && stale.BindingId == verifiedSave.Binding.BindingId,
                "stale selected evidence remains explicitly selected but unknown");
            Write("session-a", Meta("session-a"), Record("response_item", new { text = new string('x', 2 * 1024 * 1024) }, -3), Start(), Record("response_item", new { text = new string('y', 1024 * 1024) }, -1), Record("event_msg", new { type = "item_completed", turn_id = "turn-a" }));
            var large = await new BoundCodexStatusObserver(verifiedSave.Binding, home, () => now).ReadAsync();
            Check(large.CanonicalState == CodexTaskState.Working && large.LastActivity == now, "selected 8MB budget finds actual start beyond old global 512KB tail");
            Check(!large.TaskStoppedConfirmed && !large.CoverageComplete, "larger selected observation budget does not replace global safety");
            Write("session-a", Meta("session-a"), Record("event_msg", new { type = "task_complete", turn_id = "historical-turn" }, -4), Start(), Complete());
            var historicalPrefix = await new BoundCodexStatusObserver(verifiedSave.Binding, home, () => now).ReadAsync();
            Check(historicalPrefix.CanonicalState == CodexTaskState.Completed && historicalPrefix.ActualTaskStopped && !historicalPrefix.TaskStoppedConfirmed,
                "latest explicit lifecycle can be displayed while historical global gap still blocks dangerous actions");
            Write("session-a", Meta("session-a"), Start(), Record("response_item", new { text = new string('x', 8 * 1024 * 1024 + 512) }), Complete());
            var beyondLimit = await new BoundCodexStatusObserver(verifiedSave.Binding, home, () => now).ReadAsync();
            Check(beyondLimit.CanonicalState == CodexTaskState.Unknown && !beyondLimit.ActualTaskStopped, "selected fixed read cap does not grow indefinitely or fabricate missing start");
            Write("session-a", Meta("session-a"), Start(), Complete());
            var incremental = new BoundCodexStatusObserver(verifiedSave.Binding, home, () => now);
            var firstTerminal = await incremental.ReadAsync();
            File.AppendAllText(path, Start("turn-next", 0) + "\n", new UTF8Encoding(false));
            var nextWorking = await incremental.ReadAsync();
            Check(firstTerminal.ActualTaskStopped && nextWorking.CanonicalState == CodexTaskState.Working && nextWorking.TurnId == "turn-next" && !nextWorking.ActualTaskStopped,
                "incremental selected reader invalidates prior terminal for the real next turn");
            var lateSubagent = Record("event_msg", new
            {
                type = "item_completed", thread_id = "session-a", turn_id = "previous-turn",
                item = new { type = "SubAgentActivity", id = "subagent-completed-child" }
            }, -1);
            Write("session-a", Meta("session-a"), Start(), lateSubagent);
            var lateNotification = await new BoundCodexStatusObserver(verifiedSave.Binding, home, () => now).ReadAsync();
            Check(lateNotification.CanonicalState == CodexTaskState.Working && lateNotification.TurnId == "turn-a" && lateNotification.HasUnresolvedOtherTurnActivity,
                "real Desktop schema: prior-turn subagent completion does not corrupt selected current Working display");
            Check(lateNotification.LastActivity == now.AddSeconds(-2) && !lateNotification.ActualTaskStopped && !lateNotification.TaskStoppedConfirmed,
                "late subagent completion does not refresh evidence or imply current task stopped");
            Write("session-a", Meta("session-a"), Start(), lateSubagent, Complete(seconds: 0));
            var terminalAfterLate = await new BoundCodexStatusObserver(verifiedSave.Binding, home, () => now).ReadAsync();
            Check(terminalAfterLate.CanonicalState == CodexTaskState.Completed && terminalAfterLate.ActualTaskStopped && !terminalAfterLate.TaskStoppedConfirmed,
                "a selected real terminal with unresolved previous-turn activity still cannot authorize dangerous closure");
            Check(!(terminalAfterLate with { TaskStoppedConfirmed = true, CoverageComplete = true }).TaskStoppedConfirmed,
                "unresolved other-turn activity hard-blocks stop authorization even through object initialization");
            var lateStale = await new BoundCodexStatusObserver(verifiedSave.Binding, home, () => now.AddMinutes(3)).ReadAsync();
            Check(lateStale.CanonicalState == CodexTaskState.Unknown, "stale typed current lifecycle is not revived by an unrelated subagent notification");
            Write("session-a", Meta("session-a"), Start(), Record("event_msg", new
            {
                type = "item_completed", thread_id = "wrong-session", turn_id = "previous-turn", item = new { type = "SubAgentActivity" }
            }, -1));
            var wrongThread = await new BoundCodexStatusObserver(verifiedSave.Binding, home, () => now).ReadAsync();
            Check(wrongThread.CanonicalState == CodexTaskState.Unknown, "foreign thread notifications do not qualify for the precise display isolation rule");
            Write("session-a", Meta("session-a"), Start(), Record("event_msg", new
            {
                type = "item_completed", thread_id = "session-a", turn_id = "previous-turn", item = new { type = "CommandExecution" }
            }, -1));
            var foreignTool = await new BoundCodexStatusObserver(verifiedSave.Binding, home, () => now).ReadAsync();
            Check(foreignTool.CanonicalState == CodexTaskState.Unknown, "different-turn command execution is not ignored as a subagent completion");
            Write("session-a", Meta("session-a"), Start(), Complete());
            Write("session-new", Meta("session-new"), Start("turn-new"), Complete("turn-new"));
            async Task<bool> RejectBindingRace(Func<CodexSessionBinding, Task<CodexStatusObservation>> duringRead)
            {
                var expected = store.Save(project, "session-a").Binding;
                try { _ = await CodexBoundObservationGuard.ReadAsync(store, expected, _ => duringRead(expected)); return false; }
                catch (InvalidDataException) { return true; }
            }
            CodexStatusObservation BoundFixture(CodexSessionBinding binding) => new(CodexObservedState.Running, now,
                StatusEvidence.LocalSessionSource, binding.SessionId, "turn-a", false, "只读竞态fixture", true)
            { BindingId = binding.BindingId, ProjectRoot = binding.ProjectRoot, SessionFilePath = binding.SessionFilePath, EvidenceAt = now };
            Check(await RejectBindingRace(async expected =>
            {
                _ = store.Save(project, "session-new"); await Task.Yield(); return BoundFixture(expected);
            }), "guard rejects stale session A result when user saves session B during read IO");
            Check(await RejectBindingRace(async expected =>
            {
                _ = store.Clear(); await Task.Yield(); return BoundFixture(expected);
            }), "guard rejects stale state after binding is cleared during read IO");
            Check(await RejectBindingRace(async expected =>
            {
                AtomicStore.Write(store.BindingPath, expected with { SessionId = "session-new", SessionFilePath = Path.Combine(Path.GetDirectoryName(path)!, "rollout-session-new.jsonl") });
                await Task.Yield(); return BoundFixture(expected);
            }), "guard compares the entire binding record and rejects same-Guid metadata/path tampering");
            Check(await RejectBindingRace(expected => Task.FromResult(BoundFixture(expected) with { SessionFilePath = outsidePathForGuard() })),
                "guard rejects an observation with mismatched native session file even when binding id agrees");
            string outsidePathForGuard() => Path.Combine(Path.GetDirectoryName(path)!, "rollout-session-new.jsonl");
            _ = store.Save(project, "session-a");
            Write("session-a", Meta("changed-session-id"), Start(), Complete());
            Check(!store.Load().Success, "primary binding is rejected when selected file metadata id changes");
            var changed = await new BoundCodexStatusObserver(verifiedSave.Binding, home, () => now).ReadAsync();
            Check(changed.CanonicalState == CodexTaskState.Unknown && changed.SessionId == "session-a", "changed file does not silently switch selected session id");
            Write("session-a", Meta("session-a"), Start(), Complete());
            var outside = Path.Combine(root, "outside.jsonl");
            File.WriteAllText(outside, Meta("session-a") + "\n" + Start() + "\n" + Complete() + "\n");
            var tampered = verifiedSave.Binding with { SessionFilePath = outside };
            AtomicStore.Write(store.BindingPath, tampered);
            Check(!store.Load().Success, "matching metadata outside native sessions cannot become a trusted source");
            var outsideObservation = await new BoundCodexStatusObserver(tampered, home, () => now).ReadAsync();
            Check(outsideObservation.CanonicalState == CodexTaskState.Unknown, "bound observer also rejects external source path");
            File.WriteAllText(store.BindingPath, "{invalid");
            Check(!store.Load().Success, "corrupt primary never revives valid .bak selection");
            File.Delete(store.BindingPath);
            Check(File.Exists(store.BindingPath + ".bak") && !store.Load().Success, "missing primary with historical backup remains unbound");
            var cleared = store.Clear();
            Check(cleared.Success && !cleared.Binding.IsBound && cleared.Binding.BindingId != verifiedSave.Binding.BindingId && File.Exists(store.BindingPath),
                "clear writes a new safe empty state rather than deleting binding record");
            Check(store.Load().Success && !store.Load().Binding.IsBound, "clear remains empty across reload despite old backup");
            var emptyObservation = await new BoundCodexStatusObserver(cleared.Binding, home, () => now).ReadAsync();
            Check(emptyObservation.CanonicalState == CodexTaskState.Unknown && emptyObservation.BindingId == cleared.Binding.BindingId,
                "cleared binding never auto-selects an available newest session");
            Write("duplicate", Meta("session-a"), Start(), Complete());
            var duplicates = await new CodexSessionCatalog(home, () => now).ReadAsync();
            Check(!duplicates.CoverageComplete && duplicates.Sessions.All(entry => entry.SessionId != "session-a") && !store.Save(project, "session-a").Success,
                "duplicate native metadata id is excluded instead of guessing a directory");
            var cancelled = false;
            try { await new CodexSessionCatalog(home, () => now).ReadAsync(new CancellationToken(true)); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "metadata catalog respects cancellation");
            var metadataCapPath = Write("huge-metadata", Record("session_meta", new { id = "huge-metadata", cwd = project, base_instructions = new string('z', CodexSessionCatalog.MaximumMetadataBytes) }), Start());
            var metadataCap = await new CodexSessionCatalog(home, () => now).ReadAsync();
            Check(!metadataCap.CoverageComplete && metadataCap.Sessions.All(entry => entry.SessionFilePath != metadataCapPath),
                "metadata read cap is not extended into the skill/conversation body");
            for (var index = 0; index <= CodexSessionCatalog.MaximumCandidates; index++) Write("cap-" + index, Meta("cap-" + index));
            var candidatesCap = await new CodexSessionCatalog(home, () => now).ReadAsync();
            Check(!candidatesCap.CoverageComplete && candidatesCap.Sessions.Length <= CodexSessionCatalog.MaximumCandidates,
                "catalog stops at candidate bound without claiming full discovery coverage");
            return passed;
        }
        finally
        {
            var full = Path.GetFullPath(root);
            if (!full.StartsWith(Path.GetFullPath(baseDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Binding fixture cleanup escaped its workspace.");
            if (Directory.Exists(full)) Directory.Delete(full, true);
        }
    }
}

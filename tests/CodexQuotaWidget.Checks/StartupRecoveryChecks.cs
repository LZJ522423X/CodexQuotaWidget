using System.Text.Json;
using CodexQuotaWidget.Core;
using CodexQuotaWidget.Core.Automation;
using CodexQuotaWidget.Core.ControlCenter;

public static class StartupRecoveryChecks
{
    public static int Run()
    {
        var root = Path.Combine(@"D:\CodexQuotaWidget-GitHub\artifacts\startup-recovery-checks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var passed = 0; var now = DateTimeOffset.UtcNow;
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException("StartupRecovery: " + message); passed++; }
        string Case(string name) { var path = Path.Combine(root, name); Directory.CreateDirectory(path); return path; }
        var empty = new ControlCenterSnapshot();
        try
        {
            var freshRoot = Case("fresh"); var first = new StartupRecoveryStore(freshRoot, () => now);
            var fresh = first.BeginSession(empty);
            Check(!fresh.NeedsAttention && fresh.SessionTracked, "fresh startup is tracked without an invented Codex state");
            var marker = File.ReadAllBytes(first.SessionPath);
            Check(first.BeginSession(empty).SessionTracked && marker.SequenceEqual(File.ReadAllBytes(first.SessionPath)), "begin is idempotent within the widget instance");
            Check(first.MarkCleanExit(), "orderly exit writes clean marker");
            var second = new StartupRecoveryStore(freshRoot, () => now.AddMinutes(1));
            Check(!second.BeginSession(empty).PreviousWidgetExitUnclean, "previous normal widget exit is not reported as an abnormal exit");

            var crashRoot = Case("unclean"); var crashed = new StartupRecoveryStore(crashRoot, () => now);
            crashed.BeginSession(empty);
            var restarted = new StartupRecoveryStore(crashRoot, () => now.AddMinutes(1));
            var recovery = restarted.BeginSession(empty);
            Check(recovery.PreviousWidgetExitUnclean && recovery.NeedsAttention, "missing normal-exit marker requests recovery confirmation");
            Check(recovery.Reasons.Any(reason => reason.Contains("无法据此判断 Codex")), "widget lifecycle does not claim Codex crashed");
            Check(restarted.Acknowledge() && restarted.GetState().Acknowledged, "explicit acknowledgement is persisted");
            Check(restarted.GetState().PreviousWidgetExitUnclean, "acknowledgement preserves abnormal-exit evidence");
            Check(restarted.MarkCleanExit(), "acknowledged recovered session may close normally");
            Check(!new StartupRecoveryStore(crashRoot, () => now.AddMinutes(2)).BeginSession(empty).PreviousWidgetExitUnclean,
                "acknowledged historical abnormal exit does not become a perpetual new alert");

            var carryRoot = Case("carry"); new StartupRecoveryStore(carryRoot, () => now).BeginSession(empty);
            var unacknowledged = new StartupRecoveryStore(carryRoot, () => now.AddMinutes(1)); unacknowledged.BeginSession(empty); unacknowledged.MarkCleanExit();
            Check(new StartupRecoveryStore(carryRoot, () => now.AddMinutes(2)).BeginSession(empty).PreviousWidgetExitUnclean,
                "an unacknowledged abnormal-exit issue survives a later normal exit");

            var taskRoot = Case("tasks"); var task = new ControlWorkItem { Id = Guid.NewGuid(), Title = "Local unfinished work", State = WorkItemState.Interrupted, UpdatedAt = now };
            var taskStore = new StartupRecoveryStore(taskRoot, () => now);
            var queuePath = Path.Combine(taskRoot, "control-center", "state.json");
            AtomicStore.Write(queuePath, new ControlCenterState { Tasks = [task] }); var queueBefore = File.ReadAllBytes(queuePath);
            var tasks = taskStore.BeginSession(empty with { Tasks = [task, task with { Id = Guid.NewGuid(), State = WorkItemState.Queued }] });
            Check(tasks.UnfinishedTaskCount == 2 && tasks.NeedsAttention, "queued and interrupted local tasks are displayed");
            Check(taskStore.Acknowledge() && queueBefore.SequenceEqual(File.ReadAllBytes(queuePath)), "continue recovery does not dispatch or change local task state");

            var executorRoot = Case("executor"); var eventId = Guid.NewGuid(); var receiptPath = Path.Combine(executorRoot, "automation-pending.json");
            var receipt = new ExhaustionReceipt { EventId = eventId, CycleKey = "codex:300:1800000000", CreatedAt = now };
            AtomicStore.Write(receiptPath, receipt); var receiptBefore = File.ReadAllBytes(receiptPath);
            var executor = new StartupRecoveryStore(executorRoot, () => now);
            Check(executor.BeginSession(empty).PendingExecutor, "formal pending evidence with no matching completed result is surfaced");
            Check(executor.Acknowledge() && receiptBefore.SequenceEqual(File.ReadAllBytes(receiptPath)), "acknowledgement never replays or clears pending receipt");
            var lastPath = Path.Combine(executorRoot, "automation", "last-run.json");
            var result = new ExecutorRunResult { RequestId = Guid.NewGuid(), StartedAt = now, FinishedAt = now.AddSeconds(1),
                Completed = true, Success = true, ExhaustionEventId = eventId, DryRun = false };
            AtomicStore.Write(lastPath, result);
            Check(!executor.GetState().PendingExecutor, "matching successful formal result resolves the pending display");
            AtomicStore.Write(lastPath, result with { DryRun = true });
            Check(executor.GetState().PendingExecutor, "a dry run cannot resolve a pending formal receipt");
            AtomicStore.Write(lastPath, result with { ExhaustionEventId = Guid.NewGuid() });
            Check(executor.GetState().PendingExecutor, "unrelated event history cannot resolve pending evidence");
            AtomicStore.Write(receiptPath, receipt with { Synthetic = true }); AtomicStore.Write(lastPath, result with { DryRun = true });
            Check(!executor.GetState().PendingExecutor, "completed dry fixture is not presented as a real unfinished shutdown");
            AtomicStore.Write(lastPath, result with { Completed = false, Success = false });
            Check(executor.GetState().PendingExecutor, "an incomplete formal last-run is surfaced independently");

            var corruptRoot = Case("corrupt-marker"); var prior = new StartupRecoveryStore(corruptRoot, () => now); prior.BeginSession(empty); prior.MarkCleanExit();
            File.WriteAllText(prior.SessionPath, "{broken"); var broken = File.ReadAllBytes(prior.SessionPath);
            var blocked = new StartupRecoveryStore(corruptRoot, () => now.AddMinutes(1)); var blockedState = blocked.BeginSession(empty);
            Check(blockedState.StorageBlocked && !blockedState.SessionTracked && blockedState.NeedsAttention, "damaged primary fails closed despite a valid backup");
            Check(!blocked.Acknowledge() && !blocked.MarkCleanExit(), "damaged marker cannot be acknowledged or overwritten by normal exit");
            Check(broken.SequenceEqual(File.ReadAllBytes(prior.SessionPath)), "damaged primary evidence is preserved byte-for-byte");

            var missingRoot = Case("missing-primary"); var missing = new StartupRecoveryStore(missingRoot, () => now); missing.BeginSession(empty); missing.MarkCleanExit();
            File.Delete(missing.SessionPath);
            Check(new StartupRecoveryStore(missingRoot, () => now).BeginSession(empty).StorageBlocked, "missing primary with surviving backup is not silently reset");

            foreach (var (name, relative, content) in new[]
            {
                ("queue", Path.Combine("control-center", "state.json"), "{broken"),
                ("journal", Path.Combine("resume", "journal.json"), "{broken"),
                ("pending", "automation-pending.json", "{broken"),
                ("last-run", Path.Combine("automation", "last-run.json"), "{broken"),
                ("null-marker", Path.Combine("resume", "widget-session.json"), "null")
            })
            {
                var data = Case("corrupt-" + name); var path = Path.Combine(data, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, content);
                var state = new StartupRecoveryStore(data, () => now).BeginSession(empty);
                Check(state.StorageBlocked && state.NeedsAttention && File.ReadAllText(path) == content, name + " corrupt primary is preserved and reported");
            }
            var oversizedRoot = Case("oversized"); var oversizedPath = Path.Combine(oversizedRoot, "automation-pending.json");
            File.WriteAllText(oversizedPath, new string(' ', 2 * 1024 * 1024 + 1));
            Check(new StartupRecoveryStore(oversizedRoot, () => now).BeginSession(empty).StorageBlocked, "oversized primary is rejected before deserialization");
            try { _ = new StartupRecoveryStore(@"C:\Users\FixtureUser\AppData\Local\Test"); throw new InvalidOperationException("C path accepted"); }
            catch (ArgumentException) { passed++; }
            var ownerRoot = Case("ownership"); var owner = new StartupRecoveryStore(ownerRoot, () => now); owner.BeginSession(empty);
            AtomicStore.Write(owner.SessionPath, new StartupRecoveryStore.WidgetSessionMarker { SessionId = Guid.NewGuid(), StartedAt = now });
            Check(!owner.MarkCleanExit(), "a different instance's marker cannot be marked clean by the old owner");
        }
        finally
        {
            var resolved = Path.GetFullPath(root);
            if (!resolved.StartsWith(@"D:\CodexQuotaWidget-GitHub\artifacts\startup-recovery-checks\", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Startup recovery fixture cleanup escaped its workspace");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
        return passed;
    }
}

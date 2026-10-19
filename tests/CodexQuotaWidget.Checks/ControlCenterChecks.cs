using CodexQuotaWidget.Core;
using CodexQuotaWidget.Core.ControlCenter;

public static class ControlCenterChecks
{
    public static async Task<int> RunAsync()
    {
        const string fixturesRoot = @"D:\CodexQuotaWidget-GitHub\artifacts\control-center-checks";
        var runRoot = Path.Combine(fixturesRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runRoot);
        var now = new DateTimeOffset(2026, 10, 3, 8, 0, 0, TimeSpan.Zero);
        var passed = 0;
        var fixtureIndex = 0;

        void Check(bool condition, string scenario)
        {
            if (!condition) throw new InvalidOperationException("Control center: " + scenario);
            passed++;
        }

        string State(ControlWorkItem task) => task.State.ToString();
        ControlWorkItem TaskById(ControlCenterService service, Guid id)
            => service.Snapshot.Tasks.Single(task => task.Id == id);

        ControlCenterService NewService(string? file = null)
            => new(file ?? Path.Combine(runRoot, $"queue-{++fixtureIndex}.json"), () => now);

        QuotaSnapshot Quota(double? five = 75, double? weekly = 60, DateTimeOffset? observed = null)
            => new(observed ?? now, "plus",
                [new("codex", "Codex", "primary", five is { } f ? 100 - f : null, 300, now.AddHours(4).ToUnixTimeSeconds()),
                 new("codex", "Codex", "secondary", weekly is { } w ? 100 - w : null, 10080, now.AddDays(4).ToUnixTimeSeconds())],
                true, 3, [now.AddDays(3).ToUnixTimeSeconds()]);

        void Fresh(ControlCenterService service, double five = 75, double weekly = 60)
            => service.UpdateQuota(Quota(five, weekly), MonitorStatus.Live, false);

        Guid Add(ControlCenterService service, string title, int priority = 0)
        {
            var result = service.AddTask(title, "Fixture notes", priority);
            Check(result.Success && result.TaskId is not null, "valid task can be persisted: " + title);
            return result.TaskId!.Value;
        }

        try
        {
            var guard = NewService();
            var guardedTask = Add(guard, "guarded");
            Check(guard.Snapshot.Guard.State.ToString() == "Unknown", "missing quota is unknown");
            Check(!guard.StartNext().Success && State(TaskById(guard, guardedTask)) == "Queued", "unknown quota never starts a queued task");

            Fresh(guard, 80, 4);
            Check(guard.Snapshot.Guard.State.ToString() == "Protected" && !guard.Snapshot.Guard.CanStart, "weekly quota can protect despite a healthy 5-hour quota");
            Fresh(guard, 4, 80);
            Check(guard.Snapshot.Guard.State.ToString() == "Protected", "5-hour quota can protect despite a healthy weekly quota");
            Fresh(guard, 80, 20);
            Check(guard.Snapshot.Guard.State.ToString() == "Warning" && guard.Snapshot.Guard.Warning && guard.Snapshot.Guard.CanStart, "warning boundary allows local tracking with an alert");
            Fresh(guard, 5, 80);
            Check(guard.Snapshot.Guard.State.ToString() == "Protected" && !guard.StartNext().Success, "protection boundary blocks a start");
            Fresh(guard, 80, 0);
            Check(guard.Snapshot.Guard.State.ToString() == "Exhausted" && !guard.StartNext().Success, "weekly exhaustion blocks a start");
            Fresh(guard, 0, 80);
            Check(guard.Snapshot.Guard.State.ToString() == "Exhausted", "5-hour exhaustion blocks a start");
            Fresh(guard);
            Check(guard.Snapshot.Guard.State.ToString() == "Ready" && guard.Snapshot.Guard.CanStart, "fresh healthy dual quota permits local tracking");

            foreach (var status in new[] { MonitorStatus.DesktopClosed, MonitorStatus.Offline, MonitorStatus.NeedsLogin, MonitorStatus.Sleeping, MonitorStatus.ProtocolError })
            {
                guard.UpdateQuota(Quota(), status, false);
                Check(!guard.Snapshot.Guard.CanStart && !guard.StartNext().Success, "non-live monitor cannot start: " + status);
            }
            guard.UpdateQuota(Quota(), MonitorStatus.Live, true);
            Check(!guard.Snapshot.Guard.CanStart, "explicitly stale snapshot blocks a start");
            guard.UpdateQuota(Quota(observed: now.AddSeconds(-181)), MonitorStatus.Live, false);
            Check(!guard.Snapshot.Guard.CanStart, "aged quota blocks a start even when caller did not mark it stale");
            guard.UpdateQuota(Quota(null, 70), MonitorStatus.Live, false);
            Check(!guard.Snapshot.Guard.CanStart, "missing 5-hour percentage does not become available capacity");
            guard.UpdateQuota(Quota(70, null), MonitorStatus.Live, false);
            Check(!guard.Snapshot.Guard.CanStart, "missing weekly percentage does not become available capacity");
            guard.UpdateQuota(Quota(double.NaN, 70), MonitorStatus.Live, false);
            Check(!guard.Snapshot.Guard.CanStart, "non-finite quota blocks a start");
            guard.UpdateQuota(Quota(observed: now.AddMinutes(10)), MonitorStatus.Live, false);
            Check(!guard.Snapshot.Guard.CanStart, "future-dated quota is not trusted for dispatch");
            var unrelatedBuckets = Quota() with
            {
                Windows = [new("other-model", "Other model", "primary", 10, 300, null), new("other-model", "Other model", "secondary", 10, 10080, null)]
            };
            guard.UpdateQuota(unrelatedBuckets, MonitorStatus.Live, false);
            Check(!guard.Snapshot.Guard.CanStart, "other model buckets cannot impersonate Codex capacity");
            var additionalBucket = Quota();
            additionalBucket.Windows.Add(new("other-model", "Other model", "tertiary", 100, 300, null));
            guard.UpdateQuota(additionalBucket, MonitorStatus.Live, false);
            Check(guard.Snapshot.Guard.CanStart, "unrelated exhausted bucket does not block healthy Codex quota");
            var duplicateCoreWindow = Quota();
            duplicateCoreWindow.Windows.Add(new("codex", "Codex", "extra", 98, 300, now.AddHours(3).ToUnixTimeSeconds()));
            guard.UpdateQuota(duplicateCoreWindow, MonitorStatus.Live, false);
            Check(!guard.Snapshot.Guard.CanStart, "conflicting matching windows use the more conservative capacity");

            Fresh(guard);
            now = now.AddSeconds(181);
            Check(!guard.StartNext().Success && State(TaskById(guard, guardedTask)) == "Queued", "dispatch reevaluates age even without a new monitor event");

            var ageNotifications = NewService();
            var notificationSnapshot = Quota();
            var notificationCount = 0;
            ageNotifications.Changed += () => notificationCount++;
            ageNotifications.UpdateQuota(notificationSnapshot, MonitorStatus.Live, false);
            notificationCount = 0;
            now = now.AddSeconds(181);
            ageNotifications.UpdateQuota(notificationSnapshot, MonitorStatus.Live, false);
            Check(notificationCount == 1 && ageNotifications.Snapshot.Guard.State == QuotaGuardState.Unknown, "the same snapshot aging out emits a guard-change notification");
            ageNotifications.UpdateQuota(notificationSnapshot, MonitorStatus.Live, false);
            Check(notificationCount == 1, "repeated unchanged stale state does not flood guard-change notifications");

            var originalPolicy = guard.Snapshot.Policy;
            Check(!guard.UpdatePolicy(new() { WarningPercent = 3, ProtectionPercent = 10 }).Success, "inverted thresholds are rejected");
            Check(guard.Snapshot.Policy == originalPolicy, "invalid threshold edit retains the prior policy");
            Check(!guard.UpdatePolicy(new() { WarningPercent = double.NaN }).Success, "non-finite threshold is rejected");
            Check(!guard.UpdatePolicy(new() { MaximumQuotaAgeSeconds = 0 }).Success, "zero freshness limit is rejected");
            Check(guard.UpdatePolicy(new() { Enabled = false }).Success, "optional low-quota threshold protection can be disabled");
            Fresh(guard, 2, 60);
            Check(guard.Snapshot.Guard.CanStart, "disabled threshold policy permits low but nonzero capacity");
            Fresh(guard, 0, 60);
            Check(!guard.Snapshot.Guard.CanStart, "disabled threshold policy never bypasses exhaustion");
            guard.UpdateQuota(null, MonitorStatus.Live, false);
            Check(!guard.Snapshot.Guard.CanStart, "disabled threshold policy never bypasses unknown capacity");
            Check(guard.Snapshot.Tasks.Count == 1 && State(TaskById(guard, guardedTask)) == "Queued", "quota transitions have not executed or removed the queued task");

            var configurableAge = NewService();
            Check(configurableAge.UpdatePolicy(new() { MaximumQuotaAgeSeconds = 615 }).Success, "5-minute refresh can use its longer supported freshness allowance");
            Fresh(configurableAge);
            now = now.AddSeconds(300);
            Check(configurableAge.Snapshot.Guard.CanStart, "quota remains usable within the longer refresh allowance");
            Check(configurableAge.UpdatePolicy(new() { MaximumQuotaAgeSeconds = 135 }).Success && !configurableAge.Snapshot.Guard.CanStart, "shorter refresh allowance reevaluates already-aged quota immediately");

            var queue = NewService();
            Fresh(queue);
            Check(queue.Snapshot.RuntimeState.ToString() == "NotConnected", "no connector is represented as disconnected");
            Check(!queue.AddTask("   ").Success && queue.Snapshot.Tasks.Count == 0, "blank tasks are rejected without a queue mutation");
            var first = Add(queue, "first");
            now = now.AddSeconds(1);
            var second = Add(queue, "second");
            now = now.AddSeconds(1);
            var third = Add(queue, "third");
            Fresh(queue);
            Check(queue.StartNext().Success && State(TaskById(queue, first)) == "Running", "FIFO starts the oldest same-priority task");
            Check(!queue.StartTask(second).Success && queue.Snapshot.Tasks.Count(task => State(task) == "Running") == 1, "one running task is enforced");
            Check(!queue.ResumeTask(second).Success, "queued task cannot be resumed");
            Check(!queue.CompleteTask(second).Success, "queued task cannot be completed");
            Check(!queue.RetryTask(second).Success, "queued task cannot be retried");
            Check(queue.PauseTask(first).Success && State(TaskById(queue, first)) == "Paused", "running task can pause");
            Check(!queue.PauseTask(first).Success, "already paused task cannot pause again");
            Check(queue.ResumeTask(first).Success && State(TaskById(queue, first)) == "Running", "paused task can resume with fresh quota");
            Check(queue.CompleteTask(first).Success && State(TaskById(queue, first)) == "Completed", "running task can complete");
            Check(!queue.FailTask(first, "late failure").Success && !queue.PauseTask(first).Success && !queue.RetryTask(first).Success, "completed tasks reject failure, pause, and retry");
            Check(queue.StartNext().Success && State(TaskById(queue, second)) == "Running", "completion makes the next FIFO task eligible");
            Check(queue.FailTask(second, "fixture failure").Success && State(TaskById(queue, second)) == "Failed", "running task records a failed outcome");
            Check(TaskById(queue, second).FailureReason == "fixture failure", "failure reason is retained for recovery");
            Check(queue.RetryTask(second).Success && State(TaskById(queue, second)) == "Queued", "failed task can explicitly reenter the queue");
            Check(queue.CancelTask(third).Success && State(TaskById(queue, third)) == "Cancelled", "queued task can be canceled");
            Check(!queue.CancelTask(third).Success, "terminal canceled task rejects a repeated cancel");
            Check(!queue.StartTask(Guid.NewGuid()).Success, "unknown task identity never starts another task");
            Check(queue.SetDispatchPaused(true).Success && !queue.StartNext().Success, "manual queue pause blocks dispatch");
            Check(queue.SetDispatchPaused(false).Success, "manual queue pause can be released");

            var priority = NewService();
            Fresh(priority);
            var ordinary = Add(priority, "normal priority");
            now = now.AddSeconds(1);
            var urgent = Add(priority, "higher priority", 10);
            Fresh(priority);
            Check(priority.StartNext().Success && State(TaskById(priority, urgent)) == "Running" && State(TaskById(priority, ordinary)) == "Queued", "priority precedes FIFO only when priorities differ");

            var editPath = Path.Combine(runRoot, "editable-tasks.json");
            var editable = NewService(editPath);
            Fresh(editable);
            var earlierEditable = Add(editable, "older ordinary task");
            now = now.AddSeconds(1);
            var laterEditable = Add(editable, "newer task before editing");
            var beforeEdit = TaskById(editable, laterEditable);
            now = now.AddSeconds(1);
            Check(editable.UpdateTask(laterEditable, "  updated important task  ", "updated goal", 20).Success, "queued task accepts a valid title, notes and priority edit");
            var editedTask = TaskById(editable, laterEditable);
            Check(editedTask.Id == beforeEdit.Id && editedTask.Sequence == beforeEdit.Sequence && editedTask.CreatedAt == beforeEdit.CreatedAt && editedTask.State == beforeEdit.State && editedTask.UpdatedAt > beforeEdit.UpdatedAt, "editing preserves identity, FIFO sequence, creation time and state while updating its edit time");
            Check(editedTask.Title == "updated important task" && editedTask.Notes == "updated goal" && editedTask.Priority == 20, "valid queued edit records normalized text and the requested priority");
            var beforeInvalidEditBytes = File.ReadAllBytes(editPath);
            Check(!editable.UpdateTask(laterEditable, " ", "bad edit", 20).Success && !editable.UpdateTask(laterEditable, "bad priority", "bad edit", 101).Success && !editable.UpdateTask(laterEditable, "bad notes", new string('x', 2001), 20).Success, "invalid edits reject empty titles, unsupported priorities and oversized notes");
            Check(TaskById(editable, laterEditable) == editedTask && File.ReadAllBytes(editPath).SequenceEqual(beforeInvalidEditBytes), "rejected edits leave both the original record and persisted bytes unchanged");
            Check(!editable.UpdateTask(Guid.NewGuid(), "unknown task", null, 0).Success, "editing an unknown identity cannot alter another task");
            Fresh(editable);
            Check(editable.StartNext().Success && State(TaskById(editable, laterEditable)) == "Running" && State(TaskById(editable, earlierEditable)) == "Queued", "edited queued priority is honored by the next dispatch selection");
            var beforeRunningEdit = TaskById(editable, laterEditable);
            Check(!editable.UpdateTask(laterEditable, "change a running goal", "unexpected goal", 30).Success && TaskById(editable, laterEditable) == beforeRunningEdit, "running task cannot be edited underneath an active task record");
            Check(editable.PauseTask(laterEditable).Success, "editable fixture can deliberately pause its running record");
            var beforePausedEdit = TaskById(editable, laterEditable);
            now = now.AddSeconds(1);
            Check(editable.UpdateTask(laterEditable, "revised paused goal", "paused revision", 40).Success, "paused task can be edited explicitly");
            Check(TaskById(editable, laterEditable).State == WorkItemState.Paused && TaskById(editable, laterEditable).StartedAt == beforePausedEdit.StartedAt && editable.Snapshot.Tasks.All(task => task.State != WorkItemState.Running), "editing a paused task does not resume it or erase its prior start time");
            Check(editable.CompleteTask(laterEditable).Success, "edited paused fixture can be completed");
            var beforeTerminalEdit = TaskById(editable, laterEditable);
            Check(!editable.UpdateTask(laterEditable, "change completed goal", null, 0).Success && TaskById(editable, laterEditable) == beforeTerminalEdit, "terminal completed record rejects editing without changing its outcome");

            var concurrentPath = Path.Combine(runRoot, "concurrent-starts.json");
            var concurrent = NewService(concurrentPath);
            Fresh(concurrent);
            var concurrentIds = Enumerable.Range(0, 6).Select(index => Add(concurrent, "concurrent task " + index)).ToArray();
            var starts = await Task.WhenAll(concurrentIds.Select(id => Task.Run(() => concurrent.StartTask(id))));
            Check(starts.Count(result => result.Success) == 1 && concurrent.Snapshot.Tasks.Count(task => task.State == WorkItemState.Running) == 1, "simultaneous start requests commit exactly one running task");
            var concurrentReload = NewService(concurrentPath);
            Check(concurrentReload.Snapshot.Tasks.Count(task => task.State == WorkItemState.Interrupted) == 1 && concurrentReload.Snapshot.Tasks.Count == 6, "concurrent starts persist a complete queue with one recoverable in-progress task");

            var subscriberPath = Path.Combine(runRoot, "faulty-subscriber.json");
            var subscriber = NewService(subscriberPath);
            subscriber.Changed += () => throw new InvalidOperationException("injected UI subscriber failure");
            var published = subscriber.AddTask("committed despite faulty UI observer");
            Check(published.Success && subscriber.Snapshot.Tasks.Count == 1, "a faulty event subscriber does not turn a committed mutation into failure");
            Check(NewService(subscriberPath).Snapshot.Tasks.Count == 1, "a faulty observer cannot corrupt the persisted queue");

            var stop = NewService();
            Fresh(stop);
            var running = Add(stop, "running when quota drops");
            var waiting = Add(stop, "waiting when quota drops");
            Check(stop.StartTask(running).Success, "safe-stop fixture starts locally");
            var creditSnapshot = Quota(0, 80);
            stop.UpdateQuota(creditSnapshot, MonitorStatus.Live, false);
            Check(stop.Snapshot.SafeStopRequested && State(TaskById(stop, running)) == "Running", "exhaustion requests safe stop while retaining accurate running state");
            Check(!stop.StartTask(waiting).Success, "safe stop never starts the waiting task");
            Check(creditSnapshot.AvailableCount == 3 && creditSnapshot.CreditExpirations.Count == 1, "guard never spends or mutates reset credits");
            Fresh(stop);
            Check(stop.Snapshot.SafeStopRequested, "fresh quota does not silently clear a stop request");
            Check(!stop.StartNext().Success, "fresh quota alone never dispatches a waiting task");
            Check(stop.PauseTask(running).Success, "safe-stop running task can reach a local pause");
            Check(!stop.ResumeTask(running).Success, "a pause does not silently acknowledge a latched safe stop");
            Check(stop.AcknowledgeSafeStop().Success && !stop.Snapshot.SafeStopRequested, "explicit acknowledgment releases the stop latch");
            Check(stop.ResumeTask(running).Success, "explicit resume is required after quota recovers");

            var agingRun = NewService();
            Fresh(agingRun);
            var agingTask = Add(agingRun, "running as quota ages without an event");
            Check(agingRun.StartTask(agingTask).Success, "quota-age fixture starts local tracking");
            now = now.AddSeconds(181);
            Check(agingRun.Snapshot.SafeStopRequested && !agingRun.Snapshot.Guard.CanStart, "read-only UI observation exposes a stale-running safe stop");
            Fresh(agingRun);
            Check(agingRun.Snapshot.SafeStopRequested, "a fresh event cannot silently undo the stale-running stop");
            Check(agingRun.PauseTask(agingTask).Success && !agingRun.ResumeTask(agingTask).Success, "stale-running recovery requires acknowledgment before resume");
            Check(agingRun.AcknowledgeSafeStop().Success && agingRun.ResumeTask(agingTask).Success, "explicit acknowledgment and resume can recover from aged quota");

            var widerFreshness = NewService();
            Fresh(widerFreshness);
            var widerFreshnessTask = Add(widerFreshness, "running before freshness policy expands");
            Check(widerFreshness.StartTask(widerFreshnessTask).Success, "policy-expansion fixture starts local tracking");
            now = now.AddSeconds(181);
            Check(widerFreshness.Snapshot.SafeStopRequested && !widerFreshness.Snapshot.Guard.CanStart, "running task becomes unsafe under its original freshness policy");
            Check(widerFreshness.UpdatePolicy(new() { MaximumQuotaAgeSeconds = 615 }).Success && widerFreshness.Snapshot.Guard.CanStart && widerFreshness.Snapshot.SafeStopRequested, "expanding the freshness allowance does not silently release an existing stop");
            Check(widerFreshness.PauseTask(widerFreshnessTask).Success && !widerFreshness.ResumeTask(widerFreshnessTask).Success, "a policy-expanded queue still needs explicit acknowledgment before resume");
            Check(widerFreshness.AcknowledgeSafeStop().Success && widerFreshness.ResumeTask(widerFreshnessTask).Success, "an explicit acknowledgment can release the policy-change stop safely");

            var protectedWritePath = Path.Combine(runRoot, "guard-write-failure.json");
            var protectionDuringStorageFailure = NewService(protectedWritePath);
            Fresh(protectionDuringStorageFailure);
            var storageProtectedTask = Add(protectionDuringStorageFailure, "running during storage failure");
            Check(protectionDuringStorageFailure.StartTask(storageProtectedTask).Success, "guard-storage fixture starts local tracking");
            var preGuardBytes = File.ReadAllBytes(protectedWritePath);
            using (var readLock = new FileStream(protectedWritePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Fresh(protectionDuringStorageFailure, 0, 70);
                Check(protectionDuringStorageFailure.Snapshot.SafeStopRequested && !protectionDuringStorageFailure.StartNext().Success, "disk failure cannot suppress a quota protection stop");
                Check(protectionDuringStorageFailure.Snapshot.StorageError is not null && File.ReadAllBytes(protectedWritePath).SequenceEqual(preGuardBytes), "protection storage failure reports the error and retains the previous file");
            }
            Fresh(protectionDuringStorageFailure);
            Check(protectionDuringStorageFailure.Snapshot.SafeStopRequested, "storage protection stop remains latched after fresh quota");

            var persistedPath = Path.Combine(runRoot, "restart.json");
            var beforeRestart = NewService(persistedPath);
            Fresh(beforeRestart);
            var interrupted = Add(beforeRestart, "recover an in-progress task");
            var persistedWaiting = Add(beforeRestart, "preserve a waiting task");
            Check(beforeRestart.StartTask(interrupted).Success, "restart fixture contains a running task");
            var afterRestart = NewService(persistedPath);
            Check(State(TaskById(afterRestart, interrupted)) == "Interrupted", "running tasks reopen as interrupted, never running");
            Check(State(TaskById(afterRestart, persistedWaiting)) == "Queued", "restart retains unstarted tasks");
            Check(!afterRestart.Snapshot.Guard.CanStart && !afterRestart.StartNext().Success, "restart never treats prior quota as live or starts work");
            Check(afterRestart.RetryTask(interrupted).Success, "interrupted task needs an explicit retry");
            Fresh(afterRestart);
            Check(afterRestart.Snapshot.Tasks.All(task => State(task) != "Running"), "new healthy quota does not automatically run a recovered queue");

            var lockedPath = Path.Combine(runRoot, "locked.json");
            var reliable = NewService(lockedPath);
            Fresh(reliable);
            var durable = Add(reliable, "durable existing task");
            var originalBytes = File.ReadAllBytes(lockedPath);
            using (var readLock = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var failedWrite = reliable.AddTask("must not exist after failed storage");
                Check(!failedWrite.Success, "injected atomic-replace failure is reported");
                Check(reliable.Snapshot.Tasks.Count == 1 && TaskById(reliable, durable).Title == "durable existing task" && reliable.Snapshot.StorageError is not null, "failed persistence preserves the in-memory queue and exposes a storage error");
                Check(File.ReadAllBytes(lockedPath).SequenceEqual(originalBytes), "failed persistence leaves the original queue file intact");
                var originalStoredPolicy = reliable.Snapshot.Policy;
                Check(!reliable.UpdatePolicy(new() { MaximumQuotaAgeSeconds = 615 }).Success && reliable.Snapshot.Policy == originalStoredPolicy, "a policy storage failure retains the previous active policy");
                Check(File.ReadAllBytes(lockedPath).SequenceEqual(originalBytes), "a failed policy edit leaves original queue bytes intact");
            }
            var durableReload = NewService(lockedPath);
            Check(durableReload.Snapshot.Tasks.Count == 1 && TaskById(durableReload, durable).Title == "durable existing task", "old queue remains readable after failed persistence");
            Check(reliable.AddTask("storage recovered").Success, "persistence can recover after the file lock is released");
            var beforeLockedEdit = TaskById(reliable, durable);
            var beforeLockedEditBytes = File.ReadAllBytes(lockedPath);
            using (var readLock = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Check(!reliable.UpdateTask(durable, "must not replace stored task", "failed edit", 90).Success, "locked storage reports an edit failure");
                Check(TaskById(reliable, durable) == beforeLockedEdit && File.ReadAllBytes(lockedPath).SequenceEqual(beforeLockedEditBytes), "failed task editing preserves the full old record and original queue bytes");
            }
            Check(reliable.UpdateTask(durable, "edited after storage recovery", "recovered edit", 10).Success && TaskById(reliable, durable).Title == "edited after storage recovery", "task editing recovers after storage is available again");

            var disconnected = new DisconnectedWorkflowConnector();
            Check(!disconnected.CanExecute && disconnected.ConnectionState == WorkflowConnectionState.NotConnected, "default connector exposes no execution capability");
            var rejectedExecution = await disconnected.ExecuteAsync(new(Guid.NewGuid(), WorkflowProvider.Codex, "fixture only"));
            Check(!rejectedExecution.Success, "default connector rejects execution without sending a model request");
            var rejectedStop = await disconnected.RequestSafeStopAsync(Guid.NewGuid());
            Check(!rejectedStop.Accepted, "default connector never claims it stopped an unconnected remote task");
            var power = new DisabledPowerAction();
            Check(!power.CanExecute, "power actions are disabled by default");
            foreach (var action in Enum.GetValues<PowerActionKind>())
                Check(!(await power.RequestAsync(new(action, "fixture only"))).Success, "reserved power action performs no system operation: " + action);

            var spy = new CountingConnector();
            var localOnly = new ControlCenterService(Path.Combine(runRoot, "no-model-calls.json"), () => now, spy);
            Fresh(localOnly);
            var locallyTracked = Add(localOnly, "no implicit execution");
            Check(localOnly.StartTask(locallyTracked).Success, "configured connector still uses local-only task tracking");
            Fresh(localOnly, 0, 70);
            Check(spy.ExecuteCalls == 0 && spy.StopCalls == 0, "local tracking and quota protection never invoke the reserved connector");

            var exitStatePath = Path.Combine(runRoot, "safe-exit-restart.json");
            var exiting = NewService(exitStatePath);
            Fresh(exiting);
            var exitTask = Add(exiting, "safe exit while running");
            Check(exiting.StartTask(exitTask).Success, "safe-exit fixture starts local tracking");
            Check(exiting.RequestSafeExit().Success && exiting.Snapshot.ExitRequested && exiting.Snapshot.DispatchPaused && !exiting.Snapshot.CanExitSafely, "safe exit saves intent and blocks dispatch while a task is running");
            Check(!exiting.AcknowledgeSafeStop().Success && !exiting.StartNext().Success, "pending safe exit cannot be bypassed by acknowledging quota");
            Check(exiting.PauseTask(exitTask).Success && exiting.Snapshot.CanExitSafely, "pausing the tracked task makes local exit safe");
            Check(exiting.CancelSafeExit().Success && !exiting.Snapshot.ExitRequested && exiting.Snapshot.DispatchPaused, "canceling exit does not automatically resume the queue");
            Check(State(TaskById(exiting, exitTask)) == "Paused", "safe exit never kills or completes the task implicitly");
            Check(exiting.RequestSafeExit().Success, "a safely paused queue can request exit again");
            var restartedAfterExit = NewService(exitStatePath);
            Check(!restartedAfterExit.Snapshot.ExitRequested && restartedAfterExit.Snapshot.DispatchPaused, "exit intent expires with the previous process while the queue stays paused");
            Fresh(restartedAfterExit);
            Check(restartedAfterExit.SetDispatchPaused(false).Success && restartedAfterExit.AcknowledgeSafeStop().Success && restartedAfterExit.ResumeTask(exitTask).Success, "reopening after safe exit supports an explicit human-directed resume");

            var idleExitPath = Path.Combine(runRoot, "idle-safe-exit-restart.json");
            var idleExit = NewService(idleExitPath);
            Fresh(idleExit);
            var idleWaiting = Add(idleExit, "unstarted task survives normal exit");
            Check(idleExit.RequestSafeExit().Success && idleExit.Snapshot.CanExitSafely && idleExit.Snapshot.ExitRequested, "a queue with no running task saves a normal safe-exit request");
            var restartedIdle = NewService(idleExitPath);
            Check(!restartedIdle.Snapshot.ExitRequested && restartedIdle.Snapshot.DispatchPaused && !restartedIdle.Snapshot.SafeStopRequested && State(TaskById(restartedIdle, idleWaiting)) == "Queued", "normal safe-exit restart keeps tasks paused without inventing a running-task stop");
            Fresh(restartedIdle);
            Check(restartedIdle.SetDispatchPaused(false).Success && restartedIdle.StartNext().Success, "normal safe-exit restart can manually begin a preserved queued task");

            var corruptPath = Path.Combine(runRoot, "corrupt.json");
            const string corruptContent = "{unreadable queue";
            File.WriteAllText(corruptPath, corruptContent);
            File.WriteAllText(corruptPath + ".bak", corruptContent);
            var corrupt = NewService(corruptPath);
            Check(corrupt.Snapshot.StorageError is not null && !corrupt.AddTask("must not overwrite corrupt state").Success, "unreadable primary and fallback storage block mutations");
            Check(File.ReadAllText(corruptPath) == corruptContent && File.ReadAllText(corruptPath + ".bak") == corruptContent, "unreadable storage is retained for diagnosis instead of silently reset");

            var corruptWithRunningBackupPath = Path.Combine(runRoot, "corrupt-with-running-backup.json");
            var beforeCorruption = NewService(corruptWithRunningBackupPath); Fresh(beforeCorruption);
            var backupRunningTask = Add(beforeCorruption, "running task in historical backup");
            Check(beforeCorruption.StartTask(backupRunningTask).Success, "valid backup fixture has a persisted running task");
            var runningBytes = File.ReadAllBytes(corruptWithRunningBackupPath);
            File.WriteAllBytes(corruptWithRunningBackupPath + ".bak", runningBytes);
            File.WriteAllText(corruptWithRunningBackupPath, corruptContent);
            var corruptPrimaryBytes = File.ReadAllBytes(corruptWithRunningBackupPath);
            var primaryOnly = NewService(corruptWithRunningBackupPath);
            Check(primaryOnly.Snapshot.StorageError is not null && primaryOnly.Snapshot.Tasks.Count == 0,
                "valid running backup cannot revive tasks when the primary is corrupt");
            Fresh(primaryOnly);
            Check(!primaryOnly.AddTask("must not revive backup").Success && !primaryOnly.StartNext().Success,
                "fresh quota cannot re-enable mutations after a corrupt primary load");
            Check(corruptPrimaryBytes.SequenceEqual(File.ReadAllBytes(corruptWithRunningBackupPath))
                && runningBytes.SequenceEqual(File.ReadAllBytes(corruptWithRunningBackupPath + ".bak")),
                "constructor and rejected operations preserve corrupt primary and valid running backup byte-for-byte");

            var missingPrimaryPath = Path.Combine(runRoot, "missing-primary-with-running-backup.json");
            File.WriteAllBytes(missingPrimaryPath + ".bak", runningBytes);
            var missingPrimary = NewService(missingPrimaryPath);
            Check(missingPrimary.Snapshot.StorageError is not null && missingPrimary.Snapshot.Tasks.Count == 0 && !missingPrimary.AddTask("do not recreate missing primary").Success,
                "missing primary with a surviving running backup fails closed");
            Check(!File.Exists(missingPrimaryPath) && runningBytes.SequenceEqual(File.ReadAllBytes(missingPrimaryPath + ".bak")),
                "a missing primary is not recreated from historical backup evidence");

            var oversizedPrimaryPath = Path.Combine(runRoot, "oversized-primary.json");
            File.WriteAllText(oversizedPrimaryPath, new string(' ', 1024 * 1024 + 1));
            var oversizedPrimary = NewService(oversizedPrimaryPath);
            Check(oversizedPrimary.Snapshot.StorageError is not null && !oversizedPrimary.AddTask("cannot overwrite oversized primary").Success,
                "oversized task primary is rejected before JSON deserialization");

            return passed;
        }
        finally
        {
            var resolved = Path.GetFullPath(runRoot);
            var prefix = Path.GetFullPath(fixturesRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unexpected control-center fixture path.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
    }

    sealed class CountingConnector : IWorkflowConnector
    {
        public string Id => "fixture-connector";
        public WorkflowConnectionState ConnectionState => WorkflowConnectionState.Ready;
        public bool CanExecute => true;
        public int ExecuteCalls { get; private set; }
        public int StopCalls { get; private set; }
        public Task<WorkflowExecutionResult> ExecuteAsync(WorkflowExecutionRequest request, CancellationToken cancellationToken = default)
        {
            ExecuteCalls++;
            return Task.FromResult(new WorkflowExecutionResult(true, "fixture"));
        }
        public Task<SafeStopResult> RequestSafeStopAsync(Guid taskId, CancellationToken cancellationToken = default)
        {
            StopCalls++;
            return Task.FromResult(new SafeStopResult(true, "fixture"));
        }
    }
}

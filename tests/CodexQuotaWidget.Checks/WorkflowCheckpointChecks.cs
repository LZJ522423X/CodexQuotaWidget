using CodexQuotaWidget.Core.Automation;
using CodexQuotaWidget.Core.ControlCenter;

/// <summary>Fake AI channels and real isolated D-drive checkpoint I/O; never touches production workflow records.</summary>
public static class WorkflowCheckpointChecks
{
    const string FixtureParent = @"D:\CodexQuotaWidget-GitHub\artifacts\workflow-checkpoint-checks";
    const string Session = "c21ccfb5-50a3-44b0-bb27-b5af2af6b22f";
    public static int Run() => RunAsync().GetAwaiter().GetResult();

    static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool value, string description)
        { if (!value) throw new InvalidOperationException("Workflow checkpoint: " + description); count++; }
        var root = Path.GetFullPath(Path.Combine(FixtureParent, Guid.NewGuid().ToString("N")));
        if (!AutomationStore.IsSafeDPath(root)) throw new InvalidDataException("Checkpoint fixtures require safe D paths");
        var project = Path.Combine(root, "project"); Directory.CreateDirectory(project);
        var configuration = new WorkflowLoopConfiguration
        {
            TaskId = Guid.NewGuid(), AuthorizedWritePaths = ["Program.cs"], ProjectRoot = project, ChatGptThreadId = Guid.NewGuid().ToString(), CallerThreadId = Guid.NewGuid().ToString(),
            Goal = "仅验证隔离工作流检查点恢复，不执行真实模型或系统操作。", MaximumRounds = 3,
            LocalExecutionAuthorized = true
        };
        WorkflowLoopService Create(string name, Channel chat, Channel codex, Validation validation,
            Func<CancellationToken, Task<QuotaGuardSnapshot>>? quota = null)
            => new(Path.Combine(root, name), chat, codex, validation, quota ?? (_ => Task.FromResult(Ready())));
        static bool IsReview(WorkflowExecutionRequest request) => request.Goal.Contains("请评估目标是否完成", StringComparison.Ordinal);
        static Task<WorkflowExecutionResult> Reply(WorkflowExecutionRequest request, CancellationToken _)
            => Task.FromResult(new WorkflowExecutionResult(true, IsReview(request) ? "ACC_REVIEW: COMPLETE\n模拟目标完成。" : "模拟一个已确认建议。"));
        static Task<WorkflowExecutionResult> Execute(WorkflowExecutionRequest _, CancellationToken __)
            => Task.FromResult(new WorkflowExecutionResult(true, "模拟Codex执行报告。", Session));

        try
        {
            var guard = Ready();
            var advice = new Channel("chat", (_, _) =>
            {
                guard = Protected(); return Task.FromResult(new WorkflowExecutionResult(true, "暂停前已确认的建议。"));
            });
            var notYetExecuted = new Channel("codex", Execute); var notYetValidated = new Validation();
            var first = Create("after-advice", advice, notYetExecuted, notYetValidated, _ => Task.FromResult(guard));
            var stopped = await first.RunAsync(configuration);
            Check(!stopped.Success && first.State.Phase == WorkflowLoopPhase.Paused && first.State.Checkpoint == WorkflowCheckpoint.Execution,
                "quota pause after confirmed advice persists the execution checkpoint");
            Check(first.State.CurrentRequestId == Guid.Empty && !first.State.NeedsRecoveryConfirmation
                && advice.Requests.Count == 1 && notYetExecuted.Requests.Count == 0 && notYetValidated.Calls == 0,
                "confirmed advice clears only its delivered request and spends no execution quota");
            var firstTaskId = first.State.RunId;
            var resumedChat = new Channel("chat", Reply); var resumedCodex = new Channel("codex", Execute); var resumedValidation = new Validation();
            var afterAdvice = Create("after-advice", resumedChat, resumedCodex, resumedValidation);
            Check(afterAdvice.State.Checkpoint == WorkflowCheckpoint.Execution && afterAdvice.State.Advice == "暂停前已确认的建议。",
                "new service reads the durable checkpoint and confirmed advice after a simulated restart");
            Check((await afterAdvice.RunAsync(configuration)).Success && afterAdvice.State.Phase == WorkflowLoopPhase.Completed,
                "explicit restart resumes the same task to completion");
            Check(advice.Requests.Count == 1 && resumedCodex.Requests.Count == 1 && resumedChat.Requests.Count == 1
                && IsReview(resumedChat.Requests.Single()) && resumedValidation.Calls == 1,
                "execution checkpoint resumes execution and review without asking for advice again");
            Check(afterAdvice.State.RunId == firstTaskId && afterAdvice.State.CodexSessionId == Session,
                "resume preserves workflow task identity and the confirmed returned Codex session");
            var requests = advice.Requests.Concat(resumedCodex.Requests).Concat(resumedChat.Requests).ToArray();
            Check(requests.All(value => value.WorkflowTaskId == firstTaskId && value.RequestId == value.TaskId && value.RequestId != Guid.Empty),
                "stable workflow task ID is separate from the backward-compatible transport request ID");
            Check(requests.Select(value => value.RequestId).Distinct().Count() == requests.Length,
                "advice execution and review each have a unique request identity across resume");
            Check(requests.All(value => value.Goal.Contains("TaskId=" + firstTaskId, StringComparison.Ordinal)
                && value.Goal.Contains("RequestId=" + value.RequestId, StringComparison.Ordinal)
                && value.Goal.Contains("Project=" + project, StringComparison.Ordinal)),
                "every model envelope contains the matching workflow task request and project correlation");

            guard = Ready();
            var beforeExecutionChat = new Channel("chat", Reply);
            var executed = new Channel("codex", (_, _) =>
            {
                guard = Protected(); return Task.FromResult(new WorkflowExecutionResult(true, "暂停前已确认的Codex完成报告。", Session));
            });
            var completedValidation = new Validation();
            var second = Create("after-execution", beforeExecutionChat, executed, completedValidation, _ => Task.FromResult(guard));
            Check(!(await second.RunAsync(configuration)).Success && second.State.Checkpoint == WorkflowCheckpoint.Review
                && second.State.Phase == WorkflowLoopPhase.Paused && !second.State.NeedsRecoveryConfirmation,
                "quota pause after completed execution and local validation persists review checkpoint");
            Check(executed.Requests.Count == 1 && beforeExecutionChat.Requests.Count == 1 && completedValidation.Calls == 1
                && second.State.CurrentRequestId == Guid.Empty && second.State.ExecutionReport.Contains("完成报告", StringComparison.Ordinal),
                "completed execution evidence is durable and no quota is spent on premature review");
            var secondTaskId = second.State.RunId;
            var reviewOnly = new Channel("chat", Reply); var neverReexecute = new Channel("codex", Execute); var neverRevalidate = new Validation();
            var afterExecution = Create("after-execution", reviewOnly, neverReexecute, neverRevalidate);
            Check((await afterExecution.RunAsync(configuration)).Success && afterExecution.State.RunId == secondTaskId,
                "review checkpoint explicitly resumes the same task to completion");
            Check(reviewOnly.Requests.Count == 1 && IsReview(reviewOnly.Requests.Single()) && neverReexecute.Requests.Count == 0 && neverRevalidate.Calls == 0,
                "confirmed execution and validation are not repeated when only review remains");
            Check(reviewOnly.Requests.Single().Goal.Contains("暂停前已确认的Codex完成报告", StringComparison.Ordinal)
                && reviewOnly.Requests.Single().WorkflowTaskId == secondTaskId,
                "resumed review carries the existing execution report with the same workflow task");

            var primary = await File.ReadAllBytesAsync(afterExecution.StatePath);
            var backup = await File.ReadAllBytesAsync(afterExecution.StatePath + ".bak");
            var finishedState = afterExecution.State;
            var sameGoal = await afterExecution.RunAsync(configuration);
            Check(!sameGoal.Success && WorkflowTestProtocol.SameState(afterExecution.State, finishedState) && !afterExecution.IsRunning,
                "completed same task is rejected without changing its in-memory progress");
            var rejectedPrimary = await File.ReadAllBytesAsync(afterExecution.StatePath);
            var rejectedBackup = await File.ReadAllBytesAsync(afterExecution.StatePath + ".bak");
            Check(primary.SequenceEqual(rejectedPrimary) && backup.SequenceEqual(rejectedBackup),
                "completed-task rejection leaves primary and backup bytes exactly unchanged");
            Check(reviewOnly.Requests.Count == 1 && neverReexecute.Requests.Count == 0 && neverRevalidate.Calls == 0,
                "completed-task rejection cannot spend model or validation calls");
            var nextGoal = configuration with { TaskId = Guid.NewGuid(), Goal = "验证另一个明确不同的隔离目标；不扩展已完成任务。" };
            Check((await afterExecution.RunAsync(nextGoal)).Success && afterExecution.State.RunId != secondTaskId,
                "a different explicit goal starts a new task identity");
            var history = afterExecution.ReadHistory();
            Check(history.Count == 2 && WorkflowTestProtocol.SameState(history.Single(value => value.RunId == secondTaskId), finishedState)
                && history.Single(value => value.RunId == afterExecution.State.RunId).Goal == nextGoal.Goal,
                "new goal preserves the complete previous task evidence in read-only history");
            Check(neverReexecute.Requests.Count == 1 && neverReexecute.Requests.Single().ContinuationId is null,
                "new independent task never inherits or resumes the previous completed Codex session");

            foreach (var cancelAt in new[] { "advice", "execution" })
            {
                var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                async Task<WorkflowExecutionResult> Hold(WorkflowExecutionRequest _, CancellationToken token)
                { entered.TrySetResult(true); await Task.Delay(Timeout.InfiniteTimeSpan, token); return new(false, "unreachable"); }
                var canceledChat = new Channel("chat", cancelAt == "advice" ? Hold : Reply);
                var canceledCodex = new Channel("codex", cancelAt == "execution" ? Hold : Execute);
                var canceledValidation = new Validation(); var canceled = Create("cancel-" + cancelAt, canceledChat, canceledCodex, canceledValidation);
                using var stop = new CancellationTokenSource(); var pending = canceled.RunAsync(configuration, stop.Token);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); var requestId = canceled.State.CurrentRequestId; stop.Cancel();
                Check(!(await pending.WaitAsync(TimeSpan.FromSeconds(5))).Success && canceled.State.NeedsRecoveryConfirmation
                    && requestId != Guid.Empty && canceled.State.CurrentRequestId == requestId,
                    cancelAt + " cancellation retains the uncertain external request identity and recovery guard");
                var canceledPrimary = await File.ReadAllBytesAsync(canceled.StatePath);
                var canceledBackup = await File.ReadAllBytesAsync(canceled.StatePath + ".bak");
                Check(!canceled.AcknowledgeRecovery().Success && canceled.State.CurrentRequestId == requestId && canceled.State.NeedsRecoveryConfirmation,
                    cancelAt + " acknowledgement cannot erase uncertain delivery or make it replayable");
                var repeatedCanceled = await canceled.RunAsync(configuration);
                var blockedPrimary = await File.ReadAllBytesAsync(canceled.StatePath);
                var blockedBackup = await File.ReadAllBytesAsync(canceled.StatePath + ".bak");
                Check(!repeatedCanceled.Success && canceledPrimary.SequenceEqual(blockedPrimary) && canceledBackup.SequenceEqual(blockedBackup),
                    cancelAt + " blocked recovery and rerun preserve the exact persisted evidence");
                var restartChat = new Channel("chat", Reply); var restartCodex = new Channel("codex", Execute); var restartValidation = new Validation();
                var restarted = Create("cancel-" + cancelAt, restartChat, restartCodex, restartValidation);
                Check(!(await restarted.RunAsync(configuration)).Success && !restarted.AcknowledgeRecovery().Success
                    && restartChat.Requests.Count == 0 && restartCodex.Requests.Count == 0 && restartValidation.Calls == 0,
                    cancelAt + " uncertainty remains blocked after simulated process restart with no replay");
                Check(cancelAt == "execution" ? canceledCodex.StopRequests.SequenceEqual(new[] { requestId }) : canceledCodex.StopRequests.Count == 0,
                    cancelAt + " cancellation only records an appropriate fake Codex safe-stop request");
            }
            Check(!Directory.GetFiles(root, "*.lock", SearchOption.AllDirectories).Any()
                && !Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories).Any(),
                "all resumed canceled and rejected fixture runs clean temporary locks and writes");
            return count;
        }
        finally
        {
            var parent = Path.GetFullPath(FixtureParent) + Path.DirectorySeparatorChar;
            if (!root.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || !AutomationStore.IsSafeDPath(root))
                throw new InvalidOperationException("Refusing unsafe checkpoint fixture cleanup");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    static QuotaGuardSnapshot Ready() => new(QuotaGuardState.Ready, 80, 90, true, false, "模拟有效额度。");
    static QuotaGuardSnapshot Protected() => new(QuotaGuardState.Protected, 2, 90, false, true, "模拟额度保护暂停。");
    sealed class Channel(string id, Func<WorkflowExecutionRequest, CancellationToken, Task<WorkflowExecutionResult>> handler) : IWorkflowConnector
    {
        public string Id => id;
        public WorkflowConnectionState ConnectionState => WorkflowConnectionState.Ready;
        public bool CanExecute => true;
        public List<WorkflowExecutionRequest> Requests { get; } = [];
        public List<Guid> StopRequests { get; } = [];
        public async Task<WorkflowExecutionResult> ExecuteAsync(WorkflowExecutionRequest request, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Requests.Add(request); return WorkflowTestProtocol.Reply(request, await handler(request, cancellationToken)); }
        public Task<SafeStopResult> RequestSafeStopAsync(Guid taskId, CancellationToken cancellationToken = default)
        { StopRequests.Add(taskId); return Task.FromResult(new SafeStopResult(true, "仅记录模拟安全停止，不关闭程序。")); }
    }
    sealed class Validation : IWorkflowProjectValidator
    {
        public int Calls { get; private set; }
        public Task<WorkflowValidationResult> ValidateAsync(string projectRoot, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Calls++; return Task.FromResult(new WorkflowValidationResult(true, "模拟固定本地验证通过。")); }
    }
}

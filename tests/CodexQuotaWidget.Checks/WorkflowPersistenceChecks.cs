using System.Text;
using CodexQuotaWidget.Core;
using CodexQuotaWidget.Core.Automation;
using CodexQuotaWidget.Core.ControlCenter;

/// <summary>Three persistence/replay regressions using isolated D-drive state and fake model channels.</summary>
public static class WorkflowPersistenceChecks
{
    const string FixtureParent = @"D:\CodexQuotaWidget-GitHub\artifacts\workflow-persistence-checks";
    public static int Run() => RunAsync().GetAwaiter().GetResult();
    static async Task<int> RunAsync()
    {
        var fixture = Path.Combine(FixtureParent, Guid.NewGuid().ToString("N"));
        var project = Path.Combine(fixture, "project"); Directory.CreateDirectory(project);
        var configuration = new WorkflowLoopConfiguration
        {
            TaskId = Guid.NewGuid(), AuthorizedWritePaths = ["Program.cs"], ProjectRoot = project, ChatGptThreadId = Guid.NewGuid().ToString(), CallerThreadId = Guid.NewGuid().ToString(),
            Goal = "仅完成第一个隔离记录目标。", MaximumRounds = 1, LocalExecutionAuthorized = true
        };
        try
        {
            var firstChat = new FakeConnector("chat", new(true, "第一个建议。"), new(true, "ACC_REVIEW: COMPLETE"),
                new(true, "另一个目标建议。"), new(true, "ACC_REVIEW: COMPLETE"));
            var firstCodex = new FakeConnector("codex", new(true, "第一个执行报告。", Guid.NewGuid().ToString()),
                new(true, "另一个执行报告。", Guid.NewGuid().ToString()));
            var firstValidator = new FakeValidator();
            var first = Create("history", firstChat, firstCodex, firstValidator);
            var initial = await first.RunAsync(configuration);
            var completedRunId = first.State.RunId;
            var other = await first.RunAsync(configuration with { TaskId = Guid.NewGuid(), Goal = "仅完成另一个隔离记录目标。" });
            var mainBeforeReplay = await File.ReadAllBytesAsync(first.StatePath);
            var backupBeforeReplay = await File.ReadAllBytesAsync(first.StatePath + ".bak");
            var callsBeforeReplay = firstChat.Calls + firstCodex.Calls;
            var replay = await first.RunAsync(configuration);
            var mainAfterReplay = await File.ReadAllBytesAsync(first.StatePath);
            var backupAfterReplay = await File.ReadAllBytesAsync(first.StatePath + ".bak");
            Check(initial.Success && other.Success && !replay.Success && firstChat.Calls + firstCodex.Calls == callsBeforeReplay
                && firstValidator.Calls == 2 && first.ReadHistory().Any(state => state.RunId == completedRunId && state.Phase == WorkflowLoopPhase.Completed)
                && mainBeforeReplay.SequenceEqual(mainAfterReplay)
                && backupBeforeReplay.SequenceEqual(backupAfterReplay),
                "completed task archived by a different goal cannot be replayed or overwrite primary/backup state");

            var callerRoot = Path.Combine(fixture, "caller");
            var callerPath = Path.Combine(callerRoot, "workflow", "loop-state.json");
            var paused = Checkpoint(configuration);
            AtomicStore.Write(callerPath, paused with { Message = "先前检查点。" });
            AtomicStore.Write(callerPath, paused);
            var callerChat = new FakeConnector("chat"); var callerCodex = new FakeConnector("codex"); var callerValidator = new FakeValidator();
            var callerLoop = new WorkflowLoopService(callerRoot, callerChat, callerCodex, callerValidator, _ => Task.FromResult(Ready()));
            var callerMain = await File.ReadAllBytesAsync(callerPath); var callerBackup = await File.ReadAllBytesAsync(callerPath + ".bak");
            var changedCaller = await callerLoop.RunAsync(configuration with { CallerThreadId = Guid.NewGuid().ToString() });
            var callerMainAfter = await File.ReadAllBytesAsync(callerPath); var callerBackupAfter = await File.ReadAllBytesAsync(callerPath + ".bak");
            Check(!changedCaller.Success && callerChat.Calls == 0 && callerCodex.Calls == 0 && callerValidator.Calls == 0
                && callerLoop.State.CallerThreadId == configuration.CallerThreadId
                && callerMain.SequenceEqual(callerMainAfter)
                && callerBackup.SequenceEqual(callerBackupAfter),
                "same goal/project/GPT with a different caller cannot reuse a confirmed checkpoint");

            var corruptRoot = Path.Combine(fixture, "external-corruption");
            var corruptPath = Path.Combine(corruptRoot, "workflow", "loop-state.json");
            AtomicStore.Write(corruptPath, paused with { Message = "有效备份检查点。" }); AtomicStore.Write(corruptPath, paused);
            var corruptChat = new FakeConnector("chat"); var corruptCodex = new FakeConnector("codex"); var corruptValidator = new FakeValidator();
            var corruptLoop = new WorkflowLoopService(corruptRoot, corruptChat, corruptCodex, corruptValidator, _ => Task.FromResult(Ready()));
            var inMemory = corruptLoop.State;
            var corruptPrimary = Encoding.UTF8.GetBytes("{invalid-json-after-service-construction");
            await File.WriteAllBytesAsync(corruptPath, corruptPrimary);
            var preservedBackup = await File.ReadAllBytesAsync(corruptPath + ".bak");
            var rejected = await corruptLoop.RunAsync(configuration);
            var corruptMainAfter = await File.ReadAllBytesAsync(corruptPath); var corruptBackupAfter = await File.ReadAllBytesAsync(corruptPath + ".bak");
            Check(!rejected.Success && corruptChat.Calls == 0 && corruptCodex.Calls == 0 && corruptValidator.Calls == 0
                && !corruptLoop.IsRunning && WorkflowTestProtocol.SameState(corruptLoop.State, inMemory)
                && corruptPrimary.SequenceEqual(corruptMainAfter)
                && preservedBackup.SequenceEqual(corruptBackupAfter),
                "invalid persisted state discovered after construction preserves primary and backup bytes without model calls");
            return 3;
        }
        finally
        {
            var full = Path.GetFullPath(fixture);
            if (!full.StartsWith(Path.GetFullPath(FixtureParent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !AutomationStore.IsSafeDPath(full)) throw new InvalidOperationException("拒绝清理异常持久化检查夹具路径。");
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        }

        WorkflowLoopService Create(string name, IWorkflowConnector chat, IWorkflowConnector codex, IWorkflowProjectValidator validator)
            => new(Path.Combine(fixture, name), chat, codex, validator, _ => Task.FromResult(Ready()));
    }
    static WorkflowLoopState Checkpoint(WorkflowLoopConfiguration configuration) => new()
    {
        RunId = configuration.TaskId, AuthorizedWritePaths = configuration.AuthorizedWritePaths,
        StepWritePaths = configuration.AuthorizedWritePaths,
        AuthorizationId = WorkflowAuthorization.CreateAuthorizationId(configuration.TaskId, configuration.ProjectRoot, configuration.Goal, configuration.AuthorizedWritePaths),
        Phase = WorkflowLoopPhase.Paused, Checkpoint = WorkflowCheckpoint.Execution,
        ProjectRoot = configuration.ProjectRoot, ChatGptThreadId = configuration.ChatGptThreadId, CallerThreadId = configuration.CallerThreadId,
        Goal = configuration.Goal, MaximumRounds = configuration.MaximumRounds, Round = 1, UpdatedAt = DateTimeOffset.UtcNow,
        Advice = "仅恢复已确认建议后的阶段。", CurrentRequestId = Guid.Empty, NeedsRecoveryConfirmation = false
    };
    static QuotaGuardSnapshot Ready() => new(QuotaGuardState.Ready, 80, 90, true, false, "纯模拟额度。");
    static void Check(bool value, string message) { if (!value) throw new InvalidOperationException("Workflow persistence: " + message); }
    sealed class FakeConnector(string id, params WorkflowExecutionResult[] results) : IWorkflowConnector
    {
        readonly Queue<WorkflowExecutionResult> responses = new(results);
        public string Id => id;
        public WorkflowConnectionState ConnectionState => WorkflowConnectionState.Ready;
        public bool CanExecute => true;
        public int Calls { get; private set; }
        public Task<WorkflowExecutionResult> ExecuteAsync(WorkflowExecutionRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Calls++;
            if (responses.Count == 0) throw new InvalidOperationException("Unexpected fake model dispatch: " + id);
            return Task.FromResult(WorkflowTestProtocol.Reply(request, responses.Dequeue()));
        }
        public Task<SafeStopResult> RequestSafeStopAsync(Guid taskId, CancellationToken cancellationToken = default)
            => Task.FromResult(new SafeStopResult(false, "该模拟检查没有远程Turn或停止操作。"));
    }
    sealed class FakeValidator : IWorkflowProjectValidator
    {
        public int Calls { get; private set; }
        public Task<WorkflowValidationResult> ValidateAsync(string projectRoot, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Calls++; return Task.FromResult(new WorkflowValidationResult(true, "模拟固定验证通过。")); }
    }
}

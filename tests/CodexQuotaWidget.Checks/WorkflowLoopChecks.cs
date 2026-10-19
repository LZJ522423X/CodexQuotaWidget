using CodexQuotaWidget.Core.Automation;
using CodexQuotaWidget.Core.ControlCenter;

/// <summary>Product state-machine checks using fake AI channels; no real models or system actions.</summary>
public static class WorkflowLoopChecks
{
    const string FixtureParent = @"D:\CodexQuotaWidget-GitHub\artifacts\workflow-loop-checks";
    static readonly string FakeSessionId = "d3c66c42-dd17-45c8-a045-0b907950fbb2";

    public static int Run() => RunAsync().GetAwaiter().GetResult();

    static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool value, string name) { if (!value) throw new InvalidOperationException("Workflow loop: " + name); count++; }
        var root = Path.Combine(FixtureParent, Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "project"); Directory.CreateDirectory(project);
        var configuration = new WorkflowLoopConfiguration
        {
            TaskId = Guid.NewGuid(), AuthorizedWritePaths = ["Program.cs"], ProjectRoot = project, ChatGptThreadId = Guid.NewGuid().ToString(), CallerThreadId = Guid.NewGuid().ToString(),
            Goal = "仅修复当前项目已确认的问题，并运行固定验证。", LocalExecutionAuthorized = true, MaximumRounds = 3
        };
        WorkflowLoopService Create(string name, IWorkflowConnector chat, IWorkflowConnector codex, IWorkflowProjectValidator validation,
            Func<CancellationToken, Task<QuotaGuardSnapshot>>? quota = null)
            => new(Path.Combine(root, name), chat, codex, validation, quota ?? (_ => Task.FromResult(Ready())));

        try
        {
            var sequence = new List<string>();
            var chat = new FakeConnector("chat", sequence,
                new WorkflowExecutionResult(true, "第一轮：修复已有问题。"), new WorkflowExecutionResult(true, "ACC_REVIEW: CONTINUE\n同一目标还需完成一个检查。"),
                new WorkflowExecutionResult(true, "第二轮：补齐该检查。"), new WorkflowExecutionResult(true, "ACC_REVIEW: COMPLETE\n当前目标完成。"));
            var codex = new FakeConnector("codex", sequence,
                new WorkflowExecutionResult(true, "第一轮实际修改及验证自报。", FakeSessionId), new WorkflowExecutionResult(true, "第二轮实际修改及验证自报。", FakeSessionId));
            var validator = new FakeValidator(sequence, new WorkflowValidationResult(true, "固定检查通过。"), new WorkflowValidationResult(true, "固定检查再次通过。"));
            var loop = Create("two-rounds", chat, codex, validator);
            var result = await loop.RunAsync(configuration);
            Check(result.Success && loop.State.Phase == WorkflowLoopPhase.Completed && loop.State.Round == 2,
                "CONTINUE followed by COMPLETE finishes exactly two product rounds");
            Check(sequence.SequenceEqual(new[] { "chat:1", "codex:1", "validate:1", "chat:2", "chat:3", "codex:2", "validate:2", "chat:4" }),
                "GPT advice -> Codex -> fixed validation -> GPT review is sequential in both rounds");
            Check(chat.Requests.All(request => request.Provider == WorkflowProvider.ChatGPT)
                && codex.Requests.All(request => request.Provider == WorkflowProvider.Codex), "provider identity remains fixed per connector");
            var requestIds = chat.Requests.Concat(codex.Requests).Select(request => request.TaskId).ToArray();
            Check(requestIds.All(id => id != Guid.Empty) && requestIds.Distinct().Count() == requestIds.Length,
                "each model-stage request gets its own correlation id across rounds");
            Check(chat.Requests[2].Goal.Contains("同一目标还需完成一个检查", StringComparison.Ordinal),
                "second advice request carries the previous confirmed GPT review");
            Check(codex.Requests[0].ContinuationId is null && codex.Requests[1].ContinuationId == FakeSessionId,
                "second Codex round continues only the confirmed returned session");
            Check(validator.Projects.All(value => value == project) && !loop.State.GrantsPowerAuthority && !loop.IsRunning,
                "fixed project validation is retained and loop state grants no power authority");
            Check(chat.Requests[1].Goal.Contains("第一轮实际修改", StringComparison.Ordinal)
                && chat.Requests[1].Goal.Contains("固定检查通过", StringComparison.Ordinal), "review gets both execution report and independent validation");

            // Simulate another product instance holding the same fixed run lease. Refusing to
            // acquire that lease must not publish an Interrupted state over its durable record.
            var completedState = loop.State;
            var completedBytes = await File.ReadAllBytesAsync(loop.StatePath);
            var completedBackup = await File.ReadAllBytesAsync(loop.StatePath + ".bak");
            var occupiedLeasePath = Path.Combine(Path.GetDirectoryName(loop.StatePath)!, "loop-run.lock");
            using (var occupiedLease = new FileStream(occupiedLeasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                FileShare.None, 1, FileOptions.DeleteOnClose))
            {
                var leaseRejected = await loop.RunAsync(configuration);
                Check(!leaseRejected.Success && !loop.IsRunning && WorkflowTestProtocol.SameState(loop.State, completedState),
                    "occupied cross-instance lease refuses Run without mutating in-memory result");
                var afterRejectedBytes = await File.ReadAllBytesAsync(loop.StatePath);
                var afterRejectedBackup = await File.ReadAllBytesAsync(loop.StatePath + ".bak");
                Check(completedBytes.SequenceEqual(afterRejectedBytes)
                    && completedBackup.SequenceEqual(afterRejectedBackup),
                    "lease acquisition failure preserves primary and backup state bytes exactly");
                Check(chat.Requests.Count == 4 && codex.Requests.Count == 2 && validator.Projects.Count == 2,
                    "occupied cross-instance lease cannot dispatch models or validation");
            }

            var limitedChat = new FakeConnector("chat", null, new WorkflowExecutionResult(true, "一个最小步骤。"), new WorkflowExecutionResult(true, "ACC_REVIEW: CONTINUE\n仍有同目标步骤。"));
            var limitedCodex = new FakeConnector("codex", null, new WorkflowExecutionResult(true, "执行完成。", FakeSessionId));
            var limitedValidation = new FakeValidator(null, new WorkflowValidationResult(true, "通过。"));
            var limited = Create("round-limit", limitedChat, limitedCodex, limitedValidation);
            var limitedResult = await limited.RunAsync(configuration with { MaximumRounds = 1 });
            Check(limitedResult.Success && limited.State.Phase == WorkflowLoopPhase.Paused && limited.State.Round == 1,
                "CONTINUE reaches configured round limit and pauses");
            Check(limitedChat.Requests.Count == 2 && limitedCodex.Requests.Count == 1 && limitedValidation.Projects.Count == 1,
                "round limit never dispatches another model task");

            foreach (var (name, review) in new[]
            {
                ("blocked", "ACC_REVIEW: BLOCKED\n证据不足。"),
                ("untrusted-marker", "以下只是示例，并非结论：\nACC_REVIEW: COMPLETE"),
                ("quoted-marker", "> ACC_REVIEW: COMPLETE"),
                ("code-marker", "```text\nACC_REVIEW: COMPLETE\n```")
            })
            {
                var blockedChat = new FakeConnector("chat", null, new WorkflowExecutionResult(true, "一个最小步骤。"), new WorkflowExecutionResult(true, review));
                var blockedCodex = new FakeConnector("codex", null, new WorkflowExecutionResult(true, "执行报告。", FakeSessionId));
                var blockedValidation = new FakeValidator(null, new WorkflowValidationResult(true, "通过。"));
                var blocked = Create(name, blockedChat, blockedCodex, blockedValidation);
                var blockedResult = await blocked.RunAsync(configuration);
                Check(!blockedResult.Success && blocked.State.Phase == WorkflowLoopPhase.Blocked,
                    name + " pauses instead of accepting a model-controlled ambiguous marker");
                Check(blockedChat.Requests.Count == 2 && blockedCodex.Requests.Count == 1,
                    name + " never starts a new development round");
            }
            Check(WorkflowLoopService.ParseReview("\n ACC_REVIEW: COMPLETE \n说明") == "COMPLETE"
                && WorkflowLoopService.ParseReview("ACC_REVIEW: CONTINUE") == "CONTINUE"
                && WorkflowLoopService.ParseReview("ACC_REVIEW: COMPLETE and continue") == "BLOCKED",
                "review parser accepts only exact first nonempty-line directives");

            var failedChat = new FakeConnector("chat", null, new WorkflowExecutionResult(true, "修复步骤。"));
            var failedCodex = new FakeConnector("codex", null, new WorkflowExecutionResult(true, "Codex报告声称成功。", FakeSessionId));
            var failedValidator = new FakeValidator(null, new WorkflowValidationResult(false, "固定检查失败。"));
            var failedLoop = Create("validation-failed", failedChat, failedCodex, failedValidator);
            var failed = await failedLoop.RunAsync(configuration);
            Check(!failed.Success && failedLoop.State.Phase == WorkflowLoopPhase.Blocked
                && failedLoop.State.ValidationReport.Contains("失败", StringComparison.Ordinal), "independent validation failure is preserved and pauses");
            Check(failedChat.Requests.Count == 1 && failedCodex.Requests.Count == 1,
                "failed validation does not report success to GPT or start more AI work");

            var unavailable = new FakeConnector("not-ready", null) { ReadyToExecute = false };
            var available = new FakeConnector("codex", null);
            var unavailableValidation = new FakeValidator(null);
            var unavailableLoop = Create("not-ready", unavailable, available, unavailableValidation);
            Check(!(await unavailableLoop.RunAsync(configuration)).Success && unavailable.Requests.Count == 0
                && available.Requests.Count == 0 && unavailableValidation.Projects.Count == 0, "not-ready runtime refuses before any model or validation request");
            Check(!(await Create("default-disconnected", new DisconnectedWorkflowConnector(), new DisconnectedWorkflowConnector(), new FakeValidator(null))
                .RunAsync(configuration)).Success, "default disconnected connectors cannot fabricate a working loop");

            var rejectedQuota = new[]
            {
                new QuotaGuardSnapshot(QuotaGuardState.Unknown, null, null, false, false, "未知"),
                new QuotaGuardSnapshot(QuotaGuardState.Ready, 3, 90, true, false, "到达3%"),
                new QuotaGuardSnapshot(QuotaGuardState.Ready, 2, 90, true, false, "低于3%"),
                new QuotaGuardSnapshot(QuotaGuardState.Protected, 80, 90, true, true, "保护中"),
                new QuotaGuardSnapshot(QuotaGuardState.Exhausted, 0, 90, false, true, "耗尽"),
                new QuotaGuardSnapshot(QuotaGuardState.Ready, double.NaN, 90, true, false, "异常数值"),
                new QuotaGuardSnapshot(QuotaGuardState.Ready, double.PositiveInfinity, 90, true, false, "异常数值")
            };
            for (var index = 0; index < rejectedQuota.Length; index++)
            {
                var forbiddenChat = new FakeConnector("chat", null);
                var forbiddenCodex = new FakeConnector("codex", null);
                var forbiddenValidation = new FakeValidator(null);
                var forbidden = Create("quota-" + index, forbiddenChat, forbiddenCodex, forbiddenValidation,
                    _ => Task.FromResult(rejectedQuota[index]));
                Check(!(await forbidden.RunAsync(configuration)).Success && forbiddenChat.Requests.Count == 0
                    && forbiddenCodex.Requests.Count == 0 && forbiddenValidation.Projects.Count == 0,
                    "quota case " + index + " blocks initial dispatch");
            }
            Check(WorkflowLoopService.CanDispatch(Ready()) && WorkflowLoopService.CanDispatch(new(QuotaGuardState.Warning, 4, 90, true, true, "低额度仍高于3%")),
                "verified Ready/Warning above3% can dispatch within policy");

            var guard = Ready();
            var adviceDropsQuota = new FakeConnector("chat", null) { Handler = (_, _) =>
            { guard = new(QuotaGuardState.Protected, 3, 90, false, true, "建议后额度变低"); return Task.FromResult(new WorkflowExecutionResult(true, "已返回建议。")); } };
            var noExecution = new FakeConnector("codex", null);
            var noValidation = new FakeValidator(null);
            var adviceLow = Create("quota-drops-after-advice", adviceDropsQuota, noExecution, noValidation, _ => Task.FromResult(guard));
            Check(!(await adviceLow.RunAsync(configuration)).Success && adviceLow.State.Phase == WorkflowLoopPhase.Paused,
                "quota drop after GPT advice pauses before execution");
            Check(adviceDropsQuota.Requests.Count == 1 && noExecution.Requests.Count == 0 && noValidation.Projects.Count == 0,
                "no downstream request after advice-stage quota protection");
            guard = Ready();
            var executionDropsQuota = new FakeConnector("codex", null) { Handler = (_, _) =>
            { guard = new(QuotaGuardState.Protected, 2, 90, false, true, "执行后额度变低"); return Task.FromResult(new WorkflowExecutionResult(true, "已有修改保持。", FakeSessionId)); } };
            var noReview = new FakeConnector("chat", null, new WorkflowExecutionResult(true, "最小步骤。"));
            var completedValidation = new FakeValidator(null, new WorkflowValidationResult(true, "仅本地验证通过。"));
            var executionLow = Create("quota-drops-after-execution", noReview, executionDropsQuota, completedValidation, _ => Task.FromResult(guard));
            Check(!(await executionLow.RunAsync(configuration)).Success && executionLow.State.Phase == WorkflowLoopPhase.Paused,
                "quota drop after execution preserves result and pauses");
            Check(noReview.Requests.Count == 1 && executionDropsQuota.Requests.Count == 1 && completedValidation.Projects.Count == 1,
                "existing result is validated locally without spending quota on review or next task");

            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<WorkflowExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var heldChat = new FakeConnector("chat", null) { Handler = async (request, token) =>
            {
                if (request.Goal.Contains("请评估目标是否完成", StringComparison.Ordinal)) return new(true, "ACC_REVIEW: COMPLETE");
                entered.TrySetResult(true); return await release.Task.WaitAsync(token);
            } };
            var heldCodex = new FakeConnector("codex", null, new WorkflowExecutionResult(true, "确认的执行报告。", FakeSessionId));
            var heldValidation = new FakeValidator(null, new WorkflowValidationResult(true, "通过。"));
            var heldLoop = Create("duplicate-run", heldChat, heldCodex, heldValidation);
            var firstRun = heldLoop.RunAsync(configuration);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var duplicate = await heldLoop.RunAsync(configuration);
                Check(!duplicate.Success && heldLoop.IsRunning && heldChat.Requests.Count == 1 && heldCodex.Requests.Count == 0,
                    "duplicate Run on same service cannot dispatch an extra pending request");
                var crashRoot = Path.Combine(root, "restart-fixture"); Directory.CreateDirectory(Path.Combine(crashRoot, "workflow"));
                await File.WriteAllTextAsync(Path.Combine(crashRoot, "workflow", "loop-state.json"), await File.ReadAllTextAsync(heldLoop.StatePath));
                var recoveredChat = new FakeConnector("chat", null);
                var recoveredCodex = new FakeConnector("codex", null);
                var recoveredValidation = new FakeValidator(null);
                var restarted = new WorkflowLoopService(crashRoot, recoveredChat, recoveredCodex, recoveredValidation, _ => Task.FromResult(Ready()));
                Check(restarted.State.Phase == WorkflowLoopPhase.Blocked && restarted.State.NeedsRecoveryConfirmation,
                    "simulated process restart recognises persisted unconfirmed external request");
                Check(!(await restarted.RunAsync(configuration)).Success && recoveredChat.Requests.Count == 0
                    && recoveredCodex.Requests.Count == 0, "restarted pending request never auto-replays models");
                release.TrySetResult(new(true, "正常返回的第一轮建议。"));
                Check((await firstRun.WaitAsync(TimeSpan.FromSeconds(5))).Success && heldLoop.State.Phase == WorkflowLoopPhase.Completed,
                    "original in-flight run can complete normally after duplicate rejection");
            }
            finally
            {
                heldLoop.Pause(); release.TrySetCanceled();
                try { await firstRun.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
            }

            var cancelEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var canceledChat = new FakeConnector("chat", null) { Handler = async (_, token) =>
            { cancelEntered.TrySetResult(true); await Task.Delay(Timeout.InfiniteTimeSpan, token); return new(false, "unreachable"); } };
            var canceledCodex = new FakeConnector("codex", null);
            var canceledLoop = Create("cancel-pending", canceledChat, canceledCodex, new FakeValidator(null));
            var canceledRun = canceledLoop.RunAsync(configuration);
            await cancelEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)); canceledLoop.Pause();
            Check(!(await canceledRun.WaitAsync(TimeSpan.FromSeconds(5))).Success && canceledLoop.State.NeedsRecoveryConfirmation,
                "pause during unconfirmed AI request persists explicit recovery protection");
            Check(canceledCodex.Requests.Count == 0 && canceledCodex.StopRequests.Count == 0,
                "advice cancellation never invokes local computer or nonexistent Codex stop actions");

            var secret = "sk-fixture_" + new string('a', 24);
            var bearer = "Bearer " + new string('b', 24);
            var password = "password=" + new string('c', 24);
            Check(WorkflowText.HasSecret(secret) && WorkflowText.HasSecret(bearer) && WorkflowText.HasSecret(password), "fake secret patterns are detected");
            Check(!WorkflowText.Safe(secret + " " + bearer + " " + password).Contains(secret, StringComparison.Ordinal)
                && WorkflowText.Safe("12345", 3) == "123", "secret redaction precedes bounded text truncation");
            Check(!WorkflowLoopService.ValidConfiguration(configuration with { Goal = "不应派发 " + secret }),
                "secret-bearing user goal is not sent to models");
            var secretChat = new FakeConnector("chat", null, new WorkflowExecutionResult(true, "建议含 " + secret), new WorkflowExecutionResult(true, "ACC_REVIEW: COMPLETE\n" + bearer));
            var secretCodex = new FakeConnector("codex", null, new WorkflowExecutionResult(true, "执行报告含 " + password, FakeSessionId));
            var secretValidation = new FakeValidator(null, new WorkflowValidationResult(true, "验证报告含 " + secret));
            var secretLoop = Create("secret-redaction", secretChat, secretCodex, secretValidation);
            Check((await secretLoop.RunAsync(configuration)).Success, "fake secret-bearing model prose can complete after redaction");
            var persistedSecretState = await File.ReadAllTextAsync(secretLoop.StatePath);
            Check(!persistedSecretState.Contains(secret, StringComparison.Ordinal) && !persistedSecretState.Contains(bearer, StringComparison.Ordinal)
                && !persistedSecretState.Contains(password, StringComparison.Ordinal), "workflow state never records raw fake secrets");
            Check(secretCodex.Requests.All(request => !request.Goal.Contains(secret, StringComparison.Ordinal))
                && secretChat.Requests.All(request => !request.Goal.Contains(secret, StringComparison.Ordinal) && !request.Goal.Contains(password, StringComparison.Ordinal)),
                "redacted advice/reports are the only text forwarded to the next model");
            Check(!Directory.GetFiles(root, "*.lock", SearchOption.AllDirectories).Any()
                && !Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories).Any(), "all tested runs clean their ephemeral leases and atomic write files");
            var reportDirectory = Path.Combine(project, "diagnostic"); Directory.CreateDirectory(reportDirectory);
            var reportPath = Path.Combine(reportDirectory, "report.md");
            await File.WriteAllTextAsync(reportPath, "既有诊断报告：仅作为隔离测试证据，未运行模型。");
            await File.WriteAllTextAsync(Path.Combine(reportDirectory, "samples.csv"), "time,temp\n0,60\n2,61\n");
            await File.WriteAllTextAsync(Path.Combine(reportDirectory, "summary.json"), "{\"sample_count\":2}");
            foreach (var name in new[] { "gpu_start.txt", "gpu_end.txt" })
                await File.WriteAllTextAsync(Path.Combine(reportDirectory, name), "fixture GPU record");
            var evidenceValidator = new WorkflowReportProjectValidator(project, reportPath);
            Check((await evidenceValidator.ValidateAsync(project, CancellationToken.None)).Success, "report evidence parsed and counts agree without executing a program");
            var existingChat = new FakeConnector("chat", null, new WorkflowExecutionResult(true, "ACC_REVIEW: COMPLETE\n既有报告审核完成。"));
            var existingCodex = new FakeConnector("codex", null);
            var existing = new WorkflowLoopService(Path.Combine(root, "existing-report"), existingChat, existingCodex, evidenceValidator, _ => Task.FromResult(Ready()));
            var existingConfiguration = configuration with { InitialReportPath = reportPath };
            Check((await existing.RunAsync(existingConfiguration)).Success && existing.State.Phase == WorkflowLoopPhase.Completed,
                "existing report starts at validation then review");
            Check(existingCodex.Requests.Count == 0 && existingChat.Requests.Count == 1
                && existingChat.Requests[0].Goal.Contains("既有诊断报告", StringComparison.Ordinal), "no redundant advice or Codex execution for completed work");
            Check(!WorkflowLoopService.SameTask(existing.State, configuration), "report binding is part of durable task identity");
            Check(!WorkflowLoopService.ValidConfiguration(configuration with { InitialReportPath = Path.Combine(reportDirectory, "missing.md") }),
                "missing initial report rejected before dispatch");
            await File.WriteAllTextAsync(Path.Combine(reportDirectory, "summary.json"), "{\"sample_count\":1}");
            Check(!(await evidenceValidator.ValidateAsync(project, CancellationToken.None)).Success, "changed original evidence fails hash verification");
            var inconsistent = new WorkflowReportProjectValidator(project, reportPath);
            Check(!(await inconsistent.ValidateAsync(project, CancellationToken.None)).Success, "CSV JSON count mismatch fails validation");
            return count;
        }
        finally
        {
            var full = Path.GetFullPath(root);
            if (!full.StartsWith(Path.GetFullPath(FixtureParent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !AutomationStore.IsSafeDPath(full)) throw new InvalidOperationException("拒绝清理异常fixture路径。");
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        }
    }

    static QuotaGuardSnapshot Ready() => new(QuotaGuardState.Ready, 80, 90, true, false, "隔离fixture额度有效。");

    sealed class FakeConnector(string id, List<string>? sequence, params WorkflowExecutionResult[] script) : IWorkflowConnector
    {
        readonly Queue<WorkflowExecutionResult> results = new(script);
        public string Id => id;
        public bool ReadyToExecute { get; init; } = true;
        public WorkflowConnectionState ConnectionState => ReadyToExecute ? WorkflowConnectionState.Ready : WorkflowConnectionState.NotConnected;
        public bool CanExecute => ReadyToExecute;
        public List<WorkflowExecutionRequest> Requests { get; } = [];
        public List<Guid> StopRequests { get; } = [];
        public Func<WorkflowExecutionRequest, CancellationToken, Task<WorkflowExecutionResult>>? Handler { get; init; }
        public async Task<WorkflowExecutionResult> ExecuteAsync(WorkflowExecutionRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Requests.Add(request); sequence?.Add(Id + ":" + Requests.Count);
            if (Handler is not null) return WorkflowTestProtocol.Reply(request, await Handler(request, cancellationToken));
            if (results.Count == 0) throw new InvalidOperationException("Fake connector script exhausted: " + Id);
            return WorkflowTestProtocol.Reply(request, results.Dequeue());
        }
        public Task<SafeStopResult> RequestSafeStopAsync(Guid taskId, CancellationToken cancellationToken = default)
        { StopRequests.Add(taskId); return Task.FromResult(new SafeStopResult(true, "仅记录fake安全停止请求。")); }
    }

    sealed class FakeValidator(List<string>? sequence, params WorkflowValidationResult[] script) : IWorkflowProjectValidator
    {
        readonly Queue<WorkflowValidationResult> results = new(script);
        public List<string> Projects { get; } = [];
        public Task<WorkflowValidationResult> ValidateAsync(string projectRoot, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Projects.Add(projectRoot); sequence?.Add("validate:" + Projects.Count);
            if (results.Count == 0) throw new InvalidOperationException("Fake validator script exhausted");
            return Task.FromResult(results.Dequeue());
        }
    }
}

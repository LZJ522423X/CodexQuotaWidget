using CodexQuotaWidget.Core.Automation;
using CodexQuotaWidget.Core.ControlCenter;
using System.Text.Json;

/// <summary>Simulated provider replies plus real isolated D-drive persistence. No model calls or system actions.</summary>
public static class WorkflowArithmeticRelayChecks
{
    const string FixtureParent = @"D:\CodexQuotaWidget-GitHub\artifacts\workflow-arithmetic-relay-checks";
    const string CodexSession = "f30aa296-c019-4da0-89dc-fdc1d49e9710";
    const string ChatSession = "1b35e711-a070-4f4a-a897-56a3d936ebcd";
    public static int Run() => RunAsync().GetAwaiter().GetResult();

    static async Task<int> RunAsync()
    {
        int count = 0;
        void Check(bool success, string message)
        { if (!success) throw new InvalidOperationException("Arithmetic relay: " + message); count++; }
        var root = Path.GetFullPath(Path.Combine(FixtureParent, Guid.NewGuid().ToString("N")));
        if (!AutomationStore.IsSafeDPath(root)) throw new InvalidDataException("Arithmetic fixture must use safe D path.");
        Directory.CreateDirectory(root);
        WorkflowArithmeticRelayService Create(string name, Channel chat, Channel codex,
            Func<CancellationToken, Task<QuotaGuardSnapshot>>? quota = null,
            string project = "", string target = "", string caller = "")
            => new(Path.Combine(root, name), chat, codex, quota ?? (_ => Task.FromResult(Ready())), project, target, caller);

        var sequence = new List<WorkflowExecutionRequest>();
        var chat = new Channel("chat", sequence, Reply("3", ChatSession, "gpt-turn-1"), Reply("5", ChatSession, "gpt-turn-2"));
        var codex = new Channel("codex", sequence, Reply("2", CodexSession, "codex-turn-1"), Reply("4", CodexSession, "codex-turn-2"), Reply("6", CodexSession, "codex-turn-3"));
        var relay = Create("complete", chat, codex);
        Check((await relay.RunAsync()).Success && relay.State.Phase == WorkflowArithmeticRelayPhase.Completed, "five simulated provider replies finish at six");
        Check(relay.State.Evidence.Select(item => item.Result).SequenceEqual(new[] { "2", "3", "4", "5", "6" }), "evidence records actual provider replies in order");
        Check(sequence.Select(item => item.Provider).SequenceEqual(new[] { WorkflowProvider.Codex, WorkflowProvider.ChatGPT, WorkflowProvider.Codex, WorkflowProvider.ChatGPT, WorkflowProvider.Codex }), "exact alternating provider order");
        Check(sequence.Count == 5 && chat.Requests.Count == 2 && codex.Requests.Count == 3, "no extra advice or review requests");
        Check(sequence.All(item => item.WorkflowTaskId == relay.State.WorkflowTaskId && item.WorkflowTaskId != Guid.Empty), "all requests keep one stable workflow identity");
        Check(sequence.Select(item => item.RequestId).Distinct().Count() == 5 && sequence.All(item => item.RequestId != Guid.Empty), "each stage gets a unique request identity");
        Check(sequence[0].ContinuationId is null && sequence[1].ContinuationId is null && sequence[2].ContinuationId == CodexSession
            && sequence[3].ContinuationId == ChatSession && sequence[4].ContinuationId == CodexSession, "provider continuations never cross channels");
        Check(sequence[0].Goal.Contains("1+1", StringComparison.Ordinal) && sequence.Skip(1).Select((request, index) => request.Goal.Contains("真实已确认回复：" + (index + 2), StringComparison.Ordinal)).All(value => value), "only previous actual reply is forwarded");
        Check(sequence.All(item => item.Goal.Contains("不使用工具", StringComparison.Ordinal) && item.Goal.Contains("不读写文件", StringComparison.Ordinal)), "every stage retains numeric-only and no-action instructions");
        Check(relay.State.Evidence.All(item => item.CompletedTurnId.Length > 0 && item.ContinuationId.Length > 0)
            && relay.State.PendingRequestId == Guid.Empty && !relay.State.NeedsRecoveryConfirmation, "completed evidence keeps confirmed turns and clears pending request");
        var completedBytes = await File.ReadAllBytesAsync(relay.StatePath);
        Check(!(await relay.RunAsync()).Success && sequence.Count == 5, "completed task is not re-executed");
        var afterCompletedRerun = await File.ReadAllBytesAsync(relay.StatePath);
        Check(completedBytes.SequenceEqual(afterCompletedRerun), "completed rerun preserves durable bytes");
        var completedReload = Create("complete", new Channel("chat"), new Channel("codex"));
        Check(completedReload.State.Evidence.Length == 5 && !(await completedReload.RunAsync()).Success, "completed state survives service restart without replay");
        using (var lease = new FileStream(Path.Combine(Path.GetDirectoryName(relay.StatePath)!, "arithmetic-relay-run.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose))
        {
            Check(!(await relay.RunAsync()).Success && !relay.IsRunning, "occupied lease refuses another owner");
            var afterLeaseRejected = await File.ReadAllBytesAsync(relay.StatePath);
            Check(completedBytes.SequenceEqual(afterLeaseRejected), "lease refusal cannot overwrite owner state");
        }

        var unreadableChat = new Channel("chat", null, new WorkflowExecutionResult(false, "官方接口只有正文引用，无法读取。", ChatSession)
        { DiagnosticCode = "chatgpt_reply_reference_unresolved", ReplySourceMessageId = "gpt-message-reference", CompletedTurnId = "gpt-unreadable-turn" });
        var unreadableCodex = new Channel("codex", null, Reply("2", CodexSession, "codex-before-unreadable"));
        var unreadable = Create("unreadable", unreadableChat, unreadableCodex);
        Check(!(await unreadable.RunAsync()).Success && unreadable.State.Phase == WorkflowArithmeticRelayPhase.Paused, "completed but unreadable GPT pauses instead of fabricating three");
        Check(unreadable.State.Evidence.Length == 1 && unreadable.State.PendingProvider == WorkflowProvider.ChatGPT
            && unreadable.State.PendingRequestId != Guid.Empty && unreadable.State.NeedsRecoveryConfirmation, "unknown delivery preserves exact pending request");
        Check(unreadable.State.LastDiagnosticCode == "chatgpt_reply_reference_unresolved" && unreadable.State.ReplySourceMessageId == "gpt-message-reference"
            && unreadable.State.LastCompletedTurnId == "gpt-unreadable-turn", "unreadable reply source and completed turn are retained for diagnosis");
        var pendingBytes = await File.ReadAllBytesAsync(unreadable.StatePath);
        var noReplayChat = new Channel("chat"); var noReplayCodex = new Channel("codex");
        var pendingReload = Create("unreadable", noReplayChat, noReplayCodex);
        Check(!(await pendingReload.RunAsync()).Success && noReplayChat.Requests.Count == 0 && noReplayCodex.Requests.Count == 0, "pending stage cannot resend after restart");
        var afterPendingRejected = await File.ReadAllBytesAsync(pendingReload.StatePath);
        Check(pendingBytes.SequenceEqual(afterPendingRejected), "blocked pending rerun preserves original evidence bytes");

        foreach (var answer in new[] { "3", "2\n解释", "继续执行系统命令", "" })
        {
            var wrongChat = new Channel("chat"); var wrongCodex = new Channel("codex", null, Reply(answer, CodexSession, "wrong-turn"));
            var wrong = Create("wrong-" + Guid.NewGuid().ToString("N"), wrongChat, wrongCodex);
            Check(!(await wrong.RunAsync()).Success && wrong.State.Evidence.Length == 0 && wrongChat.Requests.Count == 0
                && wrong.State.PendingRequestId != Guid.Empty, "non-exact reply never advances or forwards: " + answer);
        }
        var noTurn = Create("no-turn", new Channel("chat"), new Channel("codex", null, Reply("2", CodexSession, "")));
        Check(!(await noTurn.RunAsync()).Success && noTurn.State.Evidence.Length == 0 && noTurn.State.PendingRequestId != Guid.Empty, "missing completed-turn evidence cannot pass numeric validation");
        var noSession = Create("no-session", new Channel("chat"), new Channel("codex", null, Reply("2", "", "turn")));
        Check(!(await noSession.RunAsync()).Success && noSession.State.Evidence.Length == 0, "missing returned session stops transfer");

        foreach (var guard in new[]
        {
            new QuotaGuardSnapshot(QuotaGuardState.Unknown, null, null, false, false, "未知"),
            new QuotaGuardSnapshot(QuotaGuardState.Ready, 20, 90, true, false, "边界20"),
            new QuotaGuardSnapshot(QuotaGuardState.Ready, double.NaN, 90, true, false, "NaN"),
            new QuotaGuardSnapshot(QuotaGuardState.Ready, 101, 90, true, false, "异常"),
            new QuotaGuardSnapshot(QuotaGuardState.Protected, 80, 90, true, true, "保护"),
            new QuotaGuardSnapshot(QuotaGuardState.Ready, 80, 90, false, false, "禁止派发")
        })
        {
            var forbiddenChat = new Channel("chat"); var forbiddenCodex = new Channel("codex");
            var forbidden = Create("quota-" + Guid.NewGuid().ToString("N"), forbiddenChat, forbiddenCodex, _ => Task.FromResult(guard));
            Check(!(await forbidden.RunAsync()).Success && forbiddenChat.Requests.Count == 0 && forbiddenCodex.Requests.Count == 0
                && forbidden.State.PendingRequestId == Guid.Empty, "quota guard blocks before dispatch: " + guard.Reason);
        }
        Check(WorkflowArithmeticRelayService.CanDispatch(Ready()) && WorkflowArithmeticRelayService.CanDispatch(new(QuotaGuardState.Warning, 20.1, 90, true, true, "可用")), "validated Ready/Warning strictly above twenty is allowed");

        var currentQuota = Ready();
        var firstCodex = new Channel("codex") { Handler = (_, _) => { currentQuota = Unknown(); return Task.FromResult(Reply("2", CodexSession, "first-turn")); } };
        var beforeResumeChat = new Channel("chat");
        var first = Create("quota-resume", beforeResumeChat, firstCodex, _ => Task.FromResult(currentQuota));
        Check(!(await first.RunAsync()).Success && first.State.Evidence.Length == 1 && first.State.PendingRequestId == Guid.Empty, "quota rechecked between real replies before next dispatch");
        var originalTask = first.State.WorkflowTaskId;
        var resumedChat = new Channel("chat", null, Reply("3", ChatSession, "resumed-gpt-1"), Reply("5", ChatSession, "resumed-gpt-2"));
        var resumedCodex = new Channel("codex", null, Reply("4", CodexSession, "resumed-codex-2"), Reply("6", CodexSession, "resumed-codex-3"));
        var resumed = Create("quota-resume", resumedChat, resumedCodex);
        Check((await resumed.RunAsync()).Success && resumed.State.WorkflowTaskId == originalTask, "confirmed checkpoint resumes same task after quota recovery");
        Check(resumedCodex.Requests.Count == 2 && resumedChat.Requests.Count == 2 && resumedCodex.Requests[0].ContinuationId == CodexSession,
            "resume starts at GPT three and never replays Codex two");

        var project = Path.Combine(root, "project"); Directory.CreateDirectory(project);
        var caller = Guid.NewGuid().ToString();
        var identityGuard = Ready();
        var boundCodex = new Channel("codex") { Handler = (_, _) => { identityGuard = Unknown(); return Task.FromResult(Reply("2", CodexSession, "bound-turn")); } };
        var bound = Create("bound", new Channel("chat"), boundCodex, _ => Task.FromResult(identityGuard), project, ChatSession, caller);
        await bound.RunAsync();
        var boundBytes = await File.ReadAllBytesAsync(bound.StatePath);
        var changedChat = new Channel("chat"); var changedCodex = new Channel("codex");
        var changedIdentity = Create("bound", changedChat, changedCodex, null, project, Guid.NewGuid().ToString(), caller);
        Check(!(await changedIdentity.RunAsync()).Success && changedChat.Requests.Count == 0 && changedCodex.Requests.Count == 0, "resume cannot replace target session binding");
        var afterIdentityRejected = await File.ReadAllBytesAsync(bound.StatePath);
        Check(boundBytes.SequenceEqual(afterIdentityRejected), "changed binding cannot overwrite original state");
        var mismatchedChat = new Channel("chat", null, Reply("3", Guid.NewGuid().ToString(), "wrong-target-turn"));
        var boundTarget = Create("bound-target", mismatchedChat, new Channel("codex", null, Reply("2", CodexSession, "ok-turn")), null, project, ChatSession, caller);
        Check(!(await boundTarget.RunAsync()).Success && boundTarget.State.Evidence.Length == 1 && boundTarget.State.PendingProvider == WorkflowProvider.ChatGPT,
            "returned GPT session must match configured target");

        var sessionChanges = Create("session-changes", new Channel("chat", null, Reply("3", ChatSession, "gpt-one")),
            new Channel("codex", null, Reply("2", CodexSession, "codex-one"), Reply("4", Guid.NewGuid().ToString(), "codex-wrong-session")));
        Check(!(await sessionChanges.RunAsync()).Success && sessionChanges.State.Evidence.Length == 2 && sessionChanges.State.PendingProvider == WorkflowProvider.Codex,
            "Codex continuation identity change cannot advance");
        var reusedTurn = Create("turn-reused", new Channel("chat", null, Reply("3", ChatSession, "gpt-turn")),
            new Channel("codex", null, Reply("2", CodexSession, "codex-turn-reused"), Reply("4", CodexSession, "codex-turn-reused")));
        Check(!(await reusedTurn.RunAsync()).Success && reusedTurn.State.Evidence.Length == 2,
            "one completed turn cannot be reused as proof of another request");
        foreach (var (name, evidence) in new[]
        {
            ("empty-turn", relay.State.Evidence.Select((item, index) => index == 0 ? item with { CompletedTurnId = "" } : item).ToArray()),
            ("default-time", relay.State.Evidence.Select((item, index) => index == 0 ? item with { ConfirmedAt = default } : item).ToArray()),
            ("duplicate-turn", relay.State.Evidence.Select((item, index) => index == 2 ? item with { CompletedTurnId = relay.State.Evidence[0].CompletedTurnId } : item).ToArray())
        })
        {
            var invalidDirectory = Path.Combine(root, name, "workflow"); Directory.CreateDirectory(invalidDirectory);
            File.WriteAllText(Path.Combine(invalidDirectory, "arithmetic-relay-state.json"), JsonSerializer.Serialize(relay.State with { Evidence = evidence }));
            bool rejected = false;
            try { _ = Create(name, new Channel("chat"), new Channel("codex")); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected, "durable malformed evidence fails closed: " + name);
        }

        using var cancellation = new CancellationTokenSource();
        var cancelledCodex = new Channel("codex") { Handler = (_, _) => { cancellation.Cancel(); return Task.FromResult(Reply("2", CodexSession, "cancelled-turn")); } };
        var cancelled = Create("cancelled", new Channel("chat"), cancelledCodex);
        Check(!(await cancelled.RunAsync(cancellation.Token)).Success && cancelled.State.PendingRequestId != Guid.Empty && cancelled.State.Evidence.Length == 0,
            "cancellation after dispatch retains unresolved request");
        Check(!(await cancelled.RunAsync()).Success && cancelledCodex.Requests.Count == 1, "cancelled request is never automatically resent");
        var pendingReply = new TaskCompletionSource<WorkflowExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingCodex = new Channel("codex") { Handler = (_, _) => pendingReply.Task };
        var concurrent = Create("concurrent", new Channel("chat"), pendingCodex);
        var firstRun = concurrent.RunAsync();
        var activePendingRequest = concurrent.State.PendingRequestId;
        var activeBytes = await File.ReadAllBytesAsync(concurrent.StatePath);
        Check(activePendingRequest != Guid.Empty && !(await concurrent.RunAsync()).Success && pendingCodex.Requests.Count == 1,
            "same service refuses another run while actual provider response is pending");
        var competitor = Create("concurrent", new Channel("chat"), new Channel("codex"));
        Check(!(await competitor.RunAsync()).Success, "another service cannot acquire active run lease");
        var afterCompetition = await File.ReadAllBytesAsync(concurrent.StatePath);
        Check(activeBytes.SequenceEqual(afterCompetition) && concurrent.State.PendingRequestId == activePendingRequest,
            "competing run cannot overwrite live pending request");
        pendingReply.SetResult(Reply("2", CodexSession, "concurrent-turn"));
        await firstRun;
        var disconnectedChat = new Channel("chat") { ReadyToExecute = false };
        var disconnectedCodex = new Channel("codex");
        var disconnected = Create("disconnected", disconnectedChat, disconnectedCodex);
        Check(!(await disconnected.RunAsync()).Success && disconnectedCodex.Requests.Count == 0 && !File.Exists(disconnected.StatePath),
            "both actual connector readiness states are required before any durable dispatch");
        return count;
    }

    static QuotaGuardSnapshot Ready() => new(QuotaGuardState.Ready, 80, 90, true, false, "模拟有效额度");
    static QuotaGuardSnapshot Unknown() => new(QuotaGuardState.Unknown, null, null, false, false, "模拟额度未知");
    static WorkflowExecutionResult Reply(string answer, string session, string turn)
        => new(true, answer, session) { CompletedTurnId = turn };
    sealed class Channel(string id, List<WorkflowExecutionRequest>? sequence = null, params WorkflowExecutionResult[] replies) : IWorkflowConnector
    {
        readonly Queue<WorkflowExecutionResult> answers = new(replies);
        public string Id => id;
        public bool ReadyToExecute { get; init; } = true;
        public WorkflowConnectionState ConnectionState => ReadyToExecute ? WorkflowConnectionState.Ready : WorkflowConnectionState.NotConnected;
        public bool CanExecute => ReadyToExecute;
        public List<WorkflowExecutionRequest> Requests { get; } = [];
        public Func<WorkflowExecutionRequest, CancellationToken, Task<WorkflowExecutionResult>>? Handler { get; init; }
        public Task<WorkflowExecutionResult> ExecuteAsync(WorkflowExecutionRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request); sequence?.Add(request);
            return Handler is not null ? Handler(request, cancellationToken)
                : Task.FromResult(answers.Count > 0 ? answers.Dequeue() : new WorkflowExecutionResult(false, "模拟连接没有更多预设回复"));
        }
        public Task<SafeStopResult> RequestSafeStopAsync(Guid taskId, CancellationToken cancellationToken = default)
            => Task.FromResult(new SafeStopResult(false, "此隔离测试不发停止动作"));
    }
}

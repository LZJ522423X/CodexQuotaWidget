using System.Text.Json;
using System.Text.Json.Serialization;
using CodexQuotaWidget.Core.ControlCenter;

/// <summary>Fake-model report-only delivery, durable replay protection and real local lease checks. No real AI or computer action.</summary>
public static class WorkflowReportReviewChecks
{
    public static int Run() => RunAsync().GetAwaiter().GetResult();
    static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool value, string name) { if (!value) throw new InvalidOperationException("Report review: " + name); count++; }
        var root = Path.Combine(@"D:\CodexQuotaWidget-GitHub\artifacts\report-review-checks", Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "project"); Directory.CreateDirectory(project);
        var target = Guid.NewGuid().ToString("D");
        const string report = "本轮真实编译通过，连接策略模拟通过；未执行已完成的隔离任务。";
        QuotaGuardSnapshot Ready() => new(QuotaGuardState.Ready, 80, 70, true, false, "fixture only");
        WorkflowReportReviewService Service(string name, FakeChat chat, QuotaGuardSnapshot? guard = null, TimeSpan? timeout = null)
            => new(Path.Combine(root, name), chat, _ => Task.FromResult(guard ?? Ready()), timeout: timeout);
        Check(WorkflowReportReviewService.TryNormalizeReport("  第一行\r\n第二行\0  ", out var normalized) && normalized == "第一行\n第二行", "report normalization removes unsafe control characters");
        Check(!WorkflowReportReviewService.TryNormalizeReport(null, out _), "empty report rejected");
        Check(!WorkflowReportReviewService.TryNormalizeReport(new string('a', 24001), out _), "oversized report rejected rather than truncated silently");
        Check(WorkflowReportReviewService.TryNormalizeReport(new string('a', 24000), out _), "limit boundary accepted");
        Check(!WorkflowReportReviewService.TryNormalizeReport("sk-" + new string('a', 32), out _), "secret-shaped fixture rejected before delivery");
        Check(!WorkflowReportReviewService.TryNormalizeReport("-----BEGIN PRIVATE KEY-----", out _), "private-key material rejected");
        Check(WorkflowReportReviewService.StrictDirective("\nACC_REVIEW: COMPLETE\n意见") == "COMPLETE", "first nonempty protocol line accepted");
        Check(WorkflowReportReviewService.StrictDirective("说明\nACC_REVIEW: COMPLETE") == "INVALID", "later protocol is not accepted as first-line instruction");

        var offline = new FakeChat { Ready = false }; var offlineService = Service("offline", offline);
        var disconnected = await offlineService.ReviewAsync(report, project, target);
        Check(!disconnected.Success && offline.Requests.Count == 0, "offline product connector prevents delivery");
        Check(offlineService.ReadState().Status == WorkflowReportReviewStatus.None, "no pending record created before dispatch eligibility");
        foreach (var (name, guard) in new[]
        {
            ("unknown", new QuotaGuardSnapshot(QuotaGuardState.Unknown, null, null, false, false, "unknown")),
            ("threshold", new QuotaGuardSnapshot(QuotaGuardState.Ready, 20, 70, true, false, "threshold")),
            ("weekly-empty", new QuotaGuardSnapshot(QuotaGuardState.Ready, 80, 0, true, false, "weekly empty")),
            ("weekly-unknown", new QuotaGuardSnapshot(QuotaGuardState.Ready, 80, null, true, false, "weekly unknown")),
            ("nan", new QuotaGuardSnapshot(QuotaGuardState.Ready, double.NaN, 70, true, false, "nan"))
        })
        {
            var fake = new FakeChat(); var blocked = await Service(name, fake, guard).ReviewAsync(report, project, target);
            Check(!blocked.Success && fake.Requests.Count == 0, "quota " + name + " blocks report send");
        }

        var successChat = new FakeChat(); var successService = Service("complete", successChat);
        var oldLoop = Path.Combine(root, "complete", "workflow", "loop-state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(oldLoop)!); File.WriteAllText(oldLoop, "completed isolated task must not change");
        successChat.BeforeReply = () => Check(successService.ReadState().Status == WorkflowReportReviewStatus.Pending, "durable Pending exists before connector executes");
        var completed = await successService.ReviewAsync(report, project, target);
        Check(completed.Success && completed.Status == WorkflowReportReviewStatus.Completed && completed.Directive == "COMPLETE", "confirmed GPT report result completed");
        Check(completed.TaskId != Guid.Empty && completed.RequestId != Guid.Empty && completed.TaskId != completed.RequestId, "task and request IDs unique and separate");
        var request = successChat.Requests.Single();
        Check(request.Provider == WorkflowProvider.ChatGPT && request.ContinuationId == target && request.TaskId == completed.RequestId
            && request.WorkflowTaskId == completed.TaskId, "connector gets fixed target and correctly correlated task/request");
        Check(request.Goal.Contains(completed.TaskId.ToString("D")) && request.Goal.Contains(completed.RequestId.ToString("D"))
            && request.Goal.Contains(project) && request.Goal.Contains("不是重新执行既有两轮隔离验收"), "prompt fixes scope and report-only purpose");
        Check(File.ReadAllText(oldLoop) == "completed isolated task must not change", "legacy loop-state and completed isolation untouched");
        var cached = await successService.ReviewAsync(report, project, target);
        Check(cached.Success && cached.Cached && successChat.Requests.Count == 1 && cached.RequestId == completed.RequestId, "same report cached without duplicate delivery");
        var reopenedChat = new FakeChat { Ready = false };
        var reopened = await Service("complete", reopenedChat).ReviewAsync(report, project, target);
        Check(reopened.Cached && reopened.Success && reopenedChat.Requests.Count == 0, "completed result reread across restart without a live connection");
        Check(!File.Exists(Path.Combine(root, "complete", "workflow", "report-review.lock"))
            && Directory.GetFiles(Path.Combine(root, "complete", "workflow"), "*.tmp").Length == 0, "lease and transaction temp cleaned");

        var continuing = new FakeChat { Reply = "ACC_REVIEW: CONTINUE\n仅修复当前授权连接问题。" };
        var continueResult = await Service("continue", continuing).ReviewAsync(report, project, target);
        Check(continueResult.Success && continueResult.Directive == "CONTINUE" && continuing.Requests.Count == 1, "CONTINUE recorded without invoking Codex or a new loop");
        var invalid = new FakeChat { Reply = "没有协议的普通回复" }; var invalidService = Service("invalid", invalid);
        var invalidReply = await invalidService.ReviewAsync(report, project, target);
        Check(!invalidReply.Success && invalidReply.Status == WorkflowReportReviewStatus.Completed && invalidReply.Directive == "INVALID", "native completed reply with invalid protocol is not claimed approved");
        await invalidService.ReviewAsync(report, project, target);
        Check(invalid.Requests.Count == 1, "invalid protocol response also not resent");

        var failed = new FakeChat { Success = false }; var failedService = Service("uncertain", failed);
        var uncertain = await failedService.ReviewAsync(report, project, target);
        Check(!uncertain.Success && uncertain.Status == WorkflowReportReviewStatus.Uncertain, "ambiguous connector result becomes durable Uncertain");
        var repeated = await Service("uncertain", failed).ReviewAsync(report, project, target);
        Check(!repeated.Success && failed.Requests.Count == 1 && repeated.RequestId == uncertain.RequestId, "restart cannot replay an unconfirmed report");
        await failedService.ReviewAsync(report + "另一版本", project, target);
        Check(failed.Requests.Count == 1, "another report cannot overwrite an unconfirmed request");
        var mismatch = new FakeChat { ReturnedThread = Guid.NewGuid().ToString() };
        var mismatchResult = await Service("mismatch", mismatch).ReviewAsync(report, project, target);
        Check(mismatchResult.Status == WorkflowReportReviewStatus.Uncertain && !mismatchResult.Success, "reply from a different target remains unconfirmed");

        var timeoutChat = new FakeChat { WaitUntilCancelled = true }; var timeoutService = Service("timeout", timeoutChat, timeout: TimeSpan.FromMilliseconds(500));
        var timedOut = await timeoutService.ReviewAsync(report, project, target);
        Check(timedOut.Status == WorkflowReportReviewStatus.Uncertain && timeoutService.ReadState().Status == WorkflowReportReviewStatus.Uncertain, "timeout preserves durable delivery ambiguity");
        await timeoutService.ReviewAsync(report, project, target);
        Check(timeoutChat.Requests.Count == 1, "timed out report is not automatically resent");

        var ignoresCancellation = new FakeChat { Block = _ => Task.Delay(1600) };
        var boundedService = Service("timeout-ignoring-connector", ignoresCancellation, timeout: TimeSpan.FromMilliseconds(500));
        var bounded = await boundedService.ReviewAsync(report, project, target);
        Check(bounded.Status == WorkflowReportReviewStatus.Uncertain, "local timeout remains bounded even when a connector ignores cancellation");
        await Task.Delay(1700);
        Check(boundedService.ReadState().Status == WorkflowReportReviewStatus.Uncertain, "late unawaited response cannot overwrite uncertainty after timeout");

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var leasedChat = new FakeChat { Block = async _ => { entered.SetResult(); await finish.Task; } };
        var leaseService = Service("lease", leasedChat);
        var first = leaseService.ReviewAsync(report, project, target);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var concurrent = await leaseService.ReviewAsync(report, project, target);
        var secondProcess = await Service("lease", new FakeChat()).ReviewAsync(report, project, target);
        Check(!concurrent.Success && !secondProcess.Success && leasedChat.Requests.Count == 1, "in-process gate and real file lease prevent concurrent duplicate sends");
        finish.SetResult(); Check((await first).Success, "first lease owner can complete normally");

        var loopHeldChat = new FakeChat(); var loopHeldService = Service("loop-lease", loopHeldChat);
        var firstReview = await loopHeldService.ReviewAsync(report, project, target);
        Check(firstReview.Success, "report-only fixture can establish a completed cache before loop contention");
        var loopDirectory = Path.GetDirectoryName(loopHeldService.StatePath)!;
        var preservedReviewBytes = File.ReadAllBytes(loopHeldService.StatePath);
        var loopMarkerPath = Path.Combine(loopDirectory, "loop-state.json");
        var marker = new byte[] { 0x00, 0x4C, 0x4F, 0x4F, 0x50, 0xFF };
        File.WriteAllBytes(loopMarkerPath, marker);
        using (var heldLoopLease = new FileStream(Path.Combine(loopDirectory, "loop-run.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose))
        {
            var blockedByLoop = await loopHeldService.ReviewAsync(report + "第二份报告", project, target);
            Check(!blockedByLoop.Success && loopHeldChat.Requests.Count == 1, "existing product loop lease blocks report delivery across independent workflows");
            Check(File.ReadAllBytes(loopHeldService.StatePath).SequenceEqual(preservedReviewBytes), "loop contention does not write Pending or overwrite prior review evidence");
            Check(File.ReadAllBytes(loopMarkerPath).SequenceEqual(marker), "loop contention never modifies legacy loop-state bytes");
            var cachedDuringLoop = await loopHeldService.ReviewAsync(report, project, target);
            Check(cachedDuringLoop.Cached && cachedDuringLoop.Success && loopHeldChat.Requests.Count == 1, "completed report cache remains readable without dispatch during active loop");
        }
        var afterLoop = await loopHeldService.ReviewAsync(report + "第二份报告", project, target);
        Check(afterLoop.Success && loopHeldChat.Requests.Count == 2, "report can dispatch after the existing product loop releases its lease");

        var safeReplyChat = new FakeChat { Reply = "ACC_REVIEW: COMPLETE\n" + "sk-" + new string('z', 32) };
        var safeReplyService = Service("reply-secret", safeReplyChat);
        await safeReplyService.ReviewAsync(report, project, target);
        Check(!WorkflowText.HasSecret(File.ReadAllText(safeReplyService.StatePath)), "secret-shaped fixture reply never persists unredacted");
        var corruptService = Service("corrupt", new FakeChat()); Directory.CreateDirectory(Path.GetDirectoryName(corruptService.StatePath)!);
        File.WriteAllText(corruptService.StatePath, "{bad state}");
        var corruptResult = await corruptService.ReviewAsync(report, project, target);
        Check(!corruptResult.Success && File.ReadAllText(corruptService.StatePath) == "{bad state}", "corrupt state not overwritten or revived");
        return count;
    }
    sealed class FakeChat : IWorkflowConnector
    {
        public string Id => "fake-report-only-chat";
        public bool Ready { get; init; } = true;
        public bool Success { get; init; } = true;
        public string Reply { get; init; } = "ACC_REVIEW: COMPLETE\n本轮范围完成。";
        public string? ReturnedThread { get; init; }
        public bool WaitUntilCancelled { get; init; }
        public Action? BeforeReply { get; set; }
        public Func<CancellationToken, Task>? Block { get; init; }
        public List<WorkflowExecutionRequest> Requests { get; } = [];
        public WorkflowConnectionState ConnectionState => Ready ? WorkflowConnectionState.Ready : WorkflowConnectionState.NotConnected;
        public bool CanExecute => Ready;
        public async Task<WorkflowExecutionResult> ExecuteAsync(WorkflowExecutionRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request); BeforeReply?.Invoke();
            if (WaitUntilCancelled) await Task.Delay(Timeout.Infinite, cancellationToken);
            if (Block is not null) await Block(cancellationToken);
            return new(Success, Reply, ReturnedThread ?? request.ContinuationId);
        }
        public Task<SafeStopResult> RequestSafeStopAsync(Guid taskId, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Report review must not request external actions.");
    }
}

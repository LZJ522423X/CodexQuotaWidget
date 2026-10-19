using CodexQuotaWidget.Core.ControlCenter;

/// <summary>Pure presentation fixtures; no AI requests, desktop interaction, persistence or power actions.</summary>
public static class CapsuleActivityChecks
{
    public static int Run()
    {
        var count = 0;
        void Check(bool condition, string scenario)
        {
            if (!condition) throw new InvalidOperationException("Capsule activity: " + scenario);
            count++;
        }
        var runId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var sessionId = Guid.NewGuid().ToString();
        var baseState = new WorkflowLoopState
        {
            RunId = runId, Round = 2, CurrentRequestId = requestId,
            CodexSessionId = sessionId, Goal = "修复胶囊位置保存", Message = "当前请求已派发"
        };
        CapsuleActivityPresentation Present(WorkflowLoopPhase phase, bool active = true)
            => CapsuleActivityPresentation.From(null, baseState with { Phase = phase }, active, "已更新 19:00:00");

        var empty = CapsuleActivityPresentation.From(null, null, false, null);
        Check(empty.Summary == "空闲" && empty.Detail is null && empty.Tone == "Muted", "no data is idle with no empty tooltip");
        var zeroPause = CapsuleActivityPresentation.From(new() { DispatchPaused = true, SafeStopRequested = true }, new(), false, "已更新");
        Check(zeroPause.Summary == "空闲" && zeroPause.Detail is null, "empty paused queue is not permanent activity");
        var completedLocal = new ControlCenterSnapshot { Tasks = [new() { Id = Guid.NewGuid(), State = WorkItemState.Completed, Title = "历史任务" }] };
        Check(CapsuleActivityPresentation.From(completedLocal, null, false, null).Detail is null, "only terminal local tasks produce no phantom activity");

        var advice = Present(WorkflowLoopPhase.AwaitingAdvice);
        Check(advice.Summary == "等待 GPT 建议 · 第 2 轮" && advice.Tone == "Accent", "real product advice phase is specific");
        Check(advice.Detail!.Contains("当前目标：修复胶囊位置保存") && advice.Detail.Contains("匹配回复"), "advice tooltip explains target and matching evidence");
        var executing = Present(WorkflowLoopPhase.Executing);
        Check(executing.Summary == "Codex 执行 · 第 2 轮" && executing.Tone == "Accent", "bound request displays Codex execution stage");
        Check(executing.Detail!.Contains("派发本轮请求") && executing.Detail.Contains("原生Turn") && executing.Detail.Contains("不推测"), "execution wording stays within dispatch evidence");
        var missingSession = CapsuleActivityPresentation.From(null, baseState with { Phase = WorkflowLoopPhase.Executing, CodexSessionId = "" }, true, null);
        Check(missingSession.Summary.Contains("待确认") && missingSession.Tone == "Muted", "missing native session does not impersonate active native execution");
        var missingRequest = CapsuleActivityPresentation.From(null, baseState with { Phase = WorkflowLoopPhase.Executing, CurrentRequestId = Guid.Empty }, true, null);
        Check(missingRequest.Summary.Contains("待确认") && missingRequest.Tone == "Muted", "missing request does not imply dispatch");
        Check(Present(WorkflowLoopPhase.Validating).Summary == "本地验证 · 第 2 轮", "fixed local validation is distinct from model execution");
        Check(Present(WorkflowLoopPhase.Validating).Detail!.Contains("不表示模型"), "validation tooltip does not imply model work");
        Check(Present(WorkflowLoopPhase.AwaitingReview).Summary == "等待 GPT 复核 · 第 2 轮", "review phase identifies GPT rather than generic runtime");
        Check(Present(WorkflowLoopPhase.Connecting).Summary == "工作流连接中 · 第 2 轮", "connection phase does not claim Codex work");
        Check(Present(WorkflowLoopPhase.Failed).Tone == "Danger", "failed phase uses themed danger token");
        Check(Present(WorkflowLoopPhase.Paused).Summary == "工作流已暂停", "paused product phase is explicit");
        Check(Present(WorkflowLoopPhase.Completed, false).Summary == "空闲", "finished history returns to idle instead of permanent activity");
        Check(Present(WorkflowLoopPhase.Completed, false).Detail is null, "finished history with no pending task has no idle tooltip");
        Check(Present(WorkflowLoopPhase.Interrupted, false).Summary == "工作流已中断", "interrupted history has recovery wording");
        foreach (var phase in new[] { WorkflowLoopPhase.Connecting, WorkflowLoopPhase.AwaitingAdvice, WorkflowLoopPhase.Executing,
                     WorkflowLoopPhase.Validating, WorkflowLoopPhase.AwaitingReview })
            Check(Present(phase, false).Summary == "工作流状态待确认", "persisted phase without live runtime is not current activity: " + phase);
        foreach (var phase in new[] { WorkflowLoopPhase.AwaitingAdvice, WorkflowLoopPhase.AwaitingReview })
        {
            var uncertain = CapsuleActivityPresentation.From(null, baseState with { Phase = phase, CurrentRequestId = Guid.Empty }, true, null);
            Check(uncertain.Summary == "工作流状态待确认", "phase without request does not claim GPT work: " + phase);
        }
        var recovery = CapsuleActivityPresentation.From(null, baseState with { Phase = WorkflowLoopPhase.Completed, NeedsRecoveryConfirmation = true }, false, null);
        Check(recovery.Summary == "工作流待恢复" && recovery.Tone == "Warning", "unresolved recovery wins over a persisted completed label");
        Check(CapsuleActivityPresentation.From(null, null, true, null).Summary == "工作流连接中", "runtime initialization is cautious before state arrives");

        var localRunning = new ControlCenterSnapshot
        {
            Tasks = [new() { Id = Guid.NewGuid(), State = WorkItemState.Running, Title = "修改配置页面" },
                new() { Id = Guid.NewGuid(), State = WorkItemState.Queued, Title = "后续验收" }]
        };
        var tracked = CapsuleActivityPresentation.From(localRunning, null, false, "刚刚更新");
        Check(tracked.Summary == "本地任务跟踪" && tracked.Tone == "Muted", "local Running is labeled tracking only");
        Check(tracked.Detail!.Contains("修改配置页面") && tracked.Detail.Contains("不作为AI执行证据"), "tracking tooltip states actual local task and evidence boundary");
        var priority = CapsuleActivityPresentation.From(localRunning, baseState with { Phase = WorkflowLoopPhase.AwaitingReview }, true, null);
        Check(priority.Summary.StartsWith("等待 GPT 复核"), "active product workflow wins over local tracking");
        foreach (var history in new[] { WorkflowLoopPhase.Completed, WorkflowLoopPhase.Paused, WorkflowLoopPhase.Failed })
            foreach (var localState in new[] { WorkItemState.Running, WorkItemState.Queued, WorkItemState.Paused, WorkItemState.Interrupted })
            {
                var unfinished = new ControlCenterSnapshot { Tasks = [new() { State = localState, Title = "新的本地任务" }] };
                var withoutHistory = CapsuleActivityPresentation.From(unfinished, null, false, null);
                var withHistory = CapsuleActivityPresentation.From(unfinished, baseState with { Phase = history }, false, null);
                Check(withHistory == withoutHistory, "unfinished local task wins over inactive terminal history: " + localState + "/" + history);
            }
        var recoveryPriority = CapsuleActivityPresentation.From(localRunning,
            baseState with { Phase = WorkflowLoopPhase.Completed, NeedsRecoveryConfirmation = true }, false, null);
        Check(recoveryPriority.Summary == "工作流待恢复", "unresolved workflow recovery retains priority over unfinished local work");
        var safeTracking = CapsuleActivityPresentation.From(localRunning with { SafeStopRequested = true }, null, false, null);
        Check(safeTracking.Summary == "本地跟踪待收尾" && safeTracking.Tone == "Warning", "local protection is not reported as native AI pause");
        var queued = new ControlCenterSnapshot { Tasks = [new() { State = WorkItemState.Queued, Title = "验证拖动位置" }] };
        Check(CapsuleActivityPresentation.From(queued, null, false, null).Summary == "等待任务 · 1", "nonempty pending local queue is useful activity");
        Check(CapsuleActivityPresentation.From(queued with { DispatchPaused = true }, null, false, null).Summary == "队列暂停 · 1", "only a nonempty queue shows paused count");
        Check(CapsuleActivityPresentation.From(new() { Tasks = [new() { State = WorkItemState.Paused }] }, null, false, null).Summary == "本地任务暂停 · 1", "paused local task is counted separately");
        Check(CapsuleActivityPresentation.From(new() { Tasks = [new() { State = WorkItemState.Interrupted }] }, null, false, null).Summary == "本地任务中断 · 1", "interrupted local task points to recovery");
        Check(CapsuleActivityPresentation.From(new() { ExitRequested = true }, null, false, null).Summary == "安全退出中", "explicit safe exit remains visible without tasks");
        Check(CapsuleActivityPresentation.From(new() { StorageError = "记录无法保存" }, null, false, null).Tone == "Warning", "storage failure remains visible even with an empty queue");

        var secretGoal = "测试 Bearer " + new string('a', 24);
        var secret = CapsuleActivityPresentation.From(null, baseState with { Phase = WorkflowLoopPhase.AwaitingAdvice, Goal = secretGoal }, true, null);
        Check(secret.Detail!.Contains("敏感内容已省略") && !secret.Detail.Contains(new string('a', 24)), "tooltip excludes secret-looking values");
        var longDetail = CapsuleActivityPresentation.From(null, baseState with { Phase = WorkflowLoopPhase.AwaitingAdvice,
            Goal = new string('目', 5000), Message = new string('说', 5000) }, true, new string('额', 5000));
        Check(longDetail.Detail!.Length < 1000, "goal, explanation and quota tooltip remain bounded");
        Check(new[] { advice, executing, recovery, tracked, empty }.All(item => new[] { "Muted", "Accent", "Warning", "Danger" }.Contains(item.Tone)),
            "presentation returns only existing theme resource keys");
        return count;
    }
}

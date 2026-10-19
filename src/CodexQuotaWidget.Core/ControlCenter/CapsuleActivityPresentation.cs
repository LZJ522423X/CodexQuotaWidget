namespace CodexQuotaWidget.Core.ControlCenter;

/// <summary>Summarizes owned workflow phases without treating local queue state as native AI evidence.</summary>
public sealed record CapsuleActivityPresentation(string Summary, string? Detail, string Tone)
{
    public static CapsuleActivityPresentation From(ControlCenterSnapshot? local, WorkflowLoopState? workflow,
        bool isRunning, string? quotaDetail)
    {
        if (workflow is not null && (workflow.RunId != Guid.Empty || workflow.NeedsRecoveryConfirmation))
        {
            var round = workflow.Round > 0 ? $" · 第 {workflow.Round} 轮" : "";
            if (workflow.NeedsRecoveryConfirmation)
                return Workflow("工作流待恢复", "上轮请求或结果尚未确认；未自动重发。", "Warning", workflow, quotaDetail);

            // A previous terminal workflow must not hide a newer, unfinished local task.
            // Unresolved recovery and a live product workflow still retain priority.
            if (!isRunning && (workflow.Phase is WorkflowLoopPhase.Completed or WorkflowLoopPhase.Paused or WorkflowLoopPhase.Failed)
                && local?.Tasks.Any(task => task.State is WorkItemState.Running or WorkItemState.Queued
                    or WorkItemState.Paused or WorkItemState.Interrupted) == true)
                return Local(local, quotaDetail);

            if (isRunning)
            {
                return workflow.Phase switch
                {
                    WorkflowLoopPhase.Connecting => Workflow("工作流连接中" + round, "正在核验工作流通道或准备下一步；不代表AI正在执行。", "Muted", workflow, quotaDetail),
                    WorkflowLoopPhase.AwaitingAdvice when workflow.CurrentRequestId != Guid.Empty => Workflow("等待 GPT 建议" + round, "已提交本轮建议请求，等待指定会话的匹配回复。", "Accent", workflow, quotaDetail),
                    WorkflowLoopPhase.Executing when workflow.CurrentRequestId != Guid.Empty && !string.IsNullOrWhiteSpace(workflow.CodexSessionId) => Workflow("Codex 执行" + round, "已向绑定的Codex会话派发本轮请求，等待匹配原生Turn的终止结果；不推测思考内容。", "Accent", workflow, quotaDetail),
                    WorkflowLoopPhase.Executing => Workflow("Codex 请求待确认" + round, "本轮原生会话或请求证据尚不完整；不能确认AI当前执行状态。", "Muted", workflow, quotaDetail),
                    WorkflowLoopPhase.Validating => Workflow("本地验证" + round, "正在运行预设的本地验证；该阶段不表示模型正在执行。", "Accent", workflow, quotaDetail),
                    WorkflowLoopPhase.AwaitingReview when workflow.CurrentRequestId != Guid.Empty => Workflow("等待 GPT 复核" + round, "本轮执行与验证结果已提交，等待指定会话复核。", "Accent", workflow, quotaDetail),
                    WorkflowLoopPhase.Completed => Workflow("工作流已完成", "本轮工作流已结束。", "Muted", workflow, quotaDetail),
                    WorkflowLoopPhase.Paused => Workflow("工作流已暂停", "没有继续派发新请求。", "Warning", workflow, quotaDetail),
                    WorkflowLoopPhase.Failed => Workflow("工作流失败", "本轮工作流已停止；请查看报告。", "Danger", workflow, quotaDetail),
                    _ => Workflow("工作流状态待确认", "当前阶段缺少匹配请求证据；不推测AI正在工作。", "Muted", workflow, quotaDetail)
                };
            }

            // Persisted active phases never impersonate a live runtime after restart.
            return workflow.Phase switch
            {
                WorkflowLoopPhase.Completed => Local(local, quotaDetail),
                WorkflowLoopPhase.Paused => Workflow("工作流已暂停", "当前没有运行中的循环。", "Warning", workflow, quotaDetail),
                WorkflowLoopPhase.Interrupted => Workflow("工作流已中断", "当前没有运行中的循环；请先核验恢复记录。", "Warning", workflow, quotaDetail),
                WorkflowLoopPhase.Failed => Workflow("工作流失败", "最近一次工作流已停止；请查看报告。", "Danger", workflow, quotaDetail),
                WorkflowLoopPhase.Connecting or WorkflowLoopPhase.AwaitingAdvice or WorkflowLoopPhase.Executing
                    or WorkflowLoopPhase.Validating or WorkflowLoopPhase.AwaitingReview => Workflow("工作流状态待确认", "保存的阶段不是当前运行证据；请查看工作流恢复状态。", "Warning", workflow, quotaDetail),
                _ => Local(local, quotaDetail)
            };
        }
        if (isRunning)
            return new("工作流连接中", "正在初始化本次工作流，尚未取得可确认的任务阶段。", "Muted");
        return Local(local, quotaDetail);
    }

    static CapsuleActivityPresentation Local(ControlCenterSnapshot? local, string? quotaDetail)
    {
        if (local is null) return new("空闲", null, "Muted");
        if (!string.IsNullOrWhiteSpace(local.StorageError))
            return new("任务记录待检查", Combine(local.StorageError, quotaDetail), "Warning");
        if (local.ExitRequested)
            return new("安全退出中", Combine("正在保存本地任务状态。", quotaDetail), "Warning");
        var running = local.Tasks.FirstOrDefault(task => task.State == WorkItemState.Running);
        var queued = local.Tasks.Count(task => task.State == WorkItemState.Queued);
        var interrupted = local.Tasks.Count(task => task.State == WorkItemState.Interrupted);
        var paused = local.Tasks.Count(task => task.State == WorkItemState.Paused);
        if (running is not null)
            return new(local.SafeStopRequested ? "本地跟踪待收尾" : "本地任务跟踪", Combine(
                "当前本地任务：" + Text(running.Title, 160) + $"\n等待队列：{queued}。\n本地任务状态不作为AI执行证据。", quotaDetail),
                local.SafeStopRequested ? "Warning" : "Muted");
        if (interrupted > 0)
            return new($"本地任务中断 · {interrupted}", Combine("有未完成的本地任务，请查看恢复记录。", quotaDetail), "Warning");
        if (paused > 0)
            return new($"本地任务暂停 · {paused}", Combine("本地任务已保留；不表示AI正在执行。", quotaDetail), "Muted");
        if (queued > 0)
        {
            var next = local.Tasks.First(task => task.State == WorkItemState.Queued);
            return new(local.DispatchPaused || local.SafeStopRequested ? $"队列暂停 · {queued}" : $"等待任务 · {queued}",
                Combine("下一项本地任务：" + Text(next.Title, 160) + "\n队列尚未派发AI请求。", quotaDetail),
                local.DispatchPaused || local.SafeStopRequested ? "Warning" : "Muted");
        }
        // A stale queue pause/stop bit is not meaningful activity when no unfinished task remains.
        return new("空闲", null, "Muted");
    }

    static CapsuleActivityPresentation Workflow(string summary, string explanation, string tone,
        WorkflowLoopState workflow, string? quotaDetail)
    {
        var goal = Text(workflow.Goal, 220);
        var message = Text(workflow.Message, 240);
        return new(summary, Combine((goal.Length > 0 ? "当前目标：" + goal + "\n" : "") + explanation
            + (message.Length > 0 ? "\n阶段说明：" + message : ""), quotaDetail), tone);
    }

    static string Text(string? value, int maximumLength)
        => WorkflowText.Safe(value, maximumLength).Trim();

    static string? Combine(string? activity, string? quotaDetail)
    {
        var detail = Text(activity, 800);
        var quota = Text(quotaDetail, 180);
        if (quota.Length > 0) detail += (detail.Length > 0 ? "\n" : "") + "额度读数：" + quota;
        return detail.Length > 0 ? detail : null;
    }
}

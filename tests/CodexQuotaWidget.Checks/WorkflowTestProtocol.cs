using CodexQuotaWidget.Core.ControlCenter;

/// <summary>Explicit fake provider envelopes for offline checks; never native completion evidence.</summary>
internal static class WorkflowTestProtocol
{
    internal static bool SameState(WorkflowLoopState first, WorkflowLoopState second)
        => System.Text.Json.JsonSerializer.Serialize(first) == System.Text.Json.JsonSerializer.Serialize(second);
    internal static WorkflowExecutionResult Reply(WorkflowExecutionRequest request, WorkflowExecutionResult scripted)
    {
        var message = scripted.Message;
        if (scripted.Success && request.Provider == WorkflowProvider.ChatGPT && request.Goal.Contains("首个非空行必须为 ACC_ADVICE:", StringComparison.Ordinal)
            && !message.StartsWith("ACC_ADVICE:", StringComparison.Ordinal))
            message = WorkflowAdviceProtocol.Format(request, message);
        return scripted with { Message = message, RequestId = request.RequestId, WorkflowTaskId = request.WorkflowTaskId,
            ProjectRoot = request.AuthorizedProjectRoot, AuthorizationId = request.AuthorizationId,
            CompletedTurnId = scripted.Success ? "fixture-turn-" + request.RequestId : "",
            ContinuationId = request.Provider == WorkflowProvider.ChatGPT ? request.ExpectedSessionId : scripted.ContinuationId };
    }
}

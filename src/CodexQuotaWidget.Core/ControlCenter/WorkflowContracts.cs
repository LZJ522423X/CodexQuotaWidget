namespace CodexQuotaWidget.Core.ControlCenter;

public enum WorkflowProvider { Codex, ChatGPT }
public enum WorkflowConnectionState { NotConnected, Ready, Busy, Faulted }
public sealed record WorkflowExecutionRequest(Guid TaskId, WorkflowProvider Provider, string Goal, string? ContinuationId = null)
{
    // TaskId is the original per-request correlation used by transports. Keep it compatible.
    public Guid RequestId => TaskId;
    public Guid WorkflowTaskId { get; init; }
    public string AuthorizedProjectRoot { get; init; } = "";
    public string[] AuthorizedWritePaths { get; init; } = [];
    public string[] StepWritePaths { get; init; } = [];
    public string AuthorizationId { get; init; } = "";
    public bool ReadOnly { get; init; }
    public string ExpectedSessionId { get; init; } = "";
    public WorkflowBackend Backend { get; init; } = WorkflowBackend.LegacyDesktop;
    public WorkflowTextRole TextRole { get; init; }
    public string BackendAccountId { get; init; } = "";
}
public sealed record WorkflowExecutionResult(bool Success, string Message, string? ContinuationId = null)
{
    // Provider-reported completion ID (app-server event or desktop projection).
    // A desktop projection is not a server-native terminal event; connection alone proves neither.
    public string CompletedTurnId { get; init; } = "";
    public string DiagnosticCode { get; init; } = "";
    public string ReplySourceMessageId { get; init; } = "";
    public Guid RequestId { get; init; }
    public Guid WorkflowTaskId { get; init; }
    public string ProjectRoot { get; init; } = "";
    public string AuthorizationId { get; init; } = "";
    // True only when the provider can prove no model turn was dispatched.
    public bool DispatchPrevented { get; init; }
    public WorkflowBackend Backend { get; init; } = WorkflowBackend.LegacyDesktop;
    public WorkflowTextRole TextRole { get; init; }
    public string BackendAccountId { get; init; } = "";
}
public sealed record SafeStopResult(bool Accepted, string Message);

/// <summary>Reserved contract for an explicitly configured executor; local tracking never calls it.</summary>
public interface IWorkflowConnector
{
    string Id { get; }
    WorkflowConnectionState ConnectionState { get; }
    bool CanExecute { get; }
    WorkflowBackendIdentity? BackendIdentity => null;
    Task<WorkflowExecutionResult> ExecuteAsync(WorkflowExecutionRequest request, CancellationToken cancellationToken = default);
    Task<SafeStopResult> RequestSafeStopAsync(Guid taskId, CancellationToken cancellationToken = default);
}

public sealed class DisconnectedWorkflowConnector : IWorkflowConnector
{
    public string Id => "not-connected";
    public WorkflowConnectionState ConnectionState => WorkflowConnectionState.NotConnected;
    public bool CanExecute => false;
    public Task<WorkflowExecutionResult> ExecuteAsync(WorkflowExecutionRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(new WorkflowExecutionResult(false, "尚未连接 AI 执行器；未发送任务"));
    public Task<SafeStopResult> RequestSafeStopAsync(Guid taskId, CancellationToken cancellationToken = default)
        => Task.FromResult(new SafeStopResult(false, "尚未连接 AI 执行器；没有可停止的远程任务"));
}

public enum PowerActionKind { CloseWidget, CloseWorkflow, ShutdownComputer }
public sealed record PowerActionRequest(PowerActionKind Kind, string Reason);
public sealed record SafeActionResult(bool Success, string Message);

/// <summary>Opt-in extension point only. This release never shuts down or kills another application.</summary>
public interface ISafePowerAction
{
    bool CanExecute { get; }
    Task<SafeActionResult> RequestAsync(PowerActionRequest request, CancellationToken cancellationToken = default);
}

public sealed class DisabledPowerAction : ISafePowerAction
{
    public bool CanExecute => false;
    public Task<SafeActionResult> RequestAsync(PowerActionRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(new SafeActionResult(false, "自动关闭和关机接口尚未启用；未执行任何系统操作"));
}

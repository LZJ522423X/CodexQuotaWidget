using System.Collections.Generic;
using System.Threading.Tasks;

namespace CodexQuotaWidget;

/// <summary>Read-only observation and explicit acknowledgement; diagnostics never close applications or request power.</summary>
public interface IReliabilityUiController
{
    ReliabilityUiState GetState();
    string ContinueRecovery();
    string OpenLogs();
    Task<string> RunDiagnosticsAsync();
    SessionBindingUiState GetBindingState();
    Task<SessionCatalogUiResult> RefreshSessionsAsync(string projectRoot);
    SessionBindingUiSaveResult SaveBinding(string projectRoot, string sessionId);
    SessionBindingUiSaveResult ClearBinding();
}

public sealed record ReliabilityUiState
{
    public string CodexState { get; init; } = "未知";
    public string StateSource { get; init; } = "尚无可信状态来源";
    public string StateDetail { get; init; } = "进程存在或本地任务运行中不代表 AI 正在执行。";
    public string ObservedAt { get; init; } = "";
    public bool RecoveryNeedsAttention { get; init; }
    public bool RecoveryAcknowledged { get; init; }
    public string RecoverySummary { get; init; } = "未发现需要确认的启动恢复记录。";
    public string RecoveryDetails { get; init; } = "";
    public string BindingProject { get; init; } = "";
    public string SessionId { get; init; } = "";
    public string TurnId { get; init; } = "";
    public string LastActivity { get; init; } = "";
    public string LastEvent { get; init; } = "";
    public string TrustLevel { get; init; } = "未确认";
    public string EvidenceDescription { get; init; } = "0 · None";
    public string EventSequence { get; init; } = "";
    public string StateTimeline { get; init; } = "尚无当前绑定的状态历史。";
    public string AgentAcknowledgement { get; init; } = "没有可匹配的当前状态事件。";
    public string UnknownReason { get; init; } = "未建立显式 Session 绑定。";
    public string ProjectMatchDescription { get; init; } = "尚无可核验的项目声明。";
}

public sealed record SessionChoiceUi(string SessionId, string MetadataPath, string ProjectRoot, string LastActivity, string LastEvent)
{
    public string DisplayLabel => $"{SessionId}\n活动：{(string.IsNullOrWhiteSpace(LastActivity) ? "未知" : LastActivity)}\n{MetadataPath}";
}
public sealed record SessionCatalogUiResult(bool Success, string Message, IReadOnlyList<SessionChoiceUi> Choices);
public sealed record SessionBindingUiSaveResult(bool Success, string Message, SessionBindingUiState State);
public sealed record SessionBindingUiState
{
    public bool IsBound { get; init; }
    public string ProjectRoot { get; init; } = "";
    public string SessionId { get; init; } = "";
    public string MetadataPath { get; init; } = "";
    public string Status { get; init; } = "尚未绑定真实 Session；不会自动选择最新记录。";
}

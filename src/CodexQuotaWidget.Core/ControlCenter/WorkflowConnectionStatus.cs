namespace CodexQuotaWidget.Core.ControlCenter;

public enum WorkflowLinkState { Disconnected, Connecting, Connected, Conflict, Failed }
public sealed record WorkflowProviderConnection(WorkflowLinkState State, string Message, string DiagnosticCode = "", string? SessionId = null, DateTimeOffset? VerifiedAt = null);
public sealed record WorkflowConnectionCandidate(string Id, string DisplayName, bool CanSelect = false, string Detail = "");

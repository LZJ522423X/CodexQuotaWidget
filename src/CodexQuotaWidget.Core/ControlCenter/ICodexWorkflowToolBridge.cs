using System.Text.Json;

namespace CodexQuotaWidget.Core.ControlCenter;

/// <summary>Explicit, session-scoped product capabilities. Disabled for ordinary project execution.</summary>
public interface ICodexWorkflowToolBridge
{
    string Namespace { get; }
    string Instructions { get; }
    object[] ToolSpecs { get; }
    Task<CodexProjectToolReply> InvokeAsync(string tool, JsonElement arguments, string callId,
        CancellationToken cancellationToken = default);
}

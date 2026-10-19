using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CodexQuotaWidget.Core.Automation;

namespace CodexQuotaWidget.Core.Observation;

/// <summary>
/// An external consumer's receipt, not authenticated proof of work. Received never grants
/// action authority and never confirms that an AI task stopped or a computer action succeeded.
/// </summary>
public sealed record CodexAgentAcknowledgement
{
    [JsonRequired] public int SchemaVersion { get; init; } = 1;
    [JsonRequired] public Guid EventId { get; init; }
    [JsonRequired] public Guid BindingId { get; init; }
    [JsonRequired] public string SessionId { get; init; } = "";
    [JsonRequired] public long SequenceNumber { get; init; }
    [JsonRequired] public string AgentName { get; init; } = "";
    [JsonRequired] public DateTimeOffset ReceivedAt { get; init; }
    [JsonRequired] public string Status { get; init; } = "Received";
    [JsonRequired] public bool PassiveAcknowledgement { get; init; } = true;
    [JsonRequired] public bool GrantsActionAuthority { get; init; }
}

public enum CodexAgentAcknowledgementState { Unavailable, Missing, Received, Rejected }

public sealed record CodexAgentAcknowledgementResult
{
    public CodexAgentAcknowledgementState State { get; init; }
    public string DiagnosticCode { get; init; } = "";
    public string Message { get; init; } = "";
    public CodexAgentAcknowledgement? Acknowledgement { get; init; }
    public bool Received => State == CodexAgentAcknowledgementState.Received;
    // The receipt is intentionally incapable of contributing to executor/stop confirmation.
    public bool GrantsActionAuthority => false;
    public bool ConfirmsTaskStopped => false;
    public bool ConfirmsActionSucceeded => false;
}

/// <summary>
/// Reads one bounded ordinary D-drive file. It does not create a directory/file, launch an
/// agent, acknowledge on the agent's behalf, or change an observation/executor decision.
/// Agent names restrict accepted protocol labels; they are not authentication credentials.
/// </summary>
public sealed class CodexAgentAcknowledgementReader
{
    public const string DefaultDataRoot = @"D:\CodexQuotaWidgetData";
    const int MaximumBytes = 16 * 1024;
    readonly HashSet<string> allowedAgents;
    public string AcknowledgementPath { get; }

    public CodexAgentAcknowledgementReader(string dataRoot = DefaultDataRoot, IEnumerable<string>? allowedAgentNames = null)
    {
        if (!AutomationStore.IsSafeDPath(dataRoot))
            throw new ArgumentException("Agent 回执目录必须是 D 盘普通绝对路径", nameof(dataRoot));
        AcknowledgementPath = Path.Combine(Path.GetFullPath(dataRoot), "events", "agent_ack.json");
        allowedAgents = new HashSet<string>(allowedAgentNames ?? ["dsh", "external-agent"], StringComparer.Ordinal);
        if (allowedAgents.Count is < 1 or > 8 || allowedAgents.Any(name => !ValidAgentName(name)))
            throw new ArgumentException("Agent 名称允许列表必须包含 1 至 8 个受控协议名称", nameof(allowedAgentNames));
    }

    public CodexAgentAcknowledgementResult Read(CodexStateChangedEvent? current, DateTimeOffset? observedAt = null)
    {
        var now = observedAt ?? DateTimeOffset.UtcNow;
        if (now == default || current is null || current.SchemaVersion != 2 || current.SequenceNumber <= 0
            || current.EventId == Guid.Empty || current.BindingId == Guid.Empty || !StatusEvidence.ValidIdentity(current.SessionId)
            || current.CreatedAt == default || current.CreatedAt > now || !current.PassiveNotification || current.GrantsActionAuthority)
            return Result(CodexAgentAcknowledgementState.Unavailable, "NoCurrentCorrelatableEvent", "没有可匹配的当前已绑定状态事件；不读取历史 Agent 回执。");
        try
        {
            if (!IsSafeAcknowledgementPath())
                return Rejected("UnsafePath", "Agent 回执含链接或异常路径，未采纳。");
            if (!File.Exists(AcknowledgementPath))
                return Result(CodexAgentAcknowledgementState.Missing, "NoAcknowledgement", "当前事件尚未收到 Agent 回执。");
            // FileShare.Read denies concurrent in-place replacement/write while these few bytes are read.
            using var input = new FileStream(AcknowledgementPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length is < 1 or > MaximumBytes)
                return Rejected("InvalidSize", "Agent 回执大小异常，未采纳。");
            var bytes = new byte[(int)input.Length];
            input.ReadExactly(bytes);
            if (input.Length != bytes.Length || !IsSafeAcknowledgementPath())
                return Rejected("ChangedDuringRead", "读取期间 Agent 回执发生变化，未采纳。");
            using (var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 3 }))
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    return Rejected("InvalidShape", "Agent 回执必须是单个协议对象，未采纳。");
                var properties = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in document.RootElement.EnumerateObject())
                    if (!properties.Add(property.Name))
                        return Rejected("DuplicateField", "Agent 回执字段重复，未采纳。");
            }
            var ack = JsonSerializer.Deserialize<CodexAgentAcknowledgement>(bytes, new JsonSerializerOptions
            {
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
                MaxDepth = 3
            });
            if (ack is null || ack.SchemaVersion != 1 || ack.SequenceNumber <= 0 || ack.EventId == Guid.Empty
                || ack.BindingId == Guid.Empty || !StatusEvidence.ValidIdentity(ack.SessionId)
                || !ValidAgentName(ack.AgentName) || !allowedAgents.Contains(ack.AgentName)
                || ack.Status != "Received" || !ack.PassiveAcknowledgement || ack.GrantsActionAuthority)
                return Rejected("InvalidProtocol", "Agent 回执协议、名称或被动接收语义不符合约束，未采纳。");
            if (ack.EventId != current.EventId || ack.BindingId != current.BindingId
                || !string.Equals(ack.SessionId, current.SessionId, StringComparison.Ordinal)
                || ack.SequenceNumber != current.SequenceNumber)
                return Rejected("StaleOrMismatchedEvent", "Agent 回执未精确匹配当前事件、绑定、会话和序号，未采纳。");
            if (ack.ReceivedAt == default || ack.ReceivedAt < current.CreatedAt || ack.ReceivedAt > now)
                return Rejected("InvalidReceiptTime", "Agent 回执时间早于当前事件或晚于现在，未采纳。");
            return new()
            {
                State = CodexAgentAcknowledgementState.Received, DiagnosticCode = "ReceivedCurrentEvent",
                Message = "外部 Agent 未经认证地声明已接收当前事件；不代表任务已停止、动作已执行或已授权。", Acknowledgement = ack
            };
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Do not export an untrusted file's contents, attacker-controlled exception text or paths.
            return Rejected("UnreadableOrMalformedAcknowledgement", "Agent 回执损坏或暂时无法读取；真实 Codex 状态保持独立。");
        }
    }

    static bool ValidAgentName(string? value) => value is not null && Regex.IsMatch(value, "^[A-Za-z][A-Za-z0-9._-]{0,63}$");
    bool IsSafeAcknowledgementPath()
    {
        if (!AutomationStore.IsSafeDPath(AcknowledgementPath)) return false;
        // Exists can report false for a dangling symbolic link. Inspect the entry's attributes
        // directly as well, so an absent target cannot make a linked ACK look like an ordinary miss.
        for (string? entry = AcknowledgementPath; entry is not null; entry = Path.GetDirectoryName(entry))
        {
            try
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0
                    || entry == AcknowledgementPath && (attributes & FileAttributes.Directory) != 0) return false;
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return true;
    }
    static CodexAgentAcknowledgementResult Rejected(string code, string message)
        => Result(CodexAgentAcknowledgementState.Rejected, code, message);
    static CodexAgentAcknowledgementResult Result(CodexAgentAcknowledgementState state, string code, string message)
        => new() { State = state, DiagnosticCode = code, Message = message };
}

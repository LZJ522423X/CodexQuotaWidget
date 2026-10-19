using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace CodexQuotaWidget.Core.ControlCenter;

// Persisted values: append only. A backend identity is not a Desktop conversation ID.
public enum WorkflowBackend { LegacyDesktop, SiwcGptReviewer, CodexExecutor }
public enum WorkflowTextRole { None, Advisor, Reviewer }
public sealed record WorkflowBackendIdentity(WorkflowBackend Backend, string AccountId, string SessionId);

public sealed record SiwcWorkflowOptions
{
    public bool Enabled { get; init; }
    public string AccountId { get; init; } = "";
    public string Model { get; init; } = "";
    public string AdviceSessionId { get; init; } = "";
    public string ReviewSessionId { get; init; } = "";
    public string AdvisorPrompt { get; init; } = "仅提出原任务授权范围内的最小建议。证据不足或需要用户确认时返回阻断，不扩大权限。";
    public string ReviewerPrompt { get; init; } = "独立复核执行报告和固定检查证据。不能将执行器自报、编译通过等同于用户目标达成。证据不足时阻断。";
    public string Session(WorkflowTextRole role) => role switch
    { WorkflowTextRole.Advisor => AdviceSessionId, WorkflowTextRole.Reviewer => ReviewSessionId, _ => "" };
    [JsonIgnore] public bool IsValid => IsDraftValid && AccountId.Length == 64 && AccountId.All(Uri.IsHexDigit)
        && Model.Length is > 0 and <= 128 && Model.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
        ;
    [JsonIgnore] public bool IsDraftValid => Enabled && AccountId is not null && Model is not null
        && SessionValid(AdviceSessionId, "siwc-advice-") && SessionValid(ReviewSessionId, "siwc-review-")
        && AdviceSessionId != ReviewSessionId && AdvisorPrompt is { Length: > 0 and <= 4000 }
        && ReviewerPrompt is { Length: > 0 and <= 4000 } && !WorkflowText.HasSecret(AdvisorPrompt + ReviewerPrompt);
    static bool SessionValid(string value, string prefix) => value is not null && value.StartsWith(prefix, StringComparison.Ordinal)
        && Guid.TryParseExact(value[prefix.Length..], "N", out _);
}

public static class SiwcPhaseOne
{
    // Deliberately not configurable. Phase two requires a separately reviewed code change.
    public static bool LiveAccountAndInferenceEnabled => false;
    public const string BlockReason = "实验第一阶段：真实授权与模型调用尚未批准；未联网、未读取Codex凭据。";
    public static void RequireLiveApproval() { if (!LiveAccountAndInferenceEnabled) throw new InvalidOperationException(BlockReason); }
}

public enum SiwcQuotaStatus { Unknown, Available, Insufficient }
public sealed record SiwcQuotaEvidence(string AccountId, SiwcQuotaStatus Status, double? RemainingPercent,
    DateTimeOffset ObservedAt, string Source)
{
    public static SiwcQuotaEvidence Unknown(string account) => new(account, SiwcQuotaStatus.Unknown, null, default, "Unknown_NoSupportedSiwcQuotaSource");
    public bool Allows(string account, int minimum, DateTimeOffset now) => AccountId == account
        && Source == "SIWC_AccountScoped_Verified" && Status == SiwcQuotaStatus.Available
        && ObservedAt <= now && now - ObservedAt <= TimeSpan.FromMinutes(1)
        && RemainingPercent is double value && double.IsFinite(value) && value <= 100 && value > Math.Max(3, minimum);
}

/// <summary>Only application-owned SIWC credentials, never Codex/Desktop authentication files.</summary>
public sealed class SiwcCredential
{
    public string Subject { get; init; } = "";
    public string ClientId { get; init; } = "";
    public string HostId { get; init; } = "";
    public string AccessToken { get; init; } = "";
    public string RefreshToken { get; init; } = "";
    public string IdToken { get; init; } = "";
    public string[] Scopes { get; init; } = [];
    public DateTimeOffset ExpiresAt { get; init; }
    public DateTimeOffset EarliestRefreshAt { get; init; }
    public long Revision { get; init; }
    public bool SignedOut { get; init; }
    [JsonIgnore] public string AccountId => AccountKey(Subject, ClientId);
    [JsonIgnore] public bool HasPlanPermission => !SignedOut && Scopes.Contains("chatgpt.tokens.use.direct", StringComparer.Ordinal)
        && Scopes.Contains("resource.invoke", StringComparer.Ordinal);
    public static string AccountKey(string subject, string client) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes("https://auth.openai.com\n" + client + "\n" + subject)));
    public override string ToString() => "SIWC credential [protected; tokens redacted]";
}

public interface ISiwcCredentialStore
{
    Task<T> WithExclusiveAsync<T>(string accountId, Func<SiwcCredential?, CancellationToken, Task<(SiwcCredential? Replacement, T Result)>> operation,
        CancellationToken token = default);
}

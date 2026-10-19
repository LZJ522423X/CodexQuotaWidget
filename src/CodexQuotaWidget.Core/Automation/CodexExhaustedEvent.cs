using System.Text.Json;
using System.Security.Cryptography;
using CodexQuotaWidget.Core.Observation;

namespace CodexQuotaWidget.Core.Automation;

/// <summary>A passive notification, never an executable request or permission for an external agent.</summary>
public sealed record CodexExhaustedEvent
{
    public int SchemaVersion { get; init; } = 1;
    public Guid EventId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public string TriggerReason { get; init; } = "有效实时五小时额度耗尽，且匹配项目的任务停止已确认";
    public double RemainingPercent { get; init; }
    public string CycleKey { get; init; } = "";
    public long ResetAt { get; init; }
    public string QuotaSource { get; init; } = "LiveCodexQuota";
    public string ProjectRoot { get; init; } = "";
    public CodexStatusObservation TaskStatus { get; init; } = new();
    public bool TaskStoppedConfirmed { get; init; }
    public bool Synthetic { get; init; }
    public string RecoveryFilePath { get; init; } = "";
    public string[] SuggestedNextActions { get; init; } = ["读取恢复记录并检查保存状态", "按用户已授权流程决定下一步；本事件不授权电脑交互或电源操作"];
}

public static class CodexExhaustedEventFactory
{
    /// <summary>
    /// Receipts loaded independently from request/config JSON have different array references.
    /// Compare all serialized evidence structurally, including nested status and suggested actions.
    /// This is equality only, not freshness validation or execution authorization.
    /// </summary>
    public static bool EqualReceiptEvidence(ExhaustionReceipt? first, ExhaustionReceipt? second)
    {
        if (first is null || second is null) return false;
        var firstHash = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(first));
        var secondHash = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(second));
        return CryptographicOperations.FixedTimeEquals(firstHash, secondHash);
    }

    public static ExhaustionReceipt? Confirm(ExhaustionReceipt? quotaEvidence, CodexStatusObservation? status,
        DateTimeOffset now, string projectRoot, string dataRoot)
    {
        if (!ExhaustionDetector.ValidateReceipt(quotaEvidence, now) || status is null || !AutomationStore.IsSafeDPath(dataRoot)
            || !ValidStatus(status, quotaEvidence!, now, projectRoot)) return null;
        var receipt = quotaEvidence! with { CreatedAt = now };
        return receipt with { ExhaustedEvent = new()
        {
            EventId = receipt.EventId, CreatedAt = now, CycleKey = receipt.CycleKey, ResetAt = receipt.ResetAt,
            RemainingPercent = receipt.RemainingPercent, QuotaSource = receipt.Source,
            ProjectRoot = Path.GetFullPath(projectRoot), TaskStatus = status, TaskStoppedConfirmed = true,
            Synthetic = receipt.Synthetic, RecoveryFilePath = Path.Combine(Path.GetFullPath(dataRoot), "codex_resume_state.md")
        } };
    }

    static bool ValidStatus(CodexStatusObservation? status, ExhaustionReceipt quota, DateTimeOffset now, string projectRoot)
    {
        if (status is null || !status.TaskStoppedConfirmed || !status.ProjectAssociationVerified || !status.CoverageComplete || status.ScopedSessionCount < 1
            || !StatusEvidence.HasConfirmedLifecycle(status)
            || status.Synthetic != quota.Synthetic || !StatusEvidence.ValidIdentity(status.SessionId)
            || !StatusEvidence.ValidIdentity(status.TurnId) || status.Source != "LocalCodexSession"
            || status.State is not (CodexObservedState.Completed or CodexObservedState.QuotaExhausted or CodexObservedState.UnableToContinue)
            || status.ObservedAt > now || status.ObservedAt < quota.SecondZeroObservedAt || now - status.ObservedAt > TimeSpan.FromSeconds(30)
            || status.EvidenceAt is not { } evidence || evidence > status.ObservedAt || now - evidence > TimeSpan.FromSeconds(120)
            || evidence < quota.PositiveObservedAt || evidence < quota.FirstZeroObservedAt.AddSeconds(-30)) return false;
        try { return AutomationStore.IsSafeDPath(projectRoot)
                && Path.GetFullPath(status.ProjectRoot).Equals(Path.GetFullPath(projectRoot), StringComparison.OrdinalIgnoreCase); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException) { return false; }
    }

    public static bool Validate(ExhaustionReceipt? receipt, DateTimeOffset now, string projectRoot)
    {
        if (!ExhaustionDetector.ValidateReceipt(receipt, now) || receipt?.ExhaustedEvent is not { } item) return false;
        return item.SchemaVersion == 1 && item.EventId == receipt.EventId && item.CreatedAt == receipt.CreatedAt
            && item.CycleKey == receipt.CycleKey && item.ResetAt == receipt.ResetAt && item.QuotaSource == receipt.Source
            && item.RemainingPercent == 0 && item.Synthetic == receipt.Synthetic && item.TaskStoppedConfirmed
            && string.Equals(item.ProjectRoot,projectRoot, StringComparison.OrdinalIgnoreCase)
            && ValidStatus(item.TaskStatus, receipt, now, projectRoot);
    }
}

/// <summary>One atomic notification on D. Does not dispatch an agent or revive execution after restart.</summary>
public sealed class CodexExhaustedEventStore
{
    readonly string dataRoot;
    public string EventPath => Path.Combine(dataRoot, "events", "codex_exhausted.json");
    public CodexExhaustedEventStore(string dataRoot)
    {
        if (!AutomationStore.IsSafeDPath(dataRoot)) throw new ArgumentException("事件目录必须位于 D 盘普通目录");
        this.dataRoot = Path.GetFullPath(dataRoot);
    }
    public bool Publish(ExhaustionReceipt receipt, DateTimeOffset now)
    {
        if (receipt.Synthetic || !CodexExhaustedEventFactory.Validate(receipt, now, receipt.ExhaustedEvent?.ProjectRoot ?? ""))
            throw new InvalidDataException("只有有效的真实额度与任务停止事件可以输出正式通知");
        if (!AutomationStore.IsSafeDPath(EventPath) || !AutomationStore.IsSafeDPath(EventPath + ".bak"))
            throw new InvalidDataException("事件路径含异常链接");
        if (!File.Exists(EventPath) && File.Exists(EventPath + ".bak"))throw new InvalidDataException("事件主记录缺失，保留证据等待检查");
        if (File.Exists(EventPath))
        {
            if (new FileInfo(EventPath).Length > 64 * 1024) throw new InvalidDataException("事件文件异常");
            var previous = JsonSerializer.Deserialize<CodexExhaustedEvent>(File.ReadAllText(EventPath))
                ?? throw new InvalidDataException("事件主记录损坏");
            if (previous.SchemaVersion != 1 || previous.EventId == Guid.Empty || previous.Synthetic || !previous.TaskStoppedConfirmed
                || previous.RemainingPercent != 0 || previous.TaskStatus is null || previous.CreatedAt == default
                || !AutomationStore.IsSafeDPath(previous.ProjectRoot) || !AutomationStore.IsSafeDPath(previous.RecoveryFilePath)
                || previous.QuotaSource != "LiveCodexQuota" || previous.CycleKey != ExhaustionDetector.CycleKey(previous.ResetAt))
                throw new InvalidDataException("事件版本或结构异常");
            if (previous.EventId == receipt.EventId || previous.CycleKey == receipt.CycleKey) return false;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(EventPath)!);
        if (!AutomationStore.IsSafeDPath(EventPath)) throw new InvalidDataException("事件目录异常");
        AtomicStore.Write(EventPath, receipt.ExhaustedEvent);
        return true;
    }
}

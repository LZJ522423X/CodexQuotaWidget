namespace CodexQuotaWidget.Core.ControlCenter;

public enum WorkItemState { Queued, Running, Paused, Completed, Failed, Cancelled, Interrupted }
public enum QuotaGuardState { Unknown, Ready, Warning, Protected, Exhausted }
public enum AiRuntimeState { NotConnected, Idle, LocalTracking, SafeStopRequested }

/// <summary>A locally tracked work item. Running does not mean a model is executing.</summary>
public sealed record ControlWorkItem
{
    public Guid Id { get; init; }
    public string Title { get; init; } = "";
    public string Notes { get; init; } = "";
    public int Priority { get; init; }
    public WorkItemState State { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public string? FailureReason { get; init; }
    public long Sequence { get; init; }
}

public sealed record QuotaProtectionPolicy
{
    public bool Enabled { get; init; } = true;
    public double WarningPercent { get; init; } = 20;
    public double ProtectionPercent { get; init; } = 5;
    public int MaximumQuotaAgeSeconds { get; init; } = 180;

    internal bool IsValid => double.IsFinite(WarningPercent) && double.IsFinite(ProtectionPercent)
        && ProtectionPercent is >= 0 and <= 100 && WarningPercent is >= 0 and <= 100
        && WarningPercent >= ProtectionPercent && MaximumQuotaAgeSeconds is >= 30 and <= 900;
}

public sealed record QuotaGuardSnapshot(
    QuotaGuardState State, double? FiveHourRemaining, double? WeeklyRemaining,
    bool CanStart, bool Warning, string Reason);

public sealed record ControlOperationResult(bool Success, string Message, Guid? TaskId = null);

public sealed record ControlCenterSnapshot
{
    public long Revision { get; init; }
    public IReadOnlyList<ControlWorkItem> Tasks { get; init; } = Array.Empty<ControlWorkItem>();
    public QuotaGuardSnapshot Guard { get; init; } = new(QuotaGuardState.Unknown, null, null, false, false, "尚未取得可验证的额度");
    public QuotaProtectionPolicy Policy { get; init; } = new();
    public AiRuntimeState RuntimeState { get; init; }
    public WorkflowConnectionState ConnectionState { get; init; }
    public bool DispatchPaused { get; init; }
    public bool SafeStopRequested { get; init; }
    public bool ExitRequested { get; init; }
    public bool CanExitSafely { get; init; }
    public string? StorageError { get; init; }
}

/// <summary>The persisted queue has no access tokens, process handles, or executable commands.</summary>
public sealed record ControlCenterState
{
    public int SchemaVersion { get; init; } = 1;
    public List<ControlWorkItem> Tasks { get; init; } = [];
    public QuotaProtectionPolicy Policy { get; init; } = new();
    public bool DispatchPaused { get; init; }
    public bool SafeStopRequested { get; init; }
    public bool ExitRequested { get; init; }
    public long NextSequence { get; init; }
}

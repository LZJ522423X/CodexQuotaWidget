namespace CodexQuotaWidget.Core.Automation;

/// <summary>A fixed local plan, never a command line or executable path supplied by a model.</summary>
public sealed record ExecutorRequest
{
    public int SchemaVersion { get; init; } = 1;
    public Guid RequestId { get; init; } = Guid.NewGuid();
    public string DataRoot { get; init; } = "";
    public string ProjectRoot { get; init; } = "";
    public LocalActionPlan Plan { get; init; } = new();
    public ExhaustionReceipt? Receipt { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool TestOnly { get; init; } = true;
    /// <summary>Dry-run fixture only: Codex Desktop/CLI are reported absent, with no AI or network dependency.</summary>
    public bool SimulateAiUnavailable { get; init; }
}

public sealed record ExecutorStepResult
{
    public int Index { get; init; }
    public LocalAction Action { get; init; } = new(LocalActionKind.SaveResumeState);
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset FinishedAt { get; init; }
    public bool Success { get; init; }
    public bool Simulated { get; init; }
    public string Message { get; init; } = "";
    public IReadOnlyList<string> Targets { get; init; } = Array.Empty<string>();
}

public sealed record ExecutorRunResult
{
    public Guid RequestId { get; init; }
    public bool Completed { get; init; }
    public bool Success { get; init; }
    public bool DryRun { get; init; }
    public bool PowerRequested { get; init; }
    public string Message { get; init; } = "";
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset FinishedAt { get; init; }
    public string TriggerReason { get; init; } = "";
    public double? FiveHourRemaining { get; init; }
    public string? ExhaustionCycle { get; init; }
    public Guid? ExhaustionEventId { get; init; }
    public IReadOnlyList<ExecutorStepResult> Steps { get; init; } = Array.Empty<ExecutorStepResult>();
    public string? HistoryPath { get; init; }
}

namespace CodexQuotaWidget.Core.Observation;

public enum CodexObservedState
{
    Unknown, Running, WaitingForInput, Completed, AbnormalExit, QuotaExhausted, UnableToContinue
}

public enum CodexTaskState { Working, Waiting, Completed, Failed, Exhausted, Unknown }
public enum CodexObservationTrustLevel { None, ObservedSession, ProjectVerified }
/// <summary>Cumulative evidence, separate from a state or any permission to act.</summary>
public enum CodexEvidenceLevel { None, LogPresent, SessionMatched, ProjectMatched, LifecycleConfirmed }

/// <summary>A read-only observation of a project-bound Codex turn, not a local queue status.</summary>
public sealed record CodexStatusObservation(
    CodexObservedState State = CodexObservedState.Unknown,
    DateTimeOffset ObservedAt = default,
    string Source = StatusEvidence.LocalSessionSource,
    string? SessionId = null,
    string? TurnId = null,
    bool TaskStoppedConfirmed = false,
    string Message = "",
    bool Synthetic = false)
{
    bool actualTaskStopped;
    bool taskStoppedConfirmed = TaskStoppedConfirmed;
    public bool TaskStoppedConfirmed { get => taskStoppedConfirmed && ProjectAssociationVerified && !HasUnresolvedOtherTurnActivity; init => taskStoppedConfirmed = value; }
    public Guid BindingId { get; init; }
    public bool ProjectAssociationVerified { get; init; } = true;
    public string DeclaredProject { get; init; } = "";
    public string SessionFilePath { get; init; } = "";
    public bool HasUnresolvedOtherTurnActivity { get; init; }
    public bool ActualTaskStopped { get => actualTaskStopped || TaskStoppedConfirmed; init => actualTaskStopped = value; }
    public CodexObservationTrustLevel TrustLevel { get; init; }
    public CodexEvidenceLevel EvidenceLevel { get; init; }
    /// <summary>Allowlisted typed event name only; never task text, error payload or commands.</summary>
    public string EvidenceKind { get; init; } = "";
    /// <summary>End byte offset of the actual evidence line in this session file, not its read extent.</summary>
    public long? EvidencePosition { get; init; }
    public CodexTaskState CanonicalState => State switch
    {
        CodexObservedState.Running => CodexTaskState.Working,
        CodexObservedState.WaitingForInput => CodexTaskState.Waiting,
        CodexObservedState.Completed => CodexTaskState.Completed,
        CodexObservedState.AbnormalExit => CodexTaskState.Failed,
        CodexObservedState.UnableToContinue when ActualTaskStopped => CodexTaskState.Failed,
        CodexObservedState.QuotaExhausted => CodexTaskState.Exhausted,
        _ => CodexTaskState.Unknown
    };
    public string UnknownReason => CanonicalState != CodexTaskState.Unknown ? "" : State == CodexObservedState.UnableToContinue
        ? "当前错误尚无任务终止事件；无法确认失败或能否继续。" : Message;
    public DateTimeOffset? LastActivity => EvidenceAt;
    public DateTimeOffset? EvidenceAt { get; init; }
    public string ProjectRoot { get; init; } = "";
    public int ScopedSessionCount { get; init; }
    /// <summary>
    /// All bounded recent-session candidates were readable, and no discovered session has
    /// unresolved activity. This is not proof that unobserved older sessions or background
    /// shell commands have stopped; terminal events describe the Codex turn only.
    /// </summary>
    public bool CoverageComplete { get; init; }
}

public static class StatusEvidence
{
    public const string LocalSessionSource = "LocalCodexSession";
    public static readonly TimeSpan MaximumEvidenceAge = TimeSpan.FromMinutes(2);

    public static bool IsValidEvidenceKind(string? kind) => kind is "" or "task_started" or "turn_started"
        or "task_complete" or "turn_complete" or "turn_aborted" or "request_user_input" or "exec_approval_request"
        or "apply_patch_approval_request" or "request_permissions" or "elicitation_request" or "error" or "stream_error"
        or "item_started" or "item_completed" or "exec_command_begin" or "exec_command_end" or "patch_apply_begin"
        or "patch_apply_end" or "token_count" or "response_input" or "unrecognized_activity";

    public static bool HasConfirmedLifecycle(CodexStatusObservation observation)
        => observation.EvidenceLevel == CodexEvidenceLevel.LifecycleConfirmed
            && observation.ProjectAssociationVerified && observation.TrustLevel == CodexObservationTrustLevel.ProjectVerified
            && ValidIdentity(observation.SessionId) && ValidIdentity(observation.TurnId)
            && observation.EvidenceKind is "task_complete" or "turn_complete" or "turn_aborted"
            && observation.EvidencePosition is > 0;

    public static bool ValidateTerminal(CodexStatusObservation? observation, DateTimeOffset now, string projectRoot)
        => observation is { Synthetic: false, TaskStoppedConfirmed: true, CoverageComplete: true, ProjectAssociationVerified: true }
            && HasConfirmedLifecycle(observation)
            && observation.Source == LocalSessionSource
            && observation.State is CodexObservedState.Completed or CodexObservedState.QuotaExhausted or CodexObservedState.UnableToContinue
            && ValidIdentity(observation.SessionId) && ValidIdentity(observation.TurnId)
            && SameProject(observation.ProjectRoot, projectRoot)
            && observation.ObservedAt <= now && now - observation.ObservedAt <= TimeSpan.FromSeconds(30)
            && observation.EvidenceAt is { } evidence && evidence <= now && now - evidence <= MaximumEvidenceAge;

    public static bool SameProject(string? first, string? second)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(first) && !string.IsNullOrWhiteSpace(second)
                && Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Equals(Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    internal static bool ValidIdentity(string? identity)
        => identity is { Length: > 0 and <= 128 }
            && identity.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}

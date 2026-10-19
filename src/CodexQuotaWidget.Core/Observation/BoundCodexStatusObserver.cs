namespace CodexQuotaWidget.Core.Observation;

/// <summary>Observes exactly the saved native session. Explicit association is not native project proof.</summary>
public sealed class BoundCodexStatusObserver
{
    readonly CodexSessionBinding binding;
    readonly CodexStatusObserver selected;
    readonly CodexStatusObserver global;
    readonly Func<DateTimeOffset> clock;
    readonly string codexHome;
    public BoundCodexStatusObserver(CodexSessionBinding binding, string? codexHome = null, Func<DateTimeOffset>? clock = null)
    {
        this.binding = binding;
        this.codexHome = codexHome ?? CodexSessionPaths.NativeHome;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        selected = new(binding.ProjectRoot.Length > 0 ? binding.ProjectRoot : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), this.codexHome, clock);
        global = new(binding.ProjectRoot.Length > 0 ? binding.ProjectRoot : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), this.codexHome, clock);
    }
    public async Task<CodexStatusObservation> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!binding.IsBound || !CodexSessionPaths.IsSafeLocalDirectory(binding.ProjectRoot))
            return new(CodexObservedState.Unknown, clock(), StatusEvidence.LocalSessionSource, binding.SessionId, null, false,
                "没有有效的项目与会话选择；不会自动采用最新会话。")
            { BindingId = binding.BindingId, ProjectRoot = binding.ProjectRoot, ProjectAssociationVerified = false };
        var observed = await selected.ReadSelectedAsync(binding, cancellationToken).ConfigureAwait(false);
        if (observed.State == CodexObservedState.Unknown || !observed.ProjectAssociationVerified) return observed;
        var globalState = await global.ReadAsync(cancellationToken).ConfigureAwait(false);
        var coverage = globalState.CoverageComplete && globalState.TaskStoppedConfirmed;
        return observed with
        {
            CoverageComplete = coverage,
            TaskStoppedConfirmed = observed.ActualTaskStopped && observed.ProjectAssociationVerified && coverage,
            Message = !coverage && observed.ActualTaskStopped
                ? observed.Message + " 其他会话或全局覆盖尚未确认，禁止自动收尾。" : observed.Message
        };
    }
}

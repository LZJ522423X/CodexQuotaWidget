namespace CodexQuotaWidget.Core.Observation;

/// <summary>Rejects stale observations if any saved binding field changes during native log IO.</summary>
public static class CodexBoundObservationGuard
{
    public static async Task<CodexStatusObservation> ReadAsync(CodexSessionBindingStore store,
        CodexSessionBinding expected, Func<CancellationToken, Task<CodexStatusObservation>> reader,
        CancellationToken cancellationToken = default)
    {
        ValidateCurrent(store.Load(), expected);
        var observation = await reader(cancellationToken).ConfigureAwait(false);
        ValidateCurrent(store.Load(), expected);
        if (observation.BindingId != expected.BindingId || observation.SessionId != expected.SessionId
            || !StatusEvidence.SameProject(observation.ProjectRoot, expected.ProjectRoot)
            || !string.Equals(observation.SessionFilePath, expected.SessionFilePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("状态观察与完整 Session 绑定不一致；未授权动作。");
        return observation;
    }
    static void ValidateCurrent(CodexSessionBindingResult loaded, CodexSessionBinding expected)
    {
        if (!loaded.Success || !expected.IsBound || loaded.Binding != expected)
            throw new InvalidDataException("Session 绑定在读取期间变化或不可验证；未授权动作。");
    }
}

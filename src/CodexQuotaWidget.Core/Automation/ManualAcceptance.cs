namespace CodexQuotaWidget.Core.Automation;

/// <summary>A separate, explicitly authorized hardware acceptance test. Never a real quota-exhaustion receipt.</summary>
public sealed record ManualAcceptanceRequest
{
    public int SchemaVersion { get; init; } = 1;
    public string Owner { get; init; } = ManualAcceptanceRules.Owner;
    public Guid TestId { get; init; } = Guid.NewGuid();
    public string DataRoot { get; init; } = "";
    public string ProjectRoot { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; init; } = DateTimeOffset.UtcNow.AddMinutes(14);
    public string Authorization { get; init; } = "";
    public bool RealActionsAuthorized { get; init; }
    public bool SimulatedExhaustion { get; init; } = true;
    public ExhaustionReceipt? SimulatedQuotaReceipt { get; init; }
    public int DelaySeconds { get; init; } = 90;
    public int LauncherProcessId { get; init; }
    public DateTimeOffset LauncherStartedAt { get; init; }
}

public sealed record ManualAcceptanceReport
{
    public int SchemaVersion { get; init; } = 1;
    public string Owner { get; init; } = ManualAcceptanceRules.Owner;
    public Guid TestId { get; init; }
    public string Status { get; init; } = "Preparing";
    public bool Completed { get; init; }
    public bool Success { get; init; }
    public bool DryRun { get; init; } = true;
    public bool SimulatedExhaustion { get; init; } = true;
    public bool RestartRequested { get; init; }
    public bool ActualRestartVerified { get; init; }
    public DateTimeOffset? RestartAttemptedAt { get; init; }
    public DateTimeOffset? RestartAcceptedAt { get; init; }
    public DateTimeOffset? WindowsBootAt { get; init; }
    public bool LauncherExitVerified { get; init; }
    public int? DelayRemainingSeconds { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public string Message { get; init; } = "";
    public string? RecoveryNotePath { get; init; }
    public string? SnapshotSha256 { get; init; }
    public string? SourceInventoryFingerprint { get; init; }
    public IReadOnlyList<ExecutorStepResult> Steps { get; init; } = Array.Empty<ExecutorStepResult>();
}

public static class ManualAcceptanceRules
{
    public const string Owner = "CodexQuotaWidget.ManualAcceptance";
    public const string Authorization = "UserAuthorizedClashMihomoRestartAcceptance";
    public const int MaximumRequestBytes = 32 * 1024;

    public static bool Validate(ManualAcceptanceRequest? request, DateTimeOffset now, bool realActions, out string message)
    {
        message = "单次实机验收请求无效";
        if (request is null || request.SchemaVersion != 1 || request.Owner != Owner || request.TestId == Guid.Empty
            || request.Authorization != Authorization || !request.SimulatedExhaustion
            || request.SimulatedQuotaReceipt is not { Synthetic: true, ExhaustedEvent: null, PositiveRemaining: 5 }
            || !ExhaustionDetector.ValidateReceipt(request.SimulatedQuotaReceipt, now)
            || request.CreatedAt > now || request.ExpiresAt <= now || request.ExpiresAt <= request.CreatedAt
            || request.ExpiresAt - request.CreatedAt > TimeSpan.FromMinutes(15)
            || now - request.CreatedAt > TimeSpan.FromMinutes(15) || request.DelaySeconds is <45 or >120) return false;
        if (!AutomationStore.IsSafeDPath(request.DataRoot) || !AutomationStore.IsSafeDPath(request.ProjectRoot)
            || !Directory.Exists(request.ProjectRoot))
        { message = "验收项目及资料目录必须位于 D 盘现有普通路径"; return false; }
        var data = Path.GetFullPath(request.DataRoot).TrimEnd('\\');
        var project = Path.GetFullPath(request.ProjectRoot).TrimEnd('\\');
        if (data.Equals(project, StringComparison.OrdinalIgnoreCase) || project.StartsWith(data + "\\", StringComparison.OrdinalIgnoreCase)
            || data.StartsWith(project + "\\", StringComparison.OrdinalIgnoreCase))
        { message = "验收资料目录与项目目录必须分开"; return false; }
        if (realActions && (!request.RealActionsAuthorized || request.LauncherProcessId <= 0
            || request.LauncherStartedAt == default || request.LauncherStartedAt > request.CreatedAt))
        { message = "尚未明确授权真实 Clash/mihomo 关闭和正常重启，或缺少独立启动器证据"; return false; }
        message = realActions ? "用户已授权单次模拟额度耗尽实机验收；未启用生产自动化" : "模拟验收请求验证通过；不会真实关闭程序或重启";
        return true;
    }

    public static string RequestPath(ManualAcceptanceRequest request) => Path.Combine(request.DataRoot, "manual-tests", "requests", $"{request.TestId:N}.json");
    public static string ResultPath(ManualAcceptanceRequest request) => Path.Combine(request.DataRoot, "manual-tests", "results", $"{request.TestId:N}.json");
    public static string CancelPath(ManualAcceptanceRequest request) => Path.Combine(request.DataRoot, "manual-tests", $"{request.TestId:N}.cancel");
}

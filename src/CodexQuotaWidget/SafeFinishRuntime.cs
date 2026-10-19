// 1.4 retires the percentage-triggered PowerShell runner.
// Historical snapshots preserve 1.3; active execution is the typed local Executor.
using System;
using System.Threading.Tasks;
using CodexQuotaWidget.Core.ControlCenter;
namespace CodexQuotaWidget;
internal static class SafeFinishRuntime
{
    internal static void Launch(SafeFinishCheckpointResult checkpoint,double remaining,DateTimeOffset observedAt)
        => throw new NotSupportedException("百分比收尾已停用，请使用独立本地 Executor。");
    internal static Task ValidateAsync(SafeFinishCheckpointResult checkpoint,double remaining,DateTimeOffset observedAt)
        => Task.FromException(new NotSupportedException("百分比收尾已停用，请使用独立本地 Executor。"));
}
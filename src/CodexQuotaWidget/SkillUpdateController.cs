using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using CodexQuotaWidget.Core;
using CodexQuotaWidget.Core.ControlCenter;
using CodexQuotaWidget.Executor;

namespace CodexQuotaWidget;

internal enum SkillUpdatePhase { Ready, Running, Completed, Attention, Busy, Failed, Unavailable }
internal sealed record SkillUpdateResult(SkillUpdatePhase Phase, string Summary, string Details = "");

// One controller per application; both settings surfaces share the same job.
// The existing updater owns validation, transactions, recovery, cleanup and its cross-process mutex.
internal sealed class SkillUpdateController
{
    internal static string Root => Environment.GetEnvironmentVariable("CODEX_QUOTA_WIDGET_SKILL_UPDATER")
        ?? (Directory.Exists(@"D:\Codex\SkillUpdater") ? @"D:\Codex\SkillUpdater" : Path.Combine(Paths.Data, "skill-updater"));
    internal static string Script => Path.Combine(Root, "updater.ps1");
    internal static SkillUpdateController Shared { get; } = new();
    readonly Func<Task<SkillUpdateResult>>? execute;
    readonly Func<CancellationToken, Task<UnifiedUpdateCheck>> check;
    readonly HashSet<string> selected = new(StringComparer.Ordinal);
    int running;
    public event Action? Changed;
    public SkillUpdateResult State { get; private set; } = new(SkillUpdatePhase.Ready, "先使用「检查更新」，再选择要安装的 Skill");
    public bool IsRunning => Volatile.Read(ref running) == 1;
    public bool IsChecking => Volatile.Read(ref running) == 2;
    public bool IsBusy => Volatile.Read(ref running) != 0;
    public UnifiedUpdateCheck? CheckResult { get; private set; }
    public bool IsSelected(string name) => selected.Contains(name);
    public bool CanInstall => !IsBusy && selected.Count > 0 && CheckResult?.Skills is { } snapshot && snapshot.IsFresh(DateTimeOffset.UtcNow);
    internal SkillUpdateController(Func<Task<SkillUpdateResult>>? execute = null, Func<CancellationToken, Task<UnifiedUpdateCheck>>? check = null)
    { this.execute = execute; this.check = check ?? (token => UnifiedUpdateCheck.CheckAsync(t => Task.Run(() => CheckInstalledAsync(t), t), token)); }
    public void Select(string name, bool value)
    {
        if (IsBusy || CheckResult?.Skills?.Items.Count(i => i.Name == name && i.Status == UpdateCheckStatus.Available) != 1) return;
        if (!(value ? selected.Add(name) : selected.Remove(name))) return;
        NotifyChanged();
    }
    public async Task CheckAsync()
    {
        if (Interlocked.CompareExchange(ref running, 2, 0) != 0) return;
        CheckResult = null; selected.Clear(); Set(new(SkillUpdatePhase.Ready, "正在检查组件和 Skills；不会安装更新…"));
        try { CheckResult = await check(CancellationToken.None).ConfigureAwait(false); Set(new(SkillUpdatePhase.Ready, CheckResult.Summary)); }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException or Win32Exception or OperationCanceledException)
        { Set(new(SkillUpdatePhase.Failed, "无法检查更新，未执行安装")); }
        finally { Interlocked.Exchange(ref running, 0); NotifyChanged(); }
    }

    public async Task RunAsync()
    {
        if (IsBusy) return;
        if (!CanInstall) { Set(new(SkillUpdatePhase.Ready, "请先通过「检查更新」选择已确认的 Skill 更新")); return; }
        var snapshot = CheckResult!.Skills!; var names = selected.ToArray();
        try { _ = snapshot.Approve(names, DateTimeOffset.UtcNow); }
        catch (InvalidDataException) { Set(new(SkillUpdatePhase.Ready, "检查结果已失效，请重新检查更新")); return; }
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0) return;
        CheckResult = null; selected.Clear();
        try
        {
            Set(new(SkillUpdatePhase.Running, "正在更新已选择的 Skills…", "只执行本次选择的固定提交；关闭设置页不会取消更新。"));
            Set(await (execute?.Invoke() ?? Task.Run(() => ExecuteInstalledAsync(snapshot, names))).ConfigureAwait(false));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
        { Set(new(SkillUpdatePhase.Failed, "未完成更新，请查看日志后重试", "更新器无法正常启动或读取结果。")); }
        finally { Interlocked.Exchange(ref running, 0); NotifyChanged(); }
    }
    void Set(SkillUpdateResult state) { State = state; NotifyChanged(); }
    void NotifyChanged()
    {
        foreach (var observer in Changed?.GetInvocationList() ?? [])
            try { ((Action)observer)(); }
            catch (Exception error) when (error is InvalidOperationException or TaskCanceledException)
            { /* A closing view cannot abort the updater transaction or prevent another view receiving state. */ }
    }

    internal static string? PowerShellPath()
    {
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new[] {
            Path.Combine(user, @".cache\codex-runtimes\codex-primary-runtime\dependencies\native\powershell\pwsh.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"PowerShell\7\pwsh.exe") };
        foreach (var candidate in candidates)
            if (File.Exists(candidate) && FileVersionInfo.GetVersionInfo(candidate).ProductName == "PowerShell"
                && NativeSignature.IsMicrosoftSigned(candidate)) return candidate;
        return null; // Never resolve a same-name executable from PATH or download a runtime.
    }
    static void CheckOrdinaryPath(string path)
    {
        for (var item = new FileInfo(path) as FileSystemInfo; item is not null; item = item is FileInfo file ? file.Directory : ((DirectoryInfo)item).Parent)
            if (item.Exists && (item.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("更新器路径包含链接，未执行。");
    }
    internal static async Task<SkillCheckSnapshot> CheckInstalledAsync(CancellationToken token)
    {
        var result = await RunScriptAsync(true, null, token).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new IOException("只读 Skill 更新检查失败。");
        return SkillCheckSnapshot.Parse(result.Output);
    }
    internal static async Task<SkillUpdateResult> ExecuteInstalledAsync(SkillCheckSnapshot snapshot, string[] names)
    {
        var approval = snapshot.Approve(names, DateTimeOffset.UtcNow);
        var temp = Path.Combine(Root, "temp"); CheckOrdinaryPath(temp); Directory.CreateDirectory(temp);
        var path = Path.Combine(temp, "approved-" + Guid.NewGuid().ToString("N") + ".json"); CheckOrdinaryPath(path);
        try
        {
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                var options = new JsonSerializerOptions(); options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
                JsonSerializer.Serialize(file, approval, options); file.Flush(true);
            }
            var result = await RunScriptAsync(false, path, CancellationToken.None).ConfigureAwait(false);
            return ParseResult(result.Output, result.ExitCode);
        }
        finally
        {
            foreach (var file in new[] { path, path + ".claim" })
            { CheckOrdinaryPath(file); if (File.Exists(file)) File.Delete(file); }
        }
    }
    static async Task<(string Output, int ExitCode)> RunScriptAsync(bool checkOnly, string? approval, CancellationToken token)
    {
        if (!File.Exists(Script) || !File.Exists(Path.Combine(Root, @"lib\Selection.ps1"))) throw new IOException("未找到支持只读检查与选择安装的更新器。");
        CheckOrdinaryPath(Script);
        CheckOrdinaryPath(Path.Combine(Root, @"lib\Selection.ps1"));
        var shell = PowerShellPath();
        if (shell is null) throw new IOException("未找到已验证的 PowerShell 7。");
        if (checkOnly == (approval is not null)) throw new InvalidOperationException("检查与安装模式无效。");
        var temp = Path.Combine(Root, "temp"); CheckOrdinaryPath(temp); Directory.CreateDirectory(temp);
        var start = new ProcessStartInfo(shell) { UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Root, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        // This host's PowerShell otherwise writes redirected Chinese output in its OEM code page.
        // The command is a fixed local entry, with no task text, user parameters or downloaded code.
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-STA", "-ExecutionPolicy", "Bypass", "-Command",
            "[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false); & $env:ACC_SKILL_UPDATER_SCRIPT -NoDialog "
                + (checkOnly ? "-CheckOnly" : "-ApprovedPlan $env:ACC_SKILL_APPROVED_PLAN") })
            start.ArgumentList.Add(argument);
        start.Environment["ACC_SKILL_UPDATER_SCRIPT"] = Script;
        if (approval is not null) start.Environment["ACC_SKILL_APPROVED_PLAN"] = approval;
        start.Environment["TEMP"] = temp; start.Environment["TMP"] = temp;
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new IOException("更新器启动失败。");
        var output = ReadBoundedAsync(process.StandardOutput, checkOnly ? 1024 * 1024 : 20000);
        var errors = ReadBoundedAsync(process.StandardError, 4000);
        // No timeout-kill: interrupting file replacement would break the updater's safety transaction.
        if (checkOnly)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(150));
            try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { if (!process.HasExited) process.Kill(); await process.WaitForExitAsync().ConfigureAwait(false); await output; await errors; throw; }
        }
        else await process.WaitForExitAsync().ConfigureAwait(false);
        var text = await output.ConfigureAwait(false); await errors.ConfigureAwait(false);
        return (text, process.ExitCode);
    }
    static async Task<string> ReadBoundedAsync(StreamReader reader, int limit)
    {
        var result = new StringBuilder(); var buffer = new char[1024]; int count;
        while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            if (result.Length < limit) result.Append(buffer, 0, Math.Min(count, limit - result.Length));
        return result.ToString();
    }
    internal static SkillUpdateResult ParseResult(string output, int exitCode)
    {
        if (output.Contains("另一次更新正在运行", StringComparison.Ordinal))
            return new(SkillUpdatePhase.Busy, "已有更新正在运行，请稍后查看日志", "桌面入口与小组件共用更新器的并发保护，未启动第二次更新。");
        var marker = output.LastIndexOf("Codex Skill 更新完成", StringComparison.Ordinal);
        if (marker < 0) return new(SkillUpdatePhase.Failed, "未取得有效结果，请查看日志", "没有把进程退出或旧日志当成更新成功。");
        var details = WorkflowText.Safe(output[marker..].Trim(), 12000);
        int Count(string label)
        {
            var match = Regex.Match(details, "(?m)^" + label + "：([0-9]{1,6})\\s*$");
            return match.Success && int.TryParse(match.Groups[1].Value, out var value) ? value : -1;
        }
        var updated = Count("成功更新"); var latest = Count("已是最新"); var skipped = Count("跳过"); var failed = Count("失败");
        if (Math.Min(Math.Min(updated, latest), Math.Min(skipped, failed)) < 0)
            return new(SkillUpdatePhase.Failed, "更新结果不完整，请查看日志", details);
        var attention = exitCode != 0 || failed != 0 || skipped != 0;
        return new(attention ? SkillUpdatePhase.Attention : SkillUpdatePhase.Completed,
            $"更新 {updated} · 最新 {latest} · 跳过 {skipped} · 失败 {failed}", details);
    }
}

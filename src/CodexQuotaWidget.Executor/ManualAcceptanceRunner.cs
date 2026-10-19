using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using CodexQuotaWidget.Core;
using CodexQuotaWidget.Core.Automation;
using CodexQuotaWidget.Core.ControlCenter;

namespace CodexQuotaWidget.Executor;

public sealed record ManualAcceptanceSelfTestResult(bool Success, int Checks, bool NoRealActions, string Message);

/// <summary>Explicit one-shot acceptance, isolated from all production quota/config/event authorization.</summary>
public sealed class ManualAcceptanceRunner
{
    readonly ILocalPlatform platform;
    readonly bool fixture;
    const string ClashGui = @"D:\CLASH\Clash Verge\clash-verge.exe";
    const string ClashServiceBinary = @"D:\CLASH\Clash Verge\resources\clash-verge-service.exe";
    static readonly LocalAction[] Actions =
    [
        new(LocalActionKind.CreateRecoverySnapshot), new(LocalActionKind.SaveResumeState),
        new(LocalActionKind.CloseApplicationGracefully, LocalTarget.ClashVerge), new(LocalActionKind.Wait, Seconds: 3),
        new(LocalActionKind.CloseProcess, LocalTarget.ClashVerge), new(LocalActionKind.VerifyProcessStopped, LocalTarget.ClashVerge),
        new(LocalActionKind.StopWindowsService, LocalTarget.ClashService), new(LocalActionKind.CloseProcess, LocalTarget.Mihomo),
        new(LocalActionKind.VerifyProcessStopped, LocalTarget.Mihomo), new(LocalActionKind.VerifyServiceStopped, LocalTarget.ClashService),
        new(LocalActionKind.RestartWindows)
    ];

    public ManualAcceptanceRunner(ILocalPlatform? fixturePlatform = null)
    {
        if (fixturePlatform is WindowsPlatform) throw new ArgumentException("真实平台只能由无参数构造器建立，不能混用 fixture");
        fixture = fixturePlatform is not null;
        platform = fixturePlatform ?? new WindowsPlatform();
    }

    public async Task<ManualAcceptanceReport> RunAsync(ManualAcceptanceRequest request, bool dryRun = true, CancellationToken cancellationToken = default)
    {
        var report = new ManualAcceptanceReport { TestId = request.TestId, StartedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, DryRun = dryRun };
        var steps = new List<ExecutorStepResult>();
        string? root = null, resultPath = null; bool accepted = false;
        FileStream? runLease = null, productionLease = null;
        void Publish()
        {
            report = report with { UpdatedAt = DateTimeOffset.UtcNow, Steps = steps.ToArray() };
            if (!accepted || resultPath is null) return;
            RequireSafe(resultPath); RequireSafe(resultPath + ".bak");
            AtomicStore.Write(resultPath, report);
        }
        void CheckCancellation()
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = ManualAcceptanceRules.CancelPath(request); RequireSafe(path);
            if (File.Exists(path)) throw new OperationCanceledException("用户已取消本次实机验收");
            if (DateTimeOffset.UtcNow >= request.ExpiresAt) throw new InvalidDataException("单次验收授权已过期，已停止");
        }
        try
        {
            if (!ManualAcceptanceRules.Validate(request, DateTimeOffset.UtcNow, !dryRun, out var validation)) throw new InvalidDataException(validation);
            if (!dryRun && fixture) throw new InvalidDataException("注入平台只支持 dry-run；不能授权实机操作");
            root = SafeRoot(request.DataRoot); var project = SafeRoot(request.ProjectRoot);
            var directory = Path.Combine(root, "manual-tests"); RequireSafe(directory); Directory.CreateDirectory(directory); RequireSafe(directory);
            var lockPath = Path.Combine(directory, "execution.lock"); RequireSafe(lockPath);
            try { runLease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { throw new InvalidDataException("已有单次验收执行器运行，拒绝并行操作"); }
            var productionRoot = Path.Combine(root, "automation"); RequireSafe(productionRoot); Directory.CreateDirectory(productionRoot);
            var productionLock = Path.Combine(productionRoot, "executor.lock"); RequireSafe(productionLock);
            try { productionLease = new FileStream(productionLock, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { throw new InvalidDataException("已有生产本地执行器运行，拒绝并行验收"); }
            resultPath = ManualAcceptanceRules.ResultPath(request); RequireSafe(resultPath); RequireSafe(resultPath + ".bak");
            if (File.Exists(resultPath)) throw new InvalidDataException("该验收结果已存在，禁止重复操作或覆盖历史结果");
            var consumedRoot = Path.Combine(directory, "consumed"); RequireSafe(consumedRoot); Directory.CreateDirectory(consumedRoot);
            var consumed = Path.Combine(consumedRoot, request.TestId.ToString("N") + ".used"); RequireSafe(consumed);
            try
            {
                using var used = new FileStream(consumed, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
                var bytes = Encoding.UTF8.GetBytes($"{ManualAcceptanceRules.Owner}\n{request.TestId:N}\n{DateTimeOffset.UtcNow:O}\n"); used.Write(bytes); used.Flush(true);
            }
            catch (IOException) { throw new InvalidDataException("该单次验收请求已消费，禁止重复操作"); }
            accepted = true;
            report = report with
            {
                Status = "Accepted", WindowsBootAt = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64),
                Message = "模拟 Codex 额度耗尽的一次性用户授权验收；不启用生产自动化，不伪造真实耗尽记录"
            };
            Publish(); CheckCancellation();
            if (!dryRun)
            {
                ValidateRealTargetsAndPrivilege();
                report = report with { Status = "WaitingForLauncherExit", Message = "独立本地执行器已接管，等待启动器退出；不会关闭 IDE、Codex 或其他软件" }; Publish();
                var launcherDeadline = DateTimeOffset.UtcNow.AddSeconds(15);
                while (!LauncherHasExited(request))
                {
                    CheckCancellation();
                    if (DateTimeOffset.UtcNow >= launcherDeadline) throw new InvalidDataException("启动器仍运行，未证实独立接管，已停止");
                    await Task.Delay(200, cancellationToken);
                }
                report = report with { LauncherExitVerified = true, Status = "Countdown", Message = "独立接管已确认；倒计时后仅关闭 Clash/mihomo，再请求 Windows 正常重启" };
                for (var remaining = request.DelaySeconds; remaining > 0; remaining--)
                {
                    CheckCancellation(); report = report with { DelayRemainingSeconds = remaining }; Publish();
                    await Task.Delay(1000, cancellationToken);
                }
                report = report with { DelayRemainingSeconds = 0 }; ValidateRealTargetsAndPrivilege();
            }
            string? inventory = null;
            for (var index = 0; index < Actions.Length; index++)
            {
                CheckCancellation(); var action = Actions[index]; var stepStart = DateTimeOffset.UtcNow;
                steps.Add(new() { Index = index, Action = action, StartedAt = stepStart });
                report = report with { Status = "Running", Message = ActionPlanParser.Describe(action) }; Publish();
                PlatformActionResult outcome;
                if (action.Kind == LocalActionKind.CreateRecoverySnapshot)
                {
                    await using var recovery = await RecoveryWriteLease.AcquireAsync(root, cancellationToken);
                    var before = await ReadInventoryAsync(project, root, cancellationToken);
                    var statePath = Path.Combine(root, "control-center", "state.json"); RequireSafe(statePath); RequireSafe(statePath + ".bak");
                    var state = AtomicStore.Read<ControlCenterState>(statePath) ?? new();
                    var snapshot = new ControlCenterSnapshot { Tasks = state.Tasks, Policy = state.Policy, DispatchPaused = true, SafeStopRequested = true };
                    var saved = await new SafeFinishCheckpoint(project, root).CreateAsync(snapshot, cancellationToken);
                    if (!saved.Success) throw new InvalidDataException("恢复快照未保存：" + saved.Message);
                    var manifest = await ExecutorEngine.VerifyCheckpointAsync(project, root, true, cancellationToken);
                    inventory = await ReadInventoryAsync(project, root, cancellationToken);
                    if (before != inventory) throw new InvalidDataException("保存期间源码或 Git 清单改变，已停止实机操作");
                    report = report with { SnapshotSha256 = manifest.SourceSnapshotSha256, SourceInventoryFingerprint = HashText(inventory) };
                    outcome = new(true, "源文件、恢复快照、Git patch、manifest 及摘要 SHA 校验通过", [saved.SourceSnapshotPath!]);
                }
                else if (action.Kind == LocalActionKind.SaveResumeState)
                {
                    var note = Path.Combine(directory, "results", request.TestId.ToString("N") + "-resume.md"); RequireSafe(note);
                    var content = $"# 单次实机验收恢复记录\n\nTestId：{request.TestId:N}\n时间：{DateTimeOffset.UtcNow:O}\n项目：{project}\n触发为模拟额度耗尽，不是真实额度事件。\n只授权 Clash/mihomo 关闭及 Windows 正常重启；无 /f，不关闭开发工具。\n完整恢复记录：{Path.Combine(root, "codex_resume_state.md")}\n快照：{Path.Combine(root, "resume", "latest", "source.zip")}\n恢复后先读取本测试 JSON，确认实际重启，再继续编译测试。\n";
                    Directory.CreateDirectory(Path.GetDirectoryName(note)!); await File.WriteAllTextAsync(note, content, new UTF8Encoding(false), cancellationToken);
                    report = report with { RecoveryNotePath = note };
                    outcome = new(true, "单次验收恢复记录已写入 D 盘", [note]);
                }
                else if (action.Kind == LocalActionKind.RestartWindows)
                {
                    await using var recovery = await RecoveryWriteLease.AcquireAsync(root, cancellationToken);
                    await ExecutorEngine.VerifyCheckpointAsync(project, root, true, cancellationToken);
                    if (inventory is null || inventory != await ReadInventoryAsync(project, root, cancellationToken))
                        throw new InvalidDataException("保存后的项目源文件/Git 清单新增或变化，已阻止重启");
                    CheckCancellation();
                    if (!dryRun)
                    {
                        if (!LauncherHasExited(request)) throw new InvalidDataException("启动器退出证据失效，已阻止重启");
                        ValidateRealTargetsAndPrivilege();
                        foreach (var finalCheck in new[]
                        {
                            new LocalAction(LocalActionKind.VerifyProcessStopped, LocalTarget.ClashVerge),
                            new LocalAction(LocalActionKind.VerifyProcessStopped, LocalTarget.Mihomo),
                            new LocalAction(LocalActionKind.VerifyServiceStopped, LocalTarget.ClashService)
                        })
                        {
                            var verification = await platform.PerformAsync(finalCheck, false, cancellationToken);
                            if (!verification.Success || verification.ServiceAccessDenied)
                                throw new InvalidDataException("重启前 Clash/mihomo 关闭状态复验失败，已阻止重启");
                        }
                        CheckCancellation();
                        report = report with { Status = "RestartPending", RestartAttemptedAt = DateTimeOffset.UtcNow, Message = "源码校验通过；即将请求 Windows 正常重启，尚未确认实际重启" }; Publish();
                    }
                    outcome = await platform.PerformAsync(action, dryRun, cancellationToken);
                    if (!dryRun && outcome.Success)
                    {
                        report = report with { RestartRequested = true, RestartAcceptedAt = DateTimeOffset.UtcNow, Status = "RestartAccepted", Message = "Windows 已接受正常重启请求；实际重启需下次开机核验" };
                    }
                }
                else
                {
                    // Before the first actual process action, verify saving did not race with editing.
                    if (action.Kind == LocalActionKind.CloseApplicationGracefully)
                    {
                        await ExecutorEngine.VerifyCheckpointAsync(project, root, true, cancellationToken);
                        if (inventory != await ReadInventoryAsync(project, root, cancellationToken)) throw new InvalidDataException("源码在保存后变化，已停止关闭程序");
                    }
                    outcome = await platform.PerformAsync(action, dryRun, cancellationToken);
                }
                if (outcome.Targets?.Any(target => target.EndsWith(";UnverifiedOrigin", StringComparison.Ordinal)
                    || target.EndsWith(";UnverifiedMetadata", StringComparison.Ordinal)) == true)
                    outcome = outcome with { Success = false, Message = "存在同名但来源未确认的目标，验收不能确认安全关闭；已停止" };
                if (outcome.ServiceAccessDenied) outcome = outcome with { Success = false, Message = "Clash/mihomo 尚未真实停止或服务权限不足；实机验收禁止交由重启代为停止" };
                steps[^1] = new()
                {
                    Index = index, Action = action, StartedAt = stepStart, FinishedAt = DateTimeOffset.UtcNow,
                    Success = outcome.Success, Simulated = dryRun && action.Kind is not (LocalActionKind.CreateRecoverySnapshot or LocalActionKind.SaveResumeState),
                    Message = outcome.Message, Targets = outcome.Targets ?? Array.Empty<string>()
                };
                Publish();
                if (!outcome.Success) throw new InvalidDataException(outcome.Message);
            }
            report = report with
            {
                Completed = true, Success = true, Status = dryRun ? "DryRunPassed" : "RestartAccepted",
                Message = dryRun ? "模拟验收通过；没有关闭真实进程/服务，也没有重启" : "关闭与保存步骤通过，正常重启请求已接受；实际重启尚待下次开机核验"
            };
            Publish(); return report;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException or OperationCanceledException or InvalidOperationException or Win32Exception or TimeoutException)
        {
            var message = error is InvalidDataException or OperationCanceledException ? error.Message : "单次验收停止：" + error.GetType().Name;
            if (steps.Count > 0 && steps[^1].FinishedAt == default) steps[^1] = steps[^1] with { FinishedAt = DateTimeOffset.UtcNow, Success = false, Message = message };
            report = report with { Completed = true, Success = false, Status = error is OperationCanceledException ? "Cancelled" : "Failed", Message = message, Steps = steps.ToArray() };
            try { Publish(); } catch (Exception storageError) when (storageError is IOException or UnauthorizedAccessException or InvalidDataException) { }
            return report;
        }
        finally { productionLease?.Dispose(); runLease?.Dispose(); }
    }

    static string SafeRoot(string path) { RequireSafe(path); return Path.GetFullPath(path).TrimEnd('\\'); }
    static void RequireSafe(string path)
    { if (!AutomationStore.IsSafeDPath(path)) throw new InvalidDataException("单次验收拒绝非 D 盘路径、目录联接或符号链接"); }
    static string HashText(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    static bool LauncherHasExited(ManualAcceptanceRequest request)
    {
        if (request.LauncherProcessId == Environment.ProcessId) return false;
        try
        {
            using var launcher = Process.GetProcessById(request.LauncherProcessId);
            var started = new DateTimeOffset(launcher.StartTime.ToUniversalTime());
            // A recycled PID belongs to another process. The original authorized launcher already exited.
            return Math.Abs((started - request.LauncherStartedAt).TotalMilliseconds) > 1 || launcher.HasExited;
        }
        catch (ArgumentException) { return true; }
        catch (InvalidOperationException) { return true; }
        catch (Win32Exception) { throw new InvalidDataException("启动器退出状态无法核验，已停止实机操作"); }
    }

    static void ValidateRealTargetsAndPrivilege()
    {
        RequireSafe(ClashGui); RequireSafe(ClashServiceBinary);
        var scm = OpenSCManager(null, null, 1);
        if (scm == IntPtr.Zero) throw new InvalidDataException("无法核验服务管理器");
        try
        {
            var service = OpenService(scm, "clash_verge_service", 1 | 4);
            if (service == IntPtr.Zero)
            {
                if (Marshal.GetLastWin32Error() == 1060) return;
                throw new InvalidDataException("无法核验 Clash 服务来源");
            }
            try
            {
                QueryServiceConfig(service, IntPtr.Zero, 0, out var required);
                if (required is 0 or >65536) throw new InvalidDataException("Clash 服务配置无效");
                var buffer = Marshal.AllocHGlobal((int)required);
                try
                {
                    if (!QueryServiceConfig(service, buffer, required, out _)) throw new InvalidDataException("Clash 服务安装路径无法确认");
                    var config = Marshal.PtrToStructure<ServiceConfig>(buffer);
                    var path = (Marshal.PtrToStringUni(config.BinaryPathName) ?? "").Trim();
                    if (path.StartsWith('"') && path.EndsWith('"')) path = path[1..^1];
                    if (!path.Equals(ClashServiceBinary, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) throw new InvalidDataException("Clash 服务来源路径不匹配，拒绝实机操作");
                    RequireSafe(path);
                }
                finally { Marshal.FreeHGlobal(buffer); }
                if (!QueryServiceStatusEx(service, 0, out var status, Marshal.SizeOf<ServiceStatusProcess>(), out _)) throw new InvalidDataException("Clash 服务状态未知");
                if (status.CurrentState != 1)
                {
                    using var identity = WindowsIdentity.GetCurrent();
                    if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) throw new InvalidDataException("运行中的 Clash/mihomo 服务需要本次 Executor 获得管理员权限；未关闭程序或请求重启");
                }
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(scm); }
    }

    static async Task<string> ReadInventoryAsync(string project, string data, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("git.exe") { WorkingDirectory = project, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "--no-pager", "-c", "core.fsmonitor=false", "-c", "core.untrackedCache=false", "ls-files", "-z", "--cached", "--others", "--exclude-standard" }) start.ArgumentList.Add(arg);
        var temp = Path.Combine(data, "temp"); RequireSafe(temp); Directory.CreateDirectory(temp);
        start.Environment["TEMP"] = temp; start.Environment["TMP"] = temp; start.Environment["GIT_OPTIONAL_LOCKS"] = "0"; start.Environment.Remove("GIT_EXTERNAL_DIFF");
        using var process = Process.Start(start) ?? throw new InvalidDataException("Git 源文件校验未启动");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken); var error = process.StandardError.ReadToEndAsync(cancellationToken);
        try { await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(20), cancellationToken); }
        catch { if (!process.HasExited) process.Kill(); throw; }
        var text = await output; _ = await error;
        if (process.ExitCode != 0 || text.Length > 2 * 1024 * 1024) throw new InvalidDataException("Git 源文件清单校验失败");
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".git", "bin", "obj", "artifacts", "publish", "node_modules", ".vs", ".idea", "auth", "credentials", "secrets", "logs", "cache", "backups" };
        var binary = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".exe", ".dll", ".pdb", ".msi", ".nupkg", ".dmp", ".db", ".sqlite", ".sqlite3", ".pem", ".pfx", ".p12", ".key" };
        var paths = text.Split('\0', StringSplitOptions.RemoveEmptyEntries).Where(path => !path.Split('/', '\\').Any(excluded.Contains)
            && !Path.GetFileName(path).StartsWith(".env", StringComparison.OrdinalIgnoreCase) && Path.GetFileName(path) is not ("auth.json" or "credentials.json" or "secrets.json" or ".npmrc" or ".pypirc")
            && !binary.Contains(Path.GetExtension(path))).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length > 5000) throw new InvalidDataException("源文件清单超出上限");
        foreach (var path in paths)
        {
            if (path.Contains(':')) throw new InvalidDataException("源文件路径异常");
            ExecutorPaths.Source(project, path);
        }
        return string.Join("\n", paths);
    }

    /// <summary>Only isolated local Git fixtures and injected dry-run platforms; never a Windows close/power call.</summary>
    public static async Task<ManualAcceptanceSelfTestResult> RunChecksAsync(string fixtureRoot)
    {
        var root = SafeRoot(fixtureRoot);
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new InvalidDataException("验收自检 fixture 必须为空，避免覆盖用户资料");
        var project = Path.Combine(root, "project"); var data = Path.Combine(root, "data");
        Directory.CreateDirectory(project); Directory.CreateDirectory(data);
        var source = Path.Combine(project, "Program.cs"); File.WriteAllText(source, "// manual acceptance fixture\n", new UTF8Encoding(false));
        async Task Git(params string[] arguments)
        {
            var start = new ProcessStartInfo("git.exe") { WorkingDirectory = project, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in arguments) start.ArgumentList.Add(arg);
            start.Environment["TEMP"] = root; start.Environment["TMP"] = root; start.Environment["GIT_CONFIG_NOSYSTEM"] = "1"; start.Environment["GIT_CONFIG_GLOBAL"] = "NUL";
            using var process = Process.Start(start) ?? throw new InvalidDataException("验收 fixture Git 未启动");
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
            catch { if (!process.HasExited) process.Kill(); throw; }
            _ = await output; _ = await error;
            if (process.ExitCode != 0) throw new InvalidDataException("验收 fixture Git 失败");
        }
        await Git("init", "--initial-branch=main"); await Git("add", "Program.cs");
        await Git("-c", "user.name=ManualAcceptanceFixture", "-c", "user.email=fixture@invalid.local", "-c", "commit.gpgsign=false", "commit", "--no-verify", "-m", "fixture");
        ManualAcceptanceRequest NewRequest()
        {
            var now = DateTimeOffset.UtcNow; var reset = now.AddHours(1).ToUnixTimeSeconds(); var detector = new ExhaustionDetector();
            QuotaSnapshot Sample(double remaining, int seconds) => new(now.AddSeconds(seconds), "fixture",
                [new("codex", "Codex", "primary", 100 - remaining, 300, reset)], false, null, []);
            detector.Observe(Sample(5, -3), MonitorStatus.Live, false, now);
            detector.Observe(Sample(0, -2), MonitorStatus.Live, false, now);
            var zero = detector.Observe(Sample(0, -1), MonitorStatus.Live, false, now)
                ?? throw new InvalidDataException("真实 detector 的模拟 5→0→0 未触发");
            return new()
            {
                DataRoot = data, ProjectRoot = project, Authorization = ManualAcceptanceRules.Authorization,
                CreatedAt = now, ExpiresAt = now.AddMinutes(14), SimulatedQuotaReceipt = zero with { Synthetic = true }
            };
        }
        var checks = 0;
        void Check(bool value, string reason) { if (!value) throw new InvalidDataException("单次验收自检失败：" + reason); checks++; }
        var protectedNames = new[] { "automation-config.json", "automation-pending.json", "automation-handled-cycles.json" };
        foreach (var name in protectedNames) File.WriteAllText(Path.Combine(data, name), "unmodified-production-fixture");
        var request = NewRequest(); var fake = new ManualFixturePlatform(); var runner = new ManualAcceptanceRunner(fake);
        var result = await runner.RunAsync(request);
        Check(result.Success && result.Completed && result.DryRun && !result.RestartRequested && !result.ActualRestartVerified, "dry-run 到达最后重启模拟但不请求真实电源");
        Check(result.Steps.Count == Actions.Length && result.Steps.Last().Action.Kind == LocalActionKind.RestartWindows && result.Steps.Last().Simulated, "固定步骤完整通过");
        Check(result.Steps.All(step => step.Action.Target is LocalTarget.None or LocalTarget.ClashVerge or LocalTarget.Mihomo or LocalTarget.ClashService), "不包含 Codex/IDE/其他目标");
        Check(fake.AllCallsDryRun && fake.PowerReached && File.Exists(result.RecoveryNotePath), "仅 dry-run平台，恢复记录真实保存");
        Check(protectedNames.All(name => File.ReadAllText(Path.Combine(data, name)) == "unmodified-production-fixture")
            && !Directory.Exists(Path.Combine(data, "events")), "生产配置/凭据/周期/事件未改写");
        var originalResult = File.ReadAllBytes(ManualAcceptanceRules.ResultPath(request));
        var duplicate = await runner.RunAsync(request);
        Check(!duplicate.Success && originalResult.SequenceEqual(File.ReadAllBytes(ManualAcceptanceRules.ResultPath(request))), "同 TestId 拒重复且保留首次结果");
        Check(!(await runner.RunAsync(NewRequest() with { RealActionsAuthorized = false }, false)).Success, "真实动作无授权拒绝");
        using (var own = Process.GetCurrentProcess())
        {
            var injectedReal = await runner.RunAsync(NewRequest() with
            {
                RealActionsAuthorized = true, LauncherProcessId = own.Id,
                LauncherStartedAt = new DateTimeOffset(own.StartTime.ToUniversalTime())
            }, false);
            Check(!injectedReal.Success && injectedReal.Steps.Count == 0, "注入平台不能进入真实分支");
        }
        Check(!(await runner.RunAsync(NewRequest() with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) })).Success, "过期拒绝");
        Check(!(await runner.RunAsync(NewRequest() with { SimulatedQuotaReceipt = null })).Success, "仅贴模拟标签不能替代 detector 样本");
        Check(!(await runner.RunAsync(NewRequest() with { SimulatedQuotaReceipt = request.SimulatedQuotaReceipt! with { Synthetic = false } })).Success, "真实凭据不能混入显式模拟测试");
        Check(!(await runner.RunAsync(NewRequest() with { Authorization = "other" })).Success, "未知授权拒绝");
        Check(!(await runner.RunAsync(NewRequest() with { DataRoot = @"C:\temp" })).Success, "C 盘路径拒绝");
        var changedFake = new ManualFixturePlatform(() => File.AppendAllText(source, "// edited after saving\n"));
        var changed = await new ManualAcceptanceRunner(changedFake).RunAsync(NewRequest());
        Check(!changed.Success && !changedFake.PowerReached, "已有源码改动阻止重启");
        var addedFake = new ManualFixturePlatform(() => File.WriteAllText(Path.Combine(project, "NewFile.cs"), "// new source\n"));
        var added = await new ManualAcceptanceRunner(addedFake).RunAsync(NewRequest());
        Check(!added.Success && !addedFake.PowerReached, "新增源码阻止重启");
        var serviceFake = new ManualFixturePlatform(serviceDenied: true);
        var protectedResult = await new ManualAcceptanceRunner(serviceFake).RunAsync(NewRequest());
        Check(!protectedResult.Success && !serviceFake.PowerReached, "受保护服务不能冒充已停止");
        var unknownFake = new ManualFixturePlatform(unknownTarget: true);
        var unknownResult = await new ManualAcceptanceRunner(unknownFake).RunAsync(NewRequest());
        Check(!unknownResult.Success && !unknownFake.PowerReached, "未知同名目标阻止操作");
        var cancelled = NewRequest(); File.WriteAllText(ManualAcceptanceRules.CancelPath(cancelled), "cancel");
        var cancelledResult = await runner.RunAsync(cancelled);
        Check(!cancelledResult.Success && cancelledResult.Status == "Cancelled" && cancelledResult.Steps.Count == 0, "取消文件阻止全部操作");
        var blocked = NewRequest(); var existingLock = Path.Combine(data, "automation", "executor.lock");
        await using (var busy = new FileStream(existingLock, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var blockedResult = await runner.RunAsync(blocked);
            Check(!blockedResult.Success && blockedResult.Steps.Count == 0, "生产执行器锁阻止并行验收");
        }
        return new(true, checks, true, "单次验收所有检查通过；未关闭真实软件、服务或请求电源操作");
    }

    sealed class ManualFixturePlatform(Action? sourceMutation = null, bool serviceDenied = false, bool unknownTarget = false) : ILocalPlatform
    {
        bool mutated;
        public bool PowerReached { get; private set; }
        public bool AllCallsDryRun { get; private set; } = true;
        public Task<PlatformActionResult> PerformAsync(LocalAction action, bool dryRun, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); AllCallsDryRun &= dryRun;
            if (!dryRun) throw new InvalidDataException("fixture绝不支持真实动作");
            if (action.Kind == LocalActionKind.CloseProcess && !mutated) { mutated = true; sourceMutation?.Invoke(); }
            if (unknownTarget && action.Kind == LocalActionKind.CloseApplicationGracefully)
                return Task.FromResult(new PlatformActionResult(false, "fixture：目标来源未知"));
            if (serviceDenied && action.Kind == LocalActionKind.StopWindowsService)
                return Task.FromResult(new PlatformActionResult(true, "fixture：服务未停止", ServiceAccessDenied: true));
            if (action.Kind == LocalActionKind.RestartWindows) PowerReached = true;
            return Task.FromResult(new PlatformActionResult(true, "fixture：仅模拟本地动作"));
        }
    }

    [StructLayout(LayoutKind.Sequential)] struct ServiceStatusProcess { public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint, ProcessId, ServiceFlags; }
    [StructLayout(LayoutKind.Sequential)] struct ServiceConfig { public uint ServiceType, StartType, ErrorControl; public IntPtr BinaryPathName, LoadOrderGroup; public uint TagId; public IntPtr Dependencies, ServiceStartName, DisplayName; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr OpenService(IntPtr scm, string name, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool QueryServiceConfig(IntPtr service, IntPtr config, uint size, out uint needed);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool QueryServiceStatusEx(IntPtr service, int level, out ServiceStatusProcess status, int size, out int needed);
    [DllImport("advapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool CloseServiceHandle(IntPtr handle);
}

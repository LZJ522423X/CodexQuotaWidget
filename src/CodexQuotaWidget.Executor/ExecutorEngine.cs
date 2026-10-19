using System.Diagnostics;
using System.ComponentModel;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexQuotaWidget.Core;
using CodexQuotaWidget.Core.Automation;
using CodexQuotaWidget.Core.ControlCenter;
using CodexQuotaWidget.Core.Observation;

namespace CodexQuotaWidget.Executor;

public sealed class ExecutorEngine
{
    readonly ILocalPlatform platform;
    readonly Func<DateTimeOffset> clock;
    readonly Func<CancellationToken, Task<CodexStatusObservation>>? injectedStatusReader;
    public ExecutorEngine(ILocalPlatform? platform = null, Func<DateTimeOffset>? clock = null,
        Func<CancellationToken, Task<CodexStatusObservation>>? statusReader = null)
    {
        if (statusReader is not null && (platform is null || platform is WindowsPlatform))
            throw new ArgumentException("真实 Windows 平台禁止注入任务状态；必须读取本机真实会话", nameof(statusReader));
        this.platform = platform ?? new WindowsPlatform(); this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        injectedStatusReader = statusReader;
    }

    public async Task<ExecutorRunResult> RunAsync(ExecutorRequest request, bool dryRun = true, CancellationToken cancellationToken = default)
    {
        var start = clock(); var steps = new List<ExecutorStepResult>();
        string? root = null, historyPath = null; bool power = false, accepted = false, evidenceVerified = false;
        FileStream? executionLease = null;
        SafeFinishManifest? manifest = null;
        string? criticalInventory = null;
        ExecutorRunResult Result(bool success, string message) => new()
        {
            RequestId = request.RequestId, Completed = true, Success = success, DryRun = dryRun,
            PowerRequested = power, Message = message, StartedAt = start, FinishedAt = clock(), Steps = steps.ToArray(), HistoryPath = historyPath,
            TriggerReason = !evidenceVerified ? "执行请求或耗尽授权未通过验证" : dryRun ? "模拟额度与本地操作；没有授权真实程序关闭或电源动作" : "实时 Codex 五小时耗尽与真实任务停止双重确认；独立本地收尾",
            FiveHourRemaining = evidenceVerified ? request.Receipt?.RemainingPercent : null,
            ExhaustionCycle = evidenceVerified ? request.Receipt?.CycleKey : null, ExhaustionEventId = evidenceVerified ? request.Receipt?.EventId : null
        };
        void Publish(ExecutorRunResult result)
        {
            if (root is null) return;
            if (!accepted)
            {
                var resultPath = Path.Combine(root, "automation", "results", $"{request.RequestId:N}.json");
                ExecutorPaths.NoReparse(resultPath); ExecutorPaths.NoReparse(resultPath + ".bak"); AtomicStore.Write(resultPath, result);
                return;
            }
            historyPath ??= Path.Combine(root, "logs", $"executor-history-{start:yyyyMMdd-HHmmss}-{request.RequestId:N}.json");
            var output = result with { HistoryPath = historyPath };
            foreach (var path in new[] { historyPath, Path.Combine(root, "automation", "last-run.json"), Path.Combine(root, "automation", "results", $"{request.RequestId:N}.json") })
            { ExecutorPaths.NoReparse(path); ExecutorPaths.NoReparse(path + ".bak"); AtomicStore.Write(path, output); }
            PruneOwnedHistory(root);
        }
        try
        {
            root = ExecutorPaths.OnD(request.DataRoot);
            var project = ExecutorPaths.OnD(request.ProjectRoot);
            ValidateRequest(request, root, project, dryRun, start);
            evidenceVerified = true;
            Func<CancellationToken, Task<CodexStatusObservation>>? statusReader = null;
            if (!dryRun)
            {
                if (injectedStatusReader is not null) statusReader = injectedStatusReader;
                else
                {
                    var selected = new CodexSessionBindingStore(root).Load();
                    var original = request.Receipt?.ExhaustedEvent?.TaskStatus;
                    if (!selected.Success || !selected.Binding.IsBound || original is null || original.BindingId == Guid.Empty
                        || selected.Binding.BindingId != original.BindingId || selected.Binding.SessionId != original.SessionId
                        || !string.Equals(selected.Binding.SessionFilePath,original.SessionFilePath,StringComparison.OrdinalIgnoreCase)
                        || !StatusEvidence.SameProject(selected.Binding.ProjectRoot, project))
                        throw new InvalidDataException("明确 Session 绑定已变化或缺失；禁止沿用旧项目观察授权");
                    var observer = new BoundCodexStatusObserver(selected.Binding);
                    statusReader = token => CodexBoundObservationGuard.ReadAsync(new(root),selected.Binding,observer.ReadAsync,token);
                }
                await VerifyCurrentTaskStoppedAsync(request, root, project, statusReader, clock, cancellationToken);
            }
            var directory = Path.Combine(root, "automation"); Directory.CreateDirectory(directory); ExecutorPaths.NoReparse(directory);
            var lockPath = Path.Combine(directory, "executor.lock"); ExecutorPaths.NoReparse(lockPath);
            try { executionLease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { var busy = Result(false, "已有本地执行器运行；不重复执行"); Publish(busy); return busy; }
            ConsumeRequest(root, request, dryRun);
            accepted = true;
            Publish(Result(false, "本地执行器已接管；无需 Codex、ChatGPT 或网络") with { Completed = false });
            for (var index = 0; index < request.Plan.Actions.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var action = request.Plan.Actions[index]; var stepStart = clock();
                steps.Add(new()
                {
                    Index = index, Action = action, StartedAt = stepStart,
                    Simulated = dryRun && action.Kind is not (LocalActionKind.CreateRecoverySnapshot or LocalActionKind.SaveResumeState),
                    Message = "开始：" + ActionPlanParser.Describe(action)
                });
                Publish(Result(false, "开始：" + ActionPlanParser.Describe(action)) with { Completed = false });
                if (!dryRun && action.Kind is LocalActionKind.CloseApplicationGracefully or LocalActionKind.CloseProcess or LocalActionKind.StopWindowsService)
                    await VerifyCurrentTaskStoppedAsync(request, root, project, statusReader!, clock, cancellationToken);
                PlatformActionResult outcome;
                var powerStep = IsPower(action.Kind);
                if (action.Kind == LocalActionKind.CreateRecoverySnapshot)
                {
                    await using var recoveryLease = await RecoveryWriteLease.AcquireAsync(root, cancellationToken);
                    var state = AtomicStore.Read<ControlCenterState>(Path.Combine(root, "control-center", "state.json")) ?? new();
                    var snapshot = new ControlCenterSnapshot { Tasks = state.Tasks, Policy = state.Policy, DispatchPaused = true, SafeStopRequested = true };
                    var saved = await new SafeFinishCheckpoint(project, root, clock).CreateAsync(snapshot, cancellationToken);
                    if (!saved.Success) outcome = new(false, "恢复快照保存失败：" + saved.Message);
                    else
                    {
                        manifest = await VerifyCheckpointAsync(project, root, false, cancellationToken);
                        criticalInventory = await ReadCriticalInventoryAsync(project, root, cancellationToken);
                        outcome = new(true, "完整源文件、Git patch、manifest 及恢复记录已保存并验证", [saved.SourceSnapshotPath!]);
                    }
                }
                else if (action.Kind == LocalActionKind.SaveResumeState)
                {
                    var note = Path.Combine(root, "codex_resume_state.md"); ExecutorPaths.NoReparse(note);
                    outcome = File.Exists(note) && new FileInfo(note).Length is >0 and < 1024 * 1024
                        ? new(true, "恢复记录存在，且完整快照中的 resume.md 哈希有效", [note]) : new(false, "恢复记录不存在或无效");
                }
                else if (powerStep)
                {
                    if (manifest is null || criticalInventory is null) throw new InvalidDataException("未完成恢复快照，禁止电源操作");
                    await using var recoveryLease = await RecoveryWriteLease.AcquireAsync(root, cancellationToken);
                    await VerifyCheckpointAsync(project, root, true, cancellationToken);
                    if (criticalInventory != await ReadCriticalInventoryAsync(project, root, cancellationToken))
                        throw new InvalidDataException("关键源文件清单发生变化；已保存恢复点并阻止电源操作");
                    // Hashing/Git validation may take time: observe again afterwards, immediately before power.
                    if (!dryRun)
                        await VerifyCurrentTaskStoppedAsync(request, root, project, statusReader!, clock, cancellationToken);
                    Publish(Result(false, dryRun ? "即将模拟电源步骤" : "恢复验证通过，即将请求 Windows 电源操作") with { Completed = false });
                    outcome = await platform.PerformAsync(action, dryRun, cancellationToken);
                    power = !dryRun && outcome.Success;
                }
                else if (request.SimulateAiUnavailable && action.Target is LocalTarget.CodexDesktop or LocalTarget.CodexCli)
                    outcome = new(true, "测试 fixture：Codex Desktop/CLI 不存在，ChatGPT 不可用；本地执行不需要 AI", [action.Target + ":AbsentFixture"]);
                else outcome = await platform.PerformAsync(action, dryRun, cancellationToken);
                if (!dryRun && outcome.ServiceAccessDenied && request.Plan.Actions.Last().Kind is not (LocalActionKind.ShutdownWindows or LocalActionKind.RestartWindows))
                    outcome = outcome with { Success = false, Message = "受保护服务未停止；当前计划没有正常关机/重启可交由 SCM 收尾，已暂停" };
                steps[^1] = new()
                {
                    Index = index, Action = action, StartedAt = stepStart, FinishedAt = clock(),
                    Success = outcome.Success, Simulated = dryRun && action.Kind is not (LocalActionKind.CreateRecoverySnapshot or LocalActionKind.SaveResumeState),
                    Message = outcome.Message, Targets = outcome.Targets ?? Array.Empty<string>()
                };
                Publish(Result(false, outcome.Message) with { Completed = false });
                if (!outcome.Success)
                { var failed = Result(false, "安全收尾停止：" + outcome.Message); Publish(failed); return failed with { HistoryPath = historyPath }; }
            }
            var finished = Result(true, dryRun ? "测试通过；未关闭任何程序，未执行关机/重启/睡眠" : "本地白名单操作执行完成");
            Publish(finished); return finished with { HistoryPath = historyPath };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException or OperationCanceledException or TimeoutException or InvalidOperationException or Win32Exception)
        {
            var reason = e is InvalidDataException ? e.Message : "本地安全收尾失败：" + e.GetType().Name;
            if (steps.Count > 0 && steps[^1].FinishedAt == default)
                steps[^1] = steps[^1] with { FinishedAt = clock(), Success = false, Message = reason };
            var failed = Result(false, reason);
            try { Publish(failed); } catch (Exception writeError) when (writeError is IOException or UnauthorizedAccessException or InvalidDataException) { }
            return failed with { HistoryPath = historyPath };
        }
        finally { executionLease?.Dispose(); }
    }

    static async Task VerifyCurrentTaskStoppedAsync(ExecutorRequest request, string root, string project,
        Func<CancellationToken, Task<CodexStatusObservation>> reader, Func<DateTimeOffset> clock, CancellationToken cancellationToken)
    {
        var latest = await reader(cancellationToken);
        var now = clock();
        var original = request.Receipt?.ExhaustedEvent?.TaskStatus;
        if (original is null || !string.Equals(latest.SessionId, original.SessionId, StringComparison.Ordinal)
            || !string.Equals(latest.TurnId, original.TurnId, StringComparison.Ordinal)
            || latest.BindingId != original.BindingId || !latest.ProjectAssociationVerified
            || CodexExhaustedEventFactory.Confirm(request.Receipt, latest, now, project, root) is null)
            throw new InvalidDataException("真实任务状态已变化、未知或证据过期；禁止关闭程序、服务及电源操作");
    }

    static void ValidateRequest(ExecutorRequest request, string root, string project, bool dryRun, DateTimeOffset now)
    {
        if (request.SchemaVersion != 1 || request.RequestId == Guid.Empty || request.CreatedAt > now || now - request.CreatedAt > TimeSpan.FromMinutes(15))
            throw new InvalidDataException("执行请求无效或已过期");
        if (!Directory.Exists(project) || ExecutorPaths.Within(project, root) || project.Equals(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("恢复项目路径无效");
        var validation = ActionPlanParser.Validate(request.Plan);
        if (!validation.Success) throw new InvalidDataException(validation.Message);
        if (!ExhaustionDetector.ValidateReceipt(request.Receipt, now)) throw new InvalidDataException("缺少可信、有效的真实耗尽事件");
        if (request.Receipt!.PlanFingerprint != ActionPlanParser.Fingerprint(request.Plan)) throw new InvalidDataException("耗尽事件与保存的计划不一致");
        if (request.SimulateAiUnavailable && (!request.TestOnly || !request.Receipt.Synthetic)) throw new InvalidDataException("AI 不可用 fixture 仅限模拟凭据和测试请求");
        if (dryRun) return;
        if (request.TestOnly || request.Receipt.Synthetic || request.SimulateAiUnavailable) throw new InvalidDataException("模拟请求禁止正式执行");
        if (!CodexExhaustedEventFactory.Validate(request.Receipt, now, project))
            throw new InvalidDataException("缺少匹配项目的新鲜真实任务停止证据；零额度本身不授权收尾");
        var store = new AutomationStore(root);
        var saved = store.LoadConfiguration();
        var pending = store.ReadPendingReceipt();
        if (!saved.Success || !saved.Configuration.Enabled || saved.Configuration.Plan is null
            || ActionPlanParser.Fingerprint(saved.Configuration.Plan) != request.Receipt.PlanFingerprint
            || !Path.GetFullPath(saved.Configuration.ProjectRoot).Equals(project, StringComparison.OrdinalIgnoreCase)
            || !CodexExhaustedEventFactory.EqualReceiptEvidence(pending, request.Receipt)
            || !store.LoadHandledCycles().Contains(request.Receipt.CycleKey, StringComparer.Ordinal))
            throw new InvalidDataException("请求未获得本机保存的计划授权与单次耗尽凭据");
    }

    static void ConsumeRequest(string root, ExecutorRequest request, bool dryRun)
    {
        var path = Path.Combine(root, "automation", "executor-receipts.json"); ExecutorPaths.NoReparse(path); ExecutorPaths.NoReparse(path + ".bak");
        var key = dryRun ? "test:" + request.RequestId.ToString("N") : "event:" + request.Receipt!.EventId.ToString("N");
        List<string> consumed = [];
        if (File.Exists(path))
        {
            if (new FileInfo(path).Length > 65536) throw new InvalidDataException("执行去重记录异常；禁止自动执行");
            consumed = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path)) ?? throw new InvalidDataException("执行去重主记录为空");
            if (consumed.Count > 128 || consumed.Any(value => !ValidConsumedKey(value)))
                throw new InvalidDataException("执行去重记录损坏；禁止自动执行");
        }
        if (consumed.Contains(key, StringComparer.Ordinal)) throw new InvalidDataException("该请求已执行，禁止重复收尾");
        consumed.Add(key); if (consumed.Count > 128) consumed.RemoveRange(0, consumed.Count - 128);
        AtomicStore.Write(path, consumed);
    }

    static bool ValidConsumedKey(string? value)
    {
        if (value is null) return false;
        var parts = value.Split(':');
        return parts.Length == 2 && (parts[0] is "test" or "event") && Guid.TryParseExact(parts[1], "N", out _);
    }

    public static async Task<SafeFinishManifest> VerifyCheckpointAsync(string project, string root, bool verifyCurrentSource, CancellationToken cancellationToken = default)
    {
        var latest = Path.Combine(root, "resume", "latest"); ExecutorPaths.NoReparse(latest);
        var path = Path.Combine(latest, "manifest.json"); ExecutorPaths.NoReparse(path);
        var manifest = JsonSerializer.Deserialize<SafeFinishManifest>(await File.ReadAllTextAsync(path, cancellationToken)) ?? throw new InvalidDataException("恢复 manifest 无效");
        if (manifest.Owner != "CodexQuotaWidget.SafeFinishCheckpoint" || manifest.SchemaVersion != 1
            || string.IsNullOrWhiteSpace(manifest.ProjectRoot) || !Path.GetFullPath(manifest.ProjectRoot).Equals(project, StringComparison.OrdinalIgnoreCase)
            || manifest.SourceFiles is null || manifest.SourceFiles.Count is 0 or >5000)
            throw new InvalidDataException("恢复 manifest 来源或文件列表无效");
        var archivePath = Path.Combine(latest, "source.zip"); var patchPath = Path.Combine(latest, "working-tree.patch"); var resumePath = Path.Combine(latest, "resume.md");
        foreach (var file in new[] { archivePath, patchPath, resumePath }) ExecutorPaths.NoReparse(file);
        if (!Hash(archivePath).Equals(manifest.SourceSnapshotSha256, StringComparison.OrdinalIgnoreCase)
            || !Hash(patchPath).Equals(manifest.GitPatchSha256, StringComparison.OrdinalIgnoreCase)
            || !Hash(resumePath).Equals(manifest.ResumeSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("完整恢复快照哈希验证失败");
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count != manifest.SourceFiles.Count || archive.Entries.Count > 5000) throw new InvalidDataException("ZIP 文件清单不一致");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in manifest.SourceFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (source is null || string.IsNullOrWhiteSpace(source.RelativePath) || string.IsNullOrWhiteSpace(source.Sha256)) throw new InvalidDataException("恢复快照文件记录无效");
            var full = ExecutorPaths.Source(project, source.RelativePath);
            if (!seen.Add(source.RelativePath)) throw new InvalidDataException("恢复快照存在重复路径");
            var entry = archive.GetEntry(source.RelativePath.Replace('\\', '/'));
            if (entry is null || entry.Length != source.Length || source.Length is <0 or >8 * 1024 * 1024) throw new InvalidDataException("恢复 ZIP 条目无效");
            using var stream = entry.Open();
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(source.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("恢复 ZIP 内容损坏");
            if (verifyCurrentSource && (!File.Exists(full) || !Hash(full).Equals(source.Sha256, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("源文件在保存后改变；已保留恢复快照并阻止电源操作");
        }
        return manifest;
    }

    static async Task<string> ReadCriticalInventoryAsync(string project, string dataRoot, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = project, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-c", "core.fsmonitor=false", "-c", "core.untrackedCache=false", "ls-files", "-z", "--cached", "--others", "--exclude-standard" }) start.ArgumentList.Add(argument);
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        start.Environment["TEMP"] = Path.Combine(dataRoot, "temp"); start.Environment["TMP"] = start.Environment["TEMP"];
        ExecutorPaths.NoReparse(start.Environment["TEMP"]!);
        Directory.CreateDirectory(start.Environment["TEMP"]!);
        using var process = Process.Start(start) ?? throw new InvalidDataException("无法验证关键源文件清单");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken); var error = process.StandardError.ReadToEndAsync(cancellationToken);
        try { await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken); }
        catch { if (!process.HasExited) process.Kill(); throw; }
        var text = await output; await error;
        if (process.ExitCode != 0) throw new InvalidDataException("Git 源文件清单验证失败");
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs", ".xaml", ".csproj", ".sln", ".slnx", ".props", ".targets" };
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".git", "bin", "obj", "artifacts", "publish", "node_modules", ".vs", ".idea", "auth", "credentials", "secrets", "logs", "cache", "backups" };
        var paths = text.Split('\0', StringSplitOptions.RemoveEmptyEntries).Where(value => extensions.Contains(Path.GetExtension(value)) && !value.Split('/', '\\').Any(excluded.Contains)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length > 5000) throw new InvalidDataException("关键源文件数量超出限制");
        return string.Join("\n", paths);
    }

    static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    static bool IsPower(LocalActionKind kind) => kind is LocalActionKind.ShutdownWindows or LocalActionKind.RestartWindows or LocalActionKind.SleepWindows or LocalActionKind.HibernateWindows;
    static void PruneOwnedHistory(string root)
    {
        var directory = Path.Combine(root, "logs"); ExecutorPaths.NoReparse(directory);
        var historyPattern = @"^executor-history-\d{8}-\d{6}-[0-9a-f]{32}\.json$";
        foreach (var file in new DirectoryInfo(directory).GetFiles("executor-history-*.json").Where(x => Regex.IsMatch(x.Name, historyPattern)).OrderByDescending(x => x.LastWriteTimeUtc).Skip(20))
        { ExecutorPaths.NoReparse(file.FullName); ExecutorPaths.NoReparse(file.FullName + ".bak"); file.Delete(); if (File.Exists(file.FullName + ".bak")) File.Delete(file.FullName + ".bak"); }
        foreach (var backup in new DirectoryInfo(directory).GetFiles("executor-history-*.json.bak").Where(x => Regex.IsMatch(x.Name[..^4], historyPattern)))
            if (!File.Exists(backup.FullName[..^4])) { ExecutorPaths.NoReparse(backup.FullName); backup.Delete(); }
        var resultRoot = Path.Combine(root, "automation", "results"); ExecutorPaths.NoReparse(resultRoot);
        foreach (var file in new DirectoryInfo(resultRoot).GetFiles("*.json").Where(x => Guid.TryParseExact(Path.GetFileNameWithoutExtension(x.Name), "N", out _)).OrderByDescending(x => x.LastWriteTimeUtc).Skip(20))
        { ExecutorPaths.NoReparse(file.FullName); ExecutorPaths.NoReparse(file.FullName + ".bak"); file.Delete(); if (File.Exists(file.FullName + ".bak")) File.Delete(file.FullName + ".bak"); }
        foreach (var backup in new DirectoryInfo(resultRoot).GetFiles("*.json.bak").Where(x => Guid.TryParseExact(Path.GetFileNameWithoutExtension(x.Name[..^4]), "N", out _)))
            if (!File.Exists(backup.FullName[..^4])) { ExecutorPaths.NoReparse(backup.FullName); backup.Delete(); }
    }
}

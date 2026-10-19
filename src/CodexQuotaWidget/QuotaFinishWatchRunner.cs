using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CodexQuotaWidget.Core;
using CodexQuotaWidget.Core.Automation;
using CodexQuotaWidget.Core.ControlCenter;
using CodexQuotaWidget.Executor;

namespace CodexQuotaWidget;

/// <summary>
/// A single explicitly launched monitor for this project's newly authorised 20% finish.
/// It neither extends the zero-exhaustion executor's authority nor installs a service/startup item.
/// Without --execute it reads once and performs metadata-only checks; tests never execute power.
/// </summary>
public static class QuotaFinishWatchRunner
{
    public const string ProjectRoot = @"D:\CodexQuotaWidget-GitHub";
    public const string DataRoot = @"D:\CodexQuotaWidgetData";
    const double Threshold = 20;
    static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    static readonly TimeSpan SaveGrace = TimeSpan.FromSeconds(30);

    sealed record Options(bool Execute, bool SelfTest);
    sealed record Evidence(double Remaining, long ResetsAt, DateTimeOffset ReadAt);
    sealed record Status
    {
        public string Owner { get; init; } = "CodexQuotaWidget.QuotaFinishWatch";
        public int SchemaVersion { get; init; } = 1;
        public Guid RunId { get; init; }
        public int MonitorProcessId { get; init; } = Environment.ProcessId;
        public DateTimeOffset UpdatedAt { get; init; }
        public string Project { get; init; } = ProjectRoot;
        public string Phase { get; init; } = "Starting";
        public string Source { get; init; } = "OfficialSignedCodexAppServer.account/rateLimits/read";
        public bool ExecuteRequested { get; init; }
        public double ThresholdPercent { get; init; } = Threshold;
        public double? FiveHourRemaining { get; init; }
        public long? ResetsAt { get; init; }
        public DateTimeOffset? ReadAt { get; init; }
        public int ConsecutiveLowReads { get; init; }
        public bool ObservedAboveThreshold { get; init; }
        public string Message { get; init; } = "";
        public string? ResumePath { get; init; }
        public string? ManifestPath { get; init; }
        public bool CheckpointVerified { get; init; }
        public bool ShutdownRequested { get; init; }
        public bool NoForceShutdown { get; init; } = true;
        public bool DoesNotCloseIdeOrCodex { get; init; } = true;
        public IReadOnlyList<string> Targets { get; init; } = [];
        public int SelfTestAssertions { get; init; }
    }

    sealed class Confirmation
    {
        long? cycle;
        long lastReadTick;
        public int Count { get; private set; }
        public bool ObservedAbove { get; private set; }
        public void Reset() { Count = 0; cycle = null; lastReadTick = 0; }
        public bool Observe(Evidence item, long monotonicTick)
        {
            if (item.Remaining > Threshold) { ObservedAbove = true; Reset(); return false; }
            if (cycle != item.ResetsAt || Count > 0 && (monotonicTick <= lastReadTick
                || Stopwatch.GetElapsedTime(lastReadTick, monotonicTick) > TimeSpan.FromSeconds(120))) Count = 0;
            cycle = item.ResetsAt; lastReadTick = monotonicTick; Count++;
            return Count >= 2;
        }
    }

    public static async Task<int> RunAsync(string[] args)
    {
        if (!TryOptions(args, out var options))
        { Console.WriteLine("参数无效：仅支持 --quota-finish-watch [--execute|--dry-run|--self-test] --project-root D:\\CodexQuotaWidget-GitHub [--threshold 20]。"); return 2; }
        // Paths.Data is initialised by Program's --data-dir handling. Refuse a default C home
        // before creating a directory or starting any CLI/signature child process.
        if (!SamePath(Paths.Data, DataRoot) || !AutomationStore.IsSafeDPath(DataRoot)
            || !AutomationStore.IsSafeDPath(ProjectRoot) || !Directory.Exists(ProjectRoot))
        { Console.WriteLine("监测未启动：要求当前项目及数据目录均为指定D盘普通路径。请带 --data-dir D:\\CodexQuotaWidgetData。"); return 2; }
        var directory = Path.Combine(DataRoot, "quota-finish");
        var statusPath = Path.Combine(directory, "watch-status.json");
        var stopPath = Path.Combine(directory, "stop-request.json");
        var state = new Status { RunId = Guid.NewGuid(), ExecuteRequested = options.Execute, UpdatedAt = DateTimeOffset.UtcNow };
        FileStream? watchLease = null;
        AppServer? server = null;
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            EnsureSafe(directory); Directory.CreateDirectory(directory);
            var leasePath = Path.Combine(directory, "watch.lock");
            VerifyLockEntry(leasePath);
            watchLease = new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            VerifyLockEntry(leasePath);
            var temporary = Path.Combine(directory, "temp"); EnsureSafe(temporary); Directory.CreateDirectory(temporary);
            Environment.SetEnvironmentVariable("TEMP", temporary);
            Environment.SetEnvironmentVariable("TMP", temporary);
            if (options.SelfTest)
            {
                state = state with { Phase = "SelfTestPassed", Source = "SyntheticConfirmationFixtures",
                    SelfTestAssertions = SelfTest(), Message = "确认器模拟通过；没有启动模型、关闭程序、创建生产stop-request或执行电源动作。" };
                WriteStatus(statusPath, state); return 0;
            }
            var confirmation = new Confirmation();
            while (!cancellation.IsCancellationRequested)
            {
                Evidence? evidence = null;
                var finishTriggered = false;
                try
                {
                    if (server is null)
                    {
                        server = new AppServer(cancellation.Token, isolateOwnedJob: true);
                        await server.StartAsync().ConfigureAwait(false);
                    }
                    // ReadQuota only calls account/read(refreshToken=false) and rateLimits/read.
                    // Never call LoginAsync, reset-credit consumption, or load a cached widget snapshot.
                    var snapshot = await server.ReadQuota().ConfigureAwait(false);
                    if (!TryEvidence(snapshot, DateTimeOffset.UtcNow, out evidence, out var unknown))
                    {
                        confirmation.Reset();
                        state = state with { Phase = "Unknown", FiveHourRemaining = null, ResetsAt = null, ReadAt = null,
                            ConsecutiveLowReads = 0, Message = unknown };
                    }
                    else
                    {
                        var triggered = confirmation.Observe(evidence!, Stopwatch.GetTimestamp());
                        state = state with { Phase = options.Execute ? "Watching" : "DryRunProbe", FiveHourRemaining = evidence!.Remaining,
                            ResetsAt = evidence.ResetsAt, ReadAt = evidence.ReadAt, ConsecutiveLowReads = confirmation.Count,
                            ObservedAboveThreshold = confirmation.ObservedAbove,
                            Message = triggered ? "同周期连续两次官方新鲜读取确认五小时额度≤20%。"
                                : evidence.Remaining > Threshold ? "官方新鲜额度高于20%，继续监测。" : "启动或当前额度已低；必须再进行一次新的官方读取，不能使用历史报告触发。" };
                        WriteStatus(statusPath, state);
                        finishTriggered = triggered && options.Execute;
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or RpcException
                    or ProtocolException or TimeoutException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    confirmation.Reset();
                    state = state with { Phase = "Unknown", FiveHourRemaining = null, ResetsAt = null, ReadAt = null,
                        ConsecutiveLowReads = 0, Message = "官方额度读取不可用：" + error.GetType().Name + "；未触发保存后关闭或关机。" };
                    if (server is not null) { try { await server.DisposeAsync().ConfigureAwait(false); } catch { } server = null; }
                }
                WriteStatus(statusPath, state);
                // Save/closure failures belong to the one-shot finish transaction, not to the
                // read/reconnect retry loop. Never relabel a failed save as fresh quota failure.
                if (finishTriggered)
                    return await FinishAsync(state, stopPath, statusPath, _ => server!.ReadQuota(), cancellation.Token).ConfigureAwait(false);
                if (!options.Execute)
                {
                    var metadata = await InspectClashAsync(cancellation.Token).ConfigureAwait(false);
                    state = state with { Phase = evidence is null ? "DryRunUnknown" : "DryRunProbe", Targets = metadata.Targets,
                        Message = state.Message + " Dry Run：仅检查限定Clash/mihomo/服务元数据，不关闭、不关机。" };
                    WriteStatus(statusPath, state); return evidence is not null && metadata.Success ? 0 : 1;
                }
                await Task.Delay(PollInterval, cancellation.Token).ConfigureAwait(false);
            }
            return 1;
        }
        catch (OperationCanceledException)
        {
            if (watchLease is not null) WriteStatus(statusPath, state with { Phase = "Cancelled", Message = "本次独立监测已取消；未派发新的关闭或关机动作。" });
            return 1;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException
            or JsonException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // A second monitor cannot overwrite the running monitor's durable status.
            if (watchLease is not null)
                try { WriteStatus(statusPath, state with { Phase = "Blocked", Message = "安全收尾阻止：" + error.GetType().Name + "；未允许后续电源动作。" }); } catch { }
            Console.WriteLine("安全收尾未完成：" + error.GetType().Name); return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
            if (server is not null) try { await server.DisposeAsync().ConfigureAwait(false); } catch { }
            watchLease?.Dispose();
        }
    }

    static async Task<int> FinishAsync(Status state, string stopPath, string statusPath,
        Func<CancellationToken, Task<QuotaSnapshot>> readQuota, CancellationToken token)
    {
        WriteStopRequest(stopPath, state, "SaveRequested", checkpointVerified: false);
        state = state with { Phase = "SaveRequested", Message = "已通告停止新开发，等待30秒保存磁盘编辑；不关闭IDE或Codex。" };
        WriteStatus(statusPath, state);
        await Task.Delay(SaveGrace, token).ConfigureAwait(false);
        await using var recoveryLease = await RecoveryWriteLease.AcquireAsync(DataRoot, token).ConfigureAwait(false);
        var snapshot = ReadQueueSnapshot(state.FiveHourRemaining!.Value);
        var checkpoint = await new SafeFinishCheckpoint(ProjectRoot, DataRoot).CreateAsync(snapshot, token).ConfigureAwait(false);
        if (!checkpoint.Success)
        {
            WriteStatus(statusPath, state with { Phase = "SaveFailed", Message = "检查点未成功；原项目和已有恢复记录保留，禁止关闭Clash或关机。" });
            WriteStopRequest(stopPath, state, "SaveFailed", checkpointVerified: false); return 1;
        }
        var manifest = await VerifyCheckpointAsync(checkpoint, token).ConfigureAwait(false);
        state = state with { ResumePath = checkpoint.ResumePath, ManifestPath = checkpoint.ManifestPath, CheckpointVerified = true,
            Phase = "Saved", Message = "最新源码、Git当前状态与项目进度已完整保存并校验；准备限定Clash元数据检查。" };
        WriteStatus(statusPath, state); WriteStopRequest(stopPath, state, "Saved", checkpointVerified: true);
        WriteFinishSummary(state, manifest);
        var metadata = await InspectClashAsync(token).ConfigureAwait(false);
        state = state with { Targets = metadata.Targets };
        if (!metadata.Success)
        {
            WriteStatus(statusPath, state with { Phase = "ClashMetadataBlocked", Message = "限定Clash目标存在无法核验来源，保留恢复记录，未关闭或关机。" });
            return 1;
        }
        // Saving/grace may cross a quota reset. The earlier two reads cannot authorise a power
        // action in a new cycle, or when the official source is no longer confirmed.
        var currentQuota = await readQuota(token).ConfigureAwait(false);
        if (!TryEvidence(currentQuota, DateTimeOffset.UtcNow, out var finalEvidence, out _))
        {
            WriteStatus(statusPath, state with { Phase = "ThresholdUnconfirmed", Message = "保存已完成，但关闭前官方额度无法重新确认；未关闭Clash或关机。" });
            WriteStopRequest(stopPath, state, "ThresholdUnconfirmed", checkpointVerified: true); return 1;
        }
        if (finalEvidence!.ResetsAt != state.ResetsAt || finalEvidence.Remaining > Threshold)
        {
            state = state with { Phase = "ThresholdChanged", FiveHourRemaining = finalEvidence.Remaining, ResetsAt = finalEvidence.ResetsAt,
                ReadAt = finalEvidence.ReadAt, Message = "保存后额度周期或阈值条件已改变；旧读取不能授权关闭Clash或关机。" };
            WriteStatus(statusPath, state);
            WriteStopRequest(stopPath, state, "ThresholdChanged", checkpointVerified: true, stopNewDevelopment: false); return 1;
        }
        state = state with { FiveHourRemaining = finalEvidence.Remaining, ReadAt = finalEvidence.ReadAt };
        var platform = new WindowsPlatform();
        var actions = new[]
        {
            new LocalAction(LocalActionKind.CloseApplicationGracefully, LocalTarget.ClashVerge),
            new LocalAction(LocalActionKind.Wait, Seconds: 2),
            new LocalAction(LocalActionKind.CloseProcess, LocalTarget.ClashVerge),
            new LocalAction(LocalActionKind.StopWindowsService, LocalTarget.ClashService),
            new LocalAction(LocalActionKind.CloseProcess, LocalTarget.Mihomo),
            new LocalAction(LocalActionKind.VerifyProcessStopped, LocalTarget.ClashVerge),
            new LocalAction(LocalActionKind.VerifyProcessStopped, LocalTarget.Mihomo),
            new LocalAction(LocalActionKind.VerifyServiceStopped, LocalTarget.ClashService)
        };
        foreach (var action in actions)
        {
            token.ThrowIfCancellationRequested();
            var result = await platform.PerformAsync(action, dryRun: false, token).ConfigureAwait(false);
            state = state with { Phase = "ClosingClash", Message = result.Message,
                Targets = state.Targets.Concat(result.Targets ?? []).Distinct().Take(50).ToArray() };
            WriteStatus(statusPath, state);
            if (!result.Success) return 1;
        }
        // Hold the same recovery lease until after the power request: an older live widget's
        // journal cannot replace the verified resume note between save and normal shutdown.
        await VerifyCheckpointAsync(checkpoint, token).ConfigureAwait(false);
        WriteStopRequest(stopPath, state, "ShutdownRequesting", checkpointVerified: true);
        WriteStatus(statusPath, state with { Phase = "ShutdownRequesting", Message = "恢复记录已再次核验。仅请求Windows正常shutdown /s /t 0，绝不/f。" });
        var power = await platform.PerformAsync(new(LocalActionKind.ShutdownWindows), dryRun: false, token).ConfigureAwait(false);
        WriteStatus(statusPath, state with { Phase = power.Success ? "ShutdownRequested" : "ShutdownRejected", ShutdownRequested = power.Success,
            Message = power.Success ? "Windows已接受正常关机请求；未强制关闭未保存应用。实际关机完成无法由本进程证明。" : power.Message });
        WriteStopRequest(stopPath, state, power.Success ? "ShutdownRequested" : "ShutdownRejected", checkpointVerified: true);
        return power.Success ? 0 : 1;
    }

    static async Task<(bool Success, IReadOnlyList<string> Targets)> InspectClashAsync(CancellationToken token)
    {
        var platform = new WindowsPlatform(); var targets = new List<string>();
        foreach (var action in new[] { new LocalAction(LocalActionKind.VerifyProcessStopped, LocalTarget.ClashVerge),
            new LocalAction(LocalActionKind.VerifyProcessStopped, LocalTarget.Mihomo), new LocalAction(LocalActionKind.VerifyServiceStopped, LocalTarget.ClashService) })
        {
            var result = await platform.PerformAsync(action, dryRun: true, token).ConfigureAwait(false);
            targets.AddRange(result.Targets ?? []);
            if (!result.Success || targets.Any(value => value.Contains("Unverified", StringComparison.Ordinal))) return (false, targets.Take(50).ToArray());
        }
        return (true, targets.Distinct().Take(50).ToArray());
    }

    static ControlCenterSnapshot ReadQueueSnapshot(double remaining)
    {
        var path = Path.Combine(DataRoot, "control-center", "state.json"); EnsureSafe(path); EnsureSafe(path + ".bak");
        ControlCenterState queue = new();
        if (File.Exists(path))
        {
            if (new FileInfo(path).Length is < 1 or > 512 * 1024) throw new InvalidDataException("本地任务主记录大小异常");
            queue = JsonSerializer.Deserialize<ControlCenterState>(File.ReadAllBytes(path)) ?? throw new InvalidDataException("本地任务主记录为空");
            if (queue.SchemaVersion != 1 || queue.Tasks is null || queue.Tasks.Count > ControlCenterService.MaximumTasks
                || queue.Tasks.Any(item => item is null || !Enum.IsDefined(item.State))) throw new InvalidDataException("本地任务主记录不兼容");
        }
        else if (File.Exists(path + ".bak")) throw new InvalidDataException("本地任务主记录缺失，不能使用旧备份推断已保存");
        return new() { Tasks = queue.Tasks, Policy = queue.Policy, DispatchPaused = true, SafeStopRequested = true,
            Guard = new(QuotaGuardState.Protected, remaining, null, false, true, "用户本次明确授权：五小时剩余20%安全保存并正常关机。") };
    }

    static async Task<SafeFinishManifest> VerifyCheckpointAsync(SafeFinishCheckpointResult result, CancellationToken token)
    {
        foreach (var path in new[] { result.ResumePath, result.ManifestPath, result.SourceSnapshotPath })
            if (path is null || !AutomationStore.IsSafeDPath(path) || !File.Exists(path)) throw new InvalidDataException("恢复检查点文件缺失");
        if (new FileInfo(result.ManifestPath!).Length is < 1 or > 2 * 1024 * 1024) throw new InvalidDataException("恢复清单大小异常");
        var manifest = JsonSerializer.Deserialize<SafeFinishManifest>(await File.ReadAllBytesAsync(result.ManifestPath!, token).ConfigureAwait(false))
            ?? throw new InvalidDataException("恢复清单为空");
        var patch = Path.Combine(DataRoot, "resume", "latest", "working-tree.patch");
        var embeddedResume = Path.Combine(DataRoot, "resume", "latest", "resume.md"); EnsureSafe(patch); EnsureSafe(embeddedResume);
        if (manifest.Owner != "CodexQuotaWidget.SafeFinishCheckpoint" || manifest.SchemaVersion != 1
            || !SamePath(manifest.ProjectRoot, ProjectRoot) || manifest.SourceFiles is not { Count: > 0 and <= 5000 }
            || Hash(result.SourceSnapshotPath!) != manifest.SourceSnapshotSha256 || Hash(patch) != manifest.GitPatchSha256
            || Hash(result.ResumePath!) != manifest.ResumeSha256 || Hash(embeddedResume) != manifest.ResumeSha256)
            throw new InvalidDataException("恢复检查点校验失败，禁止电源动作");
        var prefix = Path.GetFullPath(ProjectRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var file in manifest.SourceFiles)
        {
            token.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(Path.Combine(ProjectRoot, file.RelativePath));
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !AutomationStore.IsSafeDPath(path)
                || !File.Exists(path) || new FileInfo(path).Length != file.Length || Hash(path) != file.Sha256)
                throw new InvalidDataException("保存后源码再次变化或路径异常，禁止电源动作");
        }
        return manifest;
    }

    static bool TryEvidence(QuotaSnapshot snapshot, DateTimeOffset now, out Evidence? evidence, out string reason)
    {
        evidence = null; reason = "五小时官方额度缺失、异常或不新鲜，保持Unknown，不触发关机。";
        if (snapshot.UpdatedAt == default || snapshot.UpdatedAt > now || now - snapshot.UpdatedAt > TimeSpan.FromSeconds(10)) return false;
        var candidates = snapshot.Windows.Where(window => window.LimitId == "codex" && window.WindowDurationMins == 300).ToArray();
        if (candidates.Length != 1 || candidates[0].UsedPercent is not { } used || !double.IsFinite(used) || used is < 0 or > 100
            || candidates[0].ResetsAt is not { } reset || reset <= now.ToUnixTimeSeconds()
            || reset > now.AddMinutes(302).ToUnixTimeSeconds()) return false;
        evidence = new(100 - used, reset, snapshot.UpdatedAt); reason = ""; return true;
    }

    static void WriteStopRequest(string path, Status state, string phase, bool checkpointVerified, bool stopNewDevelopment = true)
    {
        EnsureSafe(path); EnsureSafe(path + ".bak");
        AtomicStore.Write(path, new { Owner = "CodexQuotaWidget.QuotaFinishWatch", SchemaVersion = 1, state.RunId,
            RequestedAt = DateTimeOffset.UtcNow, ProjectRoot, MonitorProcessId = Environment.ProcessId, StopNewDevelopment = stopNewDevelopment,
            StopNewTasks = stopNewDevelopment, StopWorkflowDispatch = stopNewDevelopment, Phase = phase, state.FiveHourRemaining, state.ResetsAt,
            state.ReadAt, ThresholdPercent = Threshold, state.ExecuteRequested, CheckpointVerified = checkpointVerified,
            ResumePath = Path.Combine(DataRoot, "codex_resume_state.md"), PassiveNotification = true, GrantsActionAuthority = false,
            Reason = "用户明确授权本次剩余20%安全收尾；同周期两次官方fresh读取已确认。" });
    }

    static void WriteFinishSummary(Status state, SafeFinishManifest manifest)
    {
        var path = Path.Combine(DataRoot, "quota-finish", "finish-summary.md"); EnsureSafe(path); EnsureSafe(path + ".bak");
        var content = $"# 20%额度安全收尾\n\n保存时间：{DateTimeOffset.UtcNow:O}\n项目：{ProjectRoot}\n版本：{manifest.Version}\n运行记录：{state.RunId}\n五小时剩余：{state.FiveHourRemaining:0.##}%\n源码文件：{manifest.SourceFiles.Count}\n未提交修改：{manifest.DirtyFiles.Count}\n\n明天先阅读 {Path.Combine(DataRoot, "codex_resume_state.md")} 与项目 docs/AI-CONTROL-CENTER-STATUS.md、docs/AI-CONTROL-CENTER-REPORT.md。完整恢复文件仍保留最新docs进度；源代码没有自动暂存、提交、重置或推送。\n\n继续前核验源码与manifest，再Release编译测试。仅保存磁盘内容，未保存IDE缓冲无法自动检查；本次只请求正常关机，应用可阻止，不使用/f。未修改Codex核心、AGENTS.md、代理或服务配置。\n";
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { var bytes = Encoding.UTF8.GetBytes(content); stream.Write(bytes); stream.Flush(true); }
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak"); else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    static void WriteStatus(string path, Status state)
    {
        EnsureSafe(path); EnsureSafe(path + ".bak"); state = state with { UpdatedAt = DateTimeOffset.UtcNow };
        AtomicStore.Write(path, state);
        Console.WriteLine(JsonSerializer.Serialize(new { state.RunId, state.Phase, state.FiveHourRemaining,
            state.ResetsAt, state.ReadAt, WindowDurationMins = 300, state.Source,
            state.ConsecutiveLowReads, state.CheckpointVerified, state.ShutdownRequested, state.Message }));
    }
    static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    static void EnsureSafe(string path) { if (!AutomationStore.IsSafeDPath(path)) throw new InvalidDataException("监测路径不是D盘普通路径"); }
    static bool SamePath(string first, string second)
        => Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar).Equals(Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    static void VerifyLockEntry(string path)
    {
        EnsureSafe(Path.GetDirectoryName(path)!);
        try { if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) throw new InvalidDataException("监测锁路径异常"); }
        catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
    }

    static bool TryOptions(string[] args, out Options options)
    {
        bool execute = false, dryRun = false, selfTest = false, entry = false, project = false, threshold = false;
        options = new(false, false);
        for (int index = 0; index < args.Length; index++)
        {
            var item = args[index];
            if (item == "--quota-finish-watch" && !entry) { entry = true; continue; }
            if (item == "--execute" && !execute) { execute = true; continue; }
            if (item == "--dry-run" && !dryRun) { dryRun = true; continue; }
            if (item == "--self-test" && !selfTest) { selfTest = true; continue; }
            if (item == "--project-root" && !project && index + 1 < args.Length)
            {
                try { if (!SamePath(args[++index], ProjectRoot)) return false; } catch (ArgumentException) { return false; }
                project = true; continue;
            }
            if (item == "--threshold" && !threshold && index + 1 < args.Length
                && double.TryParse(args[++index], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value == Threshold)
            { threshold = true; continue; }
            return false;
        }
        if (!entry || !project || execute && (dryRun || selfTest)) return false;
        options = new(execute, selfTest); return true;
    }

    static int SelfTest()
    {
        var count = 0;
        void Check(bool success) { if (!success) throw new InvalidOperationException("阈值确认器模拟失败"); count++; }
        var now = DateTimeOffset.UtcNow; var reset = now.AddHours(1).ToUnixTimeSeconds();
        var tick = Stopwatch.GetTimestamp(); var interval = Stopwatch.Frequency * 30L;
        QuotaSnapshot Sample(double used, DateTimeOffset? at = null, long? cycle = null)
            => new(at ?? now, "Synthetic", [new("codex", "codex", "primary", used, 300, cycle ?? reset)], false, null, []);
        Check(TryEvidence(Sample(80), now, out var low, out _) && low!.Remaining == 20);
        var startupLow = new Confirmation(); Check(!startupLow.Observe(low!, tick)); Check(startupLow.Observe(low!, tick + interval));
        var crossed = new Confirmation(); Check(!crossed.Observe(new(21, reset, now), tick));
        Check(crossed.ObservedAbove && !crossed.Observe(low!, tick + interval)); Check(crossed.Observe(low!, tick + interval * 2));
        var interrupted = new Confirmation(); interrupted.Observe(low!, tick); interrupted.Reset();
        Check(!interrupted.Observe(low!, tick + interval)); Check(interrupted.Observe(low!, tick + interval * 2));
        var changedCycle = new Confirmation(); changedCycle.Observe(low!, tick);
        Check(!changedCycle.Observe(low! with { ResetsAt = reset + 100 }, tick + interval));
        Check(!TryEvidence(Sample(101), now, out _, out _)); Check(!TryEvidence(Sample(double.NaN), now, out _, out _));
        Check(!TryEvidence(Sample(80, now.AddMinutes(-1)), now, out _, out _));
        Check(!TryEvidence(Sample(80, cycle: now.AddMinutes(-1).ToUnixTimeSeconds()), now, out _, out _));
        var delayed = new Confirmation(); delayed.Observe(low!, tick); Check(!delayed.Observe(low!, tick + interval * 5));
        Check(!TryOptions(["--quota-finish-watch", "--execute", "--self-test", "--project-root", ProjectRoot], out _));
        Check(!TryOptions(["--quota-finish-watch", "--execute", "--threshold", "50", "--project-root", ProjectRoot], out _));
        return count;
    }
}

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodexQuotaWidget.Core.Automation;
using CodexQuotaWidget.Core.Observation;

namespace CodexQuotaWidget.Executor;

public sealed record ExecutorSelfTestResult(bool Success, int Checks, bool IndependentOfCodexAndChatGpt, bool PowerSimulated, string DataRoot, string Message);

/// <summary>Creates an isolated D-drive Git fixture. Its injected platform has no Codex, ChatGPT, service or power dependency.</summary>
public static class ExecutorSelfTest
{
    public static async Task<ExecutorSelfTestResult> RunAsync(string fixtureRoot, int startupDelaySeconds = 0)
    {
        var fixture = ExecutorPaths.OnD(fixtureRoot);
        if (Directory.Exists(fixture) && Directory.EnumerateFileSystemEntries(fixture).Any()) throw new InvalidDataException("自检目录必须为空，避免覆盖用户文件");
        var project = Path.Combine(fixture, "project"); var data = Path.Combine(fixture, "data"); var temp = Path.Combine(fixture, "temp");
        Directory.CreateDirectory(project); Directory.CreateDirectory(data); Directory.CreateDirectory(temp);
        Environment.SetEnvironmentVariable("TEMP", temp); Environment.SetEnvironmentVariable("TMP", temp);
        await File.WriteAllTextAsync(Path.Combine(project, "README.md"), "# Independent executor fixture\n", new UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(project, "Program.cs"), "Console.WriteLine(\"fixture\");\n", new UTF8Encoding(false));
        await GitAsync(project, temp, "init"); await GitAsync(project, temp, "add", "README.md", "Program.cs");
        await GitAsync(project, temp, "-c", "user.name=LocalExecutorFixture", "-c", "user.email=fixture@invalid.local", "commit", "-m", "fixture");
        var passed = 0;
        void Check(bool condition, string message) { if (!condition) throw new InvalidDataException("执行器自检失败：" + message); passed++; }
        var plan = ActionPlanParser.Parse("关闭Clash和mihomo，然后关机").Plan ?? throw new InvalidDataException("fixture 计划解析失败");
        var now = DateTimeOffset.UtcNow; var reset = now.AddHours(1).ToUnixTimeSeconds();
        var receipt = new ExhaustionReceipt
        {
            EventId = Guid.NewGuid(), ResetAt = reset, CycleKey = ExhaustionDetector.CycleKey(reset),
            PositiveObservedAt = now.AddSeconds(-3), PositiveRemaining = 1, FirstZeroObservedAt = now.AddSeconds(-2),
            SecondZeroObservedAt = now.AddSeconds(-1), CreatedAt = now, Synthetic = true, PlanFingerprint = ActionPlanParser.Fingerprint(plan)
        };
        var request = new ExecutorRequest { DataRoot = data, ProjectRoot = project, Plan = plan, Receipt = receipt, CreatedAt = now };
        if (startupDelaySeconds > 0) await Task.Delay(TimeSpan.FromSeconds(startupDelaySeconds));
        var platform = new IsolatedPlatform();
        var engine = new ExecutorEngine(platform);
        var result = await engine.RunAsync(request);
        Check(result.Success, result.Message);
        Check(result.DryRun && !result.PowerRequested, "power 不得真实执行");
        Check(result.Steps.Last().Action.Kind == LocalActionKind.ShutdownWindows && result.Steps.Last().Simulated && result.Steps.Last().Success, "必须走到并验证关机步骤");
        Check(platform.CodexAbsent && platform.ChatGptUnavailable, "脱离 AI 依赖");
        Check(File.Exists(Path.Combine(data, "codex_resume_state.md")) && File.Exists(Path.Combine(data, "resume", "latest", "source.zip")), "恢复文件存在");
        var original = await File.ReadAllTextAsync(Path.Combine(project, "Program.cs"));
        Check(original.Contains("fixture", StringComparison.Ordinal), "未改项目源文件");
        var duplicate = await engine.RunAsync(request);
        Check(!duplicate.Success && duplicate.Message.Contains("重复", StringComparison.Ordinal), "同一请求不可重复");
        var invalid = await engine.RunAsync(request with { RequestId = Guid.NewGuid(), Receipt = receipt with { RemainingPercent = 1 } });
        Check(!invalid.Success && invalid.Steps.Count == 0, "非零额度拒绝");
        var syntheticFormal = await engine.RunAsync(request with { RequestId = Guid.NewGuid(), TestOnly = false }, false);
        Check(!syntheticFormal.Success && syntheticFormal.Steps.Count == 0, "模拟凭据禁止正式执行");
        var forgedFormal = await engine.RunAsync(request with { RequestId = Guid.NewGuid(), TestOnly = false, Receipt = receipt with { Synthetic = false } }, false);
        Check(!forgedFormal.Success && forgedFormal.Steps.Count == 0, "任意请求不能绕过保存的正式授权");
        var stale = await engine.RunAsync(request with { RequestId = Guid.NewGuid(), Receipt = receipt with { CreatedAt = now.AddHours(-1) } });
        Check(!stale.Success && stale.Steps.Count == 0, "过期凭据拒绝");
        var sourceChangePlatform = new IsolatedPlatform(() => File.AppendAllText(Path.Combine(project, "Program.cs"), "// changed after checkpoint\n"));
        var changed = await new ExecutorEngine(sourceChangePlatform).RunAsync(request with { RequestId = Guid.NewGuid() });
        Check(!changed.Success && !sourceChangePlatform.PowerStepReached, "保存后源代码变化必须阻止 power");
        var latestResume = Path.Combine(data, "resume", "latest", "resume.md");
        var note = Path.Combine(data, "codex_resume_state.md");
        await File.AppendAllTextAsync(note, "\nLightweight journal updated separately.\n");
        Check(File.ReadAllText(latestResume) != File.ReadAllText(note), "外层轻量日志与快照独立");
        await ExecutorEngine.VerifyCheckpointAsync(project, data, false);
        Check(true, "完整快照验证使用 latest/resume.md，而非外层日志");

        // Exercise real authorization code with fabricated evidence in an isolated fixture and an
        // injected platform incapable of touching processes/services/power. This is not real execution acceptance.
        var authorityPlan = ActionPlanParser.Parse("保存恢复记录").Plan ?? throw new InvalidDataException("fixture 无电源计划无效");
        var authorityQuota = receipt with { EventId = Guid.NewGuid(), Synthetic = false, PlanFingerprint = ActionPlanParser.Fingerprint(authorityPlan) };
        var terminal = new CodexStatusObservation(CodexObservedState.Completed, now, StatusEvidence.LocalSessionSource,
            "fixture-session", "fixture-turn", true, "Synthetic authorization fixture; not a real task", false)
        { EvidenceAt = now.AddSeconds(-1), ProjectRoot = project, ScopedSessionCount = 1, CoverageComplete = true,
            TrustLevel = CodexObservationTrustLevel.ProjectVerified, EvidenceLevel = CodexEvidenceLevel.LifecycleConfirmed,
            EvidenceKind = "task_complete", EvidencePosition = 1024 };
        var confirmed = CodexExhaustedEventFactory.Confirm(authorityQuota, terminal, now, project, data)
            ?? throw new InvalidDataException("fixture 双重证据未构造");
        var store = new AutomationStore(data);
        Check(store.SaveConfiguration(new() { Enabled = true, ProjectRoot = project, Text = authorityPlan.Text, Plan = authorityPlan }, out _),
            "隔离授权fixture配置保存");
        store.SavePendingReceipt(confirmed); store.MarkCycleHandled(confirmed.CycleKey);
        var authorityRequest = new ExecutorRequest { DataRoot = data, ProjectRoot = project, Plan = authorityPlan, Receipt = confirmed, CreatedAt = now, TestOnly = false };
        var roundtrip = JsonSerializer.Deserialize<ExecutorRequest>(JsonSerializer.Serialize(authorityRequest))
            ?? throw new InvalidDataException("fixture roundtrip 无效");
        Check(CodexExhaustedEventFactory.EqualReceiptEvidence(store.ReadPendingReceipt(), roundtrip.Receipt),
            "pending与request独立反序列化后嵌套数组应结构相等");
        var fakeAuthorityPlatform = new AuthorizationOnlyFixturePlatform();
        var authorityEngine = new ExecutorEngine(fakeAuthorityPlatform, () => now, _ => Task.FromResult(terminal));
        var changedEvidence = confirmed with { ExhaustedEvent = confirmed.ExhaustedEvent! with { SuggestedNextActions = ["changed-fixture-evidence"] } };
        Check(!CodexExhaustedEventFactory.EqualReceiptEvidence(confirmed, changedEvidence), "嵌套event字段改动不能绕过授权比较");
        var alteredAuthorization = await authorityEngine.RunAsync(roundtrip with { RequestId = Guid.NewGuid(), Receipt = changedEvidence }, false);
        Check(!alteredAuthorization.Success && alteredAuthorization.Steps.Count == 0 && fakeAuthorityPlatform.Calls == 0,
            "修改嵌套事件的正式分支fixture在动作前拒绝");
        var missingTaskConfirmation = await authorityEngine.RunAsync(roundtrip with { RequestId = Guid.NewGuid(), Receipt = authorityQuota }, false);
        Check(!missingTaskConfirmation.Success && missingTaskConfirmation.Steps.Count == 0 && fakeAuthorityPlatform.Calls == 0,
            "只有零额度、没有任务状态的正式分支fixture拒绝");
        var unknownStatusPlatform = new AuthorizationOnlyFixturePlatform();
        var unknownStatusEngine = new ExecutorEngine(unknownStatusPlatform, () => now,
            _ => Task.FromResult(terminal with { State = CodexObservedState.Unknown, TaskStoppedConfirmed = false }));
        var unknownCurrentTask = await unknownStatusEngine.RunAsync(roundtrip with { RequestId = Guid.NewGuid() }, false);
        Check(!unknownCurrentTask.Success && unknownCurrentTask.Steps.Count == 0 && unknownStatusPlatform.Calls == 0,
            "正式授权fixture当前状态Unknown，在保存和动作前拒绝");
        var updatedTaskPlatform = new AuthorizationOnlyFixturePlatform();
        var statusReads = 0;
        var newTaskEngine = new ExecutorEngine(updatedTaskPlatform, () => now, _ => Task.FromResult(++statusReads == 1
            ? terminal : terminal with { State = CodexObservedState.Running, TurnId = "new-fixture-turn", TaskStoppedConfirmed = false }));
        // Build a separately authorized event so it can pass the first gate, then start a new turn after saving.
        var newTurnEvent = confirmed with { EventId = Guid.NewGuid() };
        newTurnEvent = newTurnEvent with { ExhaustedEvent = newTurnEvent.ExhaustedEvent! with { EventId = newTurnEvent.EventId } };
        store.SavePendingReceipt(newTurnEvent);
        var startedAgain = await newTaskEngine.RunAsync(roundtrip with { RequestId = Guid.NewGuid(), Receipt = newTurnEvent }, false);
        Check(!startedAgain.Success && startedAgain.Steps.Any(step => step.Action.Kind == LocalActionKind.CreateRecoverySnapshot && step.Success)
            && updatedTaskPlatform.Calls == 0, "恢复保存期间出现新turn，第一次关闭前再次观察并拒绝");
        store.SavePendingReceipt(confirmed);
        try
        {
            _ = new ExecutorEngine(statusReader: _ => Task.FromResult(terminal));
            throw new InvalidDataException("真实Windows平台允许替换状态观察");
        }
        catch (ArgumentException) { Check(true, "默认真实Windows平台拒绝替换状态reader"); }
        try
        {
            _ = new ExecutorEngine(new WindowsPlatform(), statusReader: _ => Task.FromResult(terminal));
            throw new InvalidDataException("显式真实Windows平台允许替换状态观察");
        }
        catch (ArgumentException) { Check(true, "显式真实Windows平台拒绝替换状态reader"); }
        var authorizedFixture = await authorityEngine.RunAsync(roundtrip, false);
        Check(authorizedFixture.Success && !authorizedFixture.DryRun && !authorizedFixture.PowerRequested && fakeAuthorityPlatform.Calls > 0,
            "正式授权代码分支经过序列化roundtrip通过；平台完全替身且计划无电源，非真实关闭验收");
        Check(authorizedFixture.Steps.All(step => step.Action.Kind is not (LocalActionKind.ShutdownWindows or LocalActionKind.RestartWindows
            or LocalActionKind.SleepWindows or LocalActionKind.HibernateWindows)), "授权fixture不含任何电源步骤");
        var repeatedEvent = await authorityEngine.RunAsync(roundtrip with { RequestId = Guid.NewGuid() }, false);
        Check(!repeatedEvent.Success && repeatedEvent.Steps.Count == 0, "同event新request也不能重复正式分支fixture");

        var ledger = Path.Combine(data, "automation", "executor-receipts.json");
        await File.WriteAllTextAsync(ledger, "{broken-primary");
        var corruptedLedger = await engine.RunAsync(request with { RequestId = Guid.NewGuid() });
        Check(!corruptedLedger.Success && corruptedLedger.Steps.Count == 0, "损坏去重主文件不回退旧备份");
        return new(true, passed, true, true, data, "无 Codex/ChatGPT/网络，独立执行器完成真实恢复保存、全部白名单模拟步骤及电源步骤；异常保护通过");
    }

    sealed class AuthorizationOnlyFixturePlatform : ILocalPlatform
    {
        public int Calls { get; private set; }
        public Task<PlatformActionResult> PerformAsync(LocalAction action, bool dryRun, CancellationToken cancellationToken)
        {
            if (action.Kind is LocalActionKind.ShutdownWindows or LocalActionKind.RestartWindows or LocalActionKind.SleepWindows or LocalActionKind.HibernateWindows)
                throw new InvalidDataException("授权fixture绝不接受电源步骤");
            Calls++;
            return Task.FromResult(new PlatformActionResult(true, "仅验证正式授权分支的替身平台；未关闭进程或停止服务"));
        }
    }

    sealed class IsolatedPlatform(Action? mutate = null) : ILocalPlatform
    {
        bool changed;
        public bool CodexAbsent => true;
        public bool ChatGptUnavailable => true;
        public bool PowerStepReached { get; private set; }
        public Task<PlatformActionResult> PerformAsync(LocalAction action, bool dryRun, CancellationToken cancellationToken)
        {
            if (!dryRun) throw new InvalidDataException("测试 fixture 禁止正式执行");
            if (!changed && action.Kind == LocalActionKind.CloseApplicationGracefully)
            { changed = true; mutate?.Invoke(); }
            if (action.Kind is LocalActionKind.ShutdownWindows or LocalActionKind.RestartWindows or LocalActionKind.SleepWindows or LocalActionKind.HibernateWindows)
                PowerStepReached = true;
            return Task.FromResult(new PlatformActionResult(true, "隔离 fixture：Codex 不存在、ChatGPT 不可用；仅本地模拟白名单操作"));
        }
    }

    static async Task GitAsync(string project, string temp, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = project, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1"; start.Environment["GIT_CONFIG_GLOBAL"] = "NUL"; start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        start.Environment["TEMP"] = temp; start.Environment["TMP"] = temp;
        using var process = Process.Start(start) ?? throw new InvalidDataException("fixture Git 无法启动");
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        catch { if (!process.HasExited) process.Kill(); throw; }
        await output; await error;
        if (process.ExitCode != 0) throw new InvalidDataException("fixture Git 操作失败");
    }
}

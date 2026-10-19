using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using CodexQuotaWidget.Core;
using CodexQuotaWidget.Core.Automation;

namespace CodexQuotaWidget;

/// <summary>Explicit, single-use acceptance handoff. Never enables production automation.</summary>
internal static class ManualAcceptanceLauncher
{
    internal static int Run(string[] args)
    {
        string? requestPath = null;
        var launched = false;
        var realActions = false;
        try
        {
            var execute = false;
            string? project = null;
            var seen = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            for(var index = 0; index < args.Length; index++)
            {
                var key = args[index];
                if(!seen.Add(key))throw new InvalidDataException("实机验收参数重复");
                switch(key)
                {
                    case "--manual-acceptance-test": break;
                    case "--authorize-clash-restart": execute = true; break;
                    case "--project-root":
                        if(++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
                            throw new InvalidDataException("验收必须指定项目路径");
                        project = args[index]; break;
                    default: throw new InvalidDataException("验收只允许项目路径和单次 Clash/mihomo 重启授权");
                }
            }
            realActions = execute;
            if(project is null || !AutomationStore.IsSafeDPath(project) || !Directory.Exists(project)
                || !Directory.Exists(Path.Combine(project, ".git")) || !AutomationStore.IsSafeDPath(Paths.Data))
                throw new InvalidDataException("验收需要 D 盘真实 Git 项目和 D 盘数据目录");
            var directory = Path.Combine(Paths.Data, "manual-tests", "requests");
            if(!AutomationStore.IsSafeDPath(directory))throw new InvalidDataException("验收目录含异常链接");
            Directory.CreateDirectory(directory);
            var now = DateTimeOffset.UtcNow;
            using var parent = Process.GetCurrentProcess();
            var request = new ManualAcceptanceRequest
            {
                TestId = Guid.NewGuid(), DataRoot = Paths.Data, ProjectRoot = Path.GetFullPath(project),
                CreatedAt = now, ExpiresAt = now.AddMinutes(10),
                Authorization = "UserAuthorizedClashMihomoRestartAcceptance",
                RealActionsAuthorized = execute, SimulatedExhaustion = true, DelaySeconds = 90,
                SimulatedQuotaReceipt = SimulateExhaustion(now),
                LauncherProcessId = Environment.ProcessId, LauncherStartedAt = parent.StartTime.ToUniversalTime()
            };
            requestPath = Path.Combine(directory, request.TestId.ToString("N") + ".json");
            if(!AutomationStore.IsSafeDPath(requestPath) || !AutomationStore.IsSafeDPath(requestPath + ".bak"))
                throw new InvalidDataException("验收请求路径异常");
            AtomicStore.Write(requestPath, request);
            var reportPath = Path.Combine(Paths.Data, "manual-tests", "results", request.TestId.ToString("N") + ".json");
            var launchPath = Path.Combine(Paths.Data, "manual-tests", "last-launch.json");
            if(!AutomationStore.IsSafeDPath(launchPath) || !AutomationStore.IsSafeDPath(launchPath + ".bak"))
                throw new InvalidDataException("验收交接记录路径异常");
            AtomicStore.Write(launchPath, new { request.TestId, RequestPath = requestPath, ReportPath = reportPath,
                ParentPid = Environment.ProcessId, Method = "Preparing", SimulatedExhaustion = true,
                RealClashMihomoAndRestart = execute, CreatedAt = now });
            var method = IndependentExecutorLauncher.LaunchManualAcceptance(AutomationRuntime.ExecutorPath, requestPath, execute);
            launched = true;
            var receipt = new { request.TestId, RequestPath = requestPath, ReportPath = reportPath,
                ParentPid = Environment.ProcessId, Method = method, SimulatedExhaustion = true,
                RealClashMihomoAndRestart = execute, CreatedAt = now };
            AtomicStore.Write(launchPath, receipt);
            Console.WriteLine(JsonSerializer.Serialize(receipt));
            return 0;
        }
        catch(Exception error)when(error is IOException or InvalidDataException or UnauthorizedAccessException
            or ArgumentException or Win32Exception or InvalidOperationException)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { Success = false, NoPowerAction = !(launched && realActions),
                RealActionsMayBeArmed = launched && realActions,
                RequestPath = requestPath, Reason = error is InvalidDataException ? error.Message : error.GetType().Name }));
            return 1;
        }
    }
    static ExhaustionReceipt SimulateExhaustion(DateTimeOffset now)
    {
        var detector = new ExhaustionDetector();
        var reset = now.AddHours(1).ToUnixTimeSeconds();
        QuotaSnapshot Sample(DateTimeOffset at, double used) => new(at, "manual-acceptance-fixture",
            [new("codex", "Codex", "primary", used, 300, reset)], false, null, []);
        if(detector.Observe(Sample(now.AddSeconds(-3), 95), MonitorStatus.Live, false, now.AddSeconds(-3)) is not null
            || detector.Observe(Sample(now.AddSeconds(-2), 100), MonitorStatus.Live, false, now.AddSeconds(-2)) is not null)
            throw new InvalidDataException("模拟耗尽前置判断异常，未授权任何动作");
        var receipt = detector.Observe(Sample(now.AddSeconds(-1), 100), MonitorStatus.Live, false, now.AddSeconds(-1));
        if(receipt is null)throw new InvalidDataException("模拟额度耗尽未通过真实检测算法");
        // This fixture can never be a production authorization or a real CodexExhaustedEvent.
        return receipt with { Synthetic = true };
    }
}

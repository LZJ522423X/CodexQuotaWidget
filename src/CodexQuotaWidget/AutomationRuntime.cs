using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CodexQuotaWidget.Core;
using CodexQuotaWidget.Core.Automation;

namespace CodexQuotaWidget;

internal sealed class AutomationRuntime : IAutomationUiController
{
    public const string DefaultText = "关闭Clash Verge和mihomo，然后关机。";
    readonly AutomationStore store;
    readonly string projectRoot;
    AutomationConfiguration configuration;
    string status;
    bool eventStorageBlocked;
    public AutomationConfiguration Configuration => configuration;
    public AutomationStore Store => store;
    public static string ExecutorPath => Path.Combine(AppContext.BaseDirectory, "executor", "CodexQuotaWidget.Executor.exe");

    public AutomationRuntime(string projectRoot)
    {
        this.projectRoot = projectRoot; store = new AutomationStore(Paths.Data);
        var read = store.LoadConfiguration();
        configuration = read.Configuration.Plan is null
            ? read.Configuration with { Enabled = false, ProjectRoot = projectRoot, Text = DefaultText }
            : read.Configuration;
        status = !read.Success ? read.Message : configuration.Plan is null ? "保存后提前生成本地执行计划" : "本地执行计划已准备";
    }
    public AutomationUiState GetState() => new(configuration.Enabled, configuration.Text, status,
        configuration.Plan?.Actions.Select(ActionPlanParser.Describe).ToArray());
    internal void DisableForStorageError()
    {
        configuration = configuration with { Enabled = false };
        eventStorageBlocked = true;
        status = "耗尽事件记录不可用，自动收尾已停用；请先修复本地记录";
    }
    internal void SetExecutionStatus(string message) => status = message;

    public AutomationUiSaveResult Save(bool enabled, string text)
    {
        if(enabled && eventStorageBlocked)return new(false,"耗尽事件记录不可用；尚未启用，请修复记录后重启",[]);
        var parsed = ActionPlanParser.Parse(text);
        if (!parsed.Success || parsed.Plan is null) return new(false, parsed.Message, []);
        if (enabled && !File.Exists(ExecutorPath)) return new(false, "独立 Executor 不存在，未启用自动收尾", []);
        var next = new AutomationConfiguration { Enabled = enabled, Text = text.Trim(), Plan = parsed.Plan, ProjectRoot = projectRoot };
        if (!store.SaveConfiguration(next, out var error)) return new(false, error, []);
        configuration = next;
        status = enabled ? "本地执行计划已准备；仅真实额度耗尽后执行" : "本地执行计划已准备；自动收尾未开启";
        return new(true, status, parsed.Plan.Actions.Select(ActionPlanParser.Describe).ToArray());
    }

    public async Task<string> TestAsync(string text)
    {
        var parsed = ActionPlanParser.Parse(text);
        if (!parsed.Success || parsed.Plan is null) return parsed.Message;
        if (!File.Exists(ExecutorPath)) return "测试未启动：独立 Executor 不存在";
        var request = CreateTestRequest(projectRoot, parsed.Plan);
        try
        {
            Launch(request, execute: false);
            var result = await WaitForResultAsync(request.RequestId);
            if (result is null) return "测试结果等待超时；未执行真实关闭或关机，请查看本地执行历史";
            return Format(result);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException)
        { return "测试未完成：" + e.GetType().Name + "；未授权真实电源操作"; }
    }

    internal static ExecutorRequest CreateTestRequest(string projectRoot, LocalActionPlan plan, bool simulateAiUnavailable=false)
    {
        var now = DateTimeOffset.UtcNow;
        var reset = now.AddHours(1).ToUnixTimeSeconds();
        var receipt = new ExhaustionReceipt
        {
            Synthetic = true, ResetAt = reset, CycleKey = ExhaustionDetector.CycleKey(reset),
            PositiveObservedAt = now.AddSeconds(-3), PositiveRemaining = 1,
            FirstZeroObservedAt = now.AddSeconds(-2), SecondZeroObservedAt = now.AddSeconds(-1), CreatedAt = now,
            PlanFingerprint = ActionPlanParser.Fingerprint(plan)
        };
        return new ExecutorRequest { DataRoot = Paths.Data, ProjectRoot = projectRoot, Plan = plan, Receipt = receipt, TestOnly = true, SimulateAiUnavailable = simulateAiUnavailable };
    }

    internal string LaunchConfirmed(ExhaustionReceipt receipt)
    {
        // Load from disk again so a stale UI configuration cannot authorize power actions.
        var read = store.LoadConfiguration();
        if (!read.Success || read.Configuration is not { Enabled: true, Plan: not null } current)
            throw new InvalidOperationException("没有有效的已启用本地执行计划");
        var confirmed = receipt with { PlanFingerprint = ActionPlanParser.Fingerprint(current.Plan) };
        if (!CodexExhaustedEventFactory.Validate(confirmed, DateTimeOffset.UtcNow,current.ProjectRoot) || confirmed.Synthetic)
            throw new InvalidOperationException("额度耗尽或真实任务停止证据无效");
        store.SavePendingReceipt(confirmed);
        store.MarkCycleHandled(confirmed.CycleKey);
        configuration = current;
        var request = new ExecutorRequest { DataRoot = Paths.Data, ProjectRoot = current.ProjectRoot, Plan = current.Plan, Receipt = confirmed, TestOnly = false };
        Launch(request, execute: true); status = "独立本地 Executor 执行中；无需 AI 额度"; return request.RequestId.ToString("N");
    }

    internal static string Launch(ExecutorRequest request, bool execute)
    {
        var root = Path.Combine(request.DataRoot, "automation", "requests");
        if(!AutomationStore.IsSafeDPath(root))throw new IOException("请求目录不是 D 盘普通目录");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, request.RequestId.ToString("N") + ".json");
        if(!AutomationStore.IsSafeDPath(root) || !AutomationStore.IsSafeDPath(path) || !AutomationStore.IsSafeDPath(path+".bak"))throw new IOException("请求目录含异常链接");
        AtomicStore.Write(path, request);
        // Only request files owned by this program are bounded; never touch user sources.
        foreach (var old in new DirectoryInfo(root).GetFiles("*.json").OrderByDescending(x => x.LastWriteTimeUtc).Skip(10))
            if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(old.Name), "N", out _) && AutomationStore.IsSafeDPath(old.FullName)) old.Delete();
        return IndependentExecutorLauncher.Launch(ExecutorPath, path, execute);
    }

    internal static async Task<ExecutorRunResult?> WaitForResultAsync(Guid id)
    {
        var path = Path.Combine(Paths.Data, "automation", "results", id.ToString("N") + ".json");
        if(!AutomationStore.IsSafeDPath(path) || !AutomationStore.IsSafeDPath(path+".bak"))return null;
        for (var i = 0; i < 180; i++)
        {
            var result = AtomicStore.Read<ExecutorRunResult>(path);
            if (result?.Completed == true && result.RequestId==id) return result;
            await Task.Delay(1000);
        }
        return null;
    }
    public string ReadLastHistory()
    {
        var path=Path.Combine(Paths.Data,"automation","last-run.json");
        if(!AutomationStore.IsSafeDPath(path) || !AutomationStore.IsSafeDPath(path+".bak"))return "执行记录路径异常，未读取";
        var result = AtomicStore.Read<ExecutorRunResult>(path);
        return result is null ? "尚无本地收尾执行记录" : Format(result);
    }
    static string Format(ExecutorRunResult result)
    {
        var text = new StringBuilder(result.DryRun ? "测试模式：未关闭真实程序或请求电源操作\n" : "正式本地收尾记录\n");
        text.AppendLine($"时间：{result.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}").AppendLine(result.Message);
        text.AppendLine($"触发：{result.TriggerReason}；5小时剩余：{result.FiveHourRemaining?.ToString("0.#") ?? "未知"}%");
        foreach (var step in result.Steps)
            text.AppendLine($"{(step.Success ? "✓" : "×")} {ActionPlanParser.Describe(step.Action)}：{step.Message}");
        return text.ToString();
    }
}

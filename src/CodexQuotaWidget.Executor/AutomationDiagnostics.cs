using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using CodexQuotaWidget.Core.Automation;

namespace CodexQuotaWidget.Executor;

/// <summary>Checks existing artifacts only. Deliberately does not call ExecutorEngine.RunAsync or create a recovery snapshot.</summary>
public static class AutomationDiagnostics
{
    public static async Task<AutomationDiagnosticsReport> RunAsync(string dataRoot, string projectRoot, CancellationToken cancellationToken = default)
    {
        if (!AutomationStore.IsSafeDPath(dataRoot) || !AutomationStore.IsSafeDPath(projectRoot))
            throw new InvalidDataException("诊断目录必须是 D 盘普通绝对路径，禁止链接和替代数据流");
        var data = ExecutorPaths.OnD(dataRoot); var project = ExecutorPaths.OnD(projectRoot);
        if (!Directory.Exists(project) || project.Equals(data, StringComparison.OrdinalIgnoreCase)
            || ExecutorPaths.Within(data, project) || ExecutorPaths.Within(project, data))
            throw new InvalidDataException("诊断数据目录必须与现有源码目录分离");
        var startedAt = DateTimeOffset.UtcNow;
        var items = new List<AutomationDiagnosticItem>();
        void Add(string id, string title, DiagnosticsOutcome outcome, string message, params string[] evidence)
            => items.Add(new() { Id = id, Title = title, Outcome = outcome, Message = message, Evidence = evidence });

        Add("executor-start", "执行器诊断入口", DiagnosticsOutcome.RealPassed,
            "独立执行器进程已进入只读诊断模式；不接受执行计划或真实执行开关", $"PID={Environment.ProcessId}");
        Add("executor-independence", "父进程退出后独立执行", DiagnosticsOutcome.NotVerified,
            "本次只读诊断不关闭父进程；需独立隔离 fixture 验证，不等同于执行器已启动");

        var store = new AutomationStore(data);
        var saved = store.LoadConfiguration();
        if (!saved.Success)
            Add("saved-plan", "已保存操作计划", DiagnosticsOutcome.Failed, saved.Message);
        else if (saved.Configuration.Plan is null)
            Add("saved-plan", "已保存操作计划", DiagnosticsOutcome.NotVerified, "尚未保存操作计划；不创建、不启用任何配置");
        else
        {
            var check = ActionPlanParser.Validate(saved.Configuration.Plan);
            Add("saved-plan", "已保存操作计划", check.Success ? DiagnosticsOutcome.RealPassed : DiagnosticsOutcome.Failed,
                check.Success ? $"保存的计划验证通过，共 {saved.Configuration.Plan.Actions.Count} 步；Enabled={saved.Configuration.Enabled}，未执行" : check.Message);
        }
        var example = ActionPlanParser.Parse("关闭Clash Verge和mihomo，然后关机。");
        Add("plan-parser-fixture", "操作计划解析示例", example.Success ? DiagnosticsOutcome.SimulatedPassed : DiagnosticsOutcome.Failed,
            example.Success ? $"合成示例解析为 {example.Plan!.Actions.Count} 步；没有保存或执行" : example.Message);

        await VerifyRecoveryAsync(data, project, items, cancellationToken);
        try
        {
            var targets = await WindowsPlatform.InspectAsync(cancellationToken);
            foreach (var (id, title, prefix) in new[] {
                ("codex-desktop-identity", "Codex Desktop身份", "CodexDesktop:"),
                ("codex-cli-identity", "Codex CLI身份", "CodexCli:"),
                ("clash-identity", "Clash Verge身份", "ClashVerge:"),
                ("mihomo-identity", "mihomo身份", "Mihomo:"),
                ("clash-service-identity", "Clash服务状态", "ClashService:") })
            {
                var found = targets.Where(value => value.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
                var uncertain = found.Any(value => value.Contains("Unverified", StringComparison.Ordinal));
                Add(id, title, uncertain ? DiagnosticsOutcome.Unknown : DiagnosticsOutcome.RealPassed,
                    uncertain ? "存在同名但无法核验身份的进程，不能视为已确认；未操作目标"
                        : found.Length == 0 ? "已真实只读扫描，未发现该目标进程；不是任务状态判断"
                        : "已真实只读查询身份或服务状态；未停止目标", found);
            }
        }
        catch (Exception error) when (IsDiagnosticError(error))
        { Add("target-identities", "应用与服务身份", DiagnosticsOutcome.Unknown, "只读发现无法完整确认：" + SafeReason(error)); }

        Add("quota-judgment", "额度与状态联合判断", DiagnosticsOutcome.NotVerified,
            "本诊断未启动额度RPC或读取session正文；应结合控制中心当前有效额度和真实状态观察，不以进程存在或0%替代任务停止确认");
        Add("process-close", "应用与服务实际关闭", DiagnosticsOutcome.NotVerified,
            "诊断禁止关闭开发工具、Codex、Clash、mihomo或停止服务；只读发现不表示关闭成功");
        Add("windows-power", "Windows电源动作", DiagnosticsOutcome.NotVerified,
            "诊断禁止真实关机、重启、睡眠和休眠；不调用电源适配器");

        var output = Path.Combine(data, "checks", "automation-diagnostics.json");
        if (!AutomationStore.IsSafeDPath(output)) throw new InvalidDataException("诊断报告路径不安全");
        var report = new AutomationDiagnosticsReport { StartedAt = startedAt, FinishedAt = DateTimeOffset.UtcNow, Items = items.ToArray(), ReportPath = output };
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        // A single replaceable report, no recovery/config writes and no historical report backups.
        var staging = output + ".tmp-" + Guid.NewGuid().ToString("N"); ExecutorPaths.NoReparse(staging);
        try
        {
            await File.WriteAllTextAsync(staging, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
            File.Move(staging, output, true);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
        return report;
    }

    static async Task VerifyRecoveryAsync(string data, string project, List<AutomationDiagnosticItem> items, CancellationToken cancellationToken)
    {
        void Add(string id, string title, DiagnosticsOutcome outcome, string message, params string[] evidence)
            => items.Add(new() { Id = id, Title = title, Outcome = outcome, Message = message, Evidence = evidence });
        var note = Path.Combine(data, "codex_resume_state.md");
        if (!AutomationStore.IsSafeDPath(note))
            Add("resume-note", "当前恢复记录", DiagnosticsOutcome.Failed, "恢复记录路径包含异常链接");
        else
            Add("resume-note", "当前恢复记录", File.Exists(note) && new FileInfo(note).Length is >0 and <1024 * 1024
                ? DiagnosticsOutcome.RealPassed : DiagnosticsOutcome.NotVerified,
                File.Exists(note) ? "只读检查现有恢复记录大小，不读取敏感正文；此项不表示完整快照有效" : "恢复记录缺失；诊断不会自建文件冒充验证通过", note);

        var latest = Path.Combine(data, "resume", "latest");
        var required = new[] { "manifest.json", "source.zip", "working-tree.patch", "resume.md" }.Select(value => Path.Combine(latest, value)).ToArray();
        if (required.Any(value => !AutomationStore.IsSafeDPath(value)))
        { Add("recovery-integrity", "完整恢复快照", DiagnosticsOutcome.Failed, "恢复快照包含异常链接"); return; }
        var missing = required.Where(value => !File.Exists(value)).ToArray();
        if (missing.Length > 0)
        { Add("recovery-integrity", "完整恢复快照", DiagnosticsOutcome.NotVerified, "现有快照文件缺失；未生成新快照", missing); return; }
        if (new FileInfo(required[0]).Length > 8 * 1024 * 1024 || new FileInfo(required[1]).Length > 64 * 1024 * 1024
            || new FileInfo(required[2]).Length > 32 * 1024 * 1024 || new FileInfo(required[3]).Length > 1024 * 1024)
        { Add("recovery-integrity", "完整恢复快照", DiagnosticsOutcome.Failed, "恢复文件超过诊断读取限制；未覆盖现有快照"); return; }
        try
        {
            var before = Hash(required[0]);
            var manifest = await ExecutorEngine.VerifyCheckpointAsync(project, data, false, cancellationToken);
            if (before != Hash(required[0]))
            { Add("recovery-integrity", "完整恢复快照", DiagnosticsOutcome.Unknown, "校验期间manifest变化，可能正在保存；本次不能确认"); return; }
            Add("recovery-integrity", "完整恢复快照", DiagnosticsOutcome.RealPassed,
                $"现有归档、清单、patch和快照摘要哈希验证通过，共 {manifest.SourceFiles.Count} 个文件；未改写", required);
            try
            {
                await ExecutorEngine.VerifyCheckpointAsync(project, data, true, cancellationToken);
                Add("recovery-current-source", "快照与当前源码一致性", DiagnosticsOutcome.RealPassed,
                    "清单中的现有源文件与归档一致；最终执行还需重新验证新增关键文件");
            }
            catch (Exception error) when (IsDiagnosticError(error))
            { Add("recovery-current-source", "快照与当前源码一致性", DiagnosticsOutcome.Failed, "未确认当前源码完全保存，危险动作应阻止：" + SafeReason(error)); }
        }
        catch (Exception error) when (IsDiagnosticError(error))
        { Add("recovery-integrity", "完整恢复快照", DiagnosticsOutcome.Failed, "现有快照验证失败，未修改：" + SafeReason(error)); }
    }

    static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
    static bool IsDiagnosticError(Exception error) => error is IOException or InvalidDataException or JsonException
        or UnauthorizedAccessException or ArgumentException or InvalidOperationException or Win32Exception;
    static string SafeReason(Exception error) => error is InvalidDataException ? error.Message : error.GetType().Name;
}

using System.Security.Cryptography;
using System.Text.Json;
using CodexQuotaWidget.Core.Automation;

namespace CodexQuotaWidget.Core.ControlCenter;

/// <summary>Read-only evidence validation. No build, shell, model or sampling script is executed.</summary>
public sealed class WorkflowReportProjectValidator : IWorkflowProjectValidator
{
    readonly string root, report;
    readonly Dictionary<string, string> evidence;
    public WorkflowReportProjectValidator(string projectRoot, string reportPath)
    {
        root = Path.GetFullPath(projectRoot); report = Path.GetFullPath(reportPath);
        _ = ReadReport(root, report);
        evidence = Capture();
    }
    public static bool IsReportPath(string projectRoot, string reportPath)
    {
        try
        {
            return AutomationStore.IsSafeDPath(projectRoot) && AutomationStore.IsSafeDPath(reportPath)
                && Path.GetFullPath(reportPath).StartsWith(Path.GetFullPath(projectRoot).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
                && Path.GetExtension(reportPath).Equals(".md", StringComparison.OrdinalIgnoreCase)
                && File.Exists(reportPath) && new FileInfo(reportPath).Length is > 0 and <= 64000;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }
    public static string ReadReport(string projectRoot, string reportPath)
    {
        if (!IsReportPath(projectRoot, reportPath)) throw new InvalidDataException("既有报告必须位于指定D盘项目内。");
        var text = File.ReadAllText(reportPath);
        if (string.IsNullOrWhiteSpace(text) || text.Length > 12000 || WorkflowText.HasSecret(text))
            throw new InvalidDataException("既有报告为空、超过完整转交长度或含敏感内容；不截断或发送。");
        return text;
    }
    Dictionary<string, string> Capture()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in new[] { report }.Concat(new[] { "samples.csv", "summary.json", "gpu_start.txt", "gpu_end.txt" }
            .Select(name => Path.Combine(Path.GetDirectoryName(report)!, name))))
        {
            if (!AutomationStore.IsSafeDPath(path) || !File.Exists(path) || new FileInfo(path).Length is <= 0 or > 1024 * 1024)
                throw new InvalidDataException("报告证据缺失或路径/大小异常。");
            result.Add(Path.GetRelativePath(root, path), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        }
        return result;
    }
    public Task<WorkflowValidationResult> ValidateAsync(string projectRoot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (!root.Equals(Path.GetFullPath(projectRoot), StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(new WorkflowValidationResult(false, "报告验证未绑定该项目。"));
            var current = Capture();
            if (!evidence.All(pair => current.TryGetValue(pair.Key, out var hash) && hash == pair.Value))
                return Task.FromResult(new WorkflowValidationResult(false, "原始报告或证据已改变；未运行任何程序。"));
            var directory = Path.GetDirectoryName(report)!;
            using var summary = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "summary.json")));
            var rows = File.ReadLines(Path.Combine(directory, "samples.csv")).Count(line => !string.IsNullOrWhiteSpace(line)) - 1;
            if (!summary.RootElement.TryGetProperty("sample_count", out var count) || !count.TryGetInt32(out var expected)
                || rows < 1 || expected != rows)
                return Task.FromResult(new WorkflowValidationResult(false, "统计JSON与CSV记录数不一致；不发送审核。"));
            var message = $"只读验证：报告可完整读取；summary.json可解析；CSV有{rows}条数据，与sample_count一致。\n"
                + string.Join('\n', current.Select(pair => pair.Key + " SHA256=" + pair.Value))
                + "\n范围限制：只核对上述既有文件、记录数及SHA256，不证明报告统计正确、后续草稿正确或GPU推理可行。未启动编译、采样脚本或模型。";
            return Task.FromResult(new WorkflowValidationResult(true, message));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException)
        { return Task.FromResult(new WorkflowValidationResult(false, "只读报告验证失败：" + error.GetType().Name)); }
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexQuotaWidget.Core.ControlCenter;

public sealed record WorkflowAdvicePlan(Guid TaskId, Guid RequestId, string ProjectRoot, string AuthorizationId,
    string Instructions, string[] WritePaths, bool RequiresConfirmation);

/// <summary>Untrusted model advice must match caller-frozen authority before any Codex turn.</summary>
public static class WorkflowAdviceProtocol
{
    public static WorkflowAdvicePlan Parse(string text, WorkflowExecutionRequest request)
    {
        var line = text.Split('\n').Select(item => item.Trim()).FirstOrDefault(item => item.Length > 0) ?? "";
        const string prefix = "ACC_ADVICE: ";
        if (!line.StartsWith(prefix, StringComparison.Ordinal) || line.Length > 12000) throw new InvalidDataException("GPT建议缺少严格的授权计划协议，禁止派发。");
        using var document = JsonDocument.Parse(line[prefix.Length..]);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("GPT计划不是对象。");
        var names = document.RootElement.EnumerateObject().Select(item => item.Name).ToArray();
        string[] expected = ["TaskId", "RequestId", "ProjectRoot", "AuthorizationId", "Instructions", "WritePaths", "RequiresConfirmation"];
        if (names.Length != expected.Length || names.Distinct(StringComparer.Ordinal).Count() != names.Length || expected.Any(item => !names.Contains(item)))
            throw new InvalidDataException("GPT计划字段缺失、重复或包含额外授权。");
        var plan = document.RootElement.Deserialize<WorkflowAdvicePlan>() ?? throw new InvalidDataException("GPT计划不可读。");
        if (plan.TaskId != request.WorkflowTaskId || plan.RequestId != request.RequestId
            || !WorkflowAuthorization.SameProject(plan.ProjectRoot, request.AuthorizedProjectRoot) || plan.AuthorizationId != request.AuthorizationId)
            throw new InvalidDataException("GPT计划的任务、项目或授权身份不匹配。");
        if (plan.RequiresConfirmation) throw new InvalidDataException("GPT步骤需要用户确认，自动执行已阻断。");
        if (string.IsNullOrWhiteSpace(plan.Instructions) || plan.Instructions.Length > 3000 || WorkflowText.HasSecret(plan.Instructions))
            throw new InvalidDataException("GPT步骤内容无效，禁止自动派发。");
        var paths = WorkflowAuthorization.NormalizeWritePaths(plan.WritePaths ?? []);
        var approved = WorkflowAuthorization.NormalizeWritePaths(request.AuthorizedWritePaths);
        if (paths.Length == 0 || paths.Any(path => !WorkflowAuthorization.AllowsWrite(approved, path)))
            throw new InvalidDataException("GPT步骤试图修改未授权文件，禁止派发。");
        return plan with { WritePaths = paths };
    }
    public static string Format(WorkflowExecutionRequest request, string instructions, string[]? paths = null, bool requiresConfirmation = false)
        => "ACC_ADVICE: " + JsonSerializer.Serialize(new WorkflowAdvicePlan(request.WorkflowTaskId, request.RequestId,
            request.AuthorizedProjectRoot, request.AuthorizationId, instructions, paths ?? request.AuthorizedWritePaths, requiresConfirmation));
    public static string Fingerprint(WorkflowAdvicePlan plan)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plan.Instructions.Trim() + "\n" + string.Join('\n', plan.WritePaths.Select(item => item.ToUpperInvariant())))));
}

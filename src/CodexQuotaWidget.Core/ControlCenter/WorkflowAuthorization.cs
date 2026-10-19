using System.Security.Cryptography;
using System.Text;
using CodexQuotaWidget.Core.Automation;

namespace CodexQuotaWidget.Core.ControlCenter;

public sealed record WorkflowAuthorizationBinding(Guid WorkflowTaskId, string ProjectRoot, string AuthorizationId,
    string[] AuthorizedWritePaths, string WriteScopeHash, bool ReadOnly);

/// <summary>Caller-frozen authority. Advisor text and provider replies cannot create or expand it.</summary>
public static class WorkflowAuthorization
{
    public const int MaximumWritePaths = 128;

    // A trailing slash grants a directory subtree. Files must otherwise match exactly.
    // Project-wide wildcards and the project root itself are never write grants.
    public static string[] NormalizeWritePaths(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var count = 0;
        foreach (var value in paths)
        {
            if (++count > MaximumWritePaths || string.IsNullOrWhiteSpace(value) || value.Length > 512
                || value.Any(char.IsControl) || value.IndexOfAny([':', '*', '?', '\0']) >= 0 || Path.IsPathRooted(value))
                throw new WorkflowRpcException("authorization_scope_invalid");
            var normalized = value.Replace('\\', '/');
            var directory = normalized.EndsWith('/');
            var parts = (directory ? normalized[..^1] : normalized).Split('/');
            if (parts.Any(part => part.Length == 0 || part is "." or ".." || part.Trim() != part || part.EndsWith('.')
                || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || ProtectedPart(part) || ReservedDeviceName(part)))
                throw new WorkflowRpcException("authorization_scope_invalid");
            result.Add(string.Join('/', parts) + (directory ? "/" : ""));
        }
        return result.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static string ComputeWriteScopeHash(IEnumerable<string> paths)
        => Hash(string.Join('\n', NormalizeWritePaths(paths).Select(path => path.ToUpperInvariant())));

    public static string CreateAuthorizationId(Guid taskId, string projectRoot, string frozenGoal, IEnumerable<string> paths, bool readOnly = false)
    {
        if (taskId == Guid.Empty || string.IsNullOrWhiteSpace(frozenGoal)) throw new WorkflowRpcException("authorization_missing");
        var root = NormalizeProjectRoot(projectRoot);
        var normalized = NormalizeWritePaths(paths);
        if (!readOnly && normalized.Length == 0 || readOnly && normalized.Length > 0) throw new WorkflowRpcException("authorization_scope_invalid");
        return Hash("workflow-authority-v1\n" + taskId.ToString("D") + "\n" + root.ToUpperInvariant() + "\n"
            + Hash(frozenGoal) + "\n" + (readOnly ? "readOnly" : "write") + "\n" + ComputeWriteScopeHash(normalized));
    }

    public static WorkflowAuthorizationBinding ValidateRequest(WorkflowExecutionRequest request, string connectorProjectRoot)
    {
        if (request.WorkflowTaskId == Guid.Empty || string.IsNullOrWhiteSpace(request.AuthorizedProjectRoot)
            || string.IsNullOrWhiteSpace(request.AuthorizationId)) throw new WorkflowRpcException("authorization_missing");
        var root = NormalizeProjectRoot(request.AuthorizedProjectRoot);
        if (!SameProject(root, connectorProjectRoot)) throw new WorkflowRpcException("authorization_project_mismatch");
        if (request.AuthorizationId.Length != 64 || !request.AuthorizationId.All(Uri.IsHexDigit)) throw new WorkflowRpcException("authorization_identity_invalid");
        var paths = NormalizeWritePaths(request.AuthorizedWritePaths ?? []);
        if (!request.ReadOnly && paths.Length == 0 || request.ReadOnly && paths.Length > 0) throw new WorkflowRpcException("authorization_scope_invalid");
        return new(request.WorkflowTaskId, root, request.AuthorizationId.ToUpperInvariant(), paths, ComputeWriteScopeHash(paths), request.ReadOnly);
    }

    public static void ValidateContinuation(WorkflowAuthorizationBinding recorded, WorkflowAuthorizationBinding requested)
    {
        if (recorded.WorkflowTaskId != requested.WorkflowTaskId) throw new WorkflowRpcException("foreign_workflow_continuation_rejected");
        if (!SameProject(recorded.ProjectRoot, requested.ProjectRoot)) throw new WorkflowRpcException("session_project_binding_mismatch");
        if (recorded.AuthorizationId != requested.AuthorizationId || recorded.WriteScopeHash != requested.WriteScopeHash || recorded.ReadOnly != requested.ReadOnly)
            throw new WorkflowRpcException("session_authorization_binding_mismatch");
    }

    public static string[] ValidateStepWritePaths(WorkflowAuthorizationBinding binding, WorkflowExecutionRequest request)
    {
        var paths = NormalizeWritePaths(request.StepWritePaths ?? []);
        if (binding.ReadOnly)
        {
            if (paths.Length > 0) throw new WorkflowRpcException("authorization_step_scope_invalid");
        }
        else if (paths.Length == 0 || paths.Any(path => !AllowsWrite(binding.AuthorizedWritePaths, path)))
            throw new WorkflowRpcException("authorization_step_scope_invalid");
        return paths;
    }

    public static bool AllowsWrite(IReadOnlyList<string> normalizedPaths, string relative)
    {
        var candidate = relative.Replace('\\', '/');
        return normalizedPaths.Any(path => path.EndsWith('/') ? candidate.StartsWith(path, StringComparison.OrdinalIgnoreCase)
            : candidate.Equals(path, StringComparison.OrdinalIgnoreCase));
    }

    public static bool SameProject(string? first, string? second)
    {
        try { return first is not null && second is not null && NormalizeProjectRoot(first).Equals(NormalizeProjectRoot(second), StringComparison.OrdinalIgnoreCase); }
        catch (Exception error) when (error is WorkflowRpcException or ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { return false; }
    }

    public static string NormalizeProjectRoot(string root)
    {
        if (!AutomationStore.IsSafeDPath(root)) throw new WorkflowRpcException("authorization_project_invalid");
        return Path.GetFullPath(root).TrimEnd('\\', '/');
    }

    static bool ProtectedPart(string part) => part.Equals("AGENTS.md", StringComparison.OrdinalIgnoreCase)
        || new[] { ".git", ".codex", ".acc-workflow", "bin", "obj", "artifacts", "publish", "backups", "logs", "node_modules", "auth", "env", "credentials.json", "secrets.json", "config.toml" }.Contains(part, StringComparer.OrdinalIgnoreCase)
        || part.Equals(".env", StringComparison.OrdinalIgnoreCase) || part.StartsWith(".env.", StringComparison.OrdinalIgnoreCase)
        || part.StartsWith("auth.", StringComparison.OrdinalIgnoreCase) || part.StartsWith("env.", StringComparison.OrdinalIgnoreCase);
    static bool ReservedDeviceName(string name)
    {
        var stem = name.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
            || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
                && (stem[3] is >= '1' and <= '9' or '¹' or '²' or '³');
    }
    static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

using System.Diagnostics;
using System.Security.Cryptography;
using CodexQuotaWidget.Core.Automation;

namespace CodexQuotaWidget.Core.ControlCenter;

/// <summary>Fixed local commands only; never runs a command obtained from an advisor response.</summary>
public sealed class WorkflowProjectValidator(string dataRoot, string dotnetPath, string buildProjectRelative, string? checksProjectRelative = null) : IWorkflowProjectValidator
{
    string? trustedRoot;
    Dictionary<string, string>? definitions, sources;
    public void FreezeTrustedProject(string projectRoot)
    {
        if (!AutomationStore.IsSafeDPath(projectRoot)) throw new InvalidDataException("检查项目路径异常。");
        trustedRoot = Path.GetFullPath(projectRoot);
        definitions = Capture(trustedRoot, definition: true); sources = Capture(trustedRoot, definition: false);
    }
    public async Task<WorkflowValidationResult> ValidateAsync(string projectRoot, CancellationToken cancellationToken)
    {
        try
        {
            if (!WorkflowAuthorization.SameProject(trustedRoot, projectRoot) || definitions is null || sources is null
                || !Same(definitions, Capture(projectRoot, definition: true)))
                return new(false, "构建定义未绑定或发生变化，未运行宿主命令。");
            if (!AutomationStore.IsSafeDPath(dataRoot) || !AutomationStore.IsSafeDPath(dotnetPath) || !File.Exists(dotnetPath)
                || !Path.GetFileName(dotnetPath).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
                return new(false, "验证程序路径未核实，未运行命令。");
            var build = ProjectPath(projectRoot, buildProjectRelative);
            var messages = new List<string>();
            var output = await RunAsync(projectRoot, ["build", build, "-c", "Release", "--no-restore"], cancellationToken).ConfigureAwait(false);
            messages.Add("Release编译：" + (output.ExitCode == 0 ? "真实通过" : "失败") + "；" + output.Message);
            if (output.ExitCode != 0) return new(false, string.Join('\n', messages));
            if (!string.IsNullOrWhiteSpace(checksProjectRelative))
            {
                var currentSources = Capture(projectRoot, definition: false);
                if (!Same(sources, currentSources))
                    return new(false, string.Join('\n', messages) + "\n修改后的程序尚未在受限环境验收；不以宿主权限自动运行模型修改的代码。");
                var checks = ProjectPath(projectRoot, checksProjectRelative);
                if (!checks.Equals(build, StringComparison.OrdinalIgnoreCase))
                {
                    output = await RunAsync(projectRoot, ["build", checks, "-c", "Release", "--no-restore"], cancellationToken).ConfigureAwait(false);
                    messages.Add("检查项目编译：" + (output.ExitCode == 0 ? "真实通过" : "失败"));
                    if (output.ExitCode != 0) return new(false, string.Join('\n', messages));
                }
                output = await RunAsync(projectRoot, ["run", "--project", checks, "-c", "Release", "--no-build", "--no-restore"], cancellationToken).ConfigureAwait(false);
                messages.Add("固定检查运行：" + (output.ExitCode == 0 ? "真实通过" : "失败") + "；" + output.Message);
                if (output.ExitCode != 0) return new(false, string.Join('\n', messages));
            }
            return new(true, string.Join('\n', messages) + "\n只证明这些本地命令成功，不证明真实关机、外部Agent或所有AI状态已验收。");
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ArgumentException or TimeoutException)
        { return new(false, "固定验证无法完成：" + error.GetType().Name); }
    }
    static bool Same(Dictionary<string, string> first, Dictionary<string, string> second)
        => first.Count == second.Count && first.All(pair => second.TryGetValue(pair.Key, out var hash) && hash == pair.Value);
    static Dictionary<string, string> Capture(string root, bool definition)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>(); pending.Enqueue(root);
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".git", ".codex", "bin", "obj", "artifacts", "publish", ".acc-workflow", "backups", "logs", "node_modules" };
        int visited = 0;
        while (pending.Count > 0)
        {
            var directory = pending.Dequeue();
            if (++visited > 1000 || !AutomationStore.IsSafeDPath(directory)) throw new InvalidDataException("验收扫描范围异常。");
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (excluded.Contains(Path.GetFileName(entry))) continue;
                if (!AutomationStore.IsSafeDPath(entry)) throw new InvalidDataException("验收文件路径异常。");
                if (Directory.Exists(entry)) { pending.Enqueue(entry); continue; }
                var extension = Path.GetExtension(entry).ToLowerInvariant();
                bool include = definition ? extension is ".csproj" or ".props" or ".targets" or ".json" or ".config" or ".sln" : extension == ".cs";
                if (!include) continue;
                if (result.Count >= 5000 || new FileInfo(entry).Length > 8 * 1024 * 1024) throw new InvalidDataException("验收文件过大。");
                result.Add(Path.GetRelativePath(root, entry), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(entry))));
            }
        }
        return result;
    }
    static string ProjectPath(string root, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (Path.IsPathRooted(relative) || !full.StartsWith(Path.GetFullPath(root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
            || !AutomationStore.IsSafeDPath(full) || !File.Exists(full) || Path.GetExtension(full) != ".csproj")
            throw new InvalidDataException("固定检查项目不在授权目录内。");
        return full;
    }
    async Task<(int ExitCode, string Message)> RunAsync(string projectRoot, string[] arguments, CancellationToken token)
    {
        var temp = Path.Combine(dataRoot, "temp");
        var logs = Path.Combine(dataRoot, "workflow", "logs");
        if (!AutomationStore.IsSafeDPath(temp) || !AutomationStore.IsSafeDPath(logs)) throw new InvalidDataException("验证目录异常。");
        Directory.CreateDirectory(temp); Directory.CreateDirectory(logs);
        var info = new ProcessStartInfo(dotnetPath) { WorkingDirectory = projectRoot, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        info.Environment["TEMP"] = temp; info.Environment["TMP"] = temp;
        info.Environment["DOTNET_CLI_HOME"] = Path.Combine(dataRoot, "sdk-home");
        info.Environment["NUGET_PACKAGES"] = Path.Combine(dataRoot, "nuget-packages");
        info.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = Path.Combine(dataRoot, "runtime-cache");
        using var job = new ProcessJob();
        using var process = Process.Start(info) ?? throw new IOException("验证进程未启动。");
        try { job.Assign(process); }
        catch { try { process.Kill(entireProcessTree: true); } catch { } throw; }
        var stdout = DrainAsync(process.StandardOutput); var stderr = DrainAsync(process.StandardError);
        // The job contains only this newly started validation command, never existing user tools.
        // Timeout/cancellation disposes the job before another run can enter the workspace.
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(10), token).ConfigureAwait(false);
        var transcript = WorkflowText.Safe((await stdout.ConfigureAwait(false)) + "\n" + (await stderr.ConfigureAwait(false)), 24000);
        var label = arguments[0] == "run" ? "checks" : "build";
        var logPath = Path.Combine(logs, "latest-" + label + ".log");
        if (!AutomationStore.IsSafeDPath(logPath)) throw new InvalidDataException("验证日志路径异常。");
        File.WriteAllText(logPath, transcript);
        token.ThrowIfCancellationRequested();
        var summary = transcript.Split('\n').Where(line => line.StartsWith("PASS ", StringComparison.Ordinal)
            || line.Contains("Build succeeded", StringComparison.Ordinal) || line.Contains("生成成功", StringComparison.Ordinal)).TakeLast(2);
        return (process.ExitCode, string.Join("；", summary.DefaultIfEmpty("退出码 " + process.ExitCode)));
    }
    static async Task<string> DrainAsync(StreamReader reader)
    {
        var result = new System.Text.StringBuilder();
        var buffer = new char[4096]; int length;
        while ((length = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            if (result.Length < 24000) result.Append(buffer, 0, Math.Min(length, 24000 - result.Length));
        return result.ToString();
    }
}

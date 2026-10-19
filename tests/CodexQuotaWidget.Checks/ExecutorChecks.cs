using System.Diagnostics;
using System.Text.Json;

/// <summary>Starts the separate executable; all its fixture actions are injected and never touch user processes/power.</summary>
public static class ExecutorChecks
{
    public static async Task<int> RunAsync(string executable)
    {
        if (!File.Exists(executable)) throw new InvalidOperationException("独立 Executor 可执行文件不存在");
        var root = Path.Combine(@"D:\CodexQuotaWidget-GitHub\artifacts\executor-checks", Guid.NewGuid().ToString("N"));
        var run = await StartAsync(executable, root, "--self-test", "--fixture-root", root);
        using var output = JsonDocument.Parse(run.Output);
        var result = output.RootElement;
        if (run.Code != 0 || !result.GetProperty("Success").GetBoolean() || !result.GetProperty("IndependentOfCodexAndChatGpt").GetBoolean()
            || !result.GetProperty("PowerSimulated").GetBoolean()) throw new InvalidOperationException("独立 Executor 子进程自检失败：" + run.Output);
        var checks = result.GetProperty("Checks").GetInt32();
        var rejected = await StartAsync(executable, root, "--inspect", "--execute");
        if (rejected.Code == 0) throw new InvalidOperationException("检查模式不得接受真实执行开关");
        var unknown = await StartAsync(executable, root, "--command", "shutdown");
        if (unknown.Code == 0) throw new InvalidOperationException("任意命令必须拒绝");
        return checks + 2;
    }

    static async Task<(int Code, string Output)> StartAsync(string executable, string root, params string[] arguments)
    {
        var runnerRoot = root + "-runner";
        var temp = Path.Combine(runnerRoot, "temp"); Directory.CreateDirectory(temp);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["TEMP"] = temp; start.Environment["TMP"] = temp;
        start.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = Path.Combine(runnerRoot, "runtime-cache");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("独立 Executor 子进程未启动");
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90)); }
        catch { if (!process.HasExited) process.Kill(); throw; }
        var text = await output; var errors = await error;
        if (!string.IsNullOrWhiteSpace(errors)) throw new InvalidOperationException("Executor 子进程 stderr：" + errors);
        return (process.ExitCode, text);
    }
}

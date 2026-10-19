using System.Text.Json;
using System.Text.Json.Serialization;
using CodexQuotaWidget.Core.Automation;
using CodexQuotaWidget.Executor;

var realExecutionWasEntered = false;
try
{
    var switches = new HashSet<string>(["--execute", "--inspect", "--self-test", "--diagnostics", "--manual-acceptance-test", "--manual-acceptance-self-test"], StringComparer.Ordinal);
    var values = new HashSet<string>(["--request", "--fixture-root", "--delay", "--data-dir", "--project-root"], StringComparer.Ordinal);
    var parsed = new Dictionary<string, string?>(StringComparer.Ordinal);
    for (var i = 0; i < args.Length; i++)
    {
        var key = args[i];
        if (parsed.ContainsKey(key) || !switches.Contains(key) && !values.Contains(key)) throw new InvalidDataException("执行器参数无效");
        if (switches.Contains(key)) parsed[key] = null;
        else
        {
            if (++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal)) throw new InvalidDataException("执行器参数缺少值");
            parsed[key] = args[i];
        }
    }
    if (parsed.ContainsKey("--manual-acceptance-self-test"))
    {
        if (!parsed.TryGetValue("--fixture-root", out var manualFixture) || manualFixture is null
            || parsed.Keys.Any(key => key is not ("--manual-acceptance-self-test" or "--fixture-root")))
            throw new InvalidDataException("单次验收自检只接受独立 D 盘 fixture-root，禁止真实执行参数");
        var manualChecks = await ManualAcceptanceRunner.RunChecksAsync(manualFixture);
        Console.WriteLine(JsonSerializer.Serialize(manualChecks, new JsonSerializerOptions { WriteIndented = true }));
        Environment.ExitCode = manualChecks.Success ? 0 : 1;
        return;
    }
    if (parsed.ContainsKey("--manual-acceptance-test"))
    {
        if (!parsed.TryGetValue("--request", out var manualPath) || manualPath is null
            || parsed.Keys.Any(key => key is not ("--manual-acceptance-test" or "--request" or "--execute")))
            throw new InvalidDataException("单次实机验收只接受固定 request 和可选 execute");
        if (!AutomationStore.IsSafeDPath(manualPath)) throw new InvalidDataException("验收请求必须位于 D 盘普通路径");
        var manualFull = Path.GetFullPath(manualPath);
        if (!File.Exists(manualFull) || new FileInfo(manualFull).Length > ManualAcceptanceRules.MaximumRequestBytes)
            throw new InvalidDataException("单次验收请求缺失或超过 32KB");
        var manual = JsonSerializer.Deserialize<ManualAcceptanceRequest>(await File.ReadAllTextAsync(manualFull),
            new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })
            ?? throw new InvalidDataException("单次实机验收请求为空");
        if (!AutomationStore.IsSafeDPath(manual.DataRoot)
            || !manualFull.Equals(Path.GetFullPath(ManualAcceptanceRules.RequestPath(manual)), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("验收请求必须位于 manual-tests/requests 的唯一 TestId 入口");
        var manualTemp = Path.Combine(manual.DataRoot, "temp");
        if (!AutomationStore.IsSafeDPath(manualTemp)) throw new InvalidDataException("验收临时目录路径异常");
        Directory.CreateDirectory(manualTemp);
        Environment.SetEnvironmentVariable("TEMP", manualTemp); Environment.SetEnvironmentVariable("TMP", manualTemp);
        Environment.SetEnvironmentVariable("CODEX_QUOTA_WIDGET_DATA", manual.DataRoot);
        Environment.SetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR", Path.Combine(manual.DataRoot, "runtime-cache"));
        realExecutionWasEntered = parsed.ContainsKey("--execute");
        var manualResult = await new ManualAcceptanceRunner().RunAsync(manual, !realExecutionWasEntered);
        Console.WriteLine(JsonSerializer.Serialize(manualResult, new JsonSerializerOptions { WriteIndented = true }));
        Environment.ExitCode = manualResult.Success ? 0 : 1;
        return;
    }
    if (parsed.ContainsKey("--diagnostics"))
    {
        if (!parsed.TryGetValue("--data-dir", out var data) || data is null
            || !parsed.TryGetValue("--project-root", out var project) || project is null
            || parsed.Keys.Any(x => x is not ("--diagnostics" or "--data-dir" or "--project-root")))
            throw new InvalidDataException("诊断只接受 D 盘 data-dir/project-root，禁止附加真实执行参数");
        var diagnostic = await AutomationDiagnostics.RunAsync(data, project);
        Console.WriteLine(JsonSerializer.Serialize(diagnostic, new JsonSerializerOptions { WriteIndented = true }));
        Environment.ExitCode = diagnostic.Failed == 0 ? 0 : 1;
        return;
    }
    if (parsed.ContainsKey("--inspect"))
    {
        if (parsed.Count != 1) throw new InvalidDataException("检查模式不接受执行参数");
        Console.WriteLine(JsonSerializer.Serialize(new { NoProcessesClosed = true, NoPowerAction = true, Targets = await WindowsPlatform.InspectAsync() }, new JsonSerializerOptions { WriteIndented = true }));
        return;
    }
    if (parsed.ContainsKey("--self-test"))
    {
        if (!parsed.TryGetValue("--fixture-root", out var fixture) || fixture is null || parsed.Keys.Any(x => x is not ("--self-test" or "--fixture-root" or "--delay")))
            throw new InvalidDataException("自检需要 D 盘独立 fixture-root；禁止真实执行参数");
        var delay = parsed.TryGetValue("--delay", out var delayText) && int.TryParse(delayText, out var seconds) ? seconds : 0;
        if (delay is <0 or >15) throw new InvalidDataException("自检延迟必须为 0 至 15 秒");
        var result = await ExecutorSelfTest.RunAsync(fixture, delay);
        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Environment.ExitCode = result.Success ? 0 : 1;
        return;
    }
    if (!parsed.TryGetValue("--request", out var requestPath) || requestPath is null || parsed.Keys.Any(x => x is not ("--request" or "--execute")))
        throw new InvalidDataException("请使用 --request 指定保存的本地请求；默认只测试");
    var full = ExecutorPaths.OnD(requestPath);
    if (!File.Exists(full) || new FileInfo(full).Length > 256 * 1024) throw new InvalidDataException("请求文件不存在或过大");
    var request = JsonSerializer.Deserialize<ExecutorRequest>(await File.ReadAllTextAsync(full), new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })
        ?? throw new InvalidDataException("请求文件无效");
    var expected = Path.Combine(ExecutorPaths.OnD(request.DataRoot), "automation", "requests", $"{request.RequestId:N}.json");
    if (!full.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("请求必须位于本机数据目录的固定请求入口");
    var temp = Path.Combine(request.DataRoot, "temp"); ExecutorPaths.NoReparse(temp); Directory.CreateDirectory(temp);
    Environment.SetEnvironmentVariable("TEMP", temp); Environment.SetEnvironmentVariable("TMP", temp);
    Environment.SetEnvironmentVariable("CODEX_QUOTA_WIDGET_DATA", request.DataRoot);
    Environment.SetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR", Path.Combine(request.DataRoot, "runtime-cache"));
    realExecutionWasEntered = parsed.ContainsKey("--execute");
    var run = await new ExecutorEngine().RunAsync(request, !realExecutionWasEntered);
    Console.WriteLine(JsonSerializer.Serialize(run, new JsonSerializerOptions { WriteIndented = true }));
    Environment.ExitCode = run.Success ? 0 : 1;
}
catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or ArgumentException)
{
    Console.WriteLine(JsonSerializer.Serialize(new { Success = false, NoPowerAction = !realExecutionWasEntered,
        RealExecutionEntryReached = realExecutionWasEntered, Reason = error is InvalidDataException ? error.Message : error.GetType().Name }));
    Environment.ExitCode = 1;
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using CodexQuotaWidget.Core;
using CodexQuotaWidget.Core.Automation;
using CodexQuotaWidget.Core.ControlCenter;

namespace CodexQuotaWidget;

/// <summary>One-shot product-owned live exercise. It never supplies or substitutes either model's answer.</summary>
internal static class WorkflowStabilityLiveCheckRunner
{
    const string ArtifactParent = @"D:\CodexQuotaWidget-GitHub\artifacts\workflow-stability-20261014-201634";
    const string ProductionData = @"D:\CodexQuotaWidgetData";
    const string AuthenticationHome = @"D:\CodexQuotaWidgetData\codex-home";
    const string Dotnet = @"D:\codex\Tools\dotnet-10.0.401\dotnet.exe";
    const string ProjectName = "WorkflowLiveLabelCheck.csproj";
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static async Task<int> RunAsync(string[] args)
    {
        bool connectionOnly = args.Contains("--workflow-stability-connection-check");
        string prefix = connectionOnly ? "probe-" : "live-";
        string root;
        try
        {
            var index = Array.IndexOf(args, "--workflow-stability-live-root");
            if (index >= 0 && index + 1 >= args.Length) return 2;
            root = Path.GetFullPath(index >= 0 ? args[index + 1] : Path.Combine(ArtifactParent, prefix + Guid.NewGuid().ToString("N")));
            var name = Path.GetFileName(root.TrimEnd('\\'));
            if (!AutomationStore.IsSafeDPath(root) || !string.Equals(Path.GetDirectoryName(root), ArtifactParent, StringComparison.OrdinalIgnoreCase)
                || !name.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParse(name[prefix.Length..], out _)) return 2;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException) { return 2; }

        var project = Path.Combine(root, "project"); var data = Path.Combine(root, "data"); var logs = Path.Combine(root, "logs");
        var report = new LiveReport { ArtifactRoot = root, TaskId = Guid.NewGuid(), StartedAt = DateTimeOffset.UtcNow, ConnectionOnly = connectionOnly };
        WorkflowLoopRuntime? runtime = null;
        bool cleaningUp = false;
        LabelValidator? validator = null;
        bool claimed = false;
        try
        {
            // Persistent CreateNew claim survives crashes. A second invocation may inspect evidence, never dispatch again.
            Directory.CreateDirectory(root);
            if (Directory.Exists(project) || Directory.Exists(data) || Directory.Exists(logs))
            { Console.WriteLine("该独立验收目录已有内容；保留结果，不重复派发。"); return 2; }
            using (var claim = new FileStream(Path.Combine(root, "invocation.json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                JsonSerializer.Serialize(claim, new { report.ProductOwned, report.ProcessId, report.TaskId, report.StartedAt, OneShot = true }, Json);
            claimed = true;
            Directory.CreateDirectory(project); Directory.CreateDirectory(data); Directory.CreateDirectory(logs);
            WriteJson(Path.Combine(root, "recovery.json"), new { report.TaskId, report.ProcessId, State = "ClaimedBeforeConnection", AutomaticReplayAllowed = false });
            WriteReport();
            report.ProductionProtectedBefore = CaptureProductionProtected();
            // Only these two explicitly saved identities are copied. The production Goal is never read into an outgoing request or report.
            var saved = WorkflowLoopRuntime.ReadConfiguration(ProductionData);
            if (saved is null || !Guid.TryParse(saved.ChatGptThreadId, out _) || !Guid.TryParse(saved.CallerThreadId, out _))
                throw new InvalidDataException("生产设置没有明确保存两个可核验的会话ID。");
            var target = saved.ChatGptThreadId; var caller = saved.CallerThreadId;
            saved = null;
            report.GptTargetThreadId = target; report.CallerThreadId = caller;
            if (!File.Exists(Dotnet) || !AutomationStore.IsSafeDPath(AuthenticationHome) || !Directory.Exists(AuthenticationHome))
                throw new InvalidDataException("已有D盘SDK或官方认证目录不可用。");
            validator = new LabelValidator(project, data, logs);
            validator.CreateFrozenProject();
            var config = new WorkflowLoopConfiguration
            {
                TaskId = report.TaskId, ProjectRoot = project, ChatGptThreadId = target, CallerThreadId = caller,
                Goal = Goal, AuthorizedWritePaths = ["LabelNormalizer.cs"], BuildProjectPath = ProjectName, ChecksProjectPath = ProjectName,
                MaximumRounds = 2, MaximumRunMinutes = 25, MinimumRemainingPercent = 20, LocalExecutionAuthorized = true
            };
            if (!WorkflowLoopService.ValidConfiguration(config)) throw new InvalidDataException("新隔离任务身份或范围校验失败。");
            WriteJson(Path.Combine(root, "task.json"), config with { LocalExecutionAuthorized = false });
            runtime = new WorkflowLoopRuntime(data, config, validator, AuthenticationHome);
            runtime.Changed += () =>
            {
                if (cleaningUp) return;
                var state = runtime.State;
                lock (report)
                {
                    report.Phase = state.Phase.ToString(); report.Round = state.Round; report.CompletedRounds = state.CompletedRounds;
                    report.CurrentRequestId = state.CurrentRequestId; report.NeedsRecoveryConfirmation = state.NeedsRecoveryConfirmation;
                    report.CodexSessionId = state.CodexSessionId;
                    report.GptConnection = runtime.GptConnection; report.CodexConnection = runtime.CodexConnection;
                    report.Message = WorkflowText.Safe(state.Message, 800);
                    File.AppendAllText(Path.Combine(logs, "progress.jsonl"), JsonSerializer.Serialize(new
                    {
                        At = DateTimeOffset.UtcNow, state.RunId, state.Phase, state.Round, state.CompletedRounds, state.Checkpoint,
                        state.CurrentRequestId, state.PendingProvider, state.NeedsRecoveryConfirmation, Message = WorkflowText.Safe(state.Message, 800)
                    }) + Environment.NewLine);
                    WriteReport();
                }
            };
            report.ConnectionProbe = "Started"; WriteReport();
            // Read-only probes use the runtime's own official connectors; no root-chat relay participates.
            await runtime.ReconnectGptAsync().ConfigureAwait(false);
            await runtime.ConnectCodexAsync().ConfigureAwait(false);
            report.GptConnection = runtime.GptConnection; report.CodexConnection = runtime.CodexConnection;
            report.InitialGptConnection = runtime.GptConnection; report.InitialCodexConnection = runtime.CodexConnection;
            report.ConnectionProbe = report.GptConnection.State == WorkflowLinkState.Connected && report.CodexConnection.State == WorkflowLinkState.Connected
                ? "BothConnected" : "Blocked";
            if (connectionOnly)
            {
                await ReadCatalogAsync().ConfigureAwait(false);
                if (report.ConnectionProbe != "BothConnected")
                { report.BlockingReason = "只读连接探测失败；没有派发模型任务，详见分阶段时序。"; return 1; }
            }
            if (report.ConnectionProbe != "BothConnected")
            { report.BlockingReason = "真实连接核验未通过，未开始模型工作流。"; return 1; }
            report.IdleReconnectStartedAt = DateTimeOffset.UtcNow;
            report.IdleDisconnectMessage = WorkflowText.Safe(await runtime.DisconnectAcceptanceLinksAsync().ConfigureAwait(false), 800);
            report.DisconnectedGpt = runtime.GptConnection; report.DisconnectedCodex = runtime.CodexConnection;
            if (report.DisconnectedGpt.State != WorkflowLinkState.Disconnected || report.DisconnectedCodex.State != WorkflowLinkState.Disconnected)
            { report.BlockingReason = "自有空闲通道断开状态未被真实确认，未派发模型请求。"; return 1; }
            await runtime.ReconnectGptAsync().ConfigureAwait(false);
            await runtime.ConnectCodexAsync().ConfigureAwait(false);
            report.GptConnection = runtime.GptConnection; report.CodexConnection = runtime.CodexConnection;
            report.IdleReconnectPassed = report.GptConnection.State == WorkflowLinkState.Connected && report.CodexConnection.State == WorkflowLinkState.Connected;
            report.IdleReconnectFinishedAt = DateTimeOffset.UtcNow;
            WriteJson(Path.Combine(logs, "real-idle-connection-recovery.json"), new
            {
                ProductOwned = true, Simulated = false, Scope = "OnlyOwnedIdleTransports_NoInFlightTask", report.IdleReconnectStartedAt,
                report.IdleDisconnectMessage, report.DisconnectedGpt, report.DisconnectedCodex, report.GptConnection, report.CodexConnection,
                report.IdleReconnectPassed, report.IdleReconnectFinishedAt
            });
            if (!report.IdleReconnectPassed)
            { report.BlockingReason = "自有空闲通道真实重连未通过，未开始模型工作流。"; return 1; }
            await ReadCatalogAsync().ConfigureAwait(false);
            if (connectionOnly)
            {
                report.Success = report.IdleReconnectPassed;
                report.Message = "只读身份和空闲重连检查完成；零模型请求、零Thread/Turn新建。";
            }
            else
            {
            WriteReport();
            WriteJson(Path.Combine(root, "recovery.json"), new { report.TaskId, report.ProcessId, State = "DispatchMayBegin", AutomaticReplayAllowed = false });
            report.WorkflowStarted = true; WriteReport();
            var result = await runtime.StartAsync(config).ConfigureAwait(false);
            var final = runtime.State;
            report.Message = WorkflowText.Safe(result, 1200); report.Phase = final.Phase.ToString(); report.Round = final.Round;
            report.CompletedRounds = final.CompletedRounds; report.CurrentRequestId = final.CurrentRequestId;
            report.NeedsRecoveryConfirmation = final.NeedsRecoveryConfirmation;
            report.CodexSessionId = final.CodexSessionId;
            report.Evidence = final.Evidence.Select(item => item with { Summary = WorkflowText.Safe(item.Summary, 1200) }).ToArray();
            report.AuthorizedWritePaths = final.AuthorizedWritePaths; report.StepWritePaths = final.StepWritePaths;
            report.Checks = validator.Results.ToArray();
            report.Success = final.Phase == WorkflowLoopPhase.Completed && final.CompletedRounds == 2 && final.Round == 2
                && report.Checks.Length == 2 && report.Checks.All(check => check.Success)
                && final.Evidence.Count(item => item.Provider == WorkflowProvider.Codex) == 2
                && final.Evidence.Count(item => item.Provider == WorkflowProvider.ChatGPT) == 4
                && final.StepWritePaths.SequenceEqual(new[] { "LabelNormalizer.cs" }, StringComparer.Ordinal);
            if (!report.Success) report.BlockingReason = report.GptConnection?.DiagnosticCode == "chatgpt_reply_reference_unresolved"
                || runtime.GptConnection.DiagnosticCode == "chatgpt_reply_reference_unresolved"
                ? "body_unreadable：官方GPT回复只有正文引用，未读取正文；没有伪造建议、复核或两轮成功。"
                : final.Phase == WorkflowLoopPhase.Completed ? "流程提前结束；真实两轮证据未齐全。" : WorkflowText.Safe(final.Message, 1200);
            }

            async Task ReadCatalogAsync()
            {
            if (report.GptConnection.State == WorkflowLinkState.Connected)
            {
                using var catalogTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
                try
                {
                    var catalog = await runtime.ReadOfficialGptCatalogAsync(catalogTimeout.Token).ConfigureAwait(false);
                    if (WorkflowText.HasSecret(JsonSerializer.Serialize(catalog))) throw new InvalidDataException("公开工具目录含疑似敏感内容，未保存。");
                    WriteJson(Path.Combine(logs, "official-gpt-tools.json"), catalog);
                    report.CatalogRead = true; report.PublicToolCount = catalog.Length;
                    report.ExplicitContentResolverNames = catalog.Where(ExplicitlyAdvertisesResolver)
                        .Select(tool => tool.GetProperty("name").GetString() ?? "").ToArray();
                    report.ContentResolverAdvertised = report.ExplicitContentResolverNames.Length > 0;
                    report.ContentResolverAssessment = report.ContentResolverAdvertised ? "ExplicitMetadataOnly_NotInvoked" : "NoExplicitResolverAdvertised_NotInvoked";
                }
                catch (Exception error) when (Expected(error))
                { report.CatalogFailure = error.GetType().Name; report.ContentResolverAssessment = "CatalogUnavailable_Unknown"; }
            }
            }
        }
        catch (Exception error) when (Expected(error))
        {
            report.Success = false; report.BlockingReason = "验收在当前检查点阻断：" + error.GetType().Name;
            report.Message = WorkflowText.Safe(error.Message, 800); return 1;
        }
        finally
        {
            if (claimed)
            {
                if (runtime is not null)
                {
                    cleaningUp = true;
                    report.GptConnection = runtime.GptConnection; report.CodexConnection = runtime.CodexConnection;
                    report.ProviderEvidence = runtime.ReadAcceptanceProviderEvidence();
                    try { await runtime.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception error) when (Expected(error))
                    { report.Success = false; report.BlockingReason += "；自有通道释放未确认：" + error.GetType().Name; }
                }
                if (validator is not null) report.Checks = validator.Results.ToArray();
                try
                {
                    report.ProductionProtectedAfter = CaptureProductionProtected();
                    report.ProductionProtectedUnchanged = SameHashes(report.ProductionProtectedBefore, report.ProductionProtectedAfter);
                    if (!report.ProductionProtectedUnchanged) { report.Success = false; report.BlockingReason += "；生产受保护文件SHA发生变化。"; }
                    report.FinishedAt = DateTimeOffset.UtcNow; report.Status = report.Success ? "Passed" : "Blocked";
                    WriteReport();
                    WriteJson(Path.Combine(root, "recovery.json"), new { report.TaskId, report.ProcessId, report.Status, report.Success,
                        report.CurrentRequestId, report.NeedsRecoveryConfirmation, AutomaticReplayAllowed = false, Result = Path.Combine(root, "result.json") });
                    Console.WriteLine(JsonSerializer.Serialize(new { report.ProductOwned, report.ProcessId, report.TaskId, report.Status,
                        report.Round, report.CompletedRounds, report.BlockingReason, report.ProductionProtectedUnchanged, Result = Path.Combine(root, "result.json") }, Json));
                }
                catch (Exception error) when (Expected(error))
                {
                    report.Success = false;
                    report.BlockingReason += "；验收结果或生产保护校验未保存：" + error.GetType().Name;
                    Console.WriteLine("验收结果保存失败：" + error.GetType().Name + "；一次性投递标记仍保留，禁止自动重发。");
                }
            }
        }
        return report.Success ? 0 : 1;

        void WriteReport()
        {
            lock (report) WriteJson(Path.Combine(root, "result.json"), report);
        }
    }

    static bool ExplicitlyAdvertisesResolver(JsonElement tool)
    {
        var name = tool.GetProperty("name").GetString() ?? "";
        var description = tool.GetProperty("description").GetString() ?? "";
        // Record only an explicit public claim, never infer a hidden schema or call an unknown tool.
        return name.Contains("resolve_content_reference", StringComparison.OrdinalIgnoreCase)
            || (description.Contains("chatgpt-content-reference", StringComparison.OrdinalIgnoreCase)
                && (description.Contains("resolve", StringComparison.OrdinalIgnoreCase) || description.Contains("正文", StringComparison.Ordinal)));
    }
    static bool Expected(Exception error) => error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException
        or ArgumentException or InvalidOperationException or OperationCanceledException or TimeoutException or System.ComponentModel.Win32Exception or WorkflowRpcException;
    static Dictionary<string, string> CaptureProductionProtected()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relative in new[] { "settings.json", "settings.json.bak", "loop-state.json", "loop-state.json.bak", "history.json", "history.json.bak", "connection-check.json" })
        {
            var path = Path.Combine(ProductionData, "workflow", relative);
            if (!AutomationStore.IsSafeDPath(path)) throw new InvalidDataException("生产保护路径未核实。");
            result["workflow/" + relative] = File.Exists(path) ? Hash(path) : "Missing";
        }
        const string icon = @"D:\codex\Tools\Icons\CodexQuotaWidget-20260924-223636.ico";
        if (!AutomationStore.IsSafeDPath(icon)) throw new InvalidDataException("生产图标保护路径未核实。");
        result[icon] = File.Exists(icon) ? Hash(icon) : "Missing";
        return result;
    }
    static bool SameHashes(Dictionary<string, string> first, Dictionary<string, string> second)
        => first.Count == second.Count && first.All(pair => second.TryGetValue(pair.Key, out var value) && value == pair.Value);
    static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    static void WriteJson<T>(string path, T value)
    {
        if (!AutomationStore.IsSafeDPath(path) || !AutomationStore.IsSafeDPath(path + ".new")) throw new InvalidDataException("验收输出路径未核实。");
        File.WriteAllText(path + ".new", JsonSerializer.Serialize(value, Json), new UTF8Encoding(false));
        File.Move(path + ".new", path, true);
    }

    const string Goal = "新建的独立代码验收任务，必须恰好两轮。只能修改 LabelNormalizer.cs；不能修改固定Program.cs、csproj、配置、驱动或生成目录。"
        + "类和方法固定为 namespace WorkflowLiveAcceptance; public static class LabelNormalizer { public static string Normalize(string input) => 表达式; }，也可用方法块 return 表达式。"
        + "不增加using、注释、其他成员或语句。第1轮仅 trim 首尾空白，内部空白原样保留，表达式为 input.Trim()。"
        + "第1轮验证通过后GPT真实复核必须选择 ACC_REVIEW: CONTINUE，建议第2轮再压缩内部连续空白为单个ASCII空格，同时保留trim行为。"
        + "第2轮纯字符串表达式为 string.Join(\" \", input.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries))。"
        + "仅允许这两个已说明的纯字符串表达式；未知代码由固定验证器拒绝，禁止Process/File/网络/PInvoke/reflection或任何其他API。"
        + "第2轮固定检查通过后GPT再真实复核并选择 ACC_REVIEW: COMPLETE。每轮GPT建议中的WritePaths严格为[\"LabelNormalizer.cs\"]。"
        + "只由本产品调用已核验的官方GPT与Codex连接；不要请求根代理代传，不创建其他任务或后台循环，不运行命令、不读取密钥。"
        + "构建和各轮固定Driver检查由产品独立执行，模型无需运行验证。";

    sealed class LiveReport
    {
        public bool ProductOwned { get; init; } = true;
        public bool ConnectionOnly { get; init; }
        public bool Simulated { get; init; }
        public string[] SimulationEvidence { get; init; } = [];
        public int ProcessId { get; init; } = Environment.ProcessId;
        public string? ProcessPath { get; init; } = Environment.ProcessPath;
        public Guid TaskId { get; init; }
        public string ArtifactRoot { get; init; } = "";
        public DateTimeOffset StartedAt { get; init; }
        public DateTimeOffset? FinishedAt { get; set; }
        public string Status { get; set; } = "Pending";
        public bool Success { get; set; }
        public string GptTargetThreadId { get; set; } = "";
        public string CallerThreadId { get; set; } = "";
        public string ConnectionProbe { get; set; } = "NotStarted";
        public WorkflowProviderConnection? GptConnection { get; set; }
        public WorkflowProviderConnection? CodexConnection { get; set; }
        public WorkflowProviderConnection? InitialGptConnection { get; set; }
        public WorkflowProviderConnection? InitialCodexConnection { get; set; }
        public object? ProviderEvidence { get; set; }
        public DateTimeOffset? IdleReconnectStartedAt { get; set; }
        public string IdleDisconnectMessage { get; set; } = "";
        public WorkflowProviderConnection? DisconnectedGpt { get; set; }
        public WorkflowProviderConnection? DisconnectedCodex { get; set; }
        public bool IdleReconnectPassed { get; set; }
        public DateTimeOffset? IdleReconnectFinishedAt { get; set; }
        public bool CatalogRead { get; set; }
        public int PublicToolCount { get; set; }
        public bool ContentResolverAdvertised { get; set; }
        public string[] ExplicitContentResolverNames { get; set; } = [];
        public string ContentResolverAssessment { get; set; } = "NotRead";
        public string CatalogFailure { get; set; } = "";
        public bool WorkflowStarted { get; set; }
        public string Phase { get; set; } = "Idle";
        public int Round { get; set; }
        public int CompletedRounds { get; set; }
        public string Message { get; set; } = "一次性验收已占位，尚未连接或派发。";
        public string BlockingReason { get; set; } = "";
        public Guid CurrentRequestId { get; set; }
        public bool NeedsRecoveryConfirmation { get; set; }
        public string CodexSessionId { get; set; } = "";
        public string[] AuthorizedWritePaths { get; set; } = [];
        public string[] StepWritePaths { get; set; } = [];
        public WorkflowStepEvidence[] Evidence { get; set; } = [];
        public CheckEvidence[] Checks { get; set; } = [];
        public Dictionary<string, string> ProductionProtectedBefore { get; set; } = [];
        public Dictionary<string, string> ProductionProtectedAfter { get; set; } = [];
        public bool ProductionProtectedUnchanged { get; set; }
    }
    sealed record CheckEvidence(int Round, bool Success, string SourceSha256, bool FrozenFilesUnchanged, bool PureStringAllowlist,
        int? RestoreExitCode, int? BuildExitCode, int? DriverExitCode, string Message);

    /// <summary>Acceptance-only finite code allowlist. The production validator is neither relaxed nor replaced globally.</summary>
    sealed class LabelValidator(string project, string data, string logs) : IWorkflowProjectValidator
    {
        readonly Dictionary<string, string> frozen = new(StringComparer.OrdinalIgnoreCase);
        public List<CheckEvidence> Results { get; } = [];
        int invocations;
        public void CreateFrozenProject()
        {
            Create("LabelNormalizer.cs", "namespace WorkflowLiveAcceptance;\npublic static class LabelNormalizer\n{\n    public static string Normalize(string input) => input;\n}\n", false);
            Create(ProjectName, ProjectSource, true);
            Create("Program.cs", DriverSource, true);
            Create("NuGet.Config", "<configuration><packageSources><clear /></packageSources></configuration>\n", true);
            Create("global.json", "{\"sdk\":{\"version\":\"10.0.401\",\"rollForward\":\"disable\",\"allowPrerelease\":false}}\n", true);
            Directory.CreateDirectory(Path.Combine(data, "empty-feed"));
            File.Copy(Path.Combine(project, "LabelNormalizer.cs"), Path.Combine(logs, "initial-LabelNormalizer.cs.txt"), false);
            WriteJson(Path.Combine(logs, "frozen-files.json"), frozen);
        }
        void Create(string relative, string content, bool freeze)
        {
            var path = Path.Combine(project, relative);
            if (!AutomationStore.IsSafeDPath(path)) throw new InvalidDataException("固定项目路径异常。");
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(content);
            if (freeze) frozen[relative] = Hash(path);
        }
        bool FrozenUnchanged()
            => frozen.Count == 4 && frozen.All(pair => AutomationStore.IsSafeDPath(Path.Combine(project, pair.Key))
                && File.Exists(Path.Combine(project, pair.Key)) && Hash(Path.Combine(project, pair.Key)) == pair.Value)
                && Directory.EnumerateFileSystemEntries(project).All(path => AutomationStore.IsSafeDPath(path)
                    && (frozen.ContainsKey(Path.GetFileName(path)) || Path.GetFileName(path) == "LabelNormalizer.cs"
                        || Directory.Exists(path) && Path.GetFileName(path) is "bin" or "obj" or ".acc-workflow"));
        public async Task<WorkflowValidationResult> ValidateAsync(string projectRoot, CancellationToken cancellationToken)
        {
            var round = Interlocked.Increment(ref invocations);
            string sourceHash = ""; bool frozenOkay = false, allowlist = false;
            int? restore = null, build = null, driver = null;
            var heldInputs = new List<FileStream>();
            try
            {
                if (round is < 1 or > 2 || !WorkflowAuthorization.SameProject(projectRoot, project) || !(frozenOkay = FrozenUnchanged()))
                    return Result(false, "固定驱动、项目或调用轮次已改变；没有运行任何模型代码。");
                var sourcePath = Path.Combine(project, "LabelNormalizer.cs");
                if (!AutomationStore.IsSafeDPath(sourcePath) || !File.Exists(sourcePath) || new FileInfo(sourcePath).Length > 4096)
                    return Result(false, "Label源码路径或大小无效；没有运行模型代码。");
                // Windows read sharing prevents another writer from changing any accepted input until the child has exited.
                foreach (var relative in frozen.Keys.Append("LabelNormalizer.cs"))
                    heldInputs.Add(new FileStream(Path.Combine(project, relative), FileMode.Open, FileAccess.Read, FileShare.Read));
                if (!FrozenUnchanged()) return Result(false, "冻结输入锁定后与原摘要不符，拒绝构建。");
                var source = File.ReadAllText(sourcePath); sourceHash = Hash(sourcePath);
                if (!(allowlist = SafeSource(source, round))) return Result(false, "未知代码不在本轮有限纯字符串表达式白名单；没有构建或执行。");
                File.WriteAllText(Path.Combine(logs, "round-" + round + "-LabelNormalizer.cs.txt"), source, new UTF8Encoding(false));
                // No package is requested. Explicit empty local source prevents downloads or unknown installations.
                restore = await Run(round, "restore", ["restore", ProjectName, "--configfile", "NuGet.Config", "--source", Path.Combine(data, "empty-feed"), "--disable-parallel"], cancellationToken).ConfigureAwait(false);
                if (restore != 0) return Result(false, "固定项目离线restore失败。");
                if (!FrozenUnchanged() || Hash(sourcePath) != sourceHash) return Result(false, "检查后源码或驱动发生变化，拒绝构建。");
                build = await Run(round, "build", ["build", ProjectName, "-c", "Release", "--no-restore", "/nodeReuse:false"], cancellationToken).ConfigureAwait(false);
                if (build != 0) return Result(false, "固定纯字符串项目编译失败。");
                if (!FrozenUnchanged() || Hash(sourcePath) != sourceHash) return Result(false, "构建期间源码或驱动发生变化，拒绝运行。");
                var assembly = Path.Combine(project, "bin", "Release", "net10.0", "WorkflowLiveLabelCheck.dll");
                if (!AutomationStore.IsSafeDPath(assembly) || !File.Exists(assembly)) return Result(false, "固定输出程序集路径未核实，拒绝运行。");
                driver = await Run(round, "driver", [assembly, round.ToString()], cancellationToken).ConfigureAwait(false);
                return Result(driver == 0, driver == 0 ? $"PASS round {round}: 固定纯字符串白名单、冻结Driver、真实D SDK编译及7项字符串检查通过。" : "固定Driver字符串检查失败。");
            }
            catch (Exception error) when (Expected(error)) { return Result(false, "隔离字符串检查未完成：" + error.GetType().Name); }
            finally { foreach (var input in heldInputs) input.Dispose(); }
            WorkflowValidationResult Result(bool success, string message)
            {
                Results.Add(new(round, success, sourceHash, frozenOkay, allowlist, restore, build, driver, message));
                WriteJson(Path.Combine(logs, "checks.json"), Results);
                return new(success, message);
            }
        }
        static bool SafeSource(string source, int round)
        {
            // Tokenize strictly: only ASCII identifiers, whitespace, the fixed punctuation and one literal " ".
            // Comments, directives, escaped literals, attributes, new members, all other APIs and syntax are rejected.
            var compact = new StringBuilder();
            for (int i = 0; i < source.Length;)
            {
                var c = source[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (c == '"')
                {
                    if (i + 2 >= source.Length || source[i + 1] != ' ' || source[i + 2] != '"') return false;
                    compact.Append("\" \""); i += 3; continue;
                }
                if (char.IsAsciiLetterOrDigit(c) || c == '_' || ";{}().,[]?=>".Contains(c)) { compact.Append(c); i++; continue; }
                return false;
            }
            var expression = round == 1 ? "input.Trim()" : "string.Join(\" \",input.Split((char[]?)null,System.StringSplitOptions.RemoveEmptyEntries))";
            const string prefix = "namespaceWorkflowLiveAcceptance;publicstaticclassLabelNormalizer{publicstaticstringNormalize(stringinput)";
            var canonical = compact.ToString();
            return canonical == prefix + "=>" + expression + ";}" || canonical == prefix + "{return" + expression + ";}}";
        }
        async Task<int> Run(int round, string step, string[] arguments, CancellationToken token)
        {
            var temp = Path.Combine(data, "temp"); Directory.CreateDirectory(temp);
            var info = new ProcessStartInfo(Dotnet) { WorkingDirectory = project, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
            if (step is "restore" or "build")
            {
                // These global properties take effect before Sdk.props can import an ancestor Directory.Build file.
                info.ArgumentList.Add("/p:ImportDirectoryBuildProps=false"); info.ArgumentList.Add("/p:ImportDirectoryBuildTargets=false");
                info.ArgumentList.Add("/p:CustomBeforeMicrosoftCommonTargets="); info.ArgumentList.Add("/p:CustomAfterMicrosoftCommonTargets=");
            }
            info.Environment["TEMP"] = temp; info.Environment["TMP"] = temp;
            info.Environment["DOTNET_CLI_HOME"] = Path.Combine(data, "dotnet-home");
            info.Environment["NUGET_PACKAGES"] = @"D:\codex\Tools\nuget-packages";
            info.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = Path.Combine(data, "runtime-cache");
            info.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1"; info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            info.Environment["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "false";
            info.Environment["MSBUILDDISABLENODEREUSE"] = "1";
            info.Environment["DOTNET_ROOT"] = Path.GetDirectoryName(Dotnet)!;
            foreach (var key in new[] { "MSBuildSDKsPath", "MSBUILD_EXE_PATH", "DOTNET_ADDITIONAL_DEPS", "DOTNET_SHARED_STORE", "DOTNET_STARTUP_HOOKS" })
                info.Environment.Remove(key);
            using var job = new ProcessJob();
            using var process = Process.Start(info) ?? throw new IOException("固定SDK未启动。");
            try { job.Assign(process); }
            catch { try { process.Kill(entireProcessTree: true); } catch { } throw; }
            var stdout = Drain(process.StandardOutput); var stderr = Drain(process.StandardError);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2), token).ConfigureAwait(false);
            var transcript = WorkflowText.Safe(await stdout.ConfigureAwait(false) + "\n" + await stderr.ConfigureAwait(false), 24000);
            File.WriteAllText(Path.Combine(logs, $"round-{round}-{step}.log"), transcript, new UTF8Encoding(false));
            token.ThrowIfCancellationRequested(); return process.ExitCode;
        }
        static async Task<string> Drain(StreamReader reader)
        {
            var text = new StringBuilder(); var buffer = new char[4096]; int count;
            while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                if (text.Length < 24000) text.Append(buffer, 0, Math.Min(count, 24000 - text.Length));
            return text.ToString();
        }
        const string ProjectSource = """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><AssemblyName>WorkflowLiveLabelCheck</AssemblyName>
    <Nullable>enable</Nullable><ImplicitUsings>disable</ImplicitUsings><EnableDefaultItems>false</EnableDefaultItems>
    <ImportDirectoryBuildProps>false</ImportDirectoryBuildProps><ImportDirectoryBuildTargets>false</ImportDirectoryBuildTargets>
    <EnableNETAnalyzers>false</EnableNETAnalyzers><RunAnalyzers>false</RunAnalyzers><UseSharedCompilation>false</UseSharedCompilation>
    <RestoreSources></RestoreSources><NuGetAudit>false</NuGetAudit><RestoreIgnoreFailedSources>true</RestoreIgnoreFailedSources>
  </PropertyGroup>
  <ItemGroup><Compile Include="Program.cs"/><Compile Include="LabelNormalizer.cs"/></ItemGroup>
</Project>
""";
        const string DriverSource = """
using System;
using WorkflowLiveAcceptance;
internal static class Program
{
    static int Main(string[] args)
    {
        if (args.Length != 1 || (args[0] != "1" && args[0] != "2")) return 2;
        string[] input = ["", "   ", "alpha", "  alpha   beta  ", "\talpha\t\tbeta\r\n", " 中文  标签 ", " a \t b "];
        string[] first = ["", "", "alpha", "alpha   beta", "alpha\t\tbeta", "中文  标签", "a \t b"];
        string[] second = ["", "", "alpha", "alpha beta", "alpha beta", "中文 标签", "a b"];
        var expected = args[0] == "1" ? first : second;
        for (int i = 0; i < input.Length; i++)
            if (!string.Equals(LabelNormalizer.Normalize(input[i]), expected[i], StringComparison.Ordinal))
            { Console.WriteLine("FAIL round " + args[0] + " case " + i); return 1; }
        Console.WriteLine("PASS round " + args[0] + " fixed string cases=7"); return 0;
    }
}
""";
    }
}

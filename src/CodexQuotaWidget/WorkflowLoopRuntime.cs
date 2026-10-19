using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CodexQuotaWidget.Core;
using CodexQuotaWidget.Core.Automation;
using CodexQuotaWidget.Core.ControlCenter;

namespace CodexQuotaWidget;

public interface IWorkflowLoopUiController
{
    WorkflowLoopConfiguration Configuration { get; }
    WorkflowLoopState State { get; }
    bool IsRunning { get; }
    string ConnectionDescription { get; }
    WorkflowProviderConnection GptConnection => new(WorkflowLinkState.Disconnected,"未连接");
    WorkflowProviderConnection CodexConnection => new(WorkflowLinkState.Disconnected,"未连接");
    IReadOnlyList<WorkflowConnectionCandidate> GptCandidates => Array.Empty<WorkflowConnectionCandidate>();
    Task<string> ReconnectGptAsync(string? candidateId = null) => Task.FromResult("未配置GPT重连能力。");
    Task<string> ConnectCodexAsync() => Task.FromResult("未配置Codex连接能力。");
    Task<string> SaveTaskAsync(WorkflowLoopConfiguration configuration) => Task.FromResult("此控制器不支持保存任务。");
    IReadOnlyList<WorkflowLoopState> ReadHistory() => Array.Empty<WorkflowLoopState>();
    Task<string> StartAsync(WorkflowLoopConfiguration configuration);
    void Pause();
    void Stop() => Pause();
    string AcknowledgeRecovery();
}

/// <summary>Product-owned orchestration, separate from the quota reader and the local tracking queue.</summary>
internal sealed class WorkflowLoopRuntime : IWorkflowLoopUiController, IAsyncDisposable
{
    readonly string dataRoot;
    readonly IWorkflowProjectValidator? acceptanceValidator;
    readonly string? authenticationHome;
    DesktopChatGptWorkflowConnector? chat;
    SiwcGptWorkflowConnector? siwc;
    SiwcHttpProtocol? siwcProtocol;
    CodexWorkflowConnector? codex;
    WorkflowLoopService? service;
    Task<string>? activeRun;
    CancellationTokenSource? runLifetime;
    string connection = "未连接；点击开始后核验真实桌面会话与Codex身份。";
    readonly CancellationTokenSource lifetime = new();
    readonly SemaphoreSlim connectionGate = new(1, 1);
    int connecting;
    DateTimeOffset? gptVerifiedAt;
    WorkflowProviderConnection codexStatus = new(WorkflowLinkState.Disconnected, "未连接");
    WorkflowLoopConfiguration configurationBacking = new();
    public WorkflowLoopConfiguration Configuration
    {
        get => configurationBacking with { AuthorizedWritePaths = configurationBacking.AuthorizedWritePaths.ToArray() };
        private set => configurationBacking = value with { AuthorizedWritePaths = value.AuthorizedWritePaths?.ToArray() ?? [] };
    }
    public WorkflowLoopState State => service?.State ?? new();
    public bool IsRunning => activeRun is { IsCompleted: false } || service?.IsRunning == true;
    public string ConnectionDescription => connection;
    public WorkflowProviderConnection GptConnection => Configuration.GptBackend == WorkflowBackend.SiwcGptReviewer
        ? new(WorkflowLinkState.Disconnected, SiwcPhaseOne.BlockReason + " SIWC额度：Unknown。", "siwc_phase1_live_disabled", Configuration.Siwc.AccountId)
        : chat is null ? new(WorkflowLinkState.Disconnected, "未连接") : new(
        chat.ConnectionStatus switch
        {
            ChatGptConnectionStatus.Connecting => WorkflowLinkState.Connecting,
            ChatGptConnectionStatus.Connected when chat.CanExecute || chat.ConnectionState == WorkflowConnectionState.Busy => WorkflowLinkState.Connected,
            ChatGptConnectionStatus.Conflict => WorkflowLinkState.Conflict,
            ChatGptConnectionStatus.Failed => WorkflowLinkState.Failed,
            _ => WorkflowLinkState.Disconnected
        }, chat.DiagnosticMessage, chat.DiagnosticCode, chat.TargetThreadId, gptVerifiedAt);
    public WorkflowProviderConnection CodexConnection => codex is null ? codexStatus : codex.IsConnecting ? new(WorkflowLinkState.Connecting, "正在核验官方Codex身份……")
        : codex.ConnectionState is WorkflowConnectionState.Ready or WorkflowConnectionState.Busy
            ? new(WorkflowLinkState.Connected, "官方账户和执行通道已核验；任务完成另行确认。", codex.DiagnosticCode, codex.LastThreadId, codex.VerifiedAt)
            : codex.ConnectionState == WorkflowConnectionState.Faulted ? new(WorkflowLinkState.Failed,
                codexStatus.State == WorkflowLinkState.Failed ? codexStatus.Message : "Codex通道已断开或任务结果未确认；进度保留，请重新核验。", codex.DiagnosticCode) : codexStatus;
    public IReadOnlyList<WorkflowConnectionCandidate> GptCandidates => Configuration.GptBackend == WorkflowBackend.SiwcGptReviewer ? [] : chat?.CandidateDiagnostics.Select((candidate, index) => new WorkflowConnectionCandidate(
        candidate.PipePath, $"连接 {index + 1} · PID {candidate.OwnerProcessId} · {(candidate.Verified ? "目标会话已核验" : "不可用")}", candidate.Verified,
        candidate.Reason + " / " + candidate.ProbeCode)).ToArray() ?? [];
    public event Action? Changed;
    public WorkflowLoopRuntime(string dataRoot, WorkflowLoopConfiguration? configuration = null, IWorkflowProjectValidator? acceptanceValidator = null, string? authenticationHome = null)
    {
        this.dataRoot = dataRoot;
        if ((acceptanceValidator is not null || authenticationHome is not null)
            && !Path.GetFullPath(dataRoot).StartsWith(@"D:\CodexQuotaWidget-GitHub\artifacts\workflow-stability-", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("验收覆盖只允许本轮独立D盘数据，不用于生产任务。");
        this.acceptanceValidator = acceptanceValidator; this.authenticationHome = authenticationHome;
        Configuration = configuration ?? new()
        {
            MaximumRounds = 3
        };
        try
        {
            if (configuration is null) Configuration = ReadConfiguration(dataRoot) ?? Configuration;
            service = new(dataRoot, new DisconnectedWorkflowConnector(), new DisconnectedWorkflowConnector(),
                new UnconfiguredValidator(), _ => Task.FromResult(new QuotaGuardSnapshot(QuotaGuardState.Unknown, null, null, false, false, "未连接")));
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        { connection = "工作流恢复记录未核实（高级工作流需要安全的D盘数据目录）：" + error.GetType().Name + "；其他功能仍可使用。"; }
    }
    public Task<string> StartAsync(WorkflowLoopConfiguration configuration)
    {
        configuration = configuration with { AuthorizedWritePaths = configuration.AuthorizedWritePaths?.ToArray() ?? [] };
        if (configuration.GptBackend == WorkflowBackend.SiwcGptReviewer && !SiwcPhaseOne.LiveAccountAndInferenceEnabled)
            return Task.FromResult(SiwcPhaseOne.BlockReason + " SIWC额度未知；实验草稿保留，不发送任务。");
        if (!WorkflowLoopService.ValidConfiguration(configuration)) return Task.FromResult("任务ID、项目、授权范围或会话无效；未启动模型。");
        if (IsRunning || Volatile.Read(ref connecting) != 0) return Task.FromResult("本次工作流仍在运行或连接中，不重复派发。");
        if (State.Phase == WorkflowLoopPhase.Completed && WorkflowLoopService.SameTask(State, configuration))
            return Task.FromResult("该任务已经完成，请新建任务；旧验收不会重跑。");
        if (State.CurrentRequestId != Guid.Empty || State.NeedsRecoveryConfirmation)
            return Task.FromResult("旧请求交付状态仍不明确，保留进度并禁止重复派发。");
        if (State.Phase == WorkflowLoopPhase.Stopped && State.RunId == configuration.TaskId)
            return Task.FromResult("该任务已停止；请新建任务，不重放原任务。");
        if (chat is not null && Configuration.ChatGptThreadId != configuration.ChatGptThreadId
            || codex is not null && !string.Equals(codex.ProjectRoot, configuration.ProjectRoot, StringComparison.OrdinalIgnoreCase)
            || Configuration.CallerThreadId != configuration.CallerThreadId)
            return Task.FromResult("连接身份与新任务不一致，请先保存任务并重新核验连接。");
        activeRun = RunAsync(configuration);
        return activeRun;
    }
    public async Task<string> SaveTaskAsync(WorkflowLoopConfiguration configuration)
    {
        configuration = configuration with { AuthorizedWritePaths = configuration.AuthorizedWritePaths?.ToArray() ?? [] };
        if (IsRunning || Volatile.Read(ref connecting) != 0) return "运行或连接期间不能编辑任务。";
        if (!WorkflowLoopService.ValidDraftConfiguration(configuration with { LocalExecutionAuthorized = true })) return "目标、项目或会话无效，未保存。";
        if (Configuration.TaskId != Guid.Empty && Configuration.TaskId == configuration.TaskId && Configuration.GptBackend != configuration.GptBackend)
            return "已有任务不能迁移Backend，请新建独立任务；旧进度保留。";
        if (!await connectionGate.WaitAsync(0)) return "连接正在核验，请稍后保存。";
        try
        {
            var identityChanged = Configuration.GptBackend != configuration.GptBackend || Configuration.Siwc != configuration.Siwc
                || Configuration.ProjectRoot != configuration.ProjectRoot || Configuration.ChatGptThreadId != configuration.ChatGptThreadId
                || Configuration.CallerThreadId != configuration.CallerThreadId || Configuration.TaskId != configuration.TaskId
                || Configuration.Goal != configuration.Goal || !Configuration.AuthorizedWritePaths.SequenceEqual(configuration.AuthorizedWritePaths, StringComparer.OrdinalIgnoreCase);
            if (identityChanged && State.RunId != Guid.Empty && State.Phase is not (WorkflowLoopPhase.Completed or WorkflowLoopPhase.Stopped))
                return "未完成任务仍绑定原项目和会话；不能改写身份，进度保留。";
            if (identityChanged)
            {
                if (chat is not null) await chat.DisposeAsync();
                if (codex is not null) await codex.DisposeAsync();
                if (siwc is not null) await siwc.DisposeAsync(); siwc = null; siwcProtocol?.Dispose(); siwcProtocol = null;
                chat = null; codex = null; gptVerifiedAt = null; codexStatus = new(WorkflowLinkState.Disconnected, "目标已改变，需重新核验。");
            }
            SaveConfiguration(configuration); Configuration = configuration with { LocalExecutionAuthorized = false };
            return "任务已保存；点击开始前仍需明确本地执行授权。";
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException) { return "未保存：" + error.GetType().Name; }
        finally { connectionGate.Release(); Changed?.Invoke(); }
    }
    public IReadOnlyList<WorkflowLoopState> ReadHistory()
        => service?.ReadHistory() ?? [];
    public Task<string> ReconnectGptAsync(string? candidateId = null) => ConnectProviderAsync(true, candidateId);
    public Task<string> ConnectCodexAsync() => ConnectProviderAsync(false);
    async Task<string> ConnectProviderAsync(bool gpt, string? candidate = null)
    {
        if (IsRunning) return "任务正在运行，请先暂停并等待当前请求结束；未创建重复通道。";
        if (!await connectionGate.WaitAsync(0)) return "正在连接，请勿重复点击。";
        Interlocked.Exchange(ref connecting, 1);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(35));
        try
        {
            if (gpt)
            {
                if (Configuration.GptBackend == WorkflowBackend.SiwcGptReviewer)
                    return SiwcPhaseOne.BlockReason + " 未使用旧Desktop连接，也未发起授权。";
                if (Configuration.GptBackend != WorkflowBackend.LegacyDesktop) return "未知GPT Backend，已阻断；不会回退到其他连接方式。";
                chat ??= new(Configuration.ChatGptThreadId, Configuration.CallerThreadId, dataRoot);
                Changed?.Invoke();
                var success = await chat.ReconnectAsync(candidate, timeout.Token);
                if (success) gptVerifiedAt = DateTimeOffset.UtcNow;
                return chat.DiagnosticMessage;
            }
            codexStatus = new(WorkflowLinkState.Connecting, "正在核验官方Codex身份……"); Changed?.Invoke();
            codex ??= CreateCodex();
            var result = await codex.ConnectAsync(timeout.Token);
            codexStatus = new(result.Success ? WorkflowLinkState.Connected : WorkflowLinkState.Failed, WorkflowText.Safe(result.Message, 400), codex.DiagnosticCode,
                codex.LastThreadId, codex.VerifiedAt);
            return codexStatus.Message;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException or OperationCanceledException or ArgumentException)
        {
            var message = error is OperationCanceledException ? "连接超时或已取消；没有派发任务。" : "连接失败：" + error.GetType().Name + "；原任务保留。";
            if (!gpt) codexStatus = new(WorkflowLinkState.Failed, message);
            return message;
        }
        finally { Interlocked.Exchange(ref connecting, 0); connectionGate.Release(); Changed?.Invoke(); }
    }
    CodexWorkflowConnector CreateCodex()
    {
        var value = new CodexWorkflowConnector(Configuration.ProjectRoot, dataRoot, authenticationHome: authenticationHome);
        value.Changed += () => { if (value.ConnectionState == WorkflowConnectionState.Faulted && service?.IsRunning == true) service.Pause(); Changed?.Invoke(); };
        return value;
    }
    async Task<string> RunAsync(WorkflowLoopConfiguration configuration)
    {
        if (!WorkflowLoopService.ValidConfiguration(configuration)) return "目标、项目、会话或执行授权无效；未启动。";
        Configuration = configuration;
        using var runStop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        runStop.CancelAfter(TimeSpan.FromHours(1));
        runLifetime = runStop;
        try
        {
            if (configuration.GptBackend == WorkflowBackend.SiwcGptReviewer)
                return await RunExperimentalAsync(configuration, runStop.Token);
            connection = "核验GPT桌面通道与Codex登录身份……"; Changed?.Invoke();
            chat ??= new(configuration.ChatGptThreadId, configuration.CallerThreadId, dataRoot);
            codex ??= CreateCodex();
            var gptProbe = chat.ConnectAsync(runStop.Token); var codexProbe = codex.ConnectAsync(runStop.Token);
            var gptReady = await gptProbe; var codexResult = await codexProbe;
            codexStatus = new(codexResult.Success ? WorkflowLinkState.Connected : WorkflowLinkState.Failed, WorkflowText.Safe(codexResult.Message, 400), codex.DiagnosticCode);
            if (gptReady) gptVerifiedAt = DateTimeOffset.UtcNow;
            if (!gptReady || !codexResult.Success) return connection = !gptReady ? "GPT连接不可用：" + chat.DiagnosticCode : "Codex连接不可用：" + codexStatus.Message;
            if (State.CodexSessionId.Length > 0 && State.Phase != WorkflowLoopPhase.Completed && WorkflowLoopService.SameTask(State, configuration))
            {
                var owned = await codex.ValidateOwnedSessionAsync(State.CodexSessionId, State.RunId, runStop.Token);
                if (!owned.Success) return "旧Codex会话的归属或终止状态无法确认；保留进度，不接管或重新派发。";
            }
            connection = "GPT与Codex通道已核验；连接就绪不代表任务完成。";
            var guardReader = new ControlCenterService(Path.Combine(dataRoot, "workflow", "quota-read-model.json"));
            async Task<QuotaGuardSnapshot> ReadQuota(CancellationToken token)
            {
                try
                {
                    var quota = await chat.ReadQuotaAsync(token);
                    guardReader.UpdateQuota(quota, MonitorStatus.Live, false);
                    return guardReader.Snapshot.Guard;
                }
                catch (Exception error) when (error is WorkflowRpcException or IOException or InvalidOperationException or JsonException or TimeoutException)
                { return new(QuotaGuardState.Unknown, null, null, false, false, "真实额度来源暂不可确认，停止新请求。"); }
            }
            IWorkflowProjectValidator validator;
            if (acceptanceValidator is not null) validator = acceptanceValidator;
            else if (configuration.InitialReportPath.Length > 0)
                validator = new WorkflowReportProjectValidator(configuration.ProjectRoot, configuration.InitialReportPath);
            else
            {
                if (string.IsNullOrWhiteSpace(configuration.BuildProjectPath) || string.IsNullOrWhiteSpace(configuration.ChecksProjectPath))
                    return connection = "没有用户指定的固定验证项目；不会回退到默认项目或测试fixture。请在任务高级设置中确认验证范围。";
                var buildValidator = new WorkflowProjectValidator(dataRoot, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe"),
                    configuration.BuildProjectPath, configuration.ChecksProjectPath);
                buildValidator.FreezeTrustedProject(configuration.ProjectRoot);
                validator = buildValidator;
            }
            service = new(dataRoot, chat, codex, validator, ReadQuota); service.Changed += () => Changed?.Invoke();
            SaveConfiguration(configuration);
            runStop.Token.ThrowIfCancellationRequested();
            using var watcherStop = CancellationTokenSource.CreateLinkedTokenSource(runStop.Token);
            var watcher = WatchQuotaAsync(ReadQuota, watcherStop.Token);
            try
            {
                var result = await service.RunAsync(configuration, runStop.Token);
                return result.Message;
            }
            finally { watcherStop.Cancel(); try { await watcher; } catch (OperationCanceledException) { } }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException or OperationCanceledException)
        { return connection = "工作流未完成：" + error.GetType().Name + "；保留已写记录，不自动重发。"; }
        finally { runLifetime = null; Changed?.Invoke(); }
    }
    async Task WatchQuotaAsync(Func<CancellationToken, Task<QuotaGuardSnapshot>> reader, CancellationToken token)
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(20), token);
            var guard = await reader(token);
            if (!WorkflowLoopService.CanDispatch(guard, Configuration.MinimumRemainingPercent)) { service?.Pause(); return; }
        }
    }
    async Task<string> RunExperimentalAsync(WorkflowLoopConfiguration configuration, CancellationToken token)
    {
        // A task opt-in never grants account authorization or permission to spend quota.
        if (!SiwcPhaseOne.LiveAccountAndInferenceEnabled) return SiwcPhaseOne.BlockReason;
        siwcProtocol ??= new();
        siwc ??= new(dataRoot, configuration, new SiwcProtectedStore(dataRoot), siwcProtocol);
        if (!await siwc.ConnectAsync(token)) return "SIWC独立授权不可用；未回退到Desktop或API Key。";
        // No documented precise SIWC quota reader is established yet. Never substitute Desktop/Codex usage.
        var evidence = SiwcQuotaEvidence.Unknown(configuration.Siwc.AccountId);
        if (!evidence.Allows(configuration.Siwc.AccountId, configuration.MinimumRemainingPercent, DateTimeOffset.UtcNow))
            return "SIWC额度证据Unknown，安全阻断；未派发GPT或Codex。";
        codex ??= CreateCodex();
        var execution = await codex.ConnectAsync(token);
        if (!execution.Success) return "Codex执行端未核验；不派发。";
        if (State.CodexSessionId.Length > 0 && WorkflowLoopService.SameTask(State, configuration))
        {
            var owned = await codex.ValidateOwnedSessionAsync(State.CodexSessionId, State.RunId, token);
            if (!owned.Success) return "原执行Session归属或终止状态未知，不恢复。";
        }
        IWorkflowProjectValidator validator;
        if (configuration.InitialReportPath.Length > 0) validator = new WorkflowReportProjectValidator(configuration.ProjectRoot, configuration.InitialReportPath);
        else
        {
            if (configuration.BuildProjectPath.Length == 0 || configuration.ChecksProjectPath.Length == 0) return "固定验证项目缺失，不使用默认项目。";
            var fixedValidator = new WorkflowProjectValidator(dataRoot, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe"), configuration.BuildProjectPath, configuration.ChecksProjectPath);
            fixedValidator.FreezeTrustedProject(configuration.ProjectRoot); validator = fixedValidator;
        }
        // Separate adapter deliberately fails closed until BOTH independent quota sources are established.
        Task<QuotaGuardSnapshot> ReadExperimentalQuota(CancellationToken _) => Task.FromResult(new QuotaGuardSnapshot(
            QuotaGuardState.Unknown, null, null, false, false, "SIWC与Codex执行额度必须分别核验，尚无可信SIWC额度读数。"));
        service = new(dataRoot, siwc, codex, validator, ReadExperimentalQuota); service.Changed += () => Changed?.Invoke();
        SaveConfiguration(configuration);
        return (await service.RunAsync(configuration, token)).Message;
    }
    public void Pause() { try { runLifetime?.Cancel(); } catch (ObjectDisposedException) { } service?.Pause(); }
    public void Stop() { service?.Stop(); try { runLifetime?.Cancel(); } catch (ObjectDisposedException) { } Changed?.Invoke(); }
    internal Task<JsonElement[]> ReadOfficialGptCatalogAsync(CancellationToken token)
        => chat?.ReadOfficialToolCatalogAsync(token) ?? throw new InvalidOperationException("GPT尚未连接。");
    internal object ReadAcceptanceProviderEvidence() => new
    {
        Gpt = chat is null ? null : new { chat.TargetThreadId, chat.LastObservedTurnId, chat.LastCompletedTurnId,
            chat.LastReplySourceMessageId, chat.CurrentThreadStatus, chat.DiagnosticCode, CompletionSource = "DesktopProjection_NotServerNative" },
        Codex = codex is null ? null : new { codex.LastThreadId, codex.LastSessionId, codex.LastTurnId, codex.LastRunState, codex.DiagnosticCode,
            codex.ConnectionDiagnostics, CompletionSource = "OfficialAppServerNativeTurn" }
    };
    internal async Task<string> DisconnectAcceptanceLinksAsync(CancellationToken token = default)
    {
        if (acceptanceValidator is null || IsRunning || State.CurrentRequestId != Guid.Empty)
            throw new InvalidOperationException("仅独立空闲验收可断开自有通道。");
        if (!await connectionGate.WaitAsync(0, token)) throw new InvalidOperationException("连接核验尚未结束。");
        try
        {
            var gpt = chat is null ? new SafeStopResult(true, "未连接") : await chat.DisconnectForDiagnosticsAsync(token);
            var execution = codex is null ? new SafeStopResult(true, "未连接") : await codex.DisconnectForDiagnosticsAsync(token);
            if (!gpt.Accepted || !execution.Accepted) throw new InvalidOperationException("自有通道无法安全断开，未运行验收任务。");
            codexStatus = new(WorkflowLinkState.Disconnected, execution.Message);
            return gpt.Message + " " + execution.Message;
        }
        finally { connectionGate.Release(); Changed?.Invoke(); }
    }
    public string AcknowledgeRecovery() => service?.AcknowledgeRecovery().Message ?? "尚无工作流恢复记录。";
    void SaveConfiguration(WorkflowLoopConfiguration configuration)
    {
        var path = Path.Combine(dataRoot, "workflow", "settings.json");
        if (!AutomationStore.IsSafeDPath(path) || !AutomationStore.IsSafeDPath(path + ".bak")) throw new InvalidDataException("工作流设置必须在D盘普通路径。");
        AtomicStore.Write(path, configuration with { LocalExecutionAuthorized = false }); // Startup never auto-authorizes execution.
    }
    public static WorkflowLoopConfiguration? ReadConfiguration(string dataRoot)
    {
        var path = Path.Combine(dataRoot, "workflow", "settings.json");
        if (!AutomationStore.IsSafeDPath(path)) throw new InvalidDataException("工作流设置路径异常。");
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 24000) throw new InvalidDataException("工作流设置过大。");
        var value = JsonSerializer.Deserialize<WorkflowLoopConfiguration>(File.ReadAllText(path));
        if (value is not null && (value.GptBackend is not (WorkflowBackend.LegacyDesktop or WorkflowBackend.SiwcGptReviewer) || value.Siwc is null))
            throw new InvalidDataException("GPT Backend设置未知或不兼容，不回退。");
        return value is null ? null : value with { LocalExecutionAuthorized = false };
    }
    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel(); service?.Pause();
        if (activeRun is not null) try { await activeRun.WaitAsync(TimeSpan.FromSeconds(15)); } catch { }
        if (chat is not null) await chat.DisposeAsync();
        if (codex is not null) await codex.DisposeAsync();
        if (siwc is not null) await siwc.DisposeAsync(); siwcProtocol?.Dispose();
        lifetime.Dispose();
    }
    sealed class UnconfiguredValidator : IWorkflowProjectValidator
    {
        public Task<WorkflowValidationResult> ValidateAsync(string projectRoot, CancellationToken cancellationToken)
            => Task.FromResult(new WorkflowValidationResult(false, "尚未连接固定验证程序。"));
    }
}

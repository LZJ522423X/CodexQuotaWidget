using System.Diagnostics;
using System.Text.Json;
using CodexQuotaWidget.Core.Observation;

namespace CodexQuotaWidget.Core.ControlCenter;

/// <summary>
/// Explicitly configured bridge to one existing ChatGPT thread through the already installed
/// official Codex desktop MCP helper. Does not change MCP configuration, start an agent, or log text.
/// </summary>
public sealed class DesktopChatGptWorkflowConnector : IWorkflowConnector, ICodexGptRelayTransport, IAsyncDisposable
{
    const string ReadTool = "read_thread", SendTool = "send_message_to_thread", QuotaTool = "get_usage_limits";
    readonly string targetThreadId, callerThreadId, dataRoot;
    readonly TimeSpan replyTimeout;
    readonly SemaphoreSlim operationGate = new(1, 1);
    WorkflowRpcProcess? rpc;
    CancellationTokenSource? activeWait;
    Guid? activeTask;
    bool quotaAvailable;
    DesktopPipeCandidate[] candidates = [];
    string? selectedPipe;
    int? selectedOwner;
    long? selectedOwnerStartTicks;
    int disposed;
    readonly Dictionary<Guid, RelaySubmissionBinding> relaySubmissions = [];
    sealed class RelaySubmissionBinding(Guid workflowTaskId, string marker, HashSet<string> previousTurnIds)
    {
        public Guid WorkflowTaskId { get; } = workflowTaskId;
        public string Marker { get; } = marker;
        public HashSet<string> PreviousTurnIds { get; } = previousTurnIds;
        public bool SentAttempted { get; set; }
        public string? MessageId { get; set; }
        public string? TurnId { get; set; }
    }

    public DesktopChatGptWorkflowConnector(string targetThreadId, string callerThreadId,
        string dataRoot = @"D:\CodexQuotaWidgetData", TimeSpan? replyTimeout = null)
    {
        if (!Guid.TryParse(targetThreadId, out _) || !Guid.TryParse(callerThreadId, out _))
            throw new ArgumentException("Explicit existing thread identifiers required");
        if (!Path.IsPathFullyQualified(dataRoot) || Path.GetPathRoot(Path.GetFullPath(dataRoot))?.Equals(@"D:\", StringComparison.OrdinalIgnoreCase) != true)
            throw new ArgumentException("Workflow data must remain on D drive");
        this.targetThreadId = targetThreadId; this.callerThreadId = callerThreadId; this.dataRoot = Path.GetFullPath(dataRoot);
        this.replyTimeout = replyTimeout ?? TimeSpan.FromMinutes(20);
        if (this.replyTimeout <= TimeSpan.Zero || this.replyTimeout > TimeSpan.FromHours(1)) throw new ArgumentOutOfRangeException(nameof(replyTimeout));
    }

    public string Id => "desktop-chatgpt-mcp";
    public string TargetThreadId => targetThreadId;
    public WorkflowConnectionState ConnectionState { get; private set; } = WorkflowConnectionState.NotConnected;
    public bool CanExecute => ConnectionState == WorkflowConnectionState.Ready && rpc?.IsRunning == true;
    public string DiagnosticCode { get; private set; } = "not_connected";
    public string? LastObservedTurnId { get; private set; }
    public string? LastSentMessageId { get; private set; }
    public string LastCompletedTurnId { get; private set; } = "";
    public string LastReplySourceMessageId { get; private set; } = "";
    public string CurrentThreadStatus { get; private set; } = "unknown";
    public ChatGptConnectionStatus ConnectionStatus { get; private set; } = ChatGptConnectionStatus.NotConnected;
    public IReadOnlyList<DesktopPipeCandidate> CandidateDiagnostics => candidates;
    public string DiagnosticMessage => DiagnosticCode switch
    {
        "desktop_pipe_not_unique" => "多个经过身份验证的桌面实例均可读取目标 GPT 会话；请选择已验证连接，未随机接管会话。",
        "desktop_target_unavailable" => "未找到能读取指定 GPT 会话的已验证桌面通道；失效或身份不明的候选已排除。",
        "desktop_target_probe_timeout" => "官方桌面身份已确认，但目标会话核验超时；状态仍未确认，请重新连接。没有发送任务。",
        "desktop_pipe_candidate_limit" => "桌面通道数量超过安全上限；未从不完整候选集合选择连接。",
        "selected_pipe_not_verified" => "所选通道本次未通过桌面身份和目标会话验证。",
        "connect_timeout" => "GPT 重连超过 30 秒；已停止本次发现，未发送消息。",
        "desktop_pipe_owner_changed" => "桌面通道所属进程发生变化；连接已停止，需重新验证。",
        "ready_source_verified" => "GPT 桌面身份和指定会话已验证；连接就绪不代表任务已完成。",
        "chatgpt_reply_reference_unresolved" => "GPT 回复被官方接口标记为完成，但只返回正文引用且未提供正文或引用解析能力；小组件无法读取结果，已暂停，保留投递记录且不自动重发。",
        _ => "GPT 连接状态：" + DiagnosticCode
    };

    public Task<bool> ProbeAsync(CancellationToken cancellationToken = default) => ConnectAsync(cancellationToken);
    public Task<bool> ConnectAsync(CancellationToken cancellationToken = default) => ConnectCoreAsync(null, cancellationToken);
    public Task<bool> ReconnectAsync(string? selectedPipe = null, CancellationToken cancellationToken = default)
        => ConnectCoreAsync(selectedPipe, cancellationToken);
    /// <summary>Disconnects the owned idle helper while retaining the verified route and request bindings.</summary>
    public async Task<SafeStopResult> DisconnectForDiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        if (!await operationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return new(false, "GPT连接器忙，未中断请求。");
        try
        {
            if (activeTask is not null || activeWait is not null) return new(false, "GPT请求仍待确认，未关闭通道。");
            var previous = rpc; rpc = null;
            if (previous is not null) await previous.DisposeAsync().ConfigureAwait(false);
            ConnectionState = WorkflowConnectionState.NotConnected; ConnectionStatus = ChatGptConnectionStatus.NotConnected;
            DiagnosticCode = "diagnostic_transport_disconnected";
            return new(true, "仅本连接器的空闲GPT helper已断开；目标与来源绑定保留。");
        }
        finally { operationGate.Release(); }
    }

    /// <summary>Read the selected host's public MCP schema only; does not invoke a catalogued tool or send a message.</summary>
    public async Task<JsonElement[]> ReadOfficialToolCatalogAsync(CancellationToken cancellationToken = default)
    {
        if (!await operationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new WorkflowRpcException("chatgpt_catalog_operation_busy");
        try
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            if (!CanExecute) throw new WorkflowRpcException("chatgpt_catalog_connection_not_ready");
            await VerifySelectedOwnerAsync(cancellationToken).ConfigureAwait(false);
            ValidateThread(await ReadThreadAsync(cancellationToken, 1).ConfigureAwait(false));
            await VerifyCallerAsync(rpc!, cancellationToken).ConfigureAwait(false);
            await VerifySelectedOwnerAsync(cancellationToken).ConfigureAwait(false);
            var catalog = await rpc!.CallAsync("tools/list", new { }, cancellationToken, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            var entries = Value(catalog, "tools");
            if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() > 512)
                throw new WorkflowRpcException("mcp_catalog_invalid");
            var names = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<JsonElement>();
            foreach (var entry in entries.EnumerateArray())
            {
                var name = Text(entry, "name"); var description = Text(entry, "description") ?? "";
                var schema = Value(entry, "inputSchema");
                if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || !names.Add(name)
                    || description.Length > 65536 || schema.ValueKind != JsonValueKind.Object)
                    throw new WorkflowRpcException("mcp_catalog_invalid");
                result.Add(JsonSerializer.SerializeToElement(new { name, description, inputSchema = schema.Clone() }));
            }
            ValidateThread(await ReadThreadAsync(cancellationToken, 1).ConfigureAwait(false));
            await VerifyCallerAsync(rpc!, cancellationToken).ConfigureAwait(false);
            await VerifySelectedOwnerAsync(cancellationToken).ConfigureAwait(false);
            return result.ToArray();
        }
        catch (WorkflowRpcException error)
        {
            DiagnosticCode = error.Code;
            if (error.Code is "desktop_pipe_owner_changed" or "chatgpt_thread_not_verified" or "caller_thread_not_verified")
            { ConnectionState = WorkflowConnectionState.Faulted; ConnectionStatus = ChatGptConnectionStatus.Failed; }
            throw;
        }
        finally { operationGate.Release(); }
    }

    async Task<bool> ConnectCoreAsync(string? requestedPipe, CancellationToken cancellationToken)
    {
        if (!await operationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return false;
        var priorPipe = selectedPipe; var priorOwner = selectedOwner; var priorOwnerStartTicks = selectedOwnerStartTicks;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(30));
        var token = budget.Token;
        try
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            ConnectionStatus = ChatGptConnectionStatus.Connecting;
            DiagnosticCode = "desktop_discovering";
            // Reconnection to a healthy, already-bound route is a revalidation, not a new selection.
            if (rpc?.IsRunning == true && (requestedPipe is null || requestedPipe == selectedPipe))
            {
                await VerifySelectedOwnerAsync(token).ConfigureAwait(false);
                var current = await ReadThreadAsync(token, 1).ConfigureAwait(false);
                ValidateThread(current);
                await VerifyCallerAsync(rpc, token).ConfigureAwait(false);
                await VerifySelectedOwnerAsync(token).ConfigureAwait(false);
                ConnectionState = WorkflowConnectionState.Ready; DiagnosticCode = "ready_source_verified";
                ConnectionStatus = ChatGptConnectionStatus.Connected;
                return true;
            }
            var previous = rpc; rpc = null;
            if (previous is not null) await previous.DisposeAsync().ConfigureAwait(false);
            quotaAvailable = false; CurrentThreadStatus = "unknown";
            ConnectionState = WorkflowConnectionState.NotConnected;
            candidates = [];
            var paths = DesktopPipeDiscovery.Enumerate();
            if (paths.Count > DesktopPipeDiscovery.MaximumCandidates) throw new WorkflowRpcException("desktop_pipe_candidate_limit");
            if (requestedPipe is not null && !DesktopPipeDiscovery.IsSupportedPipe(requestedPipe))
                throw new WorkflowRpcException("selected_pipe_not_verified");
            using var parallel = new SemaphoreSlim(4, 4);
            var probed = await Task.WhenAll(paths.Select(async path =>
            {
                await parallel.WaitAsync(token).ConfigureAwait(false);
                try { return await ProbeCandidateAsync(path, token).ConfigureAwait(false); }
                finally { parallel.Release(); }
            })).ConfigureAwait(false);
            candidates = probed;
            var selection = ResolveVerifiedRoute(candidates, requestedPipe, priorPipe, priorOwner);
            if (selection.PipePath is null)
                throw new WorkflowRpcException(selection.DiagnosticCode == "desktop_target_unavailable"
                    && candidates.Any(item => item.IdentityVerified && item.ProbeCode is "candidate_timeout" or "rpc_timeout")
                    ? "desktop_target_probe_timeout" : selection.DiagnosticCode);
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            selectedPipe = selection.PipePath;
            selectedOwner = candidates.Single(item => item.PipePath == selectedPipe).OwnerProcessId;
            selectedOwnerStartTicks = ReadOwnerStartTicks(selectedOwner);
            if (requestedPipe is null && priorOwner is not null && selectedOwner == priorOwner
                && priorOwnerStartTicks != selectedOwnerStartTicks)
                throw new WorkflowRpcException("desktop_pipe_owner_changed");
            candidates = candidates.Select(item => item with { Selected = item.PipePath == selectedPipe }).ToArray();
            await VerifySelectedOwnerAsync(token).ConfigureAwait(false);
            rpc = new WorkflowRpcProcess(DiscoverStartInfo(selectedPipe) ?? throw new WorkflowRpcException(DiagnosticCode));
            var connectedRpc = rpc;
            rpc.Disconnected += () =>
            {
                if (!ReferenceEquals(rpc, connectedRpc)) return;
                ConnectionState = WorkflowConnectionState.NotConnected;
                ConnectionStatus = ChatGptConnectionStatus.NotConnected; DiagnosticCode = "desktop_bridge_disconnected";
            };
            quotaAvailable = await InitializeBridgeAsync(rpc, token).ConfigureAwait(false);
            var thread = await ReadThreadAsync(token, 1).ConfigureAwait(false);
            ValidateThread(thread);
            await VerifyCallerAsync(rpc, token).ConfigureAwait(false);
            await VerifySelectedOwnerAsync(token).ConfigureAwait(false);
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            ConnectionState = WorkflowConnectionState.Ready; DiagnosticCode = "ready_source_verified";
            ConnectionStatus = ChatGptConnectionStatus.Connected;
            return true;
        }
        catch (OperationCanceledException)
        {
            if (rpc is not null) await rpc.DisposeAsync().ConfigureAwait(false);
            rpc = null; quotaAvailable = false;
            selectedPipe = priorPipe; selectedOwner = priorOwner; selectedOwnerStartTicks = priorOwnerStartTicks;
            ConnectionState = WorkflowConnectionState.NotConnected;
            ConnectionStatus = cancellationToken.IsCancellationRequested ? ChatGptConnectionStatus.NotConnected : ChatGptConnectionStatus.Failed;
            DiagnosticCode = cancellationToken.IsCancellationRequested ? "connect_cancelled" : "connect_timeout"; return false;
        }
        catch (Exception error) when (error is WorkflowRpcException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            if (rpc is not null) await rpc.DisposeAsync().ConfigureAwait(false);
            rpc = null; quotaAvailable = false;
            selectedPipe = priorPipe; selectedOwner = priorOwner; selectedOwnerStartTicks = priorOwnerStartTicks;
            ConnectionState = WorkflowConnectionState.NotConnected;
            DiagnosticCode = error is WorkflowRpcException failure ? failure.Code : "desktop_bridge_unavailable";
            ConnectionStatus = DiagnosticCode == "desktop_pipe_not_unique" ? ChatGptConnectionStatus.Conflict : ChatGptConnectionStatus.Failed;
            return false;
        }
        finally { operationGate.Release(); }
    }

    /// <summary>A prior verified identity stays bound during recovery; a new owner needs explicit selection.</summary>
    public static DesktopPipeSelection ResolveVerifiedRoute(IReadOnlyList<DesktopPipeCandidate> routes,
        string? requestedPipe = null, string? priorPipe = null, int? priorOwner = null)
    {
        if (requestedPipe is not null || priorPipe is null || priorOwner is not > 0)
            return DesktopPipeSelection.Resolve(routes, requestedPipe);
        var samePath = routes.FirstOrDefault(item => item.PipePath == priorPipe && item.Verified);
        if (samePath is not null)
            return samePath.OwnerProcessId == priorOwner ? new(priorPipe, "bound_target_route_verified")
                : new(null, "desktop_pipe_owner_changed");
        var sameOwner = routes.Where(item => item.Verified && item.OwnerProcessId == priorOwner).ToArray();
        return sameOwner.Length > 0 ? DesktopPipeSelection.Resolve(sameOwner)
            : new(null, "selected_pipe_not_verified");
    }

    public async Task<QuotaSnapshot> ReadQuotaAsync(CancellationToken cancellationToken = default)
    {
        if (rpc?.IsRunning != true || !quotaAvailable) throw new WorkflowRpcException("quota_source_unavailable");
        var payload = await CallToolAsync(QuotaTool, new { }, cancellationToken).ConfigureAwait(false);
        if (Value(payload, "ordinaryUsageAllowed") is { ValueKind: JsonValueKind.False })
            throw new WorkflowRpcException("quota_usage_disallowed");
        try { return QuotaParser.Parse(payload, Text(Value(payload, "rateLimits"), "planType") ?? "Unknown"); }
        catch (ProtocolException) { throw new WorkflowRpcException("quota_payload_invalid"); }
    }

    public async Task<WorkflowExecutionResult> ExecuteAsync(WorkflowExecutionRequest request, CancellationToken cancellationToken = default)
    {
        WorkflowExecutionResult Outcome(WorkflowExecutionResult result) => CorrelateExecutionResult(result, request);
        if (request.Provider != WorkflowProvider.ChatGPT || request.Backend != WorkflowBackend.LegacyDesktop
            || request.TaskId == Guid.Empty || string.IsNullOrWhiteSpace(request.Goal) || request.Goal.Length > 65536)
            return Outcome(new(false, "ChatGPT任务参数无效；未发送任务") { DispatchPrevented = true });
        if (request.ContinuationId is not null && request.ContinuationId != targetThreadId)
            return Outcome(new(false, "目标会话与已配置会话不一致；未发送任务") { DispatchPrevented = true });
        if (!string.IsNullOrEmpty(request.ExpectedSessionId) && request.ExpectedSessionId != targetThreadId)
            return Outcome(new(false, "预期 GPT 会话与已配置会话不一致；未发送任务")
            { DispatchPrevented = true, DiagnosticCode = "chatgpt_expected_session_mismatch" });
        if (!await operationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return Outcome(new(false, "ChatGPT连接器正在处理任务；未重复发送") { DispatchPrevented = true });
        var sendStarted = false;
        var matchedTurnId = "";
        RelaySubmissionBinding? binding = null;
        LastObservedTurnId = null; LastSentMessageId = null;
        LastCompletedTurnId = ""; LastReplySourceMessageId = "";
        try
        {
            if (relaySubmissions.ContainsKey(request.RequestId))
                return Outcome(new(false, "该请求已尝试投递；保留原绑定，不会自动重发。") { DiagnosticCode = "chatgpt_request_already_attempted" });
            if (!CanExecute) return Outcome(new(false, "ChatGPT连接未验证；未发送任务") { DispatchPrevented = true });
            if (relaySubmissions.Count >= 256)
                return Outcome(new(false, "当前连接的请求证据达到上限；未丢弃旧绑定或发送。") { DispatchPrevented = true, DiagnosticCode = "chatgpt_request_limit" });
            using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            waiting.CancelAfter(replyTimeout); activeWait = waiting; activeTask = request.TaskId;
            await VerifySelectedOwnerAsync(waiting.Token).ConfigureAwait(false);
            var before = await ReadThreadAsync(waiting.Token).ConfigureAwait(false); ValidateThread(before);
            await VerifyCallerAsync(rpc!, waiting.Token).ConfigureAwait(false);
            await VerifySelectedOwnerAsync(waiting.Token).ConfigureAwait(false);
            if (ReadThreadStatus(before) != "idle")
                return Outcome(new(false, "目标ChatGPT会话尚未空闲；未发送任务") { DispatchPrevented = true });
            var previousIds = Turns(before).Select(turn => Text(turn, "id")).OfType<string>().ToHashSet(StringComparer.Ordinal);
            var marker = BuildRequestMarker(request);
            binding = new(request.WorkflowTaskId != Guid.Empty ? request.WorkflowTaskId : request.RequestId, marker, previousIds);
            relaySubmissions.Add(request.RequestId, binding);
            var prompt = marker + "\n" + request.Goal;
            ConnectionState = WorkflowConnectionState.Busy; DiagnosticCode = "chatgpt_sending";
            LastObservedTurnId = null; LastSentMessageId = null;
            sendStarted = true;
            binding.SentAttempted = true;
            var sent = await CallToolAsync(SendTool, new { threadId = targetThreadId, prompt }, waiting.Token).ConfigureAwait(false);
            var returnedThread = Text(sent, "threadId");
            if (returnedThread is not null && returnedThread != targetThreadId) throw new WorkflowRpcException("send_target_mismatch");
            LastSentMessageId = Text(sent, "userMessageId") ?? Text(sent, "messageId");
            binding.MessageId = LastSentMessageId;
            DiagnosticCode = "chatgpt_waiting_for_current_turn";
            while (true)
            {
                waiting.Token.ThrowIfCancellationRequested();
                if (rpc?.IsRunning != true) throw new WorkflowRpcException("desktop_bridge_disconnected");
                var snapshot = await ReadThreadAsync(waiting.Token).ConfigureAwait(false); ValidateThread(snapshot);
                var matches = Turns(snapshot).Where(turn => Text(turn, "id") is { Length: > 0 } id
                    && !previousIds.Contains(id) && HasRequestMarker(turn, marker, LastSentMessageId)).ToArray();
                if (matches.Length > 1) throw new WorkflowRpcException("chatgpt_turn_ambiguous");
                if (matches.Length == 1)
                {
                    var turn = matches[0]; var observedTurnId = Text(turn, "id") ?? "";
                    if (binding.TurnId is not null && binding.TurnId != observedTurnId)
                        throw new WorkflowRpcException("chatgpt_turn_changed");
                    matchedTurnId = observedTurnId; LastObservedTurnId = observedTurnId;
                    if (matchedTurnId.Length > 0) binding.TurnId ??= matchedTurnId;
                    var status = Text(turn, "status");
                    if (HasConfirmedCompletion(snapshot, turn))
                    {
                        LastCompletedTurnId = LastObservedTurnId ?? "";
                        LastReplySourceMessageId = ReadReplySourceMessageId(turn);
                        var answer = ReadCompletedAnswer(turn);
                        if (answer.Length == 0) throw new WorkflowRpcException("chatgpt_reply_missing");
                        if (LastReplySourceMessageId.Length == 0) throw new WorkflowRpcException("chatgpt_reply_source_missing");
                        ConnectionState = WorkflowConnectionState.Ready; DiagnosticCode = "chatgpt_current_turn_completed";
                        return Outcome(new(true, answer, targetThreadId)
                        { CompletedTurnId = LastCompletedTurnId, DiagnosticCode = DiagnosticCode, ReplySourceMessageId = LastReplySourceMessageId });
                    }
                    if (status is "failed" or "interrupted" or "cancelled" or "error")
                        throw new WorkflowRpcException("chatgpt_current_turn_failed");
                }
                await Task.Delay(TimeSpan.FromSeconds(2), waiting.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            DiagnosticCode = sendStarted ? "chatgpt_wait_cancelled_delivery_not_retried" : "chatgpt_cancelled_before_send";
            ConnectionState = rpc?.IsRunning == true ? WorkflowConnectionState.Ready : WorkflowConnectionState.NotConnected;
            return Outcome(new(false, sendStarted ? "ChatGPT等待已停止或超时；消息可能已送达，远程任务可能仍运行；不会自动重发" : "已取消；未发送ChatGPT任务", targetThreadId)
            { CompletedTurnId = LastCompletedTurnId, DiagnosticCode = DiagnosticCode, ReplySourceMessageId = LastReplySourceMessageId, DispatchPrevented = !sendStarted });
        }
        catch (Exception error) when (error is WorkflowRpcException or IOException or JsonException or InvalidOperationException)
        {
            DiagnosticCode = error is WorkflowRpcException failure ? failure.Code : "chatgpt_bridge_failed";
            ConnectionState = WorkflowConnectionState.Faulted;
            ConnectionStatus = ChatGptConnectionStatus.Failed;
            return Outcome(new(false, DiagnosticCode == "chatgpt_reply_reference_unresolved" ? DiagnosticMessage
                : sendStarted ? "ChatGPT本轮结果无法确认；不会自动重发（" + DiagnosticCode + "）" : "ChatGPT读取失败；未发送任务（" + DiagnosticCode + "）", targetThreadId)
            { CompletedTurnId = LastCompletedTurnId, DiagnosticCode = DiagnosticCode, ReplySourceMessageId = LastReplySourceMessageId, DispatchPrevented = !sendStarted });
        }
        finally
        {
            activeWait = null; activeTask = null;
            if (ConnectionState == WorkflowConnectionState.Busy) ConnectionState = WorkflowConnectionState.Ready;
            operationGate.Release();
        }
    }

    /// <summary>Local call context never asserts remote completion; completion IDs require a matched official turn.</summary>
    public static WorkflowExecutionResult CorrelateExecutionResult(WorkflowExecutionResult result, WorkflowExecutionRequest request)
        => result with
        {
            RequestId = request.RequestId, WorkflowTaskId = request.WorkflowTaskId,
            ProjectRoot = request.AuthorizedProjectRoot, AuthorizationId = request.AuthorizationId
        };

    /// <summary>Send once on the already verified connection, when an authorized Codex turn requests it.</summary>
    public async Task<GptRelaySubmission> SendFromCodexAsync(WorkflowExecutionRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Provider != WorkflowProvider.ChatGPT || request.RequestId == Guid.Empty || request.WorkflowTaskId == Guid.Empty
            || string.IsNullOrWhiteSpace(request.Goal) || request.Goal.Length > 65536
            || !string.IsNullOrEmpty(request.ExpectedSessionId) && request.ExpectedSessionId != targetThreadId
            || request.ContinuationId is not null && request.ContinuationId != targetThreadId)
            return new(false, "Codex 转发请求或目标会话无效；未发送。", "");
        if (!await operationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return new(false, "GPT 连接器正在处理其他操作；未发送。", "");
        RelaySubmissionBinding? binding = null;
        try
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            if (relaySubmissions.TryGetValue(request.RequestId, out var existing))
                return new(false, existing.WorkflowTaskId != request.WorkflowTaskId ? "请求 ID 已属于其他任务；拒绝复用。"
                    : "该请求已尝试投递；不会自动重发。", existing.MessageId ?? "");
            if (!CanExecute) return new(false, "GPT 连接尚未验证；不会自动重连或发送。", "");
            await VerifySelectedOwnerAsync(cancellationToken).ConfigureAwait(false);
            if (relaySubmissions.Count >= 256) return new(false, "当前连接的请求证据达到上限；未丢弃旧绑定或发送。", "");
            var before = await ReadThreadAsync(cancellationToken).ConfigureAwait(false); ValidateThread(before);
            await VerifyCallerAsync(rpc!, cancellationToken).ConfigureAwait(false);
            await VerifySelectedOwnerAsync(cancellationToken).ConfigureAwait(false);
            if (ReadThreadStatus(before) != "idle") return new(false, "指定 GPT 会话未确认空闲；未发送。", "");
            var previous = Turns(before).Select(turn => Text(turn, "id")).OfType<string>().ToHashSet(StringComparer.Ordinal);
            binding = new(request.WorkflowTaskId, BuildRequestMarker(request), previous);
            relaySubmissions.Add(request.RequestId, binding);
            ConnectionState = WorkflowConnectionState.Busy; DiagnosticCode = "chatgpt_relay_sending";
            // Retain the attempted binding even if cancellation or transport failure makes delivery unknown.
            binding.SentAttempted = true;
            var sent = await CallToolAsync(SendTool, new { threadId = targetThreadId, prompt = binding.Marker + "\n" + request.Goal }, cancellationToken).ConfigureAwait(false);
            if (Text(sent, "threadId") is { } returned && returned != targetThreadId) throw new WorkflowRpcException("send_target_mismatch");
            binding.MessageId = Text(sent, "userMessageId") ?? Text(sent, "messageId");
            LastSentMessageId = binding.MessageId;
            DiagnosticCode = "chatgpt_relay_submitted";
            return new(true, "已通过小组件投递 Codex 请求；等待对应 GPT 回合，未读取或推算正文。", binding.MessageId ?? "");
        }
        catch (OperationCanceledException)
        {
            DiagnosticCode = binding?.SentAttempted == true ? "chatgpt_relay_delivery_unknown" : "chatgpt_relay_cancelled_before_send";
            return new(false, binding?.SentAttempted == true ? "投递状态未知；保留请求绑定，不会重发。" : "投递前取消；未发送。", binding?.MessageId ?? "");
        }
        catch (Exception error) when (error is WorkflowRpcException or IOException or JsonException or InvalidOperationException)
        {
            DiagnosticCode = error is WorkflowRpcException failure ? failure.Code : "chatgpt_relay_transport_failed";
            if (DiagnosticCode is "desktop_pipe_owner_changed" or "chatgpt_thread_not_verified" or "caller_thread_not_verified" or "send_target_mismatch")
            { ConnectionState = WorkflowConnectionState.Faulted; ConnectionStatus = ChatGptConnectionStatus.Failed; }
            return new(false, binding?.SentAttempted == true ? "投递无法确认；保留请求绑定，不会重发（" + DiagnosticCode + "）。"
                : "请求核验失败；未发送（" + DiagnosticCode + "）。", binding?.MessageId ?? "");
        }
        finally
        {
            if (ConnectionState == WorkflowConnectionState.Busy)
                ConnectionState = rpc?.IsRunning == true ? WorkflowConnectionState.Ready : WorkflowConnectionState.NotConnected;
            operationGate.Release();
        }
    }

    public async Task<GptRelayObservation> ObserveForCodexAsync(Guid workflowTaskId, Guid requestId, CancellationToken cancellationToken = default)
    {
        if (!await operationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return RelayUnavailable("chatgpt_relay_operation_busy");
        try
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            if (!CanExecute) return RelayUnavailable("chatgpt_relay_connection_not_ready");
            await VerifySelectedOwnerAsync(cancellationToken).ConfigureAwait(false);
            if (!TryRelayBinding(workflowTaskId, requestId, out var binding)) return RelayUnavailable("chatgpt_relay_request_not_bound");
            var snapshot = await ReadThreadAsync(cancellationToken).ConfigureAwait(false); ValidateThread(snapshot);
            await VerifyCallerAsync(rpc!, cancellationToken).ConfigureAwait(false);
            var observation = CodexGptRelayProjection.Observe(snapshot, targetThreadId, binding.Marker,
                binding.PreviousTurnIds, binding.MessageId, binding.TurnId);
            if (observation.TurnId.Length > 0 && observation.DiagnosticCode != "chatgpt_relay_turn_changed") binding.TurnId ??= observation.TurnId;
            LastObservedTurnId = observation.TurnId.Length > 0 ? observation.TurnId : null;
            DiagnosticCode = observation.DiagnosticCode;
            return observation;
        }
        catch (OperationCanceledException) { return RelayUnavailable("chatgpt_relay_observe_cancelled"); }
        catch (Exception error) when (error is WorkflowRpcException or IOException or JsonException or InvalidOperationException)
        { return RelayFailure(error); }
        finally { operationGate.Release(); }
    }

    public async Task<GptRelayReply> ReadForCodexAsync(Guid workflowTaskId, Guid requestId, CancellationToken cancellationToken = default)
    {
        if (!await operationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return new(false, RelayUnavailable("chatgpt_relay_operation_busy"), default, false, "GPT 连接器正在处理其他操作；未读取。");
        try
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            if (!CanExecute) return new(false, RelayUnavailable("chatgpt_relay_connection_not_ready"), default, false, "GPT 连接尚未验证；未读取。");
            await VerifySelectedOwnerAsync(cancellationToken).ConfigureAwait(false);
            if (!TryRelayBinding(workflowTaskId, requestId, out var binding))
                return new(false, RelayUnavailable("chatgpt_relay_request_not_bound"), default, false, "任务与请求绑定不匹配；未提供会话内容。");
            var snapshot = await ReadThreadAsync(cancellationToken).ConfigureAwait(false); ValidateThread(snapshot);
            await VerifyCallerAsync(rpc!, cancellationToken).ConfigureAwait(false);
            var reply = CodexGptRelayProjection.Read(snapshot, targetThreadId, workflowTaskId, requestId,
                binding.Marker, binding.PreviousTurnIds, binding.MessageId, binding.TurnId);
            if (reply.Observation.TurnId.Length > 0 && reply.Observation.DiagnosticCode != "chatgpt_relay_turn_changed") binding.TurnId ??= reply.Observation.TurnId;
            DiagnosticCode = reply.Observation.DiagnosticCode;
            return reply;
        }
        catch (OperationCanceledException)
        { return new(false, RelayUnavailable("chatgpt_relay_read_cancelled"), default, false, "读取已取消；没有重发或推算结果。"); }
        catch (Exception error) when (error is WorkflowRpcException or IOException or JsonException or InvalidOperationException)
        { return new(false, RelayFailure(error), default, false, "对应 GPT 回合读取失败；没有提供其他回合或重发。"); }
        finally { operationGate.Release(); }
    }

    bool TryRelayBinding(Guid workflowTaskId, Guid requestId, out RelaySubmissionBinding binding)
    {
        binding = null!;
        return workflowTaskId != Guid.Empty && requestId != Guid.Empty && relaySubmissions.TryGetValue(requestId, out binding!)
            && binding.WorkflowTaskId == workflowTaskId && binding.SentAttempted;
    }
    GptRelayObservation RelayUnavailable(string code) => new(false, "unknown", "", "", code);
    GptRelayObservation RelayFailure(Exception error)
    {
        DiagnosticCode = error is WorkflowRpcException failure ? failure.Code : "chatgpt_relay_transport_failed";
        if (DiagnosticCode is "desktop_pipe_owner_changed" or "chatgpt_thread_not_verified" or "caller_thread_not_verified")
        { ConnectionState = WorkflowConnectionState.Faulted; ConnectionStatus = ChatGptConnectionStatus.Failed; }
        return RelayUnavailable(DiagnosticCode);
    }

    public Task<SafeStopResult> RequestSafeStopAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        if (activeTask == taskId) { try { activeWait?.Cancel(); } catch (ObjectDisposedException) { } }
        return Task.FromResult(new SafeStopResult(false, "仅停止本地等待；此接口不取消或关闭远程ChatGPT任务"));
    }

    async Task<DesktopPipeCandidate> ProbeCandidateAsync(string pipe, CancellationToken cancellationToken)
    {
        var total = Stopwatch.StartNew();
        var durations = new Dictionary<string, long>();
        var identity = await DesktopPipeDiscovery.InspectAsync(pipe, cancellationToken).ConfigureAwait(false);
        durations["native_identity"] = total.ElapsedMilliseconds;
        DesktopPipeCandidate Timed(DesktopPipeCandidate result)
        {
            durations["total_probe"] = total.ElapsedMilliseconds;
            return result with { StageMilliseconds = new System.Collections.ObjectModel.ReadOnlyDictionary<string, long>(new Dictionary<string, long>(durations)) };
        }
        if (!identity.IdentityVerified) return Timed(identity);
        var ownerStartTicks = ReadOwnerStartTicks(identity.OwnerProcessId);
        if (ownerStartTicks is null) return Timed(identity with { ProbeCode = "owner_generation_unavailable", Reason = "owner_generation_unavailable" });
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // The 30-second outer budget remains authoritative. Target reads must not inherit an
        // almost-expired six-second budget after a cold helper/catalog handshake.
        limit.CancelAfter(TimeSpan.FromSeconds(12));
        WorkflowRpcProcess? probe = null;
        var stage = "official_helper_start";
        var stageStart = total.ElapsedMilliseconds;
        try
        {
            var start = DiscoverStartInfo(pipe);
            if (start is null) return Timed(identity with { ProbeCode = DiagnosticCode, Reason = "official_helper_unavailable" });
            probe = new WorkflowRpcProcess(start);
            stage = "mcp_catalog";
            stageStart = total.ElapsedMilliseconds;
            await InitializeBridgeAsync(probe, limit.Token).ConfigureAwait(false);
            durations[stage] = total.ElapsedMilliseconds - stageStart;
            // The native host validates explicit caller metadata. A readable, exact target of kind
            // chatgpt is mandatory for EACH route; owner equality alone never proves equivalence.
            stage = "target_read";
            stageStart = total.ElapsedMilliseconds;
            var snapshot = await CallToolAsync(probe, ReadTool,
                new { threadId = targetThreadId, turnLimit = 1, includeOutputs = false, maxOutputCharsPerItem = 1 }, limit.Token).ConfigureAwait(false);
            durations[stage] = total.ElapsedMilliseconds - stageStart;
            if (!IsConfiguredTarget(snapshot, targetThreadId))
                return Timed(identity with { ProbeCode = "chatgpt_thread_not_verified", Reason = "target_identity_mismatch" });
            stage = "caller_read";
            stageStart = total.ElapsedMilliseconds;
            var caller = await CallToolAsync(probe, ReadTool,
                new { threadId = callerThreadId, turnLimit = 1, includeOutputs = false, maxOutputCharsPerItem = 1 }, limit.Token).ConfigureAwait(false);
            durations[stage] = total.ElapsedMilliseconds - stageStart;
            if (!IsConfiguredCaller(caller, callerThreadId))
                return Timed(identity with { ProbeCode = "caller_thread_not_verified", Reason = "caller_identity_mismatch" });
            var finalIdentity = await DesktopPipeDiscovery.InspectAsync(pipe, limit.Token).ConfigureAwait(false);
            if (!finalIdentity.IdentityVerified || finalIdentity.OwnerProcessId != identity.OwnerProcessId
                || ReadOwnerStartTicks(identity.OwnerProcessId) != ownerStartTicks)
                return Timed(identity with { ProbeCode = "desktop_pipe_owner_changed", Reason = "owner_changed_during_probe" });
            return Timed(identity with { TargetVerified = true, ProbeCode = "target_and_caller_verified", Reason = "exact_configured_target_and_caller_read_verified" });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            durations[stage] = total.ElapsedMilliseconds - stageStart;
            return Timed(identity with { ProbeCode = "candidate_timeout", Reason = stage + "_timeout" });
        }
        catch (Exception error) when (error is WorkflowRpcException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            durations[stage] = total.ElapsedMilliseconds - stageStart;
            return Timed(identity with { ProbeCode = error is WorkflowRpcException failure ? failure.Code : "candidate_read_failed", Reason = stage + "_rejected" });
        }
        finally { if (probe is not null) await probe.DisposeAsync().ConfigureAwait(false); }
    }

    async Task<bool> InitializeBridgeAsync(WorkflowRpcProcess bridge, CancellationToken cancellationToken)
    {
        await bridge.StartAsync(cancellationToken).ConfigureAwait(false);
        await bridge.CallAsync("initialize", new
        {
            protocolVersion = "2024-11-05", capabilities = new { },
            clientInfo = new { name = "CodexQuotaWidget", version = "1.11.0" }
        }, cancellationToken, TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        await bridge.NotifyAsync("notifications/initialized", cancellationToken: cancellationToken).ConfigureAwait(false);
        var catalog = await bridge.CallAsync("tools/list", new { }, cancellationToken, TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        var tools = Value(catalog, "tools");
        if (tools.ValueKind != JsonValueKind.Array) throw new WorkflowRpcException("mcp_catalog_invalid");
        var exact = tools.EnumerateArray().Select(item => Text(item, "name")).ToHashSet(StringComparer.Ordinal);
        if (!exact.Contains(ReadTool) || !exact.Contains(SendTool)) throw new WorkflowRpcException("mcp_required_tools_missing");
        return exact.Contains(QuotaTool);
    }

    async Task VerifySelectedOwnerAsync(CancellationToken cancellationToken)
    {
        if (selectedPipe is null || selectedOwner is null || selectedOwnerStartTicks is null)
            throw new WorkflowRpcException("desktop_pipe_unverified");
        var latest = await DesktopPipeDiscovery.InspectAsync(selectedPipe, cancellationToken).ConfigureAwait(false);
        if (!latest.IdentityVerified || latest.OwnerProcessId != selectedOwner || ReadOwnerStartTicks(selectedOwner) != selectedOwnerStartTicks)
            throw new WorkflowRpcException("desktop_pipe_owner_changed");
    }

    static long? ReadOwnerStartTicks(int? owner)
    {
        if (owner is not > 0) return null;
        try { using var child = Process.GetProcessById(owner.Value); return child.StartTime.ToUniversalTime().Ticks; }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception) { return null; }
    }

    async Task VerifyCallerAsync(WorkflowRpcProcess bridge, CancellationToken cancellationToken)
    {
        var caller = await CallToolAsync(bridge, ReadTool,
            new { threadId = callerThreadId, turnLimit = 1, includeOutputs = false, maxOutputCharsPerItem = 1 }, cancellationToken).ConfigureAwait(false);
        if (!IsConfiguredCaller(caller, callerThreadId)) throw new WorkflowRpcException("caller_thread_not_verified");
    }

    public static bool IsConfiguredCaller(JsonElement snapshot, string expectedThreadId)
        => Guid.TryParse(expectedThreadId, out _) && Text(Value(snapshot, "thread"), "id") == expectedThreadId
            && Text(Value(snapshot, "thread"), "kind") == "codex" && Value(snapshot, "turns").ValueKind == JsonValueKind.Array;

    Task<JsonElement> ReadThreadAsync(CancellationToken cancellationToken, int turnLimit = 10)
        => CallToolAsync(ReadTool, new { threadId = targetThreadId, turnLimit, includeOutputs = false, maxOutputCharsPerItem = turnLimit == 1 ? 1 : 20000 }, cancellationToken);

    async Task<JsonElement> CallToolAsync(string name, object arguments, CancellationToken cancellationToken)
        => await CallToolAsync(rpc ?? throw new WorkflowRpcException("desktop_bridge_unavailable"), name, arguments, cancellationToken).ConfigureAwait(false);

    async Task<JsonElement> CallToolAsync(WorkflowRpcProcess bridge, string name, object arguments, CancellationToken cancellationToken)
    {
        if (name is not (ReadTool or SendTool or QuotaTool)) throw new WorkflowRpcException("mcp_tool_not_allowed");
        var envelope = await bridge.CallAsync("tools/call", new Dictionary<string, object?>
        {
            ["name"] = name, ["arguments"] = arguments,
            ["_meta"] = new Dictionary<string, string> { ["openai/threadId"] = callerThreadId }
        }, cancellationToken, name == SendTool ? TimeSpan.FromSeconds(45) : TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        if (Value(envelope, "isError").ValueKind == JsonValueKind.True) throw new WorkflowRpcException("mcp_tool_failed");
        var content = Value(envelope, "content");
        if (content.ValueKind != JsonValueKind.Array) throw new WorkflowRpcException("mcp_response_invalid");
        foreach (var item in content.EnumerateArray())
        {
            if (Text(item, "type") != "text" || Text(item, "text") is not { } text) continue;
            if (text.Length > WorkflowRpcProcess.MaximumFrameBytes) throw new WorkflowRpcException("mcp_payload_limit");
            try
            {
                using var document = JsonDocument.Parse(text, new() { MaxDepth = 64 });
                if (document.RootElement.ValueKind == JsonValueKind.Object) return document.RootElement.Clone();
            }
            catch (JsonException) { throw new WorkflowRpcException("mcp_payload_invalid"); }
        }
        throw new WorkflowRpcException("mcp_payload_missing");
    }

    void ValidateThread(JsonElement snapshot)
    {
        if (!IsConfiguredTarget(snapshot, targetThreadId))
            throw new WorkflowRpcException("chatgpt_thread_not_verified");
        if (Value(snapshot, "turns").ValueKind != JsonValueKind.Array) throw new WorkflowRpcException("chatgpt_turns_invalid");
        CurrentThreadStatus = ReadThreadStatus(snapshot);
    }
    public static bool IsConfiguredTarget(JsonElement snapshot, string expectedThreadId)
        => Guid.TryParse(expectedThreadId, out _) && Text(Value(snapshot, "thread"), "id") == expectedThreadId
            && Text(Value(snapshot, "thread"), "kind") == "chatgpt" && Value(snapshot, "turns").ValueKind == JsonValueKind.Array;
    public static string BuildRequestMarker(WorkflowExecutionRequest request)
        => $"[AIControlCenter task={(request.WorkflowTaskId != Guid.Empty ? request.WorkflowTaskId : request.TaskId):D} request={request.RequestId:D}]";
    public static string ReadThreadStatus(JsonElement snapshot)
    {
        // Native desktop uses a discriminated status object. Do not infer idle from missing state.
        var status = Value(Value(snapshot, "thread"), "status");
        var kind = status.ValueKind == JsonValueKind.String ? status.GetString() : Text(status, "type");
        return kind switch { "idle" => "idle", "running" => "running", "active" => "active", "waiting" => "waiting", _ => "unknown" };
    }
    public static bool HasConfirmedCompletion(JsonElement snapshot, JsonElement turn)
        // ChatGPT's turn projection may say "completed" while the conversation is still
        // generating. A matched request plus an idle conversation is required as well.
        => Text(turn, "status") == "completed" && ReadThreadStatus(snapshot) == "idle";
    static IEnumerable<JsonElement> Turns(JsonElement snapshot) => Value(snapshot, "turns").EnumerateArray();
    static bool HasRequestMarker(JsonElement turn, string marker, string? sentMessageId)
    {
        var items = Value(turn, "items");
        if (items.ValueKind != JsonValueKind.Array) return false;
        foreach (var item in items.EnumerateArray())
        {
            if (Text(item, "type") != "userMessage" || sentMessageId is not null && Text(item, "id") != sentMessageId) continue;
            var content = Value(item, "content");
            if (content.ValueKind == JsonValueKind.Array && content.EnumerateArray().Any(piece => Text(piece, "type") == "text"
                    && Text(piece, "text") is { } text && text.StartsWith(marker + "\n", StringComparison.Ordinal))) return true;
        }
        return false;
    }
    public static string ReadCompletedAnswer(JsonElement turn)
    {
        var result = new List<string>();
        var items = Value(turn, "items");
        if (items.ValueKind != JsonValueKind.Array) return "";
        foreach (var item in items.EnumerateArray())
        {
            if (Text(item, "type") != "agentMessage") continue;
            if (Value(item, "truncated").ValueKind == JsonValueKind.True) throw new WorkflowRpcException("chatgpt_reply_truncated");
            if (Text(item, "text") is { Length: > 0 } text)
            {
                if (text.Contains("::chatgpt-content-reference{", StringComparison.Ordinal))
                    throw new WorkflowRpcException("chatgpt_reply_reference_unresolved");
                result.Add(text);
            }
        }
        var answer = string.Join("\n\n", result);
        if (answer.Length > 65536) throw new WorkflowRpcException("chatgpt_reply_limit");
        return answer;
    }

    public static string ReadReferenceSourceMessageId(JsonElement turn)
    {
        // Reference IDs are evidence only. Never infer an answer from their index or ID,
        // nor fetch private conversation endpoints to bypass the official projection.
        var items = Value(turn, "items");
        if (items.ValueKind != JsonValueKind.Array) return "";
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items.EnumerateArray())
        {
            if (Text(item, "type") != "agentMessage" || Text(item, "text") is not { } text
                || text.Length > 65536 || !text.Contains("::chatgpt-content-reference{", StringComparison.Ordinal)) continue;
            var match = System.Text.RegularExpressions.Regex.Match(text,
                "::chatgpt-content-reference\\{[^}]*source_message_id=\"(?<id>[0-9a-fA-F-]{36})\"[^}]*\\}",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            if (match.Success && Guid.TryParse(match.Groups["id"].Value, out var id)) ids.Add(id.ToString("D"));
        }
        return ids.Count == 1 ? ids.Single() : "";
    }

    public static string ReadReplySourceMessageId(JsonElement turn)
    {
        var items = Value(turn, "items");
        if (items.ValueKind != JsonValueKind.Array) return "";
        if (items.EnumerateArray().Any(item => Text(item, "type") == "agentMessage"
            && Text(item, "text")?.Contains("::chatgpt-content-reference{", StringComparison.Ordinal) == true))
            return ReadReferenceSourceMessageId(turn);
        var ids = items.EnumerateArray().Where(item => Text(item, "type") == "agentMessage")
            .Select(item => Text(item, "id")).OfType<string>().Where(id => id.Length is > 0 and <= 128)
            .Distinct(StringComparer.Ordinal).ToArray();
        return ids.Length == 1 ? ids[0] : "";
    }

    ProcessStartInfo? DiscoverStartInfo(string pipe)
    {
        try
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var serverRoot = Path.Combine(profile, ".codex", "plugins", "cache", "openai-bundled", "codex-app-tools");
            var nodeRoot = Path.Combine(local, "OpenAI", "Codex", "runtimes", "cua_node");
            if (!CodexSessionPaths.IsSafeLocalDirectory(serverRoot) || !CodexSessionPaths.IsSafeLocalDirectory(nodeRoot))
            { DiagnosticCode = "official_desktop_helper_missing"; return null; }
            var servers = Directory.EnumerateDirectories(serverRoot).Take(9).Select(path => (Path: path, Version: Version.TryParse(Path.GetFileName(path), out var version) ? version : null))
                .Where(item => item.Version is not null && CodexSessionPaths.IsSafeLocalPath(Path.Combine(item.Path, "server.mjs"), false)).ToArray();
            if (servers.Length == 0 || servers.Length > 8) { DiagnosticCode = "official_helper_version_ambiguous"; return null; }
            var server = Path.Combine(servers.OrderByDescending(item => item.Version).First().Path, "server.mjs");
            var node = Environment.GetEnvironmentVariable("CODEX_MCP_NODE_PATH");
            if (string.IsNullOrWhiteSpace(node))
            {
                var candidates = Directory.EnumerateDirectories(nodeRoot).Take(9).Select(path => Path.Combine(path, "bin", "node.exe"))
                    .Where(path => CodexSessionPaths.IsSafeLocalPath(path, false)).ToArray();
                if (candidates.Length != 1) { DiagnosticCode = "official_node_ambiguous"; return null; }
                node = candidates[0];
            }
            if (!Path.GetFullPath(node).StartsWith(nodeRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(node) != "node.exe" || !CodexSessionPaths.IsSafeLocalPath(node, false))
            { DiagnosticCode = "official_node_unverified"; return null; }
            if (!DesktopPipeDiscovery.IsSupportedPipe(pipe)) { DiagnosticCode = "desktop_pipe_unverified"; return null; }
            var temp = Path.Combine(dataRoot, "workflow", "temp");
            if (!SafeCreationPath(temp))
            { DiagnosticCode = "workflow_data_path_unverified"; return null; }
            Directory.CreateDirectory(temp);
            if (!CodexSessionPaths.IsSafeLocalDirectory(temp)) { DiagnosticCode = "workflow_temp_path_unverified"; return null; }
            var info = new ProcessStartInfo(node) { WorkingDirectory = dataRoot, UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add(server);
            info.Environment["CODEX_APP_TOOLS_PIPE_PATH"] = pipe;
            info.Environment["TEMP"] = temp; info.Environment["TMP"] = temp;
            return info;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { DiagnosticCode = "official_bridge_discovery_failed"; return null; }
    }
    static bool SafeCreationPath(string path)
    {
        // Check existing ancestors before creating anything: a D junction must never redirect writes to C.
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        if (!root.Equals(@"D:\", StringComparison.OrdinalIgnoreCase) || !Directory.Exists(root)) return false;
        var current = root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (File.Exists(current)) return false;
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
        }
        return true;
    }
    static JsonElement Value(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var found) ? found : default;
    static string? Text(JsonElement value, string name) => Value(value, name) is { ValueKind: JsonValueKind.String } found ? found.GetString() : null;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { activeWait?.Cancel(); } catch (ObjectDisposedException) { }
        if (rpc is not null) await rpc.DisposeAsync().ConfigureAwait(false);
        ConnectionState = WorkflowConnectionState.NotConnected; DiagnosticCode = "connector_disposed";
        ConnectionStatus = ChatGptConnectionStatus.NotConnected;
    }
}

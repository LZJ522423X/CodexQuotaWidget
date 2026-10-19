using System.Text.Json;
using System.Text.Json.Serialization;
using CodexQuotaWidget.Core.Automation;

namespace CodexQuotaWidget.Core.ControlCenter;

public enum CodexLedArithmeticPhase { Idle, Running, WaitingForGpt, Completed, Paused }
public sealed record CodexLedArithmeticTurn(string Stage, Guid RequestId, string Result, string SessionId,
    string CompletedTurnId, DateTimeOffset CompletedAt);
public sealed record CodexLedArithmeticState
{
    public int SchemaVersion { get; init; } = 1;
    public Guid WorkflowTaskId { get; init; }
    public CodexLedArithmeticPhase Phase { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public string ProjectRoot { get; init; } = "";
    public string GptThreadId { get; init; } = "";
    public string CallerThreadId { get; init; } = "";
    public string CodexSessionId { get; init; } = "";
    public string Stage { get; init; } = "";
    public Guid PendingCodexRequestId { get; init; }
    public Guid PendingGptRequestId { get; init; }
    public bool NeedsRecoveryConfirmation { get; init; }
    public string Message { get; init; } = "未执行算术往返。";
    public CodexLedArithmeticTurn[] Turns { get; init; } = [];
    public CodexGptToolCallEvidence[] ToolCalls { get; init; } = [];
    public GptRelayObservation[] GptObservations { get; init; } = [];
    public JsonElement[] GptReadEvidence { get; init; } = [];
}

/// <summary>Widget schedules real native Codex turns. Codex, not the widget or root agent, sends and reads GPT.</summary>
public sealed class CodexLedArithmeticService
{
    readonly IWorkflowConnector codex;
    readonly CodexGptRelayBridge bridge;
    readonly Func<CancellationToken, Task<QuotaGuardSnapshot>> quota;
    readonly string project, gpt, caller, directory;
    readonly object stateGate = new();
    CodexLedArithmeticState state;
    int running;
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    public CodexLedArithmeticState State { get { lock (stateGate) return state; } }
    public string StatePath { get; }
    public bool IsRunning => Volatile.Read(ref running) != 0;
    public event Action? Changed;
    public CodexLedArithmeticService(string dataRoot, IWorkflowConnector codex, CodexGptRelayBridge bridge,
        Func<CancellationToken, Task<QuotaGuardSnapshot>> quota, string projectRoot, string gptId, string callerId)
    {
        if (!AutomationStore.IsSafeDPath(dataRoot) || !AutomationStore.IsSafeDPath(projectRoot)
            || !Directory.Exists(projectRoot) || !Guid.TryParse(gptId, out _) || !Guid.TryParse(callerId, out _))
            throw new ArgumentException("Arithmetic relay requires an existing safe D project and fixed conversation identities.");
        this.codex = codex; this.bridge = bridge; this.quota = quota;
        project = Path.GetFullPath(projectRoot).TrimEnd('\\'); gpt = gptId; caller = callerId;
        bridge.BindTarget(gptId);
        directory = Path.Combine(Path.GetFullPath(dataRoot), "workflow");
        StatePath = Path.Combine(directory, "codex-led-arithmetic-state.json");
        state = ReadState();
        bridge.Changed += SaveToolEvidence;
    }

    public async Task<ControlOperationResult> RunAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0) return new(false, "算术往返正在运行，不重复派发。");
        FileStream? lease = null;
        try
        {
            ValidatePaths();
            Directory.CreateDirectory(directory);
            try { lease = new(Path.Combine(directory, "codex-led-arithmetic-run.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
            catch (IOException) { return new(false, "已有运行实例；未派发。"); }
            state = ReadState();
            if (state.WorkflowTaskId != Guid.Empty)
                return new(false, "此隔离任务已有执行记录；保留证据，不自动重发。", state.WorkflowTaskId);
            if (!codex.CanExecute) return new(false, "Codex 官方连接不可执行；未派发。");
            await EnsureQuota(cancellationToken).ConfigureAwait(false);
            Save(state with { WorkflowTaskId = Guid.NewGuid(), ProjectRoot = project, GptThreadId = gpt, CallerThreadId = caller,
                Phase = CodexLedArithmeticPhase.Running, Message = "小组件开始调度；Codex 负责真实发送与读取 GPT。" });
            bridge.Disable();
            await CodexTurn("codex_initial_2", "只计算1+1。仅回复计算结果的数字。不使用任何工具，不读写文件，不执行电脑或系统动作。", "2", cancellationToken).ConfigureAwait(false);
            foreach (var value in new[] { "2", "4" })
            {
                var gptRequest = Guid.NewGuid();
                Save(state with { PendingGptRequestId = gptRequest });
                bridge.ConfigureSend(state.WorkflowTaskId, gptRequest, value);
                await CodexTurn("codex_send_" + value,
                    "本小组件上一轮 Codex 的真实完成结果为 " + value + ". 必须调用 acc_gpt.send_result，result严格为\"" + value +
                    "\"，由这个工具将它发送给已绑定 GPT。禁止其他工具与电脑动作。收到真实发送回执后仅回复DISPATCHED；发送失败仅回复BLOCKED。", "DISPATCHED", cancellationToken).ConfigureAwait(false);
                if (bridge.LastSubmission is not { Success: true } || !HasOwnedTool("send_result", "sent_by_codex_tool"))
                    throw new InvalidDataException("Codex 未提供匹配的真实发送工具证据；未等待或猜测 GPT。");
                Save(state with { Phase = CodexLedArithmeticPhase.WaitingForGpt, Message = "Codex 已发送 " + value + "；小组件仅观察 GPT 完成状态。" });
                var completed = await WaitForGpt(gptRequest, cancellationToken).ConfigureAwait(false);
                bridge.ConfigureRead(state.WorkflowTaskId, gptRequest, completed);
                var next = value == "2" ? "4" : "6";
                await CodexTurn("codex_read_gpt_after_" + value,
                    "必须调用 acc_gpt.read_reply 读取上一次匹配 GPT 的真实回复。只有 readableBody=true 且可读到 GPT 正文数字时，才对 GPT 的数字加1，仅回复该计算结果。" +
                    "若仅有正文引用、正文不可读、无法确认数字或工具失败，仅回复BLOCKED。禁止根据之前输入或预期结果推算 GPT 的回答。禁止其他工具与电脑动作。",
                    next, cancellationToken, requireRead: true).ConfigureAwait(false);
                Save(state with { PendingGptRequestId = Guid.Empty });
            }
            Save(state with { Phase = CodexLedArithmeticPhase.Completed, NeedsRecoveryConfirmation = false,
                Stage = "completed_at_6", Message = "小组件调度的真实往返已完成到6；不再派发。" });
            return new(true, state.Message, state.WorkflowTaskId);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (state.WorkflowTaskId == Guid.Empty) return new(false, WorkflowText.Safe(error.Message, 1000));
            Save(state with { Phase = CodexLedArithmeticPhase.Paused, NeedsRecoveryConfirmation = true,
                ToolCalls = bridge.Evidence.ToArray(), Message = "已暂停：" + WorkflowText.Safe(error.Message, 1000) });
            return new(false, state.Message, state.WorkflowTaskId);
        }
        finally { lease?.Dispose(); Volatile.Write(ref running, 0); }
    }

    async Task CodexTurn(string stage, string prompt, string expected, CancellationToken token, bool requireRead = false)
    {
        await EnsureQuota(token).ConfigureAwait(false);
        var requestId = Guid.NewGuid();
        Save(state with { Phase = CodexLedArithmeticPhase.Running, Stage = stage, PendingCodexRequestId = requestId,
            Message = "小组件派发原生 Codex Turn：" + stage });
        var result = await codex.ExecuteAsync(new WorkflowExecutionRequest(requestId, WorkflowProvider.Codex, prompt,
            string.IsNullOrEmpty(state.CodexSessionId) ? null : state.CodexSessionId) { WorkflowTaskId = state.WorkflowTaskId }, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!result.Success || !ValidNativeId(result.ContinuationId) || !ValidNativeId(result.CompletedTurnId)
            || (state.CodexSessionId.Length > 0 && result.ContinuationId != state.CodexSessionId)
            || state.Turns.Any(turn => turn.CompletedTurnId == result.CompletedTurnId))
            throw new InvalidDataException("Codex 未真实完成或 Session/Turn 身份不匹配：" + WorkflowText.Safe(result.Message, 300));
        var text = result.Message.Trim();
        var turn = new CodexLedArithmeticTurn(stage, requestId, WorkflowText.Safe(text, 30), result.ContinuationId!, result.CompletedTurnId, DateTimeOffset.UtcNow);
        Save(state with { CodexSessionId = result.ContinuationId!, PendingCodexRequestId = Guid.Empty,
            Turns = state.Turns.Append(turn).ToArray(), ToolCalls = bridge.Evidence.ToArray() });
        if (text == "BLOCKED") throw new InvalidDataException("Codex 真实返回 BLOCKED：GPT 正文或发送证据不可用，未猜测下一数字。");
        if (text != expected) throw new InvalidDataException("Codex 回复不符合本轮协议，未继续转发。");
        if (requireRead && (!bridge.ReadBodyAvailable || !HasOwnedTool("read_reply", "read_by_codex_tool")))
            throw new InvalidDataException("虽返回数字，但缺少可读 GPT 正文及 Codex 实际读取证据；拒绝推测结果。");
    }
    bool HasOwnedTool(string tool, string status)
    {
        var last = state.Turns.Last();
        return bridge.Evidence.Any(item => item.Tool == tool && item.Status == status
            && item.WorkflowTaskId == state.WorkflowTaskId && item.GptRequestId == state.PendingGptRequestId
            && item.CallId.StartsWith(last.SessionId + "/" + last.CompletedTurnId + "/", StringComparison.Ordinal));
    }
    async Task<GptRelayObservation> WaitForGpt(Guid request, CancellationToken token)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
        wait.CancelAfter(TimeSpan.FromMinutes(2));
        while (true)
        {
            var observation = await bridge.Transport.ObserveForCodexAsync(state.WorkflowTaskId, request, wait.Token).ConfigureAwait(false);
            if (!state.GptObservations.Contains(observation)) Save(state with { GptObservations = state.GptObservations.Append(observation).TakeLast(120).ToArray() });
            if (observation.Ready)
            {
                if (!ValidNativeId(observation.TurnId) || string.IsNullOrWhiteSpace(observation.SourceMessageId))
                    throw new InvalidDataException("GPT 完成观察缺少 Turn/消息关联证据。");
                return observation;
            }
            if (observation.ThreadStatus is "failed" or "interrupted" or "cancelled"
                || observation.DiagnosticCode is "relay_identity_mismatch" or "relay_request_unknown"
                    or "chatgpt_relay_request_not_bound" or "chatgpt_relay_turn_changed" or "chatgpt_turn_ambiguous"
                    or "chatgpt_current_turn_failed" or "chatgpt_thread_not_verified" or "desktop_pipe_owner_changed")
                throw new InvalidDataException("GPT 请求无法继续：" + WorkflowText.Safe(observation.DiagnosticCode, 200));
            await Task.Delay(TimeSpan.FromSeconds(1), wait.Token).ConfigureAwait(false);
        }
    }
    async Task EnsureQuota(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!CanDispatch(await quota(token).ConfigureAwait(false))) throw new InvalidDataException("官方额度未知、异常或剩余不大于20%；禁止新派发。");
    }
    public static bool CanDispatch(QuotaGuardSnapshot guard) => guard.CanStart && guard.State is QuotaGuardState.Ready or QuotaGuardState.Warning
        && guard.FiveHourRemaining is double remaining && double.IsFinite(remaining) && remaining > 20 && remaining <= 100;
    static bool ValidNativeId(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128
        && !value.Any(char.IsControl) && !WorkflowText.HasSecret(value);
    void SaveToolEvidence() => Save(State with { ToolCalls = bridge.Evidence.ToArray(), GptReadEvidence = bridge.ReadEvidence.ToArray() });
    void Save(CodexLedArithmeticState next)
    {
        lock (stateGate)
        {
            next = next with { UpdatedAt = DateTimeOffset.UtcNow };
            ValidatePaths();
            Directory.CreateDirectory(directory);
            var temporary = StatePath + ".tmp";
            ValidatePaths();
            File.WriteAllText(temporary, JsonSerializer.Serialize(next, Options));
            File.Move(temporary, StatePath, true);
            state = next;
        }
        Changed?.Invoke();
    }
    CodexLedArithmeticState ReadState()
    {
        ValidatePaths();
        if (!File.Exists(StatePath)) return new();
        if (new FileInfo(StatePath).Length > 256 * 1024) throw new InvalidDataException("Relay state exceeds safe bounds.");
        var saved = JsonSerializer.Deserialize<CodexLedArithmeticState>(File.ReadAllText(StatePath), Options)
            ?? throw new InvalidDataException("Relay state invalid.");
        if (saved.SchemaVersion != 1 || saved.WorkflowTaskId == Guid.Empty || saved.Turns is null || saved.ToolCalls is null || saved.GptObservations is null
            || !string.Equals(saved.ProjectRoot, project, StringComparison.OrdinalIgnoreCase) || saved.GptThreadId != gpt || saved.CallerThreadId != caller
            || saved.Turns.Any(turn => !ValidNativeId(turn.SessionId) || !ValidNativeId(turn.CompletedTurnId) || turn.RequestId == Guid.Empty)
            || saved.Turns.Select(turn => turn.CompletedTurnId).Distinct(StringComparer.Ordinal).Count() != saved.Turns.Length)
            throw new InvalidDataException("Relay identity/evidence mismatch; retained without replay.");
        return saved;
    }
    void ValidatePaths()
    {
        foreach (var path in new[] { directory, StatePath, StatePath + ".tmp", Path.Combine(directory, "codex-led-arithmetic-run.lock") })
            if (!AutomationStore.IsSafeDPath(path) || path != directory && Directory.Exists(path))
                throw new InvalidDataException("Codex调度验收路径异常；保留原状态。");
    }
}

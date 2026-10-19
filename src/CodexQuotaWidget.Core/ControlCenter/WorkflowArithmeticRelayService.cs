using System.Text.Json;
using System.Text.Json.Serialization;
using CodexQuotaWidget.Core.Automation;

namespace CodexQuotaWidget.Core.ControlCenter;

public enum WorkflowArithmeticRelayPhase { Idle, Running, Paused, Completed }
public sealed record WorkflowArithmeticRelayIdentity(string ProjectRoot, string ChatGptThreadId, string CallerThreadId);
public sealed record WorkflowArithmeticRelayEvidence(
    int Step, WorkflowProvider Provider, Guid RequestId, string Result, string ContinuationId,
    string CompletedTurnId, DateTimeOffset ConfirmedAt);
public sealed record WorkflowArithmeticRelayState
{
    public int SchemaVersion { get; init; } = 1;
    public Guid WorkflowTaskId { get; init; }
    public WorkflowArithmeticRelayPhase Phase { get; init; }
    public string ProjectRoot { get; init; } = "";
    public string ChatGptThreadId { get; init; } = "";
    public string CallerThreadId { get; init; } = "";
    public Guid PendingRequestId { get; init; }
    public WorkflowProvider? PendingProvider { get; init; }
    public bool NeedsRecoveryConfirmation { get; init; }
    public string CodexContinuationId { get; init; } = "";
    public string ChatGptContinuationId { get; init; } = "";
    public string LastDiagnosticCode { get; init; } = "";
    public string LastCompletedTurnId { get; init; } = "";
    public string ReplySourceMessageId { get; init; } = "";
    public WorkflowArithmeticRelayEvidence[] Evidence { get; init; } = [];
    public DateTimeOffset UpdatedAt { get; init; }
    public string Message { get; init; } = "尚未执行隔离算术验收。";
}

/// <summary>
/// Product-owned, fixed arithmetic acceptance loop. Only successful provider replies are
/// relayed; expected numbers validate replies and are never substituted for model output.
/// This service owns no connection discovery, model selection, file tools or power actions.
/// </summary>
public sealed class WorkflowArithmeticRelayService
{
    readonly IWorkflowConnector chatGpt, codex;
    readonly Func<CancellationToken, Task<QuotaGuardSnapshot>> quota;
    readonly WorkflowArithmeticRelayIdentity identity;
    readonly string directory, statePath, lockPath;
    readonly object gate = new();
    WorkflowArithmeticRelayState state;
    bool running;
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    public event Action? Changed;
    public WorkflowArithmeticRelayState State { get { lock (gate) return state with { Evidence = state.Evidence.ToArray() }; } }
    public bool IsRunning { get { lock (gate) return running; } }
    public string StatePath => statePath;

    public WorkflowArithmeticRelayService(string dataRoot, IWorkflowConnector chatGpt, IWorkflowConnector codex,
        Func<CancellationToken, Task<QuotaGuardSnapshot>> quota,
        string projectRoot = "", string chatGptThreadId = "", string callerThreadId = "")
    {
        if (!AutomationStore.IsSafeDPath(dataRoot)) throw new ArgumentException("算术验收数据必须在D盘普通路径。", nameof(dataRoot));
        this.chatGpt = chatGpt; this.codex = codex; this.quota = quota;
        identity = new(projectRoot, chatGptThreadId, callerThreadId);
        if ((projectRoot.Length > 0 || chatGptThreadId.Length > 0 || callerThreadId.Length > 0)
            && (!AutomationStore.IsSafeDPath(projectRoot) || !Directory.Exists(projectRoot)
                || !Guid.TryParse(chatGptThreadId, out _) || !Guid.TryParse(callerThreadId, out _)))
            throw new ArgumentException("算术验收项目或会话身份无效。");
        directory = Path.Combine(Path.GetFullPath(dataRoot), "workflow");
        statePath = Path.Combine(directory, "arithmetic-relay-state.json");
        lockPath = Path.Combine(directory, "arithmetic-relay-run.lock");
        state = ReadState();
    }

    public async Task<ControlOperationResult> RunAsync(CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            if (running) return new(false, "算术循环已运行，不会重复派发。");
            running = true;
        }
        FileStream? lease = null;
        bool ownsState = false;
        try
        {
            ValidatePaths(); Directory.CreateDirectory(directory); ValidatePaths();
            lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            var persisted = ReadState();
            lock (gate) state = persisted;
            if (persisted.Phase == WorkflowArithmeticRelayPhase.Completed)
                return new(false, "该隔离算术任务已经完成，不会重新执行。", persisted.WorkflowTaskId);
            if (persisted.PendingRequestId != Guid.Empty || persisted.NeedsRecoveryConfirmation)
                return new(false, "上轮请求结果未确认，保留请求证据，不自动重发。", persisted.WorkflowTaskId);
            if (persisted.WorkflowTaskId != Guid.Empty && !SameIdentity(persisted))
                return new(false, "算术验收绑定身份发生变化，保留原状态，不接管其他会话。", persisted.WorkflowTaskId);
            cancellationToken.ThrowIfCancellationRequested();
            if (!chatGpt.CanExecute || !codex.CanExecute)
                return new(false, "GPT与Codex必须各自真实连接后才能开始算术验收。");
            ownsState = true;
            if (persisted.WorkflowTaskId == Guid.Empty)
                Set(persisted with { WorkflowTaskId = Guid.NewGuid(), ProjectRoot = identity.ProjectRoot,
                    ChatGptThreadId = identity.ChatGptThreadId, CallerThreadId = identity.CallerThreadId });
            while (State.Evidence.Length < 5)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var guard = await quota(cancellationToken).ConfigureAwait(false);
                if (!CanDispatch(guard))
                    return Pause("额度未验证或剩余不高于20%；停止派发。" + WorkflowText.Safe(guard.Reason, 300));
                var current = State;
                int step = current.Evidence.Length + 1;
                var provider = step % 2 == 1 ? WorkflowProvider.Codex : WorkflowProvider.ChatGPT;
                var connector = provider == WorkflowProvider.Codex ? codex : chatGpt;
                if (!connector.CanExecute) return Pause(provider + "连接断开；停止派发。");
                var requestId = Guid.NewGuid();
                var continuation = provider == WorkflowProvider.Codex ? current.CodexContinuationId : current.ChatGptContinuationId;
                var previousReply = current.Evidence.LastOrDefault()?.Result;
                var prompt = $"AI Control Center自身算术传递验收。TaskId={current.WorkflowTaskId}; RequestId={requestId}; Project={current.ProjectRoot}; Step={step}.\n"
                    + "只回答算术结果的一个阿拉伯数字，不写解释、不使用工具、不读写文件、不创建任务、不执行系统或远程动作。"
                    + (previousReply is null ? "\n本次计算：1+1。" : $"\n上一位AI的真实已确认回复：{previousReply}\n仅使用该回复计算：{previousReply}+1。");
                // Persist the request before invoking transport: cancellation, crashes and unknown
                // delivery are never an excuse to send the same stage a second time.
                Set(current with { Phase = WorkflowArithmeticRelayPhase.Running, PendingRequestId = requestId,
                    PendingProvider = provider, NeedsRecoveryConfirmation = true, Message = $"等待{provider}第{step}步真实回复。" });
                var reply = await connector.ExecuteAsync(new(requestId, provider, prompt,
                    continuation.Length == 0 ? null : continuation) { WorkflowTaskId = current.WorkflowTaskId }, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                Set(State with { LastDiagnosticCode = WorkflowText.Safe(reply.DiagnosticCode, 200),
                    LastCompletedTurnId = SafeIdentity(reply.CompletedTurnId) ? reply.CompletedTurnId : "",
                    ReplySourceMessageId = SafeIdentity(reply.ReplySourceMessageId) ? reply.ReplySourceMessageId : "" });
                string answer = reply.Message?.Trim() ?? "";
                if (!reply.Success)
                    return Pause(provider + "未返回已确认结果：" + WorkflowText.Safe(reply.Message, 600));
                if (answer != (step + 1).ToString(System.Globalization.CultureInfo.InvariantCulture))
                    return Pause(provider + "回复不符合本步严格数值协议；未向下一位AI转发。回复：" + WorkflowText.Safe(answer, 200));
                string returnedContinuation = reply.ContinuationId ?? "";
                if (!SafeIdentity(returnedContinuation) || returnedContinuation.Length == 0
                    || continuation.Length > 0 && returnedContinuation != continuation
                    || provider == WorkflowProvider.ChatGPT && identity.ChatGptThreadId.Length > 0 && returnedContinuation != identity.ChatGptThreadId)
                    return Pause(provider + "返回会话身份缺失或变化；停止传递，不接管其他会话。");
                string turnId = reply.CompletedTurnId;
                if (!SafeIdentity(turnId) || turnId.Length == 0
                    || current.Evidence.Any(item => item.Provider == provider && item.CompletedTurnId == turnId))
                    return Pause(provider + "完成Turn证据缺失或异常；保留当前请求，不继续。");
                var evidence = new WorkflowArithmeticRelayEvidence(step, provider, requestId, answer, returnedContinuation, turnId, DateTimeOffset.UtcNow);
                var completed = step == 5;
                Set(State with { Phase = completed ? WorkflowArithmeticRelayPhase.Completed : WorkflowArithmeticRelayPhase.Running,
                    PendingRequestId = Guid.Empty, PendingProvider = null, NeedsRecoveryConfirmation = false,
                    CodexContinuationId = provider == WorkflowProvider.Codex ? returnedContinuation : current.CodexContinuationId,
                    ChatGptContinuationId = provider == WorkflowProvider.ChatGPT ? returnedContinuation : current.ChatGptContinuationId,
                    Evidence = current.Evidence.Append(evidence).ToArray(),
                    Message = completed ? "小组件收到Codex真实结果6，隔离算术循环完成。" : $"已确认{provider}真实结果{answer}，准备转发。" });
            }
            return new(true, State.Message, State.WorkflowTaskId);
        }
        catch (OperationCanceledException)
        { return ownsState ? Pause("算术验收已取消；未确认请求保留，不自动重发。") : new(false, "算术验收已取消，未发送请求。"); }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            // Failing to acquire the lease must not overwrite the active owner's durable state.
            if (ownsState) return Pause("算术验收暂停：" + WorkflowText.Safe(error.Message, 600));
            return new(false, "算术验收未启动：" + WorkflowText.Safe(error.Message, 600));
        }
        finally { lease?.Dispose(); lock (gate) running = false; RaiseChanged(); }
    }

    public static bool CanDispatch(QuotaGuardSnapshot guard) => guard.CanStart
        && guard.State is QuotaGuardState.Ready or QuotaGuardState.Warning
        && guard.FiveHourRemaining is double remaining && double.IsFinite(remaining) && remaining is > 20 and <= 100;
    bool SameIdentity(WorkflowArithmeticRelayState saved) => string.Equals(saved.ProjectRoot, identity.ProjectRoot, StringComparison.OrdinalIgnoreCase)
        && saved.ChatGptThreadId == identity.ChatGptThreadId && saved.CallerThreadId == identity.CallerThreadId;
    static bool SafeIdentity(string? value) => value is not null && value.Length <= 200 && !WorkflowText.HasSecret(value)
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
    ControlOperationResult Pause(string message)
    {
        var paused = State with { Phase = WorkflowArithmeticRelayPhase.Paused, Message = message };
        try { Set(paused); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            lock (gate) state = paused with { NeedsRecoveryConfirmation = true,
                Message = message + "；暂停记录写入失败：" + WorkflowText.Safe(error.Message, 200) };
            RaiseChanged();
        }
        return new(false, State.Message, State.WorkflowTaskId);
    }
    WorkflowArithmeticRelayState ReadState()
    {
        ValidatePaths();
        if (!File.Exists(statePath))
        {
            if (File.Exists(statePath + ".bak")) throw new InvalidDataException("算术主记录缺失，保留备份，不重放。");
            return new();
        }
        if (new FileInfo(statePath).Length > 64 * 1024) throw new InvalidDataException("算术记录过大。");
        using var json = JsonDocument.Parse(File.ReadAllBytes(statePath));
        var names = json.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Length)
            throw new InvalidDataException("算术记录字段重复。");
        foreach (var required in new[] { "SchemaVersion", "WorkflowTaskId", "Phase", "PendingRequestId", "NeedsRecoveryConfirmation", "Evidence" })
            if (!names.Contains(required, StringComparer.Ordinal)) throw new InvalidDataException("算术记录缺少必需字段。");
        var saved = json.RootElement.Deserialize<WorkflowArithmeticRelayState>(Options)
            ?? throw new InvalidDataException("算术记录为空。");
        if (saved.SchemaVersion != 1 || !Enum.IsDefined(saved.Phase) || saved.Evidence is null || saved.Evidence.Length > 5
            || saved.PendingProvider is { } pending && !Enum.IsDefined(pending)
            || (saved.PendingRequestId == Guid.Empty) != (saved.PendingProvider is null)
            || saved.WorkflowTaskId == Guid.Empty && (saved.Evidence.Length > 0 || saved.PendingRequestId != Guid.Empty)
            || saved.ProjectRoot is null || saved.ChatGptThreadId is null || saved.CallerThreadId is null
            || !SafeIdentity(saved.CodexContinuationId) || !SafeIdentity(saved.ChatGptContinuationId))
            throw new InvalidDataException("算术记录不兼容，禁止重放。");
        for (int index = 0; index < saved.Evidence.Length; index++)
        {
            var item = saved.Evidence[index];
            if (item is null || item.Step != index + 1 || item.Provider != (index % 2 == 0 ? WorkflowProvider.Codex : WorkflowProvider.ChatGPT)
                || item.RequestId == Guid.Empty || item.Result != (index + 2).ToString(System.Globalization.CultureInfo.InvariantCulture)
                || string.IsNullOrEmpty(item.ContinuationId) || !SafeIdentity(item.ContinuationId)
                || string.IsNullOrEmpty(item.CompletedTurnId) || !SafeIdentity(item.CompletedTurnId) || item.ConfirmedAt == default)
                throw new InvalidDataException("算术阶段证据异常，禁止重放。");
        }
        if (saved.Evidence.Select(item => item.RequestId).Distinct().Count() != saved.Evidence.Length
            || saved.Evidence.Select(item => (item.Provider, item.CompletedTurnId)).Distinct().Count() != saved.Evidence.Length
            || saved.PendingRequestId != Guid.Empty && saved.Evidence.Any(item => item.RequestId == saved.PendingRequestId)
            || saved.Phase == WorkflowArithmeticRelayPhase.Completed && (saved.Evidence.Length != 5 || saved.PendingRequestId != Guid.Empty)
            || saved.Evidence.Length == 5 && saved.Phase != WorkflowArithmeticRelayPhase.Completed
            || saved.Evidence.Any(item => item.ContinuationId != (item.Provider == WorkflowProvider.Codex ? saved.CodexContinuationId : saved.ChatGptContinuationId)))
            throw new InvalidDataException("算术请求证据重复或完成记录异常。");
        return saved;
    }
    void ValidatePaths()
    {
        foreach (var path in new[] { directory, statePath, statePath + ".bak", lockPath })
            if (!AutomationStore.IsSafeDPath(path) || File.Exists(path) && (File.GetAttributes(path) & FileAttributes.Directory) != 0)
                throw new InvalidDataException("算术验收路径异常。");
        if (Directory.Exists(statePath) || Directory.Exists(statePath + ".bak") || Directory.Exists(lockPath))
            throw new InvalidDataException("算术验收文件路径被目录占用。");
    }
    void Set(WorkflowArithmeticRelayState value)
    {
        ValidatePaths(); value = value with { UpdatedAt = DateTimeOffset.UtcNow };
        AtomicStore.Write(statePath, value); lock (gate) state = value; RaiseChanged();
    }
    void RaiseChanged() { try { Changed?.Invoke(); } catch { } }
}

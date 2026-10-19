using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CodexQuotaWidget.Core;
using CodexQuotaWidget.Core.Automation;
using CodexQuotaWidget.Core.Observation;

namespace CodexQuotaWidget;

internal sealed class ReliabilityRuntime : IReliabilityUiController
{
    readonly string projectRoot;
    readonly StartupRecoveryStore recovery;
    readonly QuotaMonitor monitor;
    readonly CodexSessionBindingStore bindings;
    readonly CodexStateChangedEventStore notifications;
    readonly CodexAgentAcknowledgementReader acknowledgements;
    CodexSessionBinding binding = new();
    BoundCodexStatusObserver? observer;
    string bindingStatus = "尚未绑定真实 Session。";
    string lastEvent = "";
    string notificationError = "";
    string stateTimeline = "尚无当前绑定的状态历史。";
    string agentAcknowledgement = "没有可匹配的当前状态事件。";
    long eventSequence;
    Guid timelineEventId;
    public event Action? BindingChanged;
    public bool HasCurrentBoundObservation
    {
        get
        {
            var current = bindings.Load();
            return current.Success && current.Binding == binding && binding.IsBound && Observation.BindingId == binding.BindingId;
        }
    }
    public CodexStatusObservation Observation { get; private set; } = new();
    public ReliabilityRuntime(string projectRoot, StartupRecoveryStore recovery, QuotaMonitor monitor)
    {
        this.projectRoot = projectRoot; this.recovery = recovery; this.monitor = monitor;
        bindings = new(Paths.Data); notifications = new(Paths.Data); acknowledgements = new(Paths.Data);
        ApplyBinding(bindings.Load());
    }
    void ApplyBinding(CodexSessionBindingResult result)
    {
        bindingStatus = result.Message;
        if (!result.Success)
        {
            ResetPublishedState();
            observer = null;
            Observation = Unknown(result.Message);
            return;
        }
        if (binding == result.Binding && observer is not null) return;
        var changed = binding != result.Binding;
        binding = result.Binding;
        ResetPublishedState();
        observer = binding.IsBound ? new(binding) : null;
        Observation = Unknown(binding.IsBound ? "等待读取选定 Session 的真实任务证据。" : "未绑定明确 Session；不会自动采用最新会话。");
        if (changed) BindingChanged?.Invoke();
    }
    void ResetPublishedState()
    {
        lastEvent = ""; notificationError = ""; stateTimeline = "尚无当前绑定的状态历史。";
        eventSequence = 0; timelineEventId = Guid.Empty; agentAcknowledgement = "没有可匹配的当前状态事件。";
    }
    CodexStatusObservation Unknown(string reason) => new(Message: reason, ObservedAt: DateTimeOffset.UtcNow,
        SessionId: binding.IsBound ? binding.SessionId : null)
        { BindingId = binding.BindingId, ProjectRoot = binding.ProjectRoot, SessionFilePath = binding.SessionFilePath, ProjectAssociationVerified = false };
    public async Task<CodexStatusObservation?> ReadObservationAsync(CancellationToken cancellationToken)
    {
        var loaded = bindings.Load(); ApplyBinding(loaded);
        var snapshot = binding;
        var selected = observer;
        var observation = !loaded.Success || selected is null ? Unknown(bindingStatus)
            : await CodexBoundObservationGuard.ReadAsync(bindings,snapshot,selected.ReadAsync,cancellationToken);
        // A manual selection or external config change during IO invalidates the old result.
        var current = bindings.Load();
        if (!current.Success) { ApplyBinding(current); return Unknown(current.Message); }
        if (current.Binding != snapshot || binding != snapshot) { ApplyBinding(current); return null; }
        return observation;
    }
    public void Update(CodexStatusObservation observation)
    {
        if (observation.BindingId != binding.BindingId) return;
        Observation = observation;
        try
        {
            var item = CodexStateChangedEventFactory.FromObservation(observation,
                binding.IsBound ? binding.ProjectRoot : projectRoot, binding.BindingId, DateTimeOffset.UtcNow);
            notifications.Publish(item);
            var published = notifications.ReadCurrent();
            lastEvent = published is null ? "" : $"#{published.SequenceNumber} · {published.State} · {published.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {published.EventId}";
            eventSequence = published?.SequenceNumber ?? 0;
            if (published is not null && published.EventId != timelineEventId)
            {
                var history = notifications.ReadTimeline().Where(item => item.BindingId == binding.BindingId
                    && item.SessionId == published.SessionId).TakeLast(6).ToArray();
                stateTimeline = history.Length == 0 ? "尚无当前绑定的状态历史。" : string.Join("\n", history.Select(item =>
                    $"#{item.SequenceNumber} · {item.State} · {item.CreatedAt.ToLocalTime():HH:mm:ss} · {item.EvidenceKind}"));
                timelineEventId = published.EventId;
            }
            agentAcknowledgement = acknowledgements.Read(published).Message;
            notificationError = "";
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException)
        {
            notificationError = "状态通知写入失败：" + error.GetType().Name + "；任务观察结果仍保留，未授权动作。";
            agentAcknowledgement = "当前通知未完成核验；上一事件的回执不能表示本次已接收。";
        }
    }
    public void ReportReadFailure() => Update(Unknown("状态读取失败，保持 Unknown；未授权收尾。"));
    public SessionBindingUiState GetBindingState() => new()
    {
        IsBound = binding.IsBound, ProjectRoot = binding.ProjectRoot, SessionId = binding.SessionId,
        MetadataPath = binding.SessionFilePath, Status = bindingStatus
    };
    public async Task<SessionCatalogUiResult> RefreshSessionsAsync(string projectRoot)
    {
        var catalog = await new CodexSessionCatalog().ReadAsync();
        return new(catalog.Success, catalog.Message + " 清单包含其他项目会话；必须手动选择，关联不等于原生项目证据。",
            catalog.Sessions.Select(entry => new SessionChoiceUi(entry.SessionId, entry.SessionFilePath, entry.DeclaredProject,
                entry.LastModified.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + "（文件写入时间）", "仅元数据；未读取生命周期，来源：" + entry.Source)).ToArray());
    }
    public SessionBindingUiSaveResult SaveBinding(string projectRoot, string sessionId)
    {
        var result = bindings.Save(projectRoot, sessionId);
        if (result.Success) ApplyBinding(result);
        return new(result.Success, result.Message, GetBindingState());
    }
    public SessionBindingUiSaveResult ClearBinding()
    {
        var result = bindings.Clear();
        if (result.Success) ApplyBinding(result);
        return new(result.Success, result.Message, GetBindingState());
    }
    public ReliabilityUiState GetState()
    {
        var state = recovery.GetState();
        var label = Observation.CanonicalState switch
        {
            CodexTaskState.Working => "Working · Codex 运行中", CodexTaskState.Waiting => "Waiting · Codex 等待输入",
            CodexTaskState.Completed => "Completed · Codex 本轮完成", CodexTaskState.Failed => "Failed · Codex 任务失败",
            CodexTaskState.Exhausted => "Exhausted · Codex 额度耗尽", _ => "Unknown · 未知"
        };
        return new()
        {
            CodexState = label, StateSource = Observation.Source, StateDetail = Observation.Message + (notificationError.Length > 0 ? "\n" + notificationError : ""),
            ObservedAt = Observation.ObservedAt == default ? "" : Observation.ObservedAt.ToLocalTime().ToString("HH:mm:ss"),
            RecoveryNeedsAttention = state.NeedsAttention, RecoveryAcknowledged = state.Acknowledged,
            RecoverySummary = state.Summary, RecoveryDetails = string.Join("\n", state.Reasons),
            BindingProject = binding.ProjectRoot, SessionId = Observation.SessionId ?? binding.SessionId,
            TurnId = Observation.TurnId ?? "", LastActivity = Observation.LastActivity?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "",
            LastEvent = lastEvent + (notificationError.Length > 0 ? "\n" + notificationError : ""),
            TrustLevel = Observation.TrustLevel.ToString(), UnknownReason = Observation.UnknownReason,
            EvidenceDescription = $"{(int)Observation.EvidenceLevel} · {Observation.EvidenceLevel} · {Observation.EvidenceKind}",
            EventSequence = eventSequence > 0 ? eventSequence.ToString() : "尚无持久事件序号",
            StateTimeline = stateTimeline, AgentAcknowledgement = agentAcknowledgement,
            ProjectMatchDescription = !binding.IsBound ? "未绑定明确 Session。" : Observation.DeclaredProject.Length == 0
                ? "日志项目声明尚未核实；未授权自动收尾。" : $"日志声明：{Observation.DeclaredProject}\n原生关联核验：{Observation.ProjectAssociationVerified}；全局停止确认：{Observation.TaskStoppedConfirmed}。"
        };
    }
    public string ContinueRecovery() => recovery.Acknowledge()
        ? "已确认恢复上下文；本地任务保留，未自动启动任务、Executor 或电源操作。"
        : "恢复确认未保存；请查看日志，现有任务和记录保持不变。";
    public string OpenLogs()
    {
        var directory = Path.Combine(Paths.Data, "logs");
        try
        {
            if (!AutomationStore.IsSafeDPath(directory)) return "日志目录路径异常，未打开。";
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
            return "已打开本地日志目录。";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { return "日志目录未打开：" + e.GetType().Name; }
    }
    public async Task<string> RunDiagnosticsAsync()
    {
        try
        {
            if (!File.Exists(AutomationRuntime.ExecutorPath) || !AutomationStore.IsSafeDPath(AutomationRuntime.ExecutorPath))
                return "诊断未启动：独立 Executor 不存在或路径异常。";
            var temp = Path.Combine(Paths.Data, "temp");
            if (!AutomationStore.IsSafeDPath(temp)) return "诊断临时目录异常。";
            Directory.CreateDirectory(temp);
            var start = new ProcessStartInfo(AutomationRuntime.ExecutorPath)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "--diagnostics", "--data-dir", Paths.Data, "--project-root", projectRoot }) start.ArgumentList.Add(arg);
            start.Environment["TEMP"] = temp; start.Environment["TMP"] = temp;
            using var process = Process.Start(start) ?? throw new InvalidOperationException("诊断子进程未启动");
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90)); }
            catch (TimeoutException) { if (!process.HasExited) process.Kill(); return "诊断等待超时；未执行关闭或电源动作。"; }
            _ = await stdout; _ = await stderr;
            var path = Path.Combine(Paths.Data, "checks", "automation-diagnostics.json");
            if (!AutomationStore.IsSafeDPath(path) || !AutomationStore.IsSafeDPath(path + ".bak") || !File.Exists(path)
                || new FileInfo(path).Length > 256 * 1024) return "诊断未生成有效报告。";
            var report = JsonSerializer.Deserialize<AutomationDiagnosticsReport>(await File.ReadAllTextAsync(path));
            if (report is null || !report.DryRun || !report.NoPowerAction || !report.NoProcessesClosed
                || report.StartedAt < process.StartTime.ToUniversalTime().AddSeconds(-1)) return "诊断报告无效或不是本次 Dry Run。";
            var now = DateTimeOffset.UtcNow;
            var valid = ExhaustionDetector.IsValidLiveQuota(monitor.Snapshot, monitor.Status, monitor.IsStale, now, out var remaining, out _);
            report = report with { Items = report.Items.Concat(new[]
            {
                new AutomationDiagnosticItem { Id="widget-live-quota", Title="当前官方额度", Outcome=valid ? DiagnosticsOutcome.RealPassed : DiagnosticsOutcome.Unknown,
                    Message=valid ? $"有效新鲜五小时额度，剩余 {remaining:0.#}%；此检查不代表任务停止。" : "未知或过期；禁止作为耗尽证据。" },
                new AutomationDiagnosticItem { Id="widget-task-status", Title="真实 Codex 任务状态", Outcome=Observation.CanonicalState==CodexTaskState.Unknown ? DiagnosticsOutcome.Unknown : Observation.Synthetic ? DiagnosticsOutcome.SimulatedPassed : DiagnosticsOutcome.RealPassed,
                    Message=$"{GetState().CodexState}；{Observation.Message}；停止确认={Observation.TaskStoppedConfirmed}" }
            }).ToArray() };
            AtomicStore.Write(path, report);
            return "Automation Diagnostics · Dry Run\n未关闭程序，未执行电源操作\n" + string.Join("\n", report.Items.Select(item => $"{item.Title} [{item.Outcome}]：{item.Message}"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or JsonException or ArgumentException)
        { return "诊断未完成：" + e.GetType().Name + "；未授权关闭或电源操作。"; }
    }
}

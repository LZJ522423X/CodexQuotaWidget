using System.Text.RegularExpressions;

namespace CodexQuotaWidget.Core.ControlCenter;

/// <summary>Thread-safe, durable local queue with fail-closed quota protection. No model calls or process control.</summary>
public sealed class ControlCenterService
{
    public const int MaximumTasks = 200;
    public const int MaximumTitleLength = 100;
    public const int MaximumNotesLength = 2000;
    readonly object gate = new();
    readonly string statePath;
    readonly Func<DateTimeOffset> clock;
    readonly IWorkflowConnector connector;
    ControlCenterState state = new();
    QuotaSnapshot? quota;
    MonitorStatus monitorStatus = MonitorStatus.Starting;
    QuotaGuardSnapshot? lastPublishedGuard;
    bool quotaStale = true, storageBlocked;
    string? storageError;
    long revision;

    public event Action? Changed;

    public ControlCenterService(string? statePath = null, Func<DateTimeOffset>? clock = null, IWorkflowConnector? connector = null)
    {
        this.statePath = Path.GetFullPath(statePath ?? Path.Combine(Paths.Data, "control-center", "state.json"));
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        this.connector = connector ?? new DisconnectedWorkflowConnector();
        ControlCenterState? loaded;
        try { loaded = ReadPrimaryQueue(this.statePath); }
        catch (Exception e) when (IsStorageException(e))
        {
            storageBlocked = true;
            storageError = "任务主记录无法读取；已保留主记录及备份，暂停任务操作";
            return;
        }
        if (loaded is null)
        {
            if (ExistsOrCannotRead(this.statePath) || ExistsOrCannotRead(this.statePath + ".bak"))
            { storageBlocked = true; storageError = "任务状态无法读取；已保留现有文件，暂停写入"; }
            return;
        }
        if (!ValidStoredState(loaded))
        { storageBlocked = true; storageError = "任务状态格式不兼容；已保留现有文件，暂停写入"; return; }
        state = loaded with { Tasks = loaded.Tasks.ToList() };
        bool interrupted = state.Tasks.Any(t => t.State == WorkItemState.Running);
        if (interrupted || state.ExitRequested)
        {
            var now = this.clock();
            var recovered = state with
            {
                Tasks = state.Tasks.Select(t => t.State == WorkItemState.Running
                    ? t with { State = WorkItemState.Interrupted, UpdatedAt = now, FailureReason = "上次程序退出时任务尚未结束；请检查后重试" }
                    : t).ToList(),
                SafeStopRequested = interrupted || state.SafeStopRequested,
                DispatchPaused = interrupted || state.DispatchPaused,
                // A safe-exit request belongs to the process that has already exited.
                ExitRequested = false
            };
            try { AtomicStore.Write(this.statePath, recovered); state = recovered; }
            catch (Exception e) when (IsStorageException(e))
            {
                // Recovery is reflected safely in memory even if storage is temporarily unavailable.
                state = recovered; storageBlocked = true; storageError = "恢复状态无法保存；已暂停任务操作";
            }
        }
        revision++;
    }

    public ControlCenterSnapshot Snapshot
    {
        get
        {
            lock (gate)
            {
                var guard = EvaluateGuard();
                bool running = state.Tasks.Any(t => t.State == WorkItemState.Running);
                // A snapshot remains safe even when the UI timer is the first observer of stale data.
                bool stop = state.SafeStopRequested || (running && !guard.CanStart);
                return new()
                {
                    Revision = revision,
                    Tasks = Array.AsReadOnly(state.Tasks.OrderByDescending(t => t.Priority).ThenBy(t => t.Sequence).ToArray()),
                    Guard = guard,
                    Policy = state.Policy,
                    RuntimeState = stop ? AiRuntimeState.SafeStopRequested : running ? AiRuntimeState.LocalTracking
                        : connector.ConnectionState == WorkflowConnectionState.NotConnected ? AiRuntimeState.NotConnected : AiRuntimeState.Idle,
                    ConnectionState = connector.ConnectionState,
                    DispatchPaused = state.DispatchPaused,
                    SafeStopRequested = stop,
                    ExitRequested = state.ExitRequested,
                    CanExitSafely = !running,
                    StorageError = storageError
                };
            }
        }
    }

    public void UpdateQuota(QuotaSnapshot? snapshot, MonitorStatus status, bool isStale)
    {
        bool changed;
        lock (gate)
        {
            var previous = EvaluateGuard();
            bool wasUnsafeWithRunning = !previous.CanStart && state.Tasks.Any(t => t.State == WorkItemState.Running);
            quota = snapshot is null ? null : snapshot with { Windows = snapshot.Windows.ToList(), CreditExpirations = snapshot.CreditExpirations.ToList() };
            monitorStatus = status;
            quotaStale = isStale;
            var current = EvaluateGuard();
            // Compare with the last published result rather than a second evaluation at the same
            // time; otherwise a timer would miss the exact moment cached quota becomes too old.
            changed = lastPublishedGuard != current;
            if ((wasUnsafeWithRunning || !current.CanStart) && state.Tasks.Any(t => t.State == WorkItemState.Running) && !state.SafeStopRequested)
            {
                var draft = state with { SafeStopRequested = true };
                try { if (!storageBlocked) AtomicStore.Write(statePath, draft); }
                catch (Exception e) when (IsStorageException(e)) { storageError = "额度保护状态暂时无法保存；已阻止新任务"; }
                // Quota protection may not be suppressed by a disk write failure.
                state = draft;
                changed = true;
            }
            lastPublishedGuard = current;
            if (changed) revision++;
        }
        if (changed) RaiseChanged();
    }

    public ControlOperationResult AddTask(string title, string? notes = null, int priority = 0)
    {
        title = (title ?? "").Trim(); notes = (notes ?? "").Trim();
        var error = TaskInputError(title, notes, priority);
        if (error is not null) return Reject(error);
        return Mutate(s =>
        {
            if (s.Tasks.Count >= MaximumTasks) return (s, Reject("任务列表已达到 200 条，请先移除结束的任务"));
            if (s.NextSequence >= long.MaxValue - 1) return (s, Reject("任务序号达到安全上限，暂停新增任务"));
            var now = clock(); var id = Guid.NewGuid();
            var item = new ControlWorkItem { Id = id, Title = title, Notes = notes, Priority = priority, State = WorkItemState.Queued, CreatedAt = now, UpdatedAt = now, Sequence = s.NextSequence };
            return (s with { Tasks = [.. s.Tasks, item], NextSequence = s.NextSequence + 1 }, new(true, "任务已加入本地队列", id));
        });
    }

    public ControlOperationResult UpdateTask(Guid taskId, string title, string? notes, int priority)
    {
        title = (title ?? "").Trim(); notes = (notes ?? "").Trim();
        var error = TaskInputError(title, notes, priority);
        if (error is not null) return Reject(error, taskId);
        return ChangeTask(taskId, (s, task) =>
        {
            if (task.State is not (WorkItemState.Queued or WorkItemState.Paused))
                return (task, Reject("仅待处理或暂停任务可以编辑", taskId));
            return (task with { Title = title, Notes = notes, Priority = priority, UpdatedAt = clock() },
                new(true, "任务内容和优先级已保存", taskId));
        });
    }

    public ControlOperationResult StartTask(Guid taskId) => ChangeTask(taskId, (s, t) =>
    {
        if (t.State != WorkItemState.Queued) return (t, Reject("仅待处理任务可以开始", taskId));
        var reason = DispatchBlockReason(s);
        if (reason is not null) return (t, Reject(reason, taskId));
        var now = clock();
        return (t with { State = WorkItemState.Running, StartedAt = now, UpdatedAt = now, FinishedAt = null, FailureReason = null }, new(true, "已开始本地任务跟踪；没有发送模型请求", taskId));
    });

    public ControlOperationResult StartNext()
    {
        return Mutate(s =>
        {
            var reason = DispatchBlockReason(s);
            if (reason is not null) return (s, Reject(reason));
            var next = s.Tasks.Where(t => t.State == WorkItemState.Queued).OrderByDescending(t => t.Priority).ThenBy(t => t.Sequence).FirstOrDefault();
            if (next is null) return (s, Reject("队列中没有待处理任务"));
            var now = clock();
            var updated = next with { State = WorkItemState.Running, StartedAt = now, UpdatedAt = now, FailureReason = null, FinishedAt = null };
            return (s with { Tasks = s.Tasks.Select(t => t.Id == next.Id ? updated : t).ToList() }, new(true, "已开始队列中的下一项本地任务", next.Id));
        });
    }

    public ControlOperationResult PauseTask(Guid taskId) => Transition(taskId, [WorkItemState.Running], WorkItemState.Paused, "已暂停本地任务跟踪");
    public ControlOperationResult ResumeTask(Guid taskId) => ChangeTask(taskId, (s, t) =>
    {
        if (t.State != WorkItemState.Paused) return (t, Reject("仅已暂停任务可以恢复", taskId));
        var reason = DispatchBlockReason(s);
        if (reason is not null) return (t, Reject(reason, taskId));
        return (t with { State = WorkItemState.Running, UpdatedAt = clock() }, new(true, "已恢复本地任务跟踪", taskId));
    });
    public ControlOperationResult CompleteTask(Guid taskId) => Transition(taskId, [WorkItemState.Running, WorkItemState.Paused], WorkItemState.Completed, "任务已标记为完成");
    public ControlOperationResult FailTask(Guid taskId, string reason) => Transition(taskId, [WorkItemState.Running, WorkItemState.Paused], WorkItemState.Failed, "任务已标记为失败", RedactFailure(reason));
    public ControlOperationResult CancelTask(Guid taskId) => Transition(taskId, [WorkItemState.Queued, WorkItemState.Running, WorkItemState.Paused, WorkItemState.Interrupted], WorkItemState.Cancelled, "任务已取消");
    public ControlOperationResult RetryTask(Guid taskId) => ChangeTask(taskId, (s, t) =>
    {
        if (t.State is not (WorkItemState.Failed or WorkItemState.Cancelled or WorkItemState.Interrupted)) return (t, Reject("仅失败、取消或中断任务可以重试", taskId));
        return (t with { State = WorkItemState.Queued, StartedAt = null, FinishedAt = null, FailureReason = null, UpdatedAt = clock(), Sequence = s.NextSequence }, new(true, "任务已重新加入队列", taskId));
    }, advanceSequence: true);
    public ControlOperationResult RemoveTask(Guid taskId) => Mutate(s =>
    {
        var item = s.Tasks.FirstOrDefault(t => t.Id == taskId);
        if (item is null) return (s, Reject("未找到该任务", taskId));
        if (item.State is WorkItemState.Running or WorkItemState.Paused) return (s, Reject("请先结束或取消该任务，再移除", taskId));
        return (s with { Tasks = s.Tasks.Where(t => t.Id != taskId).ToList() }, new(true, "任务已从本地列表移除", taskId));
    });

    public ControlOperationResult SetDispatchPaused(bool paused) => Mutate(s => (s with { DispatchPaused = paused }, new(true, paused ? "已暂停队列；不会自动停止其他程序" : "队列手动暂停已解除")));

    public ControlOperationResult UpdatePolicy(QuotaProtectionPolicy policy)
    {
        if (policy is null || !policy.IsValid) return Reject("额度阈值需为 0–100，警告不低于保护阈值；有效期需为 30–900 秒");
        return Mutate(s =>
        {
            bool running = s.Tasks.Any(t => t.State == WorkItemState.Running);
            bool previouslyUnsafe = running && !EvaluateGuard().CanStart;
            var draft = s with { Policy = policy };
            var guard = EvaluateGuard(policy);
            // Relaxing a threshold or freshness allowance must not silently resume a task that
            // already became unsafe under the previous policy between monitor timer ticks.
            if (running && (previouslyUnsafe || !guard.CanStart)) draft = draft with { SafeStopRequested = true };
            return (draft, new(true, "额度保护规则已保存"));
        });
    }

    public ControlOperationResult AcknowledgeSafeStop() => Mutate(s =>
    {
        if (s.ExitRequested) return (s, Reject("安全退出仍在进行，请先取消退出请求"));
        if (!EvaluateGuard().CanStart) return (s, Reject("额度尚未恢复到可开始任务的状态"));
        return (s with { SafeStopRequested = false }, new(true, "已确认额度恢复；任务不会自动恢复"));
    });

    public ControlOperationResult RequestSafeExit() => Mutate(s =>
        (s with { ExitRequested = true, DispatchPaused = true, SafeStopRequested = s.Tasks.Any(t => t.State == WorkItemState.Running) || s.SafeStopRequested },
            new(true, s.Tasks.Any(t => t.State == WorkItemState.Running) ? "已停止新任务派发，请先暂停或结束当前任务，再退出" : "任务状态已保存，可以安全退出")));

    public ControlOperationResult CancelSafeExit() => Mutate(s => (s with { ExitRequested = false }, new(true, "已取消安全退出；队列仍保持暂停，请手动恢复")));

    ControlOperationResult Transition(Guid taskId, WorkItemState[] allowed, WorkItemState target, string message, string? failure = null)
        => ChangeTask(taskId, (s, t) =>
        {
            if (!allowed.Contains(t.State)) return (t, Reject("当前任务状态不允许此操作", taskId));
            var now = clock();
            return (t with { State = target, UpdatedAt = now, FinishedAt = target is WorkItemState.Completed or WorkItemState.Failed or WorkItemState.Cancelled ? now : null, FailureReason = failure }, new(true, message, taskId));
        });

    ControlOperationResult ChangeTask(Guid taskId, Func<ControlCenterState, ControlWorkItem, (ControlWorkItem Item, ControlOperationResult Result)> change, bool advanceSequence = false)
        => Mutate(s =>
        {
            var old = s.Tasks.FirstOrDefault(t => t.Id == taskId);
            if (old is null) return (s, Reject("未找到该任务", taskId));
            var (item, result) = change(s, old);
            if (!result.Success) return (s, result);
            return (s with { Tasks = s.Tasks.Select(t => t.Id == taskId ? item : t).ToList(), NextSequence = advanceSequence ? s.NextSequence + 1 : s.NextSequence }, result);
        });

    ControlOperationResult Mutate(Func<ControlCenterState, (ControlCenterState State, ControlOperationResult Result)> mutation)
    {
        ControlOperationResult result;
        bool changed = false;
        lock (gate)
        {
            if (storageBlocked) return Reject(storageError ?? "状态存储不可用，已暂停任务操作");
            var (draft, operation) = mutation(state);
            if (!operation.Success) return operation;
            try
            {
                AtomicStore.Write(statePath, draft);
                state = draft; storageError = null; revision++; changed = true; result = operation;
                lastPublishedGuard = EvaluateGuard();
            }
            catch (Exception e) when (IsStorageException(e))
            {
                storageError = "任务状态保存失败；原队列未改动，请检查数据目录";
                changed = true; result = Reject(storageError, operation.TaskId);
            }
        }
        if (changed) RaiseChanged();
        return result;
    }

    string? DispatchBlockReason(ControlCenterState s)
    {
        if (s.ExitRequested) return "已请求安全退出，不再开始新任务";
        if (s.DispatchPaused) return "队列已手动暂停";
        if (s.SafeStopRequested) return "额度保护已请求安全停止，请确认额度恢复后再继续";
        if (s.Tasks.Any(t => t.State == WorkItemState.Running)) return "已有一项任务正在本地跟踪";
        var guard = EvaluateGuard();
        return guard.CanStart ? null : guard.Reason;
    }

    QuotaGuardSnapshot EvaluateGuard(QuotaProtectionPolicy? policy = null)
    {
        policy ??= state.Policy;
        double? five = WindowRemaining(300), week = WindowRemaining(10080);
        if (monitorStatus != MonitorStatus.Live)
        {
            var status = monitorStatus switch
            {
                MonitorStatus.Offline => "网络离线",
                MonitorStatus.NeedsLogin => "账户需要登录",
                MonitorStatus.DesktopClosed => "Codex 未运行",
                MonitorStatus.Sleeping => "系统处于休眠恢复状态",
                MonitorStatus.CliMissing => "Codex CLI 不可用",
                MonitorStatus.ProtocolError => "额度协议不可用",
                MonitorStatus.LoggingIn => "正在登录",
                MonitorStatus.Retrying => "额度读取重试中",
                MonitorStatus.Stopped => "额度监测已停止",
                _ => "等待读取实时额度"
            };
            return Unknown(status + "；已阻止开始新任务", five, week);
        }
        if (quota is null) return Unknown("尚未取得实时额度；已阻止开始新任务", five, week);
        if (quotaStale) return Unknown("额度缓存已失效；请刷新后开始新任务", five, week);
        var now = clock(); var age = now - quota.UpdatedAt;
        if (age < TimeSpan.FromSeconds(-30) || age > TimeSpan.FromSeconds(policy.MaximumQuotaAgeSeconds)) return Unknown("额度时间不可靠或已过期，请刷新额度", five, week);
        if (five is null || week is null) return Unknown("缺少有效的 Codex 5 小时或每周额度，已阻止开始新任务", five, week);
        if (quota.Windows.Any(w => IsCoreWindow(w) && w.ResetsAt is { } reset && reset <= now.ToUnixTimeSeconds())) return Unknown("额度重置时间已到，请刷新确认新额度", five, week);
        var minimum = Math.Min(five.Value, week.Value);
        if (minimum <= 0) return new(QuotaGuardState.Exhausted, five, week, false, true, "Codex 额度已耗尽；已停止新任务派发");
        if (policy.Enabled && minimum <= policy.ProtectionPercent) return new(QuotaGuardState.Protected, five, week, false, true, $"剩余额度达到保护阈值 {policy.ProtectionPercent:0.#}%；已停止新任务派发");
        if (policy.Enabled && minimum <= policy.WarningPercent) return new(QuotaGuardState.Warning, five, week, true, true, $"剩余额度低于警告阈值 {policy.WarningPercent:0.#}%");
        return new(QuotaGuardState.Ready, five, week, true, false, "额度可用；当前任务仅作本地跟踪");
    }

    double? WindowRemaining(double duration)
    {
        if (quota is null) return null;
        var windows = quota.Windows.Where(w => string.Equals(w.LimitId, "codex", StringComparison.OrdinalIgnoreCase)
            && w.WindowDurationMins is { } d && double.IsFinite(d) && Math.Abs(d - duration) < .01).ToArray();
        if (windows.Length == 0 || windows.Any(w => w.UsedPercent is not { } p || !double.IsFinite(p) || p is < 0 or > 100)) return null;
        return windows.Min(w => 100 - w.UsedPercent!.Value);
    }

    static bool IsCoreWindow(QuotaWindow w) => string.Equals(w.LimitId, "codex", StringComparison.OrdinalIgnoreCase)
        && w.WindowDurationMins is { } d && double.IsFinite(d) && (Math.Abs(d - 300) < .01 || Math.Abs(d - 10080) < .01);
    static QuotaGuardSnapshot Unknown(string reason, double? five, double? week) => new(QuotaGuardState.Unknown, five, week, false, false, reason);
    static ControlOperationResult Reject(string message, Guid? taskId = null) => new(false, message, taskId);

    static string? TaskInputError(string title, string notes, int priority)
    {
        if (title.Length is 0 or > MaximumTitleLength || title.Any(char.IsControl)) return "任务标题需为 1–100 个字符，且不能包含控制字符";
        if (notes.Length > MaximumNotesLength || notes.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t'))) return "任务说明不能超过 2000 字符或包含异常控制字符";
        if (priority is < 0 or > 100) return "任务优先级必须为 0–100";
        return null;
    }
    static bool IsStorageException(Exception e) => e is IOException or InvalidDataException or UnauthorizedAccessException or System.Security.SecurityException or System.Text.Json.JsonException or NotSupportedException;

    static bool ExistsOrCannotRead(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return false; }
        // File.Exists deliberately hides access errors; treating an unreadable old queue as new
        // would allow a later successful write to overwrite it without loading its tasks first.
        catch (Exception e) when (IsStorageException(e)) { return true; }
    }

    // A fallback containing Running tasks could overwrite a corrupt primary during constructor
    // recovery. Only the bounded primary may authorize a running-to-interrupted state migration.
    static ControlCenterState? ReadPrimaryQueue(string path)
    {
        if (!ExistsOrCannotRead(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 1024 * 1024) throw new InvalidDataException("任务主记录超过安全上限");
        return System.Text.Json.JsonSerializer.Deserialize<ControlCenterState>(stream);
    }

    static bool ValidStoredState(ControlCenterState s)
    {
        if (s.SchemaVersion != 1 || s.Policy is null || !s.Policy.IsValid || s.Tasks is null || s.Tasks.Count > MaximumTasks || s.NextSequence < 0 || s.NextSequence == long.MaxValue) return false;
        if (s.Tasks.Any(t => t is null || t.Id == Guid.Empty || string.IsNullOrWhiteSpace(t.Title) || t.Title.Length > MaximumTitleLength
            || t.Title.Any(char.IsControl) || t.Notes is null || t.Notes.Length > MaximumNotesLength || t.Priority is < 0 or > 100
            || !Enum.IsDefined(t.State) || t.Sequence < 0 || t.Sequence >= s.NextSequence || t.FailureReason?.Length > 240)) return false;
        return s.Tasks.Select(t => t.Id).Distinct().Count() == s.Tasks.Count && s.Tasks.Count(t => t.State == WorkItemState.Running) <= 1;
    }

    static string RedactFailure(string? reason)
    {
        var text = (reason ?? "任务失败，未提供原因").Trim();
        if (string.IsNullOrWhiteSpace(text)) text = "任务失败，未提供原因";
        text = Regex.Replace(text, @"(?i)(?:bearer\s+\S+|sk-[A-Za-z0-9_-]{8,}|(?:api[_ -]?key|access[_ -]?token|refresh[_ -]?token|password|secret)\s*[:=]\s*\S+)", "[敏感信息已隐藏]");
        text = new string(text.Where(c => !char.IsControl(c)).ToArray());
        return text.Length > 240 ? text[..240] : text;
    }

    void RaiseChanged()
    {
        // One faulty subscriber cannot turn an already committed queue operation into a reported failure.
        foreach (var subscriber in Changed?.GetInvocationList() ?? [])
            try { ((Action)subscriber)(); } catch { }
    }
}

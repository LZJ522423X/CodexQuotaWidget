using System.Text.Json;
using CodexQuotaWidget.Core.ControlCenter;

namespace CodexQuotaWidget.Core.Automation;

public sealed record StartupRecoveryState
{
    public bool NeedsAttention { get; init; }
    public bool Acknowledged { get; init; }
    public bool PreviousWidgetExitUnclean { get; init; }
    public int UnfinishedTaskCount { get; init; }
    public bool PendingExecutor { get; init; }
    public bool StorageBlocked { get; init; }
    public bool SessionTracked { get; init; }
    public IReadOnlyList<string> Reasons { get; init; } = Array.Empty<string>();
    public string Summary => StorageBlocked ? "恢复记录无法可靠读取；保留原记录，不自动执行任何操作。"
        : NeedsAttention ? Acknowledged ? "已确认恢复信息；任务与收尾均不会自动续跑。" : "发现需要确认的恢复状态。"
        : "未发现需要确认的启动恢复记录。";
}

/// <summary>
/// A bounded D-drive widget lifecycle marker. Never infers Codex crashes from a widget marker,
/// never resumes a task or pending executor, and never restores a damaged primary from .bak.
/// </summary>
public sealed class StartupRecoveryStore
{
    readonly object gate = new();
    readonly string dataRoot;
    readonly Func<DateTimeOffset> clock;
    Guid sessionId;
    int unfinishedCount;
    bool begun, tracking, previousUnclean, acknowledged, storageBlocked;
    readonly List<string> persistentReasons = [];
    public string SessionPath => Path.Combine(dataRoot, "resume", "widget-session.json");

    public StartupRecoveryStore(string dataRoot, Func<DateTimeOffset>? clock = null)
    {
        this.dataRoot = RecoveryJournal.ValidateRoot(dataRoot);
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public StartupRecoveryState BeginSession(ControlCenterSnapshot snapshot)
    {
        lock (gate)
        {
            if (begun) return GetState();
            begun = true; sessionId = Guid.NewGuid();
            unfinishedCount = snapshot.Tasks.Count(task => IsUnfinished(task.State));
            if (!string.IsNullOrWhiteSpace(snapshot.StorageError))
                Block("本地任务存储异常；恢复视图不能确认任务记录完整性。");
            try
            {
                var previous = ReadPrimary<WidgetSessionMarker>(SessionPath);
                if (previous is not null)
                {
                    ValidateMarker(previous);
                    previousUnclean = !previous.CleanExit || (previous.PreviousUnclean && !previous.Acknowledged);
                    if (previousUnclean) persistentReasons.Add("上次额度小组件未记录正常退出；无法据此判断 Codex 是否异常退出。");
                }
            }
            catch (Exception error) when (IsStorageError(error))
            { Block("额度小组件会话主记录异常；已保留原始证据，不使用备份覆盖。"); }

            InspectQueuePrimary();
            InspectJournalPrimary();
            // Inspect executor records before writing even a harmless session marker.
            var state = GetState();
            if (!storageBlocked)
            {
                try { WriteMarker(new() { SessionId = sessionId, StartedAt = clock(), PreviousUnclean = previousUnclean }); tracking = true; }
                catch (Exception error) when (IsStorageError(error)) { Block("额度小组件会话标记无法保存；正常退出状态暂不可确认。"); }
            }
            return state with { StorageBlocked = storageBlocked, SessionTracked = tracking, NeedsAttention = state.NeedsAttention || storageBlocked,
                Reasons = ReasonsWith(state.Reasons) };
        }
    }

    public StartupRecoveryState GetState()
    {
        lock (gate)
        {
            var reasons = new List<string>(persistentReasons);
            if (unfinishedCount > 0) reasons.Add($"有 {unfinishedCount} 项未完成的本地任务；这不是 Codex 真实执行状态。");
            bool pending = false;
            try
            {
                var receipt = ReadPrimary<ExhaustionReceipt>(Path.Combine(dataRoot, "automation-pending.json"));
                if (receipt is not null && (receipt.SchemaVersion != 1 || receipt.EventId == Guid.Empty || string.IsNullOrWhiteSpace(receipt.CycleKey)))
                    throw new InvalidDataException("收尾凭据格式不兼容");
                var last = ReadPrimary<ExecutorRunResult>(Path.Combine(dataRoot, "automation", "last-run.json"));
                if (last is not null && (last.RequestId == Guid.Empty || last.StartedAt == default || (last.Completed && last.FinishedAt < last.StartedAt)))
                    throw new InvalidDataException("收尾历史格式不兼容");
                if (receipt is { Synthetic: false } && (last is null || last.DryRun || !last.Completed || !last.Success || last.ExhaustionEventId != receipt.EventId))
                    pending = true;
                if (last is { DryRun: false } && (!last.Completed || !last.Success)) pending = true;
                if (pending) reasons.Add("发现未完成或失败的正式收尾记录；不会重新派发 Executor 或执行遗留操作。");
            }
            catch (Exception error) when (IsStorageError(error))
            {
                Block("收尾主记录无法读取；保留记录，不能推断上次收尾已完成。");
                reasons = ReasonsWith(reasons).ToList();
            }
            return new()
            {
                NeedsAttention = previousUnclean || unfinishedCount > 0 || pending || storageBlocked,
                Acknowledged = acknowledged, PreviousWidgetExitUnclean = previousUnclean, UnfinishedTaskCount = unfinishedCount,
                PendingExecutor = pending, StorageBlocked = storageBlocked, SessionTracked = tracking,
                Reasons = reasons.Distinct(StringComparer.Ordinal).ToArray()
            };
        }
    }

    /// <summary>Acknowledge a displayed context only. Does not change the local queue or executor records.</summary>
    public bool Acknowledge()
    {
        lock (gate)
        {
            if (!begun || !tracking || storageBlocked) return false;
            try
            {
                var current = ReadOwnedMarker();
                WriteMarker(current with { Acknowledged = true }); acknowledged = true; return true;
            }
            catch (Exception error) when (IsStorageError(error))
            { Block("恢复确认无法保存；现有任务及执行记录保持不变。"); return false; }
        }
    }

    /// <summary>Call only after this widget has completed its own orderly save/disposal.</summary>
    public bool MarkCleanExit()
    {
        lock (gate)
        {
            if (!tracking || storageBlocked) return false;
            try
            {
                var current = ReadOwnedMarker();
                WriteMarker(current with { CleanExit = true, EndedAt = clock(), Acknowledged = acknowledged });
                tracking = false; return true;
            }
            catch (Exception error) when (IsStorageError(error))
            { Block("正常退出标记保存失败；下次启动需要重新检查恢复状态。"); return false; }
        }
    }

    void InspectQueuePrimary()
    {
        try
        {
            var queue = ReadPrimary<ControlCenterState>(Path.Combine(dataRoot, "control-center", "state.json"));
            if (queue is not null && (queue.SchemaVersion != 1 || queue.Tasks is null || queue.Tasks.Count > 200
                || queue.Tasks.Any(task => task is null || task.Id == Guid.Empty || !Enum.IsDefined(task.State))))
                throw new InvalidDataException("本地任务主记录格式无效");
            if (queue is not null) unfinishedCount = Math.Max(unfinishedCount, queue.Tasks.Count(task => IsUnfinished(task.State)));
            // A missing primary with a surviving backup is evidence, never a request to restore it.
            if (queue is null && File.Exists(Path.Combine(dataRoot, "control-center", "state.json.bak")))
                throw new InvalidDataException("本地任务主记录缺失");
        }
        catch (Exception error) when (IsStorageError(error))
        { Block("本地任务主记录异常；不会以备份任务状态自动续跑。"); }
    }

    void InspectJournalPrimary()
    {
        try
        {
            var journal = ReadPrimary<RecoveryJournalState>(Path.Combine(dataRoot, "resume", "journal.json"));
            if (journal is not null && (journal.SchemaVersion != 1 || journal.Owner != "CodexQuotaWidget.RecoveryJournal"
                || journal.Tasks is null || journal.Tasks.Count > 200 || journal.UpdatedAt == default || !AutomationStore.IsSafeDPath(journal.ProjectRoot)
                || journal.Tasks.Any(task => task is null || task.Id == Guid.Empty || !Enum.IsDefined(task.State))))
                throw new InvalidDataException("恢复日志格式无效");
            // Queue and journal are overlapping views: do not count the same tasks twice.
            if (journal is not null) unfinishedCount = Math.Max(unfinishedCount, journal.Tasks.Count(task => IsUnfinished(task.State)));
        }
        catch (Exception error) when (IsStorageError(error))
        { Block("恢复 journal 主记录异常；保留原始记录供检查。"); }
    }

    WidgetSessionMarker ReadOwnedMarker()
    {
        var current = ReadPrimary<WidgetSessionMarker>(SessionPath) ?? throw new InvalidDataException("会话主记录缺失");
        ValidateMarker(current);
        if (current.SessionId != sessionId || current.CleanExit) throw new InvalidDataException("会话记录不属于当前活动实例");
        return current;
    }
    static void ValidateMarker(WidgetSessionMarker marker)
    {
        if (marker.SchemaVersion != 1 || marker.Owner != "CodexQuotaWidget.WidgetSession" || marker.SessionId == Guid.Empty
            || marker.StartedAt == default || (marker.CleanExit && (marker.EndedAt is null || marker.EndedAt < marker.StartedAt)))
            throw new InvalidDataException("会话主记录格式无效");
    }
    T? ReadPrimary<T>(string path) where T : class
    {
        if (!AutomationStore.IsSafeDPath(path)) throw new InvalidDataException("恢复路径异常");
        if (!File.Exists(path))
        {
            if (File.Exists(path + ".bak")) throw new InvalidDataException("恢复主记录缺失，存在旧备份证据");
            return null;
        }
        if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new InvalidDataException("恢复记录超过上限");
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? throw new InvalidDataException("恢复主记录为空");
    }
    void WriteMarker(WidgetSessionMarker marker)
    {
        if (!AutomationStore.IsSafeDPath(SessionPath) || !AutomationStore.IsSafeDPath(SessionPath + ".bak"))
            throw new InvalidDataException("会话标记路径异常");
        AtomicStore.Write(SessionPath, marker);
    }
    void Block(string reason)
    {
        storageBlocked = true;
        if (!persistentReasons.Contains(reason, StringComparer.Ordinal)) persistentReasons.Add(reason);
    }
    IReadOnlyList<string> ReasonsWith(IEnumerable<string> reasons) => reasons.Concat(persistentReasons).Distinct(StringComparer.Ordinal).ToArray();
    static bool IsUnfinished(WorkItemState state) => state is WorkItemState.Queued or WorkItemState.Running or WorkItemState.Paused or WorkItemState.Interrupted;
    static bool IsStorageError(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or System.Security.SecurityException or JsonException or ArgumentException or NotSupportedException;

    public sealed record WidgetSessionMarker
    {
        public int SchemaVersion { get; init; } = 1;
        public string Owner { get; init; } = "CodexQuotaWidget.WidgetSession";
        public Guid SessionId { get; init; }
        public DateTimeOffset StartedAt { get; init; }
        public bool CleanExit { get; init; }
        public DateTimeOffset? EndedAt { get; init; }
        public bool PreviousUnclean { get; init; }
        public bool Acknowledged { get; init; }
    }
}

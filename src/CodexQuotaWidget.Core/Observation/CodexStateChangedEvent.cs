using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CodexQuotaWidget.Core.Automation;

namespace CodexQuotaWidget.Core.Observation;

/// <summary>
/// Passive session metadata only. The latest state_changed event is authoritative for current state;
/// working/completed files contain the last event of that type and may belong to an old binding.
/// No event grants task, computer interaction, process, service, or power authorization.
/// </summary>
public sealed record CodexStateChangedEvent
{
    [JsonRequired] public int SchemaVersion { get; init; } = 1;
    // Version 1 records remain readable. Version 2 is assigned a positive sequence by the
    // store's commit lease; callers cannot choose ordering by supplying a number or wall clock.
    public long SequenceNumber { get; init; }
    [JsonRequired] public Guid EventId { get; init; }
    [JsonRequired] public Guid BindingId { get; init; }
    [JsonRequired] public DateTimeOffset CreatedAt { get; init; }
    [JsonRequired] public string ProjectRoot { get; init; } = "";
    public string? SessionId { get; init; }
    public string? TurnId { get; init; }
    [JsonRequired] public string Source { get; init; } = StatusEvidence.LocalSessionSource;
    [JsonRequired]
    [JsonConverter(typeof(JsonStringEnumConverter<CodexObservationTrustLevel>))]
    public CodexObservationTrustLevel TrustLevel { get; init; }
    [JsonRequired, JsonConverter(typeof(JsonStringEnumConverter<CodexTaskState>))]
    public CodexTaskState State { get; init; } = CodexTaskState.Unknown;
    public DateTimeOffset? EvidenceAt { get; init; }
    public DateTimeOffset? LastActivity { get; init; }
    public string UnknownReason { get; init; } = "";
    public bool ProjectAssociationVerified { get; init; }
    public bool Synthetic { get; init; }
    [JsonConverter(typeof(JsonStringEnumConverter<CodexEvidenceLevel>))]
    public CodexEvidenceLevel EvidenceLevel { get; init; }
    public string EvidenceKind { get; init; } = "";
    public long? EvidencePosition { get; init; }
    [JsonRequired] public bool PassiveNotification { get; init; } = true;
    [JsonRequired] public bool GrantsActionAuthority { get; init; }
    public string TypeFileSemantics { get; init; } = "Last event of this type; compare EventId/BindingId with codex_state_changed.json before treating it as current.";
}

public static class CodexStateChangedEventFactory
{
    public static CodexStateChangedEvent FromObservation(CodexStatusObservation observation, string projectRoot, Guid bindingId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (!CodexSessionPaths.IsSafeLocalDirectory(projectRoot) || now == default)
            throw new ArgumentException("状态事件要求现有普通本地项目目录和有效时间；事件文件仍只写D盘");
        if (observation.BindingId != bindingId)
            throw new InvalidDataException("状态观察属于旧绑定，不能发布到当前选择");
        var state = observation.CanonicalState;
        var bound = bindingId != Guid.Empty && StatusEvidence.ValidIdentity(observation.SessionId);
        if (bound && !StatusEvidence.SameProject(observation.ProjectRoot, projectRoot))
            throw new InvalidDataException("状态观察的关联项目与当前选择不一致，不能重标记旧证据");
        var reason = state == CodexTaskState.Unknown ? BoundedReason(observation.UnknownReason) : "";
        if (!bound && state != CodexTaskState.Unknown)
        { state = CodexTaskState.Unknown; reason = "未绑定明确会话，不能发布运行或完成状态。"; }
        if (state != CodexTaskState.Unknown && observation.TrustLevel == CodexObservationTrustLevel.None)
        { state = CodexTaskState.Unknown; reason = "所选会话缺少可核实的本地状态来源。"; }
        if (state != CodexTaskState.Unknown && !StatusEvidence.ValidIdentity(observation.TurnId))
        { state = CodexTaskState.Unknown; reason = "所选会话缺少明确任务标识，状态未确认。"; }
        if (state != CodexTaskState.Unknown && (observation.EvidenceAt is not { } evidence || evidence == default || evidence > now))
        { state = CodexTaskState.Unknown; reason = "没有有效的实际任务事件时间，轮询时间不能替代状态证据。"; }
        var evidenceLevel = bound ? observation.EvidenceLevel : CodexEvidenceLevel.None;
        if (state == CodexTaskState.Unknown)
        {
            var maximumUnknownLevel = bound ? observation.ProjectAssociationVerified
                ? CodexEvidenceLevel.ProjectMatched : CodexEvidenceLevel.SessionMatched : CodexEvidenceLevel.None;
            if ((int)evidenceLevel > (int)maximumUnknownLevel) evidenceLevel = maximumUnknownLevel;
        }
        return new()
        {
            SchemaVersion = 2,
            EventId = Guid.NewGuid(), BindingId = bindingId, CreatedAt = now, ProjectRoot = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar),
            SessionId = StatusEvidence.ValidIdentity(observation.SessionId) ? observation.SessionId : null,
            TurnId = StatusEvidence.ValidIdentity(observation.TurnId) ? observation.TurnId : null,
            Source = observation.Source, TrustLevel = bound ? observation.TrustLevel : CodexObservationTrustLevel.None,
            State = state,
            EvidenceAt = observation.EvidenceAt is { } actualEvidence && actualEvidence != default && actualEvidence <= now ? actualEvidence : null,
            LastActivity = observation.LastActivity is { } actualActivity && actualActivity != default && actualActivity <= now ? actualActivity : null,
            UnknownReason = state == CodexTaskState.Unknown ? (reason.Length == 0 ? "当前会话状态未知。" : reason) : "",
            ProjectAssociationVerified = bound && observation.ProjectAssociationVerified, Synthetic = observation.Synthetic,
            EvidenceLevel = evidenceLevel, EvidenceKind = observation.EvidenceKind,
            EvidencePosition = observation.EvidencePosition
        };
    }

    static string BoundedReason(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        // Observer reasons describe availability/correlation only. Never copy task text, command/error payloads or paths.
        if (value.Length > 512 || value.IndexOfAny(['\0', '\r', '\n']) >= 0
            || Regex.IsMatch(value, @"(?:sk-[A-Za-z0-9_-]{16,}|gh[pousr]_[A-Za-z0-9]{20,}|(?i:bearer)\s+[A-Za-z0-9._~+/-]{20,})"))
            return "会话证据不可用或无法安全描述，状态未知。";
        return value;
    }

    // Historical project metadata can refer to a now-removed ordinary directory. Do not call it
    // corrupt solely because a user later removed that project; any surviving ancestor link is rejected.
    internal static bool IsSafeProjectMetadataPath(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 || !Path.IsPathFullyQualified(path)
                || path.StartsWith("\\", StringComparison.Ordinal) || path.Length < 3 || path[1] != ':'
                || path.IndexOf(':', 2) >= 0 || path.IndexOfAny(['\0', '\r', '\n']) >= 0) return false;
            var full = Path.GetFullPath(path); var root = Path.GetPathRoot(full)!;
            if (root.Length != 3 || !char.IsAsciiLetter(root[0]) || !Directory.Exists(root)) return false;
            for (string? current = full; current is not null; current = Path.GetDirectoryName(current))
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
            return true;
        }
        catch (Exception error) when (CodexSessionPaths.IsReadFailure(error)) { return false; }
    }
}

/// <summary>
/// Passive bounded files on D. The primary is the durable sequence commit marker; a timeline
/// stores at most 256 observed state changes and is never proof of task stopping. Never writes
/// Codex home, launches an agent, or owns codex_exhausted.json.
/// </summary>
public sealed class CodexStateChangedEventStore
{
    const long MaximumBytes = 64 * 1024;
    public const int MaximumTimelineEntries = CodexStateTimelineStore.MaximumEntries;
    const string SyntheticFixtureRoot = @"D:\CodexQuotaWidget-GitHub\artifacts\state-event-checks\";
    readonly object gate = new();
    readonly string directory;
    readonly bool allowSynthetic;
    public string LatestPath => Path.Combine(directory, "codex_state_changed.json");
    public string WorkingPath => Path.Combine(directory, "codex_working.json");
    public string CompletedPath => Path.Combine(directory, "codex_completed.json");
    public string TimelinePath => Path.Combine(directory, "codex_state_timeline.json");

    public CodexStateChangedEventStore(string dataRoot, bool allowSynthetic = false)
    {
        if (!AutomationStore.IsSafeDPath(dataRoot)) throw new ArgumentException("状态事件目录必须是 D 盘普通目录", nameof(dataRoot));
        var full = Path.GetFullPath(dataRoot).TrimEnd(Path.DirectorySeparatorChar);
        if (allowSynthetic && !full.StartsWith(SyntheticFixtureRoot, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("合成通知仅允许显式 state-event-checks 隔离 fixture，禁止生产事件目录", nameof(allowSynthetic));
        directory = Path.Combine(full, "events"); this.allowSynthetic = allowSynthetic;
    }

    /// <returns>True for a new logical state. False for an exact cached/restarted observation; poll time is excluded.</returns>
    public bool Publish(CodexStateChangedEvent item)
    {
        Validate(item, persisted: false);
        // The writer always emits schema 2, including an old caller's schema-1 payload. This
        // normalisation precedes dedup so old callers also remain idempotent after migration.
        item = item with { SchemaVersion = 2, SequenceNumber = 0 };
        lock (gate)
        {
            if (!AutomationStore.IsSafeDPath(directory)) throw new InvalidDataException("状态通知目录含异常链接或路径");
            Directory.CreateDirectory(directory);
            // This ephemeral lease serializes read/dedup/replace across store instances and processes.
            var leasePath = Path.Combine(directory, ".codex-state-event-write.lock");
            ValidateWriteLeasePath(leasePath);
            using var lease = new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            ValidateWriteLeasePath(leasePath);
            // Event files are inspected only after acquiring the cross-instance writer lease,
            // not while another publisher may be atomically replacing their directory entries.
            ValidatePaths();
            var previous = ReadPrimary(LatestPath);
            var working = ReadPrimary(WorkingPath);
            var completed = ReadPrimary(CompletedPath);
            if (previous is null && (working is not null || completed is not null))
                throw new InvalidDataException("当前状态主记录缺失但类型通知仍存在，保留证据等待检查");
            var timeline = new CodexStateTimelineStore(TimelinePath, entry => Validate(entry, persisted: true));
            var history = timeline.Read();
            if (history.Count > 0 && (previous is null || history[^1].SequenceNumber > previous.SequenceNumber
                || history[^1].SequenceNumber == previous.SequenceNumber && history[^1].EventId != previous.EventId))
                throw new InvalidDataException("状态历史比主记录更新或身份冲突，禁止倒退序号或覆盖证据");
            if (previous is { SchemaVersion: 2 }) history = timeline.EnsureCommitted(history, previous);
            if (previous is not null && SameLogicalState(previous, item))
            {
                // If a prior type write was interrupted, repair from the committed primary using the
                // same EventId; do not invent a new notification or rewrite current-state evidence.
                if (previous.State == CodexTaskState.Working && working?.EventId != previous.EventId) AtomicStore.Write(WorkingPath, previous);
                if (previous.State == CodexTaskState.Completed && completed?.EventId != previous.EventId) AtomicStore.Write(CompletedPath, previous);
                if (previous.SchemaVersion == 2) timeline.EnsureCommitted(history, previous);
                return false;
            }
            if (history.Any(entry => entry.EventId == item.EventId))
                throw new InvalidDataException("新状态不能复用历史事件身份，未覆盖可靠最新记录");
            RejectOlderEvidence(previous, item);
            // Unknown may deliberately lose its unavailable cursor. Consult bounded historical
            // cursors only as an anti-replay floor, never as current state or stopping evidence.
            var orderingFloor = history.LastOrDefault(entry => SameEvidenceScope(entry, item) && entry.EvidencePosition is not null);
            RejectOlderEvidence(orderingFloor, item);
            var lastKnown = history.LastOrDefault(entry => SameEvidenceScope(entry, item)
                && entry.State != CodexTaskState.Unknown);
            RejectOlderEvidence(lastKnown, item);
            // Reserve the next number only through a durable primary replacement. A crash after
            // this point leaves history/type output repairable by an identical next observation.
            // No separate counter or wall-clock ordering can race the primary commit.
            if (previous?.SequenceNumber == long.MaxValue)
                throw new InvalidDataException("状态序号已到上限，未回绕或覆盖当前记录");
            var nextSequence = (previous?.SequenceNumber ?? 0) + 1;
            item = item with { SchemaVersion = 2, SequenceNumber = nextSequence };
            Validate(item, persisted: true);
            // The primary is the current-state commit marker. A typed file must match its EventId to be current.
            AtomicStore.Write(LatestPath, item);
            if (item.State == CodexTaskState.Working) AtomicStore.Write(WorkingPath, item);
            else if (item.State == CodexTaskState.Completed) AtomicStore.Write(CompletedPath, item);
            timeline.EnsureCommitted(history, item);
            return true;
        }
    }

    public CodexStateChangedEvent? ReadCurrent()
    {
        lock (gate) { ValidatePaths(); return ReadPrimary(LatestPath); }
    }

    /// <summary>
    /// A bounded sampled state history in durable sequence order. Missing intermediate records,
    /// old bindings and terminal-looking samples grant no stop/action authority.
    /// </summary>
    public IReadOnlyList<CodexStateChangedEvent> ReadTimeline()
    {
        lock (gate)
        {
            ValidatePaths();
            // Read history first: a concurrent forward-only commit may then make the primary
            // newer, which is safe. Reading primary first could manufacture a transient mismatch.
            var history = new CodexStateTimelineStore(TimelinePath, entry => Validate(entry, persisted: true)).Read();
            var current = ReadPrimary(LatestPath);
            if (history.Count > 0 && (current is null || history[^1].SequenceNumber > current.SequenceNumber
                || history[^1].SequenceNumber == current.SequenceNumber && history[^1].EventId != current.EventId))
                throw new InvalidDataException("状态历史与可靠最新记录不一致，不能展示为当前证据");
            return history;
        }
    }

    /// <summary>Consumers must use this rule: a historical typed file is current only if it matches the primary.</summary>
    public static bool IsCurrentTypedEvent(CodexStateChangedEvent? typed, CodexStateChangedEvent? current)
        => typed is not null && current is not null && typed.EventId == current.EventId
            && typed.BindingId == current.BindingId && typed.CreatedAt == current.CreatedAt && typed.State == current.State
            && typed.SchemaVersion == current.SchemaVersion && typed.SequenceNumber == current.SequenceNumber;

    void ValidatePaths()
    {
        foreach (var path in new[] { LatestPath, WorkingPath, CompletedPath, TimelinePath })
            if (!AutomationStore.IsSafeDPath(path) || !AutomationStore.IsSafeDPath(path + ".bak"))
                throw new InvalidDataException("状态通知含异常链接或路径，未覆盖证据");
    }

    void ValidateWriteLeasePath(string path)
    {
        // The name is fixed under a separately verified D-drive parent. Inspect the actual
        // entry directly: IsSafeDPath intentionally converts read errors to false, which would
        // misclassify a competing DeleteOnClose/sharing race as a malicious path.
        if (!AutomationStore.IsSafeDPath(directory)
            || !string.Equals(Path.GetDirectoryName(path), directory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("状态通知写锁父目录异常");
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                throw new InvalidDataException("状态通知写锁是链接或目录，未写入");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        // Sharing/delete-pending IO errors and real access-denied errors are propagated with
        // their original categories. Reparse points are always a non-retryable invalid path.
    }

    CodexStateChangedEvent? ReadPrimary(string path)
    {
        if (!File.Exists(path))
        {
            if (File.Exists(path + ".bak")) throw new InvalidDataException("状态通知主记录缺失但备份仍存在，禁止回退重发");
            return null;
        }
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (input.Length is <1 or >MaximumBytes) throw new InvalidDataException("状态通知主记录大小异常");
        var bytes = new byte[(int)input.Length]; input.ReadExactly(bytes);
        if (input.Length != bytes.Length) throw new InvalidDataException("读取期间状态通知变化，未覆盖证据");
        EnsureVersionedFields(bytes);
        var item = JsonSerializer.Deserialize<CodexStateChangedEvent>(bytes,
            new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })
            ?? throw new InvalidDataException("状态通知主记录为空");
        Validate(item, persisted: true);
        if (path == WorkingPath && item.State != CodexTaskState.Working || path == CompletedPath && item.State != CodexTaskState.Completed)
            throw new InvalidDataException("类型通知与文件职责不一致，未覆盖旧证据");
        return item;
    }

    internal static void EnsureVersionedFields(ReadOnlySpan<byte> bytes)
    {
        using var document = JsonDocument.Parse(bytes.ToArray());
        var element = document.RootElement;
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException("状态通知必须是对象");
        RejectDuplicateProperties(element);
        if (element.TryGetProperty(nameof(CodexStateChangedEvent.SchemaVersion), out var version) && version.TryGetInt32(out var number) && number == 2)
            foreach (var field in new[] { nameof(CodexStateChangedEvent.SequenceNumber), nameof(CodexStateChangedEvent.EvidenceLevel),
                nameof(CodexStateChangedEvent.EvidenceKind), nameof(CodexStateChangedEvent.EvidencePosition) })
                if (!element.TryGetProperty(field, out _)) throw new InvalidDataException("新版状态通知缺少可验证字段：" + field);
    }

    internal static void RejectDuplicateProperties(JsonElement element)
    {
        var properties = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!properties.Add(property.Name)) throw new InvalidDataException("状态记录含重复 JSON 属性，未按最后值隐式采纳");
    }

    void Validate(CodexStateChangedEvent item, bool persisted)
    {
        if (item is null || item.SchemaVersion is not (1 or 2) || item.EventId == Guid.Empty || item.CreatedAt == default
            || !CodexStateChangedEventFactory.IsSafeProjectMetadataPath(item.ProjectRoot) || !Enum.IsDefined(item.State) || !Enum.IsDefined(item.TrustLevel)
            || string.IsNullOrWhiteSpace(item.Source) || item.Source.Length > 128 || !Regex.IsMatch(item.Source, "^[A-Za-z0-9._:-]+$")
            || item.UnknownReason is null || item.UnknownReason.Length > 512 || item.UnknownReason.IndexOfAny(['\0', '\r', '\n']) >= 0
            || item.TypeFileSemantics != new CodexStateChangedEvent().TypeFileSemantics || !item.PassiveNotification || item.GrantsActionAuthority
            || item.Synthetic && !allowSynthetic || item.SessionId is not null && !StatusEvidence.ValidIdentity(item.SessionId)
            || item.TurnId is not null && !StatusEvidence.ValidIdentity(item.TurnId)
            || item.EvidenceAt is { } evidence && (evidence == default || evidence > item.CreatedAt)
            || item.LastActivity is { } last && (last == default || last > item.CreatedAt)
            || !Enum.IsDefined(item.EvidenceLevel) || item.EvidenceKind is null || !StatusEvidence.IsValidEvidenceKind(item.EvidenceKind)
            || item.EvidencePosition is < 0 || item.SequenceNumber < 0
            || item.SchemaVersion == 1 && item.SequenceNumber != 0
            || item.SchemaVersion == 2 && persisted && item.SequenceNumber <= 0)
            throw new InvalidDataException("状态通知结构异常或试图授权操作，未发布");
        if (item.State != CodexTaskState.Unknown && (item.BindingId == Guid.Empty || item.SessionId is null || item.TurnId is null || item.EvidenceAt is null))
            throw new InvalidDataException("明确状态必须绑定会话、任务和真实事件时间");
        if (item.State == CodexTaskState.Unknown && string.IsNullOrWhiteSpace(item.UnknownReason))
            throw new InvalidDataException("未知状态缺少原因");
    }

    static void RejectOlderEvidence(CodexStateChangedEvent? previous, CodexStateChangedEvent incoming)
    {
        if (previous is null || !SameEvidenceScope(previous, incoming))
            return;
        if (previous.EvidencePosition is { } oldPosition && incoming.EvidencePosition is { } newPosition)
        {
            if (newPosition < oldPosition) throw new InvalidDataException("同会话日志证据位置倒退，未覆盖当前通知");
            if (newPosition == oldPosition && incoming.State != CodexTaskState.Unknown
                && previous.EvidenceAt is not null && incoming.EvidenceAt != previous.EvidenceAt)
                throw new InvalidDataException("同一日志行的证据时间发生变化，轮询不能制造新鲜状态");
            if (newPosition == oldPosition && incoming.State != CodexTaskState.Unknown
                && (previous.TurnId != incoming.TurnId || previous.EvidenceKind != incoming.EvidenceKind
                    || previous.State != CodexTaskState.Unknown && previous.State != incoming.State))
                throw new InvalidDataException("同一日志位置出现矛盾的明确状态、任务或事件类型，未覆盖证据");
            // Log positions establish native order even if the system clock moved backward.
            return;
        }
        if (incoming.EvidenceAt is { } next && previous.EvidenceAt is { } last && next < last)
            throw new InvalidDataException("兼容状态证据时间倒退且没有日志位置，未覆盖当前通知");
        if (incoming.EvidenceAt is { } equalNext && previous.EvidenceAt == equalNext
            && incoming.State != CodexTaskState.Unknown && previous.State != CodexTaskState.Unknown
            && (previous.State != incoming.State || previous.TurnId != incoming.TurnId
                || previous.EvidenceKind != incoming.EvidenceKind
                    && !(previous.SchemaVersion == 1 && previous.EvidenceKind.Length == 0 && incoming.SchemaVersion == 2)))
            throw new InvalidDataException("相同证据时间缺少日志顺序且状态矛盾，未覆盖当前通知");
    }

    static bool SameEvidenceScope(CodexStateChangedEvent first, CodexStateChangedEvent second)
        => first.BindingId == second.BindingId && first.SessionId == second.SessionId && first.Source == second.Source;

    static bool SameLogicalState(CodexStateChangedEvent first, CodexStateChangedEvent second)
    {
        var left = Fingerprint(first); var right = Fingerprint(second);
        return CryptographicOperations.FixedTimeEquals(left, right);
    }
    static byte[] Fingerprint(CodexStateChangedEvent item) => SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
    {
        item.SchemaVersion, item.BindingId, item.ProjectRoot, item.SessionId, item.TurnId, item.State,
        item.EvidenceAt, item.LastActivity, item.Source, item.TrustLevel, item.UnknownReason,
        item.ProjectAssociationVerified, item.Synthetic, item.EvidenceLevel, item.EvidenceKind, item.EvidencePosition
    }));
}

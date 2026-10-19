using System.Text;
using System.Text.Json;
using System.Security.Cryptography;

namespace CodexQuotaWidget.Core.Observation;

/// <summary>
/// Reads bounded local Codex rollout metadata/lifecycle events. Does not connect to or resume
/// Desktop threads, run the CLI, write to Codex home, or retain conversation/tool contents.
/// Process presence/absence and the widget's private quota app-server are never turn evidence.
/// </summary>
public sealed class CodexStatusObserver
{
    public const int MaximumSessionFiles = 64;
    public const int MetadataBytes = 64 * 1024;
    public const int MaximumReadBytes = 512 * 1024;
    readonly string projectRoot;
    readonly string codexHome;
    readonly Func<DateTimeOffset> utcNow;
    readonly bool synthetic;
    readonly SemaphoreSlim readerGate = new(1, 1);
    readonly Dictionary<string, SessionTrace> traces = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> unresolvedMissing = new(StringComparer.OrdinalIgnoreCase);
    bool lostCoverage;

    public CodexStatusObserver(string projectRoot, string? codexHome = null, Func<DateTimeOffset>? utcNow = null)
    {
        this.projectRoot = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var nativeHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        this.codexHome = Path.GetFullPath(codexHome ?? nativeHome);
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        synthetic = utcNow is not null || !StatusEvidence.SameProject(this.codexHome, nativeHome);
    }

    public async Task<CodexStatusObservation> ReadAsync(CancellationToken cancellationToken = default)
    {
        await readerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await Task.Run(() => Read(cancellationToken), cancellationToken).ConfigureAwait(false); }
        finally { readerGate.Release(); }
    }

    internal async Task<CodexStatusObservation> ReadSelectedAsync(CodexSessionBinding binding, CancellationToken cancellationToken = default)
    {
        await readerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                var now = utcNow();
                try
                {
                    var metadata = CodexSessionPaths.ReadMetadata(binding.SessionFilePath, codexHome);
                    if (metadata.SessionId != binding.SessionId) throw new InvalidDataException("选定会话id变化。");
                    var trace = ReadTrace(binding.SessionFilePath, now, cancellationToken, 8 * 1024 * 1024, true);
                    now = utcNow();
                    if (trace is null || trace.Invalid || trace.Partial || !trace.IsFresh(now) || trace.State == CodexObservedState.Unknown)
                        return SelectedUnknown(binding, now, metadata.DeclaredProject,
                            trace?.Partial == true ? "选定会话的JSONL尚未完成；等待下一次只读检查。"
                            : trace is { Invalid: true } ? "选定会话的typed生命周期不完整、损坏或顺序不一致。"
                            : "选定会话最近8MB内没有新鲜且完整的当前任务生命周期；状态未知。", metadataMatched: true);
                    var verified = trace.ProjectMatches;
                    return Observation(trace, now, 1, false, trace.State switch
                    {
                        CodexObservedState.Running => "已从明确选择的原生会话观察到任务正在执行。",
                        CodexObservedState.WaitingForInput => "选定会话明确等待用户输入或批准。",
                        CodexObservedState.Completed => "选定会话的当前任务已明确完成。",
                        CodexObservedState.QuotaExhausted => trace.ExplicitlyStopped ? "选定会话额度耗尽，且当前任务已终止。" : "选定会话报告额度耗尽，但任务终止尚未确认。",
                        _ => trace.ExplicitlyStopped ? "选定会话任务已结束且无法继续。" : "选定会话报告执行错误，任务终止尚未确认。"
                    }) with
                    {
                        BindingId = binding.BindingId, SessionFilePath = binding.SessionFilePath, DeclaredProject = trace.DeclaredProject,
                        ProjectAssociationVerified = verified, ActualTaskStopped = trace.ExplicitlyStopped && trace.ActiveTurns.Count == 0,
                        TrustLevel = verified ? CodexObservationTrustLevel.ProjectVerified : CodexObservationTrustLevel.ObservedSession,
                        EvidenceLevel = verified ? EvidenceLevel(trace, now) : CodexEvidenceLevel.SessionMatched,
                        HasUnresolvedOtherTurnActivity = trace.OtherTurnActivity,
                        TaskStoppedConfirmed = false, CoverageComplete = false
                    };
                }
                catch (Exception error) when (CodexSessionPaths.IsReadFailure(error))
                { return SelectedUnknown(binding, now, "", "选定会话文件不可访问或来源metadata变化；未改用其他会话。"); }
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { readerGate.Release(); }
    }

    CodexStatusObservation SelectedUnknown(CodexSessionBinding binding, DateTimeOffset now, string declared, string message, bool metadataMatched = false)
        => Unknown(now, message, false) with
        {
            BindingId = binding.BindingId, SessionId = binding.SessionId, SessionFilePath = binding.SessionFilePath,
            DeclaredProject = declared, ProjectAssociationVerified = false, ActualTaskStopped = false,
            TrustLevel = CodexObservationTrustLevel.None,
            EvidenceLevel = metadataMatched ? CodexEvidenceLevel.SessionMatched
                : CodexSessionPaths.IsSessionFile(binding.SessionFilePath, codexHome) ? CodexEvidenceLevel.LogPresent : CodexEvidenceLevel.None
        };

    CodexStatusObservation Read(CancellationToken cancellationToken)
    {
        var now = utcNow();
        var scanComplete = true;
        var candidates = new List<string>();
        try
        {
            var sessions = Path.Combine(codexHome, "sessions");
            if (!SafeDirectory(codexHome) || !SafeDirectory(sessions)) return Unknown(now, "Codex会话目录不存在、不可访问或不是普通目录。", false);
            // Calendar paths are computed directly; no recursive/full-disk/history scan.
            var dates = Enumerable.Range(0, 3).SelectMany(days => new[] { now.UtcDateTime.Date.AddDays(-days), now.LocalDateTime.Date.AddDays(-days) }).Distinct();
            foreach (var date in dates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var year = Path.Combine(sessions, date.ToString("yyyy"));
                var month = Path.Combine(year, date.ToString("MM"));
                var day = Path.Combine(month, date.ToString("dd"));
                if (!Directory.Exists(day)) continue;
                if (!SafeDirectory(year) || !SafeDirectory(month) || !SafeDirectory(day)) { scanComplete = false; continue; }
                foreach (var file in Directory.EnumerateFiles(day, "rollout-*.jsonl", SearchOption.TopDirectoryOnly))
                {
                    if (candidates.Contains(file, StringComparer.OrdinalIgnoreCase)) continue;
                    if (candidates.Count >= MaximumSessionFiles) { scanComplete = false; break; }
                    candidates.Add(file);
                }
                if (candidates.Count >= MaximumSessionFiles && !scanComplete) break;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return Unknown(now, "会话目录读取失败，不能确认真实任务状态。", false); }

        foreach (var obsolete in traces.Keys.Except(candidates, StringComparer.OrdinalIgnoreCase).ToArray())
        {
            var previous = traces[obsolete];
            // Archiving/deleting a log, or its falling outside the date window, is not evidence
            // that its active turn stopped. Keep only bounded identifiers, never log bodies.
            if (!previous.ExplicitlyStopped || previous.Invalid || previous.Partial || previous.ActiveTurns.Count != 0)
                MarkUnresolved(obsolete);
            traces.Remove(obsolete);
        }
        var current = new List<SessionTrace>();
        foreach (var path in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var trace = ReadTrace(path, now, cancellationToken);
                if (trace is null) { scanComplete = false; MarkUnresolved(path); }
                else
                {
                    current.Add(trace);
                    if (trace.ExplicitlyStopped && !trace.Invalid && !trace.Partial && trace.ActiveTurns.Count == 0) unresolvedMissing.Remove(path);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or DecoderFallbackException or System.Security.SecurityException)
            {
                scanComplete = false;
                MarkUnresolved(path);
                traces.Remove(path);
            }
        }

        now = utcNow();
        scanComplete &= !lostCoverage && unresolvedMissing.Count == 0;
        var scoped = current.Where(trace => trace.ProjectMatches).ToArray();
        if (scoped.Length == 0) return Unknown(now, "没有新鲜且明确绑定当前项目的Codex会话；进程存活不等于AI正在执行。", scanComplete);
        // Other projects and descendant agents can still be executing inside the same Desktop.
        // Never authorize closing that Desktop while any discovered lifecycle remains unresolved.
        var noUnresolvedSessions = current.All(trace => trace.ExplicitlyStopped && !trace.Invalid && !trace.Partial && trace.ActiveTurns.Count == 0);
        var coverage = scanComplete && noUnresolvedSessions;
        var running = scoped.Where(trace => trace.IsFresh(now) && trace.State == CodexObservedState.Running && !trace.Invalid && !trace.Partial)
            .OrderByDescending(trace => trace.EvidenceAt).FirstOrDefault();
        var waiting = scoped.Where(trace => trace.IsFresh(now) && trace.State == CodexObservedState.WaitingForInput && !trace.Invalid && !trace.Partial)
            .OrderByDescending(trace => trace.EvidenceAt).FirstOrDefault();
        if (running is not null) return Observation(running, now, scoped.Length, false, "已观察到绑定当前项目的Codex任务执行事件。");
        if (waiting is not null) return Observation(waiting, now, scoped.Length, false, "Codex明确请求输入或批准；任务尚未停止。");
        var uncertain = scoped.Any(trace => !trace.IsFresh(now) && !trace.ExplicitlyStopped || trace.Invalid || trace.Partial || trace.State == CodexObservedState.Unknown);
        var latest = scoped.OrderByDescending(trace => trace.EvidenceAt).First();
        if (uncertain || !latest.IsFresh(now))
            return Unknown(now, "项目会话证据过期、未完整读取或存在未确认任务；状态保持未知。", false, scoped.Length);
        var message = latest.State switch
        {
            CodexObservedState.Completed => coverage ? "Codex已明确完成当前任务。" : "当前任务已完成，但其他会话或读取范围尚未确认，禁止自动收尾。",
            CodexObservedState.QuotaExhausted => latest.ExplicitlyStopped ? "Codex明确报告额度耗尽并结束当前任务。" : "Codex报告额度耗尽，尚未收到任务终止确认。",
            CodexObservedState.UnableToContinue => latest.ExplicitlyStopped ? "Codex明确结束任务且无法继续。" : "Codex报告错误，尚未确认任务终止。",
            _ => "无法确认真实Codex状态。"
        };
        return Observation(latest, now, scoped.Length, coverage, message);
    }

    CodexStatusObservation Observation(SessionTrace trace, DateTimeOffset now, int count, bool coverage, string message)
        => new(trace.State, now, StatusEvidence.LocalSessionSource, trace.SessionId, trace.TurnId,
            coverage && trace.ExplicitlyStopped && trace.IsFresh(now), message, synthetic)
        {
            EvidenceAt = trace.EvidenceAt, ProjectRoot = projectRoot, ScopedSessionCount = count, CoverageComplete = coverage,
            DeclaredProject = trace.DeclaredProject, ActualTaskStopped = trace.ExplicitlyStopped && trace.IsFresh(now) && !trace.Invalid && !trace.Partial,
            TrustLevel = CodexObservationTrustLevel.ProjectVerified,
            EvidenceLevel = EvidenceLevel(trace, now), EvidenceKind = trace.EvidenceKind, EvidencePosition = trace.EvidencePosition
        };

    static CodexEvidenceLevel EvidenceLevel(SessionTrace trace, DateTimeOffset now)
        => !trace.ProjectMatches ? CodexEvidenceLevel.SessionMatched
            : !trace.Invalid && !trace.Partial && trace.IsFresh(now) && StatusEvidence.ValidIdentity(trace.TurnId)
                && trace.EvidencePosition is > 0 && trace.EvidenceKind.Length > 0 && StatusEvidence.IsValidEvidenceKind(trace.EvidenceKind)
                && trace.State != CodexObservedState.Unknown
                && (trace.State != CodexObservedState.UnableToContinue || trace.ExplicitlyStopped)
                    ? CodexEvidenceLevel.LifecycleConfirmed : CodexEvidenceLevel.ProjectMatched;

    CodexStatusObservation Unknown(DateTimeOffset now, string message, bool coverage, int count = 0)
        => new(CodexObservedState.Unknown, now, StatusEvidence.LocalSessionSource, null, null, false, message, synthetic)
        { ProjectRoot = projectRoot, ScopedSessionCount = count, CoverageComplete = coverage };

    void MarkUnresolved(string path)
    {
        if (unresolvedMissing.Contains(path)) return;
        if (unresolvedMissing.Count < MaximumSessionFiles) unresolvedMissing.Add(path);
        else lostCoverage = true;
    }

    SessionTrace? ReadTrace(string path, DateTimeOffset now, CancellationToken cancellationToken, int maximumReadBytes = MaximumReadBytes, bool preferLatestStart = false)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
        var length = stream.Length;
        var snapshotNow = utcNow(); // Timestamp after capturing the readable extent, never poll-time freshness fabrication.
        var head = new byte[(int)Math.Min(length, MetadataBytes)];
        stream.ReadExactly(head);
        var newline = Array.IndexOf(head, (byte)'\n');
        if (newline < 0) return null; // Includes an unfinished/oversized metadata record.
        using var metadataDocument = JsonDocument.Parse(new ReadOnlyMemory<byte>(head, 0, newline), new() { MaxDepth = 64 });
        var record = metadataDocument.RootElement;
        if (Text(record, "type") != "session_meta") return null;
        var meta = Element(record, "payload");
        var sessionId = Text(meta, "id");
        if (!StatusEvidence.ValidIdentity(sessionId)) return null;
        var modified = File.GetLastWriteTimeUtc(path);
        var fingerprint = Convert.ToHexString(SHA256.HashData(head.AsSpan(0, newline)));
        if (!traces.TryGetValue(path, out var trace) || trace.SessionId != sessionId || trace.MetadataFingerprint != fingerprint
            || trace.Invalid || trace.Offset > length || length == trace.Length && modified != trace.Modified)
        {
            trace = new SessionTrace(sessionId!, BoundToProject(meta), fingerprint, CodexSessionPaths.SafeDeclaredPath(Text(meta, "cwd")));
            traces[path] = trace;
        }
        var start = trace.Offset;
        var initialRead = trace.Offset == 0;
        trace.SelectedDisplayOnly = preferLatestStart;
        if (length - start > maximumReadBytes)
        {
            trace = new SessionTrace(sessionId!, BoundToProject(meta), fingerprint, CodexSessionPaths.SafeDeclaredPath(Text(meta, "cwd")));
            traces[path] = trace;
            trace.SelectedDisplayOnly = preferLatestStart;
            start = length - maximumReadBytes;
            initialRead = true;
        }
        stream.Seek(start, SeekOrigin.Begin);
        var buffer = new byte[(int)Math.Min(length - start, maximumReadBytes)];
        stream.ReadExactly(buffer);
        var index = 0;
        if (start > 0 && trace.Offset == 0)
        {
            // Ignore an arbitrary byte prefix, including split UTF-8 characters.
            var boundary = Array.IndexOf(buffer, (byte)'\n');
            if (boundary < 0) { trace.Invalid = true; return trace; }
            index = boundary + 1;
        }
        if (preferLatestStart && initialRead)
        {
            // Only the explicitly selected file gets the larger budget. Historical orphan
            // terminal events before the latest real start do not determine its display state;
            // the unchanged global observer remains responsible for dangerous-action coverage.
            var lastStart = FindLatestStart(buffer, index, snapshotNow, cancellationToken);
            if (lastStart >= 0) index = lastStart;
        }
        var consumed = index;
        while (index < buffer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var end = Array.IndexOf(buffer, (byte)'\n', index);
            if (end < 0) break;
            if (end > index)
            {
                try
                {
                    using var document = JsonDocument.Parse(new ReadOnlyMemory<byte>(buffer, index, end - index), new() { MaxDepth = 64 });
                    Apply(trace, document.RootElement, snapshotNow, start + end + 1);
                }
                catch (JsonException) { trace.Invalid = true; trace.ExplicitlyStopped = false; }
            }
            consumed = end + 1;
            index = consumed;
        }
        trace.Offset = start + consumed;
        trace.Partial = consumed != buffer.Length || stream.Length != length;
        trace.Length = length;
        trace.Modified = modified;
        return trace;
    }

    void Apply(SessionTrace trace, JsonElement record, DateTimeOffset now, long position)
    {
        var recordType = Text(record, "type");
        if (recordType == "response_item")
        {
            // Inspect only the role/type, never the message/tool contents. Submitted input may
            // arrive before the next task_started, so old completion must not authorize power.
            var response = Element(record, "payload");
            if (trace.ExplicitlyStopped && (Text(response, "role") == "user"
                    || Text(response, "type") is "function_call" or "function_call_output"))
            { trace.ExplicitlyStopped = false; trace.State = CodexObservedState.Unknown; }
            return;
        }
        if (recordType is not ("turn_context" or "event_msg")) return;
        var eventPayload = Element(record, "payload");
        // Desktop may append a previous turn's subagent completion after the next parent
        // turn starts. Isolate only its display; it never refreshes or stops the current turn.
        if (trace.SelectedDisplayOnly && recordType == "event_msg" && Text(eventPayload, "type") == "item_completed"
            && Text(eventPayload, "turn_id") is { } foreignTurn && foreignTurn != trace.TurnId && trace.TurnId is not null
            && Text(eventPayload, "thread_id") == trace.SessionId && Text(Element(eventPayload, "item"), "type") == "SubAgentActivity")
        {
            trace.OtherTurnActivity = true;
            return;
        }
        if (!DateTimeOffset.TryParse(Text(record, "timestamp"), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var timestamp) || timestamp > now
            || trace.LastRecordAt is { } previous && timestamp < previous)
        { trace.Invalid = true; trace.ExplicitlyStopped = false; return; }
        trace.LastRecordAt = timestamp;
        var payload = Element(record, "payload");
        var turn = Text(payload, "turn_id");
        if (recordType == "turn_context")
        {
            if (!StatusEvidence.ValidIdentity(turn)) { trace.Invalid = true; return; }
            if (trace.TurnScopes.Count >= 32 && !trace.TurnScopes.ContainsKey(turn!))
                foreach (var old in trace.TurnScopes.Keys.Where(key => !trace.ActiveTurns.Contains(key)).ToArray()) trace.TurnScopes.Remove(old);
            if (trace.TurnScopes.Count >= 32 && !trace.TurnScopes.ContainsKey(turn!)) { trace.Invalid = true; return; }
            trace.TurnScopes[turn!] = BoundToProject(payload);
            var declared = CodexSessionPaths.SafeDeclaredPath(Text(payload, "cwd"));
            if (turn == trace.TurnId)
            {
                trace.ProjectMatches = trace.TurnScopes[turn!];
                trace.DeclaredProject = declared;
            }
            return;
        }
        var type = Text(payload, "type");
        if (type is "task_started" or "turn_started")
        {
            if (!StatusEvidence.ValidIdentity(turn)) { trace.Invalid = true; trace.ExplicitlyStopped = false; return; }
            if (trace.ActiveTurns.Count >= 32 && !trace.ActiveTurns.Contains(turn!)) { trace.Invalid = true; trace.ExplicitlyStopped = false; return; }
            trace.ActiveTurns.Add(turn!);
            trace.TurnId = turn;
            trace.ProjectMatches = trace.TurnScopes.GetValueOrDefault(turn!, trace.MetadataMatches);
            trace.ExplicitlyStopped = false;
            trace.State = CodexObservedState.Running;
            SetEvidence(trace, timestamp, type!, position);
            return;
        }
        if (type is "task_complete" or "turn_complete" or "turn_aborted")
        {
            // A terminal marker cannot be joined to a previous/different/unobserved turn.
            if (!StatusEvidence.ValidIdentity(turn) || turn != trace.TurnId || !trace.ActiveTurns.Contains(turn!))
            { trace.Invalid = true; trace.ExplicitlyStopped = false; trace.State = CodexObservedState.Unknown; return; }
            trace.ActiveTurns.Remove(turn!);
            trace.ExplicitlyStopped = true;
            SetEvidence(trace, timestamp, type!, position);
            var error = Element(payload, "error");
            var code = ErrorCode(error);
            trace.State = IsUsageLimit(code) ? CodexObservedState.QuotaExhausted
                : error.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) || type == "turn_aborted"
                    ? CodexObservedState.UnableToContinue : CodexObservedState.Completed;
            return;
        }
        if (trace.TurnId is null || !trace.ActiveTurns.Contains(trace.TurnId))
        {
            // New explicit activity after completion invalidates the earlier terminal evidence.
            if (type is not ("token_count" or "thread_settings_applied" or "deprecation_notice" or "warning"))
            { trace.ExplicitlyStopped = false; trace.State = CodexObservedState.Unknown; SetEvidence(trace, timestamp, "unrecognized_activity", position); }
            return;
        }
        if (turn is not null && turn != trace.TurnId)
        { trace.Invalid = true; trace.ExplicitlyStopped = false; return; }
        if (type is "request_user_input" or "exec_approval_request" or "apply_patch_approval_request" or "request_permissions" or "elicitation_request")
        {
            if (turn is null) { trace.State = CodexObservedState.Unknown; trace.ExplicitlyStopped = false; return; }
            trace.State = CodexObservedState.WaitingForInput;
            SetEvidence(trace, timestamp, type!, position);
        }
        else if (type == "error")
        {
            trace.State = IsUsageLimit(ErrorCode(payload)) ? CodexObservedState.QuotaExhausted : CodexObservedState.UnableToContinue;
            SetEvidence(trace, timestamp, type!, position);
            trace.ExplicitlyStopped = false; // Error/429 is not a lifecycle terminal marker.
        }
        else if (type == "stream_error")
        {
            trace.State = CodexObservedState.Unknown; // May be retrying: no terminal inference.
            SetEvidence(trace, timestamp, type!, position);
            trace.ExplicitlyStopped = false;
        }
        else if (type is "item_started" or "item_completed" or "exec_command_begin" or "exec_command_end" or "patch_apply_begin" or "patch_apply_end")
        {
            if (turn is null) return; // Uncorrelated tool traffic is not task evidence.
            trace.State = CodexObservedState.Running;
            SetEvidence(trace, timestamp, type!, position);
        }
        else if (type == "token_count" && trace.State is CodexObservedState.Running or CodexObservedState.WaitingForInput)
        {
            // Heartbeat only for an already observed active turn; never resurrect a terminal.
            // Heartbeat renews freshness only inside an already observed active turn.
            // Record its actual kind; it does not infer or end a Waiting state.
            SetEvidence(trace, timestamp, type, position);
        }
    }

    static void SetEvidence(SessionTrace trace, DateTimeOffset timestamp, string kind, long position)
    { trace.EvidenceAt = timestamp; trace.EvidenceKind = kind; trace.EvidencePosition = position; }

    bool BoundToProject(JsonElement payload)
    {
        var cwd = Text(payload, "cwd");
        if (StatusEvidence.SameProject(cwd, projectRoot)) return true;
        try
        {
            if (!string.IsNullOrWhiteSpace(cwd) && Path.GetFullPath(cwd).StartsWith(projectRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { }
        foreach (var name in new[] { "workspace_roots", "runtime_workspace_roots" })
        {
            var roots = Element(payload, name);
            if (roots.ValueKind == JsonValueKind.Array && roots.EnumerateArray().Any(root => root.ValueKind == JsonValueKind.String && StatusEvidence.SameProject(root.GetString(), projectRoot))) return true;
        }
        return false;
    }

    static bool SafeDirectory(string path) => Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;
    static JsonElement Element(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;
    static string? Text(JsonElement element, string name) => Element(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
    static string? ErrorCode(JsonElement error)
    {
        var code = Element(error, "codex_error_info");
        if (code.ValueKind == JsonValueKind.String) return code.GetString();
        return code.ValueKind == JsonValueKind.Object ? code.EnumerateObject().FirstOrDefault().Name : null;
    }
    static bool IsUsageLimit(string? code) => code is "usage_limit_exceeded" or "usageLimitExceeded" or "UsageLimitExceeded";

    static int FindLatestStart(byte[] buffer, int index, DateTimeOffset now, CancellationToken token)
    {
        var latest = -1;
        while (index < buffer.Length)
        {
            token.ThrowIfCancellationRequested();
            var end = Array.IndexOf(buffer, (byte)'\n', index);
            if (end < 0) break;
            try
            {
                using var document = JsonDocument.Parse(new ReadOnlyMemory<byte>(buffer, index, end - index), new() { MaxDepth = 64 });
                var record = document.RootElement;
                var payload = Element(record, "payload");
                if (Text(record, "type") == "event_msg" && Text(payload, "type") is "task_started" or "turn_started"
                    && StatusEvidence.ValidIdentity(Text(payload, "turn_id"))
                    && DateTimeOffset.TryParse(Text(record, "timestamp"), out var time) && time <= now) latest = index;
            }
            catch (JsonException) { }
            index = end + 1;
        }
        return latest;
    }

    sealed class SessionTrace(string sessionId, bool metadataMatches, string metadataFingerprint, string declaredProject)
    {
        public string SessionId { get; } = sessionId;
        public bool MetadataMatches { get; } = metadataMatches;
        public string MetadataFingerprint { get; } = metadataFingerprint;
        public string DeclaredProject { get; set; } = declaredProject;
        public bool ProjectMatches { get; set; } = metadataMatches;
        public string? TurnId { get; set; }
        public CodexObservedState State { get; set; } = CodexObservedState.Unknown;
        public DateTimeOffset? EvidenceAt { get; set; }
        public string EvidenceKind { get; set; } = "";
        public long? EvidencePosition { get; set; }
        public DateTimeOffset? LastRecordAt { get; set; }
        public long Offset { get; set; }
        public long Length { get; set; }
        public DateTime Modified { get; set; }
        public bool ExplicitlyStopped { get; set; }
        public bool Partial { get; set; }
        public bool Invalid { get; set; }
        public bool SelectedDisplayOnly { get; set; }
        public bool OtherTurnActivity { get; set; }
        public HashSet<string> ActiveTurns { get; } = [];
        public Dictionary<string, bool> TurnScopes { get; } = new(StringComparer.Ordinal);
        public bool IsFresh(DateTimeOffset now) => EvidenceAt is { } evidence && evidence <= now && now - evidence <= StatusEvidence.MaximumEvidenceAge;
    }
}

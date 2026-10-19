using System.Text.Json;
using CodexQuotaWidget.Core.Automation;
using CodexQuotaWidget.Core.ControlCenter;

/// <summary>Simulated models/transports with real D-drive state and lease I/O. Never sends an AI request.</summary>
public static class CodexLedArithmeticChecks
{
    const string Parent = @"D:\CodexQuotaWidget-GitHub\artifacts\codex-led-arithmetic-checks";
    const string Session = "57e3bf9f-3fd2-497f-b661-2d03baf0e974";
    const string Target = "5e327e3c-5c98-4678-8d59-5365e8d70f6d";
    const string Caller = "30e7686e-4d4a-4d6d-9b1b-de765455e07a";
    public static int Run() => RunAsync().GetAwaiter().GetResult();

    static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool condition, string label)
        { if (!condition) throw new InvalidOperationException("Codex-led simulated relay: " + label); count++; }
        var root = Path.GetFullPath(Path.Combine(Parent, Guid.NewGuid().ToString("N")));
        if (!AutomationStore.IsSafeDPath(root)) throw new InvalidDataException("Fixture must stay on D.");
        var project = Path.Combine(root, "project"); Directory.CreateDirectory(project);
        Fixture New(string name, Behavior behavior = Behavior.Normal,
            Func<CancellationToken, Task<QuotaGuardSnapshot>>? quota = null)
            => new(Path.Combine(root, name), project, behavior, quota);

        var complete = New("complete");
        Check((await complete.Service.RunAsync()).Success && complete.Service.State.Phase == CodexLedArithmeticPhase.Completed,
            "five simulated owned Codex turns finish at six");
        var state = complete.Service.State;
        Check(state.Turns.Select(item => item.Stage).SequenceEqual(new[] { "codex_initial_2", "codex_send_2", "codex_read_gpt_after_2", "codex_send_4", "codex_read_gpt_after_4" }),
            "widget schedules compute, send, read/compute, send, read/compute");
        Check(state.Turns.Select(item => item.Result).SequenceEqual(new[] { "2", "DISPATCHED", "4", "DISPATCHED", "6" }),
            "completed native turns rather than a local arithmetic answer drive each stage");
        Check(complete.Trace.SequenceEqual(new[] { "codex:0", "codex:1", "tool:send:2", "gpt:send:2", "gpt:observe:2", "codex:2", "tool:read", "gpt:read:3", "codex:3", "tool:send:4", "gpt:send:4", "gpt:observe:4", "codex:4", "tool:read", "gpt:read:5" }),
            "widget observes completion while only owned Codex tool calls send/read GPT");
        Check(complete.Transport.Sends.Count == 2 && complete.Transport.Reads == 2 && complete.Actor.Requests.Count == 5,
            "no extra advice, review, or root-agent relay request");
        Check(complete.Actor.Requests.All(item => item.Provider == WorkflowProvider.Codex && item.WorkflowTaskId == state.WorkflowTaskId)
            && complete.Transport.Sends.All(item => item.Provider == WorkflowProvider.ChatGPT && item.WorkflowTaskId == state.WorkflowTaskId),
            "one stable task identity and separate provider boundaries");
        Check(complete.Actor.Requests.Select(item => item.RequestId).Concat(complete.Transport.Sends.Select(item => item.RequestId)).Distinct().Count() == 7,
            "each native and GPT request has a unique correlation");
        Check(complete.Actor.Requests[0].ContinuationId is null && complete.Actor.Requests.Skip(1).All(item => item.ContinuationId == Session),
            "only the owned Codex session continues");
        Check(state.ToolCalls.Count(item => item.Status == "sent_by_codex_tool") == 2 && state.ToolCalls.Count(item => item.Status == "read_by_codex_tool") == 2,
            "completion retains native send and read evidence");
        Check(state.GptReadEvidence.Length == 2 && state.GptReadEvidence.All(item => item.GetProperty("WorkflowTaskId").GetString() == state.WorkflowTaskId.ToString("D")
            && item.GetProperty("TargetThreadId").GetString() == Target && item.GetProperty("EvidenceSource").GetString() == "official_desktop_projection"),
            "only identity-checked official projections become durable read evidence");
        Check(state.PendingCodexRequestId == Guid.Empty && state.PendingGptRequestId == Guid.Empty && !state.NeedsRecoveryConfirmation,
            "completed checkpoint clears uncertainty");
        Check(File.Exists(complete.Service.StatePath) && !File.Exists(complete.Service.StatePath + ".tmp")
            && !File.Exists(Path.Combine(Path.GetDirectoryName(complete.Service.StatePath)!, "codex-led-arithmetic-run.lock")),
            "real durable D state is atomic and lease released");
        var completedBytes = File.ReadAllBytes(complete.Service.StatePath);
        Check(!(await complete.Service.RunAsync()).Success && complete.Actor.Requests.Count == 5, "completed task never replays");
        Check(completedBytes.SequenceEqual(File.ReadAllBytes(complete.Service.StatePath)), "completed rerun preserves evidence bytes");
        var completedReload = New("complete");
        Check(!(await completedReload.Service.RunAsync()).Success && completedReload.Actor.Requests.Count == 0,
            "service restart preserves completed task without dispatch");

        foreach (var behavior in new[] { Behavior.ReferenceBlocked, Behavior.ReferenceGuessed })
        {
            var referenced = New(behavior.ToString(), behavior);
            Check(!(await referenced.Service.RunAsync()).Success && referenced.Service.State.Phase == CodexLedArithmeticPhase.Paused,
                "reference-only GPT projection cannot complete: " + behavior);
            Check(referenced.Actor.Requests.Count == 3 && referenced.Transport.Sends.Count == 1 && referenced.Transport.Reads == 1,
                "unreadable reply stops after the owned read turn, no second send");
            Check(referenced.Service.State.ToolCalls.Any(item => item.Status == "body_unreadable" && item.ContainsContentReference)
                && referenced.Service.State.PendingGptRequestId != Guid.Empty && referenced.Service.State.NeedsRecoveryConfirmation,
                "reference and exact pending GPT request remain durable");
            var saved = File.ReadAllBytes(referenced.Service.StatePath);
            var reload = New(behavior.ToString(), behavior);
            Check(!(await reload.Service.RunAsync()).Success && reload.Actor.Requests.Count == 0 && reload.Transport.Sends.Count == 0
                && saved.SequenceEqual(File.ReadAllBytes(reload.Service.StatePath)), "unreadable or guessed result cannot be replayed on restart");
        }

        var noSend = New("no-send", Behavior.NoSendTool);
        Check(!(await noSend.Service.RunAsync()).Success && noSend.Transport.Sends.Count == 0 && noSend.Transport.Observations == 0,
            "DISPATCHED without a native tool call is not a send receipt");
        var foreignTool = New("foreign-tool", Behavior.ForeignToolOwner);
        Check(!(await foreignTool.Service.RunAsync()).Success && foreignTool.Transport.Observations == 0,
            "send evidence from another native turn cannot advance this turn");
        var unconfirmed = New("unconfirmed", Behavior.SendUnconfirmed);
        Check(!(await unconfirmed.Service.RunAsync()).Success && unconfirmed.Transport.Sends.Count == 1
            && unconfirmed.Service.State.ToolCalls.Any(item => item.Status == "send_unconfirmed"), "uncertain send pauses with evidence");
        var unconfirmedReload = New("unconfirmed");
        Check(!(await unconfirmedReload.Service.RunAsync()).Success && unconfirmedReload.Transport.Sends.Count == 0,
            "uncertain send is never retried by another run");

        foreach (var behavior in new[] { Behavior.ForgedTask, Behavior.ForgedRequest, Behavior.ForgedTarget,
            Behavior.ForgedPayloadTurn, Behavior.ForgedPayloadSource, Behavior.ForgedEvidenceSource,
            Behavior.ChangedObservationTurn, Behavior.ChangedObservationSource, Behavior.ChangedObservationStatus })
        {
            var swapped = New(behavior.ToString(), behavior);
            Check(!(await swapped.Service.RunAsync()).Success && swapped.Actor.Requests.Count == 3 && swapped.Transport.Sends.Count == 1,
                "swapped read projection cannot reach another send: " + behavior);
            Check(swapped.Service.State.ToolCalls.Any(item => item.Status == "read_identity_rejected")
                && swapped.Service.State.GptReadEvidence.Length == 0 && !swapped.Bridge.ReadBodyAvailable,
                "wrong identity or changed completion observation is rejected before recording body: " + behavior);
            Check(swapped.Bridge.LastRead is { Success: false } && swapped.Bridge.LastRead.Payload.ValueKind == JsonValueKind.Undefined,
                "rejected projection body is not exposed to native Codex: " + behavior);
        }
        foreach (var behavior in new[] { Behavior.WrongObservedTarget, Behavior.FailedObservedTurn, Behavior.UnknownRequest })
        {
            var unavailable = New(behavior.ToString(), behavior);
            Check(!(await unavailable.Service.RunAsync()).Success && unavailable.Actor.Requests.Count == 2
                && unavailable.Transport.Observations == 1 && unavailable.Transport.Reads == 0,
                "permanent GPT correlation failure pauses immediately without a read turn: " + behavior);
        }

        foreach (var behavior in new[] { Behavior.ChangedSession, Behavior.ReusedTurn, Behavior.MissingTurn, Behavior.MissingSource, Behavior.CodexFailed })
        {
            var invalid = New(behavior.ToString(), behavior);
            Check(!(await invalid.Service.RunAsync()).Success && invalid.Service.State.Phase == CodexLedArithmeticPhase.Paused,
                "invalid native/completion evidence pauses: " + behavior);
            Check(invalid.Service.State.NeedsRecoveryConfirmation && invalid.Actor.Requests.Count <= 3 && invalid.Transport.Sends.Count <= 1,
                "invalid evidence does not advance or start second GPT request: " + behavior);
        }
        var disconnected = New("disconnected", Behavior.Disconnected);
        Check(!(await disconnected.Service.RunAsync()).Success && disconnected.Actor.Requests.Count == 0 && !File.Exists(disconnected.Service.StatePath),
            "disconnected Codex dispatches no request");
        var disconnectLater = New("disconnect-later", Behavior.DisconnectedDuringRead);
        Check(!(await disconnectLater.Service.RunAsync()).Success && disconnectLater.Actor.Requests.Count == 3
            && disconnectLater.Transport.Sends.Count == 1 && disconnectLater.Service.State.PendingCodexRequestId != Guid.Empty,
            "read-turn disconnect retains unresolved native request, no replay");

        foreach (var guard in new[]
        {
            new QuotaGuardSnapshot(QuotaGuardState.Unknown, null, null, false, false, "unknown"),
            new QuotaGuardSnapshot(QuotaGuardState.Ready, 20, 90, true, false, "20 percent boundary"),
            new QuotaGuardSnapshot(QuotaGuardState.Ready, double.NaN, 90, true, false, "NaN"),
            new QuotaGuardSnapshot(QuotaGuardState.Ready, 101, 90, true, false, "out of range"),
            new QuotaGuardSnapshot(QuotaGuardState.Protected, 80, 90, true, true, "protected"),
            new QuotaGuardSnapshot(QuotaGuardState.Ready, 80, 90, false, false, "not authorized")
        })
        {
            var invalidQuota = New("quota-" + count, quota: _ => Task.FromResult(guard));
            Check(!(await invalidQuota.Service.RunAsync()).Success && invalidQuota.Actor.Requests.Count == 0 && invalidQuota.Transport.Sends.Count == 0,
                "quota safety blocks all model traffic: " + guard.Reason);
        }
        Check(CodexLedArithmeticService.CanDispatch(Ready())
            && CodexLedArithmeticService.CanDispatch(new(QuotaGuardState.Warning, 20.1, 90, true, true, "warning")), "valid quota strictly above twenty is accepted");
        var quotaReads = 0;
        var quotaChanges = New("quota-changes", quota: _ => Task.FromResult(++quotaReads <= 2 ? Ready() : new(QuotaGuardState.Unknown, null, null, false, false, "lost quota")));
        Check(!(await quotaChanges.Service.RunAsync()).Success && quotaChanges.Actor.Requests.Count == 1 && quotaChanges.Transport.Sends.Count == 0,
            "quota is rechecked before every native turn");

        using var cancellation = new CancellationTokenSource();
        var cancelled = New("cancelled");
        cancelled.Actor.AfterRequest = _ => cancellation.Cancel();
        Check(!(await cancelled.Service.RunAsync(cancellation.Token)).Success && cancelled.Service.State.PendingCodexRequestId != Guid.Empty
            && cancelled.Service.State.Turns.Length == 0 && cancelled.Transport.Sends.Count == 0, "cancellation after dispatch retains unknown native request");
        Check(!(await cancelled.Service.RunAsync()).Success && cancelled.Actor.Requests.Count == 1, "cancelled native turn cannot be resent");
        var pending = New("concurrent");
        var pendingReply = new TaskCompletionSource<WorkflowExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending.Actor.PendingReply = pendingReply;
        var ownerRun = pending.Service.RunAsync();
        var activeBytes = File.ReadAllBytes(pending.Service.StatePath);
        Check(!(await pending.Service.RunAsync()).Success && pending.Actor.Requests.Count == 1, "same service rejects overlapping run");
        var competitor = New("concurrent");
        Check(!(await competitor.Service.RunAsync()).Success && competitor.Actor.Requests.Count == 0
            && activeBytes.SequenceEqual(File.ReadAllBytes(pending.Service.StatePath)), "exclusive D lease preserves live pending state across services");
        pendingReply.SetResult(new(false, "simulated disconnect"));
        await ownerRun;
        Check(!pending.Service.IsRunning, "failure releases in-memory ownership and file lease");

        var bytesBeforeMismatch = File.ReadAllBytes(complete.Service.StatePath);
        foreach (var (boundProject, target, caller) in new[]
        {
            (project, Guid.NewGuid().ToString(), Caller), (project, Target, Guid.NewGuid().ToString()),
            (Path.Combine(root, "another-project"), Target, Caller)
        })
        {
            Directory.CreateDirectory(boundProject); var rejected = false;
            try { _ = new CodexLedArithmeticService(complete.Data, complete.Actor, new CodexGptRelayBridge(new Transport([], Behavior.Normal)),
                _ => Task.FromResult(Ready()), boundProject, target, caller); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected && bytesBeforeMismatch.SequenceEqual(File.ReadAllBytes(complete.Service.StatePath)),
                "stored task cannot be rebound to another project, GPT target, or caller");
        }

        // Native tool retries and collision handling are safety properties separate from the fixture's happy path.
        var directTrace = new List<string>(); var directTransport = new Transport(directTrace, Behavior.Normal);
        var directBridge = new CodexGptRelayBridge(directTransport); var task = Guid.NewGuid(); var request = Guid.NewGuid();
        var callId = Session + "/direct-turn/call"; directBridge.ConfigureSend(task, request, "2");
        var receipt = await directBridge.InvokeAsync("send_result", JsonSerializer.SerializeToElement(new { result = "2" }), callId);
        var duplicate = await directBridge.InvokeAsync("send_result", JsonSerializer.SerializeToElement(new { result = "2" }), callId);
        Check(receipt.Success && duplicate.Success && directTransport.Sends.Count == 1, "duplicate native call returns cached receipt without duplicate send");
        Check(!(await directBridge.InvokeAsync("send_result", JsonSerializer.SerializeToElement(new { result = "4" }), callId)).Success && directTransport.Sends.Count == 1,
            "same native identity with changed arguments is rejected");
        Check((await directBridge.InvokeAsync("send_result", JsonSerializer.SerializeToElement(new { result = "2" }), Session + "/direct-turn/second")).Success
            && directTransport.Sends.Count == 1, "second valid native call cannot send same GPT request twice");
        directBridge.Disable();
        Check(!(await directBridge.InvokeAsync("send_result", JsonSerializer.SerializeToElement(new { result = "2" }), Session + "/disabled-turn/call")).Success,
            "ordinary disabled stage cannot invoke relay capabilities");
        directBridge.ConfigureSend(task, Guid.NewGuid(), "2");
        Check(!(await directBridge.InvokeAsync("send_result", JsonSerializer.SerializeToElement(new { result = "2", target = Target }), Session + "/extra-property/call")).Success,
            "Codex cannot change destination through tool arguments");
        Check(!(await directBridge.InvokeAsync("send_result", JsonSerializer.SerializeToElement(new { result = "2" }), "not-owned-call")).Success,
            "missing native session/turn/call identity is rejected");
        directBridge.BindTarget(Target);
        var targetRebindRejected = false;
        try { directBridge.BindTarget(Guid.NewGuid().ToString()); } catch (ArgumentException) { targetRebindRejected = true; }
        Check(targetRebindRejected, "an established bridge cannot silently change target conversation");

        var failedTransport = new Transport([], Behavior.SendUnconfirmed); var failedBridge = new CodexGptRelayBridge(failedTransport);
        failedBridge.ConfigureSend(task, Guid.NewGuid(), "2");
        await failedBridge.InvokeAsync("send_result", JsonSerializer.SerializeToElement(new { result = "2" }), Session + "/failed-send/first");
        await failedBridge.InvokeAsync("send_result", JsonSerializer.SerializeToElement(new { result = "2" }), Session + "/failed-send/second");
        Check(failedTransport.Sends.Count == 1 && failedBridge.Evidence.Any(item => item.Status == "send_unconfirmed"),
            "unconfirmed transport receipt forbids another outbound attempt");
        return count;
    }

    static QuotaGuardSnapshot Ready() => new(QuotaGuardState.Ready, 80, 90, true, false, "simulated valid quota");
    enum Behavior { Normal, ReferenceBlocked, ReferenceGuessed, NoSendTool, ForeignToolOwner, SendUnconfirmed,
        ChangedSession, ReusedTurn, MissingTurn, MissingSource, CodexFailed, Disconnected, DisconnectedDuringRead,
        ForgedTask, ForgedRequest, ForgedTarget, ForgedPayloadTurn, ForgedPayloadSource, ForgedEvidenceSource,
        ChangedObservationTurn, ChangedObservationSource, ChangedObservationStatus, WrongObservedTarget, FailedObservedTurn, UnknownRequest }

    sealed class Fixture
    {
        public string Data { get; }
        public List<string> Trace { get; } = [];
        public Transport Transport { get; }
        public CodexGptRelayBridge Bridge { get; }
        public Actor Actor { get; }
        public CodexLedArithmeticService Service { get; }
        public Fixture(string data, string project, Behavior behavior, Func<CancellationToken, Task<QuotaGuardSnapshot>>? quota)
        {
            Data = data; Transport = new(Trace, behavior); Bridge = new(Transport); Actor = new(Bridge, Trace, behavior);
            Service = new(data, Actor, Bridge, quota ?? (_ => Task.FromResult(Ready())), project, Target, Caller);
        }
    }

    sealed class Actor(CodexGptRelayBridge bridge, List<string> trace, Behavior behavior) : IWorkflowConnector
    {
        public string Id => "simulated-owned-codex";
        public WorkflowConnectionState ConnectionState => CanExecute ? WorkflowConnectionState.Ready : WorkflowConnectionState.NotConnected;
        public bool CanExecute => behavior != Behavior.Disconnected;
        public List<WorkflowExecutionRequest> Requests { get; } = [];
        public Action<WorkflowExecutionRequest>? AfterRequest { get; set; }
        public TaskCompletionSource<WorkflowExecutionResult>? PendingReply { get; set; }
        public async Task<WorkflowExecutionResult> ExecuteAsync(WorkflowExecutionRequest request, CancellationToken token = default)
        {
            var index = Requests.Count; Requests.Add(request); trace.Add("codex:" + index); AfterRequest?.Invoke(request);
            if (PendingReply is not null) return await PendingReply.Task;
            if (behavior == Behavior.CodexFailed || behavior == Behavior.DisconnectedDuringRead && index == 2)
                return new(false, "simulated Codex connection failure");
            var session = behavior == Behavior.ChangedSession && index == 1 ? Guid.NewGuid().ToString() : Session;
            var turn = behavior == Behavior.ReusedTurn && index == 1 ? "codex-turn-0" : "codex-turn-" + index;
            if (behavior == Behavior.MissingTurn) turn = "";
            var call = (behavior == Behavior.ForeignToolOwner ? "foreign-session/foreign-turn" : session + "/" + turn) + "/tool-call";
            string answer;
            if (index is 1 or 3)
            {
                var value = index == 1 ? "2" : "4";
                if (behavior == Behavior.NoSendTool) answer = "DISPATCHED";
                else
                {
                    trace.Add("tool:send:" + value);
                    var reply = await bridge.InvokeAsync("send_result", JsonSerializer.SerializeToElement(new { result = value }), call, token);
                    answer = reply.Success ? "DISPATCHED" : "BLOCKED";
                }
            }
            else if (index is 2 or 4)
            {
                trace.Add("tool:read");
                await bridge.InvokeAsync("read_reply", JsonSerializer.SerializeToElement(new { }), call, token);
                answer = bridge.ReadBodyAvailable || behavior == Behavior.ReferenceGuessed ? index == 2 ? "4" : "6" : "BLOCKED";
            }
            else answer = "2";
            return new(true, answer, session) { CompletedTurnId = turn };
        }
        public Task<SafeStopResult> RequestSafeStopAsync(Guid taskId, CancellationToken token = default)
            => Task.FromResult(new SafeStopResult(false, "fixture never sends process or system actions"));
    }

    sealed class Transport(List<string> trace, Behavior behavior) : ICodexGptRelayTransport
    {
        public List<WorkflowExecutionRequest> Sends { get; } = [];
        public int Observations { get; private set; }
        public int Reads { get; private set; }
        string value = "", message = "", turn = "", marker = "";
        public Task<GptRelaySubmission> SendFromCodexAsync(WorkflowExecutionRequest request, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); Sends.Add(request);
            value = Sends.Count == 1 ? "2" : "4"; message = "gpt-sent-" + Sends.Count; turn = "gpt-turn-" + Sends.Count;
            marker = DesktopChatGptWorkflowConnector.BuildRequestMarker(request); trace.Add("gpt:send:" + value);
            return Task.FromResult(new GptRelaySubmission(behavior != Behavior.SendUnconfirmed,
                behavior == Behavior.SendUnconfirmed ? "simulated uncertain delivery" : "simulated receipt", message));
        }
        public Task<GptRelayObservation> ObserveForCodexAsync(Guid task, Guid request, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); Observations++; trace.Add("gpt:observe:" + value);
            var observation = CodexGptRelayProjection.Observe(Snapshot(), Target, marker, new HashSet<string>(), message, turn);
            if (behavior == Behavior.MissingSource) observation = observation with { SourceMessageId = "" };
            if (behavior == Behavior.UnknownRequest) observation = observation with { Ready = false, DiagnosticCode = "relay_request_unknown" };
            return Task.FromResult(observation);
        }
        public Task<GptRelayReply> ReadForCodexAsync(Guid task, Guid request, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); Reads++; trace.Add("gpt:read:" + (value == "2" ? "3" : "5"));
            var reply = CodexGptRelayProjection.Read(Snapshot(), Target, task, request, marker, new HashSet<string>(), message, turn);
            var forgedProperty = behavior switch
            {
                Behavior.ForgedTask => "WorkflowTaskId", Behavior.ForgedRequest => "RequestId", Behavior.ForgedTarget => "TargetThreadId",
                Behavior.ForgedPayloadTurn => "TurnId", Behavior.ForgedPayloadSource => "SourceMessageId", Behavior.ForgedEvidenceSource => "EvidenceSource", _ => null
            };
            if (forgedProperty is not null)
            {
                var values = reply.Payload.EnumerateObject().ToDictionary(item => item.Name, item => item.Value.Clone());
                values[forgedProperty] = JsonSerializer.SerializeToElement("foreign-" + forgedProperty);
                reply = reply with { Payload = JsonSerializer.SerializeToElement(values) };
            }
            if (behavior == Behavior.ChangedObservationTurn) reply = reply with { Observation = reply.Observation with { TurnId = "another-completed-turn" } };
            if (behavior == Behavior.ChangedObservationSource) reply = reply with { Observation = reply.Observation with { SourceMessageId = "another-agent-message" } };
            if (behavior == Behavior.ChangedObservationStatus) reply = reply with { Observation = reply.Observation with { ThreadStatus = "running" } };
            return Task.FromResult(reply);
        }
        JsonElement Snapshot()
        {
            var body = behavior is Behavior.ReferenceBlocked or Behavior.ReferenceGuessed
                ? "::chatgpt-content-reference{source_message_id=\"gpt-reference\" index=0}" : value == "2" ? "3" : "5";
            return JsonSerializer.SerializeToElement(new { thread = new { id = behavior == Behavior.WrongObservedTarget ? Caller : Target,
                kind = "chatgpt", status = new { type = "idle" } },
                turns = new[] { new { id = turn, status = behavior == Behavior.FailedObservedTurn ? "failed" : "completed", items = new object[]
                {
                    new { type = "userMessage", id = message, content = new[] { new { type = "text", text = marker + "\nfixture input" } } },
                    new { type = "agentMessage", id = "gpt-source-" + Sends.Count, text = body }
                } } } });
        }
    }
}

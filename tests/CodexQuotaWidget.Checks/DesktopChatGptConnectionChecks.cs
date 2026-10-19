using System.IO.Pipes;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodexQuotaWidget.Core.ControlCenter;

/// <summary>Routing policy fixtures plus a real local fake-pipe rejection; no model calls or user session writes.</summary>
public static class DesktopChatGptConnectionChecks
{
    public static int Run() => RunAsync().GetAwaiter().GetResult();
    static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool value, string name) { if (!value) throw new InvalidOperationException("GPT connection: " + name); count++; }
        var first = @"\\.\pipe\codex-browser-use-11111111-1111-4111-8111-111111111111";
        var second = @"\\.\pipe\codex-browser-use-22222222-2222-4222-8222-222222222222";
        var a = new DesktopPipeCandidate(first, 1, true, "fixture", TargetVerified: true);
        var b = new DesktopPipeCandidate(second, 1, true, "fixture", TargetVerified: true);
        Check(DesktopPipeDiscovery.IsSupportedPipe(first), "canonical official-prefix GUID accepted as candidate only");
        Check(!DesktopPipeDiscovery.IsSupportedPipe(@"\\.\pipe\codex-browser-use-not-a-guid"), "malformed candidate rejected");
        Check(!DesktopPipeDiscovery.IsSupportedPipe(@"\\.\pipe\another-app-11111111-1111-4111-8111-111111111111"), "unrelated pipe excluded");
        Check(DesktopPipeSelection.Resolve([]).PipePath is null, "empty candidate list cannot connect");
        Check(DesktopPipeSelection.Resolve([a]).PipePath == first, "single fully verified route selected");
        Check(DesktopPipeSelection.Resolve([a, b]).PipePath == first, "each independently verified same-owner target route can collapse");
        Check(DesktopPipeSelection.Resolve([b, a]).PipePath == first, "equivalent route selection deterministic independent of enumeration order");
        Check(DesktopPipeSelection.Resolve([a, b]).DiagnosticCode == "equivalent_target_routes_verified", "equivalent transports distinguished from multiple sessions");
        var foreign = b with { OwnerProcessId = 2 };
        Check(DesktopPipeSelection.Resolve([a, foreign]).DiagnosticCode == "desktop_pipe_not_unique", "different valid owners remain explicit conflict");
        Check(DesktopPipeSelection.Resolve([a, foreign], second).PipePath == second, "explicit verified target route resolves owner conflict");
        Check(DesktopPipeSelection.Resolve([a], second).PipePath is null, "arbitrary explicit path never accepted");
        Check(DesktopPipeSelection.Resolve([a with { IdentityVerified = false }]).PipePath is null, "target knowledge cannot bypass native identity");
        Check(DesktopPipeSelection.Resolve([a with { TargetVerified = false }]).PipePath is null, "owner identity cannot imply target session");
        Check(DesktopPipeSelection.Resolve([a with { OwnerProcessId = null }]).PipePath is null, "unavailable owner cannot select");
        Check(DesktopPipeSelection.Resolve([a with { OwnerProcessId = 0 }]).PipePath is null, "invalid process id cannot select");
        Check(DesktopPipeSelection.Resolve([a, foreign with { TargetVerified = false }]).PipePath == first, "unverified additional owner does not create false conflict");
        Check(DesktopPipeSelection.Resolve([a, foreign with { IdentityVerified = false }], second).PipePath is null, "explicit selection cannot bypass verification");
        Check(DesktopChatGptWorkflowConnector.ResolveVerifiedRoute([a, foreign], priorPipe: first, priorOwner: 1).PipePath == first,
            "previous verified route is reused despite unrelated verified owner");
        var otherOwnerPath = foreign with { PipePath = @"\\.\pipe\codex-browser-use-33333333-3333-4333-8333-333333333333" };
        Check(DesktopChatGptWorkflowConnector.ResolveVerifiedRoute([b, otherOwnerPath], priorPipe: first, priorOwner: 1).PipePath == second,
            "recovery may use independently verified equivalent route belonging to original owner");
        Check(DesktopChatGptWorkflowConnector.ResolveVerifiedRoute([foreign], priorPipe: first, priorOwner: 1).PipePath is null,
            "recovery never silently switches native owners");
        Check(DesktopChatGptWorkflowConnector.ResolveVerifiedRoute([a with { OwnerProcessId = 2 }], priorPipe: first, priorOwner: 1).DiagnosticCode == "desktop_pipe_owner_changed",
            "same path under changed owner fails closed");
        Check(DesktopChatGptWorkflowConnector.ResolveVerifiedRoute([a with { TargetVerified = false }, foreign], priorPipe: first, priorOwner: 1).PipePath is null,
            "prior path cannot bypass current target revalidation");
        Check(DesktopChatGptWorkflowConnector.ResolveVerifiedRoute([a, foreign], requestedPipe: second, priorPipe: first, priorOwner: 1).PipePath == second,
            "explicit newly verified selection can replace prior route");
        Check(a.PipeName == "codex-browser-use-11111111-1111-4111-8111-111111111111", "display name exposes no conversation text");
        var id = Guid.NewGuid().ToString();
        using var good = JsonDocument.Parse(JsonSerializer.Serialize(new { thread = new { id, kind = "chatgpt", status = new { type = "idle" } }, turns = Array.Empty<object>() }));
        Check(DesktopChatGptWorkflowConnector.IsConfiguredTarget(good.RootElement, id), "exact target metadata validates without reading history");
        Check(!DesktopChatGptWorkflowConnector.IsConfiguredTarget(good.RootElement, Guid.NewGuid().ToString()), "different readable GPT session rejected");
        Check(DesktopChatGptWorkflowConnector.ReadThreadStatus(good.RootElement) == "idle", "native discriminated idle accepted");
        using var wrongKind = JsonDocument.Parse(JsonSerializer.Serialize(new { thread = new { id, kind = "codex" }, turns = Array.Empty<object>() }));
        Check(!DesktopChatGptWorkflowConnector.IsConfiguredTarget(wrongKind.RootElement, id), "Codex session cannot be mistaken for GPT");
        Check(DesktopChatGptWorkflowConnector.IsConfiguredCaller(wrongKind.RootElement, id), "exact configured Codex caller identity validates");
        Check(!DesktopChatGptWorkflowConnector.IsConfiguredCaller(good.RootElement, id), "GPT target cannot substitute for Codex caller");
        Check(!DesktopChatGptWorkflowConnector.IsConfiguredCaller(wrongKind.RootElement, Guid.NewGuid().ToString()), "other readable caller rejected");
        using var unknown = JsonDocument.Parse("{\"thread\":{},\"turns\":[]}");
        Check(DesktopChatGptWorkflowConnector.ReadThreadStatus(unknown.RootElement) == "unknown", "missing state remains Unknown");
        using var completedTurn = JsonDocument.Parse("{\"status\":\"completed\"}");
        Check(DesktopChatGptWorkflowConnector.HasConfirmedCompletion(good.RootElement, completedTurn.RootElement), "matched completed turn plus idle thread permits completion read");
        Check(!DesktopChatGptWorkflowConnector.HasConfirmedCompletion(unknown.RootElement, completedTurn.RootElement), "projected completed turn cannot override unknown thread status");
        foreach (var status in new[] { "running", "active", "waiting" })
        {
            using var busy = JsonDocument.Parse(JsonSerializer.Serialize(new { thread = new { status = new { type = status } } }));
            Check(!DesktopChatGptWorkflowConnector.HasConfirmedCompletion(busy.RootElement, completedTurn.RootElement), "projected completed turn cannot override busy conversation " + status);
        }
        using var unfinishedTurn = JsonDocument.Parse("{\"status\":\"inProgress\"}");
        Check(!DesktopChatGptWorkflowConnector.HasConfirmedCompletion(good.RootElement, unfinishedTurn.RootElement), "idle conversation cannot complete unfinished turn");
        using var reply = JsonDocument.Parse("{\"items\":[{\"type\":\"agentMessage\",\"text\":\"ACC_REVIEW: COMPLETE\"}]}");
        Check(DesktopChatGptWorkflowConnector.ReadCompletedAnswer(reply.RootElement) == "ACC_REVIEW: COMPLETE", "real review text preserved");
        using var reference = JsonDocument.Parse("{\"items\":[{\"type\":\"agentMessage\",\"text\":\"::chatgpt-content-reference{index=0}\"}]}");
        var unresolved = false;
        try { _ = DesktopChatGptWorkflowConnector.ReadCompletedAnswer(reference.RootElement); }
        catch (WorkflowRpcException error) { unresolved = error.Code == "chatgpt_reply_reference_unresolved"; }
        Check(unresolved, "content reference cannot count as advice or a review verdict");
        var sourceId = Guid.NewGuid().ToString("D");
        using var nativeReference = JsonDocument.Parse(JsonSerializer.Serialize(new { items = new[] {
            new { type = "agentMessage", text = $"::chatgpt-content-reference{{index=\"1\" source_message_id=\"{sourceId}\"}}" } } }));
        Check(DesktopChatGptWorkflowConnector.ReadReferenceSourceMessageId(nativeReference.RootElement) == sourceId,
            "native source message ID retained as evidence without inventing reply text");
        Check(DesktopChatGptWorkflowConnector.ReadReferenceSourceMessageId(reference.RootElement) == "", "index alone cannot identify reply source");
        Check(DesktopChatGptWorkflowConnector.ReadReferenceSourceMessageId(reply.RootElement) == "", "ordinary plaintext is not a reference");
        using var directReply = JsonDocument.Parse(JsonSerializer.Serialize(new { items = new[] {
            new { type = "agentMessage", id = sourceId, text = "fixture completed reply" } } }));
        Check(DesktopChatGptWorkflowConnector.ReadReplySourceMessageId(directReply.RootElement) == sourceId,
            "plaintext reply retains its official source message ID");
        Check(DesktopChatGptWorkflowConnector.ReadReplySourceMessageId(reference.RootElement) == "",
            "index-only reference cannot use a placeholder as reply source");
        using var multipleReferences = JsonDocument.Parse(JsonSerializer.Serialize(new { items = new[] {
            new { type = "agentMessage", text = $"::chatgpt-content-reference{{index=\"1\" source_message_id=\"{sourceId}\"}}" },
            new { type = "agentMessage", text = $"::chatgpt-content-reference{{index=\"2\" source_message_id=\"{Guid.NewGuid():D}\"}}" } } }));
        Check(DesktopChatGptWorkflowConnector.ReadReferenceSourceMessageId(multipleReferences.RootElement) == "", "ambiguous source IDs never collapsed to arbitrary message");
        Check(!DesktopChatGptWorkflowConnector.IsConfiguredTarget(unknown.RootElement, id), "missing identity fails closed");
        using var invalidTurns = JsonDocument.Parse(JsonSerializer.Serialize(new { thread = new { id, kind = "chatgpt" } }));
        Check(!DesktopChatGptWorkflowConnector.IsConfiguredTarget(invalidTurns.RootElement, id), "missing turn protocol not claimed executable");
        Check(!DesktopChatGptWorkflowConnector.IsConfiguredCaller(invalidTurns.RootElement, id), "caller protocol with missing turns rejected");
        var workflowTask = Guid.NewGuid(); var requestId = Guid.NewGuid();
        var correlated = new WorkflowExecutionRequest(requestId, WorkflowProvider.ChatGPT, "fixture") { WorkflowTaskId = workflowTask };
        var marker = DesktopChatGptWorkflowConnector.BuildRequestMarker(correlated);
        Check(marker == $"[AIControlCenter task={workflowTask:D} request={requestId:D}]", "transport marker uses the actual workflow task and request IDs");
        Check(DesktopChatGptWorkflowConnector.BuildRequestMarker(correlated) == marker, "same request marker stable without extra random correlation IDs");
        var legacy = new WorkflowExecutionRequest(requestId, WorkflowProvider.ChatGPT, "fixture");
        Check(DesktopChatGptWorkflowConnector.BuildRequestMarker(legacy) == $"[AIControlCenter task={requestId:D} request={requestId:D}]", "legacy caller keeps original per-request fallback");
        var next = correlated with { TaskId = Guid.NewGuid() };
        Check(DesktopChatGptWorkflowConnector.BuildRequestMarker(next) != marker
            && DesktopChatGptWorkflowConnector.BuildRequestMarker(next).Contains(workflowTask.ToString("D")), "next request remains distinct within the same workflow task");
        var scoped = correlated with { AuthorizedProjectRoot = @"D:\fixture\authorized", AuthorizationId = "fixture-authorization" };
        var unbound = DesktopChatGptWorkflowConnector.CorrelateExecutionResult(new(false, "unknown delivery"), scoped);
        Check(!unbound.Success && unbound.CompletedTurnId.Length == 0 && unbound.RequestId == requestId && unbound.WorkflowTaskId == workflowTask,
            "local call correlation never fabricates remote completion after unknown delivery");
        var boundFailure = DesktopChatGptWorkflowConnector.CorrelateExecutionResult(new(false, "reference unresolved")
            { CompletedTurnId = "official-matched-turn", DiagnosticCode = "chatgpt_reply_reference_unresolved" }, scoped);
        Check(!boundFailure.Success && boundFailure.RequestId == requestId && boundFailure.WorkflowTaskId == workflowTask
            && boundFailure.ProjectRoot == scoped.AuthorizedProjectRoot && boundFailure.AuthorizationId == scoped.AuthorizationId,
            "matched unreadable reply retains request and authorized project context without becoming success");
        if (OperatingSystem.IsWindows())
        {
            await using (var disconnected = new DesktopChatGptWorkflowConnector(id, Guid.NewGuid().ToString("D"), @"D:\fixture\gpt-connection"))
            {
                var prevented = await disconnected.ExecuteAsync(scoped);
                Check(!prevented.Success && prevented.DispatchPrevented && prevented.CompletedTurnId.Length == 0
                    && prevented.RequestId == requestId && prevented.WorkflowTaskId == workflowTask && prevented.ProjectRoot == scoped.AuthorizedProjectRoot,
                    "unconnected real connector returns local call identity and proof of no dispatch without remote completion");
            }
            count += await RunSessionBindingChecksAsync();
            var name = "codex-browser-use-" + Guid.NewGuid().ToString("D");
            await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var accept = server.WaitForConnectionAsync();
            var evidence = await DesktopPipeDiscovery.InspectAsync(@"\\.\pipe\" + name);
            await accept.WaitAsync(TimeSpan.FromSeconds(3));
            Check(evidence.OwnerProcessId == Environment.ProcessId && !evidence.IdentityVerified, "real fake-prefix pipe owner rejected by native identity");
            Check(evidence.Reason == "owner_not_official_desktop", "real local fake pipe has explicit identity reason");
            count += await RunRpcFixturesAsync();
        }
        return count;
    }

    public static async Task<int> RunSessionBindingChecksAsync()
    {
        var target = Guid.NewGuid().ToString("D"); var requestId = Guid.NewGuid();
        await using var disconnected = new DesktopChatGptWorkflowConnector(target, Guid.NewGuid().ToString("D"), @"D:\fixture\gpt-connection");
        var request = new WorkflowExecutionRequest(requestId, WorkflowProvider.ChatGPT, "fixture")
        { WorkflowTaskId = Guid.NewGuid(), ExpectedSessionId = Guid.NewGuid().ToString("D") };
        var conflicting = await disconnected.ExecuteAsync(request);
        if (conflicting.Success || !conflicting.DispatchPrevented || conflicting.DiagnosticCode != "chatgpt_expected_session_mismatch"
            || conflicting.CompletedTurnId.Length > 0 || conflicting.RequestId != requestId)
            throw new InvalidOperationException("GPT connection: contradictory expected session was not rejected before dispatch");
        var rejectedCatalog = false;
        try { _ = await disconnected.ReadOfficialToolCatalogAsync(); }
        catch (WorkflowRpcException failure) { rejectedCatalog = failure.Code == "chatgpt_catalog_connection_not_ready"; }
        if (!rejectedCatalog) throw new InvalidOperationException("GPT connection: unverified route exposed official catalog");
        return 2;
    }

    static async Task<int> RunRpcFixturesAsync()
    {
        var count = 0;
        void Check(bool value, string name) { if (!value) throw new InvalidOperationException("GPT RPC: " + name); count++; }
        ProcessStartInfo Child(string script)
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"));
            start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-EncodedCommand");
            start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
            return start;
        }
        async Task<string> Failure(Task<JsonElement> operation)
        {
            try { _ = await operation; return "unexpected_success"; }
            catch (WorkflowRpcException error) { return error.Code; }
            catch (OperationCanceledException) { return "cancelled"; }
        }
        // These children are local protocol fixtures only. They never load a helper, app, or model.
        await using (var bridge = new WorkflowRpcProcess(Child("while($null -ne ($line=[Console]::ReadLine())){$r=$line|ConvertFrom-Json;if($r.method -eq 'withhold'){continue};if($r.method -eq 'close'){break};[Console]::WriteLine((@{jsonrpc='2.0';id=$r.id;result=@{method=$r.method}}|ConvertTo-Json -Compress))}")))
        {
            await bridge.StartAsync();
            _ = await bridge.CallAsync("warmup", timeout: TimeSpan.FromSeconds(15));
            Check(await Failure(bridge.CallAsync("withhold", timeout: TimeSpan.FromMilliseconds(150))) == "rpc_timeout", "response wait uses bounded timeout");
            Check(bridge.IsRunning, "response timeout does not fabricate transport failure or replay");
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            Check(await Failure(bridge.CallAsync("withhold", cancellationToken: cancel.Token)) == "cancelled", "caller cancellation stays cancellation");
            var after = await bridge.CallAsync("after_timeout", timeout: TimeSpan.FromSeconds(3));
            Check(after.GetProperty("method").GetString() == "after_timeout", "expired requests cannot satisfy later request");
            Check(await Failure(bridge.CallAsync("close", timeout: TimeSpan.FromSeconds(3))) is "rpc_disconnected" or "rpc_not_connected",
                "closed stdout fails pending response without claiming completion");
        }
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var bridge = new WorkflowRpcProcess(Child("[Console]::WriteLine('{\"jsonrpc\":\"2.0\",\"method\":\"fixture/ready\"}');[System.Threading.Thread]::Sleep(10000)")))
        {
            bridge.Notification += _ => ready.TrySetResult();
            await bridge.StartAsync(); await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var stalled = bridge.CallAsync("blocked_write", new { padding = new string('x', 2 * 1024 * 1024) }, timeout: TimeSpan.FromMilliseconds(650));
            var elapsed = Stopwatch.StartNew();
            Check(await Failure(bridge.CallAsync("queued_write", timeout: TimeSpan.FromMilliseconds(150))) == "rpc_timeout",
                "deadline includes waiting for an occupied write gate");
            Check(elapsed.Elapsed < TimeSpan.FromSeconds(2), "queued write timeout remains bounded");
            Check(await Failure(stalled) == "rpc_timeout", "deadline includes blocked stdin write and flush");
            Check(!bridge.IsRunning, "partial cancelled frame disconnects transport");
            Check(await Failure(bridge.CallAsync("must_not_replay")) == "rpc_not_connected", "incomplete write cannot be reused or replayed");
        }
        await using (var bridge = new WorkflowRpcProcess(Child("$r=[Console]::ReadLine()|ConvertFrom-Json;[Console]::Write((@{jsonrpc='2.0';id=$r.id;result=@{method='incomplete'}}|ConvertTo-Json -Compress))")))
        {
            await bridge.StartAsync();
            Check(await Failure(bridge.CallAsync("incomplete", timeout: TimeSpan.FromSeconds(15))) == "rpc_incomplete_frame",
                "EOF before newline never produces a successful response");
        }
        return count;
    }
}

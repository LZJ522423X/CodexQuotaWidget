using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexQuotaWidget.Core;
using CodexQuotaWidget.Core.ControlCenter;

/// <summary>Offline protocol fixtures only. Synthetic credentials; no sockets, browsers, accounts or model calls.</summary>
internal static class SiwcPhaseOneChecks
{
    static int checks;
    static readonly string root = @"D:\CodexQuotaWidget-GitHub\artifacts\siwc-phase1-20261016\checks-" + Guid.NewGuid().ToString("N");
    static void Check(bool value, string name) { if (!value) throw new InvalidOperationException("SIWC CHECK FAILED: " + name); checks++; }
    static async Task Reject(Func<Task> action, string name, string? code = null)
    {
        try { await action(); } catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or OperationCanceledException or HttpRequestException)
        { Check(code is null || e is SiwcProtocolException p && p.Code == code, name); return; }
        throw new InvalidOperationException("SIWC CHECK DID NOT REJECT: " + name);
    }
    public static async Task<int> RunAsync()
    {
        Directory.CreateDirectory(root);
        await StreamChecks(); await AuthorizationChecks(); await ConnectorChecks(); await LoopChecks(); await LoopFaultChecks();
        File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new { Mode = "OfflineFixtures", Checks = checks,
            Passed = true, RealAccountAuthorizations = 0, RealModelCalls = 0, ProductionDataWrites = 0, Evidence = root }));
        Console.WriteLine("Offline evidence: " + root); return checks;
    }
    static async Task<SiwcTextCompletion> Read(string source, CancellationToken token = default)
    { await using var stream = new ChunkStream(Encoding.UTF8.GetBytes(source)); return await SiwcResponsesReader.ReadAsync(stream, token); }
    internal static string E(int sequence, string type, object fields)
    {
        var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(fields))!;
        values["type"] = JsonSerializer.SerializeToElement(type); values["sequence_number"] = JsonSerializer.SerializeToElement(sequence);
        return "event: " + type + "\r\ndata: " + JsonSerializer.Serialize(values) + "\r\n\r\n";
    }
    static object Response(string id, string text, string status = "completed") => new { id, status,
        output = new[] { new { type = "message", id = "msg_fixture", role = "assistant", status = "completed", content = new[] { new { type = "output_text", text } } } } };
    internal static string StreamText(string text, string id = "resp_fixture") =>
        E(0, "response.created", new { response = new { id } })
        + E(1, "response.output_text.delta", new { item_id = "msg_fixture", output_index = 0, content_index = 0, delta = text })
        + E(2, "response.output_text.done", new { item_id = "msg_fixture", output_index = 0, content_index = 0, text })
        + E(3, "response.completed", new { response = Response(id, text) });
    static async Task StreamChecks()
    {
        var full = StreamText("真实协议夹具🙂\n第二行"); var result = await Read(full);
        Check(result.Text == "真实协议夹具🙂\n第二行" && result.ResponseId == "resp_fixture", "UTF8 one-byte chunks accumulate full body");
        var delta = E(1, "response.output_text.delta", new { item_id = "msg_fixture", output_index = 0, content_index = 0, delta = "hello" });
        var normal = StreamText("hello"); Check((await Read(normal.Replace(delta, delta + delta))).Text == "hello", "identical SSE sequence deduplicated");
        var created = E(0, "response.created", new { response = new { id = "resp_fixture" } });
        await Reject(() => Read(created + delta), "partial stream is not success", "siwc_stream_ended_without_completed");
        await Reject(() => Read(created + "data: [DONE]\n\n"), "DONE alone not terminal", "siwc_done_without_completed");
        await Reject(() => Read(created + E(1, "response.completed", new { response = Response("resp_other", "hello") })), "wrong response identity", "siwc_completed_identity_or_status_mismatch");
        await Reject(() => Read(created + E(1, "response.completed", new { response = Response("resp_fixture", "hello", "in_progress") })), "completed event wrong status", "siwc_completed_identity_or_status_mismatch");
        await Reject(() => Read(normal.Replace(delta, delta + delta.Replace("hello", "changed"))), "conflicting duplicate event", "siwc_event_sequence_conflict");
        await Reject(() => Read(created + E(1, "response.output_text.delta", new { response_id = "resp_other", item_id = "msg_fixture", output_index = 0, content_index = 0, delta = "x" })), "late response reference", "siwc_response_identity_mismatch");
        await Reject(() => Read(created + E(1, "response.incomplete", new { response = new { id = "resp_fixture" } })), "incomplete terminal", "siwc_response_incomplete");
        foreach (var code in new[] { "subscription_sharing_usage_limit_exceeded", "subscription_sharing_usage_unavailable" })
            await Reject(() => Read(created + delta + E(2, "response.failed", new { response = new { id = "resp_fixture", error = new { code } } })), "streaming quota failure", code);
        await Reject(() => Read(created + E(1, "response.output_item.added", new { output_index = 0, item = new { type = "function_call", id = "call_fixture" } })), "tool output rejected", "siwc_unexpected_tool_output");
        await Reject(() => Read(normal.Replace("\"text\":\"hello\"", "\"text\":\"different\"")), "done snapshot conflict", "siwc_text_snapshot_conflict");
        await Reject(() => Read(StreamText("::chatgpt-content-reference{fixture}")), "reference is not body", "siwc_invalid_text_body");
        await Reject(() => Read(StreamText("")), "empty text is not success", "siwc_invalid_text_body");
        await Reject(() => Read(created + E(2, "response.output_text.delta", new { item_id = "msg_fixture", output_index = 0, content_index = 0, delta = "x" }) + delta), "out of order events", "siwc_event_out_of_order");
        using var cancel = new CancellationTokenSource(); cancel.Cancel(); await Reject(() => Read(full, cancel.Token), "user cancellation observed");
        await Reject(() => Read("data: " + new string('x', 270000)), "bounded stream size", "siwc_stream_size_limit");
        Check((await Read(created + E(1, "response.completed", new { response = Response("resp_fixture", "snapshot-only") }))).Text == "snapshot-only", "complete authoritative text snapshot supported");
        Check((await Read(": keepalive\r\n\r\n" + normal)).Text == "hello", "SSE comments accepted");
    }
    static SiwcCredential Credential(bool expired = false, long revision = 0, string[]? scopes = null) => new() {
        Subject = "synthetic-fixture-subject", ClientId = "oaiapp_fixture", HostId = "urn:uuid:" + Guid.Empty,
        AccessToken = "synthetic-access-fixture", RefreshToken = "synthetic-refresh-fixture", IdToken = "synthetic-id-fixture",
        Scopes = scopes ?? SiwcAuthorizationAttempt.Scopes.Split(' '), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(expired ? -1 : 50), Revision = revision };
    static async Task AuthorizationChecks()
    {
        var transport = new FakeProtocol(); var host = "urn:uuid:" + Guid.NewGuid(); var redirect = new Uri("http://127.0.0.1:54321/auth/callback");
        Check(SiwcInteractiveAuthorization.ValidateCallbackRequest("GET /auth/callback?code=synthetic HTTP/1.1\r\nHost: 127.0.0.1:54321\r\n\r\n", redirect)?.Query == "?code=synthetic", "pure loopback request validation");
        foreach (var header in new[] { "GET //elsewhere/auth/callback HTTP/1.1\r\nHost: 127.0.0.1:54321\r\n\r\n",
            "POST /auth/callback HTTP/1.1\r\nHost: 127.0.0.1:54321\r\n\r\n", "GET /auth/callback HTTP/1.1\r\nHost: other.example\r\n\r\n",
            "GET /auth/callback HTTP/1.1\r\nHost: 127.0.0.1:54321\r\nHost: 127.0.0.1:54321\r\n\r\n", "GET /callback HTTP/1.1\r\nHost: 127.0.0.1:54321\r\n\r\n" })
            Check(SiwcInteractiveAuthorization.ValidateCallbackRequest(header, redirect) is null, "loopback rejects host/method/path ambiguity");
        var attempt = new SiwcAuthorizationAttempt(host, redirect);
        var parameters = SiwcAuthorizationAttempt.ParseQuery(attempt.AuthorizationUri.Query);
        Check(parameters["client_id"] == "dynamic_agent_client" && parameters["agent_name_hint"] == "AI Control Center", "first registration parameters");
        Check(parameters["code_challenge_method"] == "S256" && parameters["code_challenge"].Length == 43, "PKCE S256");
        Check(parameters["scope"].Split(' ').Contains("chatgpt.tokens.use.direct") && parameters["resource"] == "https://api.openai.com/v1", "plan scopes and resource");
        Check(new SiwcAuthorizationAttempt(host, redirect).AuthorizationUri != attempt.AuthorizationUri, "fresh state nonce and verifier");
        await Reject(() => attempt.CompleteAsync(new(redirect + "?state=wrong&code=x&client_id=oaiapp_fixture"), transport), "callback state rejected");
        Check(transport.TokenCalls == 0, "wrong state cannot exchange");
        await Reject(() => attempt.CompleteAsync(new(redirect + "?state=" + parameters["state"] + "&state=duplicate&code=x"), transport), "duplicate callback fields rejected");
        using var rsa = RSA.Create(2048); var key = rsa.ExportParameters(false);
        string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var jwks = JsonSerializer.SerializeToElement(new { keys = new[] { new { kty = "RSA", kid = "fixture-key", alg = "RS256", use = "sig", n = B64(key.Modulus!), e = B64(key.Exponent!) } } });
        string Jwt(string client = "oaiapp_fixture", string? nonce = null, long? expiry = null, string subject = "synthetic-fixture-subject", string issuer = "https://auth.openai.com")
        {
            var head = B64(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", kid = "fixture-key" }));
            var payload = B64(JsonSerializer.SerializeToUtf8Bytes(new { iss = issuer, sub = subject, aud = client, exp = expiry ?? DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(), nonce = nonce ?? parameters["nonce"] }));
            var unsigned = head + "." + payload; return unsigned + "." + B64(rsa.SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        }
        transport.Keys = jwks; transport.TokenReply = () => TokenReply(Jwt());
        var credential = await attempt.CompleteAsync(new(redirect + "?state=" + parameters["state"] + "&code=synthetic-code&client_id=oaiapp_fixture"), transport);
        Check(credential.ClientId == "oaiapp_fixture" && credential.HasPlanPermission && credential.Subject == "synthetic-fixture-subject", "signature validated identity and plan scopes");
        Check(transport.LastForm!["client_id"] == "oaiapp_fixture" && transport.LastForm["redirect_uri"] == redirect.AbsoluteUri
            && transport.LastForm.ContainsKey("code_verifier") && !transport.LastForm.ContainsKey("client_secret"), "public client exchange exact redirect");
        await Reject(() => attempt.CompleteAsync(new(redirect + "?state=" + parameters["state"] + "&code=again&client_id=oaiapp_fixture"), transport), "authorization code never reused");
        foreach (var bad in new[] { Jwt(client: "oaiapp_wrong"), Jwt(nonce: "wrong"), Jwt(expiry: 1), Jwt(subject: "other"), Jwt(issuer: "https://invalid.example") })
            await Reject(() => Task.Run(() => SiwcIdToken.Validate(bad, jwks, "oaiapp_fixture", parameters["nonce"], "synthetic-fixture-subject")), "OIDC invalid claim rejected");
        var returning = new SiwcAuthorizationAttempt(host, redirect, "oaiapp_fixture", "synthetic-fixture-subject"); var rq = SiwcAuthorizationAttempt.ParseQuery(returning.AuthorizationUri.Query);
        Check(rq["client_id"] == "oaiapp_fixture" && !rq.ContainsKey("agent_name_hint"), "returning registration retained");
        await Reject(() => returning.CompleteAsync(new(redirect + "?state=" + rq["state"] + "&code=x&client_id=oaiapp_other"), transport), "returning client cannot change");
        var declined = new SiwcAuthorizationAttempt(host, redirect); var dq = SiwcAuthorizationAttempt.ParseQuery(declined.AuthorizationUri.Query); var before = transport.TokenCalls;
        await Reject(() => declined.CompleteAsync(new(redirect + "?state=" + dq["state"] + "&error=access_denied"), transport), "consent denied");
        Check(transport.TokenCalls == before, "consent denied makes no exchange");
        var secureRoot = Path.Combine(root, "protected-store"); var store = new SiwcProtectedStore(secureRoot); var service = new SiwcAuthorizationService(store, transport);
        await store.WithExclusiveAsync(credential.AccountId, (_, _) => Task.FromResult<(SiwcCredential?, bool)>((credential, true)));
        var cipherPath = Path.Combine(secureRoot, "siwc", "credentials", credential.AccountId + ".dpapi");
        var cipher = await File.ReadAllBytesAsync(cipherPath);
        Check(!Encoding.UTF8.GetString(cipher).Contains("synthetic-access-fixture", StringComparison.Ordinal) && cipher.Length > 100, "DPAPI ciphertext contains no plaintext credential");
        Check((await service.GetAccessAsync(credential.AccountId)).Subject == credential.Subject, "current-user DPAPI round trip");
        Check(new DirectoryInfo(Path.GetDirectoryName(cipherPath)!).GetAccessControl().AreAccessRulesProtected, "owner directory inheritance disabled");
        Check(store.GetOrCreateHostId() == new SiwcProtectedStore(secureRoot).GetOrCreateHostId(), "stable per-host ID");
        var expired = Credential(true); var rotationRoot = Path.Combine(root, "rotation"); var rotatingStore = new SiwcProtectedStore(rotationRoot);
        var rotation = new FakeProtocol { TokenReply = () => TokenReply(null) }; var auth1 = new SiwcAuthorizationService(rotatingStore, rotation);
        await rotatingStore.WithExclusiveAsync(expired.AccountId, (_, _) => Task.FromResult<(SiwcCredential?, bool)>((expired, true)));
        var auth2 = new SiwcAuthorizationService(new SiwcProtectedStore(rotationRoot), rotation);
        var accesses = await Task.WhenAll(auth1.GetAccessAsync(expired.AccountId), auth2.GetAccessAsync(expired.AccountId));
        Check(rotation.TokenCalls == 1 && accesses.All(c => c.Revision == 1), "cross-instance refresh rotation serialized under file lease");
        Check(rotation.LastForm!["client_id"] == "oaiapp_fixture" && !rotation.LastForm.ContainsKey("scope"), "refresh retains registration and grant");
        var noPlan = new MemoryStore(Credential(scopes: ["openid"])); var noPlanTransport = new FakeProtocol();
        await Reject(() => new SiwcAuthorizationService(noPlan, noPlanTransport).GetAccessAsync(expired.AccountId), "ID identity alone not plan entitlement");
        Check(noPlanTransport.TokenCalls == 0, "missing plan permission cannot refresh for inference");
        var catalog = await new SiwcModelCatalog(service, transport).ReadAsync(credential.AccountId);
        Check(catalog.AccountId == credential.AccountId && catalog.Models.Single().Slug == "fixture-model", "account-scoped model selection from catalog");
        var failingStore = new MemoryStore(expired); var failing = new FakeProtocol { TokenReply = () => throw new HttpRequestException("synthetic offline fault") };
        await Reject(() => new SiwcAuthorizationService(failingStore, failing).GetAccessAsync(expired.AccountId), "refresh fault blocks");
        Check(ReferenceEquals(failingStore.Value, expired), "failed rotation keeps old token set");
        rotation.RevokeSucceeds = false;
        Check(!await auth1.SignOutAsync(expired.AccountId), "remote revocation unconfirmed reported");
        await rotatingStore.WithExclusiveAsync(expired.AccountId, (c, _) => { Check(c!.SignedOut && c.AccessToken == "" && c.RefreshToken == "" && c.IdToken == "", "logout clears all tokens, retains mapping"); return Task.FromResult<(SiwcCredential?, bool)>((null, true)); });
        await Reject(() => auth2.GetAccessAsync(expired.AccountId), "signout cannot be resurrected by another instance");
        var raceStore = new MemoryStore(Credential(true)); var raceTransport = new FakeProtocol { TokenReply = () => TokenReply(null) };
        var raceAuth = new SiwcAuthorizationService(raceStore, raceTransport);
        var refreshTask = raceAuth.GetAccessAsync(expired.AccountId);
        var logoutTask = raceAuth.SignOutAsync(expired.AccountId);
        await Task.WhenAll(refreshTask, logoutTask);
        Check(raceStore.Value.SignedOut && raceStore.Value.RefreshToken == "", "logout serialized after in-flight refresh cannot revive session");
        var noRefreshYet = new SiwcCredential { Subject = expired.Subject, ClientId = expired.ClientId, HostId = expired.HostId,
            AccessToken = expired.AccessToken, RefreshToken = expired.RefreshToken, Scopes = expired.Scopes, ExpiresAt = expired.ExpiresAt, EarliestRefreshAt = DateTimeOffset.UtcNow.AddHours(1) };
        var refreshGate = new FakeProtocol();
        await Reject(() => new SiwcAuthorizationService(new MemoryStore(noRefreshYet), refreshGate).GetAccessAsync(expired.AccountId), "earliest refresh respected");
        Check(refreshGate.TokenCalls == 0, "no premature refresh request");
        using var network = new SiwcHttpProtocol();
        await Reject(() => network.TokenAsync(new Dictionary<string, string>(), default), "production OAuth network hard gate");
        await Reject(() => network.ResponsesAsync("synthetic", [], default), "production inference network hard gate");
        var opened = false;
        await Reject(() => SiwcInteractiveAuthorization.RunAsync(host, 54321, null, transport, _ => opened = true), "interactive flow phase-one gate");
        Check(!opened && !SiwcPhaseOne.LiveAccountAndInferenceEnabled, "no browser or account authorization initiated");
    }
    static JsonElement TokenReply(string? id) => JsonSerializer.SerializeToElement(new Dictionary<string, object?> {
        ["access_token"] = "synthetic-access-fixture", ["refresh_token"] = "synthetic-refresh-fixture", ["token_type"] = "Bearer",
        ["expires_in"] = 3600, ["scope"] = SiwcAuthorizationAttempt.Scopes,
    }.Concat(id is null ? [] : new Dictionary<string, object?> { ["id_token"] = id }).ToDictionary(p => p.Key, p => p.Value));
    static WorkflowLoopConfiguration Config(string name)
    {
        var project = Path.Combine(root, name + "-project"); Directory.CreateDirectory(project);
        return new() { TaskId = Guid.NewGuid(), GptBackend = WorkflowBackend.SiwcGptReviewer,
            ProjectRoot = project, Goal = "Offline task fixture", AuthorizedWritePaths = ["fixture.txt"], LocalExecutionAuthorized = true,
            Siwc = new() { Enabled = true, AccountId = Credential().AccountId, Model = "fixture-model",
                AdviceSessionId = "siwc-advice-" + Guid.NewGuid().ToString("N"), ReviewSessionId = "siwc-review-" + Guid.NewGuid().ToString("N") } };
    }
    static WorkflowExecutionRequest Request(WorkflowLoopConfiguration c, WorkflowTextRole role = WorkflowTextRole.Advisor) => new(Guid.NewGuid(), WorkflowProvider.ChatGPT, "offline prompt") {
        WorkflowTaskId = c.TaskId, Backend = c.GptBackend, BackendAccountId = c.Siwc.AccountId, TextRole = role,
        AuthorizedProjectRoot = c.ProjectRoot, AuthorizedWritePaths = c.AuthorizedWritePaths,
        ExpectedSessionId = c.Siwc.Session(role), AuthorizationId = WorkflowAuthorization.CreateAuthorizationId(c.TaskId, c.ProjectRoot, c.Goal, c.AuthorizedWritePaths, false) };
    static Task<SiwcQuotaEvidence> Quota(WorkflowLoopConfiguration c) => Task.FromResult(new SiwcQuotaEvidence(c.Siwc.AccountId,
        SiwcQuotaStatus.Available, 90, DateTimeOffset.UtcNow, "SIWC_AccountScoped_Verified"));
    static async Task ConnectorChecks()
    {
        var config = Config("connector"); var protocol = new FakeProtocol(); var store = new MemoryStore(Credential());
        var data = Path.Combine(root, "connector-data");
        await using var connector = new SiwcGptWorkflowConnector(data, config, store, protocol, _ => Quota(config));
        Check(await connector.ConnectAsync(), "synthetic independent SIWC connection");
        var request = Request(config); var response = await connector.ExecuteAsync(request);
        Check(response.Success && response.Message == "offline body" && response.ContinuationId == config.Siwc.AdviceSessionId, "full body plus independent session");
        Check(response.Backend == WorkflowBackend.SiwcGptReviewer && response.TextRole == WorkflowTextRole.Advisor && response.BackendAccountId == config.Siwc.AccountId,
            "backend account role result identity");
        var payload = JsonDocument.Parse(protocol.Bodies[0]).RootElement;
        Check(!payload.GetProperty("store").GetBoolean() && payload.GetProperty("stream").GetBoolean(), "public Responses required flags");
        Check(!payload.TryGetProperty("tools", out _) && !payload.TryGetProperty("previous_response_id", out _) && !payload.TryGetProperty("conversation", out _), "no tools or unsupported conversation continuation");
        Check(payload.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(new[] { "input", "instructions", "model", "store", "stream" }.Order()), "minimal allowed HTTP surface");
        Check(connector.ReadConfirmedResult(request)?.Message == "offline body", "completed transport evidence recoverable without dispatch");
        var duplicate = await connector.ExecuteAsync(request);
        Check(!duplicate.Success && !duplicate.DispatchPrevented && protocol.ResponseCalls == 1, "duplicate claim preserves uncertainty and cannot resend");
        foreach (var wrong in new[] { request with { TaskId = Guid.NewGuid(), AuthorizedProjectRoot = root }, request with { TaskId = Guid.NewGuid(), ExpectedSessionId = config.Siwc.ReviewSessionId },
            request with { TaskId = Guid.NewGuid(), Backend = WorkflowBackend.LegacyDesktop }, request with { TaskId = Guid.NewGuid(), BackendAccountId = new string('A', 64) },
            request with { TaskId = Guid.NewGuid(), StepWritePaths = ["outside.txt"] }, request with { TaskId = Guid.NewGuid(), WorkflowTaskId = Guid.NewGuid() },
            request with { TaskId = Guid.NewGuid(), TextRole = WorkflowTextRole.None }, request with { TaskId = Guid.NewGuid(), Provider = WorkflowProvider.Codex } })
        { var rejected = await connector.ExecuteAsync(wrong); Check(!rejected.Success && rejected.DispatchPrevented, "wrong request identity/project/session/authorization blocked"); }
        Check(protocol.ResponseCalls == 1, "scope rejections send nothing");
        var review = await connector.ExecuteAsync(Request(config, WorkflowTextRole.Reviewer));
        Check(review.Success && review.ContinuationId == config.Siwc.ReviewSessionId && review.ContinuationId != response.ContinuationId, "reviewer session independent of advisor");
        var reviewBody = JsonDocument.Parse(protocol.Bodies[1]).RootElement;
        Check(reviewBody.GetProperty("input").GetArrayLength() == 1, "reviewer does not inherit advisor conversation");
        Check(reviewBody.GetProperty("instructions").GetString()!.Contains(config.Siwc.ReviewerPrompt), "independent reviewer prompt");
        await connector.ExecuteAsync(Request(config));
        Check(JsonDocument.Parse(protocol.Bodies[2]).RootElement.GetProperty("input").GetArrayLength() == 3, "advisor role history included explicitly");
        var calls = protocol.ResponseCalls;
        await using var restored = new SiwcGptWorkflowConnector(data, config, store, protocol, _ => Quota(config)); Check(await restored.ConnectAsync(), "restart reconnect fixture");
        Check(restored.ReadConfirmedResult(request)?.CompletedTurnId == response.CompletedTurnId, "completed evidence survives instance restart");
        Check(!(await restored.ExecuteAsync(request)).Success && protocol.ResponseCalls == calls, "restart no replay");
        var changed = config with { Siwc = config.Siwc with { Model = "different-model" } };
        await using var frozen = new SiwcGptWorkflowConnector(data, changed, store, protocol, _ => Quota(changed)); await frozen.ConnectAsync();
        Check(!(await frozen.ExecuteAsync(Request(changed))).Success && protocol.ResponseCalls == calls, "frozen task cannot change model or prompt binding");
        foreach (var evidence in new[] { SiwcQuotaEvidence.Unknown(config.Siwc.AccountId), new SiwcQuotaEvidence(config.Siwc.AccountId, SiwcQuotaStatus.Insufficient, 0, DateTimeOffset.UtcNow, "SIWC_AccountScoped_Verified"),
            new(config.Siwc.AccountId, SiwcQuotaStatus.Available, 90, DateTimeOffset.UtcNow, "DesktopQuota"), new("wrong-account", SiwcQuotaStatus.Available, 90, DateTimeOffset.UtcNow, "SIWC_AccountScoped_Verified"),
            new(config.Siwc.AccountId, SiwcQuotaStatus.Available, 90, DateTimeOffset.UtcNow.AddMinutes(-5), "SIWC_AccountScoped_Verified"),
            new(config.Siwc.AccountId, SiwcQuotaStatus.Available, double.NaN, DateTimeOffset.UtcNow, "SIWC_AccountScoped_Verified") })
        {
            await using var guarded = new SiwcGptWorkflowConnector(Path.Combine(root, Guid.NewGuid().ToString("N")), config, store, protocol, _ => Task.FromResult(evidence));
            await guarded.ConnectAsync(); var outcome = await guarded.ExecuteAsync(Request(config));
            Check(!outcome.Success && outcome.DispatchPrevented && outcome.DiagnosticCode == "siwc_quota_unknown_or_insufficient", "quota unknown/low/wrong source/account/stale invalid blocks");
        }
        Check(protocol.ResponseCalls == calls, "quota blockers produce zero requests");
        var broken = new FakeProtocol { StreamReply = _ => Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(E(0, "response.created", new { response = new { id = "resp_broken" } })))) };
        var brokenData = Path.Combine(root, "broken"); var brokenRequest = Request(config);
        await using (var partial = new SiwcGptWorkflowConnector(brokenData, config, store, broken, _ => Quota(config)))
        {
            await partial.ConnectAsync(); var outcome = await partial.ExecuteAsync(brokenRequest);
            Check(!outcome.Success && !outcome.DispatchPrevented && outcome.DiagnosticCode == "siwc_stream_ended_without_completed", "partial stream durable uncertain claim");
        }
        await using (var restart = new SiwcGptWorkflowConnector(brokenData, config, store, broken, _ => Quota(config)))
        {
            await restart.ConnectAsync(); Check(!(await restart.ExecuteAsync(brokenRequest)).Success && broken.ResponseCalls == 1, "unknown outcome cannot replay after restart");
            var newId = await restart.ExecuteAsync(Request(config));
            Check(!newId.Success && newId.DispatchPrevented && newId.DiagnosticCode == "siwc_previous_request_unresolved" && broken.ResponseCalls == 1,
                "new request ID cannot bypass prior unresolved stream");
        }
        var denied = new FakeProtocol { StreamReply = _ => throw new HttpRequestException("synthetic 401", null, System.Net.HttpStatusCode.Unauthorized) };
        await using (var expiredAuth = new SiwcGptWorkflowConnector(Path.Combine(root, "http401"), config, store, denied, _ => Quota(config)))
        {
            await expiredAuth.ConnectAsync(); var outcome = await expiredAuth.ExecuteAsync(Request(config));
            Check(!outcome.Success && !outcome.DispatchPrevented && outcome.DiagnosticCode == "siwc_authorization_expired_or_denied" && denied.ResponseCalls == 1,
                "authorization rejected after send pauses without retry or account fallback");
        }
        var blocked = new FakeProtocol { StreamReply = token => Task.Delay(Timeout.Infinite, token).ContinueWith<Stream>(_ => throw new OperationCanceledException(token), token) };
        await using var cancellable = new SiwcGptWorkflowConnector(Path.Combine(root, "cancel"), config, store, blocked, _ => Quota(config)); await cancellable.ConnectAsync();
        var pending = cancellable.ExecuteAsync(Request(config));
        while (blocked.ResponseCalls == 0) await Task.Delay(1);
        Check(!(await cancellable.RequestSafeStopAsync(Guid.NewGuid())).Accepted, "cannot stop another task");
        await cancellable.RequestSafeStopAsync(config.TaskId); var stopped = await pending;
        Check(!stopped.Success && !stopped.DispatchPrevented && stopped.DiagnosticCode == "siwc_cancelled_or_timed_out", "user stop preserves remote uncertainty");
    }
    static async Task LoopChecks()
    {
        var config = Config("loop"); var protocol = new FakeProtocol(); var advice = 0; var review = 0;
        protocol.StreamReply = _ =>
        {
            var body = JsonDocument.Parse(protocol.Bodies[^1]).RootElement;
            var input = body.GetProperty("input"); var prompt = input[input.GetArrayLength() - 1].GetProperty("content").GetString()!;
            string text;
            if (body.GetProperty("instructions").GetString()!.Contains("text-only Advisor", StringComparison.Ordinal))
            {
                var id = Guid.Parse(Regex.Match(prompt, "RequestId=([a-fA-F0-9-]+)").Groups[1].Value);
                var r = Request(config) with { TaskId = id };
                text = WorkflowAdviceProtocol.Format(r, "offline authorized step " + ++advice);
            }
            else text = ++review == 1 ? "ACC_REVIEW: CONTINUE\nSame authorized goal remains incomplete." : "ACC_REVIEW: COMPLETE\nFixture evidence complete.";
            return Task.FromResult<Stream>(new ChunkStream(Encoding.UTF8.GetBytes(StreamText(text, "resp_loop_" + protocol.ResponseCalls))));
        };
        var data = Path.Combine(root, "loop-data"); var codex = new FakeExecutor(); var validator = new FakeValidator();
        var store = new MemoryStore(Credential());
        await using var advisor = new SiwcGptWorkflowConnector(data, config, store, protocol, _ => Quota(config)); await advisor.ConnectAsync();
        var loop = new WorkflowLoopService(data, advisor, codex, validator, _ => Task.FromResult(new QuotaGuardSnapshot(QuotaGuardState.Ready, 90, 90, true, false, "offline fixture only")));
        var result = await loop.RunAsync(config);
        Check(result.Success && loop.State.Phase == WorkflowLoopPhase.Completed && loop.State.CompletedRounds == 2, "product loop two rounds with fake text protocol");
        Check(protocol.ResponseCalls == 4 && codex.Calls == 2 && validator.Calls == 2, "all steps owned by real loop service; providers simulated");
        Check(loop.State.ChatGptThreadId == "" && loop.State.CodexSessionId == "fixture-codex-session", "SIWC and executor identities not Desktop sessions");
        Check(loop.State.Evidence.Count(e => e.Backend == WorkflowBackend.SiwcGptReviewer) == 4, "backend and role persisted in step evidence");
        Check(!await loop.RunAsync(config).ContinueWith(t => t.Result.Success) && protocol.ResponseCalls == 4, "completed task not repeated");
        var restarted = new WorkflowLoopService(data, advisor, codex, validator, _ => Task.FromResult(new QuotaGuardSnapshot(QuotaGuardState.Ready, 90, 90, true, false, "offline")));
        Check(restarted.State.CompletedRounds == 2 && restarted.State.Siwc == config.Siwc, "durable backend state round-trip");
        Check(!WorkflowLoopService.SameTask(restarted.State, config with { GptBackend = WorkflowBackend.LegacyDesktop }), "same TaskId cannot migrate Backend");
        Check(!WorkflowLoopService.SameTask(restarted.State, config with { Siwc = config.Siwc with { ReviewerPrompt = "changed" } }), "role prompt frozen with task authorization");
        var legacy = JsonSerializer.Deserialize<WorkflowLoopConfiguration>("{\"Goal\":\"old\"}")!;
        Check(legacy.GptBackend == WorkflowBackend.LegacyDesktop && !legacy.Siwc.Enabled, "old settings missing new fields keep legacy default");
        var oldState = JsonSerializer.Deserialize<WorkflowLoopState>("{\"Phase\":11}")!;
        Check(oldState.GptBackend == WorkflowBackend.LegacyDesktop && !oldState.Siwc.Enabled, "legacy blocked state not migrated");
        var draft = config with { Siwc = config.Siwc with { AccountId = "", Model = "" } };
        Check(WorkflowLoopService.ValidDraftConfiguration(draft) && !WorkflowLoopService.ValidConfiguration(draft), "unbound experimental draft never executable");
        Check(!WorkflowLoopService.ValidConfiguration(config with { ChatGptThreadId = Guid.NewGuid().ToString() }), "Desktop conversation cannot masquerade as SIWC");
        Check(!WorkflowLoopService.ValidConfiguration(config with { Siwc = config.Siwc with { Enabled = false } }), "experimental explicit opt-in required");
    }
    static Task<QuotaGuardSnapshot> KnownGuard(CancellationToken _) => Task.FromResult(new QuotaGuardSnapshot(QuotaGuardState.Ready, 90, 90, true, false, "offline quota fixture"));
    static async Task LoopFaultChecks()
    {
        foreach (var fault in new[] { "project", "session", "account", "late-task", "backend", "confirmation", "write-scope", "disconnect", "duplicate-turn" })
        {
            var config = Config("fault-" + fault); var data = Path.Combine(root, "fault-" + fault + "-data");
            var provider = new FakeRoleConnector(config, (r, index) =>
            {
                var body = r.TextRole == WorkflowTextRole.Advisor ? WorkflowAdviceProtocol.Format(r, "offline authorized step",
                    fault == "write-scope" ? ["not-authorized.txt"] : null, fault == "confirmation") : "ACC_REVIEW: COMPLETE";
                var result = BoundReply(config, r, body);
                return fault switch {
                    "project" => result with { ProjectRoot = root }, "session" => result with { ContinuationId = "wrong-session" },
                    "account" => result with { BackendAccountId = new string('A', 64) }, "late-task" => result with { WorkflowTaskId = Guid.NewGuid() },
                    "backend" => result with { Backend = WorkflowBackend.LegacyDesktop },
                    "disconnect" => result with { Success = false, Message = "synthetic offline disconnection", CompletedTurnId = "" },
                    "duplicate-turn" => result with { CompletedTurnId = "resp_duplicate" }, _ => result };
            });
            var codex = new FakeExecutor(); var loop = new WorkflowLoopService(data, provider, codex, new FakeValidator(), KnownGuard);
            var result = await loop.RunAsync(config);
            Check(!result.Success && loop.State.Phase == WorkflowLoopPhase.Blocked, "new Backend loop safely blocks " + fault);
            Check(codex.Calls == (fault == "duplicate-turn" ? 1 : 0), "no unauthorized executor dispatch " + fault);
            if (fault == "disconnect")
            {
                var old = File.ReadAllBytes(loop.StatePath);
                var recovered = new WorkflowLoopService(data, provider, codex, new FakeValidator(), KnownGuard);
                var resumed = await recovered.RunAsync(config);
                Check(!resumed.Success && provider.Calls == 1 && recovered.State.CurrentRequestId != Guid.Empty, "disconnect restart preserves request identity and refuses replay");
                Check(old.SequenceEqual(File.ReadAllBytes(loop.StatePath)), "uncertain restart does not overwrite durable progress");
            }
        }
        foreach (var low in new[] { false, true })
        {
            var config = Config("outer-quota-" + low); var provider = new FakeRoleConnector(config, (r, _) => BoundReply(config, r, "unused"));
            var loop = new WorkflowLoopService(Path.Combine(root, "outer-quota-data-" + low), provider, new FakeExecutor(), new FakeValidator(),
                _ => Task.FromResult(new QuotaGuardSnapshot(low ? QuotaGuardState.Protected : QuotaGuardState.Unknown, low ? 0 : null, null, false, false, "offline unavailable")));
            Check(!(await loop.RunAsync(config)).Success && provider.Calls == 0, "global execution quota unknown/insufficient blocks before dispatch");
        }
        var checkpoint = Config("confirmed-crash"); var checkpointData = Path.Combine(root, "confirmed-crash-data");
        var req = Request(checkpoint); var authId = req.AuthorizationId;
        var persisted = new WorkflowLoopState { RunId = checkpoint.TaskId, GptBackend = checkpoint.GptBackend, Siwc = checkpoint.Siwc, Goal = checkpoint.Goal,
            ProjectRoot = checkpoint.ProjectRoot, AuthorizationId = authId, AuthorizedWritePaths = checkpoint.AuthorizedWritePaths,
            Phase = WorkflowLoopPhase.AwaitingAdvice, Checkpoint = WorkflowCheckpoint.Advice, Round = 1, CurrentRequestId = req.RequestId,
            PendingProvider = WorkflowProvider.ChatGPT, NeedsRecoveryConfirmation = true, UpdatedAt = DateTimeOffset.UtcNow };
        var statePath = Path.Combine(checkpointData, "workflow", "loop-state.json"); AtomicStore.Write(statePath, persisted);
        var resultPath = Path.Combine(checkpointData, "workflow", "results", checkpoint.TaskId.ToString("D"), req.RequestId.ToString("D") + ".json");
        AtomicStore.Write(resultPath, new { SchemaVersion = 1, TaskId = checkpoint.TaskId, req.RequestId, Round = 1, Checkpoint = WorkflowCheckpoint.Advice,
            Provider = WorkflowProvider.ChatGPT, ProjectRoot = checkpoint.ProjectRoot, AuthorizationId = authId,
            Result = BoundReply(checkpoint, req, WorkflowAdviceProtocol.Format(req, "already confirmed advice")) });
        var reviewOnly = new FakeRoleConnector(checkpoint, (r, _) => BoundReply(checkpoint, r, "ACC_REVIEW: COMPLETE"));
        var executor = new FakeExecutor(); var recoveredLoop = new WorkflowLoopService(checkpointData, reviewOnly, executor, new FakeValidator(), KnownGuard);
        Check(recoveredLoop.State.CurrentRequestId == Guid.Empty && recoveredLoop.State.Checkpoint == WorkflowCheckpoint.Execution && reviewOnly.Calls == 0,
            "confirmed new Backend journal advances evidence without automatically running");
        Check((await recoveredLoop.RunAsync(checkpoint)).Success && executor.Calls == 1 && reviewOnly.Calls == 1,
            "explicit resume skips confirmed advice and retains independent review");
        var complete = Config("review-complete"); var completeData = Path.Combine(root, "review-complete-data");
        AtomicStore.Write(Path.Combine(completeData, "workflow", "loop-state.json"), persisted with { RunId = complete.TaskId, Goal = complete.Goal, ProjectRoot = complete.ProjectRoot,
            Siwc = complete.Siwc, AuthorizationId = WorkflowAuthorization.CreateAuthorizationId(complete.TaskId, complete.ProjectRoot, complete.Goal, complete.AuthorizedWritePaths, false),
            CurrentRequestId = Guid.Empty, PendingProvider = null, NeedsRecoveryConfirmation = false, Phase = WorkflowLoopPhase.Paused,
            Checkpoint = WorkflowCheckpoint.ReviewReceived, Review = "ACC_REVIEW: COMPLETE", CompletedRounds = 1 });
        var unused = new FakeRoleConnector(complete, (r, _) => BoundReply(complete, r, "unused")); var noExec = new FakeExecutor();
        var alreadyReviewed = new WorkflowLoopService(completeData, unused, noExec, new FakeValidator(), KnownGuard);
        Check((await alreadyReviewed.RunAsync(complete)).Success && unused.Calls == 0 && noExec.Calls == 0, "crash after confirmed review does not redispatch completed stages");
    }
    static WorkflowExecutionResult BoundReply(WorkflowLoopConfiguration config, WorkflowExecutionRequest r, string body) => new(true, body, config.Siwc.Session(r.TextRole)) {
        RequestId = r.RequestId, WorkflowTaskId = r.WorkflowTaskId, ProjectRoot = r.AuthorizedProjectRoot, AuthorizationId = r.AuthorizationId,
        Backend = config.GptBackend, BackendAccountId = config.Siwc.AccountId, TextRole = r.TextRole, CompletedTurnId = "resp_" + r.RequestId.ToString("N") };
    sealed class FakeRoleConnector(WorkflowLoopConfiguration config, Func<WorkflowExecutionRequest, int, WorkflowExecutionResult> reply) : IWorkflowConnector
    {
        public int Calls; public string Id => "offline-siwc-role"; public WorkflowConnectionState ConnectionState => WorkflowConnectionState.Ready; public bool CanExecute => true;
        public WorkflowBackendIdentity BackendIdentity => new(config.GptBackend, config.Siwc.AccountId, "");
        public Task<WorkflowExecutionResult> ExecuteAsync(WorkflowExecutionRequest request, CancellationToken token = default) => Task.FromResult(reply(request, ++Calls));
        public Task<SafeStopResult> RequestSafeStopAsync(Guid id, CancellationToken token = default) => Task.FromResult(new SafeStopResult(true, "offline"));
    }
    sealed class FakeExecutor : IWorkflowConnector
    {
        public int Calls; public string Id => "offline-codex"; public WorkflowConnectionState ConnectionState => WorkflowConnectionState.Ready; public bool CanExecute => true;
        public Task<WorkflowExecutionResult> ExecuteAsync(WorkflowExecutionRequest request, CancellationToken token = default)
        { Calls++; return Task.FromResult(WorkflowTestProtocol.Reply(request, new(true, "offline execution report", "fixture-codex-session"))); }
        public Task<SafeStopResult> RequestSafeStopAsync(Guid id, CancellationToken token = default) => Task.FromResult(new SafeStopResult(true, "offline stop"));
    }
    sealed class FakeValidator : IWorkflowProjectValidator
    { public int Calls; public Task<WorkflowValidationResult> ValidateAsync(string project, CancellationToken token) { Calls++; return Task.FromResult(new WorkflowValidationResult(true, "offline fixed check fixture")); } }
    sealed class MemoryStore(SiwcCredential initial) : ISiwcCredentialStore
    {
        readonly SemaphoreSlim gate = new(1, 1); public SiwcCredential Value { get; private set; } = initial;
        public async Task<T> WithExclusiveAsync<T>(string account, Func<SiwcCredential?, CancellationToken, Task<(SiwcCredential? Replacement, T Result)>> operation, CancellationToken token = default)
        { await gate.WaitAsync(token); try { var result = await operation(Value, token); if (result.Replacement is not null) Value = result.Replacement; return result.Result; } finally { gate.Release(); } }
    }
    sealed class FakeProtocol : ISiwcProtocolTransport
    {
        public int TokenCalls, ResponseCalls; public List<byte[]> Bodies = []; public IReadOnlyDictionary<string, string>? LastForm;
        public JsonElement Keys; public Func<JsonElement>? TokenReply; public Func<CancellationToken, Task<Stream>>? StreamReply; public bool RevokeSucceeds = true;
        public async Task<JsonElement> TokenAsync(IReadOnlyDictionary<string, string> form, CancellationToken token)
        { Interlocked.Increment(ref TokenCalls); LastForm = form; await Task.Delay(20, token); return TokenReply?.Invoke() ?? throw new InvalidOperationException("Offline token reply unconfigured"); }
        public Task<JsonElement> PublicMetadataAsync(bool keys, CancellationToken token) => Task.FromResult(keys ? Keys : JsonSerializer.SerializeToElement(new { revocation_endpoint = "https://auth.openai.com/oauth/revoke" }));
        public Task<bool> RevokeAsync(Uri endpoint, IReadOnlyDictionary<string, string> form, CancellationToken token) => Task.FromResult(RevokeSucceeds);
        public Task<Stream> ResponsesAsync(string accessToken, byte[] body, CancellationToken token)
        { ResponseCalls++; Bodies.Add(body.ToArray()); return StreamReply?.Invoke(token) ?? Task.FromResult<Stream>(new ChunkStream(Encoding.UTF8.GetBytes(StreamText("offline body", "resp_fixture_" + ResponseCalls)))); }
        public Task<JsonElement> ModelsAsync(string accessToken, CancellationToken token) => Task.FromResult(JsonSerializer.SerializeToElement(new { models = new[] { new { slug = "fixture-model", visibility = "list", display_name = "Offline fixture" } } }));
    }
    sealed class ChunkStream(byte[] bytes) : MemoryStream(bytes)
    { public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => base.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], token); }
}

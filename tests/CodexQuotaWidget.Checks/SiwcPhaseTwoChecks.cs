using System.Text;
using System.Text.Json;
using CodexQuotaWidget.Core.ControlCenter;

/// <summary>All responses and credentials here are synthetic. This suite never authorizes an account or sends HTTPS.</summary>
internal static class SiwcPhaseTwoChecks
{
    public static async Task<int> RunAsync()
    {
        var root = @"D:\CodexQuotaWidget-GitHub\artifacts\siwc-phase2-20261016\offline-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(root); int checks = 0;
        void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException("SIWC phase two fixture: " + name); checks++; }
        async Task Reject(Func<Task> action, string name)
        {
            try { await action(); } catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or OperationCanceledException or JsonException) { checks++; return; }
            throw new InvalidOperationException("SIWC phase two fixture did not reject: " + name);
        }
        var credential = new SiwcCredential { Subject = "synthetic-phase2-subject", ClientId = "oaiapp_phase2_fixture", HostId = "urn:uuid:" + Guid.Empty,
            AccessToken = "synthetic-phase2-access", RefreshToken = "synthetic-phase2-refresh", Scopes = ["resource.invoke", "chatgpt.tokens.use.direct"], ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) };
        var store = new Store(credential); var protocol = new Protocol(); var auth = new SiwcAuthorizationService(store, protocol);
        // Exercise the HTTP client's real bounded JSON reader with a synthetic large directory; no network or account use.
        var boundedReader = typeof(SiwcHttpProtocol).GetMethod("ReadBoundedJsonAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var limitField = typeof(SiwcHttpProtocol).GetField("ModelCatalogMaximumBytes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var catalogLimit = (int)limitField.GetRawConstantValue()!;
        Check(new SiwcModelChoice("fixture-model", "Fixture Model").ToString() == "Fixture Model", "selected model label displays its name instead of record internals");
        Task<JsonElement> ReadJson(byte[] bytes, int limit, CancellationToken token = default)
            => (Task<JsonElement>)boundedReader.Invoke(null, new object[] { new MemoryStream(bytes), limit, token })!;
        var largeCatalog = JsonSerializer.SerializeToUtf8Bytes(new { models = new[] { new { slug = "fixture-model", display_name = "Fixture", visibility = "list", base_instructions = new string('x', 300 * 1024) } } });
        Check(largeCatalog.Length > 256 * 1024 && catalogLimit == 8 * 1024 * 1024, "catalog has a separate finite 8MiB limit");
        var parsedLarge = await ReadJson(largeCatalog, catalogLimit);
        Check(parsedLarge.GetProperty("models")[0].GetProperty("slug").GetString() == "fixture-model", "valid catalog exceeding 256KiB is readable");
        await Reject(() => ReadJson(largeCatalog, 256 * 1024), "auth metadata still rejects response over 256KiB");
        await Reject(() => ReadJson(new byte[catalogLimit + 1], catalogLimit), "catalog over 8MiB is still blocked");
        await Reject(() => ReadJson(Encoding.UTF8.GetBytes("{\"models\":[],\"models\":[]}"), catalogLimit), "duplicate model directory fields still blocked");
        using (var canceled = new CancellationTokenSource()) { canceled.Cancel(); await Reject(() => ReadJson(largeCatalog, catalogLimit, canceled.Token), "catalog read honors cancellation"); }
        SiwcSingleRequestApproval Approval(SiwcSingleRequestAcceptance a) => new(a.Ticket.RequestId, credential.AccountId, "fixture-model", a.Ticket.SessionId,
            DateTimeOffset.UtcNow, true, true, true);
        var data = Path.Combine(root, "one-request"); var service = new SiwcSingleRequestAcceptance(data, auth, protocol);
        var approval = Approval(service);
        Check(!service.HasAttempt && service.ReadReport().Status == "NotSent" && protocol.ModelPosts == 0, "opening service never sends or authorizes");
        foreach (var bad in new[] { approval with { UserConfirmedSending = false }, approval with { UnknownQuotaExceptionConfirmed = false },
            approval with { BillingUncertaintyAcknowledged = false }, approval with { RequestId = Guid.NewGuid() }, approval with { SessionId = "desktop-thread" },
            approval with { ConfirmedAt = DateTimeOffset.UtcNow.AddMinutes(-6) } })
            await Reject(() => service.SendOnceAsync(bad), "missing or mismatched explicit confirmations");
        Check(!service.HasAttempt && protocol.ModelPosts == 0 && protocol.ModelCatalogReads == 0, "invalid approval performs no preflight or send");
        await Reject(() => service.SendOnceAsync(approval with { Model = "unavailable-model" }), "model absent in verified account catalog");
        Check(!service.HasAttempt && protocol.ModelPosts == 0, "wrong model not claimed or sent");
        var body = SiwcSingleRequestAcceptance.BuildBody("fixture-model"); using var payload = JsonDocument.Parse(body);
        var properties = payload.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray();
        Check(properties.SequenceEqual(new[] { "input", "instructions", "model", "store", "stream" }.Order()), "pure text body without tools or unsupported API parameters");
        Check(payload.RootElement.GetProperty("input")[0].GetProperty("content").GetString() == SiwcSingleRequestAcceptance.Prompt, "exact user-approved request");
        var result = await service.SendOnceAsync(approval);
        Check(result.Status == "Completed" && result.ExactMatch && result.Body == SiwcSingleRequestAcceptance.ExpectedBody, "synthetic full body exact match");
        Check(result.EvidenceKind == SiwcAcceptanceEvidenceKind.OfflineFixture && result.ResponseCompleted && result.GptRequestReceived == true, "simulated evidence cannot be called live HTTPS");
        Check(result.TextDeltaEvents == 2 && result.Usage?.InputTokens == 17 && result.Usage.TotalTokens == 26, "native complete usage and delta evidence parsed");
        Check(result.QuotaEvidence.StartsWith("Unknown") && result.CreditsConsumed.StartsWith("Unknown"), "token usage does not invent quota or credit balances");
        Check(protocol.ModelPosts == 1 && protocol.ArmCount == 1 && service.HasAttempt, "one fixed request only");
        Check((await service.SendOnceAsync(approval)).ResponseId == result.ResponseId && protocol.ModelPosts == 1, "repeat button cannot resend");
        var restored = new SiwcSingleRequestAcceptance(data, auth, protocol);
        await Reject(() => Task.Run(() => new SiwcSingleRequestAcceptance(data, auth, protocol, SiwcAcceptanceKind.BodyReadRepair)), "new 456 specification cannot overwrite old 789 ticket");
        Check(restored.Ticket == service.Ticket && restored.ReadReport().Body == result.Body, "persistent identity and report survive restart");
        await restored.SendOnceAsync(Approval(restored)); Check(protocol.ModelPosts == 1, "new timestamp after restart cannot permit second request");
        File.Move(Path.Combine(data, "request-claim.json"), Path.Combine(data, "request-claim.fixture-moved"));
        await restored.SendOnceAsync(Approval(restored)); Check(protocol.ModelPosts == 1, "confirmed report still prevents replay if claim is missing");
        foreach (var mode in new[] { "disconnect", "mismatch", "reference", "timeout", "non-sse" })
        {
            var p = new Protocol { Mode = mode }; var a = new SiwcSingleRequestAcceptance(Path.Combine(root, mode), new(store, p), p);
            var r = await a.SendOnceAsync(Approval(a));
            Check(r.Status == (mode == "mismatch" ? "Mismatch" : "Blocked") && !r.ExactMatch, "non-success stops " + mode);
            Check(r.BodyReadable == (mode == "mismatch") && p.ModelPosts == 1, "body must actually be completed " + mode);
            if (mode == "non-sse") Check(r.DiagnosticCode == "siwc_expected_event_stream" && !r.HttpStreamAccepted && !r.ResponseCompleted, "unexpected media type is not a successful model reply");
            var restart = new SiwcSingleRequestAcceptance(Path.Combine(root, mode), new(store, p), p);
            await restart.SendOnceAsync(Approval(restart)); Check(p.ModelPosts == 1, "uncertain outcome cannot replay " + mode);
        }
        var denied = new Protocol(); var noScope = new Store(new SiwcCredential { Subject = credential.Subject, ClientId = credential.ClientId, ExpiresAt = credential.ExpiresAt, Scopes = ["openid"] });
        var unentitled = new SiwcSingleRequestAcceptance(Path.Combine(root, "no-scope"), new(noScope, denied), denied);
        await Reject(() => unentitled.SendOnceAsync(Approval(unentitled)), "identity without package scope");
        Check(denied.ModelPosts == 0 && !unentitled.HasAttempt, "missing scope never infers");
        Check(!SiwcPhaseOne.LiveAccountAndInferenceEnabled, "regular workflow remains phase-one blocked");
        var repairProtocol = new Protocol { Mode = "body-read" };
        var repair = new SiwcSingleRequestAcceptance(Path.Combine(root, "new-body-read"), new(store, repairProtocol), repairProtocol, SiwcAcceptanceKind.BodyReadRepair);
        var repairResult = await repair.SendOnceAsync(Approval(repair));
        Check(repairResult.ExactMatch && repairResult.Body == SiwcSingleRequestAcceptance.BodyReadExpected && repair.Ticket.RequestId != service.Ticket.RequestId, "new fixed 456 request is independent and extractable");
        await new SiwcSingleRequestAcceptance(Path.Combine(root, "new-body-read"), new(store, repairProtocol), repairProtocol, SiwcAcceptanceKind.BodyReadRepair).SendOnceAsync(Approval(repair));
        Check(repairProtocol.ModelPosts == 1, "restarting new acceptance cannot resend");
        var oldOriginalReport = File.ReadAllBytes(service.ReportPath);
        var oldRepairReport = File.ReadAllBytes(repair.ReportPath);
        var verificationData = Path.Combine(root, "new-body-verification");
        var verificationProtocol = new Protocol { Mode = "body-read" };
        var verification = new SiwcSingleRequestAcceptance(verificationData, new(store, verificationProtocol), verificationProtocol, SiwcAcceptanceKind.BodyReadVerification);
        Check(!verification.HasAttempt && verificationProtocol.ModelPosts == 0
            && verification.Ticket.RequestId != service.Ticket.RequestId && verification.Ticket.RequestId != repair.Ticket.RequestId,
            "new verification creates an independent unattempted ID, without reusing either old 789 or 456");
        Check(verification.RequestPrompt == "这是 AI Control Center 正文读取验收，请只回复：第二次连接验证成功456"
            && verification.RequestPrompt == SiwcSingleRequestAcceptance.BodyReadVerificationPrompt
            && verification.RequiredReply == SiwcSingleRequestAcceptance.BodyReadExpected,
            "new verification binds its separately approved exact prompt and 456 answer");
        var verificationBody = SiwcSingleRequestAcceptance.BuildBody("fixture-model", SiwcAcceptanceKind.BodyReadVerification);
        using (var verificationPayload = JsonDocument.Parse(verificationBody))
            Check(verificationPayload.RootElement.GetProperty("input")[0].GetProperty("content").GetString() == verification.RequestPrompt
                && !verificationBody.SequenceEqual(body)
                && !verificationBody.SequenceEqual(SiwcSingleRequestAcceptance.BuildBody("fixture-model", SiwcAcceptanceKind.BodyReadRepair)),
                "verification wire body remains separate from both old accepted request specifications");
        var verificationRestart = new SiwcSingleRequestAcceptance(verificationData, new(store, verificationProtocol), verificationProtocol, SiwcAcceptanceKind.BodyReadVerification);
        Check(verificationRestart.Ticket == verification.Ticket && !verificationRestart.HasAttempt,
            "new verification identity persists across a restart before any attempt");
        await Reject(() => Task.Run(() => new SiwcSingleRequestAcceptance(Path.Combine(root, "new-body-read"), new(store, verificationProtocol), verificationProtocol, SiwcAcceptanceKind.BodyReadVerification)),
            "new verification kind cannot open or repurpose old repair ticket");
        await Reject(() => Task.Run(() => new SiwcSingleRequestAcceptance(verificationData, new(store, verificationProtocol), verificationProtocol, SiwcAcceptanceKind.BodyReadRepair)),
            "old repair kind cannot open new verification ticket");
        await Reject(() => verification.SendOnceAsync(Approval(repair)), "old repair approval cannot send the fresh verification ticket");
        Check(!verification.HasAttempt && verificationProtocol.ModelPosts == 0, "mismatched old approval creates no verification claim or send");
        var verificationResult = await verification.SendOnceAsync(Approval(verification));
        Check(verificationResult is { Status: "Completed", ExactMatch: true, BodyReadable: true, ResponseCompleted: true }
            && verificationResult.Body == SiwcSingleRequestAcceptance.BodyReadExpected
            && verificationResult.RequestId == verification.Ticket.RequestId && verificationResult.SessionId == verification.Ticket.SessionId
            && verificationResult.EvidenceKind == SiwcAcceptanceEvidenceKind.OfflineFixture,
            "new verification completes only with its own synthetic exact body and identity evidence");
        using (var verificationClaim = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(verificationData, "request-claim.json"))))
            Check(verificationClaim.RootElement.GetProperty("RequestId").GetGuid() == verification.Ticket.RequestId
                && verificationClaim.RootElement.GetProperty("SessionId").GetString() == verification.Ticket.SessionId
                && verificationClaim.RootElement.GetProperty("PromptHash").GetString() == verification.Ticket.PromptHash,
                "new verification persisted claim binds its fresh ticket and prompt hash");
        await verification.SendOnceAsync(Approval(verification));
        await verificationRestart.SendOnceAsync(Approval(verificationRestart));
        Check(verificationProtocol.ModelPosts == 1 && verificationProtocol.ArmCount == 1 && verificationRestart.HasAttempt,
            "same instance and restarted verification both refuse to resend after one attempt");
        Check(File.ReadAllBytes(repair.ReportPath).SequenceEqual(oldRepairReport) && File.ReadAllBytes(service.ReportPath).SequenceEqual(oldOriginalReport)
            && restored.Ticket == service.Ticket && repairProtocol.ModelPosts == 1 && protocol.ModelPosts == 1,
            "new verification neither rewrites old fixture results nor replays either old request");
        var campaignStandard = Path.Combine(root, "verification-standard");
        Check(SiwcSingleRequestAcceptance.ResolveVerificationCampaign(campaignStandard, null) == campaignStandard
            && !Directory.Exists(campaignStandard), "null campaign preserves the existing directory without creating metadata");
        foreach (var name in new[] { "round", "a1", "strict-sse-20261017", new string('a', 64) })
        {
            var resolved = SiwcSingleRequestAcceptance.ResolveVerificationCampaign(campaignStandard, name);
            Check(resolved == campaignStandard + "-" + name && !Directory.Exists(resolved),
                "valid lowercase bounded campaign resolves a sibling path without creating a ticket");
        }
        foreach (var name in new[] { "", "1round", "Round", "a_", "a.", "a/b", @"a\b", "../round", "a:b", "a ", "a\0", "中文", new string('a', 65) })
            await Reject(() => Task.Run(() => SiwcSingleRequestAcceptance.ResolveVerificationCampaign(campaignStandard, name)),
                "unsafe campaign name cannot select a new metadata directory");
        await Reject(() => Task.Run(() => SiwcSingleRequestAcceptance.ResolveVerificationCampaign(@"C:\siwc-offline-fixture", null)),
            "default campaign directory must remain on D even when campaign is null");
        await Reject(() => Task.Run(() => SiwcSingleRequestAcceptance.ResolveVerificationCampaign(@"C:\siwc-offline-fixture", "round")),
            "named campaign cannot resolve a directory outside D");
        verification.VerifyDistinctFromHistory([data, Path.Combine(root, "new-body-read"), verificationData]);
        Check(verificationProtocol.ModelPosts == 1 && verification.Ticket == verificationRestart.Ticket,
            "valid sibling history and self are checked read-only without changing the current ticket or dispatching");
        var emptyHistory = Path.Combine(root, "history-empty");
        Directory.CreateDirectory(emptyHistory);
        verification.VerifyDistinctFromHistory([emptyHistory, verificationData]);
        Check(Directory.GetFileSystemEntries(emptyHistory).Length == 0, "empty sibling history is ignored without creating a replacement identity");
        string HistoryTicket(string name, string contents)
        {
            var path = Path.Combine(root, name);
            Directory.CreateDirectory(path); File.WriteAllText(Path.Combine(path, "acceptance-request.json"), contents);
            return path;
        }
        var collisionHistory = HistoryTicket("history-collision", JsonSerializer.Serialize(verification.Ticket));
        await Reject(() => Task.Run(() => verification.VerifyDistinctFromHistory([collisionHistory])),
            "a different history directory cannot reuse the current request ID or derived session");
        var historyTicket = repair.Ticket;
        var invalidHistoryTickets = new[] {
            historyTicket with { SchemaVersion = 2 },
            historyTicket with { RequestId = Guid.Empty },
            historyTicket with { SessionId = "not-the-derived-session" },
            historyTicket with { PromptHash = "short" },
            historyTicket with { PromptHash = new string('g', 64) },
            historyTicket with { PromptHash = null! } };
        for (var i = 0; i < invalidHistoryTickets.Length; i++)
        {
            var invalidHistory = HistoryTicket("history-invalid-" + i, JsonSerializer.Serialize(invalidHistoryTickets[i]));
            await Reject(() => Task.Run(() => verification.VerifyDistinctFromHistory([invalidHistory])),
                "bad historical schema, GUID, derived session or prompt hash blocks the new campaign");
        }
        var validHistoryJson = JsonSerializer.Serialize(historyTicket);
        var duplicateHistory = HistoryTicket("history-duplicate", validHistoryJson.Replace("\"SchemaVersion\":1", "\"SchemaVersion\":1,\"SchemaVersion\":1", StringComparison.Ordinal));
        await Reject(() => Task.Run(() => verification.VerifyDistinctFromHistory([duplicateHistory])),
            "duplicate historical JSON fields cannot be silently accepted");
        var oversizedHistory = HistoryTicket("history-oversized", validHistoryJson + new string(' ', 2049));
        await Reject(() => Task.Run(() => verification.VerifyDistinctFromHistory([oversizedHistory])),
            "historical ticket exceeds the bounded 2048-byte metadata limit");
        var directoryTicketHistory = Path.Combine(root, "history-directory-ticket");
        Directory.CreateDirectory(Path.Combine(directoryTicketHistory, "acceptance-request.json"));
        await Reject(() => Task.Run(() => verification.VerifyDistinctFromHistory([directoryTicketHistory])),
            "directory masquerading as a historical ticket is rejected");
        foreach (var name in new[] { "request-claim.json", "report.json", "report.json.bak", "directory-report" })
        {
            var incompleteHistory = Path.Combine(root, "history-missing-ticket-" + name);
            Directory.CreateDirectory(incompleteHistory);
            if (name == "directory-report") Directory.CreateDirectory(Path.Combine(incompleteHistory, "report.json"));
            else File.WriteAllText(Path.Combine(incompleteHistory, name), "{}");
            await Reject(() => Task.Run(() => verification.VerifyDistinctFromHistory([incompleteHistory])),
                "history with attempted evidence but no ticket is blocked rather than recreated");
        }
        await Reject(() => Task.Run(() => verification.VerifyDistinctFromHistory([Path.Combine(root, "different-parent", "child")])),
            "history outside the current sibling metadata scope is rejected");
        Check(File.ReadAllBytes(repair.ReportPath).SequenceEqual(oldRepairReport)
            && File.ReadAllBytes(service.ReportPath).SequenceEqual(oldOriginalReport)
            && verificationProtocol.ModelPosts == 1 && protocol.ModelPosts == 1 && repairProtocol.ModelPosts == 1,
            "campaign resolution and all history checks leave old reports and model attempt counters unchanged");
        using (var ordinary = new SiwcHttpProtocol())
        { await Reject(() => ordinary.ResponsesAsync("synthetic", body, default), "ordinary HTTP client remains network blocked"); }
        if (SiwcPhaseTwoAccess.IsExperimentalBuild)
        {
            var access = SiwcPhaseTwoAccess.ForUserAcceptanceWindow(); using var official = new SiwcHttpProtocol(access);
            await Reject(() => official.ResponsesAsync("synthetic", body, default), "experimental client cannot send before UI approval");
            await Reject(() => Task.Run(() => official.ArmSingleRequest(approval with { BillingUncertaintyAcknowledged = false }, body)), "billing acknowledgment needed by transport");
            official.ArmSingleRequest(approval, body);
            await Reject(() => Task.Run(() => official.ArmSingleRequest(approval, body)), "capability cannot rearm");
            await Reject(() => official.ResponsesAsync("synthetic", SiwcSingleRequestAcceptance.BuildBody("other-model"), default), "hash mismatch blocked before HTTP");
            var repairAccess = SiwcPhaseTwoAccess.ForUserAcceptanceWindow(SiwcAcceptanceKind.BodyReadRepair);
            using var repairOfficial = new SiwcHttpProtocol(repairAccess);
            await Reject(() => Task.Run(() => repairOfficial.ArmSingleRequest(approval, body)), "new fixed request rejects old 789 body before HTTP");
            repairOfficial.ArmSingleRequest(approval, SiwcSingleRequestAcceptance.BuildBody(approval.Model, SiwcAcceptanceKind.BodyReadRepair));
            await Reject(() => Task.Run(() => repairOfficial.ArmSingleRequest(approval, SiwcSingleRequestAcceptance.BuildBody(approval.Model, SiwcAcceptanceKind.BodyReadRepair))), "new fixed request capability cannot rearm");
            var verificationAccess = SiwcPhaseTwoAccess.ForUserAcceptanceWindow(SiwcAcceptanceKind.BodyReadVerification);
            using var verificationOfficial = new SiwcHttpProtocol(verificationAccess);
            var verificationApproval = Approval(verification);
            await Reject(() => Task.Run(() => verificationOfficial.ArmSingleRequest(verificationApproval, body)),
                "new verification capability rejects old 789 body before any HTTP");
            var oldRepairBody = SiwcSingleRequestAcceptance.BuildBody("fixture-model", SiwcAcceptanceKind.BodyReadRepair);
            await Reject(() => Task.Run(() => verificationOfficial.ArmSingleRequest(verificationApproval, oldRepairBody)),
                "new verification capability rejects old repair body before any HTTP");
            verificationOfficial.ArmSingleRequest(verificationApproval, verificationBody);
            await Reject(() => Task.Run(() => verificationOfficial.ArmSingleRequest(verificationApproval, verificationBody)),
                "new verification capability only arms once");
            await Reject(() => verificationOfficial.ResponsesAsync("synthetic", oldRepairBody, default),
                "armed new verification capability rejects old body hash before creating any HTTP request");
        }
        else
        {
            await Reject(() => Task.Run(() => SiwcPhaseTwoAccess.ForUserAcceptanceWindow()), "ordinary build cannot activate experiment");
            await Reject(() => Task.Run(() => SiwcPhaseTwoAccess.ForUserAcceptanceWindow(SiwcAcceptanceKind.BodyReadVerification)),
                "ordinary build cannot activate the fresh verification capability");
        }
        File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new { Passed = true, Checks = checks, Mode = "OfflineFixtures",
            RealModelCalls = 0, RealAccountAuthorizations = 0, ProductionDataChanged = false, ExperimentalBuild = SiwcPhaseTwoAccess.IsExperimentalBuild }));
        Console.WriteLine("Offline phase-two evidence: " + root); return checks;
    }
    sealed class Store(SiwcCredential credential) : ISiwcCredentialStore
    {
        public async Task<T> WithExclusiveAsync<T>(string account, Func<SiwcCredential?, CancellationToken, Task<(SiwcCredential? Replacement, T Result)>> action, CancellationToken token = default)
        { var result = await action(credential, token); return result.Result; }
    }
    sealed class Protocol : ISiwcSingleRequestTransport
    {
        public SiwcAcceptanceEvidenceKind EvidenceKind => SiwcAcceptanceEvidenceKind.OfflineFixture;
        public int ModelPosts, ModelCatalogReads, ArmCount; public string Mode = "normal";
        public void ArmSingleRequest(SiwcSingleRequestApproval approval, byte[] body) { if (!approval.IsValid) throw new InvalidOperationException(); ArmCount++; }
        public Task<JsonElement> TokenAsync(IReadOnlyDictionary<string, string> form, CancellationToken token) => throw new InvalidOperationException("No auth calls in fixtures");
        public Task<JsonElement> PublicMetadataAsync(bool keys, CancellationToken token) => throw new InvalidOperationException("No discovery calls in fixtures");
        public Task<bool> RevokeAsync(Uri endpoint, IReadOnlyDictionary<string, string> form, CancellationToken token) => throw new InvalidOperationException("No account changes in fixtures");
        public Task<JsonElement> ModelsAsync(string access, CancellationToken token)
        { ModelCatalogReads++; return Task.FromResult(JsonSerializer.SerializeToElement(new { models = new[] { new { visibility = "list", slug = "fixture-model", display_name = "Offline fixture" } } })); }
        public Task<Stream> ResponsesAsync(string access, byte[] body, CancellationToken token)
        {
            ModelPosts++;
            if (Mode == "timeout") throw new OperationCanceledException();
            if (Mode == "non-sse") throw new SiwcProtocolException("siwc_expected_event_stream");
            var text = Mode == "body-read" ? SiwcSingleRequestAcceptance.BodyReadExpected : Mode == "mismatch" ? "wrong simulated body" : Mode == "reference" ? "::chatgpt-content-reference{synthetic}" : SiwcSingleRequestAcceptance.ExpectedBody;
            var created = SiwcPhaseOneChecks.E(0, "response.created", new { response = new { id = "resp_phase2_fixture",
                @object = "response", status = "in_progress", model = "fixture-model", output = Array.Empty<object>() } });
            if (Mode == "disconnect") return Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(created)));
            var source = created
                + SiwcPhaseOneChecks.E(1, "response.output_text.delta", new { item_id = "msg_phase2", output_index = 0, content_index = 0, delta = text[..4] })
                + SiwcPhaseOneChecks.E(2, "response.output_text.delta", new { item_id = "msg_phase2", output_index = 0, content_index = 0, delta = text[4..] })
                + SiwcPhaseOneChecks.E(3, "response.output_text.done", new { item_id = "msg_phase2", output_index = 0, content_index = 0, text })
                + SiwcPhaseOneChecks.E(4, "response.completed", new { response = new { id = "resp_phase2_fixture", @object = "response", status = "completed", model = "fixture-model",
                    usage = new { input_tokens = 17, output_tokens = 9, total_tokens = 26 },
                    output = new[] { new { type = "message", id = "msg_phase2", role = "assistant", status = "completed", content = new[] { new { type = "output_text", text } } } } } });
            return Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(source)));
        }
    }
}

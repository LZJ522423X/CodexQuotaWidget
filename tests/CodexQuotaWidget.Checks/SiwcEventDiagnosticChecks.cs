using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using CodexQuotaWidget.Core.ControlCenter;

/// <summary>Synthetic streams and in-memory HTTP messages only; never reads credentials or sends a request.</summary>
internal static class SiwcEventDiagnosticChecks
{
    const string Model = "fixture-model", Body = "第二次连接验证成功456";
    const string ResponseId = "resp_diagnostic_fixture", MessageId = "msg_diagnostic_fixture";
    static readonly SiwcStrictSseRequirements Requirements = new(Model, Body);

    public static async Task<int> RunAsync()
    {
        int checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("SIWC event diagnostic fixture: " + name);
            checks++;
        }
        async Task<Exception> Reject(string source, string name)
        {
            try { await Read(source); }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException
                or InvalidOperationException or KeyNotFoundException or DecoderFallbackException)
            { checks++; return error; }
            throw new InvalidOperationException("SIWC event diagnostic fixture did not reject: " + name);
        }
        SiwcRejectedEventDiagnostic Diagnostic(Exception error, string name)
        {
            var diagnostic = SiwcResponseEvidence.GetRejection(error);
            Check(diagnostic is not null, name + ": rejection has metadata evidence");
            return diagnostic!;
        }
        void Bound(SiwcRejectedEventDiagnostic diagnostic, string name)
        {
            Check(diagnostic.Fields.Length <= 32 && diagnostic.Fields.All(f => f.Name.Length <= 64
                && Enum.TryParse<JsonValueKind>(f.ValueType, out _)) && (diagnostic.EventType?.Length ?? 0) <= 128,
                name + ": bounded names and JSON value kinds only");
        }
        var normal = Created() + Delta(1, Body) + Done(2, Body) + Completed(3);
        var complete = await Read(normal);
        Check(complete.Text == Body && complete.ResponseId == ResponseId, "supported complete stream still returns exact body");
        Check(SiwcResponseEvidence.GetRejection(new IOException("unrelated synthetic exception")) is null,
            "unrelated exception has no invented rejected-event evidence");
        var observerFailure = new SiwcProtocolException("PRIVATE_OBSERVER_CREDENTIAL_SENTINEL");
        try
        {
            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(normal));
            await SiwcResponsesReader.ReadAsync(stream, observe: _ => throw observerFailure, expectedModel: Model, strict: Requirements);
            throw new InvalidOperationException("Expected the existing observer to fail.");
        }
        catch (SiwcProtocolException error)
        {
            Check(ReferenceEquals(error, observerFailure), "diagnostic attachment preserves the exact original observer exception");
            var observerDetail = Diagnostic(error, "observer failure");
            Check(observerDetail.Reason == "siwc_protocol_rejected"
                && !JsonSerializer.Serialize(observerDetail).Contains("PRIVATE_OBSERVER_CREDENTIAL_SENTINEL", StringComparison.Ordinal),
                "arbitrary exception messages and codes cannot enter the diagnostic reason allowlist");
        }

        const string unknownType = "response.future_event.done";
        var unknown = Event(1, unknownType, new { item_id = MessageId, output_index = 0, content_index = 0, delta = "PRIVATE_BODY_SENTINEL",
            metadata = new { note = "PRIVATE_NESTED_SENTINEL" }, flag = true, count = 12, missing = (object?)null });
        var blocked = await Reject(Created() + unknown + Delta(2, Body) + Done(3, Body) + Completed(4), "unknown before a valid completion");
        Check(blocked is SiwcProtocolException { Code: "siwc_unsupported_event" }, "unknown retains the original protocol rejection code");
        var detail = Diagnostic(blocked, "unknown");
        Check(detail.EventType == unknownType && detail.StreamPosition == 2 && detail.Reason == "siwc_unsupported_event" && detail.Stage == "EventSchema",
            "safe unsupported event has exact discriminator, one-based position, fixed reason and schema phase");
        Check(detail.Fields.Any(f => f.Name == "type" && f.ValueType == "String")
            && detail.Fields.Any(f => f.Name == "sequence_number" && f.ValueType == "Number")
            && detail.Fields.Any(f => f.Name == "delta" && f.ValueType == "String")
            && detail.Fields.Any(f => f.Name == "metadata" && f.ValueType == "Object"),
            "top-level structural field names and JSON kinds survive without their values");
        Bound(detail, "unknown");
        var detailJson = JsonSerializer.Serialize(detail);
        Check(!detailJson.Contains("PRIVATE_BODY_SENTINEL", StringComparison.Ordinal) && !detailJson.Contains("PRIVATE_NESTED_SENTINEL", StringComparison.Ordinal)
            && !detailJson.Contains(MessageId, StringComparison.Ordinal), "body, nested values and item IDs are not persisted in diagnostics");
        Check(blocked is SiwcProtocolException { Code: "siwc_unsupported_event" }
            && SiwcResponseEvidence.GetRejection(blocked)?.Reason == "siwc_unsupported_event",
            "reading diagnostic metadata does not replace the original rejection");

        var unsupportedOfficial = await Reject(Created() + Event(1, "response.reasoning_text.delta", new { item_id = "rs_diagnostic_fixture",
            output_index = 0, content_index = 0, delta = "PRIVATE_REASONING_SENTINEL" }) + Completed(2), "known public but unimplemented reasoning event");
        var unsupportedOfficialDetail = Diagnostic(unsupportedOfficial, "unsupported reasoning");
        Check(unsupportedOfficial is SiwcProtocolException { Code: "siwc_unsupported_event" }
            && unsupportedOfficialDetail.EventType == "response.reasoning_text.delta"
            && !JsonSerializer.Serialize(unsupportedOfficialDetail).Contains("PRIVATE_REASONING_SENTINEL", StringComparison.Ordinal),
            "public unimplemented event remains blocked and never emits reasoning values");

        foreach (var source in new[] {
            Created() + Frame(new { sequence_number = 1, delta = "PRIVATE_INVALID_TYPE_SENTINEL" }),
            Created() + Frame(new { type = 42, sequence_number = 1, delta = "PRIVATE_INVALID_TYPE_SENTINEL" }),
            Created() + Frame(new { type = (string?)null, sequence_number = 1 }),
            Created() + Event(1, "response.output_text.delta", new { item_id = MessageId, output_index = 0, content_index = 0, delta = 42 }),
            Created() + Event(1, "response.output_text.delta", new { item_id = MessageId, output_index = 0, content_index = 0 }) })
        {
            var invalid = await Reject(source, "missing or incorrectly typed discriminator/field");
            var invalidDetail = Diagnostic(invalid, "invalid schema");
            Bound(invalidDetail, "invalid schema");
            Check(!JsonSerializer.Serialize(invalidDetail).Contains("PRIVATE_INVALID_TYPE_SENTINEL", StringComparison.Ordinal),
                "invalid type/schema diagnostics never persist field values");
        }
        var ordering = await Reject(Created() + Delta(2, Body) + Done(1, Body) + Completed(3), "event sequence regression");
        var orderingDetail = Diagnostic(ordering, "order");
        Check(ordering is SiwcProtocolException { Code: "siwc_event_out_of_order" }
            && orderingDetail.StreamPosition == 3 && orderingDetail.Reason == "siwc_event_out_of_order",
            "sequence errors remain blocked and refer to the actual rejected event");
        foreach (var failure in new[] {
            Event(1, "error", new { code = "fixture_error", message = "PRIVATE_ERROR_SENTINEL" }),
            Event(1, "response.failed", new { response = new { id = ResponseId, status = "failed", error = new { code = "fixture_error", message = "PRIVATE_ERROR_SENTINEL" } } }),
            Event(1, "response.incomplete", new { response = new { id = ResponseId, status = "incomplete", incomplete_details = new { reason = "PRIVATE_ERROR_SENTINEL" } } }) })
        {
            var error = await Reject(Created() + failure + Completed(2), "error or incomplete cannot become completion");
            var failureDetail = Diagnostic(error, "terminal failure");
            Check(failureDetail.StreamPosition == 2 && !JsonSerializer.Serialize(failureDetail).Contains("PRIVATE_ERROR_SENTINEL", StringComparison.Ordinal),
                "error and incomplete evidence retains position without remote messages");
        }
        var incomplete = await Reject(Created() + Delta(1, Body) + Done(2, Body), "missing completed");
        Check(incomplete is SiwcProtocolException { Code: "siwc_stream_ended_without_completed" }, "missing completed remains unconfirmed");
        Check(Diagnostic(incomplete, "stream end").Stage == "StreamEnd", "missing completion records only the fixed stream-end phase");
        var malformed = await Reject(Created() + "event: response.future_event.done\ndata: {broken}\n\n", "malformed JSON");
        var malformedDetail = Diagnostic(malformed, "malformed JSON");
        Check(malformedDetail.Stage == "JsonDecoding" && !JsonSerializer.Serialize(malformedDetail).Contains("{broken}", StringComparison.Ordinal),
            "malformed JSON is never stored as raw diagnostic content");

        var secretValues = new Dictionary<string, object?>
        {
            ["delta"] = "PRIVATE_ASSISTANT_TEXT_SENTINEL",
            ["token"] = "PRIVATE_TOKEN_VALUE_SENTINEL",
            ["cookie"] = "PRIVATE_COOKIE_VALUE_SENTINEL",
            ["authorization"] = "PRIVATE_AUTH_HEADER_SENTINEL",
            ["url"] = "https://example.invalid/PRIVATE_URL_VALUE_SENTINEL",
            ["response"] = new { nested = "PRIVATE_NESTED_VALUE_SENTINEL" }
        };
        var secretError = await Reject(Created() + Event(1, unknownType, secretValues), "sensitive values in unsupported schema");
        var secretDetail = Diagnostic(secretError, "sensitive values");
        var serializedSecret = JsonSerializer.Serialize(secretDetail);
        foreach (var value in new[] { "PRIVATE_ASSISTANT_TEXT_SENTINEL", "PRIVATE_TOKEN_VALUE_SENTINEL", "PRIVATE_COOKIE_VALUE_SENTINEL",
            "PRIVATE_AUTH_HEADER_SENTINEL", "PRIVATE_URL_VALUE_SENTINEL", "PRIVATE_NESTED_VALUE_SENTINEL" })
            Check(!serializedSecret.Contains(value, StringComparison.Ordinal), "diagnostic contains no synthetic private value");
        Bound(secretDetail, "sensitive values");

        foreach (var maliciousType in new[] {
            "response.access_token.done", "response.密钥.done", "response.https://example.invalid/PRIVATE_TYPE_SENTINEL",
            "response." + new string('x', 140) + ".done" })
        {
            var malicious = await Reject(Created() + Event(1, maliciousType, new { delta = "PRIVATE_TYPE_VALUE_SENTINEL" }), "unsafe event type");
            var maliciousDetail = Diagnostic(malicious, "unsafe event type");
            Check(maliciousDetail.Redacted && maliciousDetail.EventType != maliciousType
                && !JsonSerializer.Serialize(maliciousDetail).Contains(maliciousType, StringComparison.Ordinal),
                "unsafe type is redacted rather than copied into persistent metadata");
            Bound(maliciousDetail, "unsafe event type");
        }
        foreach (var maliciousField in new[] { "Authorization", "access_token", "Cookie", "秘密字段",
            "https://example.invalid/PRIVATE_FIELD_SENTINEL", new string('y', 65) })
        {
            var fields = new Dictionary<string, object?> { [maliciousField] = "PRIVATE_FIELD_VALUE_SENTINEL" };
            var unsafeField = await Reject(Created() + Event(1, unknownType, fields), "unsafe field name");
            var unsafeFieldDetail = Diagnostic(unsafeField, "unsafe field name");
            var json = JsonSerializer.Serialize(unsafeFieldDetail);
            Check(unsafeFieldDetail.Redacted && !json.Contains(maliciousField, StringComparison.Ordinal)
                && !json.Contains("PRIVATE_FIELD_VALUE_SENTINEL", StringComparison.Ordinal),
                "unsafe field names and every value are redacted");
            Bound(unsafeFieldDetail, "unsafe field name");
        }
        var manyFields = Enumerable.Range(0, 40).ToDictionary(i => "field_" + i, i => (object?)"PRIVATE_MANY_FIELDS_SENTINEL");
        var many = await Reject(Created() + Event(1, unknownType, manyFields), "diagnostic field bound");
        var manyDetail = Diagnostic(many, "many fields");
        Check(manyDetail.Truncated && manyDetail.Fields.Length <= 32, "large event diagnostics stop at 32 top-level fields");
        Bound(manyDetail, "many fields");
        Check(!JsonSerializer.Serialize(manyDetail).Contains("PRIVATE_MANY_FIELDS_SENTINEL", StringComparison.Ordinal),
            "truncated diagnostics contain no event values");

        var method = typeof(SiwcHttpProtocol).GetMethod("ReadResponseAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing in-memory HTTP fixture entry.");
        Task<Stream> ReadHttp(SiwcHttpProtocol protocol, HttpResponseMessage response, SiwcStrictSseRequirements? strict)
            => (Task<Stream>)method.Invoke(protocol, [response, CancellationToken.None, strict])!;
        HttpResponseMessage Http(string source, string? type)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(source)) };
            if (type is not null) response.Content.Headers.ContentType = new(type);
            return response;
        }
        using (var protocol = new SiwcHttpProtocol())
        {
            await using var response = await ReadHttp(protocol, Http(normal, null), Requirements);
            var completion = await SiwcResponsesReader.ReadAsync(response, expectedModel: Model, strict: Requirements);
            Check(completion.Text == Body && protocol.ResponseEvidence is { MissingContentTypeValidated: true, RejectedEvent: null },
                "supported complete missing-type SSE remains successful without a rejection diagnostic");
        }
        using (var protocol = new SiwcHttpProtocol())
        {
            var source = Created() + unknown + Delta(2, Body) + Done(3, Body) + Completed(4);
            Exception? rejected = null;
            try { await using var unexpected = await ReadHttp(protocol, Http(source, null), Requirements); }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException) { rejected = error; }
            Check(rejected is SiwcProtocolException { Code: "siwc_unsupported_event" }, "missing-type transport keeps unknown event blocked");
            Check(protocol.ResponseEvidence is { StatusCode: 200, ContentType: "Missing", MissingContentTypeValidated: false,
                Structure.Kind: "RejectedSseCandidate", RejectedEvent: not null },
                "missing-type HTTP evidence persists the rejected event without setting validated flag");
            var httpDetail = protocol.ResponseEvidence!.RejectedEvent!;
            Check(httpDetail.EventType == unknownType && httpDetail.StreamPosition == 2 && httpDetail.Stage == "EventSchema",
                "transport diagnostic retains safe type, actual position and schema phase");
            var persisted = JsonSerializer.Deserialize<SiwcHttpResponseEvidence>(JsonSerializer.Serialize(protocol.ResponseEvidence));
            Check(persisted?.RejectedEvent?.EventType == unknownType && persisted.RejectedEvent.Reason == "siwc_unsupported_event",
                "sanitized rejected-event evidence survives HTTP metadata persistence");
            var report = new SiwcAcceptanceReport { Status = "Blocked", DiagnosticCode = "siwc_unsupported_event",
                RejectedEvent = httpDetail, HttpResponse = protocol.ResponseEvidence };
            var persistedReport = JsonSerializer.Deserialize<SiwcAcceptanceReport>(JsonSerializer.Serialize(report));
            Check(persistedReport?.RejectedEvent?.StreamPosition == 2 && !persistedReport.BodyReadable && !persistedReport.ResponseCompleted,
                "acceptance reports persist diagnostic evidence while keeping body and completion unconfirmed");
            var reportJson = JsonSerializer.Serialize(persistedReport);
            Check(!reportJson.Contains("PRIVATE_BODY_SENTINEL", StringComparison.Ordinal)
                && !reportJson.Contains("PRIVATE_NESTED_SENTINEL", StringComparison.Ordinal),
                "HTTP/report JSON roundtrip preserves only structural metadata, never original event values");
        }
        using (var protocol = new SiwcHttpProtocol())
        {
            await using var response = await ReadHttp(protocol, Http(Created() + unknown, "text/event-stream"), null);
            Exception? rejected = null;
            try { await SiwcResponsesReader.ReadAsync(response, expectedModel: Model, strict: Requirements); }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException) { rejected = error; }
            Check(rejected is SiwcProtocolException { Code: "siwc_unsupported_event" }
                && SiwcResponseEvidence.GetRejection(rejected)?.EventType == unknownType,
                "declared SSE reader also exposes rejected event without turning it into body success");
        }
        var legacyReport = JsonSerializer.Deserialize<SiwcAcceptanceReport>("{\"SchemaVersion\":1,\"Status\":\"Blocked\",\"DiagnosticCode\":\"siwc_expected_event_stream\"}");
        Check(legacyReport is { RejectedEvent: null, Status: "Blocked", DiagnosticCode: "siwc_expected_event_stream" },
            "old report without rejected-event metadata remains readable with null diagnostic");

        var fixtureDirectory = @"D:\CodexQuotaWidget-GitHub\artifacts\siwc-event-diagnostic-offline-" + Guid.NewGuid().ToString("N");
        var credential = new SiwcCredential { Subject = "synthetic-diagnostic-subject", ClientId = "oaiapp_diagnostic_fixture",
            HostId = "urn:uuid:" + Guid.Empty, AccessToken = "synthetic-diagnostic-access", RefreshToken = "synthetic-diagnostic-refresh",
            Scopes = ["resource.invoke", "chatgpt.tokens.use.direct"], ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) };
        var fixtureStore = new FixtureStore(credential);
        var fixtureTransport = new FixtureTransport();
        var fixtureAuth = new SiwcAuthorizationService(fixtureStore, fixtureTransport);
        var acceptance = new SiwcSingleRequestAcceptance(fixtureDirectory, fixtureAuth, fixtureTransport, SiwcAcceptanceKind.BodyReadVerification);
        SiwcSingleRequestApproval Approval() => new(acceptance.Ticket.RequestId, credential.AccountId, Model, acceptance.Ticket.SessionId,
            DateTimeOffset.UtcNow, true, true, true);
        var acceptanceResult = await acceptance.SendOnceAsync(Approval());
        Check(acceptanceResult is { Status: "Blocked", DiagnosticCode: "siwc_unsupported_event", BodyReadable: false, ResponseCompleted: false,
                ExactMatch: false, RejectedEvent.EventType: unknownType, RejectedEvent.StreamPosition: 2 }
            && acceptanceResult.EvidenceKind == SiwcAcceptanceEvidenceKind.OfflineFixture,
            "product single-request wiring preserves unsupported-event rejection and metadata without fabricating body completion");
        var saved = acceptance.ReadReport();
        Check(saved.RejectedEvent?.EventType == unknownType && saved.RejectedEvent.Reason == "siwc_unsupported_event"
            && !File.ReadAllText(acceptance.ReportPath).Contains("PRIVATE_TRANSPORT_VALUE_SENTINEL", StringComparison.Ordinal),
            "real local report persistence stores only safe rejected-event metadata from the synthetic transport");
        await acceptance.SendOnceAsync(Approval());
        var restartedAcceptance = new SiwcSingleRequestAcceptance(fixtureDirectory, fixtureAuth, fixtureTransport, SiwcAcceptanceKind.BodyReadVerification);
        await restartedAcceptance.SendOnceAsync(Approval());
        Check(fixtureTransport.ModelPosts == 1 && fixtureTransport.ArmCount == 1 && restartedAcceptance.HasAttempt,
            "diagnostic handling never permits resend on repeat or process restart");
        Console.WriteLine("PASS SIWC rejected-event diagnostic OFFLINE checks: " + checks + "; real account/model requests: 0");
        return checks;
    }

    static async Task<SiwcTextCompletion> Read(string source)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(source));
        return await SiwcResponsesReader.ReadAsync(stream, expectedModel: Model, strict: Requirements);
    }
    static string Created() => Event(0, "response.created", new { response = new { id = ResponseId, @object = "response",
        status = "in_progress", model = Model, output = Array.Empty<object>() } });
    static string Delta(int sequence, string text) => Event(sequence, "response.output_text.delta",
        new { item_id = MessageId, output_index = 0, content_index = 0, delta = text });
    static string Done(int sequence, string text) => Event(sequence, "response.output_text.done",
        new { item_id = MessageId, output_index = 0, content_index = 0, text });
    static string Completed(int sequence) => Event(sequence, "response.completed", new { response = new { id = ResponseId,
        @object = "response", status = "completed", model = Model, error = (object?)null, incomplete_details = (object?)null,
        output = new[] { new { id = MessageId, type = "message", role = "assistant", status = "completed",
            content = new[] { new { type = "output_text", text = Body, annotations = Array.Empty<object>() } } } } } });
    static string Event(int sequence, string type, object fields)
    {
        var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(fields))!;
        values["type"] = JsonSerializer.SerializeToElement(type);
        values["sequence_number"] = JsonSerializer.SerializeToElement(sequence);
        return Frame(values, type);
    }
    static string Frame(object fields, string? eventName = null)
        => (eventName is null ? "" : "event: " + eventName + "\n") + "data: " + JsonSerializer.Serialize(fields) + "\n\n";

    sealed class FixtureStore(SiwcCredential credential) : ISiwcCredentialStore
    {
        public async Task<T> WithExclusiveAsync<T>(string account,
            Func<SiwcCredential?, CancellationToken, Task<(SiwcCredential? Replacement, T Result)>> action, CancellationToken token = default)
            => (await action(credential, token)).Result;
    }
    sealed class FixtureTransport : ISiwcSingleRequestTransport
    {
        public SiwcAcceptanceEvidenceKind EvidenceKind => SiwcAcceptanceEvidenceKind.OfflineFixture;
        public int ModelPosts, ArmCount;
        public void ArmSingleRequest(SiwcSingleRequestApproval approval, byte[] body)
        {
            if (!approval.IsValid || !body.SequenceEqual(SiwcSingleRequestAcceptance.BuildBody(Model, SiwcAcceptanceKind.BodyReadVerification)))
                throw new InvalidOperationException("Synthetic acceptance body mismatch.");
            ArmCount++;
        }
        public Task<JsonElement> ModelsAsync(string accessToken, CancellationToken token)
            => Task.FromResult(JsonSerializer.SerializeToElement(new { models = new[] { new { visibility = "list", slug = Model, display_name = "Offline fixture" } } }));
        public Task<Stream> ResponsesAsync(string accessToken, byte[] body, CancellationToken token)
        {
            ModelPosts++;
            var source = Created() + Event(1, "response.future_event.done", new { delta = "PRIVATE_TRANSPORT_VALUE_SENTINEL" })
                + Delta(2, Body) + Done(3, Body) + Completed(4);
            return Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(source)));
        }
        public Task<JsonElement> TokenAsync(IReadOnlyDictionary<string, string> form, CancellationToken token)
            => throw new InvalidOperationException("No authentication requests in offline fixtures.");
        public Task<JsonElement> PublicMetadataAsync(bool keys, CancellationToken token)
            => throw new InvalidOperationException("No discovery requests in offline fixtures.");
        public Task<bool> RevokeAsync(Uri endpoint, IReadOnlyDictionary<string, string> form, CancellationToken token)
            => throw new InvalidOperationException("No account changes in offline fixtures.");
    }
}

using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using CodexQuotaWidget.Core.ControlCenter;

/// <summary>Only bounded in-memory HTTP fixtures; no account access, credentials, HTTP requests or old task files.</summary>
internal static class SiwcMissingContentTypeChecks
{
    const string Model = "fixture-model", ExpectedText = "第二次连接验证成功456";
    const string ResponseId = "resp_missing_type_fixture", MessageId = "msg_missing_type_fixture";

    public static async Task<int> RunAsync()
    {
        int checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("SIWC Missing Content-Type fixture: " + name);
            checks++;
        }

        var requirements = new SiwcStrictSseRequirements(Model, ExpectedText);
        var method = typeof(SiwcHttpProtocol).GetMethod("ReadResponseAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing HTTP response test entry.");
        Task<Stream> Read(SiwcHttpProtocol protocol, HttpResponseMessage response, SiwcStrictSseRequirements? strict)
            => (Task<Stream>)method.Invoke(protocol, [response, CancellationToken.None, strict])!;
        HttpResponseMessage Response(byte[] bytes, string? mediaType = null, HttpStatusCode status = HttpStatusCode.OK)
        {
            var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(bytes) };
            if (mediaType is not null) response.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(mediaType);
            return response;
        }
        async Task Accept(string name, string source, int expectedDeltas = 1)
        {
            using var protocol = new SiwcHttpProtocol();
            var bytes = Encoding.UTF8.GetBytes(source);
            await using var replay = await Read(protocol, Response(bytes), requirements);
            Check(protocol.ResponseEvidence is { StatusCode: 200, ContentType: "Missing", MissingContentTypeValidated: true }, name + ": only validated missing-type stream is admitted");
            using var buffered = new MemoryStream();
            await replay.CopyToAsync(buffered);
            Check(buffered.ToArray().SequenceEqual(bytes), name + ": validated bytes are replayed unchanged");
            buffered.Position = 0;
            var completion = await SiwcResponsesReader.ReadAsync(buffered, expectedModel: Model, strict: requirements);
            Check(completion.Text == ExpectedText && completion.ResponseId == ResponseId && completion.TextDeltaEvents == expectedDeltas,
                name + ": exact complete Chinese 456 body, response identity and text events");
        }
        async Task Reject(string name, byte[] bytes, string? mediaType = null, HttpStatusCode status = HttpStatusCode.OK,
            SiwcStrictSseRequirements? strict = null, bool omitRequirements = false)
        {
            using var protocol = new SiwcHttpProtocol();
            bool rejected = false;
            try
            {
                await using var unexpected = await Read(protocol, Response(bytes, mediaType, status), omitRequirements ? null : strict ?? requirements);
            }
            catch (Exception error) when (error is SiwcProtocolException or InvalidDataException or JsonException
                or DecoderFallbackException or HttpRequestException or EndOfStreamException)
            { rejected = true; }
            Check(rejected, name + ": Blocked before any successful replay");
            Check(protocol.ResponseEvidence?.MissingContentTypeValidated != true, name + ": never records validated body evidence");
        }
        Task RejectText(string name, string source, string? mediaType = null, HttpStatusCode status = HttpStatusCode.OK,
            SiwcStrictSseRequirements? strict = null, bool omitRequirements = false)
            => Reject(name, Encoding.UTF8.GetBytes(source), mediaType, status, strict, omitRequirements);

        var normal = TextStream(ExpectedText);
        await Accept("HTTP 200, missing type and complete public Responses schema", normal);
        await Accept("CRLF framing", normal.Replace("\n", "\r\n", StringComparison.Ordinal));
        await Accept("CR framing", normal.Replace('\n', '\r'));
        await Accept("UTF-8 BOM before first SSE field", "\uFEFF" + normal);
        await Accept("standard SSE comments, id and retry fields", ": keepalive\nid: offline-fixture\nretry: 1000\n\n" + normal);
        await Accept("standard id field may precede event/data", "id: offline-fixture\n\n" + normal);
        await Accept("standard retry field may precede event/data", "retry: 1000\n\n" + normal);
        await Accept("multiline data JSON", PrettyStream());
        await Accept("standard data-only frames use JSON event discriminators", normal.Replace("event: response.created\n", "", StringComparison.Ordinal)
            .Replace("event: response.output_text.delta\n", "", StringComparison.Ordinal)
            .Replace("event: response.output_text.done\n", "", StringComparison.Ordinal)
            .Replace("event: response.completed\n", "", StringComparison.Ordinal));

        var first = ExpectedText[..4]; var second = ExpectedText[4..8]; var last = ExpectedText[8..];
        var split = Created() + Delta(1, first) + Delta(2, second) + Delta(3, last) + Done(4, ExpectedText) + Completed(5, ExpectedText);
        await Accept("three text delta fragments form exactly 456", split, 3);

        var metadata = Created()
            + Event(1, "response.in_progress", new { response = new { id = ResponseId, @object = "response", status = "in_progress", model = Model, output = Array.Empty<object>() } })
            + Event(2, "response.output_item.added", new { output_index = 0, item = new { type = "message", id = MessageId, role = "assistant", status = "in_progress", content = Array.Empty<object>() } })
            + Event(3, "response.content_part.added", new { item_id = MessageId, output_index = 0, content_index = 0, part = new { type = "output_text", text = "", annotations = Array.Empty<object>() } })
            + Delta(4, ExpectedText) + Done(5, ExpectedText)
            + Event(6, "response.output_text.annotation.added", new { item_id = MessageId, output_index = 0, content_index = 0, annotation_index = 0,
                annotation = new { type = "url_citation", start_index = 0, end_index = 1, title = "fixture metadata", url = "https://example.invalid/offline" } })
            + Event(7, "response.content_part.done", new { item_id = MessageId, output_index = 0, content_index = 0, part = new { type = "output_text", text = ExpectedText, annotations = Array.Empty<object>() } })
            + Completed(8, ExpectedText);
        await Accept("supported metadata never becomes assistant body", metadata);

        var reasoning = Created()
            + Event(1, "response.output_item.added", new { output_index = 0, item = new { id = "rs_missing_fixture", type = "reasoning", summary = Array.Empty<object>() } })
            + Event(2, "response.reasoning_summary_text.delta", new { item_id = "rs_missing_fixture", output_index = 0, summary_index = 0, delta = "not the body" })
            + Delta(3, ExpectedText, outputIndex: 1) + Done(4, ExpectedText, outputIndex: 1)
            + Event(5, "response.completed", new { response = new { id = ResponseId, @object = "response", status = "completed", model = Model, error = (object?)null,
                incomplete_details = (object?)null, output = new object[] { new { id = "rs_missing_fixture", type = "reasoning", summary = Array.Empty<object>() }, Message(ExpectedText) } } });
        await Accept("reasoning never enters assistant body", reasoning);

        await RejectText("SSE-looking prefix only", "event: response.created\ndata: ");
        await RejectText("SSE without a terminating empty line", normal[..^1]);
        await RejectText("event field mismatches JSON type", normal.Replace("event: response.created", "event: response.completed", StringComparison.Ordinal));
        await RejectText("duplicate event field is ambiguous", normal.Replace("event: response.created\n", "event: response.created\nevent: response.created\n", StringComparison.Ordinal));
        await RejectText("SSE data is invalid JSON", "event: response.created\ndata: {\"type\":broken}\n\n");
        await RejectText("JSON is not SSE", "{\"type\":\"response.completed\",\"text\":\"" + ExpectedText + "\"}");
        await RejectText("empty body", "");
        await RejectText("only SSE comments", ": keepalive\n\n");
        await RejectText("missing response.created", Delta(1, ExpectedText) + Done(2, ExpectedText) + Completed(3, ExpectedText));
        await RejectText("in_progress cannot replace created", Created(type: "response.in_progress") + Delta(1, ExpectedText) + Done(2, ExpectedText) + Completed(3, ExpectedText));
        await RejectText("no completed event", Created() + Delta(1, ExpectedText) + Done(2, ExpectedText));
        await RejectText("transport DONE cannot replace completed", Created() + Delta(1, ExpectedText) + Done(2, ExpectedText) + "data: [DONE]\n\n");
        await RejectText("non-JSON DONE after completed is not a supported Responses event", normal + "data: [DONE]\n\n");
        await RejectText("text after completed content part is rejected", Created()
            + Event(1, "response.content_part.done", new { output_index = 0, content_index = 0, item_id = MessageId, part = new { type = "output_text", text = ExpectedText } })
            + Delta(2, ExpectedText) + Done(3, ExpectedText) + Completed(4, ExpectedText));
        await RejectText("text after completed item is rejected", Created() + Event(1, "response.output_item.done", new { output_index = 0, item = Message(ExpectedText) })
            + Delta(2, ExpectedText) + Done(3, ExpectedText) + Completed(4, ExpectedText));
        await RejectText("completed envelope has non-completed status", Created() + Delta(1, ExpectedText) + Done(2, ExpectedText) + Completed(3, ExpectedText, status: "in_progress"));
        await RejectText("completed contains error", Created() + Delta(1, ExpectedText) + Done(2, ExpectedText) + Completed(3, ExpectedText, error: new { code = "fixture_error" }));
        await RejectText("completed contains incomplete_details", Created() + Delta(1, ExpectedText) + Done(2, ExpectedText) + Completed(3, ExpectedText, incomplete: new { reason = "max_output_tokens" }));
        await RejectText("top-level error event", Created() + Event(1, "error", new { code = "fixture_error", message = "offline failure" }));
        await RejectText("response.failed event", Created() + Event(1, "response.failed", new { response = new { id = ResponseId, status = "failed", error = new { code = "fixture_error" } } }));
        await RejectText("response.incomplete event", Created() + Event(1, "response.incomplete", new { response = new { id = ResponseId, status = "incomplete", incomplete_details = new { reason = "max_output_tokens" } } }));
        await RejectText("empty text", TextStream(""));
        await RejectText("whitespace-only text", TextStream(" \r\n\t"));
        await RejectText("content reference is not plain body", TextStream("::chatgpt-content-reference{fixture_reference}"));
        await RejectText("nonmatching fixed answer", TextStream("第二次连接验证成功455"));
        await RejectText("missing created response ID", Created(omitId: true) + Delta(1, ExpectedText) + Done(2, ExpectedText) + Completed(3, ExpectedText));
        await RejectText("missing completed response ID", Created() + Delta(1, ExpectedText) + Done(2, ExpectedText) + Completed(3, ExpectedText, omitId: true));
        await RejectText("conflicting response ID", Created() + Delta(1, ExpectedText) + Done(2, ExpectedText) + Completed(3, ExpectedText, id: "resp_other_fixture"));
        await RejectText("delta carries conflicting response ID", Created() + Event(1, "response.output_text.delta", new { response_id = "resp_other_fixture", item_id = MessageId, output_index = 0, content_index = 0, delta = ExpectedText }) + Done(2, ExpectedText) + Completed(3, ExpectedText));
        await RejectText("missing created model", Created(omitModel: true) + Delta(1, ExpectedText) + Done(2, ExpectedText) + Completed(3, ExpectedText));
        await RejectText("created model does not match request", Created(model: "other-model") + Delta(1, ExpectedText) + Done(2, ExpectedText) + Completed(3, ExpectedText));
        await RejectText("missing completed model", Created() + Delta(1, ExpectedText) + Done(2, ExpectedText) + Completed(3, ExpectedText, omitModel: true));
        await RejectText("completed model does not match request", Created() + Delta(1, ExpectedText) + Done(2, ExpectedText) + Completed(3, ExpectedText, model: "other-model"));
        await RejectText("created object is not a response", Created(objectKind: "other") + Delta(1, ExpectedText) + Done(2, ExpectedText) + Completed(3, ExpectedText));
        await RejectText("completed object is not a response", Created() + Delta(1, ExpectedText) + Done(2, ExpectedText) + Completed(3, ExpectedText, objectKind: "other"));
        foreach (var type in new[] { "response.created", "response.in_progress" })
        {
            var sequence = type == "response.created" ? 0 : 1;
            var prefix = type == "response.created" ? "" : Created();
            await RejectText(type + " output contains a number instead of a typed item", prefix
                + Event(sequence, type, new { response = new { id = ResponseId, @object = "response", status = "in_progress", model = Model, output = new[] { 42 } } })
                + Delta(sequence + 1, ExpectedText) + Done(sequence + 2, ExpectedText) + Completed(sequence + 3, ExpectedText));
        }
        var nullFinalPart = Created() + Delta(1, ExpectedText) + Done(2, ExpectedText)
            + Event(3, "response.output_text.done", new { item_id = MessageId, output_index = 0, content_index = 1, text = "", logprobs = Array.Empty<object>() })
            + Event(4, "response.completed", new { response = new { id = ResponseId, @object = "response", status = "completed", model = Model,
                error = (object?)null, incomplete_details = (object?)null,
                output = new[] { new { id = MessageId, type = "message", role = "assistant", status = "completed", content = new[] {
                    new { type = "output_text", text = (string?)ExpectedText, annotations = Array.Empty<object>(), logprobs = Array.Empty<object>() },
                    new { type = "output_text", text = (string?)null, annotations = Array.Empty<object>(), logprobs = Array.Empty<object>() } } } } } });
        await RejectText("null final text must not normalize into an empty matching part", nullFinalPart);
        await RejectText("final snapshot without text events", Created() + Completed(1, ExpectedText));
        await RejectText("delta and done snapshot conflict", Created() + Delta(1, ExpectedText) + Done(2, "different") + Completed(3, ExpectedText));
        await RejectText("completed snapshot conflicts with text events", Created() + Delta(1, ExpectedText) + Done(2, ExpectedText) + Completed(3, "different"));
        await RejectText("out-of-order sequence", Created() + Delta(2, ExpectedText) + Done(1, ExpectedText) + Completed(3, ExpectedText));
        await RejectText("duplicate sequence even when identical", Created() + Delta(1, ExpectedText) + Delta(1, ExpectedText) + Done(2, ExpectedText) + Completed(3, ExpectedText));
        await RejectText("duplicate created authority", Created() + Created(1) + Delta(2, ExpectedText) + Done(3, ExpectedText) + Completed(4, ExpectedText));
        await RejectText("duplicate done authority", Created() + Delta(1, ExpectedText) + Done(2, ExpectedText) + Done(3, ExpectedText) + Completed(4, ExpectedText));
        await RejectText("duplicate completed authority", normal + Completed(4, ExpectedText));
        await RejectText("conflicting completed authority", normal + Completed(4, "different"));
        await RejectText("late delta after done", Created() + Delta(1, ExpectedText) + Done(2, ExpectedText) + Delta(3, "late") + Completed(4, ExpectedText));
        await RejectText("event after terminal completion", normal + Event(4, "response.in_progress", new { response = new { id = ResponseId } }));
        await RejectText("malformed trailing frame after valid completion", normal + "event: response.output_text.delta\ndata: {broken}\n\n");
        await RejectText("unterminated trailing data after valid completion", normal + "data: {\"type\":");
        await RejectText("arbitrary trailing text after valid completion", normal + "not SSE\n\n");
        await RejectText("unsupported event schema", Created() + Event(1, "response.unknown_fixture", new { text = ExpectedText }) + Delta(2, ExpectedText) + Done(3, ExpectedText) + Completed(4, ExpectedText));
        await RejectText("duplicate JSON property", normal.Replace("\"sequence_number\":0", "\"sequence_number\":0,\"sequence_number\":0", StringComparison.Ordinal));
        await RejectText("missing sequence field", normal.Replace(",\"sequence_number\":0", "", StringComparison.Ordinal));

        var invalidUtf8 = Encoding.UTF8.GetBytes(Created() + "event: response.output_text.delta\ndata: ")
            .Concat(new byte[] { 0xC3, 0x28 }).Concat(Encoding.UTF8.GetBytes("\n\n" + Done(2, ExpectedText) + Completed(3, ExpectedText))).ToArray();
        await Reject("invalid UTF-8", invalidUtf8);
        await Reject("invalid UTF-8 after completion is still rejected", Encoding.UTF8.GetBytes(normal).Concat(new byte[] { 0xFF }).ToArray());
        await RejectText("bounded full validation rejects more than 256KiB", Created() + ": " + new string('x', 256 * 1024)
            + "\n\n" + Delta(1, ExpectedText) + Done(2, ExpectedText) + Completed(3, ExpectedText));
        await RejectText("missing type without explicit fixed requirements", normal, omitRequirements: true);
        await RejectText("empty expected model cannot arm candidate path", normal, strict: new("", ExpectedText));
        await RejectText("empty expected body cannot arm candidate path", normal, strict: new(Model, ""));
        await RejectText("model requirement mismatch", normal, strict: new("other-model", ExpectedText));
        foreach (var type in new[] { "application/json", "text/html", "text/plain", "application/octet-stream" })
            await RejectText("explicit " + type + " never uses missing-type fallback", normal, mediaType: type);
        await RejectText("HTTP 201 cannot use missing-type fallback", normal, status: HttpStatusCode.Created);
        await RejectText("HTTP 401 remains failure", normal, status: HttpStatusCode.Unauthorized);

        // Only the public metadata envelope's explicit no-action subset is acceptable.
        var noActionMetadata = new object[] { new { }, new { openai_verification_recommendation = Array.Empty<object>() } };
        for (var i = 0; i < noActionMetadata.Length; i++)
        {
            var value = noActionMetadata[i];
            await Accept("no-action metadata before text variant " + i,
                Created() + Metadata(1, value) + Delta(2, ExpectedText) + Done(3, ExpectedText) + Completed(4, ExpectedText));
            await Accept("no-action metadata between text deltas variant " + i,
                Created() + Delta(1, first) + Metadata(2, value) + Delta(3, second + last) + Done(4, ExpectedText) + Completed(5, ExpectedText), 2);
            await Accept("no-action metadata after text variant " + i,
                Created() + Delta(1, ExpectedText) + Done(2, ExpectedText) + Metadata(3, value) + Completed(4, ExpectedText));
        }
        await Accept("multiple distinct-sequence no-action metadata events do not alter text",
            Created() + Metadata(1, noActionMetadata[0]) + Metadata(2, noActionMetadata[1])
            + Delta(3, ExpectedText) + Metadata(4, noActionMetadata[0]) + Done(5, ExpectedText)
            + Metadata(6, noActionMetadata[1]) + Completed(7, ExpectedText));
        foreach (var badId in new object?[] { null, "", 42, "desktop-session", "resp_conflicting_fixture" })
            await RejectText("metadata requires the bound nonconflicting response ID",
                Created() + Event(1, "response.metadata", new { response_id = badId, metadata = new { } })
                + Delta(2, ExpectedText) + Done(3, ExpectedText) + Completed(4, ExpectedText));
        await RejectText("metadata requires response_id to be present",
            Created() + Event(1, "response.metadata", new { metadata = new { } }) + Delta(2, ExpectedText) + Done(3, ExpectedText) + Completed(4, ExpectedText));
        await RejectText("metadata before created cannot establish response identity",
            Metadata(0, noActionMetadata[0]) + Created(1) + Delta(2, ExpectedText) + Done(3, ExpectedText) + Completed(4, ExpectedText));
        await RejectText("metadata after completed is a late event", normal + Metadata(4, noActionMetadata[0]));
        var metadataEvent = Metadata(1, noActionMetadata[0]);
        await RejectText("metadata missing sequence remains blocked",
            Created() + metadataEvent.Replace(",\"sequence_number\":1", "", StringComparison.Ordinal)
            + Delta(2, ExpectedText) + Done(3, ExpectedText) + Completed(4, ExpectedText));
        await RejectText("metadata sequence must be numeric",
            Created() + metadataEvent.Replace("\"sequence_number\":1", "\"sequence_number\":\"one\"", StringComparison.Ordinal)
            + Delta(2, ExpectedText) + Done(3, ExpectedText) + Completed(4, ExpectedText));
        await RejectText("negative metadata sequence remains blocked",
            Created() + Metadata(-1, noActionMetadata[0]) + Delta(2, ExpectedText) + Done(3, ExpectedText) + Completed(4, ExpectedText));
        await RejectText("duplicate metadata sequence is not deduplicated into success",
            Created() + metadataEvent + metadataEvent + Delta(2, ExpectedText) + Done(3, ExpectedText) + Completed(4, ExpectedText));
        await RejectText("out-of-order metadata sequence remains blocked",
            Created() + Delta(2, ExpectedText) + metadataEvent + Done(3, ExpectedText) + Completed(4, ExpectedText));
        foreach (var invalidValue in new object?[] { null, Array.Empty<object>(), "metadata is not body", 42, true })
            await RejectText("metadata value must be a typed object",
                Created() + Metadata(1, invalidValue) + Delta(2, ExpectedText) + Done(3, ExpectedText) + Completed(4, ExpectedText));
        await RejectText("metadata property must be present",
            Created() + Event(1, "response.metadata", new { response_id = ResponseId })
            + Delta(2, ExpectedText) + Done(3, ExpectedText) + Completed(4, ExpectedText));
        await RejectText("duplicate keys inside metadata are rejected",
            Created() + metadataEvent.Replace("\"metadata\":{}", "\"metadata\":{\"openai_verification_recommendation\":[],\"openai_verification_recommendation\":[]}", StringComparison.Ordinal)
            + Delta(2, ExpectedText) + Done(3, ExpectedText) + Completed(4, ExpectedText));
        await RejectText("duplicate metadata envelope fields are rejected",
            Created() + metadataEvent.Replace("\"metadata\":{}", "\"metadata\":{},\"metadata\":{}", StringComparison.Ordinal)
            + Delta(2, ExpectedText) + Done(3, ExpectedText) + Completed(4, ExpectedText));
        foreach (var recommendation in new object?[] { null, "", true, 42, new { }, new object[] { new { action = "review-required" } } })
            await RejectText("verification recommendation requires further handling unless it is an empty array",
                Created() + Metadata(1, new { openai_verification_recommendation = recommendation })
                + Delta(2, ExpectedText) + Done(3, ExpectedText) + Completed(4, ExpectedText));
        await RejectText("unknown metadata field remains blocked even when its value is an empty array",
            Created() + Metadata(1, new { future_field = Array.Empty<object>() })
            + Delta(2, ExpectedText) + Done(3, ExpectedText) + Completed(4, ExpectedText));
        await RejectText("empty verification recommendation cannot authorize an extra metadata key",
            Created() + Metadata(1, new { openai_verification_recommendation = Array.Empty<object>(), extra = "" })
            + Delta(2, ExpectedText) + Done(3, ExpectedText) + Completed(4, ExpectedText));
        await RejectText("public safety buffering subtype requires explicit handling",
            Created() + Metadata(1, new { type = "safety_buffering" })
            + Delta(2, ExpectedText) + Done(3, ExpectedText) + Completed(4, ExpectedText));
        await RejectText("public moderation metadata requires explicit handling even when empty",
            Created() + Metadata(1, new { openai_chatgpt_moderation_metadata = new { } })
            + Delta(2, ExpectedText) + Done(3, ExpectedText) + Completed(4, ExpectedText));
        foreach (var field in new[] { "safety_buffer", "review_required", "quota", "model", "status", "text", "error",
            "incomplete_details", "instructions", "action", "authorization", "scopes", "shell", "tool_calls" })
        {
            var value = new Dictionary<string, object?> { [field] = "SYNTHETIC_METADATA_ACTION_NEVER_EXECUTE" };
            await RejectText("metadata cannot supply capability or action instructions: " + field,
                Created() + Metadata(1, value) + Delta(2, ExpectedText) + Done(3, ExpectedText) + Completed(4, ExpectedText));
        }
        foreach (var field in new[] { "text", "delta", "status", "extra" })
        {
            var envelope = new Dictionary<string, object?> { ["response_id"] = ResponseId, ["metadata"] = new { }, [field] = ExpectedText };
            await RejectText("metadata envelope cannot contain unrecognized top-level field: " + field,
                Created() + Event(1, "response.metadata", envelope) + Delta(2, ExpectedText) + Done(3, ExpectedText) + Completed(4, ExpectedText));
        }
        await RejectText("metadata is not a text event",
            Created() + Metadata(1, noActionMetadata[0]) + Completed(2, ExpectedText));
        await RejectText("metadata is not a completion event",
            Created() + Delta(1, ExpectedText) + Done(2, ExpectedText) + Metadata(3, noActionMetadata[0]));
        await RejectText("valid metadata cannot hide a later error",
            Created() + Metadata(1, noActionMetadata[0]) + Delta(2, ExpectedText) + Done(3, ExpectedText)
            + Event(4, "error", new { code = "fixture_error", message = "synthetic failure" }) + Completed(5, ExpectedText));
        await RejectText("valid metadata cannot hide a later incomplete response",
            Created() + Metadata(1, noActionMetadata[0]) + Delta(2, ExpectedText) + Done(3, ExpectedText)
            + Event(4, "response.incomplete", new { response = new { id = ResponseId, status = "incomplete",
                incomplete_details = new { reason = "max_output_tokens" } } }) + Completed(5, ExpectedText));
        await RejectText("valid metadata cannot whitelist a different unknown event",
            Created() + Metadata(1, noActionMetadata[0]) + Event(2, "response.future_event.done", new { })
            + Delta(3, ExpectedText) + Done(4, ExpectedText) + Completed(5, ExpectedText));
        using (var metadataProtocol = new SiwcHttpProtocol())
        {
            const string privateMetadataValue = "PRIVATE_METADATA_ACTION_SENTINEL";
            var source = Created() + Metadata(1, new { openai_verification_recommendation = new[] { privateMetadataValue } })
                + Delta(2, ExpectedText) + Done(3, ExpectedText) + Completed(4, ExpectedText);
            Exception? blockedMetadata = null;
            try { await using var unexpected = await Read(metadataProtocol, Response(Encoding.UTF8.GetBytes(source)), requirements); }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException) { blockedMetadata = error; }
            Check(blockedMetadata is SiwcProtocolException { Code: "siwc_metadata_requires_handling" },
                "metadata needing handling preserves a fixed blocked reason instead of being ignored");
            var diagnostic = blockedMetadata is null ? null : SiwcResponseEvidence.GetRejection(blockedMetadata);
            Check(diagnostic is { EventType: "response.metadata", Reason: "siwc_metadata_requires_handling" }
                && !JsonSerializer.Serialize(diagnostic).Contains(privateMetadataValue, StringComparison.Ordinal),
                "metadata rejection diagnostic retains only safe structure and never the action values");
            Check(metadataProtocol.ResponseEvidence?.MissingContentTypeValidated != true,
                "metadata needing handling never validates the missing-type response");
        }

        Console.WriteLine("PASS SIWC Missing Content-Type OFFLINE checks: " + checks + "; real requests: 0");
        return checks;
    }

    static string TextStream(string text) => Created() + Delta(1, text) + Done(2, text) + Completed(3, text);
    static string Created(int sequence = 0, string model = Model, bool omitModel = false, bool omitId = false,
        string objectKind = "response", string type = "response.created")
    {
        var response = new Dictionary<string, object?> { ["object"] = objectKind, ["status"] = "in_progress", ["output"] = Array.Empty<object>() };
        if (!omitId) response["id"] = ResponseId;
        if (!omitModel) response["model"] = model;
        return Event(sequence, type, new { response });
    }
    static string Delta(int sequence, string text, int outputIndex = 0) => Event(sequence, "response.output_text.delta",
        new { item_id = MessageId, output_index = outputIndex, content_index = 0, delta = text, logprobs = Array.Empty<object>() });
    static string Done(int sequence, string text, int outputIndex = 0) => Event(sequence, "response.output_text.done",
        new { item_id = MessageId, output_index = outputIndex, content_index = 0, text, logprobs = Array.Empty<object>() });
    static string Metadata(int sequence, object? metadata) => Event(sequence, "response.metadata", new { response_id = ResponseId, metadata });
    static object Message(string text) => new { id = MessageId, type = "message", role = "assistant", status = "completed",
        content = new[] { new { type = "output_text", text, annotations = Array.Empty<object>(), logprobs = Array.Empty<object>() } } };
    static string Completed(int sequence, string text, string id = ResponseId, string model = Model,
        string status = "completed", object? error = null, object? incomplete = null,
        bool omitModel = false, bool omitId = false, string objectKind = "response")
    {
        var response = new Dictionary<string, object?> { ["object"] = objectKind, ["status"] = status,
            ["error"] = error, ["incomplete_details"] = incomplete, ["output"] = new[] { Message(text) } };
        if (!omitId) response["id"] = id;
        if (!omitModel) response["model"] = model;
        return Event(sequence, "response.completed", new { response });
    }
    static string PrettyStream()
    {
        return string.Concat(TextStream(ExpectedText).Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Select(frame =>
        {
            var split = frame.IndexOf("\ndata: ", StringComparison.Ordinal);
            using var document = JsonDocument.Parse(frame[(split + 7)..]);
            var pretty = JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
            return frame[..split] + "\n" + string.Join("\n", pretty.Split('\n').Select(line => "data: " + line)) + "\n\n";
        }));
    }
    static string Event(int sequence, string type, object fields)
    {
        var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(fields))!;
        values["type"] = JsonSerializer.SerializeToElement(type);
        values["sequence_number"] = JsonSerializer.SerializeToElement(sequence);
        return "event: " + type + "\ndata: " + JsonSerializer.Serialize(values) + "\n\n";
    }
}

using System.Text;
using System.Text.Json;
using CodexQuotaWidget.Core.ControlCenter;

/// <summary>Only synthetic Responses events. This suite never reads credentials or creates network requests.</summary>
internal static class SiwcBodyReadParserChecks
{
    const string ExpectedText = "第二次连接验证成功456";
    const string ResponseId = "resp_body_read_fixture";
    const string MessageId = "msg_body_read_fixture";

    public static async Task<int> RunAsync()
    {
        int checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("SIWC body parser fixture: " + name);
            checks++;
        }
        async Task Reject(string source, string code, string name)
        {
            try { await Read(source); }
            catch (SiwcProtocolException error)
            {
                Check(error.Code == code, name + " (" + error.Code + ")");
                return;
            }
            throw new InvalidOperationException("SIWC body parser fixture did not reject: " + name);
        }

        var normal = TextStream(ExpectedText);
        var complete = await Read(normal);
        Check(complete.Text == ExpectedText, "a new Chinese 456 answer is read as exact text");
        Check(complete.ResponseId == ResponseId && complete.TextDeltaEvents == 1, "completed response identity and delta evidence are retained");
        Check(complete.Usage is { InputTokens: 13, OutputTokens: 8, TotalTokens: 21 }, "only completed numeric token usage is extracted");
        Check((await Read(normal.Replace("\r\n", "\n", StringComparison.Ordinal))).Text == ExpectedText, "LF event framing is supported");

        var first = ExpectedText[..4];
        var second = ExpectedText[4..8];
        var last = ExpectedText[8..];
        var split = Created()
            + Delta(1, first) + Delta(2, second) + Delta(3, last)
            + Done(4, ExpectedText) + Completed(5, [ExpectedText]);
        var splitResult = await Read(split);
        Check(splitResult.Text == ExpectedText && splitResult.TextDeltaEvents == 3, "separate text deltas concatenate in order");

        var multiContent = Created()
            + Delta(1, first, contentIndex: 0) + Done(2, first, contentIndex: 0)
            + Delta(3, second + last, contentIndex: 1) + Done(4, second + last, contentIndex: 1)
            + Completed(5, [first, second + last]);
        Check((await Read(multiContent)).Text == ExpectedText, "multiple assistant content parts concatenate using final content order");

        var finalOnly = Created() + Completed(1, [ExpectedText]);
        var finalOnlyResult = await Read(finalOnly);
        Check(finalOnlyResult.Text == ExpectedText && finalOnlyResult.TextDeltaEvents == 0, "an authoritative completed snapshot is accepted without fabricated deltas");

        var metadata = Created()
            + Event(1, "response.in_progress", new { response = new { id = ResponseId } })
            + Event(2, "response.output_item.added", new { output_index = 0, item = new { type = "message", id = MessageId, role = "assistant", status = "in_progress", content = Array.Empty<object>() } })
            + Event(3, "response.content_part.added", new { item_id = MessageId, output_index = 0, content_index = 0, part = new { type = "output_text", text = "metadata must not become the answer" } })
            + Delta(4, ExpectedText) + Done(5, ExpectedText)
            + Event(6, "response.output_text.annotation.added", new { item_id = MessageId, output_index = 0, content_index = 0, annotation_index = 0, annotation = new { type = "url_citation", title = "synthetic metadata", url = "https://example.invalid/fixture" } })
            + Event(7, "response.content_part.done", new { item_id = MessageId, output_index = 0, content_index = 0, part = new { type = "output_text", text = ExpectedText } })
            + Completed(8, [ExpectedText]);
        Check((await Read(": keepalive\r\n\r\n" + metadata)).Text == ExpectedText, "metadata, annotations and keepalive do not enter body text");

        var reasoning = Created()
            + Event(1, "response.output_item.added", new { output_index = 0, item = new { id = "rs_body_read_fixture", type = "reasoning", summary = Array.Empty<object>() } })
            + Event(2, "response.reasoning_summary_text.delta", new { item_id = "rs_body_read_fixture", output_index = 0, summary_index = 0, delta = "synthetic reasoning metadata" })
            + Delta(3, ExpectedText, outputIndex: 1) + Done(4, ExpectedText, outputIndex: 1)
            + Event(5, "response.completed", new { response = new { id = ResponseId, status = "completed", output = new object[] {
                new { id = "rs_body_read_fixture", type = "reasoning", summary = Array.Empty<object>() }, Message([ExpectedText]) } } });
        Check((await Read(reasoning)).Text == ExpectedText, "reasoning metadata is excluded from assistant output text");

        var delta = Delta(1, ExpectedText);
        var duplicate = Created() + delta + delta + Done(2, ExpectedText) + Completed(3, [ExpectedText]);
        var deduplicated = await Read(duplicate);
        Check(deduplicated.Text == ExpectedText && deduplicated.TextDeltaEvents == 1, "identical repeated sequence is counted and appended once");
        await Reject(Created() + delta + Delta(1, "different"), "siwc_event_sequence_conflict", "conflicting duplicate sequence is rejected");
        await Reject(Created() + Delta(2, "second") + Delta(1, "first"), "siwc_event_out_of_order", "out-of-order sequence is rejected");

        await Reject(Created() + Delta(1, ExpectedText), "siwc_stream_ended_without_completed", "text deltas without terminal completion remain unconfirmed");
        await Reject(Created() + Delta(1, ExpectedText) + Done(2, ExpectedText), "siwc_stream_ended_without_completed", "text.done is not response completion");
        await Reject(Created() + "data: [DONE]\r\n\r\n", "siwc_done_without_completed", "transport DONE is not response completion");
        await Reject(Created() + Event(1, "response.incomplete", new { response = new { id = ResponseId, status = "incomplete", incomplete_details = new { reason = "max_output_tokens" } } }),
            "siwc_response_incomplete", "an incomplete terminal response is not success");
        await Reject(Created() + Event(1, "response.failed", new { response = new { id = ResponseId, status = "failed", error = new { code = "synthetic_failure", message = "synthetic error" } } }),
            "siwc_response_failed", "failed response never becomes body text");
        await Reject(Created() + Event(1, "error", new { code = "synthetic_error", message = "synthetic error" }),
            "siwc_response_failed", "top-level error event is rejected");
        await Reject(Created() + Event(1, "response.failed", new { response = new { id = ResponseId, status = "failed", error = new { code = "subscription_sharing_usage_limit_exceeded" } } }),
            "subscription_sharing_usage_limit_exceeded", "documented plan exhaustion remains distinguishable");
        await Reject(TextStream(""), "siwc_invalid_text_body", "an empty completed answer is not accepted");
        await Reject(TextStream(" \r\n\t"), "siwc_invalid_text_body", "a whitespace-only answer is not accepted");
        await Reject(TextStream("::chatgpt-content-reference{synthetic-reference}"), "siwc_invalid_text_body", "a content reference is not treated as an answer");

        await Reject(Created() + Event(1, "response.output_text.delta", new { response_id = "resp_wrong_fixture", item_id = MessageId, output_index = 0, content_index = 0, delta = ExpectedText }),
            "siwc_response_identity_mismatch", "delta cannot switch the bound response identity");
        await Reject(Created() + Event(1, "response.in_progress", new { response = new { id = "resp_wrong_fixture" } }),
            "siwc_response_identity_mismatch", "in-progress event cannot replace the original response");
        await Reject(Created() + Completed(1, [ExpectedText], responseId: "resp_wrong_fixture"),
            "siwc_completed_identity_or_status_mismatch", "completed event must match the created response");
        await Reject(Created() + Completed(1, [ExpectedText], status: "in_progress"),
            "siwc_completed_identity_or_status_mismatch", "event name alone does not establish a completed status");
        await Reject(Created() + Delta(1, ExpectedText) + Done(2, "conflicting snapshot"),
            "siwc_text_snapshot_conflict", "done snapshot must match received deltas");
        await Reject(Created() + Delta(1, ExpectedText) + Done(2, ExpectedText) + Completed(3, ["conflicting final body"]),
            "siwc_completed_snapshot_conflict", "completed snapshot must match received text");
        await Reject(Created() + Delta(1, ExpectedText) + Done(2, ExpectedText) + Delta(3, "late"),
            "siwc_delta_after_text_done", "late delta cannot mutate a completed text part");
        await Reject(Created() + Delta(1, ExpectedText) + Event(2, "response.output_text.done", new { item_id = "msg_wrong_fixture", output_index = 0, content_index = 0, text = ExpectedText }),
            "siwc_output_item_mismatch", "text item identity must stay bound to its output index");

        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            var cancelled = false;
            try { await Read(normal, canceled.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "explicit user cancellation interrupts body parsing");
        }
        Console.WriteLine("SIWC body parser offline fixtures passed: " + checks + "; real account/model requests: 0");
        return checks;
    }

    static async Task<SiwcTextCompletion> Read(string source, CancellationToken token = default)
    {
        await using var stream = new FragmentedStream(Encoding.UTF8.GetBytes(source));
        return await SiwcResponsesReader.ReadAsync(stream, token);
    }
    static string TextStream(string text) => Created() + Delta(1, text) + Done(2, text) + Completed(3, [text]);
    static string Created() => Event(0, "response.created", new { response = new { id = ResponseId, status = "in_progress" } });
    static string Delta(int sequence, string text, int contentIndex = 0, int outputIndex = 0) => Event(sequence, "response.output_text.delta",
        new { item_id = MessageId, output_index = outputIndex, content_index = contentIndex, delta = text });
    static string Done(int sequence, string text, int contentIndex = 0, int outputIndex = 0) => Event(sequence, "response.output_text.done",
        new { item_id = MessageId, output_index = outputIndex, content_index = contentIndex, text });
    static object Message(string[] texts) => new { id = MessageId, type = "message", role = "assistant", status = "completed",
        content = texts.Select(text => new { type = "output_text", text }).ToArray() };
    static string Completed(int sequence, string[] texts, string responseId = ResponseId, string status = "completed") => Event(sequence, "response.completed",
        new { response = new { id = responseId, status, usage = new { input_tokens = 13, output_tokens = 8, total_tokens = 21 }, output = new[] { Message(texts) } } });
    static string Event(int sequence, string type, object fields)
    {
        var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(fields))!;
        values["type"] = JsonSerializer.SerializeToElement(type);
        values["sequence_number"] = JsonSerializer.SerializeToElement(sequence);
        return "event: " + type + "\r\ndata: " + JsonSerializer.Serialize(values) + "\r\n\r\n";
    }
    sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
            => base.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], token);
    }
}

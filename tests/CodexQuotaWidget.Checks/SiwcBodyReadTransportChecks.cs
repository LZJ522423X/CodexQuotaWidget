using System.Net;
using System.Text;
using System.Text.Json;
using CodexQuotaWidget.Core.ControlCenter;

/// <summary>In-memory HTTP responses only; never constructs or sends an HTTP request.</summary>
internal static class SiwcBodyReadTransportChecks
{
    public static async Task<int> RunAsync()
    {
        int checks = 0;
        void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException("SIWC transport fixture: " + name); checks++; }
        var method = typeof(SiwcHttpProtocol).GetMethod("ReadResponseAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        Task<Stream> Read(SiwcHttpProtocol protocol, HttpResponseMessage response) => (Task<Stream>)method.Invoke(protocol, [response, CancellationToken.None, null])!;
        HttpResponseMessage Response(string? type, string text, HttpStatusCode status = HttpStatusCode.OK)
        {
            var result = new HttpResponseMessage(status) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(text)) };
            if (type is not null) result.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(type);
            return result;
        }
        foreach (var type in new[] { "text/event-stream", "Text/Event-Stream; charset=utf-8" })
        {
            using var protocol = new SiwcHttpProtocol();
            await using var stream = await Read(protocol, Response(type, "data: {}\n\n"));
            Check(protocol.ResponseEvidence is { StatusCode: 200, ContentType: "text/event-stream", Structure.Kind: "DeclaredSse" }, "SSE media type is case-insensitive and charset-independent");
            using var reader = new StreamReader(stream);
            Check(await reader.ReadToEndAsync() == "data: {}\n\n", "declared SSE bytes are preserved for strict parser");
        }
        foreach (var test in new[] {
            (Type:(string?)"application/json", Text:"{\"object\":\"response\",\"status\":\"completed\",\"text\":\"not a valid stream\"}", Shape:"JsonObject"),
            (Type:(string?)"text/html", Text:"<html>synthetic-private-value</html>", Shape:"HtmlOrXml"),
            (Type:(string?)null, Text:"data: {}\n\n", Shape:"SseFraming"),
            (Type:(string?)"text/plain", Text:"synthetic-private-value", Shape:"NonJsonText"),
            (Type:(string?)"application/json", Text:"", Shape:"Empty") })
        {
            using var protocol = new SiwcHttpProtocol();
            try { await Read(protocol, Response(test.Type, test.Text)); throw new InvalidOperationException("unexpected response admitted"); }
            catch (SiwcProtocolException e) { Check(e.Code == "siwc_expected_event_stream", "unexpected type never becomes text success"); }
            Check(protocol.ResponseEvidence?.Structure.Kind == test.Shape, "rejected response has real structural evidence");
            var evidence = JsonSerializer.Serialize(protocol.ResponseEvidence);
            Check(!evidence.Contains("synthetic-private-value") && !evidence.Contains("not a valid stream"), "diagnostics never persist raw content");
        }
        using (var protocol = new SiwcHttpProtocol())
        {
            try { await Read(protocol, Response("application/json", "{\"error\":{\"message\":\"synthetic-private-value\"}}", HttpStatusCode.Unauthorized)); throw new InvalidOperationException(); }
            catch (HttpRequestException e) { Check(e.StatusCode == HttpStatusCode.Unauthorized, "HTTP failure remains failure"); }
            Check(protocol.ResponseEvidence is { StatusCode: 401, ContentType: "application/json", Structure.Kind: "JsonObject" }, "HTTP error retains sanitized status and shape");
        }
        var projected = SiwcResponseEvidence.Inspect(Encoding.UTF8.GetBytes("{\"type\":\"synthetic-private-value\",\"synthetic-secret-key\":\"synthetic-private-value\",\"error\":{\"message\":\"synthetic-private-value\"}}"));
        var safe = JsonSerializer.Serialize(projected);
        Check(!safe.Contains("synthetic-private-value") && !safe.Contains("synthetic-secret-key") && safe.Contains("OtherEvent"), "unknown fields and discriminator values are never logged");
        var typed = SiwcResponseEvidence.Inspect(Encoding.UTF8.GetBytes("{\"object\":\"response\",\"status\":\"completed\",\"output\":[],\"model\":\"synthetic-private-value\"}"));
        Check(typed is { ObjectKind: "response", ResponseStatus: "completed" } && !JsonSerializer.Serialize(typed).Contains("synthetic-private-value"), "only fixed non-sensitive completion discriminators are retained");
        using (var large = new MemoryStream(Encoding.UTF8.GetBytes("{\"text\":\"" + new string('x', 20000))))
        { var summary = await SiwcResponseEvidence.ProbeAsync(large, default); Check(summary.Truncated && large.Position == 16 * 1024, "diagnostic probe is bounded to 16KiB"); }
        var structures = new List<SiwcResponseShape>();
        string E(int seq, string type, object value) => SiwcPhaseOneChecks.E(seq, type, value);
        var created = E(0, "response.created", new { response = new { id = "resp_t", model = "fixture-model" } });
        var completed = E(1, "response.completed", new { response = new { id = "resp_t", status = "completed", model = "fixture-model", output = new[] {
            new { type = "message", role = "assistant", status = "completed", id = "msg_t", content = new[] { new { type = "output_text", text = "第二次连接验证成功456" } } } } } });
        await using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(created + completed)))
        { var reply = await SiwcResponsesReader.ReadAsync(stream, observe: structures.Add, expectedModel: "fixture-model"); Check(reply.Text == "第二次连接验证成功456" && structures.Count == 2, "typed completed snapshot has model and safe event structures"); }
        await using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(created + completed)))
        {
            try { await SiwcResponsesReader.ReadAsync(stream, expectedModel: "other-model"); throw new InvalidOperationException(); }
            catch (SiwcProtocolException e) { Check(e.Code == "siwc_response_model_mismatch", "wrong model identity cannot pass"); }
        }
        await using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(created + completed.Replace("\"status\":\"completed\",\"model\"", "\"status\":\"completed\",\"error\":{},\"model\"", StringComparison.Ordinal))))
        {
            try { await SiwcResponsesReader.ReadAsync(stream); throw new InvalidOperationException(); }
            catch (SiwcProtocolException e) { Check(e.Code == "siwc_completed_contains_failure", "completed envelope with an error is never accepted"); }
        }
        Console.WriteLine("PASS SIWC HTTP/structure OFFLINE checks: " + checks);
        return checks;
    }
}

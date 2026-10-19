using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace CodexQuotaWidget.Core.ControlCenter;

/// <summary>Public documented endpoints only. The phase-one hard gate runs before creating any HTTP request.</summary>
public sealed class SiwcHttpProtocol(SiwcPhaseTwoAccess? acceptanceAccess = null) : ISiwcSingleRequestTransport, ISiwcResponseEvidenceSource, IDisposable
{
    public SiwcHttpResponseEvidence? ResponseEvidence { get; private set; }
    internal const int ModelCatalogMaximumBytes = 8 * 1024 * 1024;
    public SiwcAcceptanceEvidenceKind EvidenceKind => acceptanceAccess is not null && SiwcPhaseTwoAccess.IsExperimentalBuild
        ? SiwcAcceptanceEvidenceKind.OfficialHttps : SiwcAcceptanceEvidenceKind.Unverified;
    readonly HttpClient client = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromMinutes(2) };
    public Task<JsonElement> TokenAsync(IReadOnlyDictionary<string, string> form, CancellationToken token)
        => JsonAsync(new HttpRequestMessage(HttpMethod.Post, "https://auth.openai.com/api/accounts/oauth/token") { Content = new FormUrlEncodedContent(form) }, token);
    public Task<JsonElement> PublicMetadataAsync(bool keys, CancellationToken token)
        => MetadataAsync(keys, token);
    async Task<JsonElement> MetadataAsync(bool keys, CancellationToken token)
    {
        var metadata = await JsonAsync(new(HttpMethod.Get, "https://auth.openai.com/.well-known/openid-configuration"), token).ConfigureAwait(false);
        if (!keys) return metadata;
        if (SiwcAuthorizationAttempt.Required(metadata, "issuer") != "https://auth.openai.com") throw new InvalidDataException("SIWC discovery issuer mismatch.");
        var uri = new Uri(SiwcAuthorizationAttempt.Required(metadata, "jwks_uri")); ValidateAuthEndpoint(uri);
        return await JsonAsync(new(HttpMethod.Get, uri), token).ConfigureAwait(false);
    }
    public async Task<bool> RevokeAsync(Uri endpoint, IReadOnlyDictionary<string, string> form, CancellationToken token)
    {
        RequireAccountAccess(); ValidateAuthEndpoint(endpoint);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new FormUrlEncodedContent(form) };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        return response.StatusCode == HttpStatusCode.OK;
    }
    public async Task<Stream> ResponsesAsync(string accessToken, byte[] body, CancellationToken token)
    {
        if (acceptanceAccess is null) SiwcPhaseOne.RequireLiveApproval(); else acceptanceAccess.Consume(body);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses") { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new("application/json"); request.Headers.Authorization = new("Bearer", accessToken);
        request.Headers.Accept.Add(new("text/event-stream"));
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        return await ReadResponseAsync(response, token, acceptanceAccess?.MissingContentTypeRequirements).ConfigureAwait(false);
    }
    internal async Task<Stream> ReadResponseAsync(HttpResponseMessage response, CancellationToken token, SiwcStrictSseRequirements? missingRequirements = null)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        ResponseEvidence = new((int)response.StatusCode, SiwcResponseEvidence.MediaType(mediaType), new("HeadersOnly", [], [], false));
        try
        {
            // A prefix only selects a bounded candidate; this branch returns bytes only after complete strict validation.
            if (response.StatusCode == HttpStatusCode.OK && !response.Content.Headers.Contains("Content-Type") && missingRequirements is not null)
                return await ValidateMissingContentTypeAsync(response, missingRequirements, token).ConfigureAwait(false);
            // Explicit JSON/HTML/unknown types remain rejected, regardless of their bytes.
            if (!response.IsSuccessStatusCode || !string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                using var probeTimeout = CancellationTokenSource.CreateLinkedTokenSource(token); probeTimeout.CancelAfter(TimeSpan.FromSeconds(3));
                try
                {
                    await using var probe = await response.Content.ReadAsStreamAsync(probeTimeout.Token).ConfigureAwait(false);
                    ResponseEvidence = ResponseEvidence with { Structure = await SiwcResponseEvidence.ProbeAsync(probe, probeTimeout.Token).ConfigureAwait(false) };
                }
                catch (Exception e) when (e is IOException or OperationCanceledException or HttpRequestException)
                { ResponseEvidence = ResponseEvidence with { Structure = new("ProbeUnavailable", [], [], false) }; }
                if (!response.IsSuccessStatusCode) throw new HttpRequestException("SIWC Responses HTTP failure " + (int)response.StatusCode, null, response.StatusCode);
                throw new SiwcProtocolException("siwc_expected_event_stream");
            }
            ResponseEvidence = ResponseEvidence with { Structure = new("DeclaredSse", [], [], false) };
            return new OwnedStream(await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false), response);
        }
        catch { response.Dispose(); throw; }
    }
    async Task<Stream> ValidateMissingContentTypeAsync(HttpResponseMessage response, SiwcStrictSseRequirements requirements, CancellationToken token)
    {
        const int prefixLimit = 16 * 1024, bodyLimit = 256 * 1024;
        await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var memory = new MemoryStream(); var buffer = new byte[4096];
        while (memory.Length < prefixLimit)
        {
            var count = await source.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, prefixLimit - (int)memory.Length)), token).ConfigureAwait(false);
            if (count == 0) break;
            await memory.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
        }
        var shape = SiwcResponseEvidence.Inspect(memory.GetBuffer().AsSpan(0, (int)memory.Length), memory.Length == prefixLimit);
        ResponseEvidence = ResponseEvidence! with { Structure = shape };
        if (shape.Kind != "SseFraming") throw new SiwcProtocolException("siwc_missing_content_type_not_sse_candidate");
        ResponseEvidence = ResponseEvidence with { Structure = new("UnvalidatedSseCandidate", [], [], false) };
        int read;
        while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            if (memory.Length + read > bodyLimit)
            {
                ResponseEvidence = ResponseEvidence with { Structure = new("UnvalidatedSseCandidate", [], [], true) };
                throw new SiwcProtocolException("siwc_missing_content_type_size_limit");
            }
            await memory.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
        }
        var bytes = memory.ToArray(); var shapes = new List<SiwcResponseShape>();
        try
        {
            using var validation = new MemoryStream(bytes, writable: false);
            await SiwcResponsesReader.ReadAsync(validation, token, s => { if (shapes.Count < 64) shapes.Add(s); }, requirements.Model, requirements).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            ResponseEvidence = ResponseEvidence with { RejectedEvent = SiwcResponseEvidence.GetRejection(error),
                Structure = new("RejectedSseCandidate", [], shapes.SelectMany(s => s.EventTypes).Distinct(StringComparer.Ordinal).ToArray(), false) };
            throw;
        }
        ResponseEvidence = ResponseEvidence with { MissingContentTypeValidated = true,
            Structure = new("ValidatedMissingTypeSse", [], shapes.SelectMany(s => s.EventTypes).Distinct(StringComparer.Ordinal).ToArray(), false) };
        response.Dispose();
        return new MemoryStream(bytes, writable: false); // Replay into the unchanged caller's normal reader; nothing is sent again.
    }
    public Task<JsonElement> ModelsAsync(string accessToken, CancellationToken token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.openai.com/v1/models");
        request.Headers.Authorization = new("Bearer", accessToken); return JsonAsync(request, token, ModelCatalogMaximumBytes);
    }
    async Task<JsonElement> JsonAsync(HttpRequestMessage request, CancellationToken token, int maximumBytes = 256 * 1024)
    {
        using (request)
        {
            RequireAccountAccess();
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException("SIWC HTTP failure " + (int)response.StatusCode, null, response.StatusCode);
            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            return await ReadBoundedJsonAsync(stream, maximumBytes, token).ConfigureAwait(false);
        }
    }
    internal static async Task<JsonElement> ReadBoundedJsonAsync(Stream stream, int maximumBytes, CancellationToken token)
    {
            if (maximumBytes is <= 0 or > ModelCatalogMaximumBytes) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
            using var memory = new MemoryStream(); var buffer = new byte[4096]; int count;
            while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            { if (memory.Length + count > maximumBytes) throw new InvalidDataException("SIWC protocol response too large."); await memory.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false); }
            using var document = JsonDocument.Parse(memory.ToArray()); SiwcIdToken.Unique(document.RootElement); return document.RootElement.Clone();
    }
    static void ValidateAuthEndpoint(Uri uri)
    {
        if (uri.Scheme != "https" || uri.Host != "auth.openai.com" || uri.Port != 443 || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException("Untrusted SIWC identity endpoint.");
    }
    void RequireAccountAccess() { if (acceptanceAccess is null) SiwcPhaseOne.RequireLiveApproval(); else acceptanceAccess.RequireAccountAccess(); }
    public void ArmSingleRequest(SiwcSingleRequestApproval approval, byte[] body)
    {
        if (acceptanceAccess is null) throw new InvalidOperationException("普通连接器不能启用单条额度例外。");
        acceptanceAccess.Arm(approval, body);
    }
    public void Dispose() => client.Dispose();
    sealed class OwnedStream(Stream inner, HttpResponseMessage response) : Stream
    {
        public override bool CanRead => inner.CanRead; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => inner.ReadAsync(buffer, token);
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) { inner.Dispose(); response.Dispose(); } base.Dispose(disposing); }
    }
}

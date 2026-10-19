using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexQuotaWidget.Core.ControlCenter;

public interface ISiwcProtocolTransport
{
    Task<JsonElement> TokenAsync(IReadOnlyDictionary<string, string> form, CancellationToken token);
    Task<JsonElement> PublicMetadataAsync(bool keys, CancellationToken token);
    Task<bool> RevokeAsync(Uri endpoint, IReadOnlyDictionary<string, string> form, CancellationToken token);
    Task<Stream> ResponsesAsync(string accessToken, byte[] body, CancellationToken token);
    Task<JsonElement> ModelsAsync(string accessToken, CancellationToken token);
}

/// <summary>In-memory, single-use PKCE attempt. Constructing this object never opens a browser or listener.</summary>
public sealed class SiwcAuthorizationAttempt
{
    public const string Resource = "https://api.openai.com/v1";
    public const string Scopes = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct";
    readonly string verifier = RandomValue(), state = RandomValue(), nonce = RandomValue();
    readonly string? registeredClient;
    readonly string? expectedSubject;
    readonly string host;
    readonly Uri redirect;
    readonly DateTimeOffset expires;
    public string? IssuedClientId { get; private set; }
    int consumed;
    public SiwcAuthorizationAttempt(string hostId, Uri redirectUri, string? issuedClient = null, string? expectedSubject = null)
    {
        if (!hostId.StartsWith("urn:uuid:", StringComparison.Ordinal) || !Guid.TryParse(hostId[9..], out _)
            || redirectUri.Scheme != "http" || redirectUri.Host != "127.0.0.1" || redirectUri.Port < 1024
            || redirectUri.AbsolutePath != "/auth/callback" || redirectUri.Query.Length != 0 || redirectUri.Fragment.Length != 0
            || redirectUri.UserInfo.Length != 0 || issuedClient is not null && (!ValidClient(issuedClient) || string.IsNullOrWhiteSpace(expectedSubject)))
            throw new InvalidDataException("Invalid SIWC host, loopback redirect or issued client.");
        host = hostId; redirect = redirectUri; registeredClient = issuedClient; IssuedClientId = issuedClient;
        this.expectedSubject = expectedSubject; expires = DateTimeOffset.UtcNow.AddMinutes(5);
    }
    public Uri AuthorizationUri
    {
        get
        {
            var values = new Dictionary<string, string> { ["client_id"] = registeredClient ?? "dynamic_agent_client", ["ext_agent_host_id"] = host,
                ["response_type"] = "code", ["redirect_uri"] = redirect.AbsoluteUri, ["scope"] = Scopes, ["resource"] = Resource,
                ["state"] = state, ["nonce"] = nonce, ["code_challenge_method"] = "S256",
                ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))) };
            if (registeredClient is null) values["agent_name_hint"] = "AI Control Center";
            return new("https://auth.openai.com/api/accounts/authorize?" + string.Join("&", values.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value))));
        }
    }
    public async Task<SiwcCredential> CompleteAsync(Uri callback, ISiwcProtocolTransport transport, CancellationToken token = default)
    {
        if (callback.GetLeftPart(UriPartial.Path) != redirect.AbsoluteUri || callback.Fragment.Length != 0 || DateTimeOffset.UtcNow > expires)
            throw new InvalidDataException("SIWC callback address or expiry mismatch.");
        var query = ParseQuery(callback.Query);
        if (!query.TryGetValue("state", out var received) || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(received), Encoding.UTF8.GetBytes(state)))
            throw new InvalidDataException("SIWC callback state mismatch.");
        if (Interlocked.Exchange(ref consumed, 1) != 0) throw new InvalidOperationException("SIWC authorization attempt already consumed.");
        if (query.ContainsKey("error")) throw new InvalidOperationException("SIWC authorization declined or failed; no code exchanged.");
        var client = query.GetValueOrDefault("client_id") ?? registeredClient;
        if (client is null || !ValidClient(client) || registeredClient is not null && registeredClient != client
            || !query.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code) || code.Length > 4096)
            throw new InvalidDataException("SIWC issued client or authorization code missing/mismatched.");
        IssuedClientId = client; // Retain pending registration for a fresh attempt after invalid_grant; it is not an active account.
        var response = await transport.TokenAsync(new Dictionary<string, string> { ["grant_type"] = "authorization_code", ["client_id"] = client,
            ["code"] = code, ["code_verifier"] = verifier, ["redirect_uri"] = redirect.AbsoluteUri, ["resource"] = Resource }, token).ConfigureAwait(false);
        SiwcIdToken.Unique(response);
        var identity = SiwcIdToken.Validate(Required(response, "id_token"), await transport.PublicMetadataAsync(true, token).ConfigureAwait(false), client, nonce, expectedSubject);
        return Credential(response, identity, client, host, 0);
    }
    internal static SiwcCredential Credential(JsonElement response, string subject, string client, string host, long revision, SiwcCredential? previous = null)
    {
        if (Required(response, "token_type") != "Bearer" || !response.TryGetProperty("expires_in", out var life) || !life.TryGetInt32(out var seconds) || seconds is <= 0 or > 86400)
            throw new InvalidDataException("SIWC token response type or expiry invalid.");
        var scopes = response.TryGetProperty("scope", out var scope) && scope.ValueKind == JsonValueKind.String
            ? scope.GetString()!.Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToArray() : previous?.Scopes ?? [];
        var earliest = response.TryGetProperty("earliest_refresh_at", out var refresh) && refresh.TryGetInt64(out var epoch)
            ? DateTimeOffset.FromUnixTimeSeconds(epoch) : DateTimeOffset.UtcNow;
        return new() { Subject = subject, ClientId = client, HostId = host, AccessToken = Required(response, "access_token"),
            RefreshToken = Required(response, "refresh_token"), IdToken = response.TryGetProperty("id_token", out var id) ? id.GetString() ?? "" : previous?.IdToken ?? "",
            Scopes = scopes, ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(seconds), EarliestRefreshAt = earliest, Revision = revision };
    }
    internal static string Required(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        && value.GetString() is { Length: > 0 and <= 32768 } text ? text : throw new InvalidDataException("SIWC required protocol field missing.");
    internal static bool ValidClient(string value) => value.StartsWith("oaiapp_", StringComparison.Ordinal) && value.Length is > 7 and < 256
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
    internal static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    internal static byte[] Decode(string value) => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4));
    public static IReadOnlyDictionary<string, string> ParseQuery(string query)
    {
        if (query.Length > 16384) throw new InvalidDataException("SIWC callback too large.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2); var key = Uri.UnescapeDataString(pair[0].Replace('+', ' '));
            var value = pair.Length == 2 ? Uri.UnescapeDataString(pair[1].Replace('+', ' ')) : "";
            if (!values.TryAdd(key, value)) throw new InvalidDataException("SIWC callback contains duplicate fields.");
        }
        return values;
    }
    static string RandomValue() => Base64Url(RandomNumberGenerator.GetBytes(32));
    public override string ToString() => "SIWC PKCE attempt [redacted]";
}

public static class SiwcIdToken
{
    public static string Validate(string token, JsonElement jwks, string client, string? nonce = null, string? expectedSubject = null)
    {
        try
        {
            if (token.Length > 32768) throw new InvalidDataException();
            var parts = token.Split('.'); if (parts.Length != 3) throw new InvalidDataException();
            using var header = JsonDocument.Parse(SiwcAuthorizationAttempt.Decode(parts[0]));
            using var claims = JsonDocument.Parse(SiwcAuthorizationAttempt.Decode(parts[1]));
            Unique(header.RootElement); Unique(claims.RootElement);
            var alg = SiwcAuthorizationAttempt.Required(header.RootElement, "alg");
            var kid = SiwcAuthorizationAttempt.Required(header.RootElement, "kid");
            var keys = jwks.GetProperty("keys").EnumerateArray().Where(k => k.TryGetProperty("kid", out var v) && v.GetString() == kid).ToArray();
            if (keys.Length != 1) throw new InvalidDataException();
            var key = keys[0]; Unique(key);
            if (key.TryGetProperty("use", out var use) && use.GetString() != "sig" || key.TryGetProperty("alg", out var declared) && declared.GetString() != alg)
                throw new InvalidDataException();
            var data = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]); var signature = SiwcAuthorizationAttempt.Decode(parts[2]);
            bool valid;
            if (alg == "RS256" && key.GetProperty("kty").GetString() == "RSA")
            {
                using var rsa = RSA.Create(); rsa.ImportParameters(new RSAParameters { Modulus = SiwcAuthorizationAttempt.Decode(key.GetProperty("n").GetString()!),
                    Exponent = SiwcAuthorizationAttempt.Decode(key.GetProperty("e").GetString()!) });
                valid = rsa.KeySize >= 2048 && rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            }
            else if (alg == "ES256" && key.GetProperty("kty").GetString() == "EC" && key.GetProperty("crv").GetString() == "P-256")
            {
                using var ec = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint {
                    X = SiwcAuthorizationAttempt.Decode(key.GetProperty("x").GetString()!), Y = SiwcAuthorizationAttempt.Decode(key.GetProperty("y").GetString()!) } });
                valid = ec.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            }
            else throw new InvalidDataException();
            var c = claims.RootElement; var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var aud = c.GetProperty("aud"); var audienceMatches = aud.ValueKind == JsonValueKind.String ? aud.GetString() == client
                : aud.ValueKind == JsonValueKind.Array && aud.GetArrayLength() > 0 && aud.EnumerateArray().All(a => a.ValueKind == JsonValueKind.String)
                    && aud.EnumerateArray().Any(a => a.GetString() == client) && (aud.GetArrayLength() == 1 || c.TryGetProperty("azp", out var azp) && azp.GetString() == client);
            if (!valid || c.GetProperty("iss").GetString() != "https://auth.openai.com" || !audienceMatches
                || c.TryGetProperty("azp", out var authorizedParty) && authorizedParty.GetString() != client
                || c.GetProperty("exp").GetInt64() <= now || c.TryGetProperty("nbf", out var nbf) && nbf.GetInt64() > now + 30
                || c.TryGetProperty("iat", out var iat) && iat.GetInt64() > now + 30
                || nonce is not null && (!c.TryGetProperty("nonce", out var n) || n.GetString() != nonce)) throw new InvalidDataException();
            var subject = SiwcAuthorizationAttempt.Required(c, "sub");
            if (expectedSubject is not null && subject != expectedSubject) throw new InvalidDataException();
            return subject;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException or CryptographicException or InvalidDataException)
        { throw new InvalidDataException("SIWC ID-token signature or registration identity validation failed."); }
    }
    internal static void Unique(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("SIWC protocol object required.");
        var names = root.EnumerateObject().Select(p => p.Name).ToArray();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Length) throw new InvalidDataException("SIWC duplicate protocol fields.");
    }
}

public sealed class SiwcAuthorizationService(ISiwcCredentialStore store, ISiwcProtocolTransport transport, SiwcPhaseTwoAccess? experimentalAccess = null)
{
    public async Task<string> SignInInteractiveAsync(string hostId, int port, string? issuedClient, string? subject,
        Action<Uri> openSystemBrowser, CancellationToken token = default)
    {
        var credential = await SiwcInteractiveAuthorization.RunAsync(hostId, port, issuedClient, transport, openSystemBrowser, token, subject, experimentalAccess).ConfigureAwait(false);
        await SaveValidatedAsync(credential, token).ConfigureAwait(false); return credential.AccountId;
    }
    public async Task<string> CompleteAuthorizationAsync(SiwcAuthorizationAttempt attempt, Uri callback, CancellationToken token = default)
    {
        var credential = await attempt.CompleteAsync(callback, transport, token).ConfigureAwait(false);
        await SaveValidatedAsync(credential, token).ConfigureAwait(false); return credential.AccountId;
    }
    Task<bool> SaveValidatedAsync(SiwcCredential credential, CancellationToken token = default)
        => store.WithExclusiveAsync(credential.AccountId, (previous, _) =>
        {
            if (previous is not null && (previous.Subject != credential.Subject || previous.ClientId != credential.ClientId)) throw new InvalidDataException("SIWC account replacement rejected.");
            // This method accepts only a credential obtained from the validated application OAuth flow.
            return Task.FromResult<(SiwcCredential?, bool)>((Copy(credential, (previous?.Revision ?? -1) + 1), true));
        }, token);
    public Task<SiwcCredential> GetAccessAsync(string account, CancellationToken token = default)
        => store.WithExclusiveAsync(account, async (current, ct) =>
        {
            if (current is null || current.SignedOut || !current.HasPlanPermission) throw new InvalidOperationException("SIWC account is signed out or plan permission is absent.");
            if (current.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2)) return (null, current);
            if (current.EarliestRefreshAt > DateTimeOffset.UtcNow || current.RefreshToken.Length == 0)
                throw new InvalidOperationException("SIWC refresh is unavailable or not yet permitted.");
            var response = await transport.TokenAsync(new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["client_id"] = current.ClientId,
                ["refresh_token"] = current.RefreshToken, ["resource"] = SiwcAuthorizationAttempt.Resource }, ct).ConfigureAwait(false);
            SiwcIdToken.Unique(response);
            if (response.TryGetProperty("id_token", out var id))
                SiwcIdToken.Validate(id.GetString()!, await transport.PublicMetadataAsync(true, ct).ConfigureAwait(false), current.ClientId, expectedSubject: current.Subject);
            var renewed = SiwcAuthorizationAttempt.Credential(response, current.Subject, current.ClientId, current.HostId, current.Revision + 1, current);
            if (!renewed.HasPlanPermission) throw new InvalidOperationException("SIWC plan permission lost during refresh.");
            return (renewed, renewed);
        }, token);
    public Task<bool> SignOutAsync(string account, CancellationToken token = default)
        => store.WithExclusiveAsync(account, async (current, ct) =>
        {
            if (current is null || current.SignedOut) return (null, false);
            var revoked = false;
            using var remote = CancellationTokenSource.CreateLinkedTokenSource(ct, token); remote.CancelAfter(TimeSpan.FromSeconds(8));
            try
            {
                var metadata = await transport.PublicMetadataAsync(false, remote.Token).ConfigureAwait(false);
                var endpoint = new Uri(SiwcAuthorizationAttempt.Required(metadata, "revocation_endpoint"));
                if (endpoint.Scheme != "https" || endpoint.Host != "auth.openai.com" || endpoint.Port != 443 || endpoint.UserInfo.Length != 0)
                    throw new InvalidDataException("Untrusted SIWC revocation endpoint.");
                revoked = await transport.RevokeAsync(endpoint, new Dictionary<string, string> { ["token"] = current.RefreshToken,
                    ["token_type_hint"] = "refresh_token", ["client_id"] = current.ClientId }, remote.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or InvalidOperationException or OperationCanceledException or JsonException) { }
            return (new SiwcCredential { Subject = current.Subject, ClientId = current.ClientId, HostId = current.HostId,
                SignedOut = true, Revision = current.Revision + 1 }, revoked);
        }, CancellationToken.None); // Cancelling remote revocation must not cancel the durable local token removal.
    static SiwcCredential Copy(SiwcCredential c, long revision) => new() { Subject = c.Subject, ClientId = c.ClientId, HostId = c.HostId,
        AccessToken = c.AccessToken, RefreshToken = c.RefreshToken, IdToken = c.IdToken, Scopes = c.Scopes.ToArray(),
        ExpiresAt = c.ExpiresAt, EarliestRefreshAt = c.EarliestRefreshAt, Revision = revision, SignedOut = c.SignedOut };
}

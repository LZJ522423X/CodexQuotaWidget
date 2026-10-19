using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CodexQuotaWidget.Core.ControlCenter;

/// <summary>User-initiated loopback flow entry. Phase one cannot start a listener or launch a browser.</summary>
public static class SiwcInteractiveAuthorization
{
    public static async Task<SiwcCredential> RunAsync(string hostId, int port, string? issuedClient,
        ISiwcProtocolTransport transport, Action<Uri> openSystemBrowser, CancellationToken token = default, string? expectedSubject = null,
        SiwcPhaseTwoAccess? experimentalAccess = null)
    {
        if (experimentalAccess is null) SiwcPhaseOne.RequireLiveApproval(); else experimentalAccess.RequireAccountAccess();
        if (port is < 0 or > 65535 || port is > 0 and < 1024) throw new InvalidDataException("Invalid loopback port.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromMinutes(5));
        // Bind loopback directly; no HTTP.sys URL registration, administrator rights or system configuration changes.
        using var listener = new TcpListener(IPAddress.Loopback, port); listener.Start(2);
        try
        {
            var redirect = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/auth/callback");
            var attempt = new SiwcAuthorizationAttempt(hostId, redirect, issuedClient, expectedSubject);
            openSystemBrowser(attempt.AuthorizationUri);
            while (true)
            {
                using var peer = await listener.AcceptTcpClientAsync(timeout.Token).ConfigureAwait(false);
                await using var stream = peer.GetStream();
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token); requestTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                var header = new StringBuilder(); var buffer = new byte[1];
                while (header.Length < 4 || header[header.Length - 4] != '\r' || header[header.Length - 3] != '\n'
                    || header[header.Length - 2] != '\r' || header[header.Length - 1] != '\n')
                {
                    if (header.Length >= 16384 || await stream.ReadAsync(buffer, requestTimeout.Token).ConfigureAwait(false) == 0)
                        throw new InvalidDataException("SIWC loopback request incomplete or too large.");
                    header.Append((char)buffer[0]);
                }
                var callback = ValidateCallbackRequest(header.ToString(), redirect);
                if (callback is null) { await Reply(stream, false, requestTimeout.Token).ConfigureAwait(false); continue; }
                try
                {
                    var credential = await attempt.CompleteAsync(callback, transport, timeout.Token).ConfigureAwait(false);
                    await Reply(stream, true, requestTimeout.Token).ConfigureAwait(false); return credential;
                }
                catch { try { await Reply(stream, false, timeout.Token).ConfigureAwait(false); } catch (IOException) { } throw; }
            }
        }
        finally { listener.Stop(); }
    }
    public static Uri? ValidateCallbackRequest(string header, Uri redirect)
    {
        if (header.Length > 16384 || !header.EndsWith("\r\n\r\n", StringComparison.Ordinal) || header.Any(c => c > 127 || c == '\0'))
            throw new InvalidDataException("SIWC invalid loopback HTTP header.");
        var lines = header.Split("\r\n"); var request = lines[0].Split(' ');
        if (request.Length != 3 || request[0] != "GET" || request[2] != "HTTP/1.1" || !request[1].StartsWith('/') || request[1].StartsWith("//", StringComparison.Ordinal)) return null;
        var hosts = lines.Skip(1).Where(l => l.StartsWith("Host:", StringComparison.OrdinalIgnoreCase)).Select(l => l[5..].Trim()).ToArray();
        if (hosts.Length != 1 || hosts[0] != redirect.Authority || lines.Any(l => l.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase)
            || l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))) return null;
        if (!Uri.TryCreate(redirect, request[1], out var result) || result.GetLeftPart(UriPartial.Path) != redirect.AbsoluteUri || result.Fragment.Length != 0) return null;
        return result;
    }
    static Task Reply(Stream stream, bool success, CancellationToken token) => stream.WriteAsync(Encoding.ASCII.GetBytes(
        $"HTTP/1.1 {(success ? "200 OK" : "400 Bad Request")}\r\nContent-Length: 0\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n"), token).AsTask();
}

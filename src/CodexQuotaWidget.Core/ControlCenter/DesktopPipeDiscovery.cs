using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CodexQuotaWidget.Core.ControlCenter;

public enum ChatGptConnectionStatus { NotConnected, Connecting, Connected, Conflict, Failed }

/// <summary>Metadata only. No conversation text, token, or authentication material is retained.</summary>
public sealed record DesktopPipeCandidate(string PipePath, int? OwnerProcessId, bool IdentityVerified,
    string Reason, string? PackageFullName = null, bool TargetVerified = false, string ProbeCode = "not_probed", bool Selected = false)
{
    public string PipeName => PipePath[(PipePath.LastIndexOf('\\') + 1)..];
    public bool Verified => IdentityVerified && TargetVerified;
    public IReadOnlyDictionary<string, long> StageMilliseconds { get; init; } = new Dictionary<string, long>();
}

public sealed record DesktopPipeSelection(string? PipePath, string DiagnosticCode)
{
    /// <summary>Only already-probed routes to the exact configured target may be selected.</summary>
    public static DesktopPipeSelection Resolve(IReadOnlyList<DesktopPipeCandidate> candidates, string? requestedPipe = null)
    {
        var verified = candidates.Where(item => item.Verified && item.OwnerProcessId is > 0).ToArray();
        if (requestedPipe is not null)
            return verified.Any(item => item.PipePath == requestedPipe)
                ? new(requestedPipe, "explicit_target_route_verified") : new(null, "selected_pipe_not_verified");
        if (verified.Length == 0) return new(null, "desktop_target_unavailable");
        // Every route in this set independently passed read_thread(id) and caller-context validation.
        // Identical target + identical native owner is an equivalent transport, not another session.
        if (verified.Select(item => item.OwnerProcessId).Distinct().Count() != 1)
            return new(null, "desktop_pipe_not_unique");
        return new(verified.OrderBy(item => item.PipePath, StringComparer.Ordinal).First().PipePath,
            verified.Length == 1 ? "unique_target_route_verified" : "equivalent_target_routes_verified");
    }
}

public static class DesktopPipeDiscovery
{
    public const int MaximumCandidates = 32;
    const string Prefix = @"\\.\pipe\codex-browser-use-";

    public static bool IsSupportedPipe(string pipe) => pipe.StartsWith(Prefix, StringComparison.Ordinal)
        && Guid.TryParseExact(pipe[Prefix.Length..], "D", out _);

    public static IReadOnlyList<string> Enumerate()
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<string>();
        var candidates = Directory.EnumerateFiles(@"\\.\pipe\", "codex-browser-use-*")
            .Where(IsSupportedPipe).Take(MaximumCandidates + 1).OrderBy(path => path, StringComparer.Ordinal).ToList();
        var hint = Environment.GetEnvironmentVariable("CODEX_APP_TOOLS_PIPE_PATH");
        // A supplied path is merely another candidate, never permission to bypass identity verification.
        if (hint is not null && IsSupportedPipe(hint) && !candidates.Contains(hint, StringComparer.Ordinal)) candidates.Add(hint);
        return candidates;
    }

    public static async Task<DesktopPipeCandidate> InspectAsync(string pipe, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows() || !IsSupportedPipe(pipe)) return new(pipe, null, false, "desktop_pipe_unverified");
        try
        {
            using var client = new NamedPipeClientStream(".", pipe[(@"\\.\pipe\").Length..], PipeDirection.InOut, PipeOptions.Asynchronous);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            stop.CancelAfter(TimeSpan.FromSeconds(1));
            await client.ConnectAsync(stop.Token).ConfigureAwait(false);
            if (!GetNamedPipeServerProcessId(client.SafePipeHandle, out var owner) || owner > int.MaxValue)
                return new(pipe, null, false, "pipe_owner_unavailable");
            using var process = Process.GetProcessById((int)owner);
            using var caller = Process.GetCurrentProcess();
            if (process.SessionId != caller.SessionId) return new(pipe, (int)owner, false, "different_windows_session");
            var path = process.MainModule?.FileName;
            var windowsApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps") + "\\";
            if (path is null || !path.StartsWith(windowsApps, StringComparison.OrdinalIgnoreCase))
                return new(pipe, (int)owner, false, "owner_not_official_desktop");
            var relative = path[windowsApps.Length..].Split('\\');
            if (relative.Length != 3 || !relative[0].StartsWith("OpenAI.Codex_", StringComparison.Ordinal)
                || !relative[0].EndsWith("__2p2nqsd0c76g0", StringComparison.Ordinal) || relative[1] != "app"
                || !(relative[2].Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase) || relative[2].Equals("Codex.exe", StringComparison.OrdinalIgnoreCase)))
                return new(pipe, (int)owner, false, "owner_package_path_unverified");
            uint size = 0;
            if (GetPackageFullName(process.Handle, ref size, null) != 122 || size is 0 or > 2048)
                return new(pipe, (int)owner, false, "owner_package_unavailable");
            var package = new StringBuilder((int)size);
            if (GetPackageFullName(process.Handle, ref size, package) != 0 || package.ToString() != relative[0])
                return new(pipe, (int)owner, false, "owner_package_mismatch", package.ToString());
            if (!DesktopPipeSignature.IsOpenAiSigned(path))
                return new(pipe, (int)owner, false, "owner_signature_unverified", package.ToString());
            return new(pipe, (int)owner, true, "native_owner_package_signature_verified", package.ToString());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new(pipe, null, false, "stale_or_busy_pipe"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException)
        { return new(pipe, null, false, "pipe_owner_unavailable"); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetNamedPipeServerProcessId(SafePipeHandle handle, out uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int GetPackageFullName(IntPtr process, ref uint length, StringBuilder? name);
}

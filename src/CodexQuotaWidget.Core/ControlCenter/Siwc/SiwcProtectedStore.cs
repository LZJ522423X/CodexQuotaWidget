using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using CodexQuotaWidget.Core.Automation;

namespace CodexQuotaWidget.Core.ControlCenter;

/// <summary>Current-user DPAPI, owner-only D: directory, atomic ciphertext, cross-process rotation lease.</summary>
public sealed class SiwcProtectedStore : ISiwcCredentialStore
{
    readonly string directory;
    public SiwcProtectedStore(string dataRoot)
    {
        directory = Path.Combine(dataRoot, "siwc", "credentials");
        if (!AutomationStore.IsSafeDPath(directory)) throw new InvalidDataException("SIWC storage must be a regular D: path.");
    }
    public async Task<T> WithExclusiveAsync<T>(string accountId,
        Func<SiwcCredential?, CancellationToken, Task<(SiwcCredential? Replacement, T Result)>> operation, CancellationToken token = default)
    {
        if (accountId.Length != 64 || !accountId.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid SIWC account key.");
        EnsureDirectory();
        var path = Path.Combine(directory, accountId + ".dpapi");
        var lockPath = path + ".lock"; Validate(path); Validate(lockPath);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        FileStream? lease = null;
        while (lease is null)
        {
            timeout.Token.ThrowIfCancellationRequested(); Validate(lockPath);
            try { lease = new(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { await Task.Delay(25, timeout.Token).ConfigureAwait(false); }
        }
        using (lease)
        {
            Validate(path);
            SiwcCredential? value = null;
            if (File.Exists(path))
            {
                if (new FileInfo(path).Length is <= 0 or > 128 * 1024) throw new InvalidDataException("Invalid protected SIWC record.");
                var plain = UserProtection.Unprotect(File.ReadAllBytes(path));
                try { value = JsonSerializer.Deserialize<SiwcCredential>(plain) ?? throw new InvalidDataException("Empty protected SIWC record."); }
                finally { CryptographicOperations.ZeroMemory(plain); }
                if (value.AccountId != accountId) throw new InvalidDataException("SIWC registration identity mismatch.");
            }
            var result = await operation(value, timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            if (result.Replacement is { } replacement)
            {
                if (replacement.AccountId != accountId || replacement.Revision <= (value?.Revision ?? -1))
                    throw new InvalidDataException("SIWC rotation must preserve registration and increase revision.");
                var plain = JsonSerializer.SerializeToUtf8Bytes(replacement);
                byte[] encrypted;
                try { encrypted = UserProtection.Protect(plain); }
                finally { CryptographicOperations.ZeroMemory(plain); }
                var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp"; Validate(temp);
                try
                {
                    using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                    { stream.Write(encrypted); stream.Flush(true); }
                    Validate(path); File.Move(temp, path, true);
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
            return result.Result;
        }
    }
    public string GetOrCreateHostId()
    {
        EnsureDirectory(); var path = Path.Combine(directory, "host.json"); Validate(path);
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(stream, "urn:uuid:" + Guid.NewGuid().ToString("D")); stream.Flush(true);
        }
        catch (IOException) when (File.Exists(path)) { }
        if (new FileInfo(path).Length > 128) throw new InvalidDataException("Invalid SIWC host registration.");
        var host = JsonSerializer.Deserialize<string>(File.ReadAllText(path));
        return host is not null && host.StartsWith("urn:uuid:", StringComparison.Ordinal) && Guid.TryParse(host[9..], out _)
            ? host : throw new InvalidDataException("Invalid SIWC host identity.");
    }
    void EnsureDirectory()
    {
        Validate(directory); Directory.CreateDirectory(directory); Validate(directory);
        var sid = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("No current Windows user.");
        // A current-user app must not request WRITE_OWNER or elevation just to protect its own directory.
        if (!sid.Equals(new DirectoryInfo(directory).GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier))))
            throw new UnauthorizedAccessException("SIWC credential directory is not owned by the current user.");
        var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false);
        acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(acl);
    }
    static void Validate(string path)
    {
        if (!AutomationStore.IsSafeDPath(path)) throw new InvalidDataException("SIWC storage path changed or is a reparse point.");
    }
    static class UserProtection
    {
        [StructLayout(LayoutKind.Sequential)] struct Blob { public int Length; public IntPtr Data; }
        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool CryptProtectData(ref Blob input, string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
        [DllImport("crypt32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
        [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr pointer);
        public static byte[] Protect(byte[] bytes) => Transform(bytes, true);
        public static byte[] Unprotect(byte[] bytes) => Transform(bytes, false);
        static byte[] Transform(byte[] bytes, bool encrypt)
        {
            var input = new Blob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) }; Blob output = default;
            try
            {
                Marshal.Copy(bytes, 0, input.Data, bytes.Length);
                var ok = encrypt ? CryptProtectData(ref input, "AI Control Center SIWC", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                    : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
                if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error(), "SIWC current-user credential protection failed.");
                var result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
            }
            finally
            {
                Marshal.Copy(new byte[input.Length], 0, input.Data, input.Length); Marshal.FreeHGlobal(input.Data);
                if (output.Data != IntPtr.Zero) { Marshal.Copy(new byte[output.Length], 0, output.Data, output.Length); LocalFree(output.Data); }
            }
        }
    }
}

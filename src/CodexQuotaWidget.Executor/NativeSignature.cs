using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace CodexQuotaWidget.Executor;

/// <summary>Authenticode validation via Windows APIs only. No PowerShell, CLI, network or downloaded runtime.</summary>
internal static class NativeSignature
{
    const uint SignerCertificateInfoParameter = 7; // CMSG_SIGNER_CERT_INFO_PARAM returns CERT_INFO, not CMSG_SIGNER_INFO (6).
    const uint FindSubjectCertificate = 0xB0000; // CERT_FIND_SUBJECT_CERT, matches CERT_INFO issuer and serial.
    static readonly Dictionary<string, (long Length, DateTime Write, bool Valid)> Cache = new(StringComparer.OrdinalIgnoreCase);
    internal static bool IsMicrosoftSigned(string path)
    {
        try { return VerifyTrust(path) && ReadOrganization(path).Equals("Microsoft Corporation", StringComparison.OrdinalIgnoreCase); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }
    public static bool IsOpenAiSigned(string path)
    {
        try
        {
            var file = new FileInfo(path);
            lock (Cache)
            {
                if (Cache.TryGetValue(path, out var cached) && cached.Length == file.Length && cached.Write == file.LastWriteTimeUtc) return cached.Valid;
                var valid = VerifyTrust(path) && ReadOrganization(path).StartsWith("OpenAI", StringComparison.OrdinalIgnoreCase);
                Cache[path] = (file.Length, file.LastWriteTimeUtc, valid);
                return valid;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    static bool VerifyTrust(string path)
    {
        var info = new TrustFileInfo { Size = (uint)Marshal.SizeOf<TrustFileInfo>(), FilePath = path };
        var memory = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFileInfo>());
        Marshal.StructureToPtr(info, memory, false);
        var data = new TrustData
        {
            Size = (uint)Marshal.SizeOf<TrustData>(), UiChoice = 2, UnionChoice = 1, File = memory,
            StateAction = 1, ProviderFlags = 0x1000 | 0x10 // Cache-only URL retrieval; no online revocation request.
        };
        var policy = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
        try { return WinVerifyTrust(new IntPtr(-1), ref policy, ref data) == 0; }
        finally
        {
            data.StateAction = 2; WinVerifyTrust(new IntPtr(-1), ref policy, ref data);
            Marshal.DestroyStructure<TrustFileInfo>(memory); Marshal.FreeHGlobal(memory);
        }
    }

    static string ReadOrganization(string path)
    {
        if (!CryptQueryObject(1, path, 0x400, 2, 0, out _, out _, out _, out var store, out var message, out _)) return "";
        IntPtr certificate = IntPtr.Zero, signerInfo = IntPtr.Zero;
        try
        {
            // Select the actual signer by issuer/serial. An unrelated bundled certificate cannot authorize a process.
            uint size = 0;
            if (!CryptMsgGetParam(message, SignerCertificateInfoParameter, 0, IntPtr.Zero, ref size) || size is 0 or >65536) return "";
            signerInfo = Marshal.AllocHGlobal((int)size);
            if (!CryptMsgGetParam(message, SignerCertificateInfoParameter, 0, signerInfo, ref size)) return "";
            certificate = CertFindCertificateInStore(store, 0x10001, 0, FindSubjectCertificate, signerInfo, IntPtr.Zero);
            if (certificate == IntPtr.Zero) return "";
            var name = new StringBuilder(512);
            var oid = Marshal.StringToHGlobalAnsi("2.5.4.10");
            try { CertGetNameString(certificate, 3, 0, oid, name, (uint)name.Capacity); }
            finally { Marshal.FreeHGlobal(oid); }
            return name.ToString();
        }
        finally
        {
            if (certificate != IntPtr.Zero) CertFreeCertificateContext(certificate);
            if (signerInfo != IntPtr.Zero) Marshal.FreeHGlobal(signerInfo);
            if (message != IntPtr.Zero) CryptMsgClose(message);
            if (store != IntPtr.Zero) CertCloseStore(store, 0);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct TrustFileInfo { public uint Size; [MarshalAs(UnmanagedType.LPWStr)] public string FilePath; public IntPtr FileHandle, KnownSubject; }
    [StructLayout(LayoutKind.Sequential)] struct TrustData { public uint Size; public IntPtr PolicyCallback, SipClient; public uint UiChoice, RevocationChecks, UnionChoice; public IntPtr File; public uint StateAction; public IntPtr StateData, UrlReference; public uint ProviderFlags, UiContext; public IntPtr SignatureSettings; }
    [DllImport("wintrust.dll", ExactSpelling = true)] static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool CryptQueryObject(uint type, string path, uint contentFlags, uint formatFlags, uint flags, out uint encoding, out uint content, out uint format, out IntPtr store, out IntPtr message, out IntPtr context);
    [DllImport("crypt32.dll")] static extern IntPtr CertFindCertificateInStore(IntPtr store, uint encoding, uint flags, uint type, IntPtr parameter, IntPtr previous);
    [DllImport("crypt32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool CryptMsgGetParam(IntPtr message, uint type, uint index, IntPtr value, ref uint size);
    [DllImport("crypt32.dll", EntryPoint = "CertGetNameStringW", CharSet = CharSet.Unicode)] static extern uint CertGetNameString(IntPtr certificate, uint type, uint flags, IntPtr parameter, StringBuilder text, uint length);
    [DllImport("crypt32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool CertFreeCertificateContext(IntPtr certificate);
    [DllImport("crypt32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool CertCloseStore(IntPtr store, uint flags);
    [DllImport("crypt32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool CryptMsgClose(IntPtr message);
}

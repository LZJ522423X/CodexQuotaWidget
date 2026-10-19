using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using CodexQuotaWidget.Executor;
using CodexQuotaWidget.Core.Startup;

namespace CodexQuotaWidget.StartupWatcher;

public record DesktopEvidence(int Pid, string? Path, string? Package, bool Accepted, string Reason)
{
    public long? StartTimeUtcTicks { get; init; }
    public string? Role { get; init; }
    public bool DesktopInstanceVerified { get; init; }
}
public static class DesktopIdentity
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int GetPackageFullName(IntPtr process, ref uint length, StringBuilder? name);
    [StructLayout(LayoutKind.Sequential)] struct UnicodeString { public ushort Length, MaximumLength; public IntPtr Buffer; }
    [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(IntPtr process, int informationClass,
        IntPtr information, int informationLength, out int returnLength);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CommandLineToArgvW(string commandLine, out int count);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);
    public static IReadOnlyList<DesktopEvidence> Observe()
    {
        var results = new List<DesktopEvidence>();
        foreach(var name in new[] { "Codex", "ChatGPT" })
            foreach(var process in Process.GetProcessesByName(name))
                using(process) results.Add(Inspect(process));
        return results;
    }
    public static DesktopPresenceObservation ReadPresence()
    {
        var evidence = Observe();
        var count = evidence.Count(item => item.DesktopInstanceVerified);
        return count > 0 ? new(DesktopPresenceState.Running, count)
            : new(evidence.Any(item => item.Role == "Unknown") ? DesktopPresenceState.Unknown : DesktopPresenceState.Absent);
    }
    static DesktopEvidence Inspect(Process process)
    {
        try
        {
            using var observer=Process.GetCurrentProcess();
            if(process.SessionId!=observer.SessionId) return new(process.Id,null,null,false,"不是当前 Windows 登录会话的进程。");
            var path = process.MainModule?.FileName;
            var windowsApps = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps") + "\\";
            if(path is null)
                return new(process.Id, null, null, false, "无法读取候选进程路径，Desktop 状态保持未知。") { Role = "Unknown" };
            if(!path.StartsWith(windowsApps, StringComparison.OrdinalIgnoreCase))
                return new(process.Id, path, null, false, "不是已验证的 Codex Desktop 安装路径（CLI/同名程序不触发）。");
            var relative = path[windowsApps.Length..].Split('\\');
            if(relative.Length != 3 || !relative[0].StartsWith("OpenAI.Codex_", StringComparison.Ordinal)
                || !relative[0].EndsWith("__2p2nqsd0c76g0", StringComparison.Ordinal) || relative[1] != "app"
                || !(relative[2].Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase) || relative[2].Equals("Codex.exe", StringComparison.OrdinalIgnoreCase)))
                return new(process.Id, path, null, false, "安装结构或发布者身份不匹配。");
            uint size = 0;
            if(GetPackageFullName(process.Handle, ref size, null) != 122 || size > 2048 || size == 0)
                return new(process.Id, path, null, false, "无法确认运行进程的 MSIX 身份。") { Role = "Unknown" };
            var package = new StringBuilder((int)size);
            if(GetPackageFullName(process.Handle, ref size, package) != 0 || package.ToString() != relative[0])
                return new(process.Id, path, package.ToString(), false, "运行包身份与磁盘路径不一致。") { Role = "Unknown" };
            // Helpers also retain Desktop presence until fully exited, but never become startup roots.
            if(!NativeSignature.IsOpenAiSigned(path))
                return new(process.Id, path, package.ToString(), false, "OpenAI Authenticode 签名验证未通过。") { Role = "Unknown" };
            // Electron uses the same signed packaged executable for its renderer/GPU/crash helpers.
            // Read only this verified-path process's command line for role classification, never log it.
            if(!TryIsMainProcess(process.Handle, out var main))
                return new(process.Id, path, package.ToString(), false, "无法可靠读取 Desktop 主进程身份；本轮不触发启动。") { Role = "Unknown", DesktopInstanceVerified = !process.HasExited };
            if(!main)
                return new(process.Id, path, package.ToString(), false, "Electron 辅助进程，不构成新的 Codex Desktop 启动周期。") { Role = "Helper", DesktopInstanceVerified = !process.HasExited };
            var started = process.StartTime.ToUniversalTime().Ticks;
            if(process.HasExited) return new(process.Id, path, package.ToString(), false, "Desktop 主进程在身份核验期间已退出。") { Role = "Unknown" };
            return new(process.Id, path, package.ToString(), true, "Desktop 主进程、原生包身份、WindowsApps 路径与 OpenAI 签名均已验证。")
                { StartTimeUtcTicks = started, Role = "Main", DesktopInstanceVerified = true };
        }
        catch(Exception error) when(error is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or IOException)
        { return new(process.Id, null, null, false, "进程已退出或身份不可读：" + error.GetType().Name) { Role = "Unknown" }; }
    }

    static bool TryIsMainProcess(IntPtr process, out bool main)
    {
        main = false;
        // ProcessCommandLineInformation returns a UNICODE_STRING whose buffer is in the supplied block.
        const int processCommandLineInformation = 60;
        NtQueryInformationProcess(process, processCommandLineInformation, IntPtr.Zero, 0, out var required);
        if(required < Marshal.SizeOf<UnicodeString>() || required > 128 * 1024) return false;
        var block = Marshal.AllocHGlobal(required);
        try
        {
            if(NtQueryInformationProcess(process, processCommandLineInformation, block, required, out var actual) != 0 || actual > required) return false;
            var text = Marshal.PtrToStructure<UnicodeString>(block);
            var offset = text.Buffer.ToInt64() - block.ToInt64();
            if(text.Length == 0 || text.Length % 2 != 0 || offset < 0 || offset + text.Length > required) return false;
            var commandLine = Marshal.PtrToStringUni(text.Buffer, text.Length / 2);
            if(string.IsNullOrWhiteSpace(commandLine)) return false;
            var arguments = CommandLineToArgvW(commandLine, out var count);
            if(arguments == IntPtr.Zero) return false;
            try
            {
                if(count < 1 || count > 4096) return false;
                for(var index = 1; index < count; index++)
                {
                    var argument = Marshal.PtrToStringUni(Marshal.ReadIntPtr(arguments, index * IntPtr.Size));
                    if(argument is not null && (argument.Equals("--type", StringComparison.OrdinalIgnoreCase)
                        || argument.StartsWith("--type=", StringComparison.OrdinalIgnoreCase))) return true;
                }
                main = true; return true;
            }
            finally { LocalFree(arguments); }
        }
        finally { Marshal.FreeHGlobal(block); }
    }
}

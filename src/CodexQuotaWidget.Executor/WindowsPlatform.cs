using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using CodexQuotaWidget.Core.Automation;

namespace CodexQuotaWidget.Executor;

public sealed record PlatformActionResult(bool Success, string Message, IReadOnlyList<string>? Targets = null, bool ServiceAccessDenied = false);

/// <summary>Injectable platform boundary. Tests never invoke the real Windows power adapter.</summary>
public interface ILocalPlatform
{
    Task<PlatformActionResult> PerformAsync(LocalAction action, bool dryRun, CancellationToken cancellationToken);
}

public sealed class WindowsPlatform : ILocalPlatform
{
    bool protectedService;
    public async Task<PlatformActionResult> PerformAsync(LocalAction action, bool dryRun, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (action.Kind == LocalActionKind.Wait)
        {
            if (!dryRun) await Task.Delay(TimeSpan.FromSeconds(action.Seconds), cancellationToken);
            return new(true, dryRun ? $"模拟等待 {action.Seconds} 秒" : $"已等待 {action.Seconds} 秒");
        }
        if (action.Kind is LocalActionKind.StopWindowsService or LocalActionKind.VerifyServiceStopped)
            return await ServiceActionAsync(action, dryRun, cancellationToken);
        if (action.Kind is LocalActionKind.ShutdownWindows or LocalActionKind.RestartWindows or LocalActionKind.SleepWindows or LocalActionKind.HibernateWindows)
        {
            if (dryRun) return new(true, "已到达电源步骤；测试模式禁止执行电源操作", [action.Kind.ToString()]);
            if (protectedService && action.Kind is LocalActionKind.SleepWindows or LocalActionKind.HibernateWindows)
                return new(false, "受保护 Clash 服务未停止，阻止睡眠/休眠");
            return await PowerAsync(action.Kind, cancellationToken);
        }
        var discovery = await DiscoverAsync(action.Target, cancellationToken);
        var processes = discovery.Processes;
        var targets = processes.Select(x => $"{action.Target}:PID={x.Id}").Concat(discovery.Unverified).ToArray();
        try
        {
            if (dryRun) return new(true, discovery.Unverified.Count > 0 ? "存在同名但无法核验路径的进程；正式执行会暂停，测试不关闭" : processes.Count == 0 ? "目标未运行；测试校验通过" : $"已确认 {processes.Count} 个目标；测试模式不关闭", targets);
            if (discovery.Unverified.Count > 0) return new(false, "目标进程来源无法核验，已阻止电源操作", targets);
            if (action.Kind == LocalActionKind.VerifyProcessStopped)
            {
                if (processes.Count == 0) return new(true, "目标进程已停止", targets);
                if (action.Target == LocalTarget.Mihomo && protectedService)
                    return new(true, "受保护 mihomo 仍运行；正常 Windows 关机将交由服务管理器停止", targets, true);
                return new(false, "目标进程仍在运行", targets);
            }
            foreach (var process in processes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { if (process.HasExited) continue; }
                catch (Exception error) when (error is Win32Exception or InvalidOperationException)
                {
                    if (ProcessExistsInSnapshot(process.Id) == false) continue;
                    if (action.Target == LocalTarget.Mihomo && protectedService)
                        return new(true, "已核验受保护 mihomo 的服务归属；普通用户无法查询进程句柄，正常关机交由 SCM 停止", targets, true);
                    return new(false, "无法核验目标进程的运行状态；已阻止电源操作", targets);
                }
                if (action.Kind == LocalActionKind.CloseApplicationGracefully)
                {
                    if (process.MainWindowHandle == IntPtr.Zero) continue;
                    process.CloseMainWindow();
                    // A save prompt or an IDE without a normal close path blocks the power step.
                    if (action.Target is LocalTarget.VisualStudio or LocalTarget.VSCode or LocalTarget.Rider)
                    {
                        try { await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(15), cancellationToken); }
                        catch (TimeoutException) { return new(false, "开发工具未正常关闭，可能存在未保存编辑；已阻止电源操作", targets); }
                    }
                }
                else if (action.Kind == LocalActionKind.CloseProcess)
                {
                    if (action.Target is LocalTarget.VisualStudio or LocalTarget.VSCode or LocalTarget.Rider)
                        return new(false, "禁止强制结束开发工具", targets);
                    try
                    {
                        process.Kill();
                        await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
                    }
                    catch (Exception error) when ((error is Win32Exception or InvalidOperationException) && ProcessExistsInSnapshot(process.Id) == false) { }
                    catch (Win32Exception) when (action.Target == LocalTarget.Mihomo && protectedService)
                    { return new(true, "受保护 mihomo 等待 Windows 正常关机停止", targets, true); }
                }
                else return new(false, "不支持的进程动作", targets);
            }
            return new(true, action.Kind == LocalActionKind.CloseApplicationGracefully ? "已发送正常关闭请求" : "已停止已验证目标", targets);
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    public static async Task<IReadOnlyList<string>> InspectAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<string>();
        foreach (var target in Enum.GetValues<LocalTarget>().Where(x => x is not (LocalTarget.None or LocalTarget.ClashService)))
        {
            var found = await DiscoverAsync(target, cancellationToken);
            result.AddRange(found.Processes.Select(x => $"{target}:PID={x.Id}")); result.AddRange(found.Unverified);
            foreach (var process in found.Processes) process.Dispose();
        }
        var service = QueryService();
        result.Add(service is null ? "ClashService:未安装" : $"ClashService:PID={service.Value.ProcessId};State={service.Value.CurrentState}");
        return result;
    }

    sealed record ProcessDiscovery(List<Process> Processes, IReadOnlyList<string> Unverified);
    static Task<ProcessDiscovery> DiscoverAsync(LocalTarget target, CancellationToken cancellationToken)
    {
        var names = target switch
        {
            LocalTarget.CodexDesktop => new[] { "ChatGPT", "Codex" },
            LocalTarget.CodexCli => ["codex", "codex-code-mode-host", "codex-computer-use-swift"],
            LocalTarget.VisualStudio => ["devenv"], LocalTarget.VSCode => ["Code"], LocalTarget.Rider => ["rider64", "rider"],
            LocalTarget.ClashVerge => ["clash-verge"], LocalTarget.Mihomo => ["mihomo", "verge-mihomo"], _ => []
        };
        var result = new List<Process>(); var unverified = new List<string>();
        var service = target == LocalTarget.Mihomo ? QueryService() : null;
        foreach (var name in names)
            foreach (var process in Process.GetProcessesByName(name))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    // Process.HasExited requires a process handle and fails for protected service children.
                    // Resolve the allowed path or service-parent relationship before attempting any handle operation.
                    if (process.Id == Environment.ProcessId) { process.Dispose(); continue; }
                    string? path;
                    try { path = process.MainModule?.FileName; } catch (Exception error) when (error is Win32Exception or UnauthorizedAccessException) { path = null; }
                    bool verified = target switch
                    {
                        LocalTarget.CodexDesktop => path is not null && Regex.IsMatch(path, @"^C:\\Program Files\\WindowsApps\\OpenAI\.Codex_[^\\]+\\app\\(?:ChatGPT|Codex)\.exe$", RegexOptions.IgnoreCase) && NativeSignature.IsOpenAiSigned(path),
                        LocalTarget.CodexCli => path is not null && Regex.IsMatch(path, @"\\OpenAI\\Codex\\(?:bin|runtimes)\\", RegexOptions.IgnoreCase) && NativeSignature.IsOpenAiSigned(path),
                        LocalTarget.VisualStudio => path is not null && Regex.IsMatch(path, @"\\Microsoft Visual Studio\\[^\\]+\\[^\\]+\\Common7\\IDE\\devenv\.exe$", RegexOptions.IgnoreCase),
                        LocalTarget.VSCode => path is not null && Regex.IsMatch(path, @"\\(?:Microsoft VS Code|VS Code)\\Code\.exe$", RegexOptions.IgnoreCase),
                        LocalTarget.Rider => path is not null && Regex.IsMatch(path, @"\\JetBrains\\[^\\]+\\bin\\rider(?:64)?\.exe$", RegexOptions.IgnoreCase),
                        LocalTarget.ClashVerge => path?.Equals(@"D:\CLASH\Clash Verge\clash-verge.exe", StringComparison.OrdinalIgnoreCase) == true,
                        LocalTarget.Mihomo => path is not null && path.StartsWith(@"D:\CLASH\Clash Verge\", StringComparison.OrdinalIgnoreCase) || service is { ProcessId: > 0 } known && GetParentProcessId(process.Id) == known.ProcessId,
                        _ => false
                    };
                    if (verified) result.Add(process);
                    else
                    {
                        // The standalone ChatGPT application has the same exe name but is not a Codex target.
                        var knownDifferentChatGpt = target == LocalTarget.CodexDesktop && path is not null && path.Contains(@"\WindowsApps\OpenAI.ChatGPT_", StringComparison.OrdinalIgnoreCase);
                        // Windows process-name lookup is case-insensitive: Codex also matches codex.exe CLI.
                        var knownCodexCli = target == LocalTarget.CodexDesktop && path is not null
                            && Regex.IsMatch(path, @"\\OpenAI\\Codex\\(?:bin|runtimes)\\", RegexOptions.IgnoreCase)
                            && Path.GetFileName(path).Equals("codex.exe", StringComparison.OrdinalIgnoreCase) && NativeSignature.IsOpenAiSigned(path);
                        if (!knownDifferentChatGpt && !knownCodexCli) unverified.Add($"{target}:PID={process.Id};UnverifiedOrigin");
                        process.Dispose();
                    }
                }
                catch (InvalidOperationException) { process.Dispose(); } // Already exited during enumeration.
                catch (Exception e) when (e is Win32Exception or IOException or UnauthorizedAccessException)
                { unverified.Add($"{target}:PID={process.Id};UnverifiedMetadata"); process.Dispose(); }
            }
        return Task.FromResult(new ProcessDiscovery(result, unverified));
    }

    async Task<PlatformActionResult> ServiceActionAsync(LocalAction action, bool dryRun, CancellationToken cancellationToken)
    {
        if (action.Target != LocalTarget.ClashService) return new(false, "服务目标不在白名单");
        var status = QueryService();
        if (status is null) return new(true, "Clash 服务未安装");
        var targets = new[] { $"clash_verge_service:PID={status.Value.ProcessId};State={status.Value.CurrentState}" };
        if (dryRun) return new(true, "已确认 Clash 服务；测试模式不停止", targets);
        if (action.Kind == LocalActionKind.VerifyServiceStopped)
            return status.Value.CurrentState == 1 ? new(true, "Clash 服务已停止", targets) : protectedService
                ? new(true, "Clash 服务受权限保护；Windows 正常关机时由 SCM 停止", targets, true)
                : new(false, "Clash 服务仍运行", targets);
        if (status.Value.CurrentState == 1) return new(true, "Clash 服务已停止", targets);
        var scm = OpenSCManager(null, null, 1);
        if (scm == IntPtr.Zero) return new(false, "无法连接 Windows 服务管理器", targets);
        try
        {
            var service = OpenService(scm, "clash_verge_service", 0x20 | 4);
            if (service == IntPtr.Zero)
            {
                if (Marshal.GetLastWin32Error() == 5) { protectedService = true; return new(true, "停止服务需要管理员权限；正常关机将由 SCM 停止", targets, true); }
                return new(false, "无法打开 Clash 服务", targets);
            }
            try
            {
                if (!ControlService(service, 1, out _))
                {
                    if (Marshal.GetLastWin32Error() == 5) { protectedService = true; return new(true, "Clash 服务拒绝普通用户停止；正常关机将由 SCM 停止", targets, true); }
                    return new(false, "停止 Clash 服务失败", targets);
                }
                var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
                while (DateTimeOffset.UtcNow < deadline)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var current = QueryService();
                    if (current is null || current.Value.CurrentState == 1) return new(true, "Clash 服务已停止", targets);
                    await Task.Delay(250, cancellationToken);
                }
                return new(false, "Clash 服务未在 15 秒内停止", targets);
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(scm); }
    }

    static async Task<PlatformActionResult> PowerAsync(LocalActionKind kind, CancellationToken cancellationToken)
    {
        if (kind is LocalActionKind.SleepWindows or LocalActionKind.HibernateWindows)
            return SuspendWithLocalPrivilege(kind == LocalActionKind.HibernateWindows) ? new(true, "Windows 已接受睡眠/休眠请求") : new(false, "Windows 拒绝睡眠/休眠请求或当前用户没有相应权限");
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shutdown.exe")) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(kind == LocalActionKind.RestartWindows ? "/r" : "/s"); start.ArgumentList.Add("/t"); start.ArgumentList.Add("0");
        // Intentionally no /f: applications with unsaved work can block Windows shutdown.
        using var process = Process.Start(start);
        if (process is null) return new(false, "Windows 电源请求未启动");
        await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
        return process.ExitCode == 0 ? new(true, "Windows 已接受正常关机/重启请求（不强制关闭未保存应用）") : new(false, "Windows 拒绝关机/重启请求");
    }

    static bool SuspendWithLocalPrivilege(bool hibernate)
    {
        // Only the executor's own token is adjusted, and restored. No UAC prompt or system policy change.
        if (!OpenProcessToken(new IntPtr(-1), 0x20 | 8, out var token)) return false;
        var enabled = false; var previous = new TokenPrivileges();
        try
        {
            if (!LookupPrivilegeValue(null, "SeShutdownPrivilege", out var luid)) return false;
            var requested = new TokenPrivileges { Count = 1, Id = luid, Attributes = 2 };
            if (!AdjustTokenPrivileges(token, false, ref requested, Marshal.SizeOf<TokenPrivileges>(), out previous, out _) || Marshal.GetLastWin32Error() == 1300) return false;
            enabled = true;
            return SetSuspendState(hibernate, false, false);
        }
        finally
        {
            if (enabled) RestoreTokenPrivileges(token, false, ref previous, 0, IntPtr.Zero, IntPtr.Zero);
            CloseHandle(token);
        }
    }

    static ServiceStatusProcess? QueryService()
    {
        var scm = OpenSCManager(null, null, 1);
        if (scm == IntPtr.Zero) throw new InvalidDataException("Windows 服务管理器不可访问，不能确认 Clash 服务状态");
        try
        {
            var service = OpenService(scm, "clash_verge_service", 4);
            if (service == IntPtr.Zero)
            {
                if (Marshal.GetLastWin32Error() == 1060) return null;
                throw new InvalidDataException("Clash 服务状态无法核验，拒绝视为未安装");
            }
            try
            {
                if (!QueryServiceStatusEx(service, 0, out var status, Marshal.SizeOf<ServiceStatusProcess>(), out _)) throw new InvalidDataException("Clash 服务状态查询失败");
                return status;
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(scm); }
    }

    static uint GetParentProcessId(int id)
    {
        var handle = CreateToolhelp32Snapshot(2, 0);
        if (handle == new IntPtr(-1)) return 0;
        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>(), ExeFile = "" };
            if (!Process32First(handle, ref entry)) return 0;
            do { if (entry.ProcessId == id) return entry.ParentProcessId; } while (Process32Next(handle, ref entry));
            return 0;
        }
        finally { CloseHandle(handle); }
    }

    static bool? ProcessExistsInSnapshot(int id)
    {
        var handle = CreateToolhelp32Snapshot(2, 0);
        if (handle == new IntPtr(-1)) return null;
        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>(), ExeFile = "" };
            if (!Process32First(handle, ref entry)) return Marshal.GetLastWin32Error() == 18 ? false : null;
            do { if (entry.ProcessId == id) return true; } while (Process32Next(handle, ref entry));
            return Marshal.GetLastWin32Error() == 18 ? false : null;
        }
        finally { CloseHandle(handle); }
    }

    [StructLayout(LayoutKind.Sequential)] struct ServiceStatus { public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint; }
    [StructLayout(LayoutKind.Sequential)] struct Luid { public uint LowPart; public int HighPart; }
    [StructLayout(LayoutKind.Sequential)] struct TokenPrivileges { public uint Count; public Luid Id; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] struct ServiceStatusProcess { public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint, ProcessId, ServiceFlags; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct ProcessEntry32 { public uint Size, Usage, ProcessId; public UIntPtr DefaultHeapId; public uint ModuleId, Threads, ParentProcessId; public int BasePriority; public uint Flags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr OpenService(IntPtr scm, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool QueryServiceStatusEx(IntPtr service, int level, out ServiceStatusProcess status, int size, out int needed);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool ControlService(IntPtr service, uint control, out ServiceStatus status);
    [DllImport("advapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool CloseServiceHandle(IntPtr handle);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool AdjustTokenPrivileges(IntPtr token, [MarshalAs(UnmanagedType.Bool)] bool disableAll, ref TokenPrivileges state, int length, out TokenPrivileges previous, out int returnedLength);
    [DllImport("advapi32.dll", EntryPoint = "AdjustTokenPrivileges", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool RestoreTokenPrivileges(IntPtr token, [MarshalAs(UnmanagedType.Bool)] bool disableAll, ref TokenPrivileges state, int length, IntPtr previous, IntPtr returnedLength);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint id);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool CloseHandle(IntPtr handle);
    [DllImport("powrprof.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] static extern bool SetSuspendState([MarshalAs(UnmanagedType.U1)] bool hibernate, [MarshalAs(UnmanagedType.U1)] bool forceCritical, [MarshalAs(UnmanagedType.U1)] bool disableWakeEvent);
}

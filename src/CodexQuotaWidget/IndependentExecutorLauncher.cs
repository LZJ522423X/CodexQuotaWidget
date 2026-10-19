using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using CodexQuotaWidget.Core.Automation;

namespace CodexQuotaWidget;

/// <summary>Breaks out of the parent's job; Explorer is the independent local fallback broker.</summary>
internal static class IndependentExecutorLauncher
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct StartupInfo
    {
        public int Size; public string? Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2Size; public IntPtr Reserved2, StdInput, StdOutput, StdError;
    }
    [StructLayout(LayoutKind.Sequential)] struct ProcessInformation { public IntPtr Process, Thread; public int ProcessId, ThreadId; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcess(string application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);

    internal static string Launch(string executable, string requestPath, bool execute)
    {
        if (!AutomationStore.IsSafeDPath(executable) || !AutomationStore.IsSafeDPath(requestPath) || !File.Exists(executable) || !File.Exists(requestPath))
            throw new IOException("独立 Executor 或请求文件缺失，或路径含异常链接");
        var arguments = "--request " + Quote(requestPath) + (execute ? " --execute" : "");
        return LaunchDetached(executable, arguments);
    }
    internal static string LaunchManualAcceptance(string executable, string requestPath, bool execute)
    {
        if (!AutomationStore.IsSafeDPath(executable) || !AutomationStore.IsSafeDPath(requestPath) || !File.Exists(executable) || !File.Exists(requestPath))
            throw new IOException("验收 Executor 或请求文件缺失，或路径含异常链接");
        var arguments = "--manual-acceptance-test --request " + Quote(requestPath) + (execute ? " --execute" : "");
        if (!execute) return LaunchDetached(executable, arguments);
        // AppInfo launches the one-shot executor with an administrator token for service-stop access.
        // Its fixed acceptance plan limits actions; no service permissions or background component persist.
        using var process = Process.Start(new ProcessStartInfo(executable, arguments)
        {
            UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        }) ?? throw new InvalidOperationException("实机验收执行器没有启动");
        return "ElevatedShell:" + process.Id;
    }
    static string LaunchDetached(string executable, string arguments)
    {
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Flags = 1, ShowWindow = 0 };
        // Never inherit handles or create a kill-on-close job for this child.
        if (CreateProcess(executable, new StringBuilder(Quote(executable) + " " + arguments), IntPtr.Zero, IntPtr.Zero, false,
            0x01000000 | 0x08000000, IntPtr.Zero, Path.GetDirectoryName(executable)!, ref startup, out var info))
        {
            CloseHandle(info.Thread); CloseHandle(info.Process); return "Breakaway:" + info.ProcessId;
        }
        var error = Marshal.GetLastWin32Error();
        if (error is not (5 or 87)) throw new Win32Exception(error, "独立 Executor 启动失败");
        object? shell = null, windows = null, desktop = null, document = null, broker = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!);
            windows = ((dynamic)shell!).Windows();
            object hwnd = 0;
            desktop = ((dynamic)windows!).FindWindowSW(0, 0, 8, ref hwnd, 1);
            if (desktop is null) throw new InvalidOperationException("无法取得独立桌面启动器");
            document = ((dynamic)desktop).Document; broker = ((dynamic)document).Application;
            ((dynamic)broker).ShellExecute(executable, arguments, Path.GetDirectoryName(executable), "open", 0);
            return "ExplorerBroker";
        }
        catch (Exception e) when (e is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or TypeLoadException or MissingMethodException)
        { throw new InvalidOperationException("独立桌面启动器无法接管；未请求电源操作", e); }
        finally
        {
            var released=new System.Collections.Generic.HashSet<object>(System.Collections.Generic.ReferenceEqualityComparer.Instance);
            foreach (var item in new[] { broker, document, desktop, windows, shell })
                if (item is not null && released.Add(item) && Marshal.IsComObject(item)) Marshal.FinalReleaseComObject(item);
        }
    }
    internal static string Quote(string value)
    {
        var text = new StringBuilder("\""); var slashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') { text.Append('\\', slashes * 2 + 1).Append(c); slashes = 0; continue; }
            text.Append('\\', slashes).Append(c); slashes = 0;
        }
        return text.Append('\\', slashes * 2).Append('"').ToString();
    }
}

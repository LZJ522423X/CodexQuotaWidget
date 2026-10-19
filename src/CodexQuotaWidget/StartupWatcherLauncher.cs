using System;
using System.IO;
using System.Runtime.InteropServices;
using CodexQuotaWidget.Core.Automation;
using CodexQuotaWidget.Core.Startup;

namespace CodexQuotaWidget;

/// <summary>Windows desktop Explorer owns creation, independently of the widget/development host.</summary>
internal static class StartupWatcherLauncher
{
    internal static string EnsureRunning(string executable, string dataRoot, string widget)
    {
        if(StartupWatcherControl.IsRunning) return "AlreadyRunning";
        foreach(var path in new[] { executable, dataRoot, widget })
            if(!AutomationStore.IsSafeLocalPath(path)) throw new IOException("启动行为需要 本地普通目录与已发布程序。");
        if(!File.Exists(executable) || !File.Exists(widget)
            || !Path.GetFileName(executable).Equals("AIControlCenter.StartupWatcher.exe", StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(widget).Equals("CodexQuotaWidget.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Watcher 或主程序未完整发布，请重新打开已发布版本。");
        var arguments = "--data-dir " + StartupIntegration.QuoteArgument(dataRoot) + " --widget " + StartupIntegration.QuoteArgument(widget);
        LaunchThroughDesktop(executable, arguments);
        // Dispatching through Explorer is not itself proof that the watcher actually acquired its mutex.
        for(var attempt = 0; attempt < 100; attempt++)
        {
            if(StartupWatcherControl.IsRunning) return "ExplorerBroker";
            System.Threading.Thread.Sleep(50);
        }
        throw new InvalidOperationException("Windows 桌面已收到启动请求，但 Watcher 尚未开始监听。请查看启动日志后重试。");
    }

    internal static string LaunchThroughDesktop(string executable, string arguments)
    {
        if(!AutomationStore.IsSafeLocalPath(executable) || !File.Exists(executable)
            || Path.GetFileName(executable) is not ("AIControlCenter.StartupWatcher.exe" or "CodexQuotaWidget.exe"))
            throw new InvalidOperationException("桌面启动器只允许本产品已发布的本地程序。");
        // BREAKAWAY_FROM_JOB can succeed while retaining an outer job. Do not fall back to creating a
        // child in the calling development host; use the existing Windows desktop automation broker.
        object? shell = null, windows = null, desktop = null, document = null, broker = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!);
            windows = ((dynamic)shell!).Windows();
            object hwnd = 0;
            desktop = ((dynamic)windows!).FindWindowSW(0, 0, 8, ref hwnd, 1);
            if(desktop is null) throw new InvalidOperationException("Windows 桌面启动器暂时不可用，未使用不可靠的宿主启动链。");
            document = ((dynamic)desktop).Document; broker = ((dynamic)document).Application;
            ((dynamic)broker).ShellExecute(executable, arguments, Path.GetDirectoryName(executable), "open", 0);
            return "ExplorerBroker";
        }
        catch(Exception error) when(error is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        { throw new InvalidOperationException("Windows 桌面未能独立启动小组件监听器，请稍后重试。", error); }
        finally
        {
            var released = new System.Collections.Generic.HashSet<object>(System.Collections.Generic.ReferenceEqualityComparer.Instance);
            foreach(var item in new[] { broker, document, desktop, windows, shell })
                if(item is not null && released.Add(item) && Marshal.IsComObject(item)) Marshal.FinalReleaseComObject(item);
        }
    }
}

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using CodexQuotaWidget.Core;
using CodexQuotaWidget.Core.Startup;

namespace CodexQuotaWidget;

// All settings surfaces use the same Windows-backed controller.
internal static class StartupIntegration
{
    internal static string WatcherPath => Path.Combine(AppContext.BaseDirectory, "startup-watcher", "AIControlCenter.StartupWatcher.exe");
    internal static IStartupBehaviorController CreateController() => new UiController();
    public static bool IsEnabled => CreateController().Read().WindowsLogon.Enabled;
    public static void SetEnabled(bool enabled) => CreateController().SetEnabled(StartupBehaviorOption.WindowsLogon, enabled);

    internal static void Reconcile(Settings settings)
    {
        var controller = CreateController();
        var old = controller.Read();
        if(old.FollowCodex.Owned && old.FollowCodex.Registered && old.FollowCodex.TargetPath != WatcherPath && StartupWatcherControl.IsRunning)
        {
            StartupWatcherControl.Stop();
            if(!StartupWatcherControl.WaitForStopped(TimeSpan.FromSeconds(10)))
                throw new InvalidOperationException("旧版本 Watcher 仍在结束观察，未注册冲突启动项。请稍后重新打开小组件。");
        }
        controller.RefreshPaths();
        var state = controller.Read();
        settings.AutoStart = state.WindowsLogon.Enabled;
        settings.StartWithCodex = state.FollowCodex.Enabled;
        if(state.FollowCodex.Enabled) StartupWatcherLauncher.EnsureRunning(WatcherPath, Paths.Data, Environment.ProcessPath!);
    }

    sealed class UiController : IStartupBehaviorController
    {
        readonly WindowsStartupRegistration store = new(Paths.Data, Environment.ProcessPath ?? "", WatcherPath);
        public StartupBehaviorState Read() => store.Read();
        public void RefreshPaths() => store.RefreshPaths();
        public void SetEnabled(StartupBehaviorOption option, bool enabled)
        {
            store.SetEnabled(option, enabled);
            if(option != StartupBehaviorOption.FollowCodex) return;
            if(enabled) StartupWatcherLauncher.EnsureRunning(WatcherPath, Paths.Data, Environment.ProcessPath!);
            else
            {
                StartupWatcherControl.Stop();
                if(!StartupWatcherControl.WaitForStopped(TimeSpan.FromSeconds(10)))
                    throw new InvalidOperationException("Watcher 启动项已取消，后台正在退出。请稍后重试。");
            }
        }
    }

    internal static string QuoteArgument(string value)
    {
        var result=new StringBuilder("\"");int slashes=0;
        foreach(var character in value)
        {
            if(character=='\\'){slashes++;continue;}
            result.Append('\\',character=='"'?slashes*2+1:slashes);slashes=0;result.Append(character);
        }
        result.Append('\\',slashes*2);return result.Append('"').ToString();
    }
}

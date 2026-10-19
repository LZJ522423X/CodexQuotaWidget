using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Text.Json;
using CodexQuotaWidget.Core;
using CodexQuotaWidget.Core.Startup;

namespace CodexQuotaWidget;
internal static class StartupWatcherNativeCheckRunner
{
    public static async Task<int> RunAsync(string[] args)
    {
        if(!args.Contains("--allow-own-startup-changes"))throw new InvalidOperationException("Explicit acceptance flag required");
        var controller=StartupIntegration.CreateController();var initial=controller.Read();
        if(initial.FollowCodex.Registered || StartupWatcherControl.IsRunning)throw new InvalidOperationException("Native test requires the new follow setting initially off");
        var expectedRunning=args.Contains("--expect-widget-running");
        if(StartupWatcherControl.WidgetIsRunning()!=expectedRunning)throw new InvalidOperationException("Initial main instance state mismatch");
        var output=@"D:\CodexQuotaWidget-GitHub\artifacts\startup-behavior-1.10.0";
        var status=Path.Combine(Paths.Data,"startup","watcher-status.json");
        var checks=new System.Collections.Generic.List<string>();
        void Check(bool value,string name){if(!value)throw new InvalidOperationException(name);checks.Add(name);}
        try
        {
            if(File.Exists(status))File.Delete(status); // Only our bounded diagnostic output.
            controller.SetEnabled(StartupBehaviorOption.FollowCodex,true);
            for(var attempt=0;attempt<100 && (!StartupWatcherControl.IsRunning || !File.Exists(status));attempt++)await Task.Delay(100);
            Check(StartupWatcherControl.IsRunning,"watcher starts independently");
            Check(controller.Read().FollowCodex.Enabled,"real registration enabled");
            for(var attempt=0;attempt<100 && !StartupWatcherControl.WidgetIsRunning();attempt++)await Task.Delay(100);
            Check(StartupWatcherControl.WidgetIsRunning(),expectedRunning?"existing main instance preserved":"verified Codex initial attach launches main");
            var diagnostic=JsonDocument.Parse(File.ReadAllText(status));
            Check(diagnostic.RootElement.TryGetProperty("CodexVerified",out var verified)&&verified.GetBoolean(),"real signed packaged Codex identified");
            var result=diagnostic.RootElement.GetProperty("Result").GetString();
            Check(expectedRunning?result=="Observing":result!.StartsWith("Launched:"),expectedRunning?"no duplicate launch":"actual child launch recorded");
            File.WriteAllText(Path.Combine(output,expectedRunning?"native-no-duplicate-watch.json":"native-watch-handoff.json"),diagnostic.RootElement.GetRawText());
            await Task.Delay(4200);
            Check(StartupWatcherControl.IsRunning,"watcher continues after poll");
        }
        finally
        {
            controller.SetEnabled(StartupBehaviorOption.FollowCodex,false);
            for(var attempt=0;attempt<50&&StartupWatcherControl.IsRunning;attempt++)await Task.Delay(100);
        }
        Check(!controller.Read().FollowCodex.Registered,"turning off removes only owned watcher startup");
        Check(!StartupWatcherControl.IsRunning,"watcher exits normally on stop event");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output,expectedRunning?"native-watch-existing-result.json":"native-watch-launch-result.json"),JsonSerializer.Serialize(new{Passed=true,Checks=checks,Initial=initial,Final=controller.Read(),Mode="Real process and own startup registration; no power actions"},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"PASS native watcher: {checks.Count} checks; follow setting restored off");return 0;
    }
}

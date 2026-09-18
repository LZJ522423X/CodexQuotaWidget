using CodexQuotaWidget.Core;
using System.Text.Json;
using System.Diagnostics;

if(args.Contains("--lifecycle")){Environment.ExitCode=await LifecycleChecks.Run();return;}

if(args.Contains("--desktop-benchmark"))
{
    var watch=Stopwatch.StartNew();
    var results=Enumerable.Range(0,30).Select(_=>DesktopState.IsRunning()).ToArray();
    Console.WriteLine(JsonSerializer.Serialize(new{Calls=results.Length,AllRunning=results.All(v=>v),ElapsedMs=watch.Elapsed.TotalMilliseconds,MeanMs=watch.Elapsed.TotalMilliseconds/results.Length}));
    return;
}

if(args.Contains("--discovery"))
{
    var preferred=Paths.PreferredCli;
    Console.WriteLine(JsonSerializer.Serialize(new{PreferredCli=preferred,Exists=preferred is not null&&File.Exists(preferred),Signed=preferred is not null&&await AppServer.IsOfficialSigned(preferred),Version=preferred is null?null:await AppServer.GetVersion(preferred),AppServer.DiscoveryIssue,SystemPath=Environment.GetFolderPath(Environment.SpecialFolder.System)}));
    return;
}

if(args.Contains("--live"))
{
    var runs=new List<object>();
    for(int i=0;i<2;i++)
    {
        await using var server=new AppServer();
        try
        {
            await server.StartAsync();
            var quota=await server.ReadQuota();
            await server.DisposeAsync();
            runs.Add(new{server.CliPath,server.CliVersion,server.ServerVersion,server.ActualHome,server.LastExitCode,server.Saw401,server.ExternalAuthRequested,DesktopRunning=DesktopState.IsRunning(),DesktopState.HasPackageIdentity,Quota=quota});
        }
        catch(Exception e) { Console.WriteLine(JsonSerializer.Serialize(new{Error=e.GetType().Name,Code=e is RpcException rpc?rpc.Code:0})); Environment.ExitCode=1; return; }
    }
    Console.WriteLine(JsonSerializer.Serialize(runs,new JsonSerializerOptions{WriteIndented=true}));
    return;
}
var count=0;
void Check(bool value,string label){if(!value)throw new Exception(label);count++;}
QuotaSnapshot Parse(string json)=>QuotaParser.Parse(JsonDocument.Parse(json).RootElement,"plus");
var legacy=Parse("""{"rateLimits":{"primary":{"usedPercent":25,"windowDurationMins":300,"resetsAt":1800000000}}}""");
Check(legacy.Windows.Single().Remaining==75,"legacy remaining");
Check(!legacy.ResetCreditsProvided && legacy.AvailableCount is null,"missing credits not zero");
var modern=Parse("""{"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":101,"windowDurationMins":300}},"future":{"tertiary":{"usedPercent":-1,"windowDurationMins":43200}}},"rateLimits":{"primary":{"usedPercent":20}},"rateLimitResetCredits":{"availableCount":0,"credits":[]}}""");
Check(modern.Windows.Count==2,"map deduplicates legacy and retains unknown window");
Check(modern.Windows[0].Remaining==0 && modern.Windows[1].Remaining==100,"clamp");
Check(modern.ResetCreditsProvided && modern.AvailableCount==0,"zero distinct from absent");
Check(Parse("""{"rateLimits":{"primary":{"usedPercent":5},"secondary":{"windowDurationMins":10080}}}""").Windows[1].Remaining is null,"missing percentage");
var expiry=Parse("""{"rateLimits":{"primary":{"usedPercent":50,"resetsAt":999999999999999999}} ,"rateLimitResetCredits":{"availableCount":1,"credits":[{"status":"available","expiresAt":1800000000},{"status":"expired","expiresAt":1700000000}]}}""");
Check(expiry.Windows[0].ResetsAt is null,"invalid timestamp rejected");
Check(expiry.CreditExpirations.SequenceEqual(new long[]{1800000000}),"only available expiry");
var fallback=Parse("""{"rateLimitsByLimitId":{},"rateLimits":{"primary":{"usedPercent":12.5}},"rateLimitResetCredits":null}""");
Check(fallback.Windows[0].Remaining==87.5 && !fallback.ResetCreditsProvided,"empty map fallback and null credits");
try { Parse("{}");throw new Exception("missing structure accepted"); } catch(ProtocolException){count++;}
var tmp=Path.Combine(Path.GetTempPath(),"CQW-check-"+Guid.NewGuid().ToString("N"));
try
{
    AtomicStore.Write(tmp,new Settings{RefreshSeconds=120});
    AtomicStore.Write(tmp,new Settings{RefreshSeconds=300});
    Check(AtomicStore.Read<Settings>(tmp)?.RefreshSeconds==300,"atomic replace");
    File.WriteAllText(tmp,"{broken");
    Check(AtomicStore.Read<Settings>(tmp)?.RefreshSeconds==120,"backup fallback");
}
finally { File.Delete(tmp);File.Delete(tmp+".bak"); }
var settings=new Settings{RefreshSeconds=1,Width=double.NaN,Height=-3,Left=double.PositiveInfinity};settings.Validate();
Check(settings.RefreshSeconds==60 && settings.Width==440 && settings.Height==420 && settings.Left is null,"invalid settings sanitized");
var appearance=new Settings{Theme="unknown",Accent="unknown"};appearance.Validate();
Check(appearance.Theme=="Dark" && appearance.Accent=="Mint","invalid appearance falls back safely");
await using(var denied=new AppServer())
{
    try{await denied.Call("account/rateLimits/reset");throw new Exception("write RPC accepted");}
    catch(ArgumentException){count++;}
}
using(var owned=new Process{StartInfo=new("powershell.exe","-NoProfile -NonInteractive -Command Start-Sleep -Seconds 60"){UseShellExecute=false,CreateNoWindow=true}})
{
    var job=new ProcessJob();owned.Start();job.Assign(owned);job.Dispose();
    await owned.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
    Check(owned.HasExited,"job closes owned process");
}
await using(var monitor=new QuotaMonitor(new Settings(),()=>false))
{
    await monitor.RefreshAsync();Check(monitor.Status==MonitorStatus.DesktopClosed && monitor.ServerPid is null,"closed Desktop never starts server");
    await monitor.SetSuspended(true);Check(monitor.Status==MonitorStatus.Sleeping && monitor.ServerPid is null,"suspend state");
    await monitor.SetSuspended(false);Check(monitor.Status==MonitorStatus.DesktopClosed,"resume reevaluates Desktop");
}
Console.WriteLine($"PASS {count} checks");

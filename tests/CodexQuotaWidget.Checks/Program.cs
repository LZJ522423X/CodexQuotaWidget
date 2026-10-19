using CodexQuotaWidget.Core;
using System.Text.Json;
using System.Diagnostics;
using CodexQuotaWidget.Core.Observation;
if(args.Contains("--unified-update-check")){Console.WriteLine($"PASS unified updates OFFLINE checks: {await UnifiedUpdateChecks.RunAsync()}");return;}

if(args.Contains("--siwc-event-diagnostic-check")){Console.WriteLine($"PASS SIWC event diagnostic OFFLINE checks: {await SiwcEventDiagnosticChecks.RunAsync()}");return;}

if(args.Contains("--siwc-missing-content-type-check")){Console.WriteLine($"PASS SIWC Missing Content-Type OFFLINE checks: {await SiwcMissingContentTypeChecks.RunAsync()}");return;}

if(args.Contains("--siwc-body-read-parser-check")){Console.WriteLine($"PASS SIWC body-read parser OFFLINE checks: {await SiwcBodyReadParserChecks.RunAsync()}");return;}
if(args.Contains("--siwc-body-read-transport-check")){await SiwcBodyReadTransportChecks.RunAsync();return;}

if(args.Contains("--siwc-phase2-check")){Console.WriteLine($"PASS SIWC phase-two OFFLINE checks: {await SiwcPhaseTwoChecks.RunAsync()}");return;}

if(args.Contains("--siwc-phase1-check")){Console.WriteLine($"PASS SIWC phase-one offline checks: {await SiwcPhaseOneChecks.RunAsync()}");return;}

if(args.Contains("--startup-watch-fixture-child")){Environment.ExitCode=StartupWatchLoopChecks.RunChild(args);return;}
if(args.Contains("--follow-close-fixture-child")){Environment.ExitCode=await FollowCodexCloseChecks.RunChildAsync(args);return;}
if(args.Contains("--follow-codex-close-check")){Console.WriteLine($"PASS follow Codex close checks: {await FollowCodexCloseChecks.RunAsync()}");return;}
if(args.Contains("--startup-watch-loop-check")){Console.WriteLine($"PASS startup watcher repeat checks: {StartupWatchLoopChecks.Run()}");return;}
if(args.Contains("--lifecycle")){Environment.ExitCode=await LifecycleChecks.Run();return;}
if(args.Contains("--workflow-connections-check")){Console.WriteLine($"PASS workflow connection checks: {DesktopChatGptConnectionChecks.Run() + await CodexWorkflowConnectorChecks.Run()}");return;}
if(args.Contains("--workflow-codex-binding-check")){Console.WriteLine($"PASS workflow Codex binding checks: {await CodexWorkflowConnectorChecks.Run()}");return;}
if(args.Contains("--workflow-project-files-check")){Console.WriteLine($"PASS workflow project files checks: {CodexWorkflowProjectFilesChecks.Run()}");return;}
if(args.Contains("--workflow-project-validator-check")){Console.WriteLine($"PASS workflow project validator checks: {await WorkflowProjectValidatorChecks.RunAsync()}");return;}
if(args.Contains("--workflow-gpt-connection-check")){Console.WriteLine($"PASS workflow GPT connection checks: {DesktopChatGptConnectionChecks.Run()}");return;}
if(args.Contains("--workflow-gpt-session-binding-check")){Console.WriteLine($"PASS workflow GPT session binding checks: {await DesktopChatGptConnectionChecks.RunSessionBindingChecksAsync()}");return;}
if(args.Contains("--workflow-arithmetic-relay-check")){Console.WriteLine($"PASS simulated arithmetic relay checks: {WorkflowArithmeticRelayChecks.Run()}");return;}
if(args.Contains("--workflow-codex-led-check")){Console.WriteLine($"PASS simulated Codex-led arithmetic checks: {CodexLedArithmeticChecks.Run()}");return;}
if(args.Contains("--workflow-gpt-relay-transport-check")){Console.WriteLine($"PASS simulated GPT relay transport checks: {CodexGptRelayTransportChecks.Run()}");return;}
if(args.Contains("--workflow-loop-check")){Console.WriteLine($"PASS workflow loop checks: {WorkflowLoopChecks.Run()}");return;}
if(args.Contains("--workflow-stability-check")){Console.WriteLine($"PASS workflow stability checks: {await WorkflowStabilityChecks.RunAsync()}");return;}
if(args.Contains("--workflow-checkpoint-check")){Console.WriteLine($"PASS workflow checkpoint checks: {WorkflowCheckpointChecks.Run()}");return;}
if(args.Contains("--workflow-review-check")){Console.WriteLine($"PASS workflow report review checks: {WorkflowReportReviewChecks.Run()}");return;}
if(args.Contains("--workflow-persistence-check")){Console.WriteLine($"PASS workflow persistence checks: {WorkflowPersistenceChecks.Run()}");return;}
if(args.Contains("--startup-behavior-check")){StartupBehaviorChecks.Run();return;}
if(args.Contains("--startup-native-registration-check"))
{
    var index=Array.IndexOf(args,"--widget");var watcher=Array.IndexOf(args,"--watcher");
    if(index<0 || watcher<0 || index+1>=args.Length || watcher+1>=args.Length || !args.Contains("--allow-own-startup-changes"))throw new ArgumentException("Explicit own-startup acceptance flag and published paths required");
    StartupBehaviorChecks.RunHost(args[index+1],args[watcher+1]);return;
}
if(args.Contains("--capsule-placement-check")){Console.WriteLine($"PASS capsule-placement-check: {CapsulePlacementChecks.Run()} checks");return;}
if(args.Contains("--session-catalog"))
{
    var catalog=await new CodexSessionCatalog().ReadAsync();
    Console.WriteLine(JsonSerializer.Serialize(catalog,new JsonSerializerOptions{WriteIndented=true}));
    return;
}
if(args.Contains("--observe-bound"))
{
    var id=Environment.GetEnvironmentVariable("CODEX_QUOTA_WIDGET_OBSERVE_SESSION");
    var project=Environment.GetEnvironmentVariable("CODEX_QUOTA_WIDGET_OBSERVE_PROJECT")??@"D:\CodexQuotaWidget-GitHub";
    var catalog=await new CodexSessionCatalog().ReadAsync();
    var entries=catalog.Sessions.Where(item=>item.SessionId==id).ToArray();
    if(!catalog.Success || entries.Length!=1){Console.WriteLine("Unknown: specified native session unavailable");Environment.ExitCode=1;return;}
    // Read-only native test: this explicit in-memory fixture never saves the user's production binding.
    var binding=new CodexSessionBinding{BindingId=Guid.NewGuid(),ProjectRoot=project,SessionId=entries[0].SessionId,SessionFilePath=entries[0].SessionFilePath,SavedAt=DateTimeOffset.UtcNow};
    var observer=new BoundCodexStatusObserver(binding);var watch=Stopwatch.StartNew();
    var first=await observer.ReadAsync();var firstMs=watch.Elapsed.TotalMilliseconds;watch.Restart();
    var second=await observer.ReadAsync();
    var secondMs=watch.Elapsed.TotalMilliseconds;
    object? eventFixture=null;
    var fixtureRoot=Environment.GetEnvironmentVariable("CODEX_QUOTA_WIDGET_OBSERVE_EVENT_FIXTURE");
    if(fixtureRoot is not null)
    {
        if(!Path.GetFullPath(fixtureRoot).StartsWith(@"D:\CodexQuotaWidget-GitHub\artifacts\native-status-events\",StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Native notification tests require an isolated D artifact directory");
        var output=new CodexStateChangedEventStore(fixtureRoot);
        var item=CodexStateChangedEventFactory.FromObservation(first,project,binding.BindingId,DateTimeOffset.UtcNow);
        var written=output.Publish(item);var before=File.ReadAllBytes(output.LatestPath);
        var duplicate=output.Publish(CodexStateChangedEventFactory.FromObservation(first with{ObservedAt=DateTimeOffset.UtcNow},project,binding.BindingId,DateTimeOffset.UtcNow));
        var current=output.ReadCurrent()!;
        var untouched=before.SequenceEqual(File.ReadAllBytes(output.LatestPath));
        if(!written || duplicate || !untouched || current.Synthetic || current.GrantsActionAuthority)
            throw new InvalidDataException("Real passive notification dedup verification failed");
        eventFixture=new{LatestPath=output.LatestPath,FirstPublished=written,DuplicatePublished=duplicate,PrimaryUnchanged=untouched,current.State,current.TrustLevel,
            current.EvidenceLevel,current.EvidenceKind,current.EvidencePosition,current.SequenceNumber,TimelineEntries=output.ReadTimeline().Count,current.Synthetic,current.GrantsActionAuthority};
    }
    Console.WriteLine(JsonSerializer.Serialize(new{First=first,Second=second,FirstMs=firstMs,SecondMs=secondMs,EventFixture=eventFixture},new JsonSerializerOptions{WriteIndented=true}));
    return;
}
if(args.Contains("--observe-codex"))
{
    var project=Environment.GetEnvironmentVariable("CODEX_QUOTA_WIDGET_OBSERVE_PROJECT") ?? @"D:\CodexQuotaWidget-GitHub";
    var watch=Stopwatch.StartNew();
    var observation=await new CodexQuotaWidget.Core.Observation.CodexStatusObserver(project).ReadAsync();
    Console.WriteLine(JsonSerializer.Serialize(new{Observation=observation,ElapsedMs=watch.Elapsed.TotalMilliseconds},new JsonSerializerOptions{WriteIndented=true}));
    return;
}
if(args.Contains("--update-check")){Console.WriteLine(JsonSerializer.Serialize(await ReleaseUpdates.CheckAsync()));return;}

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
        catch(Exception e) { Console.WriteLine(JsonSerializer.Serialize(new{Error=e.GetType().Name,Code=e is RpcException rpc?rpc.Code:0,
            NativeError=e is System.ComponentModel.Win32Exception native?native.NativeErrorCode:0,
            CliVerified=server.CliPath.Length>0,HomeVerified=!string.IsNullOrEmpty(server.ActualHome)})); Environment.ExitCode=1; return; }
    }
    Console.WriteLine(JsonSerializer.Serialize(runs,new JsonSerializerOptions{WriteIndented=true}));
    return;
}
var count=0;
void Check(bool value,string label){if(!value)throw new Exception(label);count++;}
QuotaSnapshot Parse(string json)=>QuotaParser.Parse(JsonDocument.Parse(json).RootElement,"plus");
var update=ReleaseUpdates.Parse("""{"tag_name":"v1.10.0","html_url":"https://untrusted.invalid"}""",new Version(1,2,0));
Check(update.State==UpdateAvailability.Available && update.Page?.AbsoluteUri==ReleaseUpdates.RepositoryPage+"/releases/tag/v1.10.0","update comparison is numeric and URL is repository-bound");
Check(ReleaseUpdates.Parse("""{"tag_name":"v1.2.0"}""",new Version(1,2,0,0)).State==UpdateAvailability.UpToDate,"equivalent version components");
Check(ReleaseUpdates.Parse("""{"tag_name":"v1.0.0"}""",new Version(1,2,0)).State==UpdateAvailability.LocalNewer,"older public release is not an upgrade");
foreach(var json in new[]{"{\"tag_name\":\"bad\"}","{\"tag_name\":\"v1.3.0\",\"prerelease\":true}"})
    try{ReleaseUpdates.Parse(json,new Version(1,2,0));throw new Exception("invalid release accepted");}catch(InvalidDataException){count++;}
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
foreach(var (value,expected) in new[]{("reset",ResetCreditOutcome.Reset),("alreadyRedeemed",ResetCreditOutcome.AlreadyRedeemed),("nothingToReset",ResetCreditOutcome.NothingToReset),("noCredit",ResetCreditOutcome.NoCredit)})
    Check(QuotaParser.ParseResetOutcome(JsonDocument.Parse("{\"outcome\":\""+value+"\"}").RootElement)==expected,"official reset outcome "+value);
try{QuotaParser.ParseResetOutcome(JsonDocument.Parse("{\"outcome\":\"unexpected\"}").RootElement);throw new Exception("unknown reset outcome accepted");}catch(ProtocolException){count++;}
try { Parse("{}");throw new Exception("missing structure accepted"); } catch(ProtocolException){count++;}
var tmp=Path.Combine(Path.GetTempPath(),"CQW-check-"+Guid.NewGuid().ToString("N"));
try
{
    AtomicStore.Write(tmp,new Settings{RefreshSeconds=120});
    AtomicStore.Write(tmp,new Settings{RefreshSeconds=300});
    Check(AtomicStore.Read<Settings>(tmp)?.RefreshSeconds==300,"atomic replace");
    File.WriteAllText(tmp,"{broken");
    Check(AtomicStore.Read<Settings>(tmp)?.RefreshSeconds==120,"backup fallback");
    AtomicStore.Write(tmp,new Settings{WindowMode="Capsule",EnableAnimations=false,TextColor="Contrast",CapsuleStaySeconds=7.5});
    var persistedAppearance=AtomicStore.Read<Settings>(tmp);
    Check(persistedAppearance?.WindowMode=="Capsule" && !persistedAppearance.EnableAnimations && persistedAppearance.TextColor=="Contrast","window mode and appearance preferences persist");
    Check(persistedAppearance?.CapsuleStaySeconds==7.5,"capsule dwell persists");
}
finally { File.Delete(tmp);File.Delete(tmp+".bak"); }
var settings=new Settings{RefreshSeconds=1,Width=double.NaN,Height=-3,Left=double.PositiveInfinity};settings.Validate();
Check(settings.RefreshSeconds==60 && settings.Width==440 && settings.Height==420 && settings.Left is null,"invalid settings sanitized");
var appearance=new Settings{Theme="unknown",Accent="unknown",TextColor="unknown"};appearance.Validate();
Check(appearance.Theme=="Dark" && appearance.Accent=="Mint" && appearance.TextColor=="Auto","invalid appearance falls back safely");
var oldSettings=System.Text.Json.JsonSerializer.Deserialize<Settings>("{\"RefreshSeconds\":120}");
Check(oldSettings?.WindowMode=="Full" && oldSettings.EnableAnimations,"legacy settings keep Full mode and animations default");
Check(oldSettings?.CapsuleStaySeconds==1.5,"old settings receive default dwell");
foreach(var (input,expected) in new[]{(double.NaN,1.5),(-1d,.5),(60d,30d)})
{
    var dwell=new Settings{CapsuleStaySeconds=input};dwell.Validate();
    Check(dwell.CapsuleStaySeconds==expected,"dwell bounds and invalid number");
}
var invalidMode=new Settings{WindowMode="unknown",EnableAnimations=false};invalidMode.Validate();
Check(invalidMode.WindowMode=="Full" && !invalidMode.EnableAnimations,"invalid window mode falls back without changing animation toggle");
await using(var denied=new AppServer())
{
    try{await denied.Call("account/rateLimits/reset");throw new Exception("write RPC accepted");}
    catch(ArgumentException){count++;}
    try{await denied.ConsumeResetCredit("not-a-guid");throw new Exception("invalid idempotency key accepted");}
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
count+=ReleaseReadinessChecks.Run();
count+=await ControlCenterChecks.RunAsync();
count+=await SafeFinishChecks.RunAsync();
count+=await AutomationChecks.RunAsync();
count+=await RecoveryJournalChecks.RunAsync();
count+=await CodexObservationChecks.RunAsync();
count+=await CodexSessionBindingChecks.RunAsync();
count+=CodexStateEventChecks.Run();
count+=CodexAgentAckChecks.Run();
count+=await CodexStateLifecycleChecks.RunAsync();
count+=WorkflowLoopChecks.Run();
count+=WorkflowCheckpointChecks.Run();
count+=WorkflowReportReviewChecks.Run();
count+=WorkflowPersistenceChecks.Run();
count+=DesktopChatGptConnectionChecks.Run();
count+=WorkflowArithmeticRelayChecks.Run();
count+=CodexLedArithmeticChecks.Run();
count+=CodexGptRelayTransportChecks.Run();
count+=CapsuleActivityChecks.Run();
count+=CapsulePlacementChecks.Run();
count+=await CodexWorkflowConnectorChecks.Run();
count+=CodexWorkflowProjectFilesChecks.Run();
count+=CodexEventChecks.Run();
count+=StartupRecoveryChecks.Run();
count+=StartupBehaviorChecks.Run();
count+=await AutomationDiagnosticsChecks.RunAsync(Environment.GetEnvironmentVariable("CODEX_QUOTA_WIDGET_EXECUTOR")
    ?? @"D:\CodexQuotaWidget-GitHub\src\CodexQuotaWidget.Executor\bin\Release\net10.0-windows\win-x64\CodexQuotaWidget.Executor.exe");
count+=await ManualAcceptanceChecks.RunAsync(Environment.GetEnvironmentVariable("CODEX_QUOTA_WIDGET_EXECUTOR")
    ?? @"D:\CodexQuotaWidget-GitHub\src\CodexQuotaWidget.Executor\bin\Release\net10.0-windows\win-x64\CodexQuotaWidget.Executor.exe");
count+=await ExecutorChecks.RunAsync(Environment.GetEnvironmentVariable("CODEX_QUOTA_WIDGET_EXECUTOR")
    ?? @"D:\CodexQuotaWidget-GitHub\src\CodexQuotaWidget.Executor\bin\Release\net10.0-windows\win-x64\CodexQuotaWidget.Executor.exe");
Console.WriteLine($"PASS {count} checks");

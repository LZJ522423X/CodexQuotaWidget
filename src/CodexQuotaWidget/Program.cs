using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using CodexQuotaWidget.Core;
using CodexQuotaWidget.Core.ControlCenter;
using CodexQuotaWidget.Core.Automation;

namespace CodexQuotaWidget;
public static class Program
{
    [DllImport("kernel32.dll")]static extern bool AttachConsole(uint processId);
    [DllImport("kernel32.dll")]static extern IntPtr GetStdHandle(int kind);
    [STAThread]
    public static int Main(string[] args)
    {
        if(args.Contains("--startup-smoke-check"))
        {
            for(var i=0;i<args.Length;i++)
            {
                if(args[i]=="--startup-smoke-check")continue;
                if(args[i]=="--data-dir" && i+1<args.Length){i++;continue;}
                return 2; // Never combine the offline smoke path with a real action.
            }
        }
        if(args.Contains("--siwc-body-read-once") || args.Contains("--siwc-body-read-verification-once"))
        {
            if(!SiwcPhaseTwoAccess.IsExperimentalBuild)return 2;
            try{return SiwcAcceptanceController.RunBodyReadOnceAsync(args).GetAwaiter().GetResult();}
            catch{return 1;} // Only sanitized controller/report metadata is persisted; never print auth exceptions.
        }
        if(args.Contains("--siwc-catalog-diagnostic"))
        {
            if(!SiwcPhaseTwoAccess.IsExperimentalBuild)return 2;
            try{return SiwcAcceptanceController.DiagnoseModelCatalogAsync().GetAwaiter().GetResult();}
            catch{return 1;}
        }
        if(args.Contains("--siwc-acceptance"))
        {
            if(!SiwcPhaseTwoAccess.IsExperimentalBuild){MessageBox.Show("此构建未开放SIWC第二阶段入口。","AI Control Center");return 2;}
            using var gate=new System.Threading.Mutex(true,@"Local\AIControlCenter.SiwcAcceptance.Phase2",out var first);
            if(!first){MessageBox.Show("独立SIWC验收窗口已打开。","AI Control Center");return 2;}
            try{var experimentalApp=new App();experimentalApp.InitializeComponent();return experimentalApp.Run();}
            finally{gate.ReleaseMutex();}
        }
        if(args.Contains("--watcher-lifetime-parent") || args.Contains("--watcher-lifetime-child"))
            return WatcherLifetimeCheckRunner.RunFixture(args);
        int dataArgument=Array.IndexOf(args,"--data-dir");
        if(dataArgument>=0)
        {
            if(dataArgument+1>=args.Length || !Path.IsPathFullyQualified(args[dataArgument+1]))
            {
                MessageBox.Show("--data-dir 需要有效的绝对目录路径。","Codex AI Control Center");
                return 2;
            }
            try { Environment.SetEnvironmentVariable("CODEX_QUOTA_WIDGET_DATA",Path.GetFullPath(args[dataArgument+1])); }
            catch(Exception e)when(e is ArgumentException or NotSupportedException or PathTooLongException)
            { MessageBox.Show("数据目录路径无效，未启动程序。","Codex AI Control Center");return 2; }
            args=args.Where((_,index)=>index!=dataArgument && index!=dataArgument+1).ToArray();
        }
        if(args.Contains("--workflow-stability-live-check") || args.Contains("--workflow-stability-connection-check"))
        {
            var handle=GetStdHandle(-11);
            if(handle==IntPtr.Zero || handle==new IntPtr(-1))AttachConsole(uint.MaxValue);
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()){AutoFlush=true});
            return WorkflowStabilityLiveCheckRunner.RunAsync(args).GetAwaiter().GetResult();
        }
        if(args.Contains("--workflow-codex-led-check"))
        {
            var handle=GetStdHandle(-11);
            if(handle==IntPtr.Zero || handle==new IntPtr(-1))AttachConsole(uint.MaxValue);
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()){AutoFlush=true});
            return CodexLedArithmeticCheckRunner.RunAsync(args).GetAwaiter().GetResult();
        }
        if(args.Contains("--follow-codex-presence-check"))
        {
            var handle=GetStdHandle(-11);
            if(handle==IntPtr.Zero || handle==new IntPtr(-1))AttachConsole(uint.MaxValue);
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()){AutoFlush=true});
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(CodexQuotaWidget.StartupWatcher.DesktopIdentity.ReadPresence()));
            return 0;
        }
        if(args.Contains("--workflow-relay-check"))
        {
            var handle=GetStdHandle(-11);
            if(handle==IntPtr.Zero || handle==new IntPtr(-1))AttachConsole(uint.MaxValue);
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()){AutoFlush=true});
            return WorkflowArithmeticCheckRunner.RunAsync(args).GetAwaiter().GetResult();
        }
        if(args.Contains("--capsule-hover-check") || args.Contains("--watcher-lifetime-check") || args.Contains("--shell-layout-check") || args.Contains("--skill-update-check") || args.Contains("--startup-behavior-check") || args.Contains("--startup-watcher-native-check"))
        {
            AttachConsole(uint.MaxValue);Console.SetOut(new StreamWriter(Console.OpenStandardOutput()){AutoFlush=true});
            var testApp=new App();testApp.InitializeComponent();return testApp.Run();
        }
        if(args.Contains("--workflow-live-connection-check") || args.Contains("--workflow-connection-check") || args.Contains("--workflow-review-report") || args.Contains("--check") || args.Contains("--once") || args.Contains("--layout-check") || args.Contains("--animation-check") || args.Contains("--capsule-interaction-check") || args.Contains("--capsule-foreground-check") || args.Contains("--capsule-game-check") || args.Contains("--capsule-dock-check") || args.Contains("--control-center-check") || args.Contains("--automation-check") || args.Contains("--reliability-check") || args.Contains("--workflow-check") || args.Contains("--workflow-run") || args.Contains("--workflow-acknowledge") || args.Contains("--quota-finish-watch") || args.Contains("--launch-executor-test") || args.Contains("--manual-acceptance-test") || args.Contains("--save-resume"))
        {
            var handle=GetStdHandle(-11);
            if(handle==IntPtr.Zero || handle==new IntPtr(-1))AttachConsole(uint.MaxValue);
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()){AutoFlush=true});
            if(args.Contains("--manual-acceptance-test"))return ManualAcceptanceLauncher.Run(args);
            if(args.Contains("--save-resume"))return SaveResume(args);
            if(args.Contains("--quota-finish-watch"))return QuotaFinishWatchRunner.RunAsync(args).GetAwaiter().GetResult();
            if(args.Contains("--workflow-acknowledge"))
            {
                var runtime=new WorkflowLoopRuntime(Paths.Data);
                var pending=runtime.State.NeedsRecoveryConfirmation;
                Console.WriteLine(runtime.AcknowledgeRecovery());
                var acknowledged=pending && !runtime.State.NeedsRecoveryConfirmation;
                runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
                return acknowledged?0:1;
            }
            if(args.Contains("--workflow-connection-check"))return CheckWorkflowConnections().GetAwaiter().GetResult();
            if(args.Contains("--workflow-review-report"))return ReviewWorkflowReport(args).GetAwaiter().GetResult();
            if(args.Contains("--workflow-run"))return RunWorkflow(args).GetAwaiter().GetResult();
            if(args.Contains("--launch-executor-test"))
            {
                int request=Array.IndexOf(args,"--request");
                // This parent exits immediately; the request's TestOnly flag also prevents real power.
                if(request>=0 && request+1<args.Length)
                    Console.WriteLine(IndependentExecutorLauncher.Launch(AutomationRuntime.ExecutorPath,args[request+1],execute:false));
                else
                {
                    int project=Array.IndexOf(args,"--project-root");
                    if(project<0 || project+1>=args.Length)return 2;
                    var plan=CodexQuotaWidget.Core.Automation.ActionPlanParser.Parse(AutomationRuntime.DefaultText).Plan!;
                    var fixture=AutomationRuntime.CreateTestRequest(args[project+1],plan,simulateAiUnavailable:true);
                    var method=AutomationRuntime.Launch(fixture,execute:false);
                    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new{fixture.RequestId,ParentPid=Environment.ProcessId,Method=method}));
                }
                return 0;
            }
            if(args.Contains("--workflow-live-connection-check") || args.Contains("--layout-check") || args.Contains("--animation-check") || args.Contains("--capsule-interaction-check") || args.Contains("--capsule-foreground-check") || args.Contains("--capsule-game-check") || args.Contains("--capsule-dock-check") || args.Contains("--control-center-check") || args.Contains("--automation-check") || args.Contains("--reliability-check") || args.Contains("--workflow-check"))
            {
                var layoutApp=new App();layoutApp.InitializeComponent();return layoutApp.Run();
            }
            return HeadlessCommand.Run(args).GetAwaiter().GetResult();
        }
        var app=new App();app.InitializeComponent();return app.Run();
    }
    static async System.Threading.Tasks.Task<int> CheckWorkflowConnections()
    {
        await using var runtime = new WorkflowLoopRuntime(Paths.Data);
        var gpt = runtime.ReconnectGptAsync(); await gpt;
        var codex = await runtime.ConnectCodexAsync();
        var output = new { runtime.GptConnection, runtime.CodexConnection, runtime.GptCandidates, ModelRequests = 0,
            CompletedLoopPreserved = runtime.State.Phase == WorkflowLoopPhase.Completed && runtime.State.Round == 2 };
        var json = System.Text.Json.JsonSerializer.Serialize(output, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        Directory.CreateDirectory(Path.Combine(Paths.Data,"workflow"));
        File.WriteAllText(Path.Combine(Paths.Data,"workflow","connection-check.json"),json);
        Console.WriteLine(json);
        return runtime.GptConnection.State == WorkflowLinkState.Connected && runtime.CodexConnection.State == WorkflowLinkState.Connected ? 0 : 1;
    }
    static async System.Threading.Tasks.Task<int> ReviewWorkflowReport(string[] args)
    {
        var index=Array.IndexOf(args,"--report");
        if(index<0 || index+1>=args.Length || !AutomationStore.IsSafeDPath(args[index+1]) || !File.Exists(args[index+1]) || new FileInfo(args[index+1]).Length>100000)return 2;
        var configuration=WorkflowLoopRuntime.ReadConfiguration(Paths.Data);
        if(configuration is null)return 2;
        await using var chat=new DesktopChatGptWorkflowConnector(configuration.ChatGptThreadId,configuration.CallerThreadId,Paths.Data);
        using var deadline=new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(20));
        if(!await chat.ConnectAsync(deadline.Token)){Console.WriteLine(chat.DiagnosticMessage);return 1;}
        var guardReader=new ControlCenterService(Path.Combine(Paths.Data,"workflow","review-quota-read-model.json"));
        async System.Threading.Tasks.Task<QuotaGuardSnapshot> ReadQuota(System.Threading.CancellationToken token)
        {
            try{var quota=await chat.ReadQuotaAsync(token);guardReader.UpdateQuota(quota,MonitorStatus.Live,false);return guardReader.Snapshot.Guard;}
            catch(Exception){return new(QuotaGuardState.Unknown,null,null,false,false,"复核额度无法确认。");}
        }
        var review=new WorkflowReportReviewService(Paths.Data,chat,ReadQuota,configuration.MinimumRemainingPercent);
        var result=await review.ReviewAsync(File.ReadAllText(args[index+1]),configuration.ProjectRoot,configuration.ChatGptThreadId,deadline.Token);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result));
        return result.Success?0:1;
    }
    static async System.Threading.Tasks.Task<int> RunWorkflow(string[] args)
    {
        var index=Array.IndexOf(args,"--workflow-config");
        if(index<0 || index+1>=args.Length || !AutomationStore.IsSafeDPath(args[index+1]))return 2;
        try
        {
            if(new FileInfo(args[index+1]).Length>24000)return 2;
            var configuration=System.Text.Json.JsonSerializer.Deserialize<WorkflowLoopConfiguration>(File.ReadAllText(args[index+1]));
            if(configuration is null || !WorkflowLoopService.ValidConfiguration(configuration))return 2;
            await using var runtime=new WorkflowLoopRuntime(Paths.Data,configuration);
            runtime.Changed+=()=>Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new{runtime.State.Phase,runtime.State.Round,runtime.State.Message}));
            var result=await runtime.StartAsync(configuration);
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new{Result=result,runtime.State,runtime.ConnectionDescription}));
            return runtime.State.Phase==WorkflowLoopPhase.Completed?0:1;
        }
        catch(Exception error)when(error is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {Console.WriteLine("工作流未完成："+error.GetType().Name);return 1;}
    }
    static int SaveResume(string[] args)
    {
        int project=Array.IndexOf(args,"--project-root");
        if(project<0 || project+1>=args.Length){Console.WriteLine("FAIL save-resume: project path required");return 2;}
        try
        {
            var state=AtomicStore.Read<ControlCenterState>(Path.Combine(Paths.Data,"control-center","state.json"));
            var snapshot=new ControlCenterSnapshot{Tasks=state?.Tasks??[],Policy=state?.Policy??new(),DispatchPaused=state?.DispatchPaused??false,SafeStopRequested=state?.SafeStopRequested??false};
            var result=SaveWithLeaseAsync(args[project+1],snapshot).GetAwaiter().GetResult();
            Console.WriteLine(result.Success?"PASS save-resume: "+result.ResumePath:"FAIL save-resume: "+result.Message);
            return result.Success?0:1;
        }
        catch(Exception e)when(e is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {Console.WriteLine("FAIL save-resume: "+e.GetType().Name);return 1;}
    }
    static async System.Threading.Tasks.Task<SafeFinishCheckpointResult> SaveWithLeaseAsync(string project,ControlCenterSnapshot snapshot)
    {
        await using var lease=await RecoveryWriteLease.AcquireAsync(Paths.Data);
        return await new SafeFinishCheckpoint(project,Paths.Data).CreateAsync(snapshot);
    }
}

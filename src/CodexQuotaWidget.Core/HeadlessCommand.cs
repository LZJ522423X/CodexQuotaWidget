using System.Security.Principal;
using System.Text.Json;

namespace CodexQuotaWidget.Core;
public sealed class CommandReport
{
    public string WidgetVersion {get;set;}="1.0.0";
    public string Mode {get;set;}="";
    public string RequestedCodexHome {get;set;}=Paths.Home;
    public string? ActualCodexHome {get;set;}
    public string? CliPath {get;set;}
    public string? CliVersion {get;set;}
    public string? ServerVersion {get;set;}
    public string Protocol {get;set;}="JSON-RPC/stdio; numeric protocol version not provided";
    public string InitializeStatus {get;set;}="not_run";
    public string AccountReadStatus {get;set;}="not_run";
    public string RateLimitsReadStatus {get;set;}="not_run";
    public string? AccountType {get;set;}
    public string? PlanType {get;set;}
    public object[] Windows {get;set;}=[];
    public double? FiveHourRemainingPercent {get;set;}
    public double? WeeklyRemainingPercent {get;set;}
    public double? GptReserveRemainingPercent {get;set;}
    public bool ResetCreditsProvided {get;set;}
    public int? AvailableResetCredits {get;set;}
    public string[] ResetCreditExpirationsLocal {get;set;}=[];
    public string? NextResetLocal {get;set;}
    public DateTimeOffset? ReadAt {get;set;}
    public int? ServerPid {get;set;}
    public int? ServerExitCode {get;set;}
    public bool GracefulExit {get;set;}
    public uint? ResidualOwnedProcesses {get;set;}
    public bool Saw401 {get;set;}
    public bool ExternalAuthRequested {get;set;}
    public bool HasPackageIdentity {get;set;}
    public string? ErrorStage {get;set;}
    public string? ErrorKind {get;set;}
    public int? RpcErrorCode {get;set;}
    public string? Action {get;set;}
    public bool Passed {get;set;}
}
public static class HeadlessCommand
{
    public static string Local(long timestamp)=>DateTimeOffset.FromUnixTimeSeconds(timestamp).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz");
    public static async Task<int> Run(string[] args)
    {
        var report=new CommandReport{Mode=args.Contains("--check")?"--check":"--once",HasPackageIdentity=DesktopState.HasPackageIdentity};
        using var mutex=new Mutex(false,@"Local\CodexQuotaWidget."+WindowsIdentity.GetCurrent().User!.Value);
        bool acquired;
        try{acquired=mutex.WaitOne(0);}catch(AbandonedMutexException){acquired=true;}
        if(!acquired){report.ErrorKind="WidgetAlreadyRunning";report.Action="Exit the widget before headless checks.";return Output(report);}
        // Do not release this thread-owned mutex after an await; disposal closes its handle on exit.
        var server=new AppServer();string stage="arguments";
        try
        {
            string? manual=null;
            for(int i=0;i<args.Length;i++)
            {
                if(args[i] is "--check" or "--once")continue;
                if(args[i]=="--codex-path" && i+1<args.Length){manual=args[++i];continue;}
                throw new ArgumentException();
            }
            if(args.Contains("--check")&&args.Contains("--once"))throw new ArgumentException();
            stage="initialize";
            await server.StartAsync(manual);
            report.InitializeStatus="ok";report.ServerPid=server.Pid;
            stage="account/read";
            report.PlanType=await server.ReadAccount();report.AccountType="chatgpt";report.AccountReadStatus="ok";
            stage="account/rateLimits/read";
            var raw=await server.Call("account/rateLimits/read");report.RateLimitsReadStatus="ok";
            stage="quota.parse";
            var quota=QuotaParser.Parse(raw,report.PlanType);
            report.ReadAt=quota.UpdatedAt;
            report.FiveHourRemainingPercent=quota.Windows.Find(w=>w.LimitId=="codex"&&w.WindowDurationMins==300)?.Remaining;
            report.WeeklyRemainingPercent=quota.Windows.Find(w=>w.LimitId=="codex"&&w.WindowDurationMins==10080)?.Remaining;
            report.GptReserveRemainingPercent=quota.Windows.Find(w=>w.Name=="gpt-reserve"||w.LimitId=="base_model_inference")?.Remaining;
            report.Windows=quota.Windows.Select(w=>(object)new{w.LimitId,w.Name,w.Slot,w.Remaining,w.WindowDurationMins,ResetsAtLocal=w.ResetsAt is {} t?Local(t):null,SecondsUntilReset=w.ResetsAt is {} t2?Math.Max(0,t2-quota.UpdatedAt.ToUnixTimeSeconds()):(long?)null}).ToArray();
            report.ResetCreditsProvided=quota.ResetCreditsProvided;report.AvailableResetCredits=quota.AvailableCount;
            report.ResetCreditExpirationsLocal=quota.CreditExpirations.Select(Local).ToArray();
            var next=quota.Windows.Where(w=>w.ResetsAt is not null).Select(w=>w.ResetsAt!.Value).Order().ToArray();
            report.NextResetLocal=next.Length>0?Local(next[0]):null;
        }
        catch(Exception e)
        {
            report.ErrorStage=stage;
            report.ErrorKind=e is RpcException{Authentication:true}?"AuthenticationRequired":e.GetType().Name;
            report.RpcErrorCode=e is RpcException rpc?rpc.Code:null;
            if(stage=="initialize")report.InitializeStatus="failed";
            if(stage=="account/read")report.AccountReadStatus="failed";
            if(stage=="account/rateLimits/read")report.RateLimitsReadStatus="failed";
            report.Action=e is FileNotFoundException?"Provide --codex-path with an official signed codex.exe; no disk scan performed.":"Stopped. No automatic login, reset consumption or UI startup.";
        }
        finally
        {
            try{await server.DisposeAsync();}
            catch(Exception){report.ErrorStage??="cleanup";report.ErrorKind??="CleanupVerificationFailed";}
            report.CliPath=server.CliPath;report.CliVersion=server.CliVersion;report.ServerVersion=server.ServerVersion;report.ActualCodexHome=server.ActualHome;
            report.ServerExitCode=server.LastExitCode;report.ResidualOwnedProcesses=server.RemainingOwnedProcesses;
            report.GracefulExit=server.LastExitCode==0&&!server.ForcedStop;
            report.Saw401=server.Saw401;report.ExternalAuthRequested=server.ExternalAuthRequested;
        }
        report.Passed=report.ErrorKind is null && report.GracefulExit && report.ResidualOwnedProcesses==0 && !report.Saw401 && !report.ExternalAuthRequested && !report.HasPackageIdentity;
        return Output(report);
    }
    static int Output(CommandReport report){Console.WriteLine(JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));return report.Passed?0:1;}
}

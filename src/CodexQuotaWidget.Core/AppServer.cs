using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace CodexQuotaWidget.Core;

public sealed class RpcException(int code, bool auth) : Exception(auth ? "Authentication required" : "App Server request failed")
{ public int Code { get; } = code; public bool Authentication { get; } = auth; }

public sealed class AppServer : IAsyncDisposable
{
    public static string DiscoveryIssue {get;private set;}="";
    readonly CancellationToken cancellation;
    public AppServer(CancellationToken cancellation=default){this.cancellation=cancellation;}
    readonly ConcurrentDictionary<int,TaskCompletionSource<JsonElement>> pending=new();
    readonly SemaphoreSlim writer=new(1,1);
    Process? process;
    ProcessJob? job;
    Task? readerTask,stderrTask;
    TaskCompletionSource<bool>? loginCompletion;
    int sequence;
    public string CliPath {get;private set;}="";
    public string CliVersion {get;private set;}="";
    public string ServerVersion {get;private set;}="";
    public string ActualHome {get;private set;}="";
    public bool Saw401 {get;private set;}
    public bool ExternalAuthRequested {get;private set;}
    public int? LastExitCode {get;private set;}
    public uint? RemainingOwnedProcesses {get;private set;}
    public bool ForcedStop {get;private set;}
    bool started;
    public int? Pid
    {
        get { try{return process is {HasExited:false} p ? p.Id : null;} catch(InvalidOperationException){return null;} }
    }

    public static IEnumerable<string> Candidates(string? manual)
    {
        if (Paths.PreferredCli is { } preferred) yield return preferred;
        var root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),@"AppData\Local\OpenAI\Codex\bin");
        if(Directory.Exists(root))
            foreach(var dir in new DirectoryInfo(root).GetDirectories().OrderByDescending(d=>d.LastWriteTimeUtc).Take(8))
                yield return Path.Combine(dir.FullName,"codex.exe");
        if(!string.IsNullOrWhiteSpace(manual)) yield return manual;
    }
    public static async Task<string?> GetVersion(string path)
    {
        if(!File.Exists(path) || !Path.GetFileName(path).Equals("codex.exe",StringComparison.OrdinalIgnoreCase)) return null;
        if(!await IsOfficialSigned(path)) return null;
        using var p=new Process{StartInfo=new(path,"--version"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true}};
        using var versionJob=new ProcessJob();
        try
        {
            p.Start(); versionJob.Assign(p); var output=p.StandardOutput.ReadToEndAsync(); var error=p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
            var value=(await output).Trim(); await error;
            return p.ExitCode==0 && System.Text.RegularExpressions.Regex.IsMatch(value,@"^codex-cli [0-9][0-9A-Za-z.\-+]{0,60}$") ? value : null;
        }
        catch { try { if(!p.HasExited) p.Kill(true); } catch(InvalidOperationException) { } return null; }
    }
    public static async Task<bool> IsOfficialSigned(string path)
    {
        const string script="$s=Get-AuthenticodeSignature -LiteralPath $env:CQW_CANDIDATE; [Console]::WriteLine(('Exists={0};Status={1};Publisher={2}' -f (Test-Path -LiteralPath $env:CQW_CANDIDATE),$s.Status,($s.SignerCertificate.Subject -match 'O=.?OpenAI'))); if($s.Status -eq 'Valid' -and $s.SignerCertificate.Subject -match 'O=.?OpenAI'){exit 0}; exit 1";
        var info=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),@"WindowsPowerShell\v1.0\powershell.exe"))
            {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        info.ArgumentList.Add("-NoProfile");info.ArgumentList.Add("-NonInteractive");info.ArgumentList.Add("-EncodedCommand");
        info.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));info.Environment["CQW_CANDIDATE"]=path;
        // A PowerShell 7 parent can otherwise hide Windows PowerShell's inbox modules.
        info.Environment.Remove("PSModulePath");
        using var p=new Process{StartInfo=info};
        using var signatureJob=new ProcessJob();
        try
        {
            p.Start();signatureJob.Assign(p);var output=p.StandardOutput.ReadToEndAsync();var error=p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));var summary=(await output).Trim();await error;
            DiscoveryIssue="SignatureExit="+p.ExitCode+(System.Text.RegularExpressions.Regex.IsMatch(summary,@"^[A-Za-z=;0-9 ]{0,120}$") ? ";"+summary : "");return p.ExitCode==0;
        }
        catch(Exception e){DiscoveryIssue=e.GetType().Name+":"+e.HResult;try{if(!p.HasExited)p.Kill(true);}catch(InvalidOperationException){}return false;}
    }
    public async Task StartAsync(string? manual=null)
    {
        foreach(var path in Candidates(manual).Distinct(StringComparer.OrdinalIgnoreCase))
            if(await GetVersion(path) is { } version){CliPath=path;CliVersion=version;break;}
        if(CliPath.Length==0) throw new FileNotFoundException("Official codex.exe not found");
        var start=new ProcessStartInfo(CliPath,"app-server --listen stdio://")
        {UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,
            StandardInputEncoding=new UTF8Encoding(false),StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8,WorkingDirectory=Paths.Data};
        start.Environment["CODEX_HOME"]=Paths.Home;
        // Independent ChatGPT authentication must not inherit API/provider credentials from its parent.
        foreach(var key in start.Environment.Keys.ToArray())
            if(key.StartsWith("CODEX_",StringComparison.OrdinalIgnoreCase) && key!="CODEX_HOME"
                || key is "OPENAI_API_KEY" or "OPENAI_BASE_URL" or "OPENAI_ORG_ID" or "OPENAI_PROJECT_ID") start.Environment.Remove(key);
        job=new ProcessJob(@"Local\CodexQuotaWidget.AppServer."+System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value); process=new Process{StartInfo=start};
        try
        {
            process.Start(); started=true; job.Assign(process);
            readerTask=ReadLoop(); stderrTask=DrainErrors();
            var init=await Call("initialize",new{clientInfo=new{name="CodexQuotaWidget",version="1.0.0"},capabilities=new{}});
            ActualHome=QuotaParser.String(init,"codexHome") ?? throw new ProtocolException("initialize.codexHome");
            if(!Path.GetFullPath(ActualHome).TrimEnd('\\').Equals(Paths.Home,StringComparison.OrdinalIgnoreCase)) throw new ProtocolException("initialize.codexHome mismatch");
            var agent=QuotaParser.String(init,"userAgent");
            ServerVersion=agent is not null && System.Text.RegularExpressions.Regex.IsMatch(agent,@"^[A-Za-z0-9 _./();\-]{1,240}$") ? agent : "not provided";
            await Send(new{method="initialized"});
        }
        catch { await DisposeAsync(); throw; }
    }
    async Task Send(object value)
    {
        await writer.WaitAsync();
        try { await process!.StandardInput.WriteLineAsync(JsonSerializer.Serialize(value)); await process.StandardInput.FlushAsync(); }
        finally { writer.Release(); }
    }
    public async Task<JsonElement> Call(string method,object? parameters=null)
    {
        if(method is not ("initialize" or "account/read" or "account/rateLimits/read" or "account/login/start"))
            throw new ArgumentException("RPC method not allowed by the read-only widget.",nameof(method));
        var id=Interlocked.Increment(ref sequence);
        var completion=new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id]=completion;
        try
        {
            await Send(new{id,method,@params=parameters??new{}});
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(45),cancellation);
        }
        finally { pending.TryRemove(id,out _); }
    }
    async Task ReadLoop()
    {
        try
        {
            while(await process!.StandardOutput.ReadLineAsync() is { } line)
            {
                using var doc=JsonDocument.Parse(line); var root=doc.RootElement;
                var method=QuotaParser.String(root,"method");
                if(method is not null)
                {
                    if(method=="account/login/completed")
                        loginCompletion?.TrySetResult(QuotaParser.Get(QuotaParser.Get(root,"params"),"success").ValueKind==JsonValueKind.True);
                    var requestId=QuotaParser.Get(root,"id");
                    if(requestId.ValueKind!=JsonValueKind.Undefined)
                    {
                        ExternalAuthRequested|=method=="account/chatgptAuthTokens/refresh";
                        await Send(new{id=requestId.Clone(),error=new{code=-32601,message="Unsupported server request"}});
                    }
                    continue;
                }
                var id=QuotaParser.Get(root,"id");
                if(id.ValueKind!=JsonValueKind.Number || !id.TryGetInt32(out var n) || !pending.TryGetValue(n,out var target)) continue;
                var error=QuotaParser.Get(root,"error");
                if(error.ValueKind==JsonValueKind.Object)
                {
                    var message=QuotaParser.String(error,"message")??"";
                    bool auth=message.Contains("401") || message.Contains("authentication",StringComparison.OrdinalIgnoreCase) || message.Contains("refresh token",StringComparison.OrdinalIgnoreCase);
                    Saw401|=message.Contains("401");
                    target.TrySetException(new RpcException((int)(QuotaParser.Number(error,"code")??-1),auth));
                }
                else
                {
                    var result=QuotaParser.Get(root,"result");
                    if(result.ValueKind==JsonValueKind.Undefined) target.TrySetException(new ProtocolException("JSON-RPC.result"));
                    else target.TrySetResult(result.Clone());
                }
            }
        }
        catch { }
        finally { foreach(var t in pending.Values) t.TrySetException(new IOException("App Server disconnected")); loginCompletion?.TrySetResult(false); }
    }
    async Task DrainErrors()
    {
        try { while(await process!.StandardError.ReadLineAsync() is { } line) Saw401|=line.Contains("401 Unauthorized",StringComparison.OrdinalIgnoreCase); }
        catch(IOException) { }
    }
    public async Task<string> ReadAccount()
    {
        var r=await Call("account/read",new{refreshToken=false});
        var account=QuotaParser.Get(r,"account");
        if(QuotaParser.String(account,"type")!="chatgpt") throw new RpcException(-32600,true);
        return QuotaParser.Label(QuotaParser.String(account,"planType"),"Unknown");
    }
    public async Task<QuotaSnapshot> ReadQuota()
    {
        var plan=await ReadAccount();
        return QuotaParser.Parse(await Call("account/rateLimits/read"),plan);
    }
    public async Task<bool> LoginAsync()
    {
        loginCompletion=new(TaskCreationOptions.RunContinuationsAsynchronously);
        var r=await Call("account/login/start",new{type="chatgpt",useHostedLoginSuccessPage=true,appBrand="codex"});
        var url=QuotaParser.String(r,"authUrl");
        if(!Uri.TryCreate(url,UriKind.Absolute,out var uri) || uri.Scheme!="https" || uri.Host is not ("auth.openai.com" or "chatgpt.com")) throw new ProtocolException("login.authUrl official host");
        Process.Start(new ProcessStartInfo(url!){UseShellExecute=true});
        return await loginCompletion.Task.WaitAsync(TimeSpan.FromMinutes(15),cancellation);
    }
    public async ValueTask DisposeAsync()
    {
        var p=process; if(p is null){job?.Dispose();job=null;return;}
        if(!started){p.Dispose();process=null;job?.Dispose();job=null;return;}
        try
        {
            if(!p.HasExited)
            {
                p.StandardInput.Close();
                try { await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8)); }
                catch(TimeoutException) { ForcedStop=true; p.Kill(true); await p.WaitForExitAsync(); SafeLog.Write(LogEvent.ServerForcedStop); }
            }
            LastExitCode=p.ExitCode;
            if(job is not null)
            {
                RemainingOwnedProcesses=job.ActiveProcesses;
                if(RemainingOwnedProcesses>0)
                {
                    ForcedStop=true;job.Terminate();
                    for(int i=0;i<40 && job.ActiveProcesses>0;i++)await Task.Delay(50);
                    RemainingOwnedProcesses=job.ActiveProcesses;
                }
            }
            if(readerTask is not null) await readerTask.WaitAsync(TimeSpan.FromSeconds(3));
            if(stderrTask is not null) await stderrTask.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally { job?.Dispose();job=null;p.Dispose();process=null; }
    }
}

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CodexQuotaWidget.Core;
using CodexQuotaWidget.Core.Automation;
using CodexQuotaWidget.Core.ControlCenter;

namespace CodexQuotaWidget;

/// <summary>The experiment window's controller. No normal workflow, executor, quota reader or Desktop connector is constructed.</summary>
internal sealed class SiwcAcceptanceController : IDisposable
{
    public const string DataRoot = @"D:\CodexQuotaWidgetData\siwc-acceptance\phase2-20261016";
    public const string BodyReadDataRoot = @"D:\CodexQuotaWidgetData\siwc-acceptance\body-read-repair-20261016";
    public const string BodyReadVerificationDataRoot = @"D:\CodexQuotaWidgetData\siwc-acceptance\body-read-verification-20261017";
    readonly string metadataDirectory;
    readonly SiwcProtectedStore store;
    readonly SiwcHttpProtocol protocol;
    readonly SiwcAuthorizationService auth;
    readonly SiwcSingleRequestAcceptance acceptance;
    readonly CancellationTokenSource stop = new();
    readonly SemaphoreSlim gate = new(1, 1);
    string account = "";
    public bool Busy { get; private set; }
    public bool Authenticated { get; private set; }
    public bool PlanScopeValidated { get; private set; }
    public string AccountLabel => account.Length > 12 ? account[..12] + "…（独立SIWC）" : "未授权";
    public string AccountId => account;
    public SiwcModelChoice[] Models { get; private set; } = [];
    public string Message { get; private set; } = "请亲自点击 Continue with ChatGPT 完成官方授权。";
    public SiwcAcceptanceTicket Ticket => acceptance.Ticket;
    public SiwcAcceptanceReport Report => acceptance.ReadReport();
    public bool HasAttempt => acceptance.HasAttempt;
    public bool HasSavedAccount { get; }
    public event Action? Changed;
    string AccountPath => Path.Combine(metadataDirectory, "account-status.json");
    string SavedAccountPath => Path.Combine(DataRoot, "account-status.json");
    sealed record AccountReceipt(int SchemaVersion, string AccountId, bool PlanScopeValidated, DateTimeOffset CheckedAt);
    public SiwcAcceptanceController(SiwcAcceptanceKind kind = SiwcAcceptanceKind.Original, string? campaign = null)
    {
        if (campaign is not null && kind != SiwcAcceptanceKind.BodyReadVerification)
            throw new InvalidOperationException("独立新轮次仅允许固定正文验收模式。");
        metadataDirectory = kind switch
        {
            SiwcAcceptanceKind.Original => DataRoot,
            SiwcAcceptanceKind.BodyReadRepair => BodyReadDataRoot,
            SiwcAcceptanceKind.BodyReadVerification => SiwcSingleRequestAcceptance.ResolveVerificationCampaign(BodyReadVerificationDataRoot, campaign),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        var access = SiwcPhaseTwoAccess.ForUserAcceptanceWindow(kind);
        store = new(DataRoot); protocol = new(access); auth = new(store, protocol, access); acceptance = new(metadataDirectory, auth, protocol, kind);
        HasSavedAccount = File.Exists(SavedAccountPath);
        if (HasAttempt) Message = Report.Message;
    }
    public Task SignInAsync() => InGate(async token =>
    {
        if (HasAttempt) { Message = "已有唯一验收尝试；不能换账户开启第二次。"; return; }
        Models = []; Authenticated = PlanScopeValidated = false;
        Message = "等待你在系统浏览器中完成官方SIWC授权；尚未请求模型。"; Changed?.Invoke();
        var host = store.GetOrCreateHostId();
        if (account.Length == 0 && HasSavedAccount) account = ReadSavedAccountId();
        (string? Client, string? Subject) registration = (null, null);
        if (account.Length > 0) registration = await store.WithExclusiveAsync(account, (credential, _) =>
            Task.FromResult<(SiwcCredential?, (string?, string?))>((null, (credential?.ClientId, credential?.Subject))), token);
        // Reuse only this app's verified registration; never imports Codex/Desktop credentials.
        account = await auth.SignInInteractiveAsync(host, 0, registration.Client, registration.Subject, uri =>
        {
            if (uri.Scheme != "https" || uri.Host != "auth.openai.com" || uri.AbsolutePath != "/api/accounts/authorize") throw new InvalidOperationException("非官方授权入口已阻止。");
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }, token);
        await InspectAndListAsync(token);
    });
    public Task LoadSavedAsync() => InGate(async token =>
    {
        if (HasAttempt) { Message = Report.Message; return; }
        Models = []; Message = "正在读取当前账户的官方模型目录；尚未发送验收消息。";
        account = ReadSavedAccountId(); await InspectAndListAsync(token);
    });
    string ReadSavedAccountId()
    {
        SafeMetadata(SavedAccountPath);
        if (!File.Exists(SavedAccountPath) || new FileInfo(SavedAccountPath).Length > 2048) throw new InvalidDataException("没有有效的应用SIWC注册记录，请登录。");
        using var document = JsonDocument.Parse(File.ReadAllBytes(SavedAccountPath));
        var record = document.RootElement.Deserialize<AccountReceipt>() ?? throw new InvalidDataException("应用注册记录缺失。");
        if (record.SchemaVersion != 1 || record.AccountId.Length != 64 || !record.AccountId.All(Uri.IsHexDigit)) throw new InvalidDataException("应用账户记录无效。");
        return record.AccountId;
    }
    async Task InspectAndListAsync(CancellationToken token)
    {
        var permission = await store.WithExclusiveAsync(account, (credential, _) =>
        {
            if (credential is null || credential.SignedOut || credential.AccountId != account) throw new InvalidOperationException("独立SIWC凭据不存在或身份不匹配。");
            return Task.FromResult<(SiwcCredential?, bool)>((null, credential.HasPlanPermission));
        }, token);
        Authenticated = true; PlanScopeValidated = permission;
        SafeMetadata(AccountPath); AtomicStore.Write(AccountPath, new AccountReceipt(1, account, permission, DateTimeOffset.UtcNow));
        if (!permission) { Message = "账户身份及签名已验证，但未授予套餐推理scope；禁止发送。不会切换API Key。"; return; }
        var catalog = await new SiwcModelCatalog(auth, protocol).ReadAsync(account, token);
        Models = catalog.Models.ToArray();
        var catalogPath = Path.Combine(metadataDirectory, "model-catalog.json"); SafeMetadata(catalogPath);
        AtomicStore.Write(catalogPath, new { SchemaVersion = 1, AccountId = account, At = DateTimeOffset.UtcNow,
            Source = "Official SIWC GET /v1/models", Models = Models.Select(m => new { m.Slug, m.DisplayName }).ToArray() });
        Message = Models.Length == 0 ? "套餐scope通过，但官方账户目录没有可选择模型；禁止发送。"
            : "身份、签名与套餐scope已验证；模型目录已读取。实际推理资格待单条真实请求确认，精确额度和积分消耗仍Unknown。";
    }
    public Task SendAsync(string model, bool sendingConfirmed, bool unknownQuotaConfirmed, bool billingConfirmed) => InGate(async token =>
    {
        if (HasAttempt) { Message = Report.Message; return; }
        if (!Authenticated || !PlanScopeValidated || !Models.Any(m => m.Slug == model)) throw new InvalidOperationException("先完成当前独立账户和模型核验。");
        var approval = new SiwcSingleRequestApproval(Ticket.RequestId, account, model, Ticket.SessionId, DateTimeOffset.UtcNow,
            sendingConfirmed, unknownQuotaConfirmed, billingConfirmed);
        Message = "正在核验独立账户与所选模型，随后仅发送这一条请求；请等待原生完成信号。"; Changed?.Invoke();
        var report = await acceptance.SendOnceAsync(approval, token); Message = report.Message;
    });
    async Task InGate(Func<CancellationToken, Task> action)
    {
        if (!await gate.WaitAsync(0)) return;
        Busy = true; Changed?.Invoke();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); timeout.CancelAfter(TimeSpan.FromMinutes(6));
        try { await action(timeout.Token); }
        catch (Exception error)
        {
            var code = error is System.Net.Http.HttpRequestException h ? "HTTP " + (h.StatusCode is { } s ? ((int)s).ToString() : "连接失败")
                : error is OperationCanceledException ? "已取消或超时" : error.GetType().Name;
            var detail = error is InvalidOperationException or InvalidDataException ? WorkflowText.Safe(error.Message, 300) : "未读取或记录令牌；请查看连接/授权状态。";
            Message = $"未完成：{code}。{detail}";
            if (error is System.Net.Http.HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden })
            { Authenticated = PlanScopeValidated = false; Models = []; }
            // Only metadata is saved; exception strings and OAuth URLs are never persisted.
            try { var errorPath = Path.Combine(metadataDirectory, "last-error.json"); SafeMetadata(errorPath); AtomicStore.Write(errorPath, new { At = DateTimeOffset.UtcNow, Code = code,
                RequestId = Ticket.RequestId, Attempted = HasAttempt }); }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { Message += " 诊断文件保存失败；不发起新请求。"; }
        }
        finally { Busy = false; gate.Release(); Changed?.Invoke(); }
    }
    public void Cancel() { try { stop.Cancel(); } catch (ObjectDisposedException) { } }
    internal static async Task<int> RunBodyReadOnceAsync(string[] args)
    {
        if (!args.Contains("--confirm-one-request") || !args.Contains("--confirm-unknown-quota") || !args.Contains("--acknowledge-existing-credit-uncertainty")) return 2;
        var verification = args.Contains("--siwc-body-read-verification-once");
        var campaignIndex = Array.IndexOf(args, "--acceptance-campaign");
        if (verification && args.Contains("--siwc-body-read-once") || args.Count(a => a == "--acceptance-campaign") > 1
            || campaignIndex >= 0 && (!verification || campaignIndex + 1 >= args.Length)) return 2;
        var campaign = campaignIndex >= 0 ? args[campaignIndex + 1] : null;
        using var controller = new SiwcAcceptanceController(verification ? SiwcAcceptanceKind.BodyReadVerification : SiwcAcceptanceKind.BodyReadRepair, campaign);
        // This is a new, separately authorized fixed 456 request. The old 789 claim/report are never rewritten.
        if (controller.HasAttempt) return controller.Report is { Status: "Completed", ResponseCompleted: true, BodyReadable: true, ExactMatch: true } ? 0 : 1;
        if (verification)
        {
            var parent = Path.GetDirectoryName(controller.metadataDirectory) ?? throw new InvalidDataException("验收目录无效。");
            controller.acceptance.VerifyDistinctFromHistory(Directory.EnumerateDirectories(parent));
        }
        await controller.LoadSavedAsync();
        const string model = "gpt-6-luna";
        if (!controller.Authenticated || !controller.PlanScopeValidated || !controller.Models.Any(m => m.Slug == model)) return 1;
        await controller.SendAsync(model, true, true, true);
        return controller.Report is { Status: "Completed", ResponseCompleted: true, BodyReadable: true, ExactMatch: true } ? 0 : 1;
    }
    internal static async Task<int> DiagnoseModelCatalogAsync()
    {
        var stage = "CreateController";
        try
        {
        using var controller = new SiwcAcceptanceController();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        stage = "ReadAppAccountReceipt";
        var accountId = controller.ReadSavedAccountId();
        stage = "ValidateOwnCredential";
        var credential = await controller.auth.GetAccessAsync(accountId, timeout.Token);
        stage = "GetOfficialModelDirectory";
        var catalog = await controller.protocol.ModelsAsync(credential.AccessToken, timeout.Token);
        var arrayName = catalog.TryGetProperty("models", out var rows) && rows.ValueKind == JsonValueKind.Array ? "models"
            : catalog.TryGetProperty("data", out rows) && rows.ValueKind == JsonValueKind.Array ? "data" : "None";
        var entries = arrayName == "None" ? Array.Empty<JsonElement>() : rows.EnumerateArray().ToArray();
        string Text(JsonElement row, string name) => row.ValueKind == JsonValueKind.Object && row.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        var diagnostics = entries.Select((row, index) => new {
            Index = index, Fields = row.ValueKind == JsonValueKind.Object ? row.EnumerateObject().Select(p => p.Name).ToArray() : [],
            Slug = Text(row,"slug"), Id = Text(row,"id"), Visibility = Text(row,"visibility"),
            DisplayNameMissing = string.IsNullOrEmpty(Text(row,"display_name")), DisplayNameHasControls = Text(row,"display_name").Any(char.IsControl),
            SlugHasPreviouslyRejectedCharacters = Text(row,"slug").Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))
        }).ToArray();
        var path = Path.Combine(DataRoot, "model-catalog-diagnostic.json"); SafeMetadata(path);
        AtomicStore.Write(path, new { At = DateTimeOffset.UtcNow, ReadOnly = true, ModelInferenceCalls = 0,
            RootFields = catalog.EnumerateObject().Select(p => p.Name).ToArray(), Array = arrayName, Count = entries.Length,
            Listed = diagnostics.Count(r => r.Visibility == "list"), Entries = diagnostics });
        await controller.LoadSavedAsync();
        return controller.Models.Length > 0 ? 0 : 1;
        }
        catch (Exception error)
        {
            var reason = error is InvalidDataException && error.Message == "SIWC protocol response too large." ? "ResponseExceededConfiguredLimit"
                : error is System.Net.Http.HttpRequestException h ? "HTTP_" + (h.StatusCode is { } status ? ((int)status).ToString() : "TransportFailure")
                : error.GetType().Name;
            var path = Path.Combine(DataRoot, "model-catalog-diagnostic.json"); SafeMetadata(path);
            AtomicStore.Write(path, new { At = DateTimeOffset.UtcNow, ReadOnly = true, ModelInferenceCalls = 0, Stage = stage, Error = reason });
            return 1;
        }
    }
    static void SafeMetadata(string path)
    { if (!AutomationStore.IsSafeDPath(path) || !AutomationStore.IsSafeDPath(path + ".bak")) throw new InvalidDataException("应用元数据路径异常，已阻断。"); }
    public void Dispose() { Cancel(); protocol.Dispose(); }
}

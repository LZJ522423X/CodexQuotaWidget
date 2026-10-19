using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexQuotaWidget.Core.Automation;

namespace CodexQuotaWidget.Core.ControlCenter;

public sealed record SiwcSingleRequestApproval(Guid RequestId, string AccountId, string Model, string SessionId,
    DateTimeOffset ConfirmedAt, bool UserConfirmedSending, bool UnknownQuotaExceptionConfirmed, bool BillingUncertaintyAcknowledged)
{
    public bool IsValid => RequestId != Guid.Empty && AccountId is { Length: 64 } && AccountId.All(Uri.IsHexDigit)
        && Model is { Length: > 0 and <= 128 } && Model.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
        && SessionId == "siwc-acceptance-" + RequestId.ToString("N") && ConfirmedAt <= DateTimeOffset.UtcNow
        && ConfirmedAt > DateTimeOffset.UtcNow.AddMinutes(-5) && UserConfirmedSending && UnknownQuotaExceptionConfirmed && BillingUncertaintyAcknowledged;
}
public sealed record SiwcAcceptanceTicket(int SchemaVersion, Guid RequestId, string SessionId, string PromptHash);
public enum SiwcAcceptanceKind { Original, BodyReadRepair, BodyReadVerification }
public sealed record SiwcAcceptanceReport
{
    public int SchemaVersion { get; init; } = 1;
    public Guid RequestId { get; init; }
    public string SessionId { get; init; } = "";
    public string AccountId { get; init; } = "";
    public string Model { get; init; } = "";
    public string Status { get; init; } = "NotSent";
    public DateTimeOffset UpdatedAt { get; init; }
    public SiwcAcceptanceEvidenceKind EvidenceKind { get; init; }
    public bool PlanScopeValidated { get; init; }
    public bool HttpStreamAccepted { get; init; }
    public bool? GptRequestReceived { get; init; }
    public bool ResponseCompleted { get; init; }
    public bool BodyReadable { get; init; }
    public bool ExactMatch { get; init; }
    public string Body { get; init; } = "";
    public string ResponseId { get; init; } = "";
    public int TextDeltaEvents { get; init; }
    public SiwcTokenUsage? Usage { get; init; }
    public string QuotaEvidence { get; init; } = "Unknown_NoSupportedSiwcQuotaSource";
    public string CreditsConsumed { get; init; } = "Unknown_NotExposedByResponsesUsage";
    public string AuthenticationChannel { get; init; } = "Independent SIWC OAuth + current-user DPAPI";
    public string Endpoint { get; init; } = "https://api.openai.com/v1/responses";
    public string DiagnosticCode { get; init; } = "";
    public SiwcHttpResponseEvidence? HttpResponse { get; init; }
    public SiwcRejectedEventDiagnostic? RejectedEvent { get; init; }
    public SiwcResponseShape[] EventStructures { get; init; } = [];
    public string Message { get; init; } = "等待用户登录、选择模型并亲自确认；未发请求。";
}

/// <summary>Single fixed text request. Independent of WorkflowLoopService and the Codex executor.</summary>
public sealed class SiwcSingleRequestAcceptance
{
    public const string Prompt = "这是AI Control Center独立GPT通信验收，请只回复：SIWC正文读取成功789";
    public const string ExpectedBody = "SIWC正文读取成功789";
    readonly string directory;
    readonly SiwcAuthorizationService auth;
    readonly ISiwcSingleRequestTransport transport;
    readonly SiwcAcceptanceKind kind;
    readonly SemaphoreSlim gate = new(1, 1);
    public SiwcAcceptanceTicket Ticket { get; }
    public string ReportPath => Path.Combine(directory, "report.json");
    string ClaimPath => Path.Combine(directory, "request-claim.json");
    public bool HasAttempt => new[] { ClaimPath, ReportPath, ReportPath + ".bak" }.Any(p => File.Exists(p) || Directory.Exists(p));
    public const string BodyReadPrompt = "这是AI Control Center正文解析验收，请只回复：第二次连接验证成功456";
    public const string BodyReadVerificationPrompt = "这是 AI Control Center 正文读取验收，请只回复：第二次连接验证成功456";
    public const string BodyReadExpected = "第二次连接验证成功456";
    public string RequestPrompt => PromptFor(kind);
    public string RequiredReply => kind == SiwcAcceptanceKind.Original ? ExpectedBody : BodyReadExpected;
    static string PromptFor(SiwcAcceptanceKind kind) => kind switch
    {
        SiwcAcceptanceKind.Original => Prompt,
        SiwcAcceptanceKind.BodyReadRepair => BodyReadPrompt,
        SiwcAcceptanceKind.BodyReadVerification => BodyReadVerificationPrompt,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
    public static string ResolveVerificationCampaign(string standardDirectory, string? campaign)
    {
        Safe(standardDirectory);
        if (campaign is null) return standardDirectory;
        if (campaign.Length is < 1 or > 64 || campaign[0] is not (>= 'a' and <= 'z')
            || campaign.Any(c => c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-')))
            throw new InvalidDataException("独立验收轮次名称无效；未创建请求。");
        var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(standardDirectory)) + "-" + campaign;
        Safe(candidate); return candidate;
    }
    public void VerifyDistinctFromHistory(IEnumerable<string> historicalDirectories)
    {
        var current = Path.TrimEndingDirectorySeparator(directory);
        var parent = Path.GetDirectoryName(current);
        foreach (var source in historicalDirectories)
        {
            var historical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source)); Safe(historical);
            if (!string.Equals(Path.GetDirectoryName(historical), parent, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("历史验收目录超出当前元数据范围；未发送。");
            if (string.Equals(historical, current, StringComparison.OrdinalIgnoreCase)) continue;
            var path = Path.Combine(historical, "acceptance-request.json"); Safe(path);
            if (Directory.Exists(path)) throw new InvalidDataException("历史验收身份路径异常；未发送。");
            if (!File.Exists(path))
            {
                if (new[] { "request-claim.json", "report.json", "report.json.bak" }.Any(n => File.Exists(Path.Combine(historical, n)) || Directory.Exists(Path.Combine(historical, n))))
                    throw new InvalidDataException("历史请求身份缺失但存在尝试证据；未发送。");
                continue;
            }
            if (new FileInfo(path).Length > 2048) throw new InvalidDataException("历史验收身份异常；未发送。");
            using var document = JsonDocument.Parse(File.ReadAllBytes(path)); SiwcIdToken.Unique(document.RootElement);
            var ticket = document.RootElement.Deserialize<SiwcAcceptanceTicket>() ?? throw new InvalidDataException("历史验收身份无法核验。");
            if (ticket.SchemaVersion != 1 || ticket.RequestId == Guid.Empty || ticket.SessionId != "siwc-acceptance-" + ticket.RequestId.ToString("N")
                || ticket.PromptHash is not { Length: 64 } || !ticket.PromptHash.All(Uri.IsHexDigit)
                || ticket.RequestId == Ticket.RequestId || ticket.SessionId == Ticket.SessionId)
                throw new InvalidDataException("新旧请求身份冲突或历史身份无效；未发送。");
        }
    }
    public SiwcSingleRequestAcceptance(string directory, SiwcAuthorizationService auth, ISiwcSingleRequestTransport transport,
        SiwcAcceptanceKind kind = SiwcAcceptanceKind.Original)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        this.kind = kind;
        Safe(directory); Directory.CreateDirectory(directory); Safe(directory);
        this.directory = Path.GetFullPath(directory); this.auth = auth; this.transport = transport;
        var path = Path.Combine(directory, "acceptance-request.json"); Safe(path);
        if (!File.Exists(path) && HasAttempt) throw new InvalidDataException("旧验收身份缺失但存在尝试证据，禁止生成新RequestId。");
        try
        {
            using var created = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
            var id = Guid.NewGuid(); var ticket = new SiwcAcceptanceTicket(1, id, "siwc-acceptance-" + id.ToString("N"), Hash(RequestPrompt));
            JsonSerializer.Serialize(created, ticket); created.Flush(true);
        }
        catch (IOException) when (File.Exists(path)) { }
        if (new FileInfo(path).Length > 2048) throw new InvalidDataException("单条验收身份记录过大，禁止创建替代请求。");
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path)); SiwcIdToken.Unique(doc.RootElement);
        Ticket = doc.RootElement.Deserialize<SiwcAcceptanceTicket>() ?? throw new InvalidDataException("单条验收身份缺失。");
        if (Ticket.SchemaVersion != 1 || Ticket.RequestId == Guid.Empty || Ticket.SessionId != "siwc-acceptance-" + Ticket.RequestId.ToString("N") || Ticket.PromptHash != Hash(RequestPrompt))
            throw new InvalidDataException("单条验收身份异常，不创建新RequestId绕过。");
        Safe(ClaimPath); Safe(ReportPath); Safe(ReportPath + ".bak");
    }
    public SiwcAcceptanceReport ReadReport()
    {
        Safe(ReportPath); Safe(ClaimPath);
        if (!File.Exists(ReportPath)) return new() { RequestId = Ticket.RequestId, SessionId = Ticket.SessionId,
            Status = HasAttempt ? "Blocked" : "NotSent", DiagnosticCode = HasAttempt ? "siwc_prior_attempt_unconfirmed" : "",
            Message = HasAttempt ? "已有请求claim但无完成证据；禁止重发。" : "等待用户登录、选择模型并亲自确认；未发请求。" };
        if (new FileInfo(ReportPath).Length > 256 * 1024) throw new InvalidDataException("验收报告异常，禁止重发。");
        using var document = JsonDocument.Parse(File.ReadAllBytes(ReportPath)); SiwcIdToken.Unique(document.RootElement);
        var r = document.RootElement.Deserialize<SiwcAcceptanceReport>() ?? throw new InvalidDataException("验收报告缺失。");
        if (r.RequestId != Ticket.RequestId || r.SessionId != Ticket.SessionId) throw new InvalidDataException("验收报告身份不匹配。");
        return r.Status == "Sending" ? r with { Status = "Blocked", Message = "上次请求在发送期间中断，远端完成状态未知，不能重发。" } : r;
    }
    public async Task<SiwcAcceptanceReport> SendOnceAsync(SiwcSingleRequestApproval approval, CancellationToken token = default)
    {
        if (!approval.IsValid || approval.RequestId != Ticket.RequestId || approval.SessionId != Ticket.SessionId)
            throw new InvalidOperationException("账户、模型、RequestId、费用确认及一次性额度例外必须全部匹配。");
        if (!await gate.WaitAsync(0, token).ConfigureAwait(false)) throw new InvalidOperationException("验收正在处理，不重复发送。");
        bool claimed = false; SiwcAcceptanceReport? report = null;
        var eventStructures = new List<SiwcResponseShape>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            var lockPath = Path.Combine(directory, "single-request.lock"); Safe(lockPath);
            using var lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            Safe(ClaimPath); if (HasAttempt) return ReadReport();
            // Scope and model discovery are read-only preflight, not proof that inference has completed.
            var credential = await auth.GetAccessAsync(approval.AccountId, timeout.Token).ConfigureAwait(false);
            if (credential.AccountId != approval.AccountId || !credential.HasPlanPermission || credential.ExpiresAt <= DateTimeOffset.UtcNow)
                throw new InvalidOperationException("独立SIWC身份或套餐scope不可用，未发送。");
            var models = await new SiwcModelCatalog(auth, transport).ReadAsync(approval.AccountId, timeout.Token).ConfigureAwait(false);
            if (!models.Models.Any(m => m.Slug == approval.Model)) throw new InvalidOperationException("所选模型不在当前账户可用目录中，未发送。");
            credential = await auth.GetAccessAsync(approval.AccountId, timeout.Token).ConfigureAwait(false);
            if (!approval.IsValid) throw new InvalidOperationException("确认已过期，未发送。");
            var body = BuildBody(approval.Model, kind); transport.ArmSingleRequest(approval, body);
            using (var claim = new FileStream(ClaimPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { JsonSerializer.Serialize(claim, new { SchemaVersion = 1, Ticket.RequestId, Ticket.SessionId, approval.AccountId, approval.Model,
                Ticket.PromptHash, BodyHash = Convert.ToHexString(SHA256.HashData(body)), approval.ConfirmedAt, approval.UserConfirmedSending,
                approval.UnknownQuotaExceptionConfirmed, approval.BillingUncertaintyAcknowledged, EvidenceKind = transport.EvidenceKind }); claim.Flush(true); }
            claimed = true;
            report = new() { RequestId = Ticket.RequestId, SessionId = Ticket.SessionId, AccountId = approval.AccountId, Model = approval.Model,
                PlanScopeValidated = true, EvidenceKind = transport.EvidenceKind, Status = "Sending", Message = "唯一请求已记录；等待官方流完成，不会自动重试。" };
            Save(report); timeout.Token.ThrowIfCancellationRequested();
            await using var stream = await transport.ResponsesAsync(credential.AccessToken, body, timeout.Token).ConfigureAwait(false);
            report = report with { HttpStreamAccepted = true, HttpResponse = (transport as ISiwcResponseEvidenceSource)?.ResponseEvidence }; Save(report);
            var reply = await SiwcResponsesReader.ReadAsync(stream, timeout.Token,
                shape => { if (eventStructures.Count < 64) eventStructures.Add(shape); },
                kind != SiwcAcceptanceKind.Original ? approval.Model : null,
                kind == SiwcAcceptanceKind.BodyReadVerification ? new(approval.Model, RequiredReply) : null).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            var match = string.Equals(reply.Text, RequiredReply, StringComparison.Ordinal);
            report = report with { GptRequestReceived = true, ResponseCompleted = true, BodyReadable = true, ExactMatch = match,
                Body = reply.Text, ResponseId = reply.ResponseId, TextDeltaEvents = reply.TextDeltaEvents, Usage = reply.Usage,
                EventStructures = eventStructures.ToArray(),
                Status = match ? "Completed" : "Mismatch", DiagnosticCode = match ? "siwc_body_exact_match" : "siwc_body_mismatch",
                Message = match ? "已读取完整正文并严格匹配；单条验收结束，不启动循环。" : "收到完整正文但严格比对不一致；验收停止，不再请求。" };
            Save(report); return report;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or HttpRequestException
            or OperationCanceledException or InvalidOperationException or JsonException or KeyNotFoundException or FormatException
            or DecoderFallbackException or System.ComponentModel.Win32Exception)
        {
            if (!claimed) throw; // Preflight did not create a model request; report error without consuming a new ID.
            report = (report ?? new() { RequestId = Ticket.RequestId, SessionId = Ticket.SessionId }) with { Status = "Blocked",
                HttpResponse = (transport as ISiwcResponseEvidenceSource)?.ResponseEvidence, EventStructures = eventStructures.ToArray(),
                RejectedEvent = SiwcResponseEvidence.GetRejection(error),
                DiagnosticCode = error is SiwcProtocolException p ? p.Code : error is HttpRequestException h ? "siwc_http_" + (h.StatusCode is { } s ? ((int)s).ToString() : "unavailable")
                    : error is OperationCanceledException ? "siwc_cancelled_or_timeout"
                    : error is JsonException or KeyNotFoundException or FormatException or DecoderFallbackException ? "siwc_response_structure_invalid" : "siwc_result_unconfirmed",
                Message = "请求已尝试，结果未确认；保留claim与证据，禁止自动重发。" };
            try { Save(report); } catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { }
            return report;
        }
        finally { gate.Release(); }
    }
    void Save(SiwcAcceptanceReport r) { Safe(ReportPath); Safe(ReportPath + ".bak"); AtomicStore.Write(ReportPath, r with { UpdatedAt = DateTimeOffset.UtcNow }); }
    public static byte[] BuildBody(string model, SiwcAcceptanceKind kind = SiwcAcceptanceKind.Original) => JsonSerializer.SerializeToUtf8Bytes(new { model,
        instructions = "Follow the user's exact short text request. Return text only. Do not request tools or perform other actions.",
        input = new[] { new { role = "user", content = PromptFor(kind) } }, store = false, stream = true });
    static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    static void Safe(string path) { if (!AutomationStore.IsSafeDPath(path)) throw new InvalidDataException("验收只允许D盘普通路径。"); }
}

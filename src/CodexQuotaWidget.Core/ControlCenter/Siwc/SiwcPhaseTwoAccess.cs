using System.Security.Cryptography;

namespace CodexQuotaWidget.Core.ControlCenter;

/// <summary>Separate build and instance capability. It never enables the normal workflow connector.</summary>
public sealed class SiwcPhaseTwoAccess
{
    public static bool IsExperimentalBuild
    {
        get
        {
#if SIWC_PHASE2_ACCEPTANCE
            return true;
#else
            return false;
#endif
        }
    }
    byte[]? bodyHash;
    internal SiwcStrictSseRequirements? MissingContentTypeRequirements { get; private set; }
    readonly SiwcAcceptanceKind kind;
    int armed, consumed;
    SiwcPhaseTwoAccess(SiwcAcceptanceKind kind) { this.kind = kind; }
    public static SiwcPhaseTwoAccess ForUserAcceptanceWindow(SiwcAcceptanceKind kind = SiwcAcceptanceKind.Original)
    {
        if (!IsExperimentalBuild) throw new InvalidOperationException("SIWC第二阶段只允许专门的实验构建。");
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        return new(kind);
    }
    public void RequireAccountAccess()
    { if (!IsExperimentalBuild) throw new InvalidOperationException("SIWC实验授权入口未编译启用。"); }
    internal void Arm(SiwcSingleRequestApproval approval, byte[] body)
    {
        RequireAccountAccess();
        if (!approval.IsValid || !body.AsSpan().SequenceEqual(SiwcSingleRequestAcceptance.BuildBody(approval.Model, kind)))
            throw new InvalidOperationException("单条验收确认或固定正文不匹配。");
        if (Interlocked.CompareExchange(ref armed, 1, 0) != 0) throw new InvalidOperationException("单条验收已确认过，不允许重新授权第二条。");
        bodyHash = SHA256.HashData(body);
        if (kind is SiwcAcceptanceKind.BodyReadRepair or SiwcAcceptanceKind.BodyReadVerification)
            MissingContentTypeRequirements = new(approval.Model, SiwcSingleRequestAcceptance.BodyReadExpected);
    }
    internal void Consume(byte[] body)
    {
        RequireAccountAccess();
        if (Volatile.Read(ref armed) != 1 || bodyHash is null || !CryptographicOperations.FixedTimeEquals(bodyHash, SHA256.HashData(body)))
            throw new InvalidOperationException("没有匹配的单条正文确认；请求被阻止。");
        if (Interlocked.CompareExchange(ref consumed, 1, 0) != 0) throw new InvalidOperationException("单条推理许可已使用，不自动重试。");
    }
}

public interface ISiwcSingleRequestTransport : ISiwcProtocolTransport
{
    SiwcAcceptanceEvidenceKind EvidenceKind { get; }
    void ArmSingleRequest(SiwcSingleRequestApproval approval, byte[] body);
}
public enum SiwcAcceptanceEvidenceKind { Unverified, OfficialHttps, OfflineFixture }

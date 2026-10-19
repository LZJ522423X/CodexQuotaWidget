using System.Text.Json;
using CodexQuotaWidget.Core.Automation;

namespace CodexQuotaWidget.Core.Observation;

public sealed record CodexSessionBinding
{
    public int SchemaVersion { get; init; } = 1;
    public Guid BindingId { get; init; }
    public string ProjectRoot { get; init; } = "";
    public string SessionId { get; init; } = "";
    public string SessionFilePath { get; init; } = "";
    public DateTimeOffset SavedAt { get; init; }
    public bool IsBound => ProjectRoot.Length > 0 && SessionId.Length > 0 && SessionFilePath.Length > 0 && BindingId != Guid.Empty;
}

public sealed record CodexSessionBindingResult(bool Success, string Message, CodexSessionBinding Binding);

/// <summary>Explicit user selection on D. Primary-only reads: a .bak never revives an old binding.</summary>
public sealed class CodexSessionBindingStore
{
    readonly object gate = new();
    readonly string codexHome;
    readonly Func<DateTimeOffset> clock;
    public string BindingPath { get; }
    public CodexSessionBindingStore(string dataRoot, string? codexHome = null, Func<DateTimeOffset>? clock = null)
    {
        if (!AutomationStore.IsSafeDPath(dataRoot)) throw new ArgumentException("绑定数据目录必须是D盘普通路径。", nameof(dataRoot));
        BindingPath = Path.Combine(Path.GetFullPath(dataRoot), "codex-session-binding.json");
        this.codexHome = Path.GetFullPath(codexHome ?? CodexSessionPaths.NativeHome);
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }
    public CodexSessionBindingResult Load()
    {
        lock (gate)
        {
            try
            {
                CheckStorePaths();
                if (!File.Exists(BindingPath)) return File.Exists(BindingPath + ".bak")
                    ? new(false, "绑定主记录缺失；未恢复旧备份绑定。", new()) : new(true, "尚未绑定Codex会话。", new());
                if (new FileInfo(BindingPath).Length > 16 * 1024) throw new InvalidDataException("绑定记录过大。");
                var binding = JsonSerializer.Deserialize<CodexSessionBinding>(File.ReadAllText(BindingPath)) ?? throw new InvalidDataException("绑定为空。");
                Validate(binding, allowClear: true);
                return new(true, binding.IsBound ? "已加载明确选择的Codex会话。" : "Codex会话绑定已清除。", binding);
            }
            catch (Exception error) when (CodexSessionPaths.IsReadFailure(error))
            { return new(false, "绑定主记录或选定会话不可验证；未切换至其他会话。", new()); }
        }
    }
    public CodexSessionBindingResult Save(string projectRoot, string sessionId)
    {
        lock (gate)
        {
            try
            {
                if (!CodexSessionPaths.IsSafeLocalDirectory(projectRoot)) throw new InvalidDataException("项目必须是存在的本地普通目录。");
                if (!StatusEvidence.ValidIdentity(sessionId)) throw new InvalidDataException("会话id不合理。");
                var catalog = new CodexSessionCatalog(codexHome, clock).Read();
                if (!catalog.Success) throw new InvalidDataException("原生会话清单不可用。");
                var matches = catalog.Sessions.Where(entry => entry.SessionId == sessionId).ToArray();
                if (matches.Length != 1) throw new InvalidDataException("会话不在当前可验证清单内或id存在歧义。");
                var binding = new CodexSessionBinding
                {
                    BindingId = Guid.NewGuid(), ProjectRoot = Path.GetFullPath(projectRoot), SessionId = sessionId,
                    SessionFilePath = matches[0].SessionFilePath, SavedAt = clock()
                };
                Validate(binding, false);
                CheckStorePaths();
                AtomicStore.Write(BindingPath, binding);
                return new(true, "项目与明确选择的Codex会话已保存；不会自动切换会话。", binding);
            }
            catch (Exception error) when (CodexSessionPaths.IsReadFailure(error))
            { return new(false, "绑定未保存；项目、来源路径或会话metadata验证失败。", new()); }
        }
    }
    public CodexSessionBindingResult Clear()
    {
        lock (gate)
        {
            try
            {
                CheckStorePaths();
                var cleared = new CodexSessionBinding { BindingId = Guid.NewGuid(), SavedAt = clock() };
                AtomicStore.Write(BindingPath, cleared);
                return new(true, "绑定已清除；未删除会话文件，未启动任何任务。", cleared);
            }
            catch (Exception error) when (CodexSessionPaths.IsReadFailure(error))
            { return new(false, "绑定清除未保存；现有状态保持不变。", new()); }
        }
    }
    void CheckStorePaths()
    {
        if (!AutomationStore.IsSafeDPath(BindingPath) || !AutomationStore.IsSafeDPath(BindingPath + ".bak"))
            throw new InvalidDataException("绑定记录路径异常。");
    }
    internal void Validate(CodexSessionBinding binding, bool allowClear)
    {
        if (binding.SchemaVersion != 1 || binding.BindingId == Guid.Empty || binding.SavedAt == default
            || binding.SavedAt > clock() || binding.ProjectRoot is null || binding.SessionId is null || binding.SessionFilePath is null)
            throw new InvalidDataException("绑定记录结构无效。");
        if (allowClear && binding.ProjectRoot.Length == 0 && binding.SessionId.Length == 0 && binding.SessionFilePath.Length == 0) return;
        if (!binding.IsBound || !CodexSessionPaths.IsSafeLocalDirectory(binding.ProjectRoot)) throw new InvalidDataException("绑定项目不可用。");
        var metadata = CodexSessionPaths.ReadMetadata(binding.SessionFilePath, codexHome);
        if (metadata.SessionId != binding.SessionId) throw new InvalidDataException("选定路径的会话id已变化。");
    }
}

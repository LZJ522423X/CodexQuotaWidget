using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace CodexQuotaWidget.Core.Observation;

public sealed record CodexSessionCatalogEntry
{
    public string SessionId { get; init; } = "";
    public string SessionFilePath { get; init; } = "";
    public string DeclaredProject { get; init; } = "";
    public string[] RuntimeWorkspaceRoots { get; init; } = [];
    public DateTimeOffset LastModified { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public string Source { get; init; } = "Codex session";
    public string MetadataFingerprint { get; init; } = "";
}

public sealed record CodexSessionCatalogResult
{
    public bool Success { get; init; }
    public bool CoverageComplete { get; init; }
    public string Message { get; init; } = "";
    public DateTimeOffset ReadAt { get; init; }
    public CodexSessionCatalogEntry[] Sessions { get; init; } = [];
}

/// <summary>Bounded metadata-only discovery. Never reads message/tool body records or writes Codex home.</summary>
public sealed class CodexSessionCatalog
{
    public const int MaximumCandidates = 128;
    public const int MaximumMetadataBytes = 64 * 1024;
    readonly string codexHome;
    readonly Func<DateTimeOffset> clock;
    public CodexSessionCatalog(string? codexHome = null, Func<DateTimeOffset>? clock = null)
    { this.codexHome = Path.GetFullPath(codexHome ?? CodexSessionPaths.NativeHome); this.clock = clock ?? (() => DateTimeOffset.UtcNow); }
    public Task<CodexSessionCatalogResult> ReadAsync(CancellationToken cancellationToken = default)
        => Task.Run(() => Read(cancellationToken), cancellationToken);

    internal CodexSessionCatalogResult Read(CancellationToken cancellationToken = default)
    {
        var now = clock();
        var entries = new List<CodexSessionCatalogEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var complete = true;
        var count = 0;
        try
        {
            var sessions = Path.Combine(codexHome, "sessions");
            if (!CodexSessionPaths.IsSafeLocalDirectory(codexHome) || !CodexSessionPaths.IsSafeLocalDirectory(sessions))
                return new() { ReadAt = now, Message = "原生Codex会话目录不可访问或路径异常。" };
            var dates = Enumerable.Range(0, 7).SelectMany(days => new[] { now.LocalDateTime.Date.AddDays(-days), now.UtcDateTime.Date.AddDays(-days) }).Distinct();
            foreach (var date in dates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var day = Path.Combine(sessions, date.ToString("yyyy"), date.ToString("MM"), date.ToString("dd"));
                if (!Directory.Exists(day)) continue;
                if (!CodexSessionPaths.IsSafeLocalDirectory(day)) { complete = false; continue; }
                foreach (var file in Directory.EnumerateFiles(day, "rollout-*.jsonl", SearchOption.TopDirectoryOnly))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!seen.Add(file)) continue;
                    if (count++ >= MaximumCandidates) { complete = false; break; }
                    try { entries.Add(CodexSessionPaths.ReadMetadata(file, codexHome)); }
                    catch (Exception error) when (CodexSessionPaths.IsReadFailure(error)) { complete = false; }
                }
                if (count > MaximumCandidates) break;
            }
            var duplicateIds = entries.GroupBy(entry => entry.SessionId, StringComparer.Ordinal).Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
            if (duplicateIds.Count > 0) { complete = false; entries.RemoveAll(entry => duplicateIds.Contains(entry.SessionId)); }
            return new()
            {
                Success = true, CoverageComplete = complete, ReadAt = now,
                Sessions = entries.OrderByDescending(entry => entry.LastModified).ToArray(),
                Message = complete ? $"找到 {entries.Count} 个原生Codex会话；未读取任务正文。" : $"找到 {entries.Count} 个可验证会话；范围受限或部分metadata不可用，未读取正文。"
            };
        }
        catch (Exception error) when (CodexSessionPaths.IsReadFailure(error))
        { return new() { ReadAt = now, Message = "原生会话清单读取失败；未自动选择其他会话。" }; }
    }
}

internal static class CodexSessionPaths
{
    public static string NativeHome => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    public static bool IsReadFailure(Exception error)
        => error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException or System.Security.SecurityException;

    public static bool IsSafeLocalDirectory(string path) => IsSafeLocalPath(path, true);
    public static bool IsSafeLocalPath(string path, bool directory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 || !Path.IsPathFullyQualified(path)
                || path.StartsWith("\\", StringComparison.Ordinal) || path.Length < 3 || path[1] != ':'
                || path.IndexOf(':', 2) >= 0 || path.Contains('\0')) return false;
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full)!;
            if (root.Length != 3 || !char.IsAsciiLetter(root[0]) || !Directory.Exists(root)) return false;
            var current = root;
            foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                if (!Directory.Exists(current) && !File.Exists(current)) return false;
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
            }
            return directory ? Directory.Exists(full) : File.Exists(full);
        }
        catch (Exception error) when (IsReadFailure(error)) { return false; }
    }

    public static bool IsSessionFile(string file, string codexHome)
    {
        try
        {
            if (!IsSafeLocalDirectory(codexHome) || !IsSafeLocalPath(file, false)) return false;
            var sessions = Path.GetFullPath(Path.Combine(codexHome, "sessions"));
            var full = Path.GetFullPath(file);
            if (!full.StartsWith(sessions + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
            var parts = Path.GetRelativePath(sessions, full).Split(Path.DirectorySeparatorChar);
            return parts.Length == 4 && parts[0].Length == 4 && parts[1].Length == 2 && parts[2].Length == 2
                && DateTime.TryParseExact(string.Join('-', parts.Take(3)), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
                && parts[3].StartsWith("rollout-", StringComparison.Ordinal) && parts[3].EndsWith(".jsonl", StringComparison.Ordinal)
                && parts[3].Length <= 220 && parts[3].All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or '+');
        }
        catch (Exception error) when (IsReadFailure(error)) { return false; }
    }

    public static CodexSessionCatalogEntry ReadMetadata(string path, string codexHome)
    {
        if (!IsSessionFile(path, codexHome)) throw new InvalidDataException("会话文件不在原生sessions普通目录中。");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[(int)Math.Min(stream.Length, CodexSessionCatalog.MaximumMetadataBytes)];
        stream.ReadExactly(bytes);
        var newline = Array.IndexOf(bytes, (byte)'\n');
        if (newline < 0) throw new InvalidDataException("会话metadata未完成或超过读取上限。");
        using var document = JsonDocument.Parse(new ReadOnlyMemory<byte>(bytes, 0, newline), new() { MaxDepth = 64 });
        var root = document.RootElement;
        if (Text(root, "type") != "session_meta") throw new InvalidDataException("首记录不是Codex session_meta。");
        var meta = Element(root, "payload");
        var id = Text(meta, "id");
        if (!StatusEvidence.ValidIdentity(id)) throw new InvalidDataException("会话metadata id无效。");
        var cwd = SafeDeclaredPath(Text(meta, "cwd"));
        var roots = Element(meta, "runtime_workspace_roots");
        var declaredRoots = roots.ValueKind == JsonValueKind.Array
            ? roots.EnumerateArray().Take(32).Where(value => value.ValueKind == JsonValueKind.String).Select(value => SafeDeclaredPath(value.GetString())).Where(value => value.Length > 0).ToArray() : [];
        var originator = Text(meta, "originator");
        var source = Element(meta, "source");
        var label = source.ValueKind == JsonValueKind.Object && source.TryGetProperty("subagent", out _) ? "Codex Subagent"
            : originator == "Codex Desktop" ? "Codex Desktop"
            : originator is "codex_cli_rs" or "codex" ? "Codex CLI" : "Codex session";
        DateTimeOffset? started = DateTimeOffset.TryParse(Text(meta, "timestamp"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp) ? timestamp : null;
        return new()
        {
            SessionId = id!, SessionFilePath = Path.GetFullPath(path), DeclaredProject = cwd,
            RuntimeWorkspaceRoots = declaredRoots, LastModified = new DateTimeOffset(File.GetLastWriteTimeUtc(path)), StartedAt = started,
            Source = label, MetadataFingerprint = Convert.ToHexString(SHA256.HashData(bytes.AsSpan(0, newline)))
        };
    }

    public static string SafeDeclaredPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 1024 || !Path.IsPathFullyQualified(value)
            || value.StartsWith("\\", StringComparison.Ordinal) || value.Length < 3 || value[1] != ':' || value.IndexOf(':', 2) >= 0) return "";
        try { return Path.GetFullPath(value); } catch (Exception error) when (IsReadFailure(error)) { return ""; }
    }
    static JsonElement Element(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;
    static string? Text(JsonElement element, string name) => Element(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
}

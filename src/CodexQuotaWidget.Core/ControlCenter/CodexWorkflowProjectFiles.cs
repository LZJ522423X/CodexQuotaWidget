using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CodexQuotaWidget.Core.Automation;

namespace CodexQuotaWidget.Core.ControlCenter;

public sealed record CodexProjectToolContent([property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("text")] string Text);
public sealed record CodexProjectToolReply([property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("contentItems")] CodexProjectToolContent[] ContentItems);

/// <summary>Explicitly authorized project file I/O only: no commands, network or system actions.</summary>
public sealed class CodexWorkflowProjectFiles
{
    public const string ToolNamespace = "acc_project";
    public const int MaximumFileBytes = 128 * 1024, MaximumFiles = 500, MaximumCalls = 256;
    readonly string root;
    readonly string[] authorizedWritePaths;
    readonly object gate = new();
    readonly Dictionary<string, (string Fingerprint, CodexProjectToolReply Reply)> calls = new(StringComparer.Ordinal);
    readonly string backupInstance = Guid.NewGuid().ToString("N");
    readonly HashSet<string> backedUp = new(StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".cs", ".csproj", ".xaml", ".md", ".json", ".txt", ".props", ".targets", ".sln" };
    static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
        { ".git", ".codex", "bin", "obj", "artifacts", "publish", "backups", "logs", ".acc-workflow", "node_modules" };
    static readonly Regex Secrets = new("-----BEGIN [A-Z ]*PRIVATE KEY-----|sk-(?:proj-|svcacct-)?[A-Za-z0-9_-]{12,}|eyJ[A-Za-z0-9_-]{8,}\\.[A-Za-z0-9_-]{8,}\\.[A-Za-z0-9_-]{8,}|(?im)(?:api[_-]?key|access[_-]?token|refresh[_-]?token|id[_-]?token|client[_-]?secret|password)[\\\"'\\s]*[:=]\\s*[\\\"']?[A-Za-z0-9_+./=-]{8,}", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public CodexWorkflowProjectFiles(string projectRoot, IEnumerable<string>? authorizedWritePaths = null)
    {
        root = Path.GetFullPath(projectRoot).TrimEnd('\\');
        this.authorizedWritePaths = WorkflowAuthorization.NormalizeWritePaths(authorizedWritePaths ?? []);
        ValidateRoot();
    }
    public static object[] DynamicToolSpecs(bool readOnly = false)
    {
        var tools = new List<object>
        {
                Spec("list_project_files", "List at most 500 allowed project text files. Build output, protected paths and links are excluded.", Array.Empty<string>(), new Dictionary<string, object>()),
                Spec("read_project_file", "Read a permitted project text file, returning its SHA256 and content. Credential patterns are refused.", new[] { "relativePath" }, new Dictionary<string, object> { ["relativePath"] = new { type = "string", maxLength = 512 } }),
        };
        if (!readOnly)
            tools.Add(Spec("write_project_file", "Atomically write only a file within the caller-frozen authorized write paths. Build definitions (.csproj/.props/.targets/.sln) are read-only. Supply its current SHA256, or MISSING only for a new file. The first pre-write version of each file in this run is retained; no command is run.", new[] { "relativePath", "expectedSHA", "content" }, new Dictionary<string, object>
                {
                    ["relativePath"] = new { type = "string", maxLength = 512 },
                    ["expectedSHA"] = new { type = "string", pattern = "^(?:[A-Fa-f0-9]{64}|MISSING)$" },
                    ["content"] = new { type = "string", maxLength = MaximumFileBytes }
                }));
        return new object[] { new { type = "namespace", name = ToolNamespace,
            description = "Caller-authorized local project text files only. No shell, network, credentials, system actions or approvals.", tools = tools.ToArray() } };
    }
    static object Spec(string name, string description, string[] required, Dictionary<string, object> properties)
        => new { type = "function", name, description, inputSchema = new { type = "object", properties, required, additionalProperties = false } };

    public CodexProjectToolReply Invoke(string tool, JsonElement arguments, string callId, IEnumerable<string>? stepWritePaths = null)
    {
        lock (gate)
        {
            if (string.IsNullOrWhiteSpace(callId) || callId.Length > 512 || callId.Any(char.IsControl)) return Failure("invalid_call_identity");
            if (arguments.ValueKind == JsonValueKind.Undefined) return Failure("invalid_tool_arguments");
            string[] turnScope;
            try
            {
                turnScope = stepWritePaths is null ? authorizedWritePaths : WorkflowAuthorization.NormalizeWritePaths(stepWritePaths);
                if (turnScope.Any(path => !WorkflowAuthorization.AllowsWrite(authorizedWritePaths, path))) return Failure("step_scope_outside_task_authorization");
            }
            catch (Exception error) when (error is WorkflowRpcException or ArgumentException) { return Failure("step_scope_invalid"); }
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tool + "\n" + arguments.GetRawText() + "\n" + WorkflowAuthorization.ComputeWriteScopeHash(turnScope))));
            if (calls.TryGetValue(callId, out var existing)) return existing.Fingerprint == fingerprint ? existing.Reply : Failure("call_identity_collision");
            if (calls.Count >= MaximumCalls) return Failure("tool_call_limit");
            CodexProjectToolReply reply;
            try
            {
                ValidateRoot(); ValidateArguments(tool, arguments);
                reply = tool switch
                {
                    "list_project_files" => List(),
                    "read_project_file" => Read(Text(arguments, "relativePath")),
                    "write_project_file" => Write(Text(arguments, "relativePath"), Text(arguments, "expectedSHA"), Text(arguments, "content"), turnScope),
                    _ => Failure("tool_not_allowed")
                };
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or JsonException or DecoderFallbackException or RegexMatchTimeoutException)
            { reply = Failure(error is ProjectFileRejected rejected ? rejected.Code : "project_file_io_unverified"); }
            calls.Add(callId, (fingerprint, reply)); return reply;
        }
    }
    static void ValidateArguments(string tool, JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object) throw new ProjectFileRejected("invalid_tool_arguments");
        var expected = tool switch
        {
            "list_project_files" => Array.Empty<string>(), "read_project_file" => new[] { "relativePath" },
            "write_project_file" => new[] { "relativePath", "expectedSHA", "content" },
            _ => throw new ProjectFileRejected("tool_not_allowed")
        };
        var fields = arguments.EnumerateObject().Select(property => property.Name).ToArray();
        if (fields.Length != expected.Length || fields.Distinct(StringComparer.Ordinal).Count() != fields.Length
            || expected.Any(key => !fields.Contains(key, StringComparer.Ordinal))) throw new ProjectFileRejected("invalid_tool_arguments");
    }
    CodexProjectToolReply List()
    {
        var files = new List<string>(); var directories = new Queue<(string Path, int Depth)>(); directories.Enqueue((root, 0));
        var visited = 0; var enumerated = 0; var truncated = false;
        while (directories.Count > 0 && files.Count < MaximumFiles && visited < 1000 && enumerated < 20_000)
        {
            var (directory, depth) = directories.Dequeue(); ValidateOrdinaryDirectory(directory); visited++;
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++enumerated > 20_000) { truncated = true; break; }
                var name = Path.GetFileName(entry); if (Excluded.Contains(name) || ProtectedName(name)) continue;
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0)
                { if (depth < 16 && directories.Count < 1000) directories.Enqueue((entry, depth + 1)); else truncated = true; continue; }
                if (!Extensions.Contains(Path.GetExtension(name))) continue;
                files.Add(Path.GetRelativePath(root, entry)); if (files.Count == MaximumFiles) { truncated = true; break; }
            }
        }
        truncated |= directories.Count > 0;
        return Success(new { files, truncated, maximumFiles = MaximumFiles });
    }
    CodexProjectToolReply Read(string relative)
    {
        var path = Resolve(relative); var (bytes, content) = ReadText(path);
        return Success(new { relativePath = relative, sha256 = Hash(bytes), content });
    }
    CodexProjectToolReply Write(string relative, string expected, string content, string[] turnScope)
    {
        if (Path.GetExtension(relative).ToLowerInvariant() is ".csproj" or ".props" or ".targets" or ".sln")
            throw new ProjectFileRejected("protected_build_definition");
        if (!WorkflowAuthorization.AllowsWrite(authorizedWritePaths, relative)) throw new ProjectFileRejected("file_outside_authorized_write_scope");
        if (!WorkflowAuthorization.AllowsWrite(turnScope, relative)) throw new ProjectFileRejected("file_outside_authorized_step_scope");
        if (content.Length > MaximumFileBytes || content.IndexOf('\0') >= 0) throw new ProjectFileRejected("file_too_large_or_binary");
        var bytes = new UTF8Encoding(false, true).GetBytes(content);
        if (bytes.Length > MaximumFileBytes) throw new ProjectFileRejected("file_too_large_or_binary");
        RejectSecrets(content);
        if (expected != "MISSING" && (expected.Length != 64 || !expected.All(Uri.IsHexDigit))) throw new ProjectFileRejected("invalid_expected_sha");
        var path = Resolve(relative); var existed = File.Exists(path);
        byte[]? oldBytes = null;
        if (existed)
        {
            if (expected == "MISSING") throw new ProjectFileRejected("file_already_exists");
            oldBytes = ReadText(path).Bytes;
            if (!Hash(oldBytes).Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new ProjectFileRejected("expected_sha_mismatch");
        }
        else if (expected != "MISSING") throw new ProjectFileRejected("file_missing");
        if (oldBytes is not null && oldBytes.AsSpan().SequenceEqual(bytes)) return Success(new { relativePath = relative, sha256 = Hash(bytes), changed = false });
        var work = Path.Combine(root, ".acc-workflow"); ValidateOrdinaryDirectory(root);
        if (!AutomationStore.IsSafeDPath(work)) throw new ProjectFileRejected("unsafe_work_directory");
        Directory.CreateDirectory(work); ValidateOrdinaryDirectory(work);
        var backupDirectory = Path.Combine(work, backupInstance);
        var backup = Path.Combine(backupDirectory, Hash(Encoding.UTF8.GetBytes(Path.GetRelativePath(root, path).ToUpperInvariant())) + ".txt");
        var temp = Path.Combine(work, "pending-write-" + Guid.NewGuid().ToString("N") + ".tmp");
        var displaced = Path.Combine(work, "pending-replaced-" + Guid.NewGuid().ToString("N") + ".tmp");
        var preserveDisplaced = false;
        try
        {
            if (oldBytes is not null && !backedUp.Contains(path))
            {
                if (!AutomationStore.IsSafeDPath(backupDirectory)) throw new ProjectFileRejected("unsafe_work_directory");
                Directory.CreateDirectory(backupDirectory); ValidateOrdinaryDirectory(backupDirectory);
                ValidateOrdinaryFileOrMissing(backup);
                using (var saved = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { saved.Write(oldBytes); saved.Flush(true); }
                backedUp.Add(path);
            }
            using (var staged = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { staged.Write(bytes); staged.Flush(true); }
            // Revalidate ancestry, source and expected contents immediately before replacement.
            Resolve(relative); ValidateOrdinaryDirectory(work);
            if (existed)
            {
                if (!Hash(ReadText(path).Bytes).Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new ProjectFileRejected("expected_sha_mismatch");
                // Keep the actual file displaced by the atomic operation. A separate pre-read
                // cannot by itself promise OS-level compare-and-swap against another editor.
                File.Replace(temp, path, displaced);
                try
                {
                    if (!Hash(ReadText(displaced).Bytes).Equals(expected, StringComparison.OrdinalIgnoreCase))
                        throw new ProjectFileRejected("source_changed_during_replace");
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or DecoderFallbackException)
                {
                    var restored = TryRestoreDisplaced(path, displaced, bytes);
                    preserveDisplaced = !restored;
                    throw new ProjectFileRejected(restored ? "source_changed_during_replace" : "write_race_recovery_required");
                }
            }
            else
            {
                if (File.Exists(path)) throw new ProjectFileRejected("file_already_exists");
                File.Move(temp, path, overwrite: false);
            }
            var verified = ReadText(path).Bytes;
            if (!verified.AsSpan().SequenceEqual(bytes)) throw new ProjectFileRejected("write_verification_failed");
            backedUp.Add(path);
            return Success(new { relativePath = relative, sha256 = Hash(verified), changed = true, backup = File.Exists(backup) ? Path.GetRelativePath(root, backup) : null });
        }
        finally
        {
            if (AutomationStore.IsSafeDPath(temp) && File.Exists(temp)) File.Delete(temp);
            if (!preserveDisplaced && AutomationStore.IsSafeDPath(displaced) && File.Exists(displaced)) File.Delete(displaced);
        }
    }
    static bool TryRestoreDisplaced(string path, string displaced, byte[] ourBytes)
    {
        try
        {
            ValidateOrdinaryDirectory(Path.GetDirectoryName(path)!); ValidateOrdinaryDirectory(Path.GetDirectoryName(displaced)!);
            ValidateOrdinaryFileOrMissing(path); ValidateOrdinaryFileOrMissing(displaced);
            if (!ReadText(path).Bytes.AsSpan().SequenceEqual(ourBytes)) return false;
            File.Replace(displaced, path, null); return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or DecoderFallbackException) { return false; }
    }
    string Resolve(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Length > 512 || Path.IsPathFullyQualified(relative)
            || relative.IndexOfAny([':', '\0']) >= 0 || relative.Any(char.IsControl)) throw new ProjectFileRejected("invalid_relative_path");
        var parts = relative.Replace('/', '\\').Split('\\');
        if (parts.Any(part => part.Length == 0 || part is "." or ".." || part.Trim() != part || part.EndsWith('.')
            || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || ReservedDeviceName(part) || Excluded.Contains(part) || ProtectedName(part)))
            throw new ProjectFileRejected("protected_or_traversing_path");
        if (!Extensions.Contains(Path.GetExtension(relative))) throw new ProjectFileRejected("unsupported_file_extension");
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) throw new ProjectFileRejected("path_outside_project");
        ValidateRoot(); ValidateOrdinaryDirectory(Path.GetDirectoryName(path)!); ValidateOrdinaryFileOrMissing(path);
        return path;
    }
    void ValidateRoot()
    {
        if (root.Length <= 3 || !AutomationStore.IsSafeDPath(root) || !Directory.Exists(root)) throw new ProjectFileRejected("unsafe_project_root");
        ValidateOrdinaryDirectory(root);
    }
    static void ValidateOrdinaryDirectory(string path)
    {
        if (!AutomationStore.IsSafeDPath(path) || !Directory.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new ProjectFileRejected("unsafe_directory_ancestry");
    }
    static void ValidateOrdinaryFileOrMissing(string path)
    {
        if (!AutomationStore.IsSafeDPath(path) || Directory.Exists(path)) throw new ProjectFileRejected("unsafe_file_path");
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new ProjectFileRejected("unsafe_file_path");
    }
    static (byte[] Bytes, string Content) ReadText(string path)
    {
        ValidateOrdinaryFileOrMissing(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > MaximumFileBytes) throw new ProjectFileRejected("file_too_large_or_binary");
        var bytes = new byte[(int)input.Length]; input.ReadExactly(bytes);
        var content = new UTF8Encoding(false, true).GetString(bytes);
        if (content.IndexOf('\0') >= 0) throw new ProjectFileRejected("file_too_large_or_binary");
        RejectSecrets(content); return (bytes, content);
    }
    static bool ProtectedName(string name) => name.Equals("AGENTS.md", StringComparison.OrdinalIgnoreCase)
        || name.Equals(".env", StringComparison.OrdinalIgnoreCase) || name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase)
        || name.Equals("auth", StringComparison.OrdinalIgnoreCase) || name.StartsWith("auth.", StringComparison.OrdinalIgnoreCase)
        || name.Equals("env", StringComparison.OrdinalIgnoreCase) || name.StartsWith("env.", StringComparison.OrdinalIgnoreCase)
        || name.Equals("credentials.json", StringComparison.OrdinalIgnoreCase) || name.Equals("secrets.json", StringComparison.OrdinalIgnoreCase)
        || name.Equals("config.toml", StringComparison.OrdinalIgnoreCase);
    static bool ReservedDeviceName(string name)
    {
        var stem = name.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
            || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
                && (stem[3] is >= '1' and <= '9' or '¹' or '²' or '³');
    }
    static void RejectSecrets(string content) { if (Secrets.IsMatch(content)) throw new ProjectFileRejected("credential_pattern_refused"); }
    static string Text(JsonElement element, string key) => element.GetProperty(key).ValueKind == JsonValueKind.String
        ? element.GetProperty(key).GetString()! : throw new ProjectFileRejected("invalid_tool_arguments");
    static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    static CodexProjectToolReply Success(object value) => new(true, new[] { new CodexProjectToolContent("inputText", JsonSerializer.Serialize(value)) });
    static CodexProjectToolReply Failure(string code) => new(false, new[] { new CodexProjectToolContent("inputText", JsonSerializer.Serialize(new { error = code, noCommandsOrSystemActions = true })) });
    sealed class ProjectFileRejected(string code) : IOException { public string Code { get; } = code; }
}

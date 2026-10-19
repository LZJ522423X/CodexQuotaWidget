using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;

namespace CodexQuotaWidget.Core.ControlCenter;

public sealed record SafeFinishCheckpointResult(bool Success, string Message, string? ResumePath = null, string? ManifestPath = null, string? SourceSnapshotPath = null);
public sealed record CheckpointSourceFile(string RelativePath, string Sha256, long Length) { public string Path => RelativePath; }
public sealed record CheckpointDirtyFile(string RelativePath, string Status);
public sealed record SafeFinishManifest
{
    public string Owner { get; init; } = "CodexQuotaWidget.SafeFinishCheckpoint";
    public int SchemaVersion { get; init; } = 1;
    public string ProjectRoot { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public string Version { get; init; } = "unknown";
    public string GitCommit { get; init; } = "";
    public string GitVersion { get; init; } = "";
    public List<CheckpointSourceFile> SourceFiles { get; init; } = [];
    public List<CheckpointSourceFile> Files => SourceFiles;
    public List<CheckpointDirtyFile> DirtyFiles { get; init; } = [];
    public int ExcludedFileCount { get; init; }
    public string SourceSnapshotSha256 { get; init; } = "";
    public string GitPatchSha256 { get; init; } = "";
    public string ResumeSha256 { get; init; } = "";
}

/// <summary>Read-only Git/source capture. Never stages, commits, closes programs, or shuts down Windows.</summary>
public sealed class SafeFinishCheckpoint
{
    const string Owner = "CodexQuotaWidget.SafeFinishCheckpoint";
    readonly string projectRoot, dataRoot, resumeRoot;
    readonly Func<DateTimeOffset> clock;
    readonly SemaphoreSlim gate = new(1, 1);
    static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    { ".git", "bin", "obj", "artifacts", "publish", "node_modules", ".vs", ".idea", "auth", "credentials", "secrets", "logs", "cache", "backups" };
    static readonly HashSet<string> ExcludedExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".exe", ".dll", ".pdb", ".msi", ".nupkg", ".dmp", ".db", ".sqlite", ".sqlite3", ".pem", ".pfx", ".p12", ".key" };
    static readonly Regex SecretMarker = new(@"(?:sk-[A-Za-z0-9_-]{16,}|gh[pousr]_[A-Za-z0-9]{20,}|-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----|(?i:bearer)\s+[A-Za-z0-9._~+/-]{20,})", RegexOptions.Compiled);

    public SafeFinishCheckpoint(string projectRoot, string dataRoot, Func<DateTimeOffset>? clock = null)
    {
        this.projectRoot = ValidateDRoot(projectRoot); this.dataRoot = ValidateDRoot(dataRoot);
        if (!Directory.Exists(this.projectRoot)) throw new ArgumentException("项目目录不存在");
        if (Within(this.projectRoot, this.dataRoot)) throw new ArgumentException("数据目录不能包含项目源目录");
        EnsureNoReparse(this.projectRoot); EnsureNoReparse(this.dataRoot);
        resumeRoot = Path.Combine(this.dataRoot, "resume"); this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<SafeFinishCheckpointResult> CreateAsync(ControlCenterSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        string? staging = null, previous = null, previousResume = null;
        bool oldMoved = false, newMoved = false, resumeWritten = false, resumeExisted = false, entered = false, committed = false, preserveProtection = false;
        var latest = Path.Combine(resumeRoot, "latest");
        var resumePath = Path.Combine(dataRoot, "codex_resume_state.md");
        var manifestPath = Path.Combine(latest, "manifest.json"); var sourcePath = Path.Combine(latest, "source.zip");
        try
        {
            await gate.WaitAsync(cancellationToken); entered = true;
            cancellationToken.ThrowIfCancellationRequested();
            EnsureNoReparse(projectRoot); EnsureNoReparse(dataRoot); EnsureNoReparse(resumeRoot); EnsureNoReparse(resumePath);
            Directory.CreateDirectory(resumeRoot);
            var id = Guid.NewGuid().ToString("N");
            staging = Path.Combine(resumeRoot, ".checkpoint-" + id);
            previous = Path.Combine(resumeRoot, ".previous-" + id);
            previousResume = Path.Combine(resumeRoot, ".previous-resume-" + id + ".md");
            Directory.CreateDirectory(staging);
            if (Directory.Exists(latest)) ValidateOwnedLatest(latest);
            var git = FindGit();
            var gitVersion = await RunGit(git, ["--version"], staging, cancellationToken);
            var commit = (await RunGit(git, ["rev-parse", "--verify", "HEAD"], staging, cancellationToken)).Trim();
            if (!Regex.IsMatch(commit, @"^[0-9a-fA-F]{40,64}$")) throw new InvalidDataException("Git HEAD 无法可靠确认");
            var initialStatus = ParseStatus(await RunGit(git, ["status", "--porcelain=v1", "-z", "--untracked-files=all", "--ignore-submodules=all"], staging, cancellationToken));
            var paths = SplitPaths(await RunGit(git, ["ls-files", "-z", "--cached", "--others", "--exclude-standard"], staging, cancellationToken));
            foreach (var item in initialStatus) paths.Add(item.RelativePath);
            if (paths.Count > 5000) throw new InvalidDataException("源文件数量超过检查点安全上限");
            var admitted = new List<string>(); int excluded = 0;
            foreach (var relative in paths.Order(StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var full = SourcePath(relative);
                if (Excluded(relative) || Within(full, dataRoot)) { excluded++; continue; }
                EnsureNoReparse(full);
                if (Directory.Exists(full)) { excluded++; continue; }
                if (File.Exists(full))
                {
                    if (new FileInfo(full).Length > 8 * 1024 * 1024) throw new InvalidDataException("单个源文件超过安全上限");
                    if (await ContainsSecret(full, cancellationToken)) { excluded++; continue; }
                }
                admitted.Add(relative);
            }
            if (admitted.Count == 0) throw new InvalidDataException("没有可验证的项目源文件");
            var admittedSet = admitted.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var dirty = initialStatus.Where(x => admittedSet.Contains(x.RelativePath)).OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
            var sources = await CreateSourceZip(Path.Combine(staging, "source.zip"), admitted, cancellationToken);
            var patch = new StringBuilder();
            // Only exact, admitted paths can enter the saved patch. No raw Git payload is logged.
            foreach (var batch in Batches(admitted))
            {
                var args = new List<string> { "diff", "--binary", "--no-ext-diff", "--no-textconv", "--no-renames", "HEAD", "--" };
                args.AddRange(batch.Select(x => ":(literal)" + x));
                var part = await RunGit(git, args, staging, cancellationToken);
                if (SecretMarker.IsMatch(part)) throw new InvalidDataException("Git 修改含敏感内容，未保存其原文");
                patch.Append(part);
                if (patch.Length > 32 * 1024 * 1024) throw new InvalidDataException("Git 修改内容超过安全上限");
            }
            foreach (var file in sources)
            {
                var full = SourcePath(file.RelativePath); EnsureNoReparse(full);
                if (!File.Exists(full) || Hash(full) != file.Sha256) throw new InvalidDataException("保存期间源文件发生变化，请停止编辑后重试");
            }
            var after = ParseStatus(await RunGit(git, ["status", "--porcelain=v1", "-z", "--untracked-files=all", "--ignore-submodules=all"], staging, cancellationToken))
                .Where(x => !Excluded(x.RelativePath) && !Within(SourcePath(x.RelativePath), dataRoot)).ToList();
            if (after.Any(x => !admittedSet.Contains(x.RelativePath) && !paths.Contains(x.RelativePath))
                || !dirty.SequenceEqual(after.Where(x => admittedSet.Contains(x.RelativePath)).OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase)))
                throw new InvalidDataException("保存期间 Git 状态发生变化，请停止编辑后重试");
            WriteText(Path.Combine(staging, "working-tree.patch"), patch.ToString());
            var created = clock(); var version = ReadVersion();
            WriteText(Path.Combine(staging, "resume.md"), BuildResume(snapshot, created, commit, version, sources, dirty, excluded));
            var manifest = new SafeFinishManifest
            {
                ProjectRoot = projectRoot, CreatedAt = created, Version = version, GitCommit = commit, GitVersion = gitVersion.Trim(),
                SourceFiles = sources, DirtyFiles = dirty, ExcludedFileCount = excluded,
                SourceSnapshotSha256 = Hash(Path.Combine(staging, "source.zip")),
                GitPatchSha256 = Hash(Path.Combine(staging, "working-tree.patch")), ResumeSha256 = Hash(Path.Combine(staging, "resume.md"))
            };
            WriteText(Path.Combine(staging, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            cancellationToken.ThrowIfCancellationRequested();
            EnsureNoReparse(resumePath); resumeExisted = File.Exists(resumePath);
            if (Directory.Exists(latest)) { Directory.Move(latest, previous); oldMoved = true; }
            Directory.Move(staging, latest); newMoved = true; staging = null;
            // The outer resume note is the commit marker. Defer cancellation during this short transaction.
            var resumeTemp = Path.Combine(resumeRoot, ".resume-" + id + ".tmp");
            try
            {
                File.Copy(Path.Combine(latest, "resume.md"), resumeTemp, false);
                if (resumeExisted) File.Replace(resumeTemp, resumePath, previousResume);
                else File.Move(resumeTemp, resumePath);
                resumeWritten = true;
            }
            finally { if (File.Exists(resumeTemp)) File.Delete(resumeTemp); }
            if (Hash(resumePath) != manifest.ResumeSha256 || Hash(sourcePath) != manifest.SourceSnapshotSha256
                || Hash(Path.Combine(latest, "working-tree.patch")) != manifest.GitPatchSha256)
                throw new InvalidDataException("检查点写后校验失败");
            committed = true;
            if (oldMoved) { DeleteOwnedTemporary(previous!); oldMoved = false; }
            if (File.Exists(previousResume)) File.Delete(previousResume);
            return new(true, "源码、Git 当前状态和继续开发说明已保存", resumePath, manifestPath, sourcePath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or OperationCanceledException or ArgumentException or JsonException or XmlException or System.ComponentModel.Win32Exception)
        {
            bool rollbackOk = true;
            try
            {
                if (!committed && resumeWritten)
                {
                    if (resumeExisted && File.Exists(previousResume)) File.Copy(previousResume, resumePath, true);
                    else if (!resumeExisted && File.Exists(resumePath)) File.Delete(resumePath);
                }
                if (!committed && newMoved && Directory.Exists(latest)) DeleteOwnedTemporary(latest);
                if (!committed && oldMoved && Directory.Exists(previous)) Directory.Move(previous, latest);
                if (!committed) oldMoved = false;
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or InvalidDataException) { rollbackOk = false; }
            preserveProtection = !rollbackOk || committed;
            if (committed) return new(false, "新检查点已写入并校验，但临时保护副本清理未完成；禁止关机", resumePath, manifestPath, sourcePath);
            var reason = e switch
            {
                OperationCanceledException => "检查点已取消，未允许关机",
                InvalidDataException => e.Message,
                UnauthorizedAccessException => "检查点目录权限不足",
                _ => "检查点保存失败，可能存在文件占用或磁盘错误"
            };
            return new(false, reason + (rollbackOk ? "；原检查点已保留" : "；临时保护副本已保留，禁止关机"));
        }
        finally
        {
            if (staging is not null) TryDeleteTemporary(staging);
            if (previousResume is not null && !oldMoved && !preserveProtection && File.Exists(previousResume))
                try { File.Delete(previousResume); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            if (entered) gate.Release();
        }
    }

    async Task<List<CheckpointSourceFile>> CreateSourceZip(string destination, List<string> admitted, CancellationToken token)
    {
        var result = new List<CheckpointSourceFile>(); long total = 0;
        using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough))
        {
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true, Encoding.UTF8))
                foreach (var relative in admitted)
                {
                    token.ThrowIfCancellationRequested();
                    var full = SourcePath(relative); EnsureNoReparse(full);
                    if (!File.Exists(full)) continue; // Deletions remain in the binary Git patch.
                    await using var input = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
                    total += input.Length;
                    if (input.Length > 8 * 1024 * 1024) throw new InvalidDataException("保存期间源文件大小发生异常变化");
                    if (total > 64 * 1024 * 1024) throw new InvalidDataException("源文件总量超过安全上限");
                    var bytes = new byte[(int)input.Length];
                    await input.ReadExactlyAsync(bytes, token);
                    if (!bytes.Take(4096).Contains((byte)0) && SecretMarker.IsMatch(Encoding.UTF8.GetString(bytes)))
                        throw new InvalidDataException("保存期间源文件出现敏感内容，未写入源码快照");
                    using var entry = zip.CreateEntry(relative.Replace('\\', '/'), CompressionLevel.Optimal).Open();
                    await entry.WriteAsync(bytes, token);
                    result.Add(new(relative.Replace('\\', '/'), Convert.ToHexString(SHA256.HashData(bytes)), input.Length));
                }
            output.Flush(true);
        }
        return result;
    }

    async Task<string> RunGit(string executable, IEnumerable<string> arguments, string temporary, CancellationToken token)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = projectRoot, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var arg in new[] { "--no-pager", "-C", projectRoot, "-c", "core.fsmonitor=false", "-c", "core.untrackedCache=false" }) start.ArgumentList.Add(arg);
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0"; start.Environment["TEMP"] = temporary; start.Environment["TMP"] = temporary;
        start.Environment.Remove("GIT_EXTERNAL_DIFF");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new InvalidDataException("无法启动本机 Git");
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token); var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var output = await stdout; _ = await stderr;
            if (process.ExitCode != 0) throw new InvalidDataException("Git 只读检查失败，未更改索引或工作区");
            if (output.Length > 32 * 1024 * 1024) throw new InvalidDataException("Git 输出超过安全上限");
            return output;
        }
        finally
        {
            try { if (process.Id != 0 && !process.HasExited) { process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); } }
            catch (InvalidOperationException) { }
        }
    }

    string BuildResume(ControlCenterSnapshot snapshot, DateTimeOffset created, string commit, string version, List<CheckpointSourceFile> sources, List<CheckpointDirtyFile> dirty, int excluded)
    {
        var statusPath = Path.Combine(projectRoot, "docs", "AI-CONTROL-CENTER-STATUS.md");
        string progress = "开发进度文档尚未创建；请先检查当前代码和本次修改。";
        if (File.Exists(statusPath))
        {
            EnsureNoReparse(statusPath);
            progress = string.Join("\n", File.ReadLines(statusPath, Encoding.UTF8).Take(200).Where(x => !SecretMarker.IsMatch(x)));
            if (progress.Length > 14000) progress = progress[..14000];
        }
        var text = new StringBuilder();
        text.AppendLine("# CodexQuotaWidget 继续开发状态").AppendLine()
            .AppendLine($"保存时间：{created:O}").AppendLine($"项目：{projectRoot}").AppendLine($"版本：{version}；Git HEAD：{commit}")
            .AppendLine($"源码快照：{Path.Combine(resumeRoot, "latest", "source.zip")}")
            .AppendLine($"校验清单：{Path.Combine(resumeRoot, "latest", "manifest.json")}")
            .AppendLine($"未提交修改：{Path.Combine(resumeRoot, "latest", "working-tree.patch")}")
            .AppendLine("Git 索引和工作区未自动暂存、提交或重置；原代码仍保留。")
            .AppendLine("仅保存磁盘文件，无法读取 IDE 未保存的编辑缓冲。关闭工具出现保存提示必须先保存，否则禁止关机。")
            .AppendLine($"本地任务：待处理 {snapshot.Tasks.Count(t => t.State == WorkItemState.Queued)}，运行跟踪 {snapshot.Tasks.Count(t => t.State == WorkItemState.Running)}，暂停 {snapshot.Tasks.Count(t => t.State == WorkItemState.Paused)}。不保存任务正文或账户密钥。")
            .AppendLine($"源文件 {sources.Count} 个；排除缓存、编译产物和敏感文件 {excluded} 个。")
            .AppendLine().AppendLine("## 当前进度").AppendLine().AppendLine(progress)
            .AppendLine().AppendLine("## 继续开发前的检查").AppendLine()
            .AppendLine("1. 阅读本文件与 docs/AI-CONTROL-CENTER-STATUS.md，保留已有用户修改。")
            .AppendLine("2. 对照 manifest.json SHA256 核验源文件；zip 和 binary patch 用于恢复，不要直接覆盖工作区。")
            .AppendLine("3. 使用 D 盘 .NET SDK 构建，重跑核心、布局和动画检查；本检查点不宣称尚未验证的修改已通过测试。")
            .AppendLine("4. 刷新真实 Codex 额度后再继续；本地任务状态不表示 AI 实际执行。")
            .AppendLine().AppendLine("## 当前未提交文件").AppendLine();
        foreach (var item in dirty) text.AppendLine("- " + item.Status + " " + item.RelativePath);
        return text.ToString();
    }

    string ReadVersion()
    {
        foreach (var relative in new[] { "src/CodexQuotaWidget/CodexQuotaWidget.csproj", "src/CodexQuotaWidget.Core/CodexQuotaWidget.Core.csproj" })
        {
            var path = SourcePath(relative); if (!File.Exists(path)) continue; EnsureNoReparse(path);
            using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            while (reader.Read()) if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Version")
            { var value = reader.ReadElementContentAsString(); if (Regex.IsMatch(value, @"^[0-9A-Za-z.+-]{1,40}$")) return value; }
        }
        return "unknown";
    }

    string SourcePath(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathFullyQualified(relative) || relative.Contains(':') || relative.Any(char.IsControl)) throw new InvalidDataException("Git 文件路径不安全");
        if (relative.Replace('\\', '/').Split('/').Any(x => x is ".." or "")) throw new InvalidDataException("Git 文件路径越界");
        var full = Path.GetFullPath(Path.Combine(projectRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!Within(full, projectRoot) || full.Equals(projectRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Git 文件路径越界");
        return full;
    }
    static bool Excluded(string relative)
    {
        var parts = relative.Replace('\\', '/').Split('/'); var name = parts[^1];
        return parts.Any(ExcludedDirectories.Contains) || name.StartsWith(".env", StringComparison.OrdinalIgnoreCase)
            || name is ".npmrc" or ".pypirc" || name.Equals("auth.json", StringComparison.OrdinalIgnoreCase)
            || name.Equals("credentials.json", StringComparison.OrdinalIgnoreCase) || name.Equals("secrets.json", StringComparison.OrdinalIgnoreCase)
            || name.Equals("id_rsa", StringComparison.OrdinalIgnoreCase) || name.Equals("id_ed25519", StringComparison.OrdinalIgnoreCase)
            || ExcludedExtensions.Contains(Path.GetExtension(name)) || SecretMarker.IsMatch(relative);
    }
    static async Task<bool> ContainsSecret(string path, CancellationToken token)
    {
        var bytes = await File.ReadAllBytesAsync(path, token);
        return !bytes.Take(4096).Contains((byte)0) && SecretMarker.IsMatch(Encoding.UTF8.GetString(bytes));
    }
    static List<CheckpointDirtyFile> ParseStatus(string output)
    {
        var pieces = output.Split('\0', StringSplitOptions.RemoveEmptyEntries); var result = new List<CheckpointDirtyFile>();
        for (int i = 0; i < pieces.Length; i++)
        {
            if (pieces[i].Length < 4 || pieces[i][2] != ' ') throw new InvalidDataException("Git 状态格式无效");
            var status = pieces[i][..2]; result.Add(new(pieces[i][3..].Replace('\\', '/'), status));
            if (status.Contains('R') || status.Contains('C'))
            {
                if (++i >= pieces.Length) throw new InvalidDataException("Git 重命名状态格式无效");
                result.Add(new(pieces[i].Replace('\\', '/'), "D "));
            }
        }
        return result.Distinct().ToList();
    }
    static HashSet<string> SplitPaths(string output) => output.Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
    static IEnumerable<List<string>> Batches(IEnumerable<string> paths)
    {
        var batch = new List<string>(); int length = 0;
        foreach (var path in paths)
        {
            if (batch.Count > 0 && length + path.Length + 16 > 16000) { yield return batch; batch = []; length = 0; }
            batch.Add(path); length += path.Length + 16;
        }
        if (batch.Count > 0) yield return batch;
    }
    static string ValidateDRoot(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)) throw new ArgumentException("必须使用 D 盘绝对路径");
        var full = Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(Path.GetPathRoot(full), @"D:\", StringComparison.OrdinalIgnoreCase) || full.Length <= 3) throw new ArgumentException("只允许 D 盘本地目录");
        return full;
    }
    static bool Within(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    static void EnsureNoReparse(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("发现重解析路径，已停止检查点保存"); }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { }
    }
    void ValidateOwnedLatest(string latest)
    {
        EnsureNoReparse(latest); var path = Path.Combine(latest, "manifest.json"); EnsureNoReparse(path);
        if (!File.Exists(path)) throw new InvalidDataException("latest 目录来源未知，已保留且未覆盖");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("Owner", out var owner) || owner.ValueKind != JsonValueKind.String || owner.GetString() != Owner
            || !doc.RootElement.TryGetProperty("SchemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var schemaNumber) || schemaNumber != 1)
            throw new InvalidDataException("latest 检查点格式不兼容，已保留且未覆盖");
        foreach (var entry in Directory.EnumerateFileSystemEntries(latest))
        {
            EnsureNoReparse(entry);
            if (Directory.Exists(entry) || Path.GetFileName(entry) is not ("manifest.json" or "source.zip" or "working-tree.patch" or "resume.md"))
                throw new InvalidDataException("latest 含未知文件，已保留且未覆盖");
        }
    }
    void DeleteOwnedTemporary(string path)
    {
        var full = Path.GetFullPath(path);
        if (!Within(full, resumeRoot) || full == resumeRoot) throw new InvalidDataException("检查点清理路径越界");
        EnsureNoReparse(full); if (!Directory.Exists(full)) return;
        foreach (var file in Directory.EnumerateFileSystemEntries(full))
        { EnsureNoReparse(file); if (Directory.Exists(file)) throw new InvalidDataException("临时目录含未知子目录"); }
        Directory.Delete(full, true);
    }
    void TryDeleteTemporary(string path) { try { DeleteOwnedTemporary(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } catch (InvalidDataException) { } }
    static string Hash(string path) { using var input = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(input)); }
    static void WriteText(string path, string value)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough);
        file.Write(new UTF8Encoding(false).GetBytes(value)); file.Flush(true);
    }
    static string FindGit()
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            var candidate = Path.Combine(directory.Trim().Trim('"'), "git.exe"); if (File.Exists(candidate)) return candidate;
        }
        throw new InvalidDataException("本机 Git 命令不存在，未创建不完整检查点");
    }
}

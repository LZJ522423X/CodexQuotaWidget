using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CodexQuotaWidget.Core.ControlCenter;

namespace CodexQuotaWidget.Core.Automation;

/// <summary>Serializes small journals and full checkpoints across the widget and independent executor.</summary>
public static class RecoveryWriteLease
{
    public static async Task<FileStream> AcquireAsync(string dataRoot, CancellationToken cancellationToken = default)
    {
        var root = RecoveryJournal.ValidateRoot(dataRoot);
        var directory = Path.Combine(root, "resume");
        Directory.CreateDirectory(directory);
        RecoveryJournal.RejectReparse(directory);
        var path = Path.Combine(directory, "write.lock");
        RecoveryJournal.RejectReparse(path);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(40);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTimeOffset.UtcNow < deadline) { await Task.Delay(150, cancellationToken); }
        }
    }
}

public sealed record RecoveryTask(Guid Id, string Title, WorkItemState State, DateTimeOffset UpdatedAt);
public sealed record RecoveryJournalState
{
    public int SchemaVersion { get; init; } = 1;
    public string Owner { get; init; } = "CodexQuotaWidget.RecoveryJournal";
    public string ProjectRoot { get; init; } = "";
    public DateTimeOffset UpdatedAt { get; init; }
    public string GitCommit { get; init; } = "";
    public List<string> ModifiedFiles { get; init; } = [];
    public List<RecoveryTask> Tasks { get; init; } = [];
    public string SourceFingerprint { get; init; } = "";
    public string? LatestSnapshot { get; init; }
    public DateTimeOffset? LatestSnapshotAt { get; init; }
    public string NextStep { get; init; } = "读取恢复记录和项目进度文档，检查 Git 修改，再编译测试；手动确认任务续接。";
}

/// <summary>Read-only Git inspection, bounded lightweight journals; ZIP only at changed major checkpoints.</summary>
public sealed class RecoveryJournal
{
    readonly string projectRoot, dataRoot;
    readonly SemaphoreSlim gate = new(1, 1);
    readonly Func<DateTimeOffset> clock;
    DateTimeOffset lastFull = DateTimeOffset.MinValue;
    string? lastFullFingerprint;
    int completed;
    public string? LastError { get; private set; }
    static readonly Regex Secret = new(@"(?:sk-[A-Za-z0-9_-]{16,}|gh[pousr]_[A-Za-z0-9]{20,}|-----BEGIN .*PRIVATE KEY|(?i:bearer)\s+[A-Za-z0-9._~+/-]{20,})");
    static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    { ".git", "bin", "obj", "artifacts", "publish", "node_modules", ".vs", ".idea", "auth", "credentials", "secrets", "logs", "cache", "backups" };

    public RecoveryJournal(string projectRoot, string dataRoot, Func<DateTimeOffset>? clock = null)
    {
        this.projectRoot = ValidateRoot(projectRoot); this.dataRoot = ValidateRoot(dataRoot);
        if (!Directory.Exists(this.projectRoot)) throw new ArgumentException("恢复项目目录不存在");
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<bool> RefreshAsync(ControlCenterSnapshot snapshot, bool importantCheckpoint = false, CancellationToken cancellationToken = default)
    {
        if (!await gate.WaitAsync(0, cancellationToken)) return false;
        try
        {
            await using var lease = await RecoveryWriteLease.AcquireAsync(dataRoot, cancellationToken);
            var now = clock();
            var status = await GitAsync(["status", "--porcelain=v1", "-z", "--untracked-files=all", "--ignore-submodules=all"], cancellationToken);
            var paths = (await GitAsync(["ls-files", "-z", "--cached", "--others", "--exclude-standard"], cancellationToken))
                .Split('\0', StringSplitOptions.RemoveEmptyEntries).Where(IsSource).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray();
            if (paths.Length > 5000) throw new InvalidDataException("源文件超过轻量恢复上限");
            var fingerprintText = new StringBuilder();
            foreach (var relative in paths)
            {
                var file = Path.Combine(projectRoot, relative); RejectReparse(file);
                if (!File.Exists(file)) continue;
                var info = new FileInfo(file);
                fingerprintText.Append(relative).Append('|').Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks).Append('\n');
            }
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintText.ToString())));
            var tokens = status.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            var dirty = new List<string>();
            for (var i = 0; i < tokens.Length; i++)
            {
                if (tokens[i].Length < 4) continue;
                var relative = tokens[i][3..];
                if (IsSource(relative)) dirty.Add(SafeText(tokens[i]));
                if (tokens[i][..2].Contains('R') || tokens[i][..2].Contains('C')) i++;
            }
            var commit = (await GitAsync(["rev-parse", "--verify", "HEAD"], cancellationToken)).Trim();
            if (!Regex.IsMatch(commit, "^[a-fA-F0-9]{40,64}$")) throw new InvalidDataException("Git HEAD 无法确认");
            var terminal = snapshot.Tasks.Count(t => t.State == WorkItemState.Completed);
            var manifestPath=Path.Combine(dataRoot,"resume","latest","manifest.json");RejectReparse(manifestPath);
            SafeFinishManifest? manifest=null;
            if(File.Exists(manifestPath))
            {
                if(new FileInfo(manifestPath).Length>2*1024*1024)throw new InvalidDataException("恢复清单异常");
                manifest=System.Text.Json.JsonSerializer.Deserialize<SafeFinishManifest>(await File.ReadAllTextAsync(manifestPath,cancellationToken));
                if(manifest is null || manifest.Owner!="CodexQuotaWidget.SafeFinishCheckpoint" || manifest.SchemaVersion!=1
                    || !Path.GetFullPath(manifest.ProjectRoot).Equals(projectRoot,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("完整恢复清单不属于当前项目");
            }
            if (lastFull == DateTimeOffset.MinValue && manifest is not null) lastFull = manifest.CreatedAt;
            var changed = fingerprint != lastFullFingerprint;
            if (changed && (importantCheckpoint || terminal > completed || now - lastFull >= TimeSpan.FromMinutes(10)))
            {
                var result = await new SafeFinishCheckpoint(projectRoot, dataRoot, clock).CreateAsync(snapshot, cancellationToken);
                if (!result.Success) throw new InvalidDataException(result.Message);
                manifest = AtomicStore.Read<SafeFinishManifest>(result.ManifestPath!); lastFull = now; lastFullFingerprint = fingerprint;
            }
            var state = new RecoveryJournalState
            {
                ProjectRoot = projectRoot, UpdatedAt = now, GitCommit = commit, ModifiedFiles = dirty.Take(200).ToList(),
                Tasks = snapshot.Tasks.Take(200).Select(t => new RecoveryTask(t.Id, SafeText(t.Title), t.State, t.UpdatedAt)).ToList(),
                SourceFingerprint = fingerprint,
                LatestSnapshot = manifest is null ? null : Path.Combine(dataRoot, "resume", "latest", "source.zip"),
                LatestSnapshotAt = manifest?.CreatedAt
            };
            var journalPath = Path.Combine(dataRoot, "resume", "journal.json"); RejectReparse(journalPath);RejectReparse(journalPath+".bak");
            AtomicStore.Write(journalPath, state);
            await WriteTextAtomicAsync(Path.Combine(dataRoot, "codex_resume_state.md"), BuildMarkdown(state), cancellationToken);
            completed = terminal; LastError = null; return true;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException or OperationCanceledException)
        { LastError = "恢复状态保存失败：" + e.GetType().Name; return false; }
        finally { gate.Release(); }
    }

    string BuildMarkdown(RecoveryJournalState state)
    {
        var text = new StringBuilder("# AI Control Center 继续开发状态\n\n");
        text.AppendLine($"版本：{ReleaseUpdates.CurrentLabel}；最后更新时间：{state.UpdatedAt:O}")
            .AppendLine($"项目：{state.ProjectRoot}").AppendLine($"Git HEAD：{state.GitCommit}")
            .AppendLine($"最新完整快照：{state.LatestSnapshot ?? "尚未生成"}")
            .AppendLine($"完整快照时间：{state.LatestSnapshotAt:O}")
            .AppendLine("快照校验清单及 binary patch：resume/latest/manifest.json、working-tree.patch。")
            .AppendLine("轻量记录独立于最新 ZIP；完整快照的恢复摘要在 resume/latest/resume.md。")
            .AppendLine("保存磁盘源码及 Git 状态；未自动暂存、提交或重置，无法读取 IDE 内存中未保存的编辑。")
            .AppendLine().AppendLine("## 当前任务 / 已完成 / 待处理").AppendLine();
        foreach (var task in state.Tasks) text.AppendLine($"- {task.State}：{task.Title} ({task.Id})");
        if (state.Tasks.Count == 0) text.AppendLine("本地队列为空；开发进度见下方项目进度文档。");
        var progress = Path.Combine(projectRoot, "docs", "AI-CONTROL-CENTER-STATUS.md");
        if (File.Exists(progress))
        {
            RejectReparse(progress);
            text.AppendLine().AppendLine("## 开发进度").AppendLine();
            foreach (var line in File.ReadLines(progress).Take(160)) if (!Secret.IsMatch(line)) text.AppendLine(line[..Math.Min(line.Length, 500)]);
        }
        text.AppendLine().AppendLine("## 下一步").AppendLine().AppendLine(state.NextStep)
            .AppendLine("重新编译及测试：需要。读取 docs/AI-CONTROL-CENTER-REPORT.md 后运行 Release 与离线检查。")
            .AppendLine().AppendLine("## 主要修改文件").AppendLine();
        foreach (var file in state.ModifiedFiles) text.AppendLine("- " + file);
        return text.ToString();
    }

    static async Task WriteTextAtomicAsync(string path, string text, CancellationToken token)
    {
        RejectReparse(path); RejectReparse(path+".bak"); var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { await stream.WriteAsync(Encoding.UTF8.GetBytes(text), token); stream.Flush(true); }
            if (File.Exists(path)) File.Replace(temp, path, path + ".bak"); else File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    async Task<string> GitAsync(string[] arguments, CancellationToken token)
    {
        var git = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(p => Path.Combine(p.Trim('"'), "git.exe")).FirstOrDefault(File.Exists) ?? throw new InvalidDataException("Git 不存在");
        var start = new ProcessStartInfo(git) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = projectRoot, StandardOutputEncoding = Encoding.UTF8 };
        foreach (var arg in new[] { "--no-pager", "-C", projectRoot, "-c", "core.fsmonitor=false", "-c", "core.untrackedCache=false" }.Concat(arguments)) start.ArgumentList.Add(arg);
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0"; start.Environment.Remove("GIT_EXTERNAL_DIFF");
        var temp = Path.Combine(dataRoot, "temp"); Directory.CreateDirectory(temp); RejectReparse(temp);
        start.Environment["TEMP"] = temp; start.Environment["TMP"] = temp;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Git 检查未启动");
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token); var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token); var value = await output; _ = await error;
            if (process.ExitCode != 0 || value.Length > 2 * 1024 * 1024) throw new InvalidDataException("Git 只读检查失败");
            return value;
        }
        finally { if (!process.HasExited) process.Kill(true); }
    }

    bool IsSource(string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Split('/', '\\').Any(p => p is "." or ".." || Excluded.Contains(p))) return false;
        var name = Path.GetFileName(relative);
        return !name.StartsWith(".env", StringComparison.OrdinalIgnoreCase) && !new[]{"auth.json","credentials.json","secrets.json","token.json"}.Contains(name,StringComparer.OrdinalIgnoreCase)
            && !new[]{".pem",".pfx",".key",".p12"}.Contains(Path.GetExtension(name),StringComparer.OrdinalIgnoreCase) && !Secret.IsMatch(relative);
    }
    static string SafeText(string value) => Secret.IsMatch(value) ? "[已省略敏感内容]" : new string(value.Take(240).Where(c => !char.IsControl(c)).ToArray()).Replace("`", "'");
    internal static string ValidateRoot(string path)
    {
        if (!AutomationStore.IsSafeDPath(path)) throw new ArgumentException("恢复资料必须位于 D 盘普通路径");
        var full = Path.GetFullPath(path); RejectReparse(full); return full;
    }
    internal static void RejectReparse(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("恢复路径不允许重解析点");
            current = Path.GetDirectoryName(current);
        }
    }
}

using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexQuotaWidget.Core;
using CodexQuotaWidget.Core.ControlCenter;

public static class SafeFinishChecks
{
    public static async Task<int> RunAsync()
    {
        const string fixtureRoot = @"D:\CodexQuotaWidget-GitHub\artifacts\safe-finish-checks";
        var runRoot = Path.Combine(fixtureRoot, Guid.NewGuid().ToString("N"));
        var project = Path.Combine(runRoot, "project");
        var data = Path.Combine(runRoot, "data");
        var scratch = Path.Combine(runRoot, "temp");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(scratch);
        var now = new DateTimeOffset(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);
        var passed = 0;
        var junctions = new List<string>();

        void Check(bool condition, string scenario)
        {
            if (!condition) throw new InvalidOperationException("Safe-finish checkpoint: " + scenario);
            passed++;
        }

        void Put(string relative, string content)
        {
            var path = Path.Combine(project, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content, new UTF8Encoding(false));
        }

        async Task<string> Git(params string[] arguments)
        {
            var start = new ProcessStartInfo("git")
            {
                WorkingDirectory = project,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
            start.Environment["GIT_CONFIG_GLOBAL"] = "NUL";
            start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
            start.Environment["TEMP"] = scratch;
            start.Environment["TMP"] = scratch;
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Fixture git failed to start.");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
            catch { if (!process.HasExited) process.Kill(true); throw; }
            var stdout = await output;
            var stderr = await error;
            if (process.ExitCode != 0) throw new InvalidOperationException("Fixture git command failed: " + stderr);
            return stdout;
        }

        JsonElement Field(JsonElement item, string name)
            => item.EnumerateObject().First(property => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

        async Task<bool> RejectsRoot(string root, string output)
        {
            try { return !(await new SafeFinishCheckpoint(root, output, () => now).CreateAsync(new ControlCenterSnapshot())).Success; }
            catch (Exception error) when (error is ArgumentException or IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
            { return true; }
        }

        async Task CreateJunction(string link, string target)
        {
            var script = "$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path $env:CQW_FIXTURE_LINK -Target $env:CQW_FIXTURE_TARGET | Out-Null";
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-EncodedCommand");
            start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
            start.Environment["CQW_FIXTURE_LINK"] = link;
            start.Environment["CQW_FIXTURE_TARGET"] = target;
            start.Environment["TEMP"] = scratch;
            start.Environment["TMP"] = scratch;
            start.Environment.Remove("PSModulePath");
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Fixture junction process failed to start.");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
            catch { if (!process.HasExited) process.Kill(true); throw; }
            await output;
            if (process.ExitCode != 0) throw new InvalidOperationException("Fixture junction failed: " + await error);
            await error;
            junctions.Add(link);
        }

        try
        {
            QuotaSnapshot TriggerQuota(double? fiveRemaining, double weeklyRemaining = 60, DateTimeOffset? observed = null, long? reset = null)
                => new(observed ?? now, "plus",
                    [new("codex", "Codex", "primary", fiveRemaining is { } remaining ? 100 - remaining : null, 300, reset),
                     new("codex", "Codex", "secondary", 100 - weeklyRemaining, 10080, now.AddDays(3).ToUnixTimeSeconds())], false, null, []);

            bool Due(QuotaSnapshot? quota, MonitorStatus status = MonitorStatus.Live, bool stale = false, int age = 180)
                => SafeFinishTrigger.IsDue(quota, status, stale, now, age);

            Check(!Due(TriggerQuota(3)), "three percent no longer triggers automatic finish");
            Check(Due(TriggerQuota(0)), "fresh exhausted 5-hour quota still triggers preservation");
            Check(!Due(TriggerQuota(3.001)), "5-hour quota above 3 percent does not trigger preservation");
            Check(!Due(TriggerQuota(70, 1)), "weekly exhaustion alone does not claim the 5-hour preservation threshold was reached");
            Check(!Due(null) && !Due(TriggerQuota(null)), "unknown 5-hour capacity never triggers a false numeric threshold");
            Check(!Due(TriggerQuota(double.NaN)) && !Due(TriggerQuota(-1)) && !Due(TriggerQuota(101)), "invalid percentages cannot trigger preservation");
            Check(!Due(TriggerQuota(1), MonitorStatus.Offline) && !Due(TriggerQuota(1), stale: true), "offline or explicitly stale readings do not trigger preservation");
            Check(!Due(TriggerQuota(1, observed: now.AddSeconds(-181))), "readings outside their permitted freshness window do not trigger preservation");
            Check(Due(TriggerQuota(0, observed: now.AddSeconds(-300)), age: 615), "the 5-minute refresh allowance can still recognize fresh exhausted capacity");
            Check(!Due(TriggerQuota(1, observed: now.AddMinutes(10))), "future-dated readings do not trigger preservation");
            Check(!Due(TriggerQuota(1, reset: now.ToUnixTimeSeconds())), "a crossed reset time requires a refreshed reading before threshold detection");
            Check(!Due(TriggerQuota(1), age: 0) && !Due(TriggerQuota(1), age: 901), "unsupported freshness policy cannot drive threshold detection");
            var wrongModel = TriggerQuota(1) with { Windows = [new("other-model", "Other", "primary", 99, 300, null)] };
            Check(!Due(wrongModel), "another model's short-window capacity is not Codex 5-hour quota");

            Put(".gitignore", "ignored/\nbin/\nobj/\n");
            Put(".gitattributes", "src/tracked.txt diff=checkpointfixture\n");
            Put("README.md", "# Checkpoint fixture\nLocal test repository only.\n");
            Put("src/tracked.txt", "baseline source\n");
            // Deliberately tracked placeholder files verify that neither zip nor patch leaks excluded content.
            Put(".env", "CHECKPOINT_ENV_PLACEHOLDER=baseline-only\n");
            Put("config/auth.json", "{\"fixture\":\"CHECKPOINT_AUTH_BASELINE\"}\n");
            await Git("init", "--initial-branch=main");
            await Git("-c", "core.autocrlf=false", "add", "--", ".gitignore", ".gitattributes", "README.md", "src/tracked.txt", ".env", "config/auth.json");
            await Git("-c", "user.name=Checkpoint Fixture", "-c", "user.email=fixture@example.invalid", "-c", "commit.gpgsign=false", "commit", "--no-verify", "-m", "fixture baseline");
            // This harmless external driver would fail diff if the service executed custom repo commands.
            await Git("config", "--local", "diff.checkpointfixture.command", "\"" + Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe") + "\" /d /c exit 99");
            var originalCommit = (await Git("rev-parse", "HEAD")).Trim();
            Put("src/tracked.txt", "dirty source preserved exactly\n");
            Put("src/untracked.cs", "// newly created source is part of the checkpoint\n");
            Put("ignored/noise.txt", "IGNORED_FILE_MUST_NOT_BE_ARCHIVED\n");
            Put("bin/build-output.txt", "BUILD_OUTPUT_MUST_NOT_BE_ARCHIVED\n");
            Put(".env", "CHECKPOINT_ENV_PLACEHOLDER=DIRTY_VALUE_MUST_BE_EXCLUDED\n");
            Put("config/auth.json", "{\"fixture\":\"DIRTY_AUTH_VALUE_MUST_BE_EXCLUDED\"}\n");
            var statusBefore = await Git("status", "--porcelain=v1", "--untracked-files=all");
            var indexPath = Path.Combine(project, ".git", "index");
            var indexBefore = SHA256.HashData(File.ReadAllBytes(indexPath));

            const string privateTaskNote = "PRIVATE_TASK_NOTE_MUST_NOT_ENTER_CHECKPOINT";
            var queue = new ControlCenterService(Path.Combine(data, "queue.json"), () => now);
            Check(queue.AddTask("Checkpoint fixture task", privateTaskNote).Success, "local queued fixture can be persisted");
            var checkpoint = new SafeFinishCheckpoint(project, data, () => now);
            var first = await checkpoint.CreateAsync(queue.Snapshot);
            Check(first.Success, "a dirty Git project creates a checkpoint without executing its configured external diff driver");
            Check(first.ResumePath is not null && File.Exists(first.ResumePath) && first.ManifestPath is not null && File.Exists(first.ManifestPath) && first.SourceSnapshotPath is not null && File.Exists(first.SourceSnapshotPath), "resume, manifest and source snapshot are all present after success");
            Check(SHA256.HashData(File.ReadAllBytes(indexPath)).SequenceEqual(indexBefore), "checkpoint leaves the Git index bytes unchanged");
            Check(await Git("status", "--porcelain=v1", "--untracked-files=all") == statusBefore && (await Git("rev-parse", "HEAD")).Trim() == originalCommit, "checkpoint neither commits nor stages nor removes dirty/untracked source");

            var manifestBytes = File.ReadAllBytes(first.ManifestPath!);
            var resumeBytes = File.ReadAllBytes(first.ResumePath!);
            var zipBytes = File.ReadAllBytes(first.SourceSnapshotPath!);
            using (var manifest = JsonDocument.Parse(manifestBytes))
            {
                var sources = Field(manifest.RootElement, "SourceFiles").EnumerateArray().ToArray();
                var sourceNames = sources.Select(item => Field(item, "RelativePath").GetString()!).ToHashSet(StringComparer.Ordinal);
                Check(sourceNames.Contains("src/tracked.txt") && sourceNames.Contains("src/untracked.cs") && sourceNames.Contains("README.md"), "manifest includes tracked edits and newly created source files");
                Check(!sourceNames.Any(name => name == ".env" || name.Contains("auth.json", StringComparison.OrdinalIgnoreCase) || name.StartsWith("ignored/", StringComparison.Ordinal) || name.StartsWith("bin/", StringComparison.Ordinal) || name.StartsWith(".git/", StringComparison.Ordinal)), "manifest source list excludes credentials, ignored files, build files and Git internals");
                Check(Field(manifest.RootElement, "GitCommit").GetString() == originalCommit, "manifest identifies the original Git commit without inventing a new one");
                foreach (var source in sources)
                {
                    var relative = Field(source, "RelativePath").GetString()!;
                    var original = File.ReadAllBytes(Path.Combine(project, relative.Replace('/', Path.DirectorySeparatorChar)));
                    Check(Field(source, "Length").GetInt64() == original.LongLength && string.Equals(Field(source, "Sha256").GetString(), Convert.ToHexString(SHA256.HashData(original)), StringComparison.OrdinalIgnoreCase), "manifest checksum and length describe the exact current bytes: " + relative);
                }
                var dirty = Field(manifest.RootElement, "DirtyFiles").EnumerateArray().Select(item => Field(item, "RelativePath").GetString()).ToArray();
                Check(dirty.Contains("src/tracked.txt") && dirty.Contains("src/untracked.cs"), "manifest records both tracked and untracked work for recovery");
                Check(!dirty.Any(name => name is not null && (name == ".env" || name.Contains("auth.json", StringComparison.OrdinalIgnoreCase))), "dirty-file metadata does not expose excluded credential file entries");

                using var archive = ZipFile.OpenRead(first.SourceSnapshotPath!);
                var entries = archive.Entries.Where(entry => !entry.FullName.EndsWith('/')).ToDictionary(entry => entry.FullName, StringComparer.Ordinal);
                Check(entries.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(sourceNames), "source zip contains exactly the verified manifest file set");
                foreach (var (relative, entry) in entries)
                {
                    await using var archived = entry.Open();
                    using var memory = new MemoryStream();
                    await archived.CopyToAsync(memory);
                    Check(memory.ToArray().SequenceEqual(File.ReadAllBytes(Path.Combine(project, relative.Replace('/', Path.DirectorySeparatorChar)))), "archived source bytes preserve the working tree: " + relative);
                }
            }
            var latest = Path.GetDirectoryName(first.ManifestPath!)!;
            var patchPath = Path.Combine(latest, "working-tree.patch");
            var patch = File.ReadAllText(patchPath);
            Check(patch.Contains("dirty source preserved exactly", StringComparison.Ordinal), "recovery patch includes the tracked source edit");
            Check(!patch.Contains("CHECKPOINT_ENV_PLACEHOLDER", StringComparison.Ordinal) && !patch.Contains("DIRTY_AUTH_VALUE", StringComparison.Ordinal) && !patch.Contains("config/auth.json", StringComparison.Ordinal), "recovery patch excludes dirty credential files as well as their contents");
            Check(!Encoding.UTF8.GetString(manifestBytes).Contains(privateTaskNote, StringComparison.Ordinal) && !Encoding.UTF8.GetString(resumeBytes).Contains(privateTaskNote, StringComparison.Ordinal), "checkpoint metadata and resume text omit private task notes");
            Check(Encoding.UTF8.GetString(resumeBytes).Contains(project, StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(latest, "resume.md")), "recovery instructions identify the project and have an internal retained copy");

            now = now.AddMinutes(1);
            Put("src/tracked.txt", "second source revision\n");
            using (var lockZip = new FileStream(first.SourceSnapshotPath!, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var failed = await checkpoint.CreateAsync(queue.Snapshot);
                Check(!failed.Success, "locked old snapshot prevents replacement and reports failure");
                Check(File.ReadAllBytes(first.SourceSnapshotPath!).SequenceEqual(zipBytes) && File.ReadAllBytes(first.ManifestPath!).SequenceEqual(manifestBytes) && File.ReadAllBytes(first.ResumePath!).SequenceEqual(resumeBytes), "replacement failure preserves the complete previous source/manifest/resume checkpoint");
            }
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                try
                {
                    Check(!(await checkpoint.CreateAsync(queue.Snapshot, canceled.Token)).Success, "canceled checkpoint does not report success");
                }
                catch (OperationCanceledException) { passed++; }
                Check(File.ReadAllBytes(first.SourceSnapshotPath!).SequenceEqual(zipBytes) && File.ReadAllBytes(first.ManifestPath!).SequenceEqual(manifestBytes), "cancellation leaves the previous checkpoint intact");
            }
            var recovered = await checkpoint.CreateAsync(queue.Snapshot);
            Check(recovered.Success, "checkpoint can recover after the transient snapshot lock is released");
            Check(!File.ReadAllBytes(recovered.SourceSnapshotPath!).SequenceEqual(zipBytes), "successful recovery replaces the source snapshot with the new working-tree revision");
            using (var lockResume = new FileStream(recovered.ResumePath!, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var stableSource = File.ReadAllBytes(recovered.SourceSnapshotPath!);
                var stableManifest = File.ReadAllBytes(recovered.ManifestPath!);
                Put("src/tracked.txt", "third source revision\n");
                var failedResume = await checkpoint.CreateAsync(queue.Snapshot);
                Check(!failedResume.Success && File.ReadAllBytes(recovered.SourceSnapshotPath!).SequenceEqual(stableSource) && File.ReadAllBytes(recovered.ManifestPath!).SequenceEqual(stableManifest), "failure replacing the external resume pointer rolls back the source and manifest together");
            }

            Check(await RejectsRoot(@"C:\checkpoint-fixture-that-does-not-exist", data), "a C-drive project root is rejected before writing");
            Check(await RejectsRoot(project, @"C:\checkpoint-fixture-that-does-not-exist"), "a C-drive output root is rejected before writing");
            Check(await RejectsRoot(@"\\localhost\D$\checkpoint-fixture", data), "UNC source roots are rejected");
            Check(await RejectsRoot(project, @"D:relative-checkpoint-fixture"), "drive-relative output paths are rejected");

            var outside = Path.Combine(runRoot, "outside-project");
            Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "outside.txt"), "OUTSIDE_PROJECT_MUST_NOT_BE_READ");
            var sourceJunction = Path.Combine(project, "src", "escape");
            await CreateJunction(sourceJunction, outside);
            Check((File.GetAttributes(sourceJunction) & FileAttributes.ReparsePoint) != 0, "fixture reparse source really exists");
            var sourceEscape = await checkpoint.CreateAsync(queue.Snapshot);
            Check(!sourceEscape.Success, "source junction traversal is refused instead of including outside data");
            Check(!Directory.EnumerateFiles(data, "*.tmp", SearchOption.AllDirectories).Any(), "failed checkpoints do not leave temporary pointer files behind");
            var outputJunction = Path.Combine(runRoot, "data-junction");
            await CreateJunction(outputJunction, data);
            Check(await RejectsRoot(project, outputJunction), "reparse output roots are rejected");
            return passed;
        }
        finally
        {
            var resolved = Path.GetFullPath(runRoot);
            var prefix = Path.GetFullPath(fixtureRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unexpected safe-finish fixture path.");
            foreach (var junction in junctions.AsEnumerable().Reverse())
                if (Directory.Exists(junction)) Directory.Delete(junction, false);
            if (Directory.Exists(resolved))
            {
                // Git's fixture-only loose objects can be read-only on Windows.
                foreach (var file in Directory.EnumerateFiles(resolved, "*", SearchOption.AllDirectories))
                {
                    if (!Path.GetFullPath(file).StartsWith(resolved + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Unexpected safe-finish fixture cleanup target.");
                    var attributes = File.GetAttributes(file);
                    if ((attributes & FileAttributes.ReparsePoint) == 0 && (attributes & FileAttributes.ReadOnly) != 0)
                        File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                }
                Directory.Delete(resolved, true);
            }
        }
    }
}

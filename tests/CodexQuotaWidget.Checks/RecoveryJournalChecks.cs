using System.Diagnostics;
using CodexQuotaWidget.Core;
using CodexQuotaWidget.Core.Automation;
using CodexQuotaWidget.Core.ControlCenter;

public static class RecoveryJournalChecks
{
    public static async Task<int> RunAsync()
    {
        var root = Path.Combine(@"D:\CodexQuotaWidget-GitHub\artifacts\journal-checks", Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "project"); var data = Path.Combine(root, "data");
        Directory.CreateDirectory(project); Directory.CreateDirectory(data);
        var passed = 0;
        void Check(bool test, string description) { if (!test) throw new InvalidOperationException("RecoveryJournal: " + description); passed++; }
        async Task Git(params string[] arguments)
        {
            var start = new ProcessStartInfo("git.exe") { WorkingDirectory = project, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            start.Environment["GIT_CONFIG_NOSYSTEM"] = "1"; start.Environment["GIT_CONFIG_GLOBAL"] = "NUL";
            start.Environment["TEMP"] = root; start.Environment["TMP"] = root;
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); await output; await error;
            if (process.ExitCode != 0) throw new InvalidOperationException("Journal fixture Git failed");
        }
        try
        {
            File.WriteAllText(Path.Combine(project, "main.cs"), "// first checkpoint\n");
            await Git("init", "--initial-branch=main"); await Git("add", "main.cs");
            await Git("-c", "user.name=Journal Fixture", "-c", "user.email=fixture@example.invalid", "-c", "commit.gpgsign=false", "commit", "--no-verify", "-m", "fixture");
            var now = DateTimeOffset.UtcNow;
            var journal = new RecoveryJournal(project, data, () => now);
            var task = new ControlWorkItem { Id = Guid.NewGuid(), Title = "Continue local implementation", State = WorkItemState.Running, UpdatedAt = now };
            var snapshot = new ControlCenterSnapshot { Tasks = [task] };
            Check(await journal.RefreshAsync(snapshot, importantCheckpoint: true), "initial important full checkpoint saved");
            var full = Path.Combine(data, "resume", "latest", "source.zip");
            var stamp = File.GetLastWriteTimeUtc(full); var original = File.ReadAllBytes(full);
            now = now.AddSeconds(30); File.AppendAllText(Path.Combine(project, "main.cs"), "// source changed\n");
            Check(await journal.RefreshAsync(snapshot), "source change updates lightweight recovery");
            var state = AtomicStore.Read<RecoveryJournalState>(Path.Combine(data, "resume", "journal.json"))!;
            Check(state.UpdatedAt == now && state.ModifiedFiles.Any(p => p.EndsWith("main.cs")), "time and Git dirty paths saved");
            Check(state.Tasks.Single().State == WorkItemState.Running, "current tracked task saved");
            Check(File.GetLastWriteTimeUtc(full) == stamp && original.SequenceEqual(File.ReadAllBytes(full)), "periodic light save does not recreate ZIP");
            Check(File.ReadAllText(Path.Combine(data, "codex_resume_state.md")).Contains("重新编译及测试：需要"), "resume says source needs compilation");
            now = now.AddSeconds(30);
            Check(await journal.RefreshAsync(snapshot with { Tasks = [task with { State = WorkItemState.Completed, UpdatedAt = now }] }), "task completion makes a changed major checkpoint");
            Check(!original.SequenceEqual(File.ReadAllBytes(full)), "completed checkpoint includes changed source");
            var head = Path.Combine(project, ".git", "HEAD"); var index = Path.Combine(project, ".git", "index");
            var beforeHead = File.ReadAllBytes(head); var beforeIndex = File.ReadAllBytes(index);
            now = now.AddSeconds(30); Check(await journal.RefreshAsync(snapshot), "repeat light save succeeds");
            Check(beforeHead.SequenceEqual(File.ReadAllBytes(head)) && beforeIndex.SequenceEqual(File.ReadAllBytes(index)), "does not commit or stage Git");
            await using (var lease = await RecoveryWriteLease.AcquireAsync(data))
            {
                using var cancellation = new CancellationTokenSource(200);
                try { await RecoveryWriteLease.AcquireAsync(data, cancellation.Token); throw new InvalidOperationException("lock not exclusive"); }
                catch (OperationCanceledException) { passed++; }
            }
            Check(Directory.GetDirectories(Path.Combine(data, "resume")).Length == 1, "bounded single full snapshot");
        }
        finally
        {
            var resolved = Path.GetFullPath(root);
            if (resolved.StartsWith(@"D:\CodexQuotaWidget-GitHub\artifacts\journal-checks\", StringComparison.OrdinalIgnoreCase))
            {
                foreach(var file in Directory.EnumerateFiles(resolved,"*",SearchOption.AllDirectories))File.SetAttributes(file,FileAttributes.Normal);
                Directory.Delete(resolved, true);
            }
        }
        return passed;
    }
}

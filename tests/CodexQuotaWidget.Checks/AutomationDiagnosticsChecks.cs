using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using CodexQuotaWidget.Core.Automation;
using CodexQuotaWidget.Core.ControlCenter;

/// <summary>Only isolated D-drive fixtures and the executor's read-only diagnostics entry. Never runs a saved plan.</summary>
public static class AutomationDiagnosticsChecks
{
    public static async Task<int> RunAsync(string executable)
    {
        var root = Path.Combine(@"D:\CodexQuotaWidget-GitHub\artifacts\automation-diagnostics-checks", Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "project"); var data = Path.Combine(root, "data");
        Directory.CreateDirectory(project); Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(project, "App.cs"), "class App { }\n");
        var checks = 0;
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException("Automation diagnostics: " + message); checks++; }
        DiagnosticsOutcome Outcome(AutomationDiagnosticsReport report, string id) => report.Items.Single(item => item.Id == id).Outcome;
        async Task<(int Code, AutomationDiagnosticsReport? Report)> Run(params string[] additional)
        {
            var args = new List<string> { "--diagnostics", "--data-dir", data, "--project-root", project }; args.AddRange(additional);
            var result = await Start(executable, root, args.ToArray());
            var report = JsonSerializer.Deserialize<AutomationDiagnosticsReport>(result.Output);
            return (result.Code, report?.Items.Count > 0 ? report : null);
        }

        var missing = await Run();
        Check(missing.Code == 0 && missing.Report is not null, "valid CLI produces a report even when recovery is absent");
        var report = missing.Report!;
        Check(report.DryRun && report.NoProcessesClosed && report.NoPowerAction, "diagnostics has immutable safe flags");
        Check(Outcome(report, "recovery-integrity") == DiagnosticsOutcome.NotVerified, "missing recovery is never called valid");
        Check(Outcome(report, "resume-note") == DiagnosticsOutcome.NotVerified, "missing resume is never called valid");
        Check(!Directory.Exists(Path.Combine(data, "resume")) && !File.Exists(Path.Combine(data, "codex_resume_state.md")), "diagnostics does not invent missing recovery");
        Check(!File.Exists(Path.Combine(data, "automation-config.json")) && !Directory.Exists(Path.Combine(data, "automation")), "diagnostics does not invent or enable a plan");
        Check(Outcome(report, "plan-parser-fixture") == DiagnosticsOutcome.SimulatedPassed, "synthetic parser test clearly labelled simulated");
        Check(Outcome(report, "process-close") == DiagnosticsOutcome.NotVerified && Outcome(report, "windows-power") == DiagnosticsOutcome.NotVerified,
            "identity inspection is not mistaken for real close or power acceptance");
        Check(Outcome(report, "executor-independence") == DiagnosticsOutcome.NotVerified, "process start does not assert parent-exit survival");
        Check(File.Exists(report.ReportPath) && report.ReportPath.StartsWith(data, StringComparison.OrdinalIgnoreCase), "report exists only on isolated D data root");
        Check(!File.Exists(report.ReportPath + ".bak") && !Directory.GetFiles(Path.GetDirectoryName(report.ReportPath)!, "*.tmp-*").Any(), "no report backup or staging residue");

        var rejected = await Run("--execute");
        Check(rejected.Code != 0 && rejected.Report is null, "execute flag cannot elevate diagnostic mode");
        var request = await Run("--request", Path.Combine(data, "request.json"));
        Check(request.Code != 0 && request.Report is null, "request cannot be mixed into diagnostic mode");
        var cRoot = await Start(executable, root, "--diagnostics", "--data-dir", @"C:\CQW-forbidden", "--project-root", project);
        Check(cRoot.Code != 0, "C drive diagnostic output is rejected before writing");

        var plan = ActionPlanParser.Parse("关闭Clash Verge和mihomo，然后关机。").Plan!;
        var store = new AutomationStore(data);
        Check(store.SaveConfiguration(new() { Enabled = true, Text = plan.Text, Plan = plan, ProjectRoot = project }, out _), "fixture saved enabled plan is valid");
        var configurationHash = Hash(store.ConfigurationPath);
        var latest = Path.Combine(data, "resume", "latest"); Directory.CreateDirectory(latest);
        var archive = Path.Combine(latest, "source.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create)) zip.CreateEntryFromFile(Path.Combine(project, "App.cs"), "App.cs");
        File.WriteAllText(Path.Combine(latest, "working-tree.patch"), "");
        File.WriteAllText(Path.Combine(latest, "resume.md"), "Synthetic diagnostics fixture; never execute.\n");
        File.WriteAllText(Path.Combine(data, "codex_resume_state.md"), "Fixture resume\n");
        var manifest = new SafeFinishManifest
        {
            ProjectRoot = project, CreatedAt = DateTimeOffset.UtcNow,
            SourceFiles = [new("App.cs", Hash(Path.Combine(project, "App.cs")), new FileInfo(Path.Combine(project, "App.cs")).Length)],
            SourceSnapshotSha256 = Hash(archive), GitPatchSha256 = Hash(Path.Combine(latest, "working-tree.patch")),
            ResumeSha256 = Hash(Path.Combine(latest, "resume.md"))
        };
        File.WriteAllText(Path.Combine(latest, "manifest.json"), JsonSerializer.Serialize(manifest));
        var before = Directory.GetFiles(data, "*", SearchOption.AllDirectories).Where(path => !path.Contains(Path.Combine("checks", "automation-diagnostics.json"), StringComparison.OrdinalIgnoreCase))
            .ToDictionary(path => path, Hash);
        var seeded = await Run(); report = seeded.Report!;
        Check(seeded.Code == 0 && report is not null, "existing valid fixture recovery passes read-only validation");
        Check(Outcome(report!, "saved-plan") == DiagnosticsOutcome.RealPassed, "actual saved enabled plan parsed without executing");
        Check(Outcome(report!, "recovery-integrity") == DiagnosticsOutcome.RealPassed && Outcome(report!, "recovery-current-source") == DiagnosticsOutcome.RealPassed,
            "existing archive/hash/source verified independently");
        Check(before.All(pair => File.Exists(pair.Key) && pair.Value == Hash(pair.Key)), "all existing data remains byte-for-byte unchanged");
        Check(configurationHash == Hash(store.ConfigurationPath) && store.LoadConfiguration().Configuration.Enabled, "saved enabled setting remains unchanged but not executed");

        File.AppendAllText(Path.Combine(project, "App.cs"), "// changed\n");
        var changed = await Run();
        Check(changed.Code != 0 && Outcome(changed.Report!, "recovery-integrity") == DiagnosticsOutcome.RealPassed
            && Outcome(changed.Report!, "recovery-current-source") == DiagnosticsOutcome.Failed, "changed source distinguished from intact archive");
        Check(before.All(pair => File.Exists(pair.Key) && pair.Value == Hash(pair.Key)), "mismatch diagnostics does not overwrite recovery");
        File.AppendAllText(Path.Combine(latest, "resume.md"), "corrupt\n");
        var corrupt = await Run();
        Check(corrupt.Code != 0 && Outcome(corrupt.Report!, "recovery-integrity") == DiagnosticsOutcome.Failed,
            "corrupted full resume hash rejected");
        File.WriteAllText(store.ConfigurationPath, "{broken");
        var invalidConfig = await Run();
        Check(invalidConfig.Code != 0 && Outcome(invalidConfig.Report!, "saved-plan") == DiagnosticsOutcome.Failed,
            "corrupt primary configuration never resurrects backup enabled plan");
        Check(!Directory.Exists(Path.Combine(data, "automation")), "no execution receipts or locks ever created");
        return checks;
    }

    static string Hash(string file) { using var input = File.OpenRead(file); return Convert.ToHexString(SHA256.HashData(input)); }
    static async Task<(int Code, string Output)> Start(string executable, string root, params string[] arguments)
    {
        var temp = Path.Combine(root, "runner-temp"); Directory.CreateDirectory(temp);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["TEMP"] = temp; start.Environment["TMP"] = temp;
        start.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = Path.Combine(root, "runtime-cache");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("diagnostics fixture process did not start");
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90)); }
        catch { if (!process.HasExited) process.Kill(); throw; }
        var text = await output; var errors = await error;
        if (!string.IsNullOrWhiteSpace(errors)) throw new InvalidOperationException("diagnostics fixture stderr: " + errors);
        return (process.ExitCode, text);
    }
}

using System.Diagnostics;
using System.Text.Json;
using CodexQuotaWidget.Core;
using CodexQuotaWidget.Core.Automation;

public static class ManualAcceptanceChecks
{
    public static async Task<int> RunAsync(string executorPath)
    {
        const string fixtures = @"D:\CodexQuotaWidget-GitHub\artifacts\manual-acceptance-checks";
        var root = Path.Combine(fixtures, Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "project"); var data = Path.Combine(root, "data");
        Directory.CreateDirectory(project); Directory.CreateDirectory(data);
        var passed = 0;
        void Check(bool condition, string scenario)
        {
            if (!condition) throw new InvalidOperationException("ManualAcceptance: " + scenario);
            passed++;
        }
        var now = DateTimeOffset.UtcNow; var reset = now.AddHours(1).ToUnixTimeSeconds();
        QuotaSnapshot Quota(double remaining, int seconds) => new(now.AddSeconds(seconds), "fixture",
            [new("codex", "Codex", "primary", 100 - remaining, 300, reset)], false, null, []);
        var detector = new ExhaustionDetector(); detector.Observe(Quota(5, -3), MonitorStatus.Live, false, now);
        detector.Observe(Quota(0, -2), MonitorStatus.Live, false, now);
        var receipt = detector.Observe(Quota(0, -1), MonitorStatus.Live, false, now) ?? throw new InvalidOperationException("Expected fixture zero transition");
        var request = new ManualAcceptanceRequest
        {
            DataRoot = data, ProjectRoot = project, CreatedAt = now, ExpiresAt = now.AddMinutes(14),
            Authorization = ManualAcceptanceRules.Authorization, SimulatedQuotaReceipt = receipt with { Synthetic = true }
        };
        try
        {
            Check(ManualAcceptanceRules.Validate(request, now, false, out _), "explicit dry request with genuine detector samples validates");
            Check(!ManualAcceptanceRules.Validate(request, now, true, out _), "dry request never authorizes real actions");
            Check(!ManualAcceptanceRules.Validate(request with { TestId = Guid.Empty }, now, false, out _), "empty identity rejected");
            Check(!ManualAcceptanceRules.Validate(request with { Owner = "other" }, now, false, out _), "unknown owner rejected");
            Check(!ManualAcceptanceRules.Validate(request with { Authorization = "" }, now, false, out _), "authorization must be explicit");
            Check(!ManualAcceptanceRules.Validate(request with { SimulatedQuotaReceipt = null }, now, false, out _), "label alone does not prove detector handover");
            Check(!ManualAcceptanceRules.Validate(request with { SimulatedQuotaReceipt = receipt }, now, false, out _), "real-source receipt is not written or used as test authorization");
            Check(!ManualAcceptanceRules.Validate(request with { SimulatedExhaustion = false }, now, false, out _), "manual mode cannot claim real exhaustion");
            Check(!ManualAcceptanceRules.Validate(request with { ExpiresAt = now.AddMinutes(16) }, now, false, out _), "maximum authorization window enforced");
            Check(!ManualAcceptanceRules.Validate(request with { ExpiresAt = now }, now, false, out _), "expired authorization rejected");
            Check(!ManualAcceptanceRules.Validate(request with { DelaySeconds = 0 }, now, false, out _), "countdown cannot be bypassed");
            Check(!ManualAcceptanceRules.Validate(request with { DelaySeconds = 121 }, now, false, out _), "countdown upper bound enforced");
            Check(!ManualAcceptanceRules.Validate(request with { DataRoot = project }, now, false, out _), "source and data directory cannot overlap");
            Check(!ManualAcceptanceRules.Validate(request with { DataRoot = @"C:\temp" }, now, false, out _), "all data stays on D");
            Check(ManualAcceptanceRules.RequestPath(request).EndsWith($@"manual-tests\requests\{request.TestId:N}.json")
                && ManualAcceptanceRules.ResultPath(request).EndsWith($@"manual-tests\results\{request.TestId:N}.json")
                && ManualAcceptanceRules.CancelPath(request).EndsWith($@"manual-tests\{request.TestId:N}.cancel"), "fixed identity-scoped request/report/cancel paths");
            Check(File.Exists(executorPath), "current independent executor exists");
            var isolated = Path.Combine(root, "executor-fixture");
            var start = new ProcessStartInfo(executorPath)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add("--manual-acceptance-self-test"); start.ArgumentList.Add("--fixture-root"); start.ArgumentList.Add(isolated);
            start.Environment["TEMP"] = root; start.Environment["TMP"] = root;
            start.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = Path.Combine(root, "runtime-cache");
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Manual fixture executor failed to start");
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90)); }
            catch { if (!process.HasExited) process.Kill(); throw; }
            var stdout = await output; _ = await error;
            Check(process.ExitCode == 0, "separate executor manual self-test passes: " + stdout);
            using var result = JsonDocument.Parse(stdout);
            Check(result.RootElement.GetProperty("Success").GetBoolean() && result.RootElement.GetProperty("NoRealActions").GetBoolean(), "self-test certifies zero real process/service/power actions");
            var runnerChecks = result.RootElement.GetProperty("Checks").GetInt32();
            Check(runnerChecks >= 18, "runner covered source changes, duplicate, cancellation, protected service, production lock and scope");
            passed += runnerChecks;
        }
        finally
        {
            if (Path.GetFullPath(root).StartsWith(fixtures + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(root, true);
            }
        }
        return passed;
    }
}

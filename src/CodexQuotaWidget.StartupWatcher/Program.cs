using System.Diagnostics;
using System.Text.Json;
using CodexQuotaWidget.Core.Startup;
using CodexQuotaWidget.Core.Automation;
using CodexQuotaWidget.StartupWatcher;

if(args.Contains("--stop")) { StartupWatcherControl.Stop(); return 0; }
string? Argument(string key) { var index = Array.IndexOf(args, key); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }
var data = Argument("--data-dir") ?? CodexQuotaWidget.Core.Paths.Data;
var widget = Argument("--widget");
if(!AutomationStore.IsSafeLocalPath(data)) return 2;
var statusPath = Argument("--report") ?? Path.Combine(data, "startup", "watcher-status.json");
if(!AutomationStore.IsSafeLocalPath(statusPath)) return 2;
if(args.Contains("--probe"))
{
    Directory.CreateDirectory(Path.GetDirectoryName(statusPath)!);
    File.WriteAllText(statusPath, JsonSerializer.Serialize(new { At = DateTimeOffset.Now, Mode = "ReadOnlyProbe",
        WidgetRunning = StartupWatcherControl.WidgetIsRunning(), Processes = DesktopIdentity.Observe() }, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}
// Harmless unsigned same-name process fixture. It can never bypass production Desktop identity verification.
if(args.Contains("--test-idle")) { Thread.Sleep(1000 * Math.Clamp(int.TryParse(Argument("--test-idle"), out var seconds) ? seconds : 15, 1, 60)); return 0; }
if(widget is null || !AutomationStore.IsSafeLocalPath(widget) || !File.Exists(widget)
    || !Path.GetFileName(widget).Equals("CodexQuotaWidget.exe", StringComparison.OrdinalIgnoreCase)) return 2;
var ownExe = Environment.ProcessPath!;
var pairedWidget = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(ownExe))!, "CodexQuotaWidget.exe");
if(!AutomationStore.IsSafeLocalPath(ownExe) || !Path.GetFullPath(widget).Equals(pairedWidget, StringComparison.OrdinalIgnoreCase)) return 2;
using var mutex = new Mutex(true, StartupWatcherControl.WatcherMutexName, out var first);
if(!first) return 0;
try
{
    // Release the stop-event handle before releasing the singleton mutex. A replacement watcher must
    // never inherit the previous instance's signalled manual-reset event during a version handoff.
    using var stop = new EventWaitHandle(false, EventResetMode.ManualReset, StartupWatcherControl.WatcherMutexName + ".stop");
    var host = new InstalledStartupWatchHost(data, widget, ownExe, statusPath);
    StartupWatchLoop.Run(host, stop, TimeSpan.FromSeconds(4), args.Contains("--once") ? 1 : null);
    return 0;
}
finally { mutex.ReleaseMutex(); }

sealed class InstalledStartupWatchHost : IStartupWatchHost
{
    readonly string data, widget, statusPath;
    readonly WindowsStartupRegistration registration;
    readonly string ownExe;
    IReadOnlyList<DesktopEvidence> evidence = Array.Empty<DesktopEvidence>();
    string? last;
    DateTimeOffset lastWritten;
    public InstalledStartupWatchHost(string data, string widget, string ownExe, string statusPath)
    {
        this.data = data; this.widget = widget; this.ownExe = ownExe; this.statusPath = statusPath;
        registration = new(data, widget, ownExe);
    }
    public bool IsEnabled()
    {
        var entry = registration.Read().FollowCodex;
        return entry.Enabled && ownExe.Equals(entry.TargetPath, StringComparison.OrdinalIgnoreCase);
    }
    public IReadOnlyList<DesktopRunIdentity> ObserveMainProcesses()
    {
        evidence = DesktopIdentity.Observe();
        if(!evidence.Any(item => item.Accepted) && evidence.Any(item => item.Role == "Unknown"))
            throw new InvalidOperationException("DesktopMainIdentityUnknown");
        return evidence.Where(item => item.Accepted && item.StartTimeUtcTicks is > 0)
            .Select(item => new DesktopRunIdentity(item.Pid, item.StartTimeUtcTicks!.Value)).ToArray();
    }
    public bool WidgetIsRunning() => StartupWatcherControl.WidgetIsRunning();
    public bool WidgetExitInProgress() => StartupWatcherControl.WidgetExitInProgress();
    public int? LaunchWidget()
    {
        var info = new ProcessStartInfo(widget) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(widget)! };
        info.ArgumentList.Add("--startup"); info.ArgumentList.Add("--data-dir"); info.ArgumentList.Add(data);
        info.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = Path.Combine(data, "runtime-cache");
        info.Environment["TEMP"] = Path.Combine(data, "startup", "temp"); info.Environment["TMP"] = info.Environment["TEMP"];
        Directory.CreateDirectory(info.Environment["TEMP"]!);
        using var child = Process.Start(info);
        return child?.Id;
    }
    public void Report(StartupWatchReport report)
    {
        var now = DateTimeOffset.Now;
        var result = report.Result == "Launched" ? "Launched:" + report.LaunchedPid : report.Result;
        var key = $"{report.Cycle}:{report.CodexVerified}:{report.WidgetRunning}:{result}:{report.Error}";
        var changed = key != last;
        if(!changed && now - lastWritten < TimeSpan.FromSeconds(30)) return;
        var payload = new { At = now, Mode = "Watcher", WatcherPid = Environment.ProcessId,
            report.Cycle, report.NewCycle, report.CodexVerified, report.WidgetRunning, Result = result,
            report.Error, Processes = report.CodexVerified is null ? null : evidence };
        Directory.CreateDirectory(Path.GetDirectoryName(statusPath)!);
        var staged = statusPath + ".tmp";
        File.WriteAllText(staged, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(staged, statusPath, true);
        if(changed)
        {
            var events = Path.Combine(data, "startup", "watcher-events.jsonl");
            if(File.Exists(events) && new FileInfo(events).Length > 256 * 1024)
                File.WriteAllLines(events, File.ReadLines(events).TakeLast(128).ToArray());
            File.AppendAllText(events, JsonSerializer.Serialize(new { At = now, WatcherPid = Environment.ProcessId,
                report.Cycle, report.NewCycle, report.CodexVerified, report.WidgetRunning, Result = result, report.Error,
                MainProcesses = evidence.Where(item => item.Accepted).Select(item => new { item.Pid, item.StartTimeUtcTicks }) }) + Environment.NewLine);
        }
        last = key; lastWritten = now;
    }
}

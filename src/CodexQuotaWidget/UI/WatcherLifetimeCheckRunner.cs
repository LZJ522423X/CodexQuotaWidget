using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CodexQuotaWidget.Core.Startup;

namespace CodexQuotaWidget;

/// <summary>
/// An opt-in native lifecycle check. Only the parent created here is assigned to the owned Job;
/// no user process, production watcher mutex, startup registration, AI or quota monitor is touched.
/// </summary>
internal static class WatcherLifetimeCheckRunner
{
    const string Root = @"D:\CodexQuotaWidget-GitHub\artifacts\watcher-lifetime-1.12.2";
    const uint KillOnJobClose = 0x2000;
    const int ExtendedLimitInformation = 9;
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal static int RunFixture(string[] args)
    {
        string? directory = null;
        try
        {
            var parent = args.Contains("--watcher-lifetime-parent");
            var child = args.Contains("--watcher-lifetime-child");
            if(parent == child || args.Length != 3 || args[1] != "--fixture-dir") return 2;
            directory = ValidateDirectory(args[2]);
            // Broker children inherit Explorer's environment, not the parent's environment. The
            // check is deliberately run from an expanded D-drive build to avoid pre-Main extraction.
            if(!File.Exists(Path.Combine(AppContext.BaseDirectory, "CodexQuotaWidget.dll"))) return 2;
            SetFixtureEnvironment(directory);
            using var parentGate = EventWaitHandle.OpenExisting(EventName(directory, "parent-gate"));
            using var parentStop = EventWaitHandle.OpenExisting(EventName(directory, "parent-stop"));
            using var childStop = EventWaitHandle.OpenExisting(EventName(directory, "child-stop"));
            if(parent)
            {
                Write(directory, "parent-ready.json", Ready());
                if(WaitHandle.WaitAny(new WaitHandle[] { parentGate, parentStop }, TimeSpan.FromSeconds(25)) != 0) return 2;
                var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Fixture executable unavailable.");
                var method = StartupWatcherLauncher.LaunchThroughDesktop(executable,
                    "--watcher-lifetime-child --fixture-dir " + StartupIntegration.QuoteArgument(directory));
                Write(directory, "parent-launched.json", new { ParentPid = Environment.ProcessId, Method = method, At = DateTimeOffset.UtcNow });
                // Test controller closes its own Job, which terminates only this parent fixture.
                // On a failed check the private stop event allows a normal exit instead.
                return parentStop.WaitOne(TimeSpan.FromSeconds(40)) ? 0 : 2;
            }
            Write(directory, "child-ready.json", Ready());
            var deadline = DateTimeOffset.UtcNow.AddSeconds(45); var sequence = 0;
            while(DateTimeOffset.UtcNow < deadline)
            {
                Write(directory, "child-heartbeat.json", new { Pid = Environment.ProcessId, Sequence = ++sequence, At = DateTimeOffset.UtcNow });
                if(childStop.WaitOne(TimeSpan.FromMilliseconds(150)))
                {
                    Write(directory, "child-exit.json", new { Pid = Environment.ProcessId, NormalExit = true, At = DateTimeOffset.UtcNow });
                    return 0;
                }
            }
            Write(directory, "child-exit.json", new { Pid = Environment.ProcessId, NormalExit = false, Reason = "Fixture deadline", At = DateTimeOffset.UtcNow });
            return 2;
        }
        catch(Exception error) when(IsExpected(error))
        {
            if(directory is not null)
                try { Write(directory, "fixture-error-" + Environment.ProcessId + ".json", new { Pid = Environment.ProcessId, Type = error.GetType().Name, error.Message }); }
                catch(IOException) { }
            return 1;
        }
    }

    internal static async Task<int> RunAsync()
    {
        Directory.CreateDirectory(Root);
        var directory = Path.Combine(Root, "fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); ValidateDirectory(directory);
        using var parentGate = new EventWaitHandle(false, EventResetMode.ManualReset, EventName(directory, "parent-gate"));
        using var parentStop = new EventWaitHandle(false, EventResetMode.ManualReset, EventName(directory, "parent-stop"));
        using var childStop = new EventWaitHandle(false, EventResetMode.ManualReset, EventName(directory, "child-stop"));
        var checks = new List<string>(); Process? parent = null; Process? child = null; var job = IntPtr.Zero;
        bool? parentInOwnedJob = null, childInOwnedJob = null; bool passed = false, cleanupPassed = true;
        string? errorType = null, errorMessage = null;
        var jobClosedAt = (DateTimeOffset?)null;
        void Check(bool value, string description)
        {
            if(!value) throw new InvalidOperationException(description);
            checks.Add(description); Console.WriteLine("PASS " + description);
        }
        try
        {
            Check(File.Exists(Path.Combine(AppContext.BaseDirectory, "CodexQuotaWidget.dll")),
                "expanded D-drive fixture runtime avoids pre-Main single-file extraction");
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Fixture executable unavailable.");
            Check(Path.GetFullPath(executable).StartsWith(@"D:\", StringComparison.OrdinalIgnoreCase), "fixture executable is on D drive");
            job = CreateJobObject(IntPtr.Zero, null);
            if(job == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create owned fixture Job.");
            var limits = new JobExtendedLimitInformation { BasicLimitInformation = new JobBasicLimitInformation { LimitFlags = KillOnJobClose } };
            if(!SetInformationJobObject(job, ExtendedLimitInformation, ref limits, (uint)Marshal.SizeOf<JobExtendedLimitInformation>()))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not configure owned fixture Job.");
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory,
            };
            start.ArgumentList.Add("--watcher-lifetime-parent"); start.ArgumentList.Add("--fixture-dir"); start.ArgumentList.Add(directory);
            start.Environment["TEMP"] = directory; start.Environment["TMP"] = directory;
            start.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = directory;
            parent = Process.Start(start) ?? throw new InvalidOperationException("Parent fixture was not started.");
            var parentReady = await WaitDocument(directory, "parent-ready.json", TimeSpan.FromSeconds(12));
            Check(parentReady.RootElement.GetProperty("Pid").GetInt32() == parent.Id, "readiness belongs to the controller-created parent fixture");
            Check(parentReady.RootElement.GetProperty("NoProductionStartup").GetBoolean(), "parent runs only the helper entry point");
            parentReady.Dispose();
            if(!AssignProcessToJobObject(job, parent.Handle))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not assign controller-created parent to owned fixture Job.");
            parentInOwnedJob = QueryJob(parent, job);
            Check(parentInOwnedJob == true, "real parent is in the specific owned KILL_ON_JOB_CLOSE Job");
            parentGate.Set();
            using var launched = await WaitDocument(directory, "parent-launched.json", TimeSpan.FromSeconds(15));
            Check(launched.RootElement.GetProperty("Method").GetString() == "ExplorerBroker", "parent uses the production Explorer desktop launch broker");
            using var childReady = await WaitDocument(directory, "child-ready.json", TimeSpan.FromSeconds(12));
            var childPid = childReady.RootElement.GetProperty("Pid").GetInt32();
            Check(childPid != parent.Id && childPid != Environment.ProcessId, "independent child is a distinct helper process");
            child = Process.GetProcessById(childPid);
            Check(string.Equals(child.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase)
                && child.StartTime.ToUniversalTime() >= parent.StartTime.ToUniversalTime(), "child identity matches the newly launched D-drive fixture executable");
            Check(childReady.RootElement.GetProperty("Temp").GetString() == directory
                && childReady.RootElement.GetProperty("NoProductionStartup").GetBoolean(), "child enters isolated helper mode with D-drive temporary storage");
            childInOwnedJob = QueryJob(child, job);
            Check(childInOwnedJob == false, "real Explorer-launched child is outside the specific parent fixture Job");
            // The machine or interactive shell may itself be in an unrelated Job; this check uses
            // the exact owned handle above, never IsProcessInJob(process, NULL).
            jobClosedAt = DateTimeOffset.UtcNow;
            if(!CloseHandle(job)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not close owned fixture Job.");
            job = IntPtr.Zero;
            Check(await WaitExit(parent, TimeSpan.FromSeconds(8)), "closing owned Job terminates only its controller-created parent fixture");
            await WaitHeartbeat(directory, jobClosedAt.Value, TimeSpan.FromSeconds(5));
            Check(!child.HasExited, "independent helper stays alive and publishes activity after parent Job exit");
            childStop.Set();
            Check(await WaitExit(child, TimeSpan.FromSeconds(8)) && child.ExitCode == 0, "independent child exits normally through its private stop event");
            using var exit = await WaitDocument(directory, "child-exit.json", TimeSpan.FromSeconds(3));
            Check(exit.RootElement.GetProperty("NormalExit").GetBoolean(), "child records a normal exit without forced termination");
            passed = true;
        }
        catch(Exception error) when(IsExpected(error))
        {
            errorType = error.GetType().Name; errorMessage = error.Message;
            Console.WriteLine("FAIL watcher parent lifetime: " + errorType + "; " + errorMessage);
        }
        finally
        {
            // Only the parent created above is in this Job. No termination API is called on any
            // discovered process. The independent child is stopped only by its unique event.
            parentStop.Set(); childStop.Set();
            if(job != IntPtr.Zero) { CloseHandle(job); job = IntPtr.Zero; }
            if(parent is not null) cleanupPassed &= await WaitExit(parent, TimeSpan.FromSeconds(5));
            if(child is not null) cleanupPassed &= await WaitExit(child, TimeSpan.FromSeconds(5));
            Write(directory, "result.json", new
            {
                Passed = passed && cleanupPassed, Checks = checks, ParentPid = parent?.Id, ChildPid = child?.Id,
                ParentInOwnedJob = parentInOwnedJob, ChildInOwnedJob = childInOwnedJob, JobClosedAt = jobClosedAt,
                CleanupPassed = cleanupPassed, ErrorType = errorType, Error = errorMessage,
                RealWindowsJob = true, RealExplorerBroker = passed, RealHelperProcesses = true,
                CodexLifecycle = "Simulated by the isolated parent; real user Codex was not closed",
                ModelRequests = 0, ProductionStartupChanges = false, UserProcessesClosed = false,
            });
            parent?.Dispose(); child?.Dispose();
        }
        Console.WriteLine((passed && cleanupPassed ? "PASS" : "FAIL") + " watcher lifetime native check; report: " + Path.Combine(directory, "result.json"));
        return passed && cleanupPassed ? 0 : 1;
    }

    static object Ready() => new
    {
        Pid = Environment.ProcessId, StartedAt = Process.GetCurrentProcess().StartTime.ToUniversalTime(),
        At = DateTimeOffset.UtcNow, Temp = Environment.GetEnvironmentVariable("TEMP"), NoProductionStartup = true,
    };
    static void SetFixtureEnvironment(string directory)
    {
        Environment.SetEnvironmentVariable("TEMP", directory); Environment.SetEnvironmentVariable("TMP", directory);
        Environment.SetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR", directory);
    }
    static string ValidateDirectory(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if(!string.Equals(Path.GetDirectoryName(full), Root, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(full).StartsWith("fixture-", StringComparison.Ordinal)
            || !Guid.TryParseExact(Path.GetFileName(full)[8..], "N", out _) || !Directory.Exists(full))
            throw new InvalidOperationException("Only the private D-drive lifecycle fixture directory is allowed.");
        for(var current = full; current is not null; current = Path.GetDirectoryName(current))
            if(Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Fixture storage may not cross reparse points.");
        return full;
    }
    static string EventName(string directory, string purpose) => @"Local\AIControlCenter-WatcherLifetime-" + Path.GetFileName(directory)[8..] + "-" + purpose;
    static void Write(string directory, string name, object value)
    {
        var target = Path.Combine(directory, name); var staging = target + ".writing";
        File.WriteAllText(staging, JsonSerializer.Serialize(value, JsonOptions)); File.Move(staging, target, true);
    }
    static async Task<JsonDocument> WaitDocument(string directory, string name, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while(DateTimeOffset.UtcNow < deadline)
        {
            try { if(File.Exists(Path.Combine(directory, name))) return JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, name))); }
            catch(IOException) { }
            await Task.Delay(60);
        }
        throw new TimeoutException("Fixture evidence was not produced: " + name);
    }
    static async Task WaitHeartbeat(string directory, DateTimeOffset after, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while(DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var heartbeat = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "child-heartbeat.json")));
                if(heartbeat.RootElement.GetProperty("At").GetDateTimeOffset() > after) return;
            }
            catch(IOException) { }
            await Task.Delay(80);
        }
        throw new TimeoutException("Independent child stopped publishing activity after parent exit.");
    }
    static async Task<bool> WaitExit(Process process, TimeSpan timeout)
    {
        try
        {
            if(process.HasExited) return true;
            using var deadline = new CancellationTokenSource(timeout);
            await process.WaitForExitAsync(deadline.Token); return true;
        }
        catch(OperationCanceledException) { return false; }
        catch(InvalidOperationException) { return false; }
    }
    static bool QueryJob(Process process, IntPtr job)
    {
        if(!IsProcessInJob(process.Handle, job, out var result))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read membership of the specific owned fixture Job.");
        return result;
    }
    static bool IsExpected(Exception error) => error is IOException or UnauthorizedAccessException or InvalidOperationException
        or Win32Exception or TimeoutException or ArgumentException or JsonException or WaitHandleCannotBeOpenedException
        or COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException;

    [StructLayout(LayoutKind.Sequential)] struct JobBasicLimitInformation
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit;
        public UIntPtr Affinity; public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] struct IoCounters { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)] struct JobExtendedLimitInformation
    {
        public JobBasicLimitInformation BasicLimitInformation; public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JobExtendedLimitInformation information, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool IsProcessInJob(IntPtr process, IntPtr job, [MarshalAs(UnmanagedType.Bool)] out bool result);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr handle);
}

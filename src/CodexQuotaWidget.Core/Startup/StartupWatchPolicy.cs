namespace CodexQuotaWidget.Core.Startup;

/// <summary>A verified Desktop main-process lifetime. PID reuse must not reuse a launch decision.</summary>
public sealed record DesktopRunIdentity(int ProcessId, long StartTimeUtcTicks);
public sealed record StartupWatchDecision(long Cycle, bool NewCycle, bool ShouldLaunch);

/// <summary>One attempt per verified main-process lifetime, even when no absent poll occurs between starts.</summary>
public sealed class StartupWatchPolicy
{
    readonly HashSet<DesktopRunIdentity> consumed = new();
    readonly LinkedList<DesktopRunIdentity> recent = new();
    long cycle;
    public StartupWatchDecision Evaluate(IEnumerable<DesktopRunIdentity> verifiedMainProcesses, bool widgetRunning)
    {
        var current = verifiedMainProcesses.Where(item => item.ProcessId > 0 && item.StartTimeUtcTicks > 0).Distinct().ToHashSet();
        var fresh = false;
        foreach(var identity in current)
            if(consumed.Add(identity)) { fresh = true; recent.AddLast(identity); }
        // Bound historical state without forgetting a still-observed main process. No disk state is needed.
        for(var node = recent.First; node is not null && consumed.Count > 128;)
        {
            var next = node.Next;
            if(!current.Contains(node.Value)) { consumed.Remove(node.Value); recent.Remove(node); }
            node = next;
        }
        if(fresh) cycle++;
        return new(cycle, fresh, fresh && !widgetRunning);
    }
}

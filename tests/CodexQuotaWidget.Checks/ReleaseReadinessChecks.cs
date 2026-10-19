using CodexQuotaWidget.Core;
using CodexQuotaWidget.Core.Automation;
using CodexQuotaWidget.Core.ControlCenter;

static class ReleaseReadinessChecks
{
    public static int Run()
    {
        var count = 0;
        void Check(bool valid, string why) { if (!valid) throw new Exception(why); count++; }
        Check(ReleaseUpdates.CurrentLabel == "1.13.9", "Consistent release version");
        var settings = new Settings(); settings.Validate();
        Check(!settings.AutoStart && !settings.StartWithCodex && !settings.CloseWithCodex, "Startup actions default off");
        Check(!settings.LowQuotaSafeFinishEnabled, "Legacy percentage shutdown remains disabled");
        Check(settings.SafeFinishProjectPath is null, "No developer project fallback in defaults");
        var safe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIControlCenterFixture");
        Check(AutomationStore.IsSafeLocalPath(safe), "Passive startup supports user local data");
        Check(!AutomationStore.IsSafeDPath(@"C:\AIControlCenterFixture"), "Power automation D-only gate unchanged");
        foreach (var invalid in new[] { "relative", @"\\server\share\widget.exe", @"C:\file:stream", "C:\\bad\nfile", "C:\\" })
            Check(!AutomationStore.IsSafeLocalPath(invalid), "Reject unsafe local path");
        Check(new WorkflowLoopConfiguration().AuthorizedWritePaths.Length == 0, "No implicit write authorization");
        Check(new WorkflowLoopConfiguration().TaskId == Guid.Empty, "No implicit task identity");
        Check(!new WorkflowLoopConfiguration().LocalExecutionAuthorized, "No startup execution authorization");
        return count;
    }
}

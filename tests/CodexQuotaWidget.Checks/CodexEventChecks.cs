using System.Text.Json;
using CodexQuotaWidget.Core;
using CodexQuotaWidget.Core.Automation;
using CodexQuotaWidget.Core.Observation;

public static class CodexEventChecks
{
    public static int Run()
    {
        var count=0;
        void Check(bool condition,string name){if(!condition)throw new InvalidOperationException("Codex event: "+name);count++;}
        var root=Path.Combine(@"D:\CodexQuotaWidget-GitHub\artifacts\event-checks",Guid.NewGuid().ToString("N"));
        var project=Path.Combine(root,"project");var data=Path.Combine(root,"data");Directory.CreateDirectory(project);Directory.CreateDirectory(data);
        var now=DateTimeOffset.UtcNow;var reset=now.AddHours(1).ToUnixTimeSeconds();
        var quota=new ExhaustionReceipt{ResetAt=reset,CycleKey=ExhaustionDetector.CycleKey(reset),PositiveRemaining=5,
            PositiveObservedAt=now.AddSeconds(-30),FirstZeroObservedAt=now.AddSeconds(-5),SecondZeroObservedAt=now.AddSeconds(-2),CreatedAt=now};
        var status=new CodexStatusObservation{State=CodexObservedState.Completed,ObservedAt=now,EvidenceAt=now.AddSeconds(-4),
            Source="LocalCodexSession",ProjectRoot=project,SessionId="session-1",TurnId="turn-1",CoverageComplete=true,ScopedSessionCount=1,TaskStoppedConfirmed=true,
            TrustLevel=CodexObservationTrustLevel.ProjectVerified,EvidenceLevel=CodexEvidenceLevel.LifecycleConfirmed,EvidenceKind="task_complete",EvidencePosition=1024};
        ExhaustionReceipt? Confirm(CodexStatusObservation? candidate,ExhaustionReceipt? evidence=null)
            =>CodexExhaustedEventFactory.Confirm(evidence??quota,candidate,now,project,data);
        Check(Confirm(status) is not null,"valid quota plus explicit same-project terminal creates unified event");
        foreach(var level in new[]{CodexEvidenceLevel.None,CodexEvidenceLevel.LogPresent,CodexEvidenceLevel.SessionMatched,CodexEvidenceLevel.ProjectMatched})
            Check(Confirm(status with{EvidenceLevel=level}) is null,"less than confirmed lifecycle cannot authorize: "+level);
        Check(Confirm(status with{EvidenceKind="token_count"}) is null,"heartbeat is not a terminal marker");
        Check(Confirm(status with{EvidencePosition=null}) is null,"missing native evidence position cannot authorize");
        foreach(var state in new[]{CodexObservedState.Unknown,CodexObservedState.Running,CodexObservedState.WaitingForInput,CodexObservedState.AbnormalExit})
            Check(Confirm(status with{State=state}) is null,"nonterminal state cannot authorize: "+state);
        Check(Confirm(null) is null,"missing task state blocks");
        Check(Confirm(status with{TaskStoppedConfirmed=false}) is null,"nonterminal error blocks");
        var nonterminalError=status with{State=CodexObservedState.UnableToContinue,TaskStoppedConfirmed=false};
        Check(nonterminalError.CanonicalState==CodexTaskState.Unknown && nonterminalError.UnknownReason.Length>0,"error without terminal evidence is Unknown, not Failed");
        Check((status with{State=CodexObservedState.UnableToContinue}).CanonicalState==CodexTaskState.Failed,"explicit unsuccessful terminal remains Failed");
        Check(Confirm(status with{ProjectAssociationVerified=false}) is null,"manual project association never grants native stop authority");
        Check(Confirm(status with{EvidenceAt=null}) is null,"null actual evidence time blocks");
        Check(Confirm(status with{EvidenceAt=now.AddMinutes(-3)}) is null,"old terminal never revived by fresh poll");
        Check(Confirm(status with{ObservedAt=now.AddSeconds(-31)}) is null,"stale scan blocks");
        Check(Confirm(status with{EvidenceAt=now.AddSeconds(1)}) is null,"future evidence blocks");
        Check(Confirm(status with{ProjectRoot=root}) is null,"another project blocks");
        Check(Confirm(status with{SessionId=""}) is null && Confirm(status with{TurnId=""}) is null,"missing identity blocks");
        Check(Confirm(status with{CoverageComplete=false}) is null,"active or unknown other sessions block");
        Check(Confirm(status with{ScopedSessionCount=0}) is null,"missing scoped session blocks");
        Check(Confirm(status with{Source="QuotaAppServer"}) is null,"own quota connection does not represent Desktop turn");
        Check(Confirm(status with{Synthetic=true}) is null,"synthetic status cannot combine with real quota");
        Check(Confirm(status,quota with{RemainingPercent=1}) is null,"positive quota blocks");
        Check(Confirm(status,quota with{ResetAt=reset+1}) is null,"invalid quota cycle blocks");
        Check(!CodexExhaustedEventFactory.Validate(quota,now,project),"quota-only legacy receipt cannot authorize executor");
        var confirmed=Confirm(status)!;
        Check(CodexExhaustedEventFactory.Validate(confirmed,now,project),"unified event validates");
        Check(!CodexExhaustedEventFactory.Validate(confirmed with{ExhaustedEvent=confirmed.ExhaustedEvent! with{CycleKey="changed"}},now,project),"altered event cycle blocks");
        Check(!CodexExhaustedEventFactory.Validate(confirmed,now.AddMinutes(3),project),"delayed dispatch cannot reuse terminal");
        var store=new CodexExhaustedEventStore(data);
        Check(store.Publish(confirmed,now),"real-format local notification written atomically (fixture evidence)");
        var before=File.ReadAllBytes(store.EventPath);
        Check(!store.Publish(confirmed,now),"same event suppressed");
        var otherId=Guid.NewGuid();
        Check(!store.Publish(confirmed with{EventId=otherId,ExhaustedEvent=confirmed.ExhaustedEvent! with{EventId=otherId}},now),
            "same cycle with another event is suppressed");
        var json=JsonSerializer.Deserialize<CodexExhaustedEvent>(File.ReadAllText(store.EventPath))!;
        Check(json.TaskStoppedConfirmed && json.RecoveryFilePath==Path.Combine(data,"codex_resume_state.md") && json.SuggestedNextActions.Length>0,"external agent receives recovery path and passive advice");
        Check(before.SequenceEqual(File.ReadAllBytes(store.EventPath)),"duplicate does not rewrite notification");
        var synthetic=Confirm(status with{Synthetic=true},quota with{Synthetic=true})!;
        try{store.Publish(synthetic,now);throw new Exception("synthetic event published");}catch(InvalidDataException){count++;}
        File.WriteAllText(store.EventPath,"{broken");
        try{store.Publish(confirmed,now);throw new Exception("corrupt notification overwritten");}catch(JsonException){count++;}
        Check(File.ReadAllText(store.EventPath)=="{broken","corrupt primary preserved");
        return count;
    }
}

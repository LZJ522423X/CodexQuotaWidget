using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexQuotaWidget.Core.Observation;

/// <summary>Real isolated D-drive file I/O with synthetic event/agent metadata; no agent actions.</summary>
public static class CodexAgentAckChecks
{
    public static int Run()
    {
        var count = 0;
        void Check(bool value, string name) { if (!value) throw new InvalidOperationException("Codex agent ACK: " + name); count++; }
        var parent = @"D:\CodexQuotaWidget-GitHub\artifacts\agent-ack-checks";
        var root = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        var now = DateTimeOffset.UtcNow;
        var current = new CodexStateChangedEvent
        {
            SchemaVersion = 2, SequenceNumber = 7, EventId = Guid.NewGuid(), BindingId = Guid.NewGuid(),
            SessionId = "ack-session-1", TurnId = "ack-turn-1", CreatedAt = now.AddSeconds(-10),
            ProjectRoot = @"D:\CodexQuotaWidget-GitHub", State = CodexTaskState.Working,
            TrustLevel = CodexObservationTrustLevel.ObservedSession, EvidenceAt = now.AddSeconds(-11), Synthetic = true
        };
        var ack = new CodexAgentAcknowledgement
        {
            EventId = current.EventId, BindingId = current.BindingId, SessionId = current.SessionId!,
            SequenceNumber = current.SequenceNumber, AgentName = "dsh", ReceivedAt = now.AddSeconds(-5)
        };
        try
        {
            var reader = new CodexAgentAcknowledgementReader(root);
            Check(reader.AcknowledgementPath == Path.Combine(root, "events", "agent_ack.json"), "fixed passive path uses supplied D data root");
            Check(reader.Read(current, now).State == CodexAgentAcknowledgementState.Missing, "missing ACK honest result");
            Check(!Directory.Exists(root), "read of missing ACK does not create any directory or file");
            Check(reader.Read(null, now).State == CodexAgentAcknowledgementState.Unavailable, "no current event excludes historical ACK");
            Check(reader.Read(current with { SchemaVersion = 1, SequenceNumber = 0 }, now).State == CodexAgentAcknowledgementState.Unavailable,
                "legacy schema1 event cannot be implicitly assigned a sequence or ACK");
            Check(reader.Read(current with { BindingId = Guid.Empty }, now).State == CodexAgentAcknowledgementState.Unavailable, "unbound current event excluded");
            Check(reader.Read(current with { SessionId = null }, now).State == CodexAgentAcknowledgementState.Unavailable, "sessionless current event excluded");
            Check(reader.Read(current with { SequenceNumber = 0 }, now).State == CodexAgentAcknowledgementState.Unavailable, "uncommitted current event excluded");
            Check(reader.Read(current with { CreatedAt = now.AddSeconds(1) }, now).State == CodexAgentAcknowledgementState.Unavailable, "future current event excluded");
            Check(reader.Read(current with { GrantsActionAuthority = true }, now).State == CodexAgentAcknowledgementState.Unavailable, "active current event cannot solicit passive ACK");
            Directory.CreateDirectory(Path.GetDirectoryName(reader.AcknowledgementPath)!);
            void Write(CodexAgentAcknowledgement item) => File.WriteAllText(reader.AcknowledgementPath, JsonSerializer.Serialize(item));
            void Reject(CodexAgentAcknowledgement item, string name)
            {
                Write(item);
                var before = Hash(reader.AcknowledgementPath);
                var result = reader.Read(current, now);
                Check(result.State == CodexAgentAcknowledgementState.Rejected && result.Acknowledgement is null, name);
                Check(before == Hash(reader.AcknowledgementPath), name + " preserves original ACK bytes");
            }
            Write(ack);
            var validHash = Hash(reader.AcknowledgementPath);
            var received = reader.Read(current, now);
            Check(received.Received && received.Acknowledgement == ack, "fully correlated controlled Received receipt accepted");
            Check(!received.GrantsActionAuthority && !received.ConfirmsTaskStopped && !received.ConfirmsActionSucceeded,
                "Received grants no authority, stop confirmation or action success");
            Check(received.Message.Contains("声明", StringComparison.Ordinal), "display distinguishes external unverified assertion from proof");
            Check(reader.Read(current, now).Acknowledgement == ack && validHash == Hash(reader.AcknowledgementPath), "duplicate polling is read only and leaves same receipt");
            Check(Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length == 1, "reading valid receipt creates no auxiliary state files");

            Reject(ack with { EventId = Guid.NewGuid() }, "old/foreign EventId rejected");
            Reject(ack with { BindingId = Guid.NewGuid() }, "old/foreign binding rejected");
            Reject(ack with { SessionId = "ack-session-2" }, "foreign session rejected");
            Reject(ack with { SessionId = "ACK-SESSION-1" }, "session identity remains case-sensitive");
            Reject(ack with { SequenceNumber = 6 }, "old sequence rejected even with current event ID");
            Reject(ack with { SequenceNumber = 8 }, "future sequence rejected");
            Reject(ack with { ReceivedAt = current.CreatedAt.AddTicks(-1) }, "receipt before event rejected");
            Reject(ack with { ReceivedAt = now.AddTicks(1) }, "future receipt rejected without clock substitution");
            Reject(ack with { ReceivedAt = default }, "default receipt time rejected");
            Reject(ack with { AgentName = "unregistered-agent" }, "unknown agent label rejected");
            Reject(ack with { AgentName = "DSH" }, "controlled agent labels are exact");
            Reject(ack with { AgentName = "dsh\nexecute" }, "multiline agent injection rejected");
            Reject(ack with { AgentName = new string('a', 65) }, "overlong agent label rejected");
            Reject(ack with { Status = "Completed" }, "receipt cannot claim AI completion");
            Reject(ack with { Status = "ActionSucceeded" }, "receipt cannot claim action success");
            Reject(ack with { Status = "TaskStopped" }, "receipt cannot claim stopped task");
            Reject(ack with { PassiveAcknowledgement = false }, "active acknowledgement rejected");
            Reject(ack with { GrantsActionAuthority = true }, "action authority payload rejected");
            Reject(ack with { SchemaVersion = 2 }, "unknown ACK protocol rejected independently of event schema");

            foreach (var name in new[] { "ConfirmsTaskStopped", "Command", "Actions", "Proxy", "SuggestedNextAction" })
            {
                var injected = JsonSerializer.SerializeToNode(ack)!.AsObject(); injected[name] = "untrusted-control-payload";
                File.WriteAllText(reader.AcknowledgementPath, injected.ToJsonString());
                Check(reader.Read(current, now).State == CodexAgentAcknowledgementState.Rejected, "unmapped dangerous field " + name + " rejected");
            }
            var absent = JsonSerializer.SerializeToNode(ack)!.AsObject(); absent.Remove("SequenceNumber");
            File.WriteAllText(reader.AcknowledgementPath, absent.ToJsonString());
            Check(reader.Read(current, now).State == CodexAgentAcknowledgementState.Rejected, "required correlation field cannot default silently");
            absent = JsonSerializer.SerializeToNode(ack)!.AsObject(); absent.Remove("GrantsActionAuthority");
            File.WriteAllText(reader.AcknowledgementPath, absent.ToJsonString());
            Check(reader.Read(current, now).State == CodexAgentAcknowledgementState.Rejected, "explicit passive protocol declaration required");
            var duplicated = JsonSerializer.Serialize(ack).TrimEnd('}') + ",\"EventId\":\"" + ack.EventId + "\"}";
            File.WriteAllText(reader.AcknowledgementPath, duplicated);
            Check(reader.Read(current, now).DiagnosticCode == "DuplicateField", "duplicate JSON identities rejected even when equal");
            File.WriteAllText(reader.AcknowledgementPath, "{broken-ack-untrusted-canary}");
            var malformed = reader.Read(current, now);
            Check(malformed.State == CodexAgentAcknowledgementState.Rejected && !malformed.Message.Contains("untrusted-canary", StringComparison.Ordinal),
                "malformed bytes do not escape into diagnostics");
            Check(current.State == CodexTaskState.Working && current.SequenceNumber == 7, "malformed ACK leaves real observation metadata independent");
            File.WriteAllText(reader.AcknowledgementPath, new string(' ', 16 * 1024 + 1));
            Check(reader.Read(current, now).DiagnosticCode == "InvalidSize", "bounded ACK size enforced before parsing");
            File.WriteAllText(reader.AcknowledgementPath, "[]");
            Check(reader.Read(current, now).DiagnosticCode == "InvalidShape", "array protocol rejected");
            Write(ack);
            using (var exclusive = new FileStream(reader.AcknowledgementPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Check(reader.Read(current, now).State == CodexAgentAcknowledgementState.Rejected, "busy receipt gives diagnostic without mutating AI state");
            Check(reader.Read(current, now).Received, "read recovers after transient exclusive writer releases file");
            File.Delete(reader.AcknowledgementPath); Directory.CreateDirectory(reader.AcknowledgementPath);
            Check(reader.Read(current, now).DiagnosticCode == "UnsafePath", "directory masquerading as fixed ACK file rejected");
            Directory.Delete(reader.AcknowledgementPath);
            Write(ack with { AgentName = "external-agent" });
            Check(reader.Read(current, now).Received, "second configured default consumer label accepted");
            Write(ack with { AgentName = "my-agent" });
            Check(new CodexAgentAcknowledgementReader(root, ["my-agent"]).Read(current, now).Received, "bounded in-memory allowlist supports deliberate consumer configuration");
            Check(reader.Read(current, now).State == CodexAgentAcknowledgementState.Rejected, "custom reader allowlist does not change global defaults");

            try { _ = new CodexAgentAcknowledgementReader(@"C:\Users\FixtureUser\agent-ack"); Check(false, "C root should fail"); }
            catch (ArgumentException) { count++; }
            try { _ = new CodexAgentAcknowledgementReader(root, ["unsafe/agent"]); Check(false, "unsafe label should fail"); }
            catch (ArgumentException) { count++; }
            try { _ = new CodexAgentAcknowledgementReader(root, Enumerable.Range(0, 9).Select(value => "agent" + value)); Check(false, "unbounded labels should fail"); }
            catch (ArgumentException) { count++; }

            // This probes actual link rejection only when Windows permits creating a fixture symlink.
            // A denied symlink privilege is explicitly not reported as a simulated security pass.
            var linkedRoot = Path.Combine(root, "linked-root");
            try
            {
                Directory.CreateSymbolicLink(linkedRoot, Path.GetDirectoryName(reader.AcknowledgementPath)!);
                try { _ = new CodexAgentAcknowledgementReader(linkedRoot); Check(false, "linked data root should fail"); }
                catch (ArgumentException) { count++; }
            }
            catch (Exception error) when (error is UnauthorizedAccessException or IOException or NotSupportedException)
            { Console.WriteLine("Agent ACK symbolic-link fixture: NotVerified (Windows link privilege unavailable)."); }
            return count;
        }
        finally
        {
            // Only this generated fixture subtree is eligible for cleanup; no production ACK/state files.
            if (Path.GetFullPath(root).StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}

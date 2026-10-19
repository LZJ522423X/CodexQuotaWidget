using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexQuotaWidget.Core.ControlCenter;

/// <summary>Real isolated D-drive file I/O. No model, shell, network or computer actions.</summary>
public static class CodexWorkflowProjectFilesChecks
{
    public static int Run()
    {
        var count = 0;
        void Check(bool value, string name) { if (!value) throw new InvalidOperationException("Codex project tools: " + name); count++; }
        var root = Path.Combine(@"D:\CodexQuotaWidget-GitHub\artifacts\project-files-checks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var host = new CodexWorkflowProjectFiles(root, new[] { "Code.cs", "Other.cs", "New.cs", "Absent.cs", "Secret.cs", "Huge.cs" }); var call = 0;
        CodexProjectToolReply Invoke(string tool, object args, string? id = null) => host.Invoke(tool, JsonSerializer.SerializeToElement(args), id ?? "call-" + ++call);
        static JsonElement Data(CodexProjectToolReply reply) => JsonDocument.Parse(reply.ContentItems.Single().Text).RootElement.Clone();
        static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        File.WriteAllText(Path.Combine(root, "Code.cs"), "first\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(root, "Other.cs"), "other\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(root, "AGENTS.md"), "preserve", new UTF8Encoding(false));
        Directory.CreateDirectory(Path.Combine(root, "bin")); File.WriteAllText(Path.Combine(root, "bin", "hidden.cs"), "excluded");
        var list = Invoke("list_project_files", new { });
        Check(list.Success && Data(list).GetProperty("files").EnumerateArray().Count() == 2, "list excludes protected names and build artifacts");
        var read = Invoke("read_project_file", new { relativePath = "Code.cs" });
        Check(read.Success && Data(read).GetProperty("sha256").GetString() == Hash("first\n") && Data(read).GetProperty("content").GetString() == "first\n", "read returns exact content and SHA");
        var written = Invoke("write_project_file", new { relativePath = "Code.cs", expectedSHA = Hash("first\n"), content = "second\n" }, "write-id");
        Check(written.Success && File.ReadAllText(Path.Combine(root, "Code.cs")) == "second\n", "matching SHA permits atomic source replacement");
        var backup = Path.Combine(root, Data(written).GetProperty("backup").GetString()!);
        Check(File.ReadAllText(backup) == "first\n" && backup.StartsWith(Path.Combine(root, ".acc-workflow"), StringComparison.OrdinalIgnoreCase), "first original backup stays in owned D project");
        var repeated = Invoke("write_project_file", new { relativePath = "Code.cs", expectedSHA = Hash("first\n"), content = "second\n" }, "write-id");
        Check(repeated == written && File.ReadAllText(Path.Combine(root, "Code.cs")) == "second\n", "duplicate call returns original reply without writing again");
        var collision = Invoke("write_project_file", new { relativePath = "Code.cs", expectedSHA = Hash("second\n"), content = "collision\n" }, "write-id");
        Check(!collision.Success && File.ReadAllText(Path.Combine(root, "Code.cs")) == "second\n", "same call ID with changed payload cannot mutate files");
        Check(!Invoke("write_project_file", new { relativePath = "Code.cs", expectedSHA = Hash("first\n"), content = "stale\n" }).Success,
            "changed local contents reject stale expected SHA");
        Check(Invoke("write_project_file", new { relativePath = "Code.cs", expectedSHA = Hash("second\n"), content = "third\n" }).Success
            && File.ReadAllText(backup) == "first\n", "repeated edits retain first original rather than intermediate versions");
        var other = Invoke("write_project_file", new { relativePath = "Other.cs", expectedSHA = Hash("other\n"), content = "updated\n" });
        var otherBackup = Path.Combine(root, Data(other).GetProperty("backup").GetString()!);
        Check(other.Success && backup != otherBackup && File.ReadAllText(otherBackup) == "other\n" && File.ReadAllText(backup) == "first\n",
            "second file gets distinct bounded backup and preserves first file backup");
        Check(Invoke("write_project_file", new { relativePath = "New.cs", expectedSHA = "MISSING", content = "new\n" }).Success, "explicit missing expectation creates only a new permitted file");
        Check(!Invoke("write_project_file", new { relativePath = "New.cs", expectedSHA = "MISSING", content = "overwrite\n" }).Success, "MISSING cannot overwrite a file created later");
        Check(!Invoke("write_project_file", new { relativePath = "Absent.cs", expectedSHA = Hash("absent"), content = "unexpected" }).Success, "SHA expectation cannot silently create missing file");
        foreach (var relative in new[] { "../Outside.cs", @"..\Outside.cs", "AGENTS.md", ".codex/auth.json", "auth.json", ".env.txt", "env.json", "Code.cs:stream", @"C:\Other.cs", "bin/hidden.cs", "a/../Code.cs", "./Code.cs", "Code.cs.", "run.ps1", ".acc-workflow/file.txt", "NUL.txt", "CON.cs", "COM1.txt", "LPT9.cs", "COM¹.txt", "LPT².cs", "AUX/file.cs", "bad?.cs" })
            Check(!Invoke("read_project_file", new { relativePath = relative }).Success, "protected/traversal/ADS/unsupported path rejected: " + relative);
        foreach (var relative in new[] { "Project.csproj", "Directory.Build.props", "Build.targets", "Project.sln" })
            Check(!Invoke("write_project_file", new { relativePath = relative, expectedSHA = "MISSING", content = "blocked build definition" }).Success
                && !File.Exists(Path.Combine(root, relative)), "host validator build definition cannot be mutated: " + relative);
        File.WriteAllText(Path.Combine(root, "ReadOnly.csproj"), "<Project />", new UTF8Encoding(false));
        Check(Invoke("read_project_file", new { relativePath = "ReadOnly.csproj" }).Success, "build definitions remain readable for project understanding");
        Check(File.ReadAllText(Path.Combine(root, "AGENTS.md")) == "preserve", "protected instructions preserved");
        var secret = "sk-" + new string('a', 32); File.WriteAllText(Path.Combine(root, "Credential.cs"), secret);
        var refused = Invoke("read_project_file", new { relativePath = "Credential.cs" });
        Check(!refused.Success && !refused.ContentItems[0].Text.Contains(secret, StringComparison.Ordinal), "credential read is refused without echoing secret");
        Check(!Invoke("write_project_file", new { relativePath = "Secret.cs", expectedSHA = "MISSING", content = secret }).Success && !File.Exists(Path.Combine(root, "Secret.cs")), "credential write rejected before touching target");
        Check(!Invoke("write_project_file", new { relativePath = "Huge.cs", expectedSHA = "MISSING", content = new string('z', CodexWorkflowProjectFiles.MaximumFileBytes + 1) }).Success, "oversized write refused");
        File.WriteAllBytes(Path.Combine(root, "Binary.cs"), new byte[] { 0, 1, 2 });
        Check(!Invoke("read_project_file", new { relativePath = "Binary.cs" }).Success, "binary content cannot be returned as source text");
        Check(!Invoke("read_project_file", new { relativePath = "Code.cs", extra = true }).Success, "unexpected tool arguments rejected");
        Check(!Invoke("run_command", new { command = "fixture only" }).Success, "unlisted command tool unavailable");
        Check(Invoke("list_project_files", new { }).Success && Directory.GetFiles(Path.Combine(root, ".acc-workflow"), "pending-*.tmp").Length == 0,
            "completed file writes leave no staging files");
        var capRoot = Path.Combine(root, "call-cap"); Directory.CreateDirectory(capRoot); var capped = new CodexWorkflowProjectFiles(capRoot);
        for (var index = 0; index < CodexWorkflowProjectFiles.MaximumCalls; index++)
            if (!capped.Invoke("list_project_files", JsonSerializer.SerializeToElement(new { }), "cap-" + index).Success) throw new InvalidOperationException("Premature project call cap");
        Check(!capped.Invoke("list_project_files", JsonSerializer.SerializeToElement(new { }), "over-cap").Success, "call cache bounded at 256 replies");
        Check(capped.Invoke("list_project_files", JsonSerializer.SerializeToElement(new { }), "cap-0").Success, "duplicate remains idempotent when cache is full");
        var listRoot = Path.Combine(root, "list-cap"); Directory.CreateDirectory(listRoot);
        for (var index = 0; index <= CodexWorkflowProjectFiles.MaximumFiles; index++) File.WriteAllText(Path.Combine(listRoot, "file-" + index + ".txt"), "fixture");
        var limited = new CodexWorkflowProjectFiles(listRoot).Invoke("list_project_files", JsonSerializer.SerializeToElement(new { }), "list-cap");
        Check(limited.Success && Data(limited).GetProperty("files").GetArrayLength() == CodexWorkflowProjectFiles.MaximumFiles
            && Data(limited).GetProperty("truncated").GetBoolean(), "listing is bounded at 500 files and explicitly reports truncation");
        try
        {
            var outside = Path.Combine(Path.GetDirectoryName(root)!, "outside-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "Outside.cs"), "outside unchanged");
            Directory.CreateSymbolicLink(Path.Combine(root, "linked"), outside);
            Check(!Invoke("read_project_file", new { relativePath = "linked/Outside.cs" }).Success, "ancestor directory link cannot escape project read scope");
            Check(!Invoke("write_project_file", new { relativePath = "linked/Outside.cs", expectedSHA = Hash("outside unchanged"), content = "forbidden" }).Success
                && File.ReadAllText(Path.Combine(outside, "Outside.cs")) == "outside unchanged", "ancestor link cannot escape project write scope");
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or System.ComponentModel.Win32Exception)
        { Console.WriteLine("Project tool directory-link fixture: NotVerified (Windows link privilege unavailable)."); }
        var specs = JsonSerializer.SerializeToElement(CodexWorkflowProjectFiles.DynamicToolSpecs());
        Check(specs[0].GetProperty("type").GetString() == "namespace" && specs[0].GetProperty("tools").GetArrayLength() == 3,
            "only three explicit project dynamic capabilities declared");
        File.WriteAllText(Path.Combine(root, "OutOfScope.cs"), "preserved");
        Check(!Invoke("write_project_file", new { relativePath = "OutOfScope.cs", expectedSHA = Hash("preserved"), content = "forbidden" }).Success
            && File.ReadAllText(Path.Combine(root, "OutOfScope.cs")) == "preserved", "a permitted extension outside frozen write scope remains unchanged");
        Check(!Invoke("write_project_file", new { relativePath = "Unapproved.cs", expectedSHA = "MISSING", content = "forbidden" }).Success
            && !File.Exists(Path.Combine(root, "Unapproved.cs")), "new files cannot expand the frozen write scope");
        var readOnlyHost = new CodexWorkflowProjectFiles(root);
        Check(readOnlyHost.Invoke("read_project_file", JsonSerializer.SerializeToElement(new { relativePath = "Code.cs" }), "read-only-read").Success,
            "empty write scope preserves permitted project reads");
        Check(!readOnlyHost.Invoke("write_project_file", JsonSerializer.SerializeToElement(new { relativePath = "Code.cs", expectedSHA = Hash("third\n"), content = "forbidden" }), "read-only-write").Success
            && File.ReadAllText(Path.Combine(root, "Code.cs")) == "third\n", "read-only host refuses write even if a model requests an undeclared tool");
        var readOnlySpecs = JsonSerializer.SerializeToElement(CodexWorkflowProjectFiles.DynamicToolSpecs(readOnly: true));
        Check(readOnlySpecs[0].GetProperty("tools").GetArrayLength() == 2
            && readOnlySpecs[0].GetProperty("tools").EnumerateArray().All(spec => spec.GetProperty("name").GetString() != "write_project_file"),
            "read-only requests never declare a write capability");
        var scopedRoot = Path.Combine(root, "directory-scope"); Directory.CreateDirectory(Path.Combine(scopedRoot, "src"));
        Directory.CreateDirectory(Path.Combine(scopedRoot, "src-other"));
        var scoped = new CodexWorkflowProjectFiles(scopedRoot, new[] { "src/" });
        Check(scoped.Invoke("write_project_file", JsonSerializer.SerializeToElement(new { relativePath = "src/New.cs", expectedSHA = "MISSING", content = "authorized" }), "scoped-write").Success,
            "explicit directory prefix allows its own subtree");
        Check(!scoped.Invoke("write_project_file", JsonSerializer.SerializeToElement(new { relativePath = "src-other/New.cs", expectedSHA = "MISSING", content = "forbidden" }), "sibling-write").Success
            && !File.Exists(Path.Combine(scopedRoot, "src-other", "New.cs")), "directory prefix cannot authorize a similarly named sibling");
        var stepRoot = Path.Combine(root, "step-scope"); Directory.CreateDirectory(stepRoot);
        File.WriteAllText(Path.Combine(stepRoot, "A.cs"), "original-a"); File.WriteAllText(Path.Combine(stepRoot, "B.cs"), "original-b");
        var stepHost = new CodexWorkflowProjectFiles(stepRoot, new[] { "A.cs", "B.cs" });
        Check(!stepHost.Invoke("write_project_file", JsonSerializer.SerializeToElement(new { relativePath = "B.cs", expectedSHA = Hash("original-b"), content = "forbidden" }), "turn-a/blocked-b", new[] { "A.cs" }).Success
            && File.ReadAllText(Path.Combine(stepRoot, "B.cs")) == "original-b", "task-authorized second file remains unchanged outside the current advice-plan subset");
        Check(stepHost.Invoke("write_project_file", JsonSerializer.SerializeToElement(new { relativePath = "A.cs", expectedSHA = Hash("original-a"), content = "allowed-a" }), "turn-a/write-a", new[] { "A.cs" }).Success,
            "current advice-plan subset grants its own file capability");
        Check(stepHost.Invoke("write_project_file", JsonSerializer.SerializeToElement(new { relativePath = "B.cs", expectedSHA = Hash("original-b"), content = "allowed-b" }), "turn-b/write-b", new[] { "B.cs" }).Success,
            "later explicit step may use another subset of the same task authorization");
        Check(!stepHost.Invoke("write_project_file", JsonSerializer.SerializeToElement(new { relativePath = "A.cs", expectedSHA = Hash("allowed-a"), content = "forbidden" }), "empty-step/write", Array.Empty<string>()).Success
            && File.ReadAllText(Path.Combine(stepRoot, "A.cs")) == "allowed-a", "empty step scope cannot inherit the task's broader write grants");
        Check(!stepHost.Invoke("write_project_file", JsonSerializer.SerializeToElement(new { relativePath = "New.cs", expectedSHA = "MISSING", content = "forbidden" }), "outside-step/write", new[] { "New.cs" }).Success
            && !File.Exists(Path.Combine(stepRoot, "New.cs")), "direct tool invocation cannot inject a step grant outside task authorization");
        var exactCall = new { relativePath = "A.cs", expectedSHA = Hash("original-a"), content = "allowed-a" };
        Check(!stepHost.Invoke("write_project_file", JsonSerializer.SerializeToElement(exactCall), "turn-a/write-a", new[] { "B.cs" }).Success,
            "tool-call cache cannot reuse a prior successful write under a changed step scope");
        return count;
    }
}

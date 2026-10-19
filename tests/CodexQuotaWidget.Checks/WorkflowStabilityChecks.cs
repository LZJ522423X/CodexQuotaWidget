using System.Text.Json;
using System.Text.Json.Serialization;
using CodexQuotaWidget.Core.ControlCenter;

/// <summary>Fault injection against the real loop and D-drive stores, with explicitly simulated providers only.</summary>
public static class WorkflowStabilityChecks
{
    const string Parent = @"D:\CodexQuotaWidget-GitHub\artifacts\workflow-stability-checks";
    const string Session = "0b56b33e-2ac4-49a9-b5f8-e2a862d07d96";
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static async Task<int> RunAsync()
    {
        var root = Path.Combine(Parent, Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "project"); Directory.CreateDirectory(project);
        await File.WriteAllTextAsync(Path.Combine(project, "Program.cs"), "// inert stability fixture; never compiled or executed\n");
        var assertions = 0; var cases = new List<string>();
        void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException("Workflow stability: " + message);
            assertions++; Console.WriteLine("  PASS " + message);
        }
        WorkflowLoopConfiguration Config() => new() { TaskId = Guid.NewGuid(), ProjectRoot = project, AuthorizedWritePaths = ["Program.cs"],
            ChatGptThreadId = Guid.NewGuid().ToString("D"), CallerThreadId = Guid.NewGuid().ToString("D"),
            Goal = "只验证模拟步骤与工作流证据保护，不运行真实模型或项目。", LocalExecutionAuthorized = true, MaximumRounds = 3,
            BuildProjectPath = "Inert.Build.csproj", ChecksProjectPath = "Inert.Checks.csproj" };
        WorkflowLoopService Loop(string name, Fake chat, Fake codex, Validator validation) => new(Path.Combine(root, name), chat, codex,
            validation, _ => Task.FromResult(new QuotaGuardSnapshot(QuotaGuardState.Ready, 90, 90, true, false, "verified fixture quota")));
        Fake AdviceAndReview(bool continueReview = false, bool repeatAdvice = false) => new("sim-gpt", (request, index, _) => Task.FromResult(
            Reply(request, IsAdvice(request) ? repeatAdvice ? "唯一受限步骤" : "受限步骤 " + index
                : continueReview ? "ACC_REVIEW: CONTINUE\n同目标下一步骤。" : "ACC_REVIEW: COMPLETE\n模拟目标完成。")));
        Fake Execution() => new("sim-codex", (request, _, _) => Task.FromResult(Reply(request, "模拟原生终止报告", Session)));
        async Task<WorkflowLoopState> ConfirmedCheckpoint(string name, WorkflowLoopConfiguration configuration, WorkflowCheckpoint checkpoint)
        {
            WorkflowLoopState? captured = null;
            var chat = new Fake("sim-gpt", (request, _, _) => Task.FromResult(Reply(request,
                IsAdvice(request) ? "已确认的前序受限步骤" : "ACC_REVIEW: CONTINUE\n同目标尚有下一步骤。")));
            var source = Loop(name + "-source", chat, Execution(), new Validator());
            source.Changed += () =>
            {
                if (captured is not null || source.State.Checkpoint != checkpoint || source.State.CurrentRequestId != Guid.Empty) return;
                captured = JsonSerializer.Deserialize<WorkflowLoopState>(File.ReadAllText(source.StatePath), Json);
                source.Pause();
            };
            var result = await source.RunAsync(configuration);
            var expectedEvidence = checkpoint == WorkflowCheckpoint.ReviewReceived ? 3 : 2;
            Check(!result.Success && captured is not null && captured.RunId == configuration.TaskId
                && captured.Evidence.Length == expectedEvidence && captured.CodexSessionId == Session
                && captured.Evidence.All(item => item.RequestId != Guid.Empty && item.CompletedTurnId.Length > 0),
                name + " fixture originates from a confirmed simulated run persisted on D drive");
            return captured!;
        }

        try
        {
            foreach (var defect in new[] { "wrong-project", "old-request", "old-task", "wrong-authorization", "gpt-session-switch" })
            {
                var configuration = Config(); var validation = new Validator(); var codex = Execution();
                var chat = new Fake("sim-gpt", (request, _, _) =>
                {
                    var valid = Reply(request, "一个受限步骤");
                    var invalid = defect switch { "wrong-project" => valid with { ProjectRoot = Path.Combine(root, "other-project") },
                        "old-request" => valid with { RequestId = Guid.NewGuid() }, "old-task" => valid with { WorkflowTaskId = Guid.NewGuid() },
                        "wrong-authorization" => valid with { AuthorizationId = "different-authority" },
                        _ => valid with { ContinuationId = Guid.NewGuid().ToString("D") } };
                    return Task.FromResult(invalid);
                });
                var loop = Loop(defect, chat, codex, validation); var result = await loop.RunAsync(configuration);
                Check(!result.Success && loop.State.Phase == WorkflowLoopPhase.Blocked && loop.State.NeedsRecoveryConfirmation, defect + " blocks untrusted typed result");
                Check(chat.Requests.Count == 1 && codex.Requests.Count == 0 && validation.Calls == 0 && loop.State.Evidence.Length == 0,
                    defect + " never reaches execution or adds false evidence");
                Check(!loop.AcknowledgeRecovery().Success && !(await loop.RunAsync(configuration)).Success && chat.Requests.Count == 1,
                    defect + " cannot be erased by acknowledgement or replay"); cases.Add(defect);
            }

            foreach (var defect in new[] { "requires-confirmation", "scope-escape", "scope-unapproved", "advice-old-task", "advice-old-request", "advice-wrong-project", "advice-wrong-authorization", "advice-extra-field" })
            {
                var configuration = Config(); var codex = Execution(); var validation = new Validator();
                var chat = new Fake("sim-gpt", (request, _, _) =>
                {
                    var plan = new WorkflowAdvicePlan(request.WorkflowTaskId, request.RequestId, request.AuthorizedProjectRoot,
                        request.AuthorizationId, "仅处理允许文件", ["Program.cs"], defect == "requires-confirmation");
                    plan = defect switch { "scope-escape" => plan with { WritePaths = ["../outside.txt"] },
                        "scope-unapproved" => plan with { WritePaths = ["Other.cs"] },
                        "advice-old-task" => plan with { TaskId = Guid.NewGuid() }, "advice-old-request" => plan with { RequestId = Guid.NewGuid() },
                        "advice-wrong-project" => plan with { ProjectRoot = Path.Combine(root, "other-project") },
                        "advice-wrong-authorization" => plan with { AuthorizationId = "different-authority" }, _ => plan };
                    var payload = JsonSerializer.Serialize(plan);
                    if (defect == "advice-extra-field") payload = payload[..^1] + ",\"ExtraPower\":true}";
                    return Task.FromResult(Reply(request, "ACC_ADVICE: " + payload));
                });
                var loop = Loop(defect, chat, codex, validation); var result = await loop.RunAsync(configuration);
                Check(!result.Success && loop.State.Phase == WorkflowLoopPhase.Blocked && !loop.State.NeedsRecoveryConfirmation
                    && loop.State.CurrentRequestId == Guid.Empty, defect + " rejects confirmed but unauthorized advice");
                Check(codex.Requests.Count == 0 && validation.Calls == 0 && loop.State.Evidence.Length == 1, defect + " preserves reply evidence without execution");
                cases.Add(defect);
            }

            foreach (var defect in new[] { "codex-project-switch", "codex-session-switch", "duplicate-turn" })
            {
                var configuration = Config(); var chat = AdviceAndReview(true); var validation = new Validator(); string firstTurn = "";
                var codex = new Fake("sim-codex", (request, index, _) =>
                {
                    var result = Reply(request, "模拟执行完成 " + index, Session);
                    if (index == 1) firstTurn = result.CompletedTurnId;
                    else result = defect switch { "codex-project-switch" => result with { ProjectRoot = Path.Combine(root, "other-project") },
                        "codex-session-switch" => result with { ContinuationId = Guid.NewGuid().ToString("D") },
                        _ => result with { CompletedTurnId = firstTurn } };
                    return Task.FromResult(result);
                });
                var loop = Loop(defect, chat, codex, validation); var result = await loop.RunAsync(configuration);
                Check(!result.Success && loop.State.Phase == WorkflowLoopPhase.Blocked && loop.State.CompletedRounds == 1,
                    defect + " cannot claim completion of its second round");
                Check(codex.Requests.Count == 2 && validation.Calls == 1 && chat.Requests.Count == 3
                    && codex.Requests[1].ContinuationId == Session && loop.State.CodexSessionId == Session,
                    defect + " preserves first confirmed session and never validates or reviews invalid second result");
                Check(loop.State.Evidence.Count(item => item.Provider == WorkflowProvider.Codex) == 1, defect + " preserves exactly one accepted execution Turn"); cases.Add(defect);
            }

            {
                var configuration = Config(); var chat = AdviceAndReview(true, true); var codex = Execution(); var validation = new Validator();
                var loop = Loop("repeated-step", chat, codex, validation); var result = await loop.RunAsync(configuration);
                Check(!result.Success && loop.State.Phase == WorkflowLoopPhase.Blocked && loop.State.CompletedRounds == 1
                    && loop.State.Round == 2, "repeated completed step stops the loop instead of spending remaining rounds");
                Check(chat.Requests.Count == 3 && codex.Requests.Count == 1 && validation.Calls == 1, "repeated advice cannot dispatch a second execution"); cases.Add("repeated-step");
            }

            {
                var configuration = Config(); WorkflowExecutionResult? oldReply = null; var pendingRequestId = Guid.Empty;
                var codex = Execution(); var validation = new Validator();
                var chat = new Fake("sim-gpt", (request, index, _) =>
                {
                    if (index == 1) { oldReply = Reply(request, "第一轮受限步骤"); return Task.FromResult(oldReply); }
                    if (index == 2) return Task.FromResult(Reply(request, "ACC_REVIEW: CONTINUE"));
                    pendingRequestId = request.RequestId; return Task.FromResult(oldReply!);
                });
                var loop = Loop("old-reply-replay", chat, codex, validation); var result = await loop.RunAsync(configuration);
                Check(!result.Success && loop.State.Phase == WorkflowLoopPhase.Blocked && loop.State.NeedsRecoveryConfirmation
                    && loop.State.CompletedRounds == 1 && pendingRequestId != Guid.Empty && pendingRequestId != oldReply!.RequestId
                    && loop.State.CurrentRequestId == pendingRequestId && loop.State.PendingProvider == WorkflowProvider.ChatGPT,
                    "actual prior-round reply cannot complete a newly pending request");
                Check(codex.Requests.Count == 1 && validation.Calls == 1 && chat.Requests.Count == 3 && loop.State.Evidence.Length == 3
                    && loop.State.Evidence.Select(item => item.RequestId).ToHashSet().SetEquals(new[] {
                        chat.Requests[0].RequestId, codex.Requests[0].RequestId, chat.Requests[1].RequestId }),
                    "late prior-round reply preserves the first round and prevents another execution"); cases.Add("old-reply-replay");
            }

            {
                var configuration = Config(); var adviceEntered = Signal(); var executionEntered = Signal(); var releaseAdvice = Signal(); var releaseExecution = Signal();
                var chat = new Fake("sim-gpt", async (request, _, token) =>
                {
                    if (!IsAdvice(request)) return Reply(request, "ACC_REVIEW: COMPLETE");
                    adviceEntered.TrySetResult(true); await releaseAdvice.Task.WaitAsync(token); return Reply(request, "只修改原允许文件");
                });
                var codex = new Fake("sim-codex", async (request, _, token) =>
                { executionEntered.TrySetResult(true); await releaseExecution.Task.WaitAsync(token); return Reply(request, "模拟终止", Session); });
                var validation = new Validator(); var loop = Loop("array-aliases", chat, codex, validation); var run = loop.RunAsync(configuration);
                try
                {
                    await adviceEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)); configuration.AuthorizedWritePaths[0] = "outside.txt";
                    var firstView = loop.State; firstView.AuthorizedWritePaths[0] = "outside.txt";
                    Check(loop.State.AuthorizedWritePaths.SequenceEqual(new[] { "Program.cs" }), "configuration and returned state arrays cannot increase frozen authority");
                    releaseAdvice.TrySetResult(true); await executionEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    var executionView = loop.State; executionView.StepWritePaths[0] = "outside.txt"; executionView.AuthorizedWritePaths[0] = "outside.txt";
                    executionView.Evidence[0] = executionView.Evidence[0] with { SessionId = "attacker-session", CompletedTurnId = "attacker-turn" };
                    Check(loop.State.StepWritePaths.SequenceEqual(new[] { "Program.cs" }) && loop.State.Evidence[0].SessionId != "attacker-session",
                        "step scope and evidence arrays are defensive snapshots");
                    Check(codex.Requests.Single().AuthorizedWritePaths.SequenceEqual(new[] { "Program.cs" })
                        && codex.Requests.Single().StepWritePaths.SequenceEqual(new[] { "Program.cs" }), "execution receives the original frozen task and step grants");
                    releaseExecution.TrySetResult(true);
                    Check((await run.WaitAsync(TimeSpan.FromSeconds(5))).Success && loop.State.CompletedRounds == 1, "array mutation cannot corrupt completion of the originally authorized step");
                }
                finally { releaseAdvice.TrySetResult(true); releaseExecution.TrySetResult(true); loop.Pause(); await run.WaitAsync(TimeSpan.FromSeconds(5)); }
                cases.Add("array-aliases");
            }

            {
                var configuration = Config(); var entered = Signal(); var release = Signal(); var codex = Execution(); var validation = new Validator();
                var chat = new Fake("sim-gpt", async (request, _, _) => { entered.TrySetResult(true); await release.Task; return Reply(request, "迟到的确认建议"); });
                var loop = Loop("stop-late-reply", chat, codex, validation); var run = loop.RunAsync(configuration);
                try
                {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Check(loop.Stop().Success, "running stop is accepted without assuming remote completion");
                    release.TrySetResult(true); var result = await run.WaitAsync(TimeSpan.FromSeconds(5));
                    Check(!result.Success && loop.State.Phase == WorkflowLoopPhase.Stopped && loop.State.NeedsRecoveryConfirmation
                        && loop.State.CurrentRequestId != Guid.Empty && loop.State.Evidence.Length == 0, "late reply ignoring cancellation cannot overwrite Stopped or clear unknown delivery");
                    var durable = JsonSerializer.Deserialize<WorkflowLoopState>(await File.ReadAllTextAsync(loop.StatePath), Json)!;
                    Check(durable.Phase == WorkflowLoopPhase.Stopped && durable.NeedsRecoveryConfirmation
                        && durable.CurrentRequestId == loop.State.CurrentRequestId, "Stopped and its pending identity are persisted to real D-drive state");
                    var beforeRerun = loop.State; var beforeRerunBytes = await File.ReadAllBytesAsync(loop.StatePath);
                    var rerun = await loop.RunAsync(configuration);
                    var afterRerunBytes = await File.ReadAllBytesAsync(loop.StatePath);
                    Check(!rerun.Success && chat.Requests.Count == 1 && codex.Requests.Count == 0 && validation.Calls == 0
                        && WorkflowTestProtocol.SameState(loop.State, beforeRerun) && beforeRerunBytes.SequenceEqual(afterRerunBytes),
                        "stopped unknown request never restarts the same task or dispatches downstream work");
                }
                finally { release.TrySetResult(true); loop.Pause(); await run.WaitAsync(TimeSpan.FromSeconds(5)); }
                cases.Add("stop-late-reply");
            }

            {
                var configuration = Config(); var data = Path.Combine(root, "unknown-pending"); var pending = Saved(configuration) with {
                    Phase = WorkflowLoopPhase.Blocked, Checkpoint = WorkflowCheckpoint.Execution, CurrentRequestId = Guid.NewGuid(),
                    PendingProvider = WorkflowProvider.Codex, NeedsRecoveryConfirmation = true, StepWritePaths = ["Program.cs"] };
                await WriteState(data, pending); var chat = AdviceAndReview(); var codex = Execution(); var validation = new Validator();
                var loop = Loop("unknown-pending", chat, codex, validation); var original = await File.ReadAllBytesAsync(loop.StatePath);
                Check(!loop.AcknowledgeRecovery().Success && !(await loop.RunAsync(configuration)).Success,
                    "manual acknowledgement cannot eliminate an unknown pending request");
                var after = await File.ReadAllBytesAsync(loop.StatePath);
                Check(chat.Requests.Count == 0 && codex.Requests.Count == 0 && validation.Calls == 0
                    && original.SequenceEqual(after), "unknown pending recovery never replays or changes durable state"); cases.Add("unknown-pending");
            }

            {
                var configuration = Config(); var data = Path.Combine(root, "confirmed-journal-blocked"); var requestId = Guid.NewGuid();
                var saved = Saved(configuration) with { Phase = WorkflowLoopPhase.Blocked, Checkpoint = WorkflowCheckpoint.Execution,
                    CurrentRequestId = requestId, PendingProvider = WorkflowProvider.Codex, NeedsRecoveryConfirmation = true, StepWritePaths = ["Program.cs"] };
                await WriteState(data, saved);
                var request = Request(saved, WorkflowProvider.Codex); var result = Reply(request, "确认完成、尚未来得及写主状态", Session);
                await WriteJournal(data, saved, result);
                var chat = AdviceAndReview(); var codex = Execution(); var validation = new Validator(); var loop = Loop("confirmed-journal-blocked", chat, codex, validation);
                Check(loop.State.Checkpoint == WorkflowCheckpoint.Validation && !loop.State.NeedsRecoveryConfirmation
                    && loop.State.CurrentRequestId == Guid.Empty && loop.State.ExecutionReport == result.Message,
                    "Blocked crash window recovers its already confirmed journal to Validation");
                Check((await loop.RunAsync(configuration)).Success && loop.State.Phase == WorkflowLoopPhase.Completed
                    && codex.Requests.Count == 0 && validation.Calls == 1 && chat.Requests.Count == 1 && !IsAdvice(chat.Requests[0]),
                    "journal recovery validates and reviews without executing or advising again"); cases.Add("confirmed-journal-blocked");
            }

            {
                var configuration = Config(); var data = Path.Combine(root, "review-received");
                var confirmed = await ConfirmedCheckpoint("review-received", configuration, WorkflowCheckpoint.ReviewReceived);
                var saved = confirmed with { Phase = WorkflowLoopPhase.Blocked };
                await WriteState(data, saved); var chat = AdviceAndReview(); var codex = Execution(); var validation = new Validator();
                var loop = Loop("review-received", chat, codex, validation);
                Check((await loop.RunAsync(configuration)).Success && loop.State.Round == 2 && loop.State.CompletedRounds == 2,
                    "ReviewReceived recovery advances the next round and its completed count");
                Check(chat.Requests.Count == 2 && IsAdvice(chat.Requests[0]) && !IsAdvice(chat.Requests[1]) && codex.Requests.Count == 1
                    && validation.Calls == 1, "confirmed review is never repeated on resume"); cases.Add("review-received");
                Check(loop.State.Evidence.Length == 6 && loop.State.Evidence.Take(confirmed.Evidence.Length).SequenceEqual(confirmed.Evidence),
                    "resumed review checkpoint preserves the complete confirmed first-round evidence");
            }

            {
                var configuration = Config() with { MaximumRounds = 1 }; var chat = AdviceAndReview(true); var codex = Execution(); var validation = new Validator();
                var loop = Loop("round-budget", chat, codex, validation); var result = await loop.RunAsync(configuration);
                Check(result.Success && loop.State.Phase == WorkflowLoopPhase.Paused && loop.State.Round == 1 && loop.State.CompletedRounds == 1
                    && chat.Requests.Count == 2 && codex.Requests.Count == 1, "round budget pauses after exactly one confirmed round without expanding scope");
                foreach (var invalid in new[] { configuration with { MaximumRounds = 0 }, configuration with { MaximumRounds = 11 },
                    configuration with { MaximumRunMinutes = 0 }, configuration with { MaximumRunMinutes = 61 } })
                {
                    var blockedChat = AdviceAndReview(); var blockedCodex = Execution(); var blocked = Loop("invalid-budget-" + cases.Count, blockedChat, blockedCodex, new Validator());
                    Check(!(await blocked.RunAsync(invalid)).Success && blockedChat.Requests.Count == 0 && blockedCodex.Requests.Count == 0,
                        "invalid resource budget refuses every model request"); cases.Add("invalid-budget");
                }
                cases.Add("round-budget");
            }

            {
                var configuration = Config(); var data = Path.Combine(root, "confirmed-user-recovery");
                var confirmed = await ConfirmedCheckpoint("confirmed-user-recovery", configuration, WorkflowCheckpoint.Validation);
                await WriteState(data, confirmed with { Phase = WorkflowLoopPhase.Blocked, NeedsRecoveryConfirmation = true });
                var chat = AdviceAndReview(); var codex = Execution(); var validation = new Validator(); var loop = Loop("confirmed-user-recovery", chat, codex, validation);
                Check(!(await loop.RunAsync(configuration)).Success && chat.Requests.Count == 0 && validation.Calls == 0,
                    "a confirmed checkpoint still waits for the explicit recovery gate");
                Check(loop.AcknowledgeRecovery().Success && loop.State.Phase == WorkflowLoopPhase.Paused && !loop.State.NeedsRecoveryConfirmation,
                    "explicit acknowledgement unlocks only an already confirmed checkpoint");
                Check((await loop.RunAsync(configuration)).Success && codex.Requests.Count == 0 && chat.Requests.Count == 1 && validation.Calls == 1,
                    "acknowledged recovery resumes validation rather than replaying execution"); cases.Add("confirmed-user-recovery");
                Check(loop.State.Evidence.Length == 3 && loop.State.Evidence.Take(confirmed.Evidence.Length).SequenceEqual(confirmed.Evidence)
                    && loop.State.ExecutionReport == confirmed.ExecutionReport,
                    "acknowledged recovery preserves the original execution report and accepted evidence");
            }

            {
                var configuration = Config();
                var legacy = "{\"RunId\":\"" + configuration.TaskId.ToString("D") + "\",\"Phase\":\"Completed\",\"Round\":1,\"CompletedRounds\":1}";
                var legacyLoop = Loop("history-legacy", AdviceAndReview(), Execution(), new Validator());
                Directory.CreateDirectory(Path.GetDirectoryName(legacyLoop.StatePath)!);
                await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(legacyLoop.StatePath)!, "history.json"), "[" + legacy + "]");
                Check(legacyLoop.ReadHistory().Single().RunId == configuration.TaskId,
                    "legacy history remains readable without new authorization or resource-budget fields");
                Check(Loop("history-absent", AdviceAndReview(), Execution(), new Validator()).ReadHistory().Count == 0,
                    "a fresh store with no history or backup still returns an empty history");

                foreach (var defect in new[] { "missing-primary", "json-null", "null-item", "missing-identity", "empty-identity", "duplicate-identity", "invalid-phase", "primary-directory", "backup-directory", "valid-empty" })
                {
                    var name = "history-" + defect; var chat = AdviceAndReview(); var codex = Execution(); var validation = new Validator();
                    var loop = Loop(name, chat, codex, validation); var directory = Path.GetDirectoryName(loop.StatePath)!;
                    Directory.CreateDirectory(directory); var historyPath = Path.Combine(directory, "history.json");
                    string? primary = defect switch
                    {
                        "missing-primary" or "primary-directory" => null, "json-null" => "null", "null-item" => "[null]",
                        "missing-identity" => "[{\"Phase\":\"Completed\"}]",
                        "empty-identity" => "[" + legacy.Replace(configuration.TaskId.ToString("D"), Guid.Empty.ToString("D")) + "]",
                        "duplicate-identity" => "[" + legacy[..^1] + ",\"RunId\":\"" + Guid.NewGuid().ToString("D") + "\"}]",
                        "invalid-phase" => "[" + legacy.Replace("\"Completed\"", "999") + "]", "valid-empty" => "[]", _ => "[" + legacy + "]"
                    };
                    if (primary is not null) await File.WriteAllTextAsync(historyPath, primary);
                    if (defect == "missing-primary") await File.WriteAllTextAsync(historyPath + ".bak", "[" + legacy + "]");
                    if (defect == "primary-directory") Directory.CreateDirectory(historyPath);
                    if (defect == "backup-directory") Directory.CreateDirectory(historyPath + ".bak");
                    if (defect == "valid-empty") { Check(loop.ReadHistory().Count == 0, "a valid empty history remains compatible"); continue; }
                    bool rejected = false; try { loop.ReadHistory(); } catch (InvalidDataException) { rejected = true; }
                    var outcome = await loop.RunAsync(configuration);
                    Check(rejected && !outcome.Success && chat.Requests.Count == 0 && codex.Requests.Count == 0 && validation.Calls == 0
                        && !File.Exists(loop.StatePath) && !File.Exists(loop.StatePath + ".bak"), defect + " history refuses replay before every simulated provider and state write");
                    Check(primary is null || await File.ReadAllTextAsync(historyPath) == primary, defect + " preserves the invalid history bytes");
                    if (defect == "missing-primary") Check(await File.ReadAllTextAsync(historyPath + ".bak") == "[" + legacy + "]",
                        "missing-primary history preserves its backup and never falls back to replay");
                    cases.Add(name);
                }

                foreach (var defect in new[] { "primary-link", "backup-link", "dangling-primary-link", "dangling-backup-link" })
                {
                    var name = "history-" + defect; var chat = AdviceAndReview(); var codex = Execution(); var validation = new Validator();
                    var loop = Loop(name, chat, codex, validation); var directory = Path.GetDirectoryName(loop.StatePath)!;
                    Directory.CreateDirectory(directory); var historyPath = Path.Combine(directory, "history.json");
                    var target = Path.Combine(root, name + "-target.json");
                    if (!defect.StartsWith("dangling", StringComparison.Ordinal)) await File.WriteAllTextAsync(target, "[" + legacy + "]");
                    if (defect.Contains("backup", StringComparison.Ordinal)) await File.WriteAllTextAsync(historyPath, "[" + legacy + "]");
                    try { File.CreateSymbolicLink(historyPath + (defect.Contains("backup", StringComparison.Ordinal) ? ".bak" : ""), target); }
                    catch (Exception error) when (error is UnauthorizedAccessException or IOException or NotSupportedException)
                    { Console.WriteLine("  NotVerified " + name + " (Windows symbolic-link privilege unavailable)"); cases.Add(name + "-NotVerified"); continue; }
                    bool rejected = false; try { loop.ReadHistory(); } catch (InvalidDataException) { rejected = true; }
                    Check(rejected && !(await loop.RunAsync(configuration)).Success && chat.Requests.Count == 0 && codex.Requests.Count == 0
                        && validation.Calls == 0 && !File.Exists(loop.StatePath), defect + " history refuses replay without following the symbolic link");
                    Check(!File.Exists(target) || await File.ReadAllTextAsync(target) == "[" + legacy + "]", defect + " preserves the linked fixture target");
                    cases.Add(name);
                }
            }

            {
                var chat = AdviceAndReview(); var codex = Execution(); var validation = new Validator();
                var loop = Loop("permanent-completed", chat, codex, validation); var first = Config();
                for (int index = 0; index < 32; index++)
                    Check((await loop.RunAsync(index == 0 ? first : Config())).Success, "permanent completion fixture confirms simulated task " + (index + 1));
                var completedDirectory = Path.Combine(Path.GetDirectoryName(loop.StatePath)!, "completed-tasks");
                Check(!loop.ReadHistory().Any(item => item.RunId == first.TaskId) && Directory.GetFiles(completedDirectory, "*.json").Length == 32,
                    "bounded 30-entry history can evict the first task while all 32 permanent completion IDs remain");
                var calls = chat.Requests.Count + codex.Requests.Count; var stateBefore = await File.ReadAllBytesAsync(loop.StatePath);
                var markerPath = Path.Combine(completedDirectory, first.TaskId.ToString("D") + ".json"); var markerBefore = await File.ReadAllBytesAsync(markerPath);
                foreach (var old in new[] { first, first with { Goal = "同一ID但被改变的目标。", AuthorizedWritePaths = ["Other.cs"] } })
                    Check(!(await loop.RunAsync(old)).Success && chat.Requests.Count + codex.Requests.Count == calls && validation.Calls == 32,
                        "an evicted completed TaskId cannot replay even when its Goal and authorization change");
                var stateAfter = await File.ReadAllBytesAsync(loop.StatePath); var markerAfter = await File.ReadAllBytesAsync(markerPath);
                Check(stateBefore.SequenceEqual(stateAfter) && markerBefore.SequenceEqual(markerAfter),
                    "completed TaskId refusal preserves the current task and immutable completion marker bytes");
                var freshChat = AdviceAndReview(); var freshCodex = Execution(); var freshValidation = new Validator();
                var restarted = Loop("permanent-completed", freshChat, freshCodex, freshValidation);
                Check(!(await restarted.RunAsync(first)).Success && freshChat.Requests.Count == 0 && freshCodex.Requests.Count == 0 && freshValidation.Calls == 0,
                    "permanent TaskId deduplication survives a new service instance after history eviction");
                using var marker = JsonDocument.Parse(markerBefore);
                Check(marker.RootElement.EnumerateObject().Select(item => item.Name).Order().SequenceEqual(new[] { "RecordedAt", "SchemaVersion", "TaskId" }),
                    "completion markers contain only version, TaskId and recorded time, without Goal, model text or authentication data");
                cases.Add("permanent-completion-over-30");
            }

            foreach (var source in new[] { "current", "history" })
            {
                var configuration = Config(); var name = "completion-migration-" + source;
                var chat = AdviceAndReview(); var codex = Execution(); var validation = new Validator();
                var data = Path.Combine(root, name); var directory = Path.Combine(data, "workflow"); Directory.CreateDirectory(directory);
                if (source == "current") await WriteState(data, Saved(configuration) with { Phase = WorkflowLoopPhase.Completed,
                    Checkpoint = WorkflowCheckpoint.Completed, CompletedRounds = 1 });
                else await File.WriteAllTextAsync(Path.Combine(directory, "history.json"), "[{\"RunId\":\"" + configuration.TaskId.ToString("D")
                    + "\",\"Phase\":\"Completed\",\"Round\":1,\"CompletedRounds\":1}]");
                var loop = Loop(name, chat, codex, validation);
                Check(!(await loop.RunAsync(configuration with { Goal = "旧完成ID的改变目标。" })).Success
                    && File.Exists(Path.Combine(directory, "completed-tasks", configuration.TaskId.ToString("D") + ".json"))
                    && chat.Requests.Count == 0 && codex.Requests.Count == 0 && validation.Calls == 0,
                    source + " completed legacy evidence migrates to an immutable TaskId marker before any new request");
                cases.Add(name);
            }

            foreach (var defect in new[] { "empty", "null", "array", "broken-json", "wrong-task", "wrong-version", "duplicate-field", "oversize", "marker-directory", "parent-file" })
            {
                var configuration = Config(); var name = "completion-marker-" + defect;
                var chat = AdviceAndReview(); var codex = Execution(); var validation = new Validator();
                var loop = Loop(name, chat, codex, validation); var directory = Path.GetDirectoryName(loop.StatePath)!;
                Directory.CreateDirectory(directory); var completionDirectory = Path.Combine(directory, "completed-tasks");
                var markerPath = Path.Combine(completionDirectory, configuration.TaskId.ToString("D") + ".json");
                var valid = JsonSerializer.Serialize(new { SchemaVersion = 1, TaskId = configuration.TaskId, RecordedAt = DateTimeOffset.UtcNow });
                string? content = defect switch { "empty" => "", "null" => "null", "array" => "[]", "broken-json" => "{",
                    "wrong-task" => valid.Replace(configuration.TaskId.ToString("D"), Guid.NewGuid().ToString("D")),
                    "wrong-version" => valid.Replace("\"SchemaVersion\":1", "\"SchemaVersion\":2"),
                    "duplicate-field" => valid[..^1] + ",\"TaskId\":\"" + configuration.TaskId.ToString("D") + "\"}",
                    "oversize" => new string(' ', 2049), _ => null };
                if (defect == "parent-file") await File.WriteAllTextAsync(completionDirectory, "preserved fixture file");
                else
                {
                    Directory.CreateDirectory(completionDirectory);
                    if (defect == "marker-directory") Directory.CreateDirectory(markerPath);
                    else await File.WriteAllTextAsync(markerPath, content!);
                }
                Check(!(await loop.RunAsync(configuration)).Success && chat.Requests.Count == 0 && codex.Requests.Count == 0
                    && validation.Calls == 0 && !File.Exists(loop.StatePath), defect + " permanent marker blocks before model dispatch or state writes");
                Check(content is null || await File.ReadAllTextAsync(markerPath) == content, defect + " invalid permanent marker is never overwritten or treated as absent");
                cases.Add(name);
            }

            {
                var configuration = Config(); var chat = AdviceAndReview(); var codex = Execution(); FileStream? lockedMarker = null;
                var name = "completion-write-failure"; var markerPath = Path.Combine(root, name, "workflow", "completed-tasks", configuration.TaskId.ToString("D") + ".json");
                var validation = new Validator(() => lockedMarker ??= new FileStream(markerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None));
                var loop = Loop(name, chat, codex, validation);
                try
                {
                    var outcome = await loop.RunAsync(configuration);
                    Check(!outcome.Success && loop.State.Phase == WorkflowLoopPhase.Blocked && loop.State.CompletedRounds == 1
                        && chat.Requests.Count == 2 && codex.Requests.Count == 1 && validation.Calls == 1,
                        "a completion-marker write failure preserves the confirmed round but never claims Completed");
                }
                finally { lockedMarker?.Dispose(); }
                var calls = chat.Requests.Count + codex.Requests.Count;
                Check(loop.AcknowledgeRecovery().Success && !(await loop.RunAsync(configuration)).Success
                    && chat.Requests.Count + codex.Requests.Count == calls && validation.Calls == 1 && new FileInfo(markerPath).Length == 0,
                    "a torn completion marker survives explicit recovery and prevents every new model request");
                cases.Add(name);
            }

            foreach (var invalid in new[] { "null", "[]", "7" })
            {
                var name = "state-root-" + cases.Count; var path = Path.Combine(root, name, "workflow", "loop-state.json");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllTextAsync(path, invalid);
                bool rejected = false; try { Loop(name, AdviceAndReview(), Execution(), new Validator()); } catch (InvalidDataException) { rejected = true; }
                Check(rejected && await File.ReadAllTextAsync(path) == invalid, "a non-object persisted state is rejected as InvalidData without changing its bytes");
                cases.Add(name);
            }

            await WriteResult(true, null); Console.WriteLine($"PASS workflow-stability-check: {assertions} assertions; {cases.Count} fault fixtures; simulated providers; real D I/O; no model/network/process/system actions");
            return assertions;
        }
        catch (Exception error) { await WriteResult(false, error.Message); throw; }

        Task WriteResult(bool passed, string? error) => File.WriteAllTextAsync(Path.Combine(root, "check-result.json"), JsonSerializer.Serialize(new {
            passed, error, assertions, cases, fixtureRoot = root, simulatedModel = true, realDDriveIO = true,
            realModelCalls = false, realNetworkActions = false, realProcessActions = false, realSystemActions = false,
            productionStateTouched = false, testedAt = DateTimeOffset.UtcNow
        }, Json));
    }

    static bool IsAdvice(WorkflowExecutionRequest request) => request.Goal.Contains("首个非空行必须为 ACC_ADVICE:", StringComparison.Ordinal);
    static WorkflowExecutionResult Reply(WorkflowExecutionRequest request, string text, string? session = null)
        => WorkflowTestProtocol.Reply(request, new(true, text, session));
    static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    static WorkflowLoopState Saved(WorkflowLoopConfiguration configuration) => new() { RunId = configuration.TaskId, Round = 1,
        Phase = WorkflowLoopPhase.Blocked, UpdatedAt = DateTimeOffset.UtcNow, ProjectRoot = Path.GetFullPath(configuration.ProjectRoot).TrimEnd('\\'),
        Goal = configuration.Goal, ChatGptThreadId = configuration.ChatGptThreadId, CallerThreadId = configuration.CallerThreadId,
        MaximumRounds = configuration.MaximumRounds, AuthorizedWritePaths = configuration.AuthorizedWritePaths.ToArray(),
        AuthorizationId = WorkflowAuthorization.CreateAuthorizationId(configuration.TaskId, configuration.ProjectRoot, configuration.Goal,
            configuration.AuthorizedWritePaths, false), BuildProjectPath = configuration.BuildProjectPath, ChecksProjectPath = configuration.ChecksProjectPath };
    static WorkflowExecutionRequest Request(WorkflowLoopState saved, WorkflowProvider provider) => new(saved.CurrentRequestId, provider, "") {
        WorkflowTaskId = saved.RunId, AuthorizedProjectRoot = saved.ProjectRoot, AuthorizedWritePaths = saved.AuthorizedWritePaths,
        StepWritePaths = saved.StepWritePaths, AuthorizationId = saved.AuthorizationId, ExpectedSessionId = provider == WorkflowProvider.ChatGPT ? saved.ChatGptThreadId : saved.CodexSessionId };
    static async Task WriteState(string data, WorkflowLoopState state)
    {
        var path = Path.Combine(data, "workflow", "loop-state.json"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(state, Json));
    }
    static async Task WriteJournal(string data, WorkflowLoopState state, WorkflowExecutionResult result)
    {
        var path = Path.Combine(data, "workflow", "results", state.RunId.ToString("D"), state.CurrentRequestId.ToString("D") + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { SchemaVersion = 1, TaskId = state.RunId, RequestId = state.CurrentRequestId,
            Round = state.Round, Checkpoint = state.Checkpoint, Provider = state.PendingProvider, ProjectRoot = state.ProjectRoot,
            AuthorizationId = state.AuthorizationId, Result = result }, Json));
    }
    sealed class Fake(string id, Func<WorkflowExecutionRequest, int, CancellationToken, Task<WorkflowExecutionResult>> script) : IWorkflowConnector
    {
        public string Id => id;
        public WorkflowConnectionState ConnectionState => WorkflowConnectionState.Ready;
        public bool CanExecute => true;
        public List<WorkflowExecutionRequest> Requests { get; } = [];
        public Task<WorkflowExecutionResult> ExecuteAsync(WorkflowExecutionRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request with { AuthorizedWritePaths = request.AuthorizedWritePaths.ToArray(), StepWritePaths = request.StepWritePaths.ToArray() });
            return script(request, Requests.Count, cancellationToken);
        }
        public Task<SafeStopResult> RequestSafeStopAsync(Guid taskId, CancellationToken cancellationToken = default)
            => Task.FromResult(new SafeStopResult(true, "simulated safe stop only; no real process action"));
    }
    sealed class Validator(Action? beforeReturn = null) : IWorkflowProjectValidator
    {
        public int Calls { get; private set; }
        public Task<WorkflowValidationResult> ValidateAsync(string projectRoot, CancellationToken cancellationToken)
        { Calls++; cancellationToken.ThrowIfCancellationRequested(); beforeReturn?.Invoke(); return Task.FromResult(new WorkflowValidationResult(true, "固定模拟验证通过，未执行项目或进程。")); }
    }
}

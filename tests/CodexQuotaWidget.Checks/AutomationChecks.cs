using System.Text.Json;
using CodexQuotaWidget.Core;
using CodexQuotaWidget.Core.Automation;

public static class AutomationChecks
{
    public static Task<int> RunAsync()
    {
        const string fixtures = @"D:\CodexQuotaWidget-GitHub\artifacts\automation-checks";
        var root = Path.Combine(fixtures, Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "project");
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(project);
        var passed = 0;
        void Check(bool condition, string scenario)
        {
            if (!condition) throw new InvalidOperationException("Automation: " + scenario);
            passed++;
        }
        try
        {
            var parsed = ActionPlanParser.Parse("关闭Clash和mihomo，然后关机");
            Check(parsed.Success && parsed.Plan is not null, "natural Chinese sample parses to a typed plan");
            var plan = parsed.Plan!;
            Check(plan.Actions[0].Kind == LocalActionKind.CreateRecoverySnapshot && plan.Actions[1].Kind == LocalActionKind.SaveResumeState,
                "recovery always precedes all process actions");
            Check(plan.Actions.Last().Kind == LocalActionKind.ShutdownWindows && plan.Actions.Count(action => action.Kind == LocalActionKind.ShutdownWindows) == 1,
                "one final normal-shutdown step");
            Check(plan.Actions.All(action => Enum.IsDefined(action.Kind) && Enum.IsDefined(action.Target)), "only explicit whitelist enum values");
            Check(plan.Actions.Any(action => action.Kind == LocalActionKind.StopWindowsService && action.Target == LocalTarget.ClashService), "mihomo identifies only the allowed service");
            Check(plan.Actions.Any(action => action.Kind == LocalActionKind.VerifyServiceStopped && action.Target == LocalTarget.ClashService)
                && plan.Actions.Any(action => action.Kind == LocalActionKind.VerifyProcessStopped && action.Target == LocalTarget.Mihomo), "service and process verification are explicit");
            Check(plan.Actions.FindIndex(action => action.Target == LocalTarget.ClashVerge) > plan.Actions.FindIndex(action => action.Target == LocalTarget.CodexCli), "user actions follow mandatory local recovery and Codex steps");
            Check(ActionPlanParser.Validate(plan).Success, "canonical plan validates");
            Check(ActionPlanParser.Fingerprint(plan) == ActionPlanParser.Fingerprint(plan with { CreatedAt = plan.CreatedAt.AddHours(1) }), "plan identity does not depend on parse time");
            Check(!ActionPlanParser.Validate(plan with { Actions = [new(LocalActionKind.ShutdownWindows)] }).Success, "tampered steps cannot bypass recovery or process verification");
            Check(!ActionPlanParser.Validate(plan with { Actions = [new((LocalActionKind)300)] }).Success, "unknown enum steps cannot execute");
            Check(!ActionPlanParser.Validate(plan with { Text = "睡眠" }).Success, "changed user text requires reparse and save");
            Check(!ActionPlanParser.Validate(null).Success && !ActionPlanParser.Validate(plan with { SchemaVersion = 2 }).Success, "unknown plan schemas are rejected");
            foreach (var unsupportedText in new[] { "", "随便处理一下", "关闭Chrome然后关机", "关闭Clash并删除配置然后关机", "powershell shutdown /s", "关机 /f", "清空回收站", "修改代理", "停止任意服务", "关闭Clash然后执行cmd", "关机然后等待1秒", "关机然后重启", "等待0秒然后关机", "等待121秒然后关机" })
                Check(!ActionPlanParser.Parse(unsupportedText).Success, "unsupported input fails complete parsing: " + unsupportedText);
            Check(!ActionPlanParser.Parse(new string('a', 2049)).Success, "text size is bounded");
            Check(ActionPlanParser.Parse("等待2秒\n保存恢复记录\n休眠").Success, "multiline wait and hibernate parse");
            Check(ActionPlanParser.Parse("重启").Plan?.Actions.Last().Kind == LocalActionKind.RestartWindows, "restart is an explicit typed final power action");
            Check(ActionPlanParser.Parse("睡眠").Plan?.Actions.Last().Kind == LocalActionKind.SleepWindows, "sleep is an explicit typed final power action");
            Check(ActionPlanParser.Parse("关闭Clash").Success && !ActionPlanParser.Parse("关闭Clash").Plan!.Actions.Any(action => action.Kind == LocalActionKind.ShutdownWindows), "power action is optional and never inferred");
            foreach (var editor in new[] { LocalTarget.VisualStudio, LocalTarget.VSCode, LocalTarget.Rider })
            {
                var at = plan.Actions.FindIndex(action => action.Kind == LocalActionKind.CloseApplicationGracefully && action.Target == editor);
                Check(at > 1 && plan.Actions[at + 1].Kind == LocalActionKind.Wait
                    && plan.Actions[at + 2] == new LocalAction(LocalActionKind.VerifyProcessStopped, editor), "editor graceful close/wait/verify remain adjacent: " + editor);
                Check(!plan.Actions.Any(action => action.Target == editor && action.Kind == LocalActionKind.CloseProcess), "editors with unsaved changes are never forced: " + editor);
                Check(at < plan.Actions.FindIndex(action => action.Target == LocalTarget.CodexDesktop), "editor save prompts can block before closing Codex: " + editor);
            }
            var repeated = ActionPlanParser.Parse("关闭Visual Studio和Codex\n关闭VSCode\n关闭Rider\n关机").Plan!;
            var simple = ActionPlanParser.Parse("关机").Plan!;
            Check(repeated.Actions.SequenceEqual(simple.Actions), "explicit repeated mandatory targets do not add or reorder close/wait groups");
            Check(ActionPlanParser.Parse("等待2秒然后关机").Plan!.Actions.Count(action => action.Kind == LocalActionKind.Wait && action.Seconds == 2) == 1, "an independent user wait is preserved");
            var verifyThenClose = ActionPlanParser.Parse("确认mihomo已停止，然后关闭mihomo，然后关机").Plan!;
            Check(verifyThenClose.Actions.FindLastIndex(action => action.Kind == LocalActionKind.VerifyProcessStopped && action.Target == LocalTarget.Mihomo)
                > verifyThenClose.Actions.FindIndex(action => action.Kind == LocalActionKind.CloseProcess && action.Target == LocalTarget.Mihomo),
                "an earlier verification cannot remove the verification after a close group");
            Check(ActionPlanParser.Parse("关闭mihomo然后停止Clash服务然后关机").Plan!.Actions.Count(action => action.Kind == LocalActionKind.StopWindowsService) == 1,
                "aliases for the same service/process closure are grouped once");

            var now = new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
            var reset = now.AddHours(4).ToUnixTimeSeconds();
            QuotaSnapshot Quota(double? remaining, int seconds, long? resetAt = null) => new(now.AddSeconds(seconds), "plus",
                [new("codex", "Codex", "primary", remaining is { } value ? 100 - value : null, 300, resetAt ?? reset)], false, null, []);
            ExhaustionReceipt? Observe(ExhaustionDetector detector, double? remaining, int seconds, MonitorStatus status = MonitorStatus.Live, bool stale = false)
                => detector.Observe(Quota(remaining, seconds), status, stale, now.AddSeconds(seconds));

            var detector = new ExhaustionDetector();
            Check(Observe(detector, 0, 0) is null && Observe(detector, 0, 1) is null && Observe(detector, 0, 2) is null, "startup at persistent zero never triggers");
            Check(Observe(detector, 3, 3) is null && Observe(detector, 1, 4) is null && Observe(detector, .001, 5) is null, "3 percent and all positive capacity never trigger power");
            Check(Observe(detector, 0, 6) is null, "first genuine zero requires independent confirmation");
            Check(Observe(detector, 0, 6) is null, "rechecking cached zero is not independent confirmation");
            var receipt = Observe(detector, 0, 7);
            Check(receipt is not null && receipt.RemainingPercent == 0 && receipt.CycleKey == ExhaustionDetector.CycleKey(reset), "second distinct zero yields one exhaustion receipt");
            var verifiedReceipt = receipt ?? throw new InvalidOperationException("Expected a confirmed exhaustion receipt");
            Check(Observe(detector, 0, 8) is null && Observe(detector, 20, 9) is null && Observe(detector, 0, 10) is null && Observe(detector, 0, 11) is null,
                "one detector never retries the same handled cycle even after an apparent positive reading");
            Check(ExhaustionDetector.ValidateReceipt(receipt, now.AddSeconds(8)), "valid live receipt is locally verifiable");
            Check(!ExhaustionDetector.ValidateReceipt(receipt! with { RemainingPercent = 3 }, now.AddSeconds(8)), "legacy 3-percent receipt cannot authorize execution");
            Check(!ExhaustionDetector.ValidateReceipt(receipt! with { Source = "RpcError" }, now.AddSeconds(8)), "generic RPC errors cannot authorize execution");
            Check(!ExhaustionDetector.ValidateReceipt(receipt! with { PositiveObservedAt = receipt!.FirstZeroObservedAt }, now.AddSeconds(8)), "receipt must prove a positive-to-zero transition");
            Check(!ExhaustionDetector.ValidateReceipt(receipt! with { FirstZeroObservedAt = receipt!.SecondZeroObservedAt }, now.AddSeconds(8)), "receipt must prove separate zero observations");
            Check(!ExhaustionDetector.ValidateReceipt(receipt, now.AddMinutes(16)), "stale exhaustion receipts cannot authorize execution");
            Check(!ExhaustionDetector.ValidateReceipt(receipt! with { LimitId = "other", CycleKey = "other:300:123" }, now.AddSeconds(8)), "receipt must identify actual Codex five-hour window");

            foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, -1d, 101d })
            {
                var invalidDetector = new ExhaustionDetector();
                Observe(invalidDetector, 1, 0);
                Check(Observe(invalidDetector, invalid, 1) is null && Observe(invalidDetector, invalid, 2) is null, "invalid numeric quotas cannot trigger: " + invalid);
            }
            var disrupted = new ExhaustionDetector();
            Observe(disrupted, 1, 0); Observe(disrupted, 0, 1);
            Check(Observe(disrupted, 0, 2, MonitorStatus.Offline) is null, "offline does not trigger");
            Check(Observe(disrupted, 0, 3) is null && Observe(disrupted, 0, 4) is not null, "offline interruption requires two new live zero confirmations");
            var unknown = new ExhaustionDetector(); Observe(unknown, 2, 0);
            Check(Observe(unknown, null, 1) is null && Observe(unknown, 0, 2, stale: true) is null, "unknown or stale quota never triggers");
            Check(unknown.Observe(Quota(0, -181), MonitorStatus.Live, false, now) is null, "old cache does not trigger");
            Check(unknown.Observe(Quota(0, 10), MonitorStatus.Live, false, now) is null, "future observations are rejected");
            Check(unknown.Observe(Quota(0, 1, now.ToUnixTimeSeconds()), MonitorStatus.Live, false, now.AddSeconds(1)) is null, "passed reset cannot trigger");
            var missingReset = Quota(0, 2) with { Windows = [new("codex", "Codex", "primary", 100, 300, null)] };
            Check(unknown.Observe(missingReset, MonitorStatus.Live, false, now.AddSeconds(2)) is null, "missing cycle reset cannot trigger");
            var mixed = Quota(0, 3) with { Windows = [new("codex", "Codex", "primary", 100, 300, reset), new("codex", "Codex", "primary", 50, 300, reset)] };
            Check(unknown.Observe(mixed, MonitorStatus.Live, false, now.AddSeconds(3)) is null, "conflicting duplicate quota observations are rejected");
            var weekly = Quota(0, 4) with { Windows = [new("codex", "Codex", "secondary", 100, 10080, reset)] };
            Check(unknown.Observe(weekly, MonitorStatus.Live, false, now.AddSeconds(4)) is null, "weekly exhaustion alone never triggers this feature");
            var restarted = new ExhaustionDetector([ExhaustionDetector.CycleKey(reset)]);
            Check(Observe(restarted, 10, 0) is null && Observe(restarted, 0, 1) is null && Observe(restarted, 0, 2) is null, "persisted handled cycle prevents reboot replay");
            var newCycle = new ExhaustionDetector(); Observe(newCycle, 20, 0);
            var laterReset = reset + 300;
            Check(newCycle.Observe(Quota(0, 1, laterReset), MonitorStatus.Live, false, now.AddSeconds(1)) is null
                && newCycle.Observe(Quota(0, 2, laterReset), MonitorStatus.Live, false, now.AddSeconds(2)) is null, "a changed reset cycle cannot reuse old positive observation");
            newCycle.Observe(Quota(10, 3, laterReset), MonitorStatus.Live, false, now.AddSeconds(3));
            Check(newCycle.Observe(Quota(0, 4, laterReset), MonitorStatus.Live, false, now.AddSeconds(4)) is null
                && newCycle.Observe(Quota(0, 5, laterReset), MonitorStatus.Live, false, now.AddSeconds(5)) is not null, "new cycle works after its own positive and zero transition");

            var store = new AutomationStore(data);
            Check(store.LoadConfiguration().Success && !store.LoadConfiguration().Configuration.Enabled, "new install starts disabled");
            var configuration = new AutomationConfiguration { Enabled = true, Text = plan.Text, Plan = plan, ProjectRoot = project };
            Check(store.SaveConfiguration(configuration, out _), "valid typed configuration persists locally");
            Check(store.LoadConfiguration().Configuration.Enabled && store.LoadConfiguration().Configuration.Plan!.Actions.SequenceEqual(plan.Actions), "typed plan restores from primary");
            Check(!store.SaveConfiguration(configuration with { ProjectRoot = @"C:\Users\FixtureUser" }, out _), "automation does not add recovery data to C drive");
            Check(!store.SaveConfiguration(configuration with { Text = "关机" }, out _), "cannot persist mismatched text and actions");
            Check(!store.SaveConfiguration(configuration with { Plan = null }, out _), "cannot enable with missing plan");
            Check(!store.SaveConfiguration(configuration with { Text = null! }, out _), "null configuration input fails safely");
            Check(store.SaveConfiguration(configuration with { Enabled = false }, out _), "disabled replacement config persists");
            File.WriteAllText(store.ConfigurationPath, "{broken");
            Check(!store.LoadConfiguration().Success && !store.LoadConfiguration().Configuration.Enabled, "corrupt primary does not fall back to enabled backup");
            Check(!AutomationStore.IsSafeDPath(@"D:\foo:secret") && !AutomationStore.IsSafeDPath(@"\\server\share\data") && !AutomationStore.IsSafeDPath("relative"), "ADS, UNC and relative data paths are rejected");
            Check(store.LoadHandledCycles().Count == 0, "new handled-cycle store is empty");
            store.MarkCycleHandled(verifiedReceipt.CycleKey); store.MarkCycleHandled(verifiedReceipt.CycleKey);
            Check(store.LoadHandledCycles().Count == 1 && store.LoadHandledCycles()[0] == verifiedReceipt.CycleKey, "handled cycle persists idempotently");
            var boundReceipt = verifiedReceipt with { PlanFingerprint = ActionPlanParser.Fingerprint(plan) };
            store.SavePendingReceipt(boundReceipt);
            Check(store.ReadPendingReceipt()?.EventId == verifiedReceipt.EventId && store.ReadPendingReceipt()?.PlanFingerprint == ActionPlanParser.Fingerprint(plan), "pending receipt binds event and exact plan");
            store.SavePendingReceipt(boundReceipt with { EventId = Guid.NewGuid() });
            File.WriteAllText(store.PendingReceiptPath, "{broken");
            Check(store.ReadPendingReceipt() is null, "corrupt pending primary never revives earlier backup event");
            File.WriteAllText(store.HandledCyclesPath, JsonSerializer.Serialize(new { SchemaVersion = 1, Cycles = new[] { "evil" } }));
            var corruptedCyclesRejected = false;
            try { store.LoadHandledCycles(); } catch (InvalidDataException) { corruptedCyclesRejected = true; }
            Check(corruptedCyclesRejected, "corrupt replay protection fails closed rather than allowing an old cycle");
            return Task.FromResult(passed);
        }
        finally
        {
            if (Path.GetFullPath(root).StartsWith(fixtures + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                Directory.Delete(root, true);
        }
    }
}

using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CodexQuotaWidget.Core;
using CodexQuotaWidget.Core.Automation;
using CodexQuotaWidget.Core.ControlCenter;

namespace CodexQuotaWidget;

/// <summary>Explicit product acceptance entry. The widget's connectors own every model call.</summary>
internal static class WorkflowArithmeticCheckRunner
{
    public static async Task<int> RunAsync(string[] args)
    {
        var index = Array.IndexOf(args, "--workflow-config");
        var authIndex = Array.IndexOf(args, "--codex-auth-home");
        if (index < 0 || index + 1 >= args.Length || authIndex < 0 || authIndex + 1 >= args.Length
            || !AutomationStore.IsSafeDPath(args[index + 1]) || !AutomationStore.IsSafeDPath(args[authIndex + 1])) return 2;
        if (Path.GetFullPath(Paths.Data).TrimEnd('\\').Equals(@"D:\CodexQuotaWidgetData", StringComparison.OrdinalIgnoreCase))
        { Console.WriteLine("算术验收必须指定独立的D盘数据目录，原任务未改动。"); return 2; }
        try
        {
            if (!File.Exists(args[index + 1]) || new FileInfo(args[index + 1]).Length > 24000) return 2;
            var config = JsonSerializer.Deserialize<WorkflowLoopConfiguration>(File.ReadAllText(args[index + 1]));
            if (config is null || !WorkflowLoopService.ValidConfiguration(config)) return 2;
            Directory.CreateDirectory(Paths.Data);
            await using var chat = new DesktopChatGptWorkflowConnector(config.ChatGptThreadId, config.CallerThreadId, Paths.Data, TimeSpan.FromMinutes(3));
            await using var codex = new CodexWorkflowConnector(config.ProjectRoot, Paths.Data, authenticationHome: args[authIndex + 1]);
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(12));
            var gptProbe = chat.ConnectAsync(deadline.Token);
            var codexProbe = codex.ConnectAsync(deadline.Token);
            var gptReady = await gptProbe; var codexReady = await codexProbe;
            var connections = new { GptReady = gptReady, GptCode = chat.DiagnosticCode,
                CodexReady = codexReady.Success, CodexCode = codex.DiagnosticCode,
                Model = codex.SelectedModel, ModelRequests = 0 };
            Console.WriteLine(JsonSerializer.Serialize(connections));
            File.WriteAllText(Path.Combine(Paths.Data, "arithmetic-connections.json"), JsonSerializer.Serialize(connections));
            if (!gptReady || !codexReady.Success) return 1;
            var guardReader = new ControlCenterService(Path.Combine(Paths.Data, "workflow", "arithmetic-quota-read-model.json"));
            async Task<QuotaGuardSnapshot> ReadQuota(CancellationToken token)
            {
                try
                {
                    var quota = await chat.ReadQuotaAsync(token);
                    guardReader.UpdateQuota(quota, MonitorStatus.Live, false);
                    return guardReader.Snapshot.Guard;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { return new(QuotaGuardState.Unknown, null, null, false, false, "真实额度读取失败，禁止发送。"); }
            }
            var relay = new WorkflowArithmeticRelayService(Paths.Data, chat, codex, ReadQuota,
                config.ProjectRoot, config.ChatGptThreadId, config.CallerThreadId);
            relay.Changed += () => Console.WriteLine(JsonSerializer.Serialize(new { relay.State.Phase,
                ConfirmedSteps = relay.State.Evidence.Length, relay.State.PendingRequestId, relay.State.Message }));
            var result = await relay.RunAsync(deadline.Token);
            var report = new { ProductOwned = true, Simulated = false, result.Success, result.Message,
                State = relay.State, GptCode = chat.DiagnosticCode, GptCompletedTurn = chat.LastCompletedTurnId,
                GptSourceMessage = chat.LastReplySourceMessageId, CodexThread = codex.LastThreadId,
                CodexTurn = codex.LastTurnId, CodexRunState = codex.LastRunState };
            var output = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(Paths.Data, "arithmetic-result.json"), output);
            Console.WriteLine(output);
            return result.Success ? 0 : 1;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException
            or JsonException or ArgumentException or InvalidOperationException or OperationCanceledException)
        { Console.WriteLine("小组件算术验收未完成：" + error.GetType().Name + "；未替换原任务状态。"); return 1; }
    }
}

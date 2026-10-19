using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CodexQuotaWidget.Core;
using CodexQuotaWidget.Core.Automation;
using CodexQuotaWidget.Core.ControlCenter;

namespace CodexQuotaWidget;

/// <summary>The widget schedules Codex. Only Codex-invoked tools may send or read GPT replies.</summary>
internal static class CodexLedArithmeticCheckRunner
{
    public static async Task<int> RunAsync(string[] args)
    {
        var index = Array.IndexOf(args, "--workflow-config");
        var authIndex = Array.IndexOf(args, "--codex-auth-home");
        if (index < 0 || index + 1 >= args.Length || authIndex < 0 || authIndex + 1 >= args.Length
            || !AutomationStore.IsSafeDPath(args[index + 1]) || !AutomationStore.IsSafeDPath(args[authIndex + 1])) return 2;
        if (Path.GetFullPath(Paths.Data).TrimEnd('\\').Equals(@"D:\CodexQuotaWidgetData", StringComparison.OrdinalIgnoreCase))
        { Console.WriteLine("Codex调度验收必须使用独立D盘数据目录，原任务未改动。"); return 2; }
        try
        {
            if (!File.Exists(args[index + 1]) || new FileInfo(args[index + 1]).Length > 24000) return 2;
            var config = JsonSerializer.Deserialize<WorkflowLoopConfiguration>(File.ReadAllText(args[index + 1]));
            if (config is null || !WorkflowLoopService.ValidConfiguration(config)) return 2;
            Directory.CreateDirectory(Paths.Data);
            await using var chat = new DesktopChatGptWorkflowConnector(config.ChatGptThreadId, config.CallerThreadId, Paths.Data);
            var bridge = new CodexGptRelayBridge(chat);
            await using var codex = new CodexWorkflowConnector(config.ProjectRoot, Paths.Data,
                authenticationHome: args[authIndex + 1], relayTools: bridge);
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(12));
            var gptProbe = chat.ConnectAsync(deadline.Token);
            var codexProbe = codex.ConnectAsync(deadline.Token);
            var gptReady = await gptProbe; var codexReady = await codexProbe;
            var connections = new { GptReady = gptReady, GptCode = chat.DiagnosticCode,
                CodexReady = codexReady.Success, CodexCode = codex.DiagnosticCode,
                Model = codex.SelectedModel, ModelRequests = 0, Mode = "Widget schedules Codex-owned send/read tools" };
            Console.WriteLine(JsonSerializer.Serialize(connections));
            File.WriteAllText(Path.Combine(Paths.Data, "codex-led-connections.json"), JsonSerializer.Serialize(connections));
            if (!gptReady || !codexReady.Success) return 1;
            var guardReader = new ControlCenterService(Path.Combine(Paths.Data, "workflow", "codex-led-quota.json"));
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
            var service = new CodexLedArithmeticService(Paths.Data, codex, bridge, ReadQuota,
                config.ProjectRoot, config.ChatGptThreadId, config.CallerThreadId);
            service.Changed += () => Console.WriteLine(JsonSerializer.Serialize(service.State));
            var result = await service.RunAsync(deadline.Token);
            var report = new { ProductOwned = true, RootAgentRelayed = false, Simulated = false,
                result.Success, result.Message, State = service.State, ToolCalls = bridge.Evidence,
                CodexThread = codex.LastThreadId, CodexTurn = codex.LastTurnId,
                CodexRunState = codex.LastRunState, GptStatus = chat.CurrentThreadStatus };
            var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(Paths.Data, "codex-led-result.json"), json);
            Console.WriteLine(json);
            return result.Success ? 0 : 1;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException
            or JsonException or ArgumentException or InvalidOperationException or OperationCanceledException)
        { Console.WriteLine("Codex调度验收未完成：" + error.GetType().Name + "；保留请求，不替换原任务。"); return 1; }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexQuotaWidget.Core;
using CodexQuotaWidget.Core.ControlCenter;

namespace CodexQuotaWidget;

/// <summary>Real WPF and isolated product runtime; all account/network/inference entry points remain disabled.</summary>
internal static class SiwcUiChecks
{
    public static async Task<int> RunAsync(Window owner)
    {
        int checks = 0; void Check(bool ok, string text) { if (!ok) throw new InvalidOperationException("SIWC UI: " + text); checks++; }
        var directory = @"D:\CodexQuotaWidget-GitHub\artifacts\siwc-phase1-20261016\ui-" + Guid.NewGuid().ToString("N"); Directory.CreateDirectory(directory);
        var config = new WorkflowLoopConfiguration { ProjectRoot = directory, ChatGptThreadId = Guid.NewGuid().ToString(), CallerThreadId = Guid.NewGuid().ToString() };
        WorkflowLoopConfiguration draft;
        var dialog = new TaskEditorDialog(config, true) { Owner = owner, ShowActivated = false };
        try
        {
            dialog.Show(); await dialog.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var backend = Find<ComboBox>(dialog, "WorkflowTaskBackend");
            Check(backend.SelectedIndex == 0 && backend.IsEnabled, "new task defaults to Legacy Desktop");
            Find<TextBox>(dialog, "WorkflowTaskGoal").Text = "Offline experimental draft";
            Find<TextBox>(dialog, "WorkflowTaskWritePaths").Text = "fixture.txt";
            Find<TextBox>(dialog, "WorkflowTaskBuildProject").Text = "Fixture.csproj";
            Find<TextBox>(dialog, "WorkflowTaskChecksProject").Text = "Checks.csproj";
            backend.SelectedIndex = 1; Find<Expander>(dialog, "WorkflowTaskAdvanced").IsExpanded = true;
            Check(!dialog.TrySaveDraft(), "experimental mode requires explicit opt-in");
            Find<CheckBox>(dialog, "WorkflowTaskSiwcEnabled").IsChecked = true;
            Check(!Find<Button>(dialog, "WorkflowTaskSiwcLogin").IsEnabled, "real login button hard disabled");
            Check(dialog.TrySaveDraft() && dialog.Result is not null, "unbound experimental draft can be saved"); draft = dialog.Result!;
            Check(draft.ChatGptThreadId == "" && draft.CallerThreadId == "" && draft.GptBackend == WorkflowBackend.SiwcGptReviewer, "no Desktop session reused");
            Check(draft.Siwc.AdviceSessionId != draft.Siwc.ReviewSessionId && !draft.LocalExecutionAuthorized, "distinct role sessions, no execution authorization");
            Check(WorkflowLoopService.ValidDraftConfiguration(draft with { LocalExecutionAuthorized = true })
                && !WorkflowLoopService.ValidConfiguration(draft with { LocalExecutionAuthorized = true }), "saving a draft grants no runtime identity");
            dialog.UpdateLayout();
            var image = new RenderTargetBitmap((int)Math.Ceiling(dialog.ActualWidth), (int)Math.Ceiling(dialog.ActualHeight), 96, 96, PixelFormats.Pbgra32); image.Render(dialog);
            using var file = File.Create(Path.Combine(directory, "experimental-task-editor.png")); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); encoder.Save(file);
        }
        finally { dialog.Close(); }
        var edit = new TaskEditorDialog(draft, false) { Owner = owner, ShowActivated = false };
        try
        {
            edit.Show(); Check(!Find<ComboBox>(edit, "WorkflowTaskBackend").IsEnabled, "existing task backend locked");
            Check(edit.TrySaveDraft() && edit.Result!.Siwc.AdviceSessionId == draft.Siwc.AdviceSessionId, "editing preserves role sessions");
            Find<ComboBox>(edit, "WorkflowTaskBackend").SelectedIndex = 0;
            Check(!edit.TrySaveDraft(), "programmatic backend change rejected too");
        }
        finally { edit.Close(); }
        var data = Path.Combine(directory, "runtime");
        await using (var runtime = new WorkflowLoopRuntime(data))
        {
            var saved = await runtime.SaveTaskAsync(draft); Check(saved.Contains("已保存"), "product runtime persists explicit draft only");
            Check(runtime.Configuration.GptBackend == WorkflowBackend.SiwcGptReviewer && !runtime.Configuration.LocalExecutionAuthorized, "runtime selection persisted, dispatch authorization stripped");
            Check((await runtime.ReconnectGptAsync()).Contains("未发起授权"), "reconnect cannot initiate account authorization");
            Check((await runtime.StartAsync(draft with { LocalExecutionAuthorized = true })).Contains("尚未批准"), "start hard blocked before network");
            Check(runtime.GptConnection.State != WorkflowLinkState.Connected && runtime.GptConnection.DiagnosticCode == "siwc_phase1_live_disabled", "no fake connected state");
            Check(runtime.CodexConnection.State == WorkflowLinkState.Disconnected && runtime.GptCandidates.Count == 0, "no fallback Desktop candidate or executor started");
            Check(!Directory.Exists(Path.Combine(data, "siwc", "credentials")), "UI flow never creates or reads credentials");
        }
        await using (var restored = new WorkflowLoopRuntime(data))
        { Check(restored.Configuration.Siwc == draft.Siwc && restored.Configuration.GptBackend == draft.GptBackend, "draft backend restored without executing"); }
        var legacyData = Path.Combine(directory, "legacy-pending"); Directory.CreateDirectory(Path.Combine(legacyData, "workflow"));
        var legacyConfig = config with { TaskId = Guid.NewGuid(), Goal = "Original blocked task", AuthorizedWritePaths = ["original.txt"] };
        AtomicStore.Write(Path.Combine(legacyData, "workflow", "settings.json"), legacyConfig);
        var statePath = Path.Combine(legacyData, "workflow", "loop-state.json");
        AtomicStore.Write(statePath, new WorkflowLoopState { RunId = legacyConfig.TaskId, Phase = WorkflowLoopPhase.Blocked, Checkpoint = WorkflowCheckpoint.Advice,
            CurrentRequestId = Guid.NewGuid(), PendingProvider = WorkflowProvider.ChatGPT, NeedsRecoveryConfirmation = true, Round = 1, UpdatedAt = DateTimeOffset.UtcNow });
        var before = File.ReadAllBytes(statePath);
        await using (var preserved = new WorkflowLoopRuntime(legacyData))
        {
            Check(preserved.State.GptBackend == WorkflowBackend.LegacyDesktop && preserved.State.NeedsRecoveryConfirmation, "legacy pending stays legacy and blocked");
            Check(!(await preserved.SaveTaskAsync(draft)).Contains("已保存"), "unfinished old task cannot be replaced by experimental draft");
        }
        Check(before.SequenceEqual(File.ReadAllBytes(statePath)), "legacy pending bytes preserved");
        File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new { Passed = true, Checks = checks, RealWpf = true,
            IsolatedRuntime = true, AccountAuthorizations = 0, ModelCalls = 0 }));
        Console.WriteLine($"PASS SIWC UI and runtime checks: {checks}; {directory}"); return checks;
    }
    static T Find<T>(DependencyObject parent, string name) where T : FrameworkElement
        => Descendants(parent).OfType<T>().First(e => e.Name == name);
    static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        yield return parent;
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
            foreach (var nested in Descendants(child)) yield return nested;
    }
}

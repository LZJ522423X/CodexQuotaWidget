using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Threading;
using CodexQuotaWidget.Core.ControlCenter;

namespace CodexQuotaWidget;

/// <summary>Shows the current task and real provider states; detailed task editing lives in an explicit modal.</summary>
public sealed class WorkflowLoopPanel : UserControl
{
    readonly IWorkflowLoopUiController controller;
    readonly TextBlock phase = Copy("待创建任务"), status = Copy("当前状态 · 待开始"), rounds = Copy("已完成 0 轮"), summary = Copy("暂无进行中的任务"), gptState = Copy("GPT · 未连接"), codexState = Copy("Codex · 未连接");
    readonly TextBlock anomaly = Copy(""), feedback = Copy(""), diagnostics = Copy(""), historyDetail = Copy(""), nextStep = Copy("", true);
    readonly Button start = Action("开始循环", "WorkflowLoopStart"), pause = Action("暂停循环", "WorkflowLoopPause");
    readonly Button stop = Action("停止循环", "WorkflowLoopStop");
    readonly CheckBox authorize = new() { Name = "WorkflowLoopAuthorize", Content = "允许在指定范围内执行本地修改", MinHeight = 36,
        VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
    readonly Button create = Action("新建任务", "WorkflowLoopNewTask"), edit = Action("编辑任务", "WorkflowLoopEditTask");
    readonly Button reconnect = Action("重新连接 GPT", "WorkflowLoopReconnectGpt"), connectCodex = Action("连接 Codex", "WorkflowLoopConnectCodex");
    readonly Button recover = Action("已核验，继续恢复", "WorkflowLoopRecover");
    readonly Button chooseGpt = Action("连接所选会话", "WorkflowLoopChooseGpt");
    readonly ComboBox candidates = new() { Name = "WorkflowLoopGptCandidates", MinHeight = 44, IsEditable = false,
        DisplayMemberPath = nameof(WorkflowConnectionCandidate.DisplayName), Margin = new Thickness(0, 4, 0, 4) };
    readonly StackPanel candidatePanel = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 8) };
    readonly ListBox history = new() { Name = "WorkflowLoopHistory", MaxHeight = 180, MinHeight = 44,
        HorizontalContentAlignment = HorizontalAlignment.Stretch };
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly Expander advanced, connections, historySection;
    string lastConnectionIssue = "";
    bool connectionBusy, editBusy;
    Guid authorizationTaskId;
    WorkflowLoopState[] lastHistory = Array.Empty<WorkflowLoopState>();
    internal Func<WorkflowLoopConfiguration, bool, Task<WorkflowLoopConfiguration?>>? TaskEditor { get; set; }

    public WorkflowLoopPanel(IWorkflowLoopUiController controller)
    {
        this.controller = controller; Name = "WorkflowLoopPanel"; SetResourceReference(ForegroundProperty, "Ink");
        var body = new StackPanel { Margin = new Thickness(0, 4, 0, 16) };
        var title = Copy("循环任务"); title.FontSize = 22; title.FontWeight = FontWeights.SemiBold; body.Children.Add(title);
        body.Children.Add(Copy("GPT 建议 → Codex 执行 → 验证复核", true));
        gptState.Name = "WorkflowLoopGptState"; codexState.Name = "WorkflowLoopCodexState";
        AutomationProperties.SetName(gptState, "GPT独立连接状态"); AutomationProperties.SetName(codexState, "Codex独立连接状态");
        var providerStates = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        providerStates.Children.Add(StateBadge(gptState)); providerStates.Children.Add(StateBadge(codexState)); body.Children.Add(providerStates);
        candidates.Template = ChoiceTemplate.Value; candidates.SetResourceReference(ForegroundProperty, "Ink");
        candidates.SetResourceReference(BackgroundProperty, "Surface"); candidates.SetResourceReference(BorderBrushProperty, "Line");
        AutomationProperties.SetName(candidates, "明确选择GPT会话，不默认选择任何候选");
        AutomationProperties.SetHelpText(candidates, "仅列出身份已验证且允许用户选择的候选；不会随机连接其他会话。");
        candidatePanel.Children.Add(Copy("连接冲突：请选择属于本工作流的已验证会话。")); candidatePanel.Children.Add(candidates);
        candidatePanel.Children.Add(chooseGpt);

        var taskBody = new StackPanel(); var taskTitle = Copy("当前任务"); taskTitle.FontWeight = FontWeights.SemiBold; taskBody.Children.Add(taskTitle);
        summary.Name = "WorkflowLoopTaskSummary"; summary.FontSize = 16; summary.MaxHeight = 64; summary.TextTrimming = TextTrimming.CharacterEllipsis;
        AutomationProperties.SetName(summary, "当前任务摘要；完整目标在编辑任务中查看"); taskBody.Children.Add(summary);
        status.Name = "WorkflowLoopStatus"; status.FontWeight = FontWeights.SemiBold;
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        rounds.Name = "WorkflowLoopCompletedRounds"; rounds.Margin = new Thickness(16, 4, 0, 4);
        var progress = new WrapPanel(); progress.Children.Add(status); progress.Children.Add(rounds); taskBody.Children.Add(progress);
        phase.Name = "WorkflowLoopPhase"; phase.FontWeight = FontWeights.SemiBold; phase.SetResourceReference(TextBlock.ForegroundProperty, "Accent"); taskBody.Children.Add(phase);
        nextStep.Name = "WorkflowLoopNextStep"; taskBody.Children.Add(nextStep);
        authorize.SetResourceReference(ForegroundProperty, "Ink");
        AutomationProperties.SetName(authorize, "明确授权本任务在已填写的范围内执行本地修改");
        AutomationProperties.SetHelpText(authorize, "必须先编辑任务，填写允许修改的文件或目录；保存任务不会自动授权或执行。");
        authorize.Checked += (_, _) => UpdateAvailability(); authorize.Unchecked += (_, _) => UpdateAvailability(); taskBody.Children.Add(authorize);
        var taskActions = new WrapPanel();
        taskActions.Children.Add(start); taskActions.Children.Add(pause); taskActions.Children.Add(stop);
        taskActions.Children.Add(create); taskActions.Children.Add(edit); taskBody.Children.Add(taskActions);
        var card = new Border { Child = taskBody, Padding = new Thickness(16), Margin = new Thickness(0, 12, 0, 8),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12) };
        card.SetResourceReference(Border.BorderBrushProperty, "Line"); card.SetResourceReference(Border.BackgroundProperty, "Surface"); body.Children.Add(card);
        start.SetResourceReference(BorderBrushProperty, "Accent");
        AutomationProperties.SetName(start, "开始GPT与Codex自动循环"); AutomationProperties.SetName(pause, "暂停自动工作流");
        AutomationProperties.SetName(stop, "停止当前自动工作流，保留进度");
        anomaly.Name = "WorkflowLoopAnomaly"; AutomationProperties.SetLiveSetting(anomaly, AutomationLiveSetting.Polite);
        body.Children.Add(anomaly);
        AutomationProperties.SetHelpText(recover, "仅解除已人工核验的恢复保护；不发送任务，不自动重发上轮请求。");
        feedback.Name = "WorkflowLoopFeedback"; AutomationProperties.SetName(feedback, "连接与工作流操作结果");
        AutomationProperties.SetLiveSetting(feedback, AutomationLiveSetting.Polite); body.Children.Add(feedback);

        var connectionBody = new StackPanel();
        connectionBody.Children.Add(Copy("首次使用时分别连接 GPT 与 Codex；连接就绪后，点击“开始循环”。", true));
        connectionBody.Children.Add(ProviderRow(Copy("GPT 建议与复核"), reconnect));
        connectionBody.Children.Add(ProviderRow(Copy("Codex 执行任务"), connectCodex));
        connections = Disclosure("WorkflowLoopConnections", "连接设置", connectionBody); body.Children.Add(connections);

        var advancedBody = new StackPanel();
        advancedBody.Children.Add(Copy("每轮派发前检查任务的轮数、时长和额度保护阈值；额度不足或状态不明时暂停。循环不授权关闭程序、关机或网络设置操作；恢复后不会自动重发未确认请求。", true));
        advancedBody.Children.Add(candidatePanel); advancedBody.Children.Add(recover);
        diagnostics.Name = "WorkflowLoopDiagnostics"; advancedBody.Children.Add(diagnostics);
        var historyBody = new StackPanel();
        history.SetResourceReference(BackgroundProperty, "Surface"); history.SetResourceReference(ForegroundProperty, "Ink"); history.SetResourceReference(BorderBrushProperty, "Line");
        var item = new FrameworkElementFactory(typeof(TextBlock)); item.SetBinding(TextBlock.TextProperty, new Binding(nameof(WorkflowLoopState.Goal)));
        item.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap); item.SetValue(TextBlock.FontSizeProperty, 12d); item.SetValue(TextBlock.MarginProperty, new Thickness(4));
        history.ItemTemplate = new DataTemplate(typeof(WorkflowLoopState)) { VisualTree = item };
        AutomationProperties.SetName(history, "历史工作流记录；选择后只读查看，不执行任务");
        historyBody.Children.Add(history); historyBody.Children.Add(historyDetail);
        historySection = Disclosure("WorkflowLoopHistorySection", "任务历史", historyBody); body.Children.Add(historySection);
        advanced = Disclosure("WorkflowLoopAdvanced", "高级详情", advancedBody); body.Children.Add(advanced); Content = body;

        reconnect.Click += async (_, _) => await ConnectAsync(true, null);
        connectCodex.Click += async (_, _) => await ConnectAsync(false, null);
        chooseGpt.Click += async (_, _) =>
        {
            if (candidates.SelectedItem is WorkflowConnectionCandidate { CanSelect: true } selected) await ConnectAsync(true, selected.Id);
        };
        candidates.SelectionChanged += (_, _) => UpdateAvailability();
        create.Click += async (_, _) => await EditAsync(true); edit.Click += async (_, _) => await EditAsync(false);
        start.Click += async (_, _) =>
        {
            if (!CanStart()) return;
            start.IsEnabled = false;
            try
            {
                feedback.Text = "正在核验并开始工作流……";
                var pending = controller.StartAsync(controller.Configuration with { LocalExecutionAuthorized = authorize.IsChecked == true }); Refresh();
                feedback.Text = await pending;
            }
            catch (Exception error) { feedback.Text = "工作流未完成：" + error.GetType().Name + "；已保存进度不会自动重发。"; }
            finally { Refresh(); }
        };
        pause.Click += (_, _) => { controller.Pause(); feedback.Text = "已请求暂停，正在保存并确认当前请求。"; Refresh(); };
        stop.Click += (_, _) => { if (!stop.IsEnabled) return; controller.Stop(); authorize.IsChecked = false;
            feedback.Text = "已请求停止，正在保存并确认当前请求。"; Refresh(); };
        recover.Click += (_, _) => { feedback.Text = controller.AcknowledgeRecovery(); Refresh(); };
        advanced.Expanded += (_, _) => RefreshAdvanced();
        historySection.Expanded += (_, _) => RefreshHistory();
        history.SelectionChanged += (_, _) => ShowHistory(history.SelectedItem as WorkflowLoopState);
        timer.Tick += (_, _) => Refresh(); Loaded += (_, _) => { timer.Start(); Refresh(); }; Unloaded += (_, _) => timer.Stop(); Refresh();
    }

    async Task ConnectAsync(bool gpt, string? candidateId)
    {
        if (connectionBusy || editBusy || controller.IsRunning) return;
        connectionBusy = true; feedback.Text = gpt ? "正在重新发现并核验GPT连接……" : "正在核验Codex连接与项目……"; Refresh();
        try
        {
            // The controller also owns cancellation and transport timeouts. UI never dispatches a model request here.
            var operation = gpt ? controller.ReconnectGptAsync(candidateId) : controller.ConnectCodexAsync();
            try
            {
                var result = await operation.WaitAsync(TimeSpan.FromSeconds(40));
                var state = gpt ? controller.GptConnection : controller.CodexConnection;
                feedback.Text = state.State == WorkflowLinkState.Connected ? (gpt ? "GPT 已连接。" : "Codex 已连接。") : result;
            }
            catch (TimeoutException)
            {
                feedback.Text = "连接核验超时；正在等待本次连接操作结束，不重复连接。";
                // Keep the guard until the existing attempt has actually settled, rather than creating a competing attempt.
                try { await operation; } catch { }
                feedback.Text = "连接核验超时；现有任务进度保留，可重新连接。";
            }
        }
        catch (Exception error) { feedback.Text = "连接失败：" + error.GetType().Name + "；现有任务进度保留。"; }
        finally { connectionBusy = false; Refresh(); }
    }

    async Task EditAsync(bool isNew)
    {
        if (editBusy || connectionBusy || controller.IsRunning || controller.State.NeedsRecoveryConfirmation) return;
        editBusy = true; Refresh();
        try
        {
            WorkflowLoopConfiguration? edited;
            if (TaskEditor is not null) edited = await TaskEditor(controller.Configuration, isNew);
            else
            {
                var editor = new TaskEditorDialog(controller.Configuration, isNew);
                if (Window.GetWindow(this) is { } owner) { editor.Owner = owner; if (owner.Icon is not null) editor.Icon = owner.Icon; }
                edited = editor.ShowDialog() == true ? editor.Result : null;
            }
            if (edited is not null) { authorize.IsChecked = false; feedback.Text = await controller.SaveTaskAsync(edited with { LocalExecutionAuthorized = false }); }
        }
        catch (Exception error) { feedback.Text = "任务未保存：" + error.GetType().Name + "；原有进度保留。"; }
        finally { editBusy = false; Refresh(); }
    }

    bool HasCurrentTask() => !string.IsNullOrWhiteSpace(controller.Configuration.Goal);
    bool IsCurrentState() => controller.Configuration.TaskId != Guid.Empty ? controller.Configuration.TaskId == controller.State.RunId
        : SameGoal(controller.Configuration.Goal, controller.State.Goal);
    bool TaskCompleted() => IsCurrentState() && controller.State.Phase == WorkflowLoopPhase.Completed;
    bool TaskStopped() => IsCurrentState() && controller.State.Phase == WorkflowLoopPhase.Stopped;
    bool HasWriteScope() => controller.Configuration.TaskId != Guid.Empty && controller.Configuration.AuthorizedWritePaths.Length > 0;
    bool HasValidationProjects() => !string.IsNullOrWhiteSpace(controller.Configuration.BuildProjectPath)
        && !string.IsNullOrWhiteSpace(controller.Configuration.ChecksProjectPath);
    bool CanStart() => HasCurrentTask() && !TaskCompleted() && !TaskStopped() && HasWriteScope() && HasValidationProjects() && authorize.IsChecked == true
        && !controller.IsRunning && !connectionBusy && !editBusy && !controller.State.NeedsRecoveryConfirmation
        && controller.GptConnection.State == WorkflowLinkState.Connected && controller.CodexConnection.State == WorkflowLinkState.Connected;
    static bool SameGoal(string left, string right) => string.Equals(left.Trim(), right.Trim(), StringComparison.Ordinal);

    public void Refresh()
    {
        var state = controller.State; var taskPresent = HasCurrentTask();
        if (authorizationTaskId != controller.Configuration.TaskId) { authorizationTaskId = controller.Configuration.TaskId; authorize.IsChecked = false; }
        gptState.Text = "GPT · " + ConnectionName(controller.GptConnection.State); codexState.Text = "Codex · " + ConnectionName(controller.CodexConnection.State);
        summary.Text = taskPresent ? ShortSummary(controller.Configuration.Goal) : "暂无进行中的任务";
        var stateIsCurrent = taskPresent && IsCurrentState();
        status.Text = "当前状态 · " + (!stateIsCurrent && !controller.IsRunning ? "待开始" : StatusName(state));
        rounds.Text = $"已完成 {(stateIsCurrent ? state.CompletedRounds : 0)} 轮";
        phase.Text = !taskPresent ? "创建第一项任务即可开始" : "当前阶段 · " + (!stateIsCurrent && !controller.IsRunning ? "待开始" : StageName(state));
        nextStep.Text = state.NeedsRecoveryConfirmation ? "结果尚未确认。请在高级详情中核验后再恢复。"
            : controller.IsRunning ? "正在推进；暂停或停止都会保留当前进度。"
            : !taskPresent ? "写下目标、选择项目；已完成的内容保留在任务历史。"
            : controller.Configuration.GptBackend == WorkflowBackend.SiwcGptReviewer ? "SIWC实验草稿：真实授权和模型调用尚未批准，额度Unknown；本阶段不能开始循环。"
            : TaskCompleted() ? "任务已完成。可查看历史或新建任务。"
            : TaskStopped() ? "任务已停止。进度保留；需要继续处理时请新建任务。"
            : !HasWriteScope() ? "当前任务为只读。编辑任务，确认允许修改的范围后才能开始执行。"
            : !HasValidationProjects() ? "请在编辑任务的高级设置中指定本地验证项目。"
            : controller.GptConnection.State != WorkflowLinkState.Connected || controller.CodexConnection.State != WorkflowLinkState.Connected
                ? "请分别连接 GPT 与 Codex。"
            : authorize.IsChecked != true ? "确认允许本地修改后，点击开始循环。" : "准备就绪。";
        var problems = new[] { ConnectionProblem("GPT", controller.GptConnection), ConnectionProblem("Codex", controller.CodexConnection),
            state.NeedsRecoveryConfirmation ? "当前请求结果未确认，循环已阻断。"
                : state.Phase == WorkflowLoopPhase.Blocked ? "循环已阻断，请在高级详情中查看原因。"
                : state.Phase == WorkflowLoopPhase.Failed ? "循环未完成，请在高级详情中查看原因。" : "" };
        anomaly.Text = string.Join("\n", problems.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => ShortSummary(value, 180)));
        anomaly.Visibility = anomaly.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        feedback.Visibility = feedback.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        recover.Visibility = state.NeedsRecoveryConfirmation ? Visibility.Visible : Visibility.Collapsed;
        candidatePanel.Visibility = controller.GptConnection.State == WorkflowLinkState.Conflict && controller.GptCandidates.Any(value => value.CanSelect)
            ? Visibility.Visible : Visibility.Collapsed;
        if (candidatePanel.Visibility == Visibility.Visible || state.NeedsRecoveryConfirmation) advanced.Header = "高级详情 · 需要核验";
        else advanced.Header = "高级详情";
        var selectedId = (candidates.SelectedItem as WorkflowConnectionCandidate)?.Id;
        var available = controller.GptCandidates.Where(value => value.CanSelect && !string.IsNullOrWhiteSpace(value.Id)).DistinctBy(value => value.Id).ToArray();
        if (candidates.ItemsSource is not WorkflowConnectionCandidate[] previous || !previous.SequenceEqual(available))
        {
            candidates.ItemsSource = available;
            candidates.SelectedItem = selectedId is null ? null : available.FirstOrDefault(value => value.Id == selectedId);
        }
        RefreshConnectionDisclosure();
        if (advanced.IsExpanded) RefreshAdvanced();
        if (historySection.IsExpanded) RefreshHistory();
        UpdateAvailability();
    }
    void UpdateAvailability()
    {
        var busy = connectionBusy || editBusy || controller.IsRunning;
        start.IsEnabled = CanStart(); pause.IsEnabled = controller.IsRunning; stop.IsEnabled = !connectionBusy && !editBusy
            && (controller.IsRunning || IsCurrentState() && (controller.State.Phase is WorkflowLoopPhase.Paused or WorkflowLoopPhase.Blocked or WorkflowLoopPhase.Interrupted or WorkflowLoopPhase.Failed));
        start.Visibility = HasCurrentTask() && !TaskCompleted() && !TaskStopped() && !controller.IsRunning && !controller.State.NeedsRecoveryConfirmation ? Visibility.Visible : Visibility.Collapsed;
        start.Content = IsCurrentState() && (controller.State.Phase is WorkflowLoopPhase.Paused or WorkflowLoopPhase.Blocked) ? "继续循环" : "开始循环";
        pause.Visibility = controller.IsRunning ? Visibility.Visible : Visibility.Collapsed;
        stop.Visibility = stop.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        authorize.Visibility = HasCurrentTask() && !TaskCompleted() && !TaskStopped() && !controller.IsRunning && !controller.State.NeedsRecoveryConfirmation ? Visibility.Visible : Visibility.Collapsed;
        authorize.IsEnabled = !busy && HasWriteScope() && HasValidationProjects();
        reconnect.IsEnabled = !busy && controller.GptConnection.State != WorkflowLinkState.Connecting;
        connectCodex.IsEnabled = !busy && controller.CodexConnection.State != WorkflowLinkState.Connecting;
        reconnect.Content = connectionBusy && controller.GptConnection.State == WorkflowLinkState.Connecting ? "连接中…" : controller.GptConnection.State == WorkflowLinkState.Disconnected ? "连接 GPT" : "重新连接 GPT";
        connectCodex.Content = connectionBusy && controller.CodexConnection.State == WorkflowLinkState.Connecting ? "连接中…" : controller.CodexConnection.State is WorkflowLinkState.Failed or WorkflowLinkState.Conflict ? "重新连接 Codex" : "连接 Codex";
        create.IsEnabled = !busy && !controller.State.NeedsRecoveryConfirmation; edit.IsEnabled = create.IsEnabled && HasCurrentTask() && !TaskCompleted() && !TaskStopped();
        create.Visibility = !controller.IsRunning && !controller.State.NeedsRecoveryConfirmation ? Visibility.Visible : Visibility.Collapsed;
        create.SetResourceReference(BorderBrushProperty, HasCurrentTask() && !TaskCompleted() && !TaskStopped() ? "Line" : "Accent");
        edit.Visibility = HasCurrentTask() && !TaskCompleted() && !TaskStopped() && !controller.IsRunning && !controller.State.NeedsRecoveryConfirmation ? Visibility.Visible : Visibility.Collapsed;
        candidates.IsEnabled = !busy; chooseGpt.IsEnabled = !busy && candidates.SelectedItem is WorkflowConnectionCandidate { CanSelect: true };
        recover.IsEnabled = !busy;
    }
    void RefreshConnectionDisclosure()
    {
        var gpt = controller.GptConnection; var codex = controller.CodexConnection;
        var ready = gpt.State == WorkflowLinkState.Connected && codex.State == WorkflowLinkState.Connected;
        var issue = ready ? "" : $"{gpt.State}:{gpt.DiagnosticCode}|{codex.State}:{codex.DiagnosticCode}";
        connections.Header = ready ? "连接设置 · 已就绪" : "连接设置 · 需要检查";
        // Expand a newly observed connection problem once. Respect a user's subsequent collapse while it is unchanged.
        if (issue != lastConnectionIssue) { connections.IsExpanded = !ready; lastConnectionIssue = issue; }
    }
    void RefreshAdvanced()
    {
        var state = controller.State;
        diagnostics.Text = $"{controller.ConnectionDescription}\n项目：{controller.Configuration.ProjectRoot}\nGPT会话：{controller.Configuration.ChatGptThreadId}"
            + $"\nGPT Backend：{controller.Configuration.GptBackend}\n执行 Backend：Codex Executor"
            + (controller.Configuration.GptBackend == WorkflowBackend.SiwcGptReviewer
                ? $"\nSIWC建议Session：{controller.Configuration.Siwc.AdviceSessionId}\nSIWC复核Session：{controller.Configuration.Siwc.ReviewSessionId}\nSIWC额度：Unknown（独立来源；不采用旧Desktop/Codex读数）\n{SiwcPhaseOne.BlockReason}" : "")
            + $"\nCodex控制会话：{controller.Configuration.CallerThreadId}\nTask：{controller.Configuration.TaskId}\nRun：{state.RunId}\nRequest：{state.CurrentRequestId}\nSession：{state.CodexSessionId}"
            + $"\nGPT诊断：{controller.GptConnection.DiagnosticCode}\nCodex诊断：{controller.CodexConnection.DiagnosticCode}"
            + $"\nGPT连接说明：{controller.GptConnection.Message}\nCodex连接说明：{controller.CodexConnection.Message}"
            + "\n桌面候选：\n" + string.Join("\n",controller.GptCandidates.Select(candidate => candidate.DisplayName + " · " + candidate.Detail))
            + $"\n允许修改的范围：\n{string.Join("\n", controller.Configuration.AuthorizedWritePaths)}\n初始报告：{controller.Configuration.InitialReportPath}"
            + $"\n本地验证项目：{controller.Configuration.BuildProjectPath}\n检查项目：{controller.Configuration.ChecksProjectPath}"
            + $"\n完整 Prompt / 目标：\n{controller.Configuration.Goal}\n状态说明：{state.Message}\n本轮验证：\n{state.ValidationReport}\nGPT复核：\n{state.Review}";
    }
    void RefreshHistory()
    {
        WorkflowLoopState[] records;
        try { records = controller.ReadHistory().ToArray(); }
        catch (Exception error)
        {
            historyDetail.Text = "历史记录暂不可读取：" + error.GetType().Name + "；当前任务与连接保持不变。";
            return;
        }
        if (!lastHistory.SequenceEqual(records))
        {
            var selected = (history.SelectedItem as WorkflowLoopState)?.RunId; lastHistory = records; history.ItemsSource = records;
            history.SelectedItem = selected is null ? null : records.FirstOrDefault(value => value.RunId == selected);
        }
        if (records.Length == 0) historyDetail.Text = "暂无历史记录。";
    }
    void ShowHistory(WorkflowLoopState? state)
    {
        historyDetail.Text = state is null ? "选择记录查看完整目标、执行报告和复核；不会重新执行。"
            : $"{PhaseName(state.Phase)} · 已完成 {state.CompletedRounds} 轮 · {state.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm}\n完整目标：\n{state.Goal}\n项目：{state.ProjectRoot}"
                + $"\nTask：{state.RunId}\nSession：{state.CodexSessionId}\n执行报告：\n{state.ExecutionReport}\n本地验证：\n{state.ValidationReport}\nGPT复核：\n{state.Review}";
    }
    static string ConnectionProblem(string provider, WorkflowProviderConnection connection) => connection.State switch
    {
        WorkflowLinkState.Conflict when connection.DiagnosticCode == "desktop_pipe_not_unique" => "GPT连接冲突：检测到多个桌面连接，请在高级详情中选择已核验会话。",
        WorkflowLinkState.Conflict => provider + "连接冲突，请重新核验连接。",
        WorkflowLinkState.Failed => provider + "连接失败，请重新连接。",
        _ => ""
    };
    static string ConnectionName(WorkflowLinkState state) => state switch
    { WorkflowLinkState.Connected => "已连接", WorkflowLinkState.Connecting => "连接中", WorkflowLinkState.Conflict => "连接冲突", WorkflowLinkState.Failed => "连接失败", _ => "未连接" };
    static string ShortSummary(string text, int length = 96)
    {
        var value = string.Join(" ", text.Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
        return value.Length <= length ? value : value[..length] + "…";
    }
    static Grid ProviderRow(TextBlock text, Button action)
    {
        var row = new Grid { Margin = new Thickness(0, 4, 0, 0) }; row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        text.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(text); action.Margin = new Thickness(8, 0, 0, 0); Grid.SetColumn(action, 1); row.Children.Add(action); return row;
    }
    static Border StateBadge(TextBlock text)
    {
        var badge = new Border { Child = text, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 8, 4),
            CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
        badge.SetResourceReference(Border.BackgroundProperty, "Surface"); badge.SetResourceReference(Border.BorderBrushProperty, "Line"); return badge;
    }
    static Expander Disclosure(string name, string caption, FrameworkElement body)
    {
        var section = DisclosureSection.Create(caption, body, name);
        section.Margin = new Thickness(0, 8, 0, 0);
        section.FontSize = 13;
        return section;
    }
    static TextBlock Copy(string text, bool muted = false)
    {
        var block = new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) };
        block.SetResourceReference(TextBlock.ForegroundProperty, muted ? "Muted" : "Ink"); return block;
    }
    static Button Action(string text, string name)
    {
        var button = new Button { Content = text, Name = name, MinHeight = 44, MinWidth = 76, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 8, 8, 0) };
        AutomationProperties.SetName(button, text); return button;
    }
    static string PhaseName(WorkflowLoopPhase value) => value switch
    {
        WorkflowLoopPhase.Idle => "待开始", WorkflowLoopPhase.Connecting => "核验连接与额度", WorkflowLoopPhase.AwaitingAdvice => "等待GPT建议",
        WorkflowLoopPhase.Executing => "Codex执行中", WorkflowLoopPhase.Validating => "本地验证中", WorkflowLoopPhase.AwaitingReview => "等待GPT复核",
        WorkflowLoopPhase.Completed => "已完成", WorkflowLoopPhase.Paused => "已暂停", WorkflowLoopPhase.Interrupted => "中断待核验",
        WorkflowLoopPhase.Blocked => "已阻断", WorkflowLoopPhase.Stopped => "已停止", _ => "未完成"
    };
    string StatusName(WorkflowLoopState value) => value.Phase == WorkflowLoopPhase.Stopped ? "已停止"
        : value.NeedsRecoveryConfirmation ? "已阻断" : value.Phase switch
    {
        WorkflowLoopPhase.Completed => "已完成", WorkflowLoopPhase.Stopped => "已停止", WorkflowLoopPhase.Blocked => "已阻断",
        WorkflowLoopPhase.Paused => "已暂停", WorkflowLoopPhase.Interrupted or WorkflowLoopPhase.Failed => "已阻断",
        _ => controller.IsRunning ? "运行中" : "待开始"
    };
    static string StageName(WorkflowLoopState value) => value.Phase switch
    {
        WorkflowLoopPhase.Idle => "待开始",
        WorkflowLoopPhase.AwaitingAdvice => "GPT 建议", WorkflowLoopPhase.Executing => "Codex 执行",
        WorkflowLoopPhase.Validating => "本地验证", WorkflowLoopPhase.AwaitingReview => "GPT 复核", WorkflowLoopPhase.Completed => "目标完成",
        WorkflowLoopPhase.Connecting => "核验连接与额度",
        _ => value.Checkpoint switch { WorkflowCheckpoint.Advice => "GPT 建议", WorkflowCheckpoint.Execution => "Codex 执行",
            WorkflowCheckpoint.Validation => "本地验证", WorkflowCheckpoint.Review or WorkflowCheckpoint.ReviewReceived => "GPT 复核",
            WorkflowCheckpoint.Completed => "目标完成", _ => "待开始" }
    };
    internal static ControlTemplate ChoiceControlTemplate => ChoiceTemplate.Value;
    static readonly Lazy<ControlTemplate> ChoiceTemplate = new(() => (ControlTemplate)XamlReader.Parse("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="{x:Type ComboBox}">
          <Grid>
            <ToggleButton x:Name="Toggle" Focusable="False" ClickMode="Press" BorderBrush="{DynamicResource Line}"
                          IsChecked="{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}">
              <ToggleButton.Template><ControlTemplate TargetType="{x:Type ToggleButton}">
                <Border Background="{DynamicResource Surface}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1" CornerRadius="4" Padding="9,6">
                  <TextBlock Text="▾" Foreground="{DynamicResource Ink}" HorizontalAlignment="Right" VerticalAlignment="Center"/>
                </Border>
              </ControlTemplate></ToggleButton.Template>
            </ToggleButton>
            <ContentPresenter Margin="10,0,28,0" IsHitTestVisible="False" VerticalAlignment="Center" Content="{TemplateBinding SelectionBoxItem}" ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}"/>
            <Popup x:Name="PART_Popup" Placement="Bottom" Focusable="False" AllowsTransparency="True" IsOpen="{TemplateBinding IsDropDownOpen}">
              <Border Background="{DynamicResource Surface}" BorderBrush="{DynamicResource Line}" BorderThickness="1" CornerRadius="4"
                      MinWidth="{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}">
                <ScrollViewer MaxHeight="220" CanContentScroll="True"><ItemsPresenter/></ScrollViewer>
              </Border>
            </Popup>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property="IsKeyboardFocusWithin" Value="True"><Setter TargetName="Toggle" Property="BorderBrush" Value="{DynamicResource Accent}"/></Trigger>
            <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
        """));
}

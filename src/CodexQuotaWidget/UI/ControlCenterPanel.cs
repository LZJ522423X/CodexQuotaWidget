using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using CodexQuotaWidget.Core.ControlCenter;

namespace CodexQuotaWidget;

/// <summary>
/// Local task tracking and quota protection. The panel never starts an AI process.
/// Quota refreshes update existing rows, retaining selection, keyboard focus and input.
/// </summary>
public sealed class ControlCenterPanel : UserControl, IDisposable
{
    readonly ControlCenterService service;
    readonly ObservableCollection<TaskRow> rows = new();
    readonly Dictionary<Guid, TaskRow> rowById = new();
    readonly ListBox taskList = new();
    readonly TextBox titleInput = Input("任务标题", 100);
    readonly TextBox notesInput = Input("任务目标或备注", 2000);
    readonly ComboBox priorityInput = PriorityChoice("新任务优先级");
    readonly ComboBox filterInput = Choice("任务筛选", new[] { "全部任务", "待处理", "已结束" });
    readonly TextBox editTitleInput = Input("编辑所选任务标题", 100);
    readonly TextBox editNotesInput = Input("编辑所选任务目标或备注", 2000);
    readonly ComboBox editPriorityInput = PriorityChoice("编辑所选任务优先级");
    readonly TextBox warningInput = Input("低额度警告阈值，百分比", 6);
    readonly TextBox protectionInput = Input("停止新任务阈值，百分比", 6);
    readonly Expander editor = new() { Header = "编辑所选任务", Margin = new Thickness(0, 8, 0, 0) };
    readonly Expander policyEditor = new() { Header = "额度保护阈值", Margin = new Thickness(0, 8, 0, 0) };
    readonly TextBlock editHint = Text("仅排队或暂停任务可编辑。", 11, true);
    readonly Button saveEdit = Button("保存任务", "保存所选任务内容与优先级");
    readonly Button resetEdit = Button("放弃本次编辑", "放弃所选任务未保存的编辑");
    readonly Button applyPolicy = Button("应用阈值", "应用低额度警告与保护阈值");
    readonly Button resetPolicy = Button("恢复当前值", "恢复已保存的额度阈值");
    readonly Button remove = Button("移除记录", "移除所选非运行中的本地任务记录");
    readonly Button confirmRemove = Button("确认移除", "确认移除本地任务记录");
    readonly Button keepRecord = Button("保留记录", "取消移除并保留本地任务记录");
    readonly Border removalConfirmation = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(10), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5) };
    readonly TextBlock removalCopy = Text("", 12);
    readonly Dictionary<Guid, TaskEditDraft> editDrafts = new();
    readonly TextBlock runtime = Text("尚未连接 AI 执行器", 15);
    readonly TextBlock guardTitle = Text("等待额度数据", 15);
    readonly TextBlock guardReason = Text("尚未获取可用于保护判断的额度。", 12, true);
    readonly TextBlock remaining = Text("5 小时 -- · 每周 --", 13);
    readonly ProgressBar headroom = new() { Minimum = 0, Maximum = 100, Height = 5, Margin = new Thickness(0, 10, 0, 0) };
    readonly TextBlock policyCaption = Text("", 11, true);
    readonly TextBlock queueSummary = Text("还没有本地任务", 12, true);
    readonly TextBlock selectedTitle = Text("选中任务后可更新状态", 14);
    readonly TextBlock selectedNotes = Text("任务状态由你记录，不代表 AI 已执行。", 12, true);
    readonly TextBlock selectedTime = Text("", 11, true);
    readonly TextBlock feedback = Text("", 12, true);
    readonly TextBlock storageStatus = Text("", 12, true);
    readonly TextBlock emptyHint = Text("添加一个任务，开始整理你的 AI 工作流。", 13, true);
    readonly Button add = Button("添加任务", "添加本地任务记录");
    readonly Button queueToggle = Button("暂停队列", "暂停开始新的本地任务");
    readonly Button startNext = Button("标记开始下一项", "按队列顺序标记下一项为进行中");
    readonly Button acknowledge = Button("确认额度恢复", "确认额度恢复后解除安全停止锁存");
    readonly Button primary = Button("标记开始", "标记所选本地任务开始");
    readonly Button complete = Button("完成", "标记所选任务完成");
    readonly Button fail = Button("失败", "标记所选任务失败");
    readonly Button cancel = Button("取消任务", "取消所选本地任务");
    bool disposed, subscribed, applyingRows;
    int refreshPending;
    long renderedRevision = -1;
    QuotaGuardSnapshot? renderedGuard;
    bool renderedSafeStop;
    string? renderedStorageError;
    string primaryAction = "";
    ICollectionView? taskView;
    Guid? editingTaskId, pendingRemovalId;
    bool loadingEditor, loadingPolicy, policyDirty;
    string loadedEditTitle = "", loadedEditNotes = "";
    int loadedEditPriority;

    public ControlCenterPanel(ControlCenterService service)
    {
        this.service = service ?? throw new ArgumentNullException(nameof(service));
        SetResourceReference(ForegroundProperty, "Ink");
        Name = "ControlCenterPanel";
        titleInput.Name = "ControlTaskTitle";
        notesInput.Name = "ControlTaskNotes";
        taskList.Name = "ControlTaskList";
        add.Name = "ControlTaskAdd";
        primary.Name = "ControlTaskPrimaryAction";
        complete.Name = "ControlTaskComplete";
        fail.Name = "ControlTaskFail";
        cancel.Name = "ControlTaskCancel";
        queueToggle.Name = "ControlQueueToggle";
        startNext.Name = "ControlQueueStartNext";
        acknowledge.Name = "ControlQuotaAcknowledge";
        priorityInput.Name = "ControlTaskPriority";
        filterInput.Name = "ControlTaskFilter";
        editTitleInput.Name = "ControlEditTitle";
        editNotesInput.Name = "ControlEditNotes";
        editPriorityInput.Name = "ControlEditPriority";
        saveEdit.Name = "ControlEditSave";
        resetEdit.Name = "ControlEditReset";
        remove.Name = "ControlTaskRemove";
        confirmRemove.Name = "ControlTaskConfirmRemove";
        keepRecord.Name = "ControlTaskKeepRecord";
        warningInput.Name = "ControlPolicyWarning";
        protectionInput.Name = "ControlPolicyProtection";
        applyPolicy.Name = "ControlPolicyApply";
        resetPolicy.Name = "ControlPolicyReset";
        storageStatus.Name = "ControlStorageStatus";
        editor.Name = "ControlTaskEditor";
        policyEditor.Name = "ControlPolicyEditor";
        NameScope.SetNameScope(this, new NameScope());
        foreach (var control in new FrameworkElement[]
            { titleInput, notesInput, taskList, add, primary, complete, fail, cancel, queueToggle, startNext, acknowledge,
                priorityInput, filterInput, editTitleInput, editNotesInput, editPriorityInput, saveEdit, resetEdit, remove,
                confirmRemove, keepRecord, warningInput, protectionInput, applyPolicy, resetPolicy, storageStatus, editor, policyEditor })
            RegisterName(control.Name, control);
        AutomationProperties.SetName(this, "AI 控制中心，本地任务与额度保护");
        AutomationProperties.SetName(guardTitle, "当前额度保护状态");
        AutomationProperties.SetLiveSetting(guardTitle, AutomationLiveSetting.Polite);
        var body = new StackPanel { Margin = new Thickness(0, 8, 0, 8) };
        Content = body;

        var intro = Text("工作流", 21);
        intro.FontWeight = FontWeights.SemiBold;
        body.Children.Add(intro);
        body.Children.Add(Text("本地任务记录 · 尚未连接 AI 执行器", 12, true));

        var runtimeCard = Card();
        runtimeCard.Children.Add(Text("AI 运行状态", 11, true));
        runtimeCard.Children.Add(runtime);
        runtimeCard.Children.Add(Text("额度连接状态与 AI 执行状态分别显示。当前操作只记录任务，不会启动模型或外部程序。", 12, true));
        body.Children.Add(Frame(runtimeCard));

        var guardCard = Card();
        guardCard.Children.Add(Text("额度保护", 11, true));
        guardCard.Children.Add(guardTitle);
        guardCard.Children.Add(remaining);
        headroom.SetResourceReference(ProgressBar.ForegroundProperty, "Accent");
        headroom.SetResourceReference(ProgressBar.BackgroundProperty, "Line");
        AutomationProperties.SetName(headroom, "5 小时及每周额度的最小剩余比例");
        guardCard.Children.Add(headroom);
        guardCard.Children.Add(guardReason);
        guardCard.Children.Add(policyCaption);
        acknowledge.HorizontalAlignment = HorizontalAlignment.Left;
        acknowledge.Margin = new Thickness(0, 10, 0, 0);
        acknowledge.Visibility = Visibility.Collapsed;
        acknowledge.Click += (_, _) => Perform(() => service.Snapshot.ExitRequested?service.CancelSafeExit():service.AcknowledgeSafeStop());
        guardCard.Children.Add(acknowledge);
        BuildPolicyEditor();
        guardCard.Children.Add(policyEditor);
        body.Children.Add(Frame(guardCard));

        var form = Card();
        form.Children.Add(Text("新任务", 14));
        form.Children.Add(Label("标题", titleInput));
        AutomationProperties.SetHelpText(titleInput, "最多 100 字。任务与备注保存在本机，不自动发送给 AI。");
        form.Children.Add(titleInput);
        form.Children.Add(Label("目标 / 备注（可选）", notesInput));
        notesInput.AcceptsReturn = true;
        notesInput.TextWrapping = TextWrapping.Wrap;
        notesInput.MinHeight = 62;
        notesInput.MaxHeight = 120;
        notesInput.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        AutomationProperties.SetHelpText(notesInput, "最多 2000 字；目标或备注只保存在本地任务记录中。");
        form.Children.Add(notesInput);
        form.Children.Add(Label("优先级", priorityInput));
        form.Children.Add(priorityInput);
        add.HorizontalAlignment = HorizontalAlignment.Left;
        add.Margin = new Thickness(0, 12, 0, 0);
        add.SetResourceReference(System.Windows.Controls.Button.BorderBrushProperty, "Accent");
        add.FontWeight = FontWeights.SemiBold;
        add.Click += (_, _) => AddTask();
        form.Children.Add(add);
        titleInput.TextChanged += (_, _) => add.IsEnabled = !string.IsNullOrWhiteSpace(titleInput.Text) && service.Snapshot.Tasks.Count < 200;
        titleInput.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
            {
                AddTask();
                e.Handled = true;
            }
        };
        body.Children.Add(Frame(form));

        var queueHeader = new StackPanel { Margin = new Thickness(0, 12, 0, 8) };
        queueHeader.Children.Add(Text("任务队列", 15));
        queueHeader.Children.Add(queueSummary);
        var queueActions = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        queueActions.Children.Add(queueToggle);
        queueActions.Children.Add(startNext);
        queueToggle.Click += (_, _) => Perform(() => service.SetDispatchPaused(!service.Snapshot.DispatchPaused));
        startNext.Click += (_, _) => Perform(() => service.StartNext());
        queueHeader.Children.Add(queueActions);
        filterInput.HorizontalAlignment = HorizontalAlignment.Left;
        filterInput.MinWidth = 160;
        filterInput.Margin = new Thickness(0, 0, 0, 6);
        filterInput.SelectionChanged += (_, _) => ApplyFilter();
        queueHeader.Children.Add(Label("列表筛选（不改变派发顺序）", filterInput));
        queueHeader.Children.Add(filterInput);
        body.Children.Add(queueHeader);

        ConfigureList();
        body.Children.Add(taskList);
        emptyHint.Margin = new Thickness(12, 16, 12, 16);
        body.Children.Add(emptyHint);

        var selectedCard = Card();
        selectedCard.Children.Add(selectedTitle);
        selectedCard.Children.Add(selectedNotes);
        selectedCard.Children.Add(selectedTime);
        var taskActions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var button in new[] { primary, complete, fail, cancel, remove }) taskActions.Children.Add(button);
        selectedCard.Children.Add(taskActions);
        primary.Click += (_, _) => RunSelected();
        complete.Click += (_, _) => WithSelected(id => service.CompleteTask(id));
        fail.Click += (_, _) => WithSelected(id => service.FailTask(id, "由用户标记失败"));
        cancel.Click += (_, _) => WithSelected(id => service.CancelTask(id));
        BuildTaskEditor();
        selectedCard.Children.Add(editor);
        BuildRemovalConfirmation();
        selectedCard.Children.Add(removalConfirmation);
        body.Children.Add(Frame(selectedCard));

        feedback.Margin = new Thickness(0, 6, 0, 6);
        AutomationProperties.SetName(feedback, "操作反馈");
        AutomationProperties.SetLiveSetting(feedback, AutomationLiveSetting.Polite);
        body.Children.Add(feedback);
        storageStatus.Margin = new Thickness(0, 4, 0, 4);
        body.Children.Add(storageStatus);
        body.Children.Add(Text("低额度时停止开始新任务，任务记录保存在本机。", 11, true));

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        RefreshView();
    }

    void ConfigureList()
    {
        taskView = CollectionViewSource.GetDefaultView(rows);
        taskView.Filter = item => item is TaskRow row && MatchesFilter(row);
        taskList.ItemsSource = taskView;
        taskList.MaxHeight = 260;
        taskList.MinHeight = 0;
        taskList.BorderThickness = new Thickness(1);
        taskList.SetResourceReference(Control.BorderBrushProperty, "Line");
        taskList.SetResourceReference(Control.BackgroundProperty, "Surface");
        taskList.SetResourceReference(Control.ForegroundProperty, "Ink");
        taskList.SelectionMode = SelectionMode.Single;
        taskList.SelectionChanged += (_, _) => { if (!applyingRows) RefreshSelection(); };
        ScrollViewer.SetHorizontalScrollBarVisibility(taskList, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(taskList, ScrollBarVisibility.Auto);
        VirtualizingPanel.SetIsVirtualizing(taskList, true);
        VirtualizingPanel.SetVirtualizationMode(taskList, VirtualizationMode.Recycling);
        KeyboardNavigation.SetTabNavigation(taskList, KeyboardNavigationMode.Once);
        AutomationProperties.SetName(taskList, "本地任务队列，使用上下方向键选择任务");

        var rowStyle = new Style(typeof(ListBoxItem));
        rowStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        rowStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 8, 10, 8)));
        rowStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(3, 2, 3, 2)));
        rowStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(2)));
        rowStyle.Setters.Add(new Setter(Control.BorderBrushProperty, Brushes.Transparent));
        rowStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        rowStyle.Setters.Add(new Setter(AutomationProperties.NameProperty, new Binding(nameof(TaskRow.AccessibleName))));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        border.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
        rowStyle.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(ListBoxItem)) { VisualTree = border }));
        var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("Hover")));
        selected.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension("Accent")));
        rowStyle.Triggers.Add(selected);
        var focused = new Trigger { Property = UIElement.IsKeyboardFocusWithinProperty, Value = true };
        focused.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension("Accent")));
        rowStyle.Triggers.Add(focused);
        taskList.ItemContainerStyle = rowStyle;

        var panel = new FrameworkElementFactory(typeof(StackPanel));
        var title = new FrameworkElementFactory(typeof(TextBlock));
        title.SetValue(TextBlock.FontSizeProperty, 13d);
        title.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        title.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        title.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        title.SetBinding(TextBlock.TextProperty, new Binding(nameof(TaskRow.Title)));
        panel.AppendChild(title);
        var caption = new FrameworkElementFactory(typeof(TextBlock));
        caption.SetValue(TextBlock.FontSizeProperty, 11d);
        caption.SetValue(TextBlock.MarginProperty, new Thickness(0, 3, 0, 0));
        caption.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        caption.SetBinding(TextBlock.TextProperty, new Binding(nameof(TaskRow.Caption)));
        panel.AppendChild(caption);
        taskList.ItemTemplate = new DataTemplate(typeof(TaskRow)) { VisualTree = panel };
    }

    void BuildTaskEditor()
    {
        editor.SetResourceReference(Control.ForegroundProperty, "Ink");
        var fields = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        fields.Children.Add(editHint);
        fields.Children.Add(Label("标题", editTitleInput));
        fields.Children.Add(editTitleInput);
        fields.Children.Add(Label("目标 / 备注", editNotesInput));
        editNotesInput.AcceptsReturn = true;
        editNotesInput.TextWrapping = TextWrapping.Wrap;
        editNotesInput.MinHeight = 62;
        editNotesInput.MaxHeight = 120;
        editNotesInput.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        fields.Children.Add(editNotesInput);
        fields.Children.Add(Label("优先级", editPriorityInput));
        fields.Children.Add(editPriorityInput);
        var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        actions.Children.Add(saveEdit);
        actions.Children.Add(resetEdit);
        fields.Children.Add(actions);
        editor.Content = fields;
        editTitleInput.TextChanged += (_, _) => RememberEditDraft();
        editNotesInput.TextChanged += (_, _) => RememberEditDraft();
        editPriorityInput.SelectionChanged += (_, _) => RememberEditDraft();
        saveEdit.Click += (_, _) => SaveTaskEdit();
        resetEdit.Click += (_, _) =>
        {
            if (editingTaskId is Guid id) editDrafts.Remove(id);
            loadedEditTitle = "";
            RefreshSelection();
            feedback.Text = "已恢复所选任务的已保存内容；新任务草稿未改变。";
        };
    }

    void BuildPolicyEditor()
    {
        policyEditor.SetResourceReference(Control.ForegroundProperty, "Ink");
        var fields = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        fields.Children.Add(Label("警告阈值（%）", warningInput));
        fields.Children.Add(warningInput);
        fields.Children.Add(Label("停止新任务阈值（%）", protectionInput));
        fields.Children.Add(protectionInput);
        fields.Children.Add(Text("警告需不低于保护阈值。修改后不会自动恢复任务；未知或耗尽的额度仍禁止开始。", 11, true));
        var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        actions.Children.Add(applyPolicy);
        actions.Children.Add(resetPolicy);
        fields.Children.Add(actions);
        policyEditor.Content = fields;
        warningInput.TextChanged += (_, _) => { if (!loadingPolicy) policyDirty = true; };
        protectionInput.TextChanged += (_, _) => { if (!loadingPolicy) policyDirty = true; };
        applyPolicy.Click += (_, _) =>
        {
            if (!double.TryParse(warningInput.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var warning)
                || !double.TryParse(protectionInput.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var protection)
                || !double.IsFinite(warning) || !double.IsFinite(protection))
            {
                feedback.Text = "阈值必须是 0–100 的数字，警告阈值不能低于保护阈值。";
                feedback.FontWeight = FontWeights.SemiBold;
                return;
            }
            var current = service.Snapshot.Policy;
            var result = Perform(() => service.UpdatePolicy(current with { WarningPercent = warning, ProtectionPercent = protection }));
            if (result?.Success == true) { policyDirty = false; LoadPolicy(service.Snapshot.Policy); }
        };
        resetPolicy.Click += (_, _) =>
        {
            policyDirty = false;
            LoadPolicy(service.Snapshot.Policy);
            feedback.Text = "已恢复当前已保存的额度阈值。";
        };
    }

    void LoadPolicy(QuotaProtectionPolicy policy)
    {
        if (policyDirty) return;
        loadingPolicy = true;
        try
        {
            warningInput.Text = policy.WarningPercent.ToString("0.##", CultureInfo.CurrentCulture);
            protectionInput.Text = policy.ProtectionPercent.ToString("0.##", CultureInfo.CurrentCulture);
        }
        finally { loadingPolicy = false; }
    }

    void BuildRemovalConfirmation()
    {
        removalConfirmation.SetResourceReference(Border.BorderBrushProperty, "Line");
        removalConfirmation.SetResourceReference(Border.BackgroundProperty, "Surface");
        var content = new StackPanel();
        content.Children.Add(removalCopy);
        var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        actions.Children.Add(confirmRemove);
        actions.Children.Add(keepRecord);
        content.Children.Add(actions);
        removalConfirmation.Child = content;
        remove.Click += (_, _) =>
        {
            if (taskList.SelectedItem is not TaskRow row) return;
            pendingRemovalId = row.Id;
            removalCopy.Text = $"移除“{row.Title}”的本地记录？此操作无法撤销，不会终止任何外部程序。";
            removalConfirmation.Visibility = Visibility.Visible;
            confirmRemove.Focus();
            removalConfirmation.BringIntoView();
        };
        keepRecord.Click += (_, _) =>
        {
            pendingRemovalId = null;
            removalConfirmation.Visibility = Visibility.Collapsed;
            feedback.Text = "已保留本地任务记录。";
            remove.Focus();
        };
        confirmRemove.Click += (_, _) =>
        {
            if (pendingRemovalId is not Guid id) return;
            var result = Perform(() => service.RemoveTask(id));
            if (result?.Success == true) editDrafts.Remove(id);
            pendingRemovalId = null;
            removalConfirmation.Visibility = Visibility.Collapsed;
        };
    }

    bool MatchesFilter(TaskRow row) => filterInput.SelectedIndex switch
    {
        1 => row.State is not (WorkItemState.Completed or WorkItemState.Failed or WorkItemState.Cancelled),
        2 => row.State is WorkItemState.Completed or WorkItemState.Failed or WorkItemState.Cancelled,
        _ => true
    };

    void ApplyFilter()
    {
        if (taskView is null) return;
        var selected = taskList.SelectedItem as TaskRow;
        applyingRows = true;
        try
        {
            taskView.Refresh();
            taskList.SelectedItem = selected is not null && MatchesFilter(selected) ? selected : taskView.Cast<TaskRow>().FirstOrDefault();
        }
        finally { applyingRows = false; }
        RefreshEmptyHint();
        RefreshSelection();
    }

    void RefreshEmptyHint()
    {
        var empty = taskView?.IsEmpty ?? rows.Count == 0;
        emptyHint.Text = rows.Count == 0 ? "添加一个任务，开始整理你的 AI 工作流。" : "当前筛选没有任务。筛选不会改变队列派发顺序。";
        emptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        taskList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (disposed || subscribed) return;
        service.Changed += OnServiceChanged;
        subscribed = true;
        RefreshView();
    }

    void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!subscribed) return;
        service.Changed -= OnServiceChanged;
        subscribed = false;
    }

    void OnServiceChanged()
    {
        if (disposed || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        if (Dispatcher.CheckAccess()) { RefreshView(); return; }
        // Multiple monitor updates share one queued dispatch instead of rebuilding controls.
        if (Interlocked.CompareExchange(ref refreshPending, 1, 0) != 0) return;
        try
        {
            Dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(() =>
            {
                Interlocked.Exchange(ref refreshPending, 0);
                if (!disposed && subscribed) RefreshView();
            }));
        }
        catch (InvalidOperationException) when (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        { Interlocked.Exchange(ref refreshPending, 0); }
    }

    public void RefreshView()
    {
        if (disposed) return;
        if (!Dispatcher.CheckAccess()) { OnServiceChanged(); return; }
        var snapshot = service.Snapshot;
        if (renderedRevision == snapshot.Revision && renderedGuard == snapshot.Guard && renderedSafeStop == snapshot.SafeStopRequested && renderedStorageError == snapshot.StorageError) return;
        renderedRevision = snapshot.Revision;
        renderedGuard = snapshot.Guard;
        renderedSafeStop = snapshot.SafeStopRequested;
        renderedStorageError = snapshot.StorageError;
        runtime.Text = snapshot.RuntimeState.ToString() switch
        {
            "LocalTracking" => "本地任务跟踪中",
            "SafeStopRequested" => "已请求安全停止 · 请暂停当前任务",
            "Idle" => "本地任务记录待命",
            _ => "尚未连接 AI 执行器"
        };
        guardTitle.Text = snapshot.Guard.State.ToString() switch
        {
            "Ready" => "额度充足",
            "Warning" => "额度偏低，请控制任务规模",
            "Protected" => "已保护：停止开始新任务",
            "Exhausted" => "额度耗尽：队列已停止",
            _ => "额度未知：暂停开始新任务"
        };
        guardTitle.FontWeight = snapshot.Guard.Warning ? FontWeights.SemiBold : FontWeights.Normal;
        guardTitle.SetResourceReference(TextBlock.ForegroundProperty,snapshot.Guard.State is QuotaGuardState.Exhausted or QuotaGuardState.Protected?"Danger":snapshot.Guard.State==QuotaGuardState.Warning?"Warning":"Ink");
        remaining.Text = $"5 小时 {Percent(snapshot.Guard.FiveHourRemaining)} · 每周 {Percent(snapshot.Guard.WeeklyRemaining)}";
        guardReason.Text = snapshot.Guard.Reason;
        var bothKnown = snapshot.Guard.FiveHourRemaining.HasValue && snapshot.Guard.WeeklyRemaining.HasValue;
        headroom.Visibility = bothKnown ? Visibility.Visible : Visibility.Collapsed;
        if (bothKnown)
        {
            headroom.Value = Math.Clamp(Math.Min(snapshot.Guard.FiveHourRemaining!.Value, snapshot.Guard.WeeklyRemaining!.Value), 0, 100);
            remaining.Text += snapshot.Guard.State == QuotaGuardState.Unknown
                ? " · 旧快照，当前不可用" : $" · 基准 {Percent(headroom.Value)}";
            AutomationProperties.SetHelpText(headroom, "保护基准取 5 小时与每周剩余额度的较低值。");
        }
        policyCaption.Text = snapshot.Policy.Enabled
            ? $"不高于 {snapshot.Policy.WarningPercent:0.#}% 提醒 · 不高于 {snapshot.Policy.ProtectionPercent:0.#}% 停止新任务 · 仅使用新鲜额度数据"
            : "额度保护已关闭；数据未知或耗尽时仍阻止启动。";
        LoadPolicy(snapshot.Policy);
        acknowledge.Visibility = snapshot.SafeStopRequested || snapshot.ExitRequested ? Visibility.Visible : Visibility.Collapsed;
        acknowledge.Content = snapshot.ExitRequested?"取消安全退出请求":"确认额度恢复";
        AutomationProperties.SetName(acknowledge,snapshot.ExitRequested?"取消安全退出请求，队列仍暂停":"确认额度恢复后解除安全停止锁存");
        acknowledge.IsEnabled = snapshot.ExitRequested || snapshot.SafeStopRequested && snapshot.Guard.CanStart;
        queueToggle.Content = snapshot.DispatchPaused ? "恢复队列" : "暂停队列";
        AutomationProperties.SetName(queueToggle, snapshot.DispatchPaused ? "恢复本地任务队列" : "暂停本地任务队列");
        queueToggle.IsEnabled = !snapshot.ExitRequested;
        var canStart = snapshot.Guard.CanStart && !snapshot.DispatchPaused && !snapshot.SafeStopRequested && !snapshot.ExitRequested;
        startNext.IsEnabled = canStart && snapshot.Tasks.Any(t => t.State.ToString() == "Queued") && !snapshot.Tasks.Any(t => t.State.ToString() == "Running");
        var queued = snapshot.Tasks.Count(t => t.State.ToString() == "Queued");
        var running = snapshot.Tasks.Count(t => t.State.ToString() == "Running");
        queueSummary.Text = $"{snapshot.Tasks.Count} 项记录 · {queued} 项排队 · {running} 项进行中" +
            (snapshot.SafeStopRequested ? " · 安全停止已锁存" : snapshot.DispatchPaused ? " · 队列已暂停" : " · 手动开始");
        storageStatus.Visibility = string.IsNullOrWhiteSpace(snapshot.StorageError) ? Visibility.Collapsed : Visibility.Visible;
        storageStatus.Text = string.IsNullOrWhiteSpace(snapshot.StorageError) ? "" : "任务存储异常：" + snapshot.StorageError;
        storageStatus.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
        storageStatus.FontWeight = FontWeights.SemiBold;
        RefreshRows(snapshot.Tasks);
        add.IsEnabled = !string.IsNullOrWhiteSpace(titleInput.Text) && snapshot.Tasks.Count < 200;
        RefreshSelection();
    }

    void RefreshRows(IReadOnlyList<ControlWorkItem> tasks)
    {
        var selectedId = (taskList.SelectedItem as TaskRow)?.Id;
        applyingRows = true;
        bool filterChanged = false;
        try
        {
            var ids = tasks.Select(t => t.Id).ToHashSet();
            for (var i = rows.Count - 1; i >= 0; i--)
                if (!ids.Contains(rows[i].Id)) { rowById.Remove(rows[i].Id); rows.RemoveAt(i); }
            for (var i = 0; i < tasks.Count; i++)
            {
                var task = tasks[i];
                if (!rowById.TryGetValue(task.Id, out var row))
                {
                    row = new TaskRow(task);
                    rowById.Add(task.Id, row);
                    rows.Insert(Math.Min(i, rows.Count), row);
                }
                else
                {
                    filterChanged |= row.State != task.State;
                    row.Update(task);
                    var oldIndex = rows.IndexOf(row);
                    if (oldIndex != i) rows.Move(oldIndex, i);
                }
            }
            if (filterChanged && filterInput.SelectedIndex != 0) taskView?.Refresh();
            if (selectedId.HasValue && rowById.TryGetValue(selectedId.Value, out var selection) && MatchesFilter(selection)) taskList.SelectedItem = selection;
            else if (taskList.SelectedItem is null || taskList.SelectedItem is TaskRow hidden && !MatchesFilter(hidden)) taskList.SelectedItem = taskView?.Cast<TaskRow>().FirstOrDefault();
        }
        finally { applyingRows = false; }
        RefreshEmptyHint();
    }

    void RefreshSelection()
    {
        var snapshot = service.Snapshot;
        var selected = taskList.SelectedItem as TaskRow;
        var task = selected is null ? null : snapshot.Tasks.FirstOrDefault(t => t.Id == selected.Id);
        primaryAction = "";
        complete.IsEnabled = fail.IsEnabled = cancel.IsEnabled = primary.IsEnabled = remove.IsEnabled = false;
        if (task is null)
        {
            selectedTitle.Text = "选中任务后可更新状态";
            selectedNotes.Text = "任务状态由你记录，不代表 AI 已执行。";
            selectedTime.Text = "";
            editingTaskId = null;
            editor.IsEnabled = false;
            pendingRemovalId = null;
            removalConfirmation.Visibility = Visibility.Collapsed;
            return;
        }
        selectedTitle.Text = task.Title;
        selectedNotes.Text = string.IsNullOrWhiteSpace(task.Notes) ? "未填写备注" : task.Notes;
        if (!string.IsNullOrWhiteSpace(task.FailureReason)) selectedNotes.Text += "\n原因：" + task.FailureReason;
        selectedTime.Text = $"{StateLabel(task.State.ToString())} · 更新于 {task.UpdatedAt.ToLocalTime():MM-dd HH:mm}";
        var state = task.State.ToString();
        var canStart = snapshot.Guard.CanStart && !snapshot.DispatchPaused && !snapshot.SafeStopRequested && !snapshot.ExitRequested;
        switch (state)
        {
            case "Queued": primary.Content = "标记开始"; primaryAction = "start"; primary.IsEnabled = canStart && !snapshot.Tasks.Any(t => t.State.ToString() == "Running"); break;
            case "Running": primary.Content = "暂停任务"; primaryAction = "pause"; primary.IsEnabled = true; break;
            case "Paused": primary.Content = "恢复任务"; primaryAction = "resume"; primary.IsEnabled = canStart && !snapshot.Tasks.Any(t => t.State.ToString() == "Running"); break;
            case "Failed":
            case "Cancelled":
            case "Interrupted": primary.Content = "重新排队"; primaryAction = "retry"; primary.IsEnabled = true; break;
            default: primary.Content = "已完成"; break;
        }
        AutomationProperties.SetName(primary, primary.Content + "：" + task.Title);
        complete.IsEnabled = fail.IsEnabled = state is "Running" or "Paused";
        cancel.IsEnabled = state is "Queued" or "Running" or "Paused";
        remove.IsEnabled = task.State is not (WorkItemState.Running or WorkItemState.Paused);
        if (pendingRemovalId is Guid pending && (pending != task.Id || !remove.IsEnabled))
        {
            pendingRemovalId = null;
            removalConfirmation.Visibility = Visibility.Collapsed;
        }
        RefreshTaskEditor(task);
        AutomationProperties.SetHelpText(primary, primaryAction is "start" or "resume" && !canStart
            ? "额度未知、受保护或队列已暂停时不能开始；不会启动 AI 执行器。"
            : "只更新本地任务状态，不启动或停止外部 AI 进程。");
    }

    void AddTask()
    {
        if (!add.IsEnabled) { titleInput.Focus(); return; }
        var result = Perform(() => service.AddTask(titleInput.Text, notesInput.Text, Priority(priorityInput)));
        if (result?.Success != true) return;
        titleInput.Clear();
        notesInput.Clear();
        priorityInput.SelectedIndex = 0;
        if (result.TaskId is Guid id && rowById.TryGetValue(id, out var row))
        {
            if (!MatchesFilter(row)) filterInput.SelectedIndex = 1;
            taskList.SelectedItem = row;
            taskList.ScrollIntoView(row);
        }
        titleInput.Focus();
    }

    void RefreshTaskEditor(ControlWorkItem task)
    {
        bool editable = task.State is WorkItemState.Queued or WorkItemState.Paused;
        if (editingTaskId != task.Id || !editDrafts.ContainsKey(task.Id) &&
            (loadedEditTitle != task.Title || loadedEditNotes != task.Notes || loadedEditPriority != task.Priority))
        {
            editingTaskId = task.Id;
            loadingEditor = true;
            try
            {
                var draft = editDrafts.GetValueOrDefault(task.Id) ?? new(task.Title, task.Notes, task.Priority);
                editTitleInput.Text = draft.Title;
                editNotesInput.Text = draft.Notes;
                SelectPriority(editPriorityInput, draft.Priority);
                loadedEditTitle = task.Title;
                loadedEditNotes = task.Notes;
                loadedEditPriority = task.Priority;
            }
            finally { loadingEditor = false; }
        }
        editor.IsEnabled = editable;
        saveEdit.IsEnabled = editable && !string.IsNullOrWhiteSpace(editTitleInput.Text) && editDrafts.ContainsKey(task.Id);
        resetEdit.IsEnabled = editDrafts.ContainsKey(task.Id);
        editHint.Text = editable ? editDrafts.ContainsKey(task.Id) ? "有未保存的编辑；不会覆盖新任务草稿。" : "修改标题、备注或优先级后保存。" : "当前状态不可编辑；需暂停或重新排队。";
    }

    void RememberEditDraft()
    {
        if (loadingEditor || editingTaskId is not Guid id) return;
        editDrafts[id] = new(editTitleInput.Text, editNotesInput.Text, Priority(editPriorityInput));
        saveEdit.IsEnabled = editor.IsEnabled && !string.IsNullOrWhiteSpace(editTitleInput.Text);
        resetEdit.IsEnabled = true;
        editHint.Text = "有未保存的编辑；不会覆盖新任务草稿。";
    }

    void SaveTaskEdit()
    {
        if (editingTaskId is not Guid id) return;
        var result = Perform(() => service.UpdateTask(id, editTitleInput.Text, editNotesInput.Text, Priority(editPriorityInput)));
        if (result?.Success != true) return;
        editDrafts.Remove(id);
        RefreshSelection();
    }

    void RunSelected() => WithSelected(id => primaryAction switch
    {
        "start" => service.StartTask(id),
        "pause" => service.PauseTask(id),
        "resume" => service.ResumeTask(id),
        "retry" => service.RetryTask(id),
        _ => new ControlOperationResult(false, "当前状态没有可执行操作。")
    });

    void WithSelected(Func<Guid, ControlOperationResult> operation)
    {
        if (taskList.SelectedItem is TaskRow row) Perform(() => operation(row.Id));
    }

    ControlOperationResult? Perform(Func<ControlOperationResult> operation)
    {
        try
        {
            var result = operation();
            feedback.Text = result.Message;
            feedback.SetResourceReference(TextBlock.ForegroundProperty, result.Success ? "Muted" : "Ink");
            feedback.FontWeight = result.Success ? FontWeights.Normal : FontWeights.SemiBold;
            RefreshView();
            return result;
        }
        catch (Exception)
        {
            feedback.Text = "操作未完成，请检查任务存储状态。现有任务不会自动删除。";
            feedback.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
            feedback.FontWeight = FontWeights.SemiBold;
            RefreshView();
            return null;
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (subscribed) service.Changed -= OnServiceChanged;
        subscribed = false;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
    }

    internal Guid? SelectedTaskIdForLayout => (taskList.SelectedItem as TaskRow)?.Id;
    internal int TaskCountForLayout => rows.Count;
    internal string RuntimeTextForLayout => runtime.Text;
    internal bool StartNextEnabledForLayout => startNext.IsEnabled;
    internal void SelectTaskForLayout(Guid id)
    {
        if (rowById.TryGetValue(id, out var row)) taskList.SelectedItem = row;
    }

    static string Percent(double? value) => value.HasValue ? Math.Clamp(value.Value, 0, 100).ToString("0.#", CultureInfo.CurrentCulture) + "%" : "未知";
    static string StateLabel(string state) => state switch
    {
        "Queued" => "排队中", "Running" => "进行中（本地记录）", "Paused" => "已暂停",
        "Completed" => "已完成", "Failed" => "失败", "Cancelled" => "已取消", "Interrupted" => "运行中断", _ => "未知状态"
    };

    static TextBlock Text(string text, double size, bool muted = false)
    {
        var block = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 3) };
        block.SetResourceReference(TextBlock.ForegroundProperty, muted ? "Muted" : "Ink");
        return block;
    }

    static Label Label(string text, Control target)
    {
        var label = new Label { Content = text, Target = target, Padding = new Thickness(0, 7, 0, 5), FontSize = 12 };
        label.SetResourceReference(Control.ForegroundProperty, "Muted");
        return label;
    }

    static TextBox Input(string name, int limit)
    {
        var input = new TextBox { MaxLength = limit, MinHeight = 38, Padding = new Thickness(8, 6, 8, 6), FontSize = 13, BorderThickness = new Thickness(1) };
        input.SetResourceReference(Control.BackgroundProperty, "Surface");
        input.SetResourceReference(Control.ForegroundProperty, "Ink");
        input.SetResourceReference(Control.BorderBrushProperty, "Line");
        input.SetResourceReference(TextBox.SelectionBrushProperty, "Accent");
        AutomationProperties.SetName(input, name);
        return input;
    }

    static readonly Lazy<ControlTemplate> ChoiceTemplate = new(() => (ControlTemplate)XamlReader.Parse("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="{x:Type ComboBox}">
          <Grid>
            <ToggleButton x:Name="Toggle" Focusable="False" ClickMode="Press" BorderBrush="{DynamicResource Line}"
                          IsChecked="{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}">
              <ToggleButton.Template>
                <ControlTemplate TargetType="{x:Type ToggleButton}">
                  <Border Background="{DynamicResource Surface}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1" CornerRadius="4" Padding="9,6">
                    <TextBlock Text="▾" Foreground="{DynamicResource Ink}" HorizontalAlignment="Right" VerticalAlignment="Center"/>
                  </Border>
                </ControlTemplate>
              </ToggleButton.Template>
            </ToggleButton>
            <ContentPresenter Margin="10,0,28,0" IsHitTestVisible="False" VerticalAlignment="Center"
                              Content="{TemplateBinding SelectionBoxItem}" ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}"/>
            <Popup x:Name="PART_Popup" Placement="Bottom" Focusable="False" AllowsTransparency="True"
                   IsOpen="{TemplateBinding IsDropDownOpen}">
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

    static ComboBox Choice(string name, string[] choices)
    {
        var combo = new ComboBox { MinHeight = 38, MinWidth = 160, HorizontalAlignment = HorizontalAlignment.Left, Template = ChoiceTemplate.Value };
        combo.SetResourceReference(Control.ForegroundProperty, "Ink");
        combo.SetResourceReference(Control.BackgroundProperty, "Surface");
        var style = new Style(typeof(ComboBoxItem));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(9, 7, 9, 7)));
        style.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("Ink")));
        style.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("Surface")));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
        style.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(ComboBoxItem)) { VisualTree = border }));
        var highlighted = new Trigger { Property = ComboBoxItem.IsHighlightedProperty, Value = true };
        highlighted.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("Hover")));
        style.Triggers.Add(highlighted);
        combo.ItemContainerStyle = style;
        foreach (var text in choices) combo.Items.Add(new ComboBoxItem { Content = text });
        combo.SelectedIndex = 0;
        AutomationProperties.SetName(combo, name);
        return combo;
    }

    static ComboBox PriorityChoice(string name)
    {
        var combo = Choice(name, new[] { "常规", "优先", "最高" });
        for (var i = 0; i < 3; i++) ((ComboBoxItem)combo.Items[i]).Tag = i * 50;
        return combo;
    }

    static int Priority(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Tag is int value ? value : 0;
    static void SelectPriority(ComboBox combo, int priority)
    {
        foreach (ComboBoxItem item in combo.Items)
            if (item.Tag is int value && value == priority) { combo.SelectedItem = item; return; }
        while (combo.Items.Count > 3) combo.Items.RemoveAt(3);
        var custom = new ComboBoxItem { Content = $"自定义 {priority}", Tag = priority };
        combo.Items.Add(custom);
        combo.SelectedItem = custom;
    }

    static Button Button(string content, string name)
    {
        var button = new Button { Content = content, MinHeight = 38, Margin = new Thickness(0, 0, 8, 8) };
        AutomationProperties.SetName(button, name);
        AnimationHelper.AttachButtonHover(button);
        return button;
    }

    static StackPanel Card() => new() { Margin = new Thickness(12, 10, 12, 10) };
    static Border Frame(UIElement child)
    {
        var frame = new Border { Child = child, CornerRadius = new CornerRadius(7), BorderThickness = new Thickness(1), Margin = new Thickness(0, 10, 0, 0) };
        frame.SetResourceReference(Border.BackgroundProperty, "Surface");
        frame.SetResourceReference(Border.BorderBrushProperty, "Line");
        return frame;
    }

    sealed class TaskRow : INotifyPropertyChanged
    {
        public Guid Id { get; }
        public string Title { get; private set; } = "";
        public string Caption { get; private set; } = "";
        public WorkItemState State { get; private set; }
        public string AccessibleName => Title + "，" + Caption;
        public event PropertyChangedEventHandler? PropertyChanged;
        public TaskRow(ControlWorkItem task) { Id = task.Id; Update(task); }
        public void Update(ControlWorkItem task)
        {
            var priority = task.Priority switch { 0 => "常规", 50 => "优先", 100 => "最高", _ => $"优先级 {task.Priority}" };
            var caption = StateLabel(task.State.ToString()) + " · " + priority + " · " + task.UpdatedAt.ToLocalTime().ToString("MM-dd HH:mm");
            if (Title == task.Title && Caption == caption) return;
            State = task.State;
            Title = task.Title;
            Caption = caption;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Caption)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(State)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AccessibleName)));
        }
    }

    sealed record TaskEditDraft(string Title, string Notes, int Priority);
}

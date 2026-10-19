using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CodexQuotaWidget;

/// <summary>
/// Edits a saved local action plan without starting it. Refreshes only update runtime status;
/// user drafts remain intact. The explicit test button can only call the dry-run controller.
/// </summary>
public sealed class AutomationSettingsPanel : UserControl, IDisposable
{
    readonly IAutomationUiController? controller;
    readonly CheckBox enabled = new() { Content = "确认额度耗尽后执行本地收尾", Margin = new Thickness(0, 8, 0, 8), MinHeight = 36 };
    readonly TextBox input = Input("额度耗尽后的操作 / 关机前操作", false);
    readonly TextBlock status = Text("", 12, true);
    readonly TextBlock feedback = Text("", 12);
    readonly StackPanel steps = new();
    readonly Button save = Button("保存操作计划", "解析并保存本地操作计划，不立即执行");
    readonly Button test = Button("测试关机前操作", "仅模拟检查，不关闭程序、不改变服务、不执行关机");
    readonly Button reset = Button("恢复已保存内容", "放弃未保存的操作计划草稿");
    readonly Button history = Button("查看上次执行", "读取本地最后一次自动收尾记录");
    readonly TextBox historyText = Input("上次本地执行记录", true);
    readonly Expander historyPanel = new() { Header = "上次执行记录", Visibility = Visibility.Collapsed, Margin = new Thickness(0, 10, 0, 0) };
    readonly Expander preview = new() { Header = "本地操作顺序", IsExpanded = true, Margin = new Thickness(0, 8, 0, 0) };
    bool loading, dirty, busy, disposed;
    int lifetime;
    string savedText = "";
    bool savedEnabled;

    public AutomationSettingsPanel(IAutomationUiController? controller)
    {
        this.controller = controller;
        Name = "AutomationSettingsPanel";
        NameScope.SetNameScope(this, new NameScope());
        var named = new (FrameworkElement Element, string Name)[]
        {
            (enabled, "AutomationEnabled"), (input, "AutomationPlanText"), (save, "AutomationSave"),
            (test, "AutomationTest"), (reset, "AutomationReset"), (history, "AutomationHistory"),
            (feedback, "AutomationFeedback"), (status, "AutomationStatus"), (preview, "AutomationPreview"),
            (steps, "AutomationSteps"), (historyText, "AutomationHistoryText"), (historyPanel, "AutomationHistoryPanel")
        };
        foreach (var (element, name) in named) { element.Name = name; RegisterName(name, element); }
        AutomationProperties.SetName(this, "额度耗尽后的本地自动收尾设置");
        AutomationProperties.SetName(enabled, "仅在真实5小时额度耗尽后启用本地收尾");
        AutomationProperties.SetName(status, "本地收尾运行状态");
        AutomationProperties.SetName(feedback, "操作计划保存与测试结果");
        AutomationProperties.SetLiveSetting(feedback, AutomationLiveSetting.Polite);
        SetResourceReference(ForegroundProperty, "Ink");
        var body = new StackPanel { Margin = new Thickness(0, 12, 0, 12) };
        var title = Text("额度耗尽后的操作", 15); title.FontWeight = FontWeights.SemiBold;
        body.Children.Add(title);
        body.Children.Add(Text("仅在确认5小时额度真正耗尽后触发。重启时读到0%、断线或读取失败均不会直接执行。", 12, true));
        body.Children.Add(enabled);
        body.Children.Add(status);
        var label = new Label { Content = "关机前操作", Target = input, Padding = new Thickness(0, 8, 0, 4) };
        label.SetResourceReference(ForegroundProperty, "Ink"); body.Children.Add(label);
        input.MinHeight = 96; input.MaxHeight = 180; input.MaxLength = 2000;
        AutomationProperties.SetHelpText(input, "例如：关闭Clash和mihomo，然后关机。保存时转换为白名单操作；无法识别的内容不会保存。");
        body.Children.Add(input);
        body.Children.Add(Text("例如：关闭Clash和mihomo，然后关机。只支持本地白名单操作；保存不会执行。", 12, true));
        var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        save.FontWeight = FontWeights.SemiBold; save.SetResourceReference(System.Windows.Controls.Button.BorderBrushProperty, "Accent");
        foreach (var button in new[] { save, test, reset }) actions.Children.Add(button);
        body.Children.Add(actions);
        feedback.Visibility = Visibility.Collapsed; feedback.Margin = new Thickness(0, 6, 0, 0); body.Children.Add(feedback);
        preview.Content = steps; preview.SetResourceReference(ForegroundProperty, "Ink"); body.Children.Add(preview);
        body.Children.Add(Text("测试仅检查计划、目标、恢复文件与独立Executor。不会关闭程序、停止服务或执行电源操作。", 12, true));
        history.HorizontalAlignment = HorizontalAlignment.Left; history.Margin = new Thickness(0, 10, 0, 0); body.Children.Add(history);
        historyText.MinHeight = 96; historyText.MaxHeight = 220; historyText.Text = "暂无执行记录。";
        historyPanel.Content = historyText; historyPanel.SetResourceReference(ForegroundProperty, "Ink"); body.Children.Add(historyPanel);
        var frame = new Border { Padding = new Thickness(12), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Child = body };
        frame.SetResourceReference(Border.BackgroundProperty, "Surface"); frame.SetResourceReference(Border.BorderBrushProperty, "Line"); Content = frame;
        input.TextChanged += (_, _) => DraftChanged();
        enabled.Checked += (_, _) => DraftChanged(); enabled.Unchecked += (_, _) => DraftChanged();
        save.Click += (_, _) => SaveDraft();
        test.Click += async (_, _) => await TestDraftAsync();
        reset.Click += (_, _) => LoadSaved();
        history.Click += (_, _) => ShowHistory();
        PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control && save.IsEnabled)
            { SaveDraft(); args.Handled = true; }
        };
        LoadSaved();
    }

    static TextBlock Text(string value, double size, bool muted = false)
    {
        var block = new TextBlock { Text = value, FontSize = size, FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) };
        block.SetResourceReference(TextBlock.ForegroundProperty, muted ? "Muted" : "Ink"); return block;
    }
    static TextBox Input(string name, bool readOnly)
    {
        var box = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, IsReadOnly = readOnly, FontSize = 13, Padding = new Thickness(9), BorderThickness = new Thickness(1) };
        box.SetResourceReference(TextBox.ForegroundProperty, "Ink"); box.SetResourceReference(TextBox.BackgroundProperty, "Surface");
        box.SetResourceReference(TextBox.BorderBrushProperty, "Line"); AutomationProperties.SetName(box, name); return box;
    }
    static Button Button(string value, string name)
    {
        var button = new Button { Content = value, MinHeight = 40, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 8, 8) };
        AutomationProperties.SetName(button, name); return button;
    }
    void DraftChanged()
    {
        if (loading || disposed) return;
        dirty = input.Text != savedText || (enabled.IsChecked == true) != savedEnabled;
        if (dirty) ShowFeedback("有未保存的修改；保存后才替换本地操作计划。", false);
        UpdateAvailability();
    }
    void ShowFeedback(string value, bool error)
    {
        feedback.Text = value; feedback.Visibility = Visibility.Visible;
        feedback.SetResourceReference(TextBlock.ForegroundProperty, error ? "Danger" : "Ink");
    }
    void ShowSteps(IReadOnlyList<string>? values)
    {
        steps.Children.Clear();
        if (values is null || values.Count == 0) { steps.Children.Add(Text("保存后显示已验证的操作顺序。", 12, true)); return; }
        for (var i = 0; i < values.Count; i++) steps.Children.Add(Text($"{i + 1}. {values[i]}", 12));
    }
    void LoadSaved()
    {
        if (disposed || busy) return;
        loading = true;
        try
        {
            var state = controller?.GetState() ?? new AutomationUiState(false, "关闭Clash和mihomo，然后关机", "预览模式：独立本地执行器未连接。");
            savedEnabled = state.Enabled; savedText = state.Text; input.Text = savedText; enabled.IsChecked = savedEnabled;
            status.Text = state.Status; ShowSteps(state.Steps); dirty = false;
            feedback.Text = ""; feedback.Visibility = Visibility.Collapsed;
        }
        catch (Exception) { ShowFeedback("无法读取已保存计划；未改变当前配置。", true); }
        finally { loading = false; UpdateAvailability(); }
    }
    public void RefreshState()
    {
        if (disposed || controller is null) return;
        if (!Dispatcher.CheckAccess()) { _ = Dispatcher.InvokeAsync(RefreshState); return; }
        try
        {
            var state = controller.GetState();
            status.Text = state.Status;
            // Do not replace a draft, keyboard selection, preview or async test result on quota ticks.
            if (!dirty && !busy && (state.Text != savedText || state.Enabled != savedEnabled)) LoadSaved();
        }
        catch (Exception) { status.Text = "本地收尾状态暂不可用，当前操作计划未改变。"; }
    }
    void SaveDraft()
    {
        if (disposed || busy || controller is null) return;
        try
        {
            var result = controller.Save(enabled.IsChecked == true, input.Text);
            ShowFeedback(result.Success ? result.Message : result.Message + SavedSwitchWarning(), !result.Success);
            if (result.Success)
            {
                savedText = input.Text; savedEnabled = enabled.IsChecked == true; dirty = false;
                ShowSteps(result.Steps); status.Text = controller.GetState().Status;
            }
            else ShowSteps(Array.Empty<string>());
        }
        catch (Exception) { ShowFeedback("操作计划未保存，原配置保留。请检查本地存储状态。" + SavedSwitchWarning(), true); }
        UpdateAvailability();
    }
    string SavedSwitchWarning()
    {
        var active = savedEnabled;
        try { if (controller is not null) active = controller.GetState().Enabled; }
        catch (Exception) { }
        return active ? " 已保存的自动收尾仍开启；未保存的草稿不改变执行状态。" : " 已保存的自动收尾仍关闭；未保存的草稿不改变执行状态。";
    }
    async Task TestDraftAsync()
    {
        if (disposed || busy || controller is null) return;
        busy = true; UpdateAvailability(); test.Content = "正在模拟检查…";
        ShowFeedback("正在检查本地计划；不会关闭程序或执行电源操作。", false);
        var run = lifetime;
        try
        {
            var result = await controller.TestAsync(input.Text);
            if (!disposed && run == lifetime) ShowFeedback(result, false);
        }
        catch (Exception) { if (!disposed && run == lifetime) ShowFeedback("模拟检查未完成，未执行任何关机操作。请查看本地日志。", true); }
        finally
        {
            if (!disposed && run == lifetime) { busy = false; test.Content = "测试关机前操作"; UpdateAvailability(); }
        }
    }
    void ShowHistory()
    {
        if (disposed || busy || controller is null) return;
        try { historyText.Text = controller.ReadLastHistory(); }
        catch (Exception) { historyText.Text = "暂时无法读取本地执行记录。"; }
        historyPanel.Visibility = Visibility.Visible; historyPanel.IsExpanded = true;
    }
    void UpdateAvailability()
    {
        var editable = controller is not null && !busy && !disposed;
        enabled.IsEnabled = editable; input.IsEnabled = editable; save.IsEnabled = editable;
        test.IsEnabled = editable; reset.IsEnabled = editable && dirty; history.IsEnabled = editable;
    }
    public void Dispose() { disposed = true; lifetime++; UpdateAvailability(); }
    internal bool BusyForCheck => busy;
    internal bool DirtyForCheck => dirty;
    internal int StepCountForCheck => steps.Children.Count;
}

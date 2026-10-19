using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Markup;

namespace CodexQuotaWidget;

/// <summary>Observation and recovery display only. No control capable of dispatching tasks or executing a pending action.</summary>
public sealed class ReliabilityPanel : UserControl, IDisposable
{
    readonly IReliabilityUiController? controller;
    readonly TextBlock codexState = Text("未知", 15);
    readonly TextBlock source = Text("尚无可信状态来源", 12, true);
    readonly TextBlock detail = Text("", 12, true);
    readonly TextBlock observed = Text("", 11, true);
    readonly TextBlock recovery = Text("", 13);
    readonly TextBlock recoveryDetail = Text("", 12, true);
    readonly TextBlock feedback = Text("", 12);
    readonly Button continueRecovery = Button("继续恢复", "确认查看恢复上下文，不开始任务或重放收尾计划");
    readonly Button logs = Button("查看日志", "只打开本地已验证的 D 盘日志目录");
    readonly Button diagnostics = Button("Automation Diagnostics · Dry Run", "模拟额度、计划、Executor与恢复检查；不关闭程序、停止服务或执行电源操作");
    readonly Border recoveryFrame = new() { Padding = new Thickness(10), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Margin = new Thickness(0, 12, 0, 0) };
    readonly TabControl tabs = new() { Margin = new Thickness(0, 8, 0, 0), BorderThickness = new Thickness(0) };
    readonly SessionBindingPanel binding;
    readonly SessionDiagnosticsPanel sessionDiagnostics = new();
    ReliabilityUiState lastState = new();
    bool busy, disposed, needsAttention, acknowledged;
    int lifetime;

    public ReliabilityPanel(IReliabilityUiController? controller)
    {
        this.controller = controller;
        binding = new SessionBindingPanel(controller);
        Name = "ReliabilityPanel"; NameScope.SetNameScope(this, new NameScope());
        foreach (var (element, name) in new (FrameworkElement, string)[]
        {
            (codexState, "ReliabilityCodexState"), (source, "ReliabilityStateSource"), (detail, "ReliabilityStateDetail"),
            (observed, "ReliabilityObservedAt"), (recovery, "ReliabilityRecovery"), (recoveryDetail, "ReliabilityRecoveryDetail"),
            (continueRecovery, "ReliabilityContinue"), (logs, "ReliabilityLogs"), (diagnostics, "ReliabilityDiagnostics"),
            (feedback, "ReliabilityFeedback"), (recoveryFrame, "ReliabilityRecoveryFrame"), (tabs, "ReliabilityTabs"),
            (binding, "ReliabilityBindingPanel"), (sessionDiagnostics, "ReliabilitySessionDiagnostics")
        }) { element.Name = name; RegisterName(name, element); }
        AutomationProperties.SetName(this, "真实 Codex 状态、启动恢复与安全诊断");
        AutomationProperties.SetName(codexState, "观察到的真实 Codex 状态");
        AutomationProperties.SetName(recovery, "额度小组件启动恢复状态");
        AutomationProperties.SetName(feedback, "恢复及模拟诊断结果");
        AutomationProperties.SetLiveSetting(feedback, AutomationLiveSetting.Polite);
        SetResourceReference(ForegroundProperty, "Ink");
        var body = new StackPanel();
        var heading = Text("运行状态与恢复", 15); heading.FontWeight = FontWeights.SemiBold; body.Children.Add(heading);
        body.Children.Add(codexState); body.Children.Add(source); body.Children.Add(detail); body.Children.Add(observed);
        var recoveryBody = new StackPanel(); recoveryBody.Children.Add(recovery); recoveryBody.Children.Add(recoveryDetail);
        recoveryBody.Children.Add(Text("仅恢复显示上下文；不会自动开始本地任务或执行遗留收尾操作。", 12, true));
        var recoveryActions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        recoveryActions.Children.Add(continueRecovery); recoveryActions.Children.Add(logs); recoveryBody.Children.Add(recoveryActions);
        recoveryFrame.Child = recoveryBody; recoveryFrame.SetResourceReference(Border.BorderBrushProperty, "Line"); body.Children.Add(recoveryFrame);
        diagnostics.Margin = new Thickness(0, 12, 0, 4); diagnostics.HorizontalAlignment = HorizontalAlignment.Left;
        body.Children.Add(diagnostics);
        body.Children.Add(Text("默认 Dry Run：检查额度判断、计划、独立 Executor、Clash/mihomo 识别及恢复文件。没有真实关机、程序关闭或服务停止。", 12, true));
        feedback.Visibility = Visibility.Collapsed; feedback.Margin = new Thickness(0, 8, 0, 0); body.Children.Add(feedback);
        var diagnosisBody = new StackPanel(); diagnosisBody.Children.Add(binding); diagnosisBody.Children.Add(sessionDiagnostics);
        tabs.Items.Add(new TabItem { Header = "状态与恢复", Content = body });
        tabs.Items.Add(new TabItem { Header = "状态诊断", Content = diagnosisBody });
        foreach (TabItem tab in tabs.Items) { tab.Template = TabTemplate.Value; tab.MinHeight = 40; tab.SetResourceReference(Control.ForegroundProperty, "Ink"); }
        tabs.SetResourceReference(TabControl.ForegroundProperty, "Ink"); tabs.SetResourceReference(TabControl.BackgroundProperty, "Surface");
        var frame = new Border { Padding = new Thickness(12), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Child = tabs };
        frame.SetResourceReference(Border.BackgroundProperty, "Surface"); frame.SetResourceReference(Border.BorderBrushProperty, "Line"); Content = frame;
        continueRecovery.Click += (_, _) => Continue();
        logs.Click += (_, _) => OpenLogs();
        diagnostics.Click += async (_, _) => await RunDiagnosticsAsync();
        binding.CatalogBusyChanged += UpdateAvailability;
        RefreshState();
    }

    static TextBlock Text(string text, double size, bool muted = false)
    {
        var block = new TextBlock { Text = text, FontSize = size, FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 3) };
        block.SetResourceReference(TextBlock.ForegroundProperty, muted ? "Muted" : "Ink"); return block;
    }
    static Button Button(string text, string description)
    {
        var button = new Button { Content = text, MinHeight = 40, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 8, 4) };
        AutomationProperties.SetName(button, description); return button;
    }
    static readonly Lazy<ControlTemplate> TabTemplate = new(() => (ControlTemplate)XamlReader.Parse("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="{x:Type TabItem}">
          <Border x:Name="Frame" Background="{DynamicResource Surface}" BorderBrush="{DynamicResource Line}" BorderThickness="1" CornerRadius="4" Padding="10,7" Margin="0,0,6,4">
            <ContentPresenter ContentSource="Header" HorizontalAlignment="Center" VerticalAlignment="Center"/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsSelected" Value="True"><Setter TargetName="Frame" Property="Background" Value="{DynamicResource Hover}"/><Setter TargetName="Frame" Property="BorderBrush" Value="{DynamicResource Accent}"/></Trigger>
            <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Frame" Property="BorderBrush" Value="{DynamicResource Accent}"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
        """));
    public void RefreshState()
    {
        if (disposed) return;
        if (!Dispatcher.CheckAccess()) { _ = Dispatcher.InvokeAsync(RefreshState); return; }
        try
        {
            var state = controller?.GetState() ?? new ReliabilityUiState { StateDetail = "预览模式：真实状态观察器未连接。" };
            lastState = state;
            codexState.Text = "Codex · " + SessionDiagnosticsPanel.ExplainState(state.CodexState);
            source.Text = "来源：" + (string.IsNullOrWhiteSpace(state.StateSource) ? "未知" : state.StateSource);
            detail.Text = state.StateDetail; observed.Text = string.IsNullOrWhiteSpace(state.ObservedAt) ? "尚无有效观测时间" : "观测时间：" + state.ObservedAt;
            needsAttention = state.RecoveryNeedsAttention; acknowledged = state.RecoveryAcknowledged;
            recovery.Text = state.RecoverySummary; recoveryDetail.Text = state.RecoveryDetails;
            recovery.SetResourceReference(TextBlock.ForegroundProperty, needsAttention && !acknowledged ? "Danger" : "Ink");
            continueRecovery.Content = acknowledged ? "已确认恢复信息" : "继续恢复";
            sessionDiagnostics.Update(state); binding.RefreshState();
        }
        catch (Exception)
        {
            codexState.Text = "Codex · 未知"; source.Text = "来源：状态读取失败";
            detail.Text = "无法可靠确认执行状态，未触发任何自动操作。"; observed.Text = "尚无有效观测时间";
            recovery.Text = "恢复信息暂不可用；保留记录供检查。";
            recoveryDetail.Text = "当前读取失败，不能确认恢复记录完整性；未改变现有记录。";
            needsAttention = true; acknowledged = false;
            sessionDiagnostics.Update(lastState with { CodexState = "Unknown", StateSource = "状态读取失败", TrustLevel = "未知",
                EvidenceDescription = "0 · None · 当前状态读取失败", AgentAcknowledgement = "当前事件未核实；历史回执不表示本次已接收。",
                UnknownReason = "状态读取失败，显示上次已知绑定；未重新判断或清除已保存绑定。" });
            binding.RefreshState();
        }
        UpdateAvailability();
    }
    void Continue()
    {
        if (disposed || busy || binding.BusyForCheck || controller is null || !needsAttention || acknowledged) return;
        try { ShowFeedback(controller.ContinueRecovery(), false); RefreshState(); }
        catch (Exception) { ShowFeedback("恢复信息无法确认；未开始任务、未执行收尾计划。", true); }
    }
    void OpenLogs()
    {
        if (disposed || busy || binding.BusyForCheck || controller is null) return;
        try { ShowFeedback(controller.OpenLogs(), false); }
        catch (Exception) { ShowFeedback("本地日志无法打开；未访问其他路径。", true); }
    }
    async Task RunDiagnosticsAsync()
    {
        if (disposed || busy || binding.BusyForCheck || controller is null) return;
        busy = true; UpdateAvailability(); diagnostics.Content = "正在执行 Dry Run…";
        var run = lifetime; ShowFeedback("正在进行安全诊断；程序、服务和电源操作均不执行。", false);
        try
        {
            var result = await controller.RunDiagnosticsAsync();
            if (!disposed && lifetime == run) ShowFeedback(result, false);
        }
        catch (Exception) { if (!disposed && lifetime == run) ShowFeedback("Dry Run 未完成；未执行真实关闭或电源操作。", true); }
        finally
        {
            if (!disposed && lifetime == run)
            { busy = false; diagnostics.Content = "Automation Diagnostics · Dry Run"; RefreshState(); }
        }
    }
    void ShowFeedback(string message, bool error)
    {
        feedback.Text = message; feedback.Visibility = Visibility.Visible;
        feedback.SetResourceReference(TextBlock.ForegroundProperty, error ? "Danger" : "Ink");
    }
    void UpdateAvailability()
    {
        var available = controller is not null && !disposed && !busy && !binding.BusyForCheck;
        binding.SetExternalBusy(busy || disposed);
        continueRecovery.IsEnabled = available && needsAttention && !acknowledged;
        logs.IsEnabled = available; diagnostics.IsEnabled = available;
    }
    public void ShowStateDiagnostics() { if (disposed) return; tabs.SelectedIndex = 1; RefreshState(); }
    public void Dispose() { disposed = true; lifetime++; binding.CatalogBusyChanged -= UpdateAvailability; binding.Dispose(); UpdateAvailability(); }
    internal bool BusyForCheck => busy;
    internal SessionBindingPanel BindingPanelForCheck => binding;
    internal SessionDiagnosticsPanel SessionDiagnosticsForCheck => sessionDiagnostics;
}

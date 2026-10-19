using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Markup;

namespace CodexQuotaWidget;

/// <summary>Explicit project + session selection. Catalog reads are requested by a button, never by typing or polling.</summary>
public sealed class SessionBindingPanel : UserControl, IDisposable
{
    readonly IReliabilityUiController? controller;
    readonly TextBox project = new() { MinHeight = 40, FontSize = 13, Padding = new Thickness(8), BorderThickness = new Thickness(1), MaxLength = 1024 };
    readonly ComboBox sessions = new() { MinHeight = 44, IsEditable = false, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    readonly TextBlock saved = Text("尚未绑定真实 Session。", 12, true);
    readonly TextBlock selectedMetadata = Text("手动选择 Session 后显示其元数据路径与活动时间。", 12, true);
    readonly TextBlock feedback = Text("", 12);
    readonly Button refresh = Button("查找项目 Session", "只读取项目候选 Session 的元数据；不自动选择或保存");
    readonly Button save = Button("保存绑定", "保存明确选择的项目和 Session ID，不运行任务");
    readonly Button clear = Button("解除绑定", "显式解除状态观察绑定，不关闭任何程序");
    SessionBindingUiState savedState = new();
    IReadOnlyList<SessionChoiceUi> choices = Array.Empty<SessionChoiceUi>();
    string loadedProject = "", catalogProject = "";
    bool initialized, busy, externalBusy, disposed, loading;
    int lifetime;
    internal event Action? CatalogBusyChanged;

    public SessionBindingPanel(IReliabilityUiController? controller)
    {
        this.controller = controller; Name = "SessionBindingPanel"; NameScope.SetNameScope(this, new NameScope());
        foreach (var (element, name) in new (FrameworkElement, string)[]
        {
            (project, "BindingProjectInput"), (sessions, "BindingSessions"), (saved, "BindingSavedState"),
            (selectedMetadata, "BindingSelectedMetadata"), (feedback, "BindingFeedback"), (refresh, "BindingRefresh"),
            (save, "BindingSave"), (clear, "BindingClear")
        }) { element.Name = name; RegisterName(name, element); }
        AutomationProperties.SetName(this, "显式绑定项目与真实 Codex Session");
        AutomationProperties.SetName(project, "需要观察的项目绝对路径");
        AutomationProperties.SetName(sessions, "手动选择真实 Session，不默认选择最新项");
        AutomationProperties.SetName(feedback, "Session 查询和绑定结果");
        AutomationProperties.SetLiveSetting(feedback, AutomationLiveSetting.Polite);
        SetResourceReference(ForegroundProperty, "Ink");
        project.SetResourceReference(TextBox.ForegroundProperty, "Ink"); project.SetResourceReference(TextBox.BackgroundProperty, "Surface"); project.SetResourceReference(TextBox.BorderBrushProperty, "Line");
        sessions.SetResourceReference(ComboBox.ForegroundProperty, "Ink"); sessions.SetResourceReference(ComboBox.BackgroundProperty, "Surface"); sessions.SetResourceReference(ComboBox.BorderBrushProperty, "Line");
        sessions.Template = SessionChoiceTemplate.Value;
        var rowStyle = new Style(typeof(ComboBoxItem));
        rowStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 6, 8, 6)));
        rowStyle.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("Ink")));
        rowStyle.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("Surface")));
        var rowBorder = new FrameworkElementFactory(typeof(Border));
        rowBorder.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        rowBorder.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        rowBorder.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
        rowStyle.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(ComboBoxItem)) { VisualTree = rowBorder }));
        var highlighted = new Trigger { Property = ComboBoxItem.IsHighlightedProperty, Value = true };
        highlighted.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("Hover"))); rowStyle.Triggers.Add(highlighted);
        sessions.ItemContainerStyle = rowStyle;
        var item = new FrameworkElementFactory(typeof(TextBlock));
        item.SetBinding(TextBlock.TextProperty, new Binding(nameof(SessionChoiceUi.DisplayLabel)));
        item.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap); item.SetValue(TextBlock.MaxWidthProperty, 250d);
        item.SetValue(TextBlock.MarginProperty, new Thickness(3)); item.SetValue(TextBlock.FontSizeProperty, 12d);
        sessions.ItemTemplate = new DataTemplate(typeof(SessionChoiceUi)) { VisualTree = item };
        AutomationProperties.SetHelpText(sessions, "只保存选中 Session ID；不会接受任意日志文件路径。");
        var body = new StackPanel();
        var title = Text("项目与 Session 绑定", 14); title.FontWeight = FontWeights.SemiBold; body.Children.Add(title);
        body.Children.Add(Text("填写项目路径，查询候选后手动选择真实 Session。仅填写项目不会自动绑定最新日志。", 12, true));
        body.Children.Add(saved);
        var projectLabel = new Label { Content = "项目路径", Target = project, Padding = new Thickness(0, 8, 0, 4) };
        projectLabel.SetResourceReference(ForegroundProperty, "Ink"); body.Children.Add(projectLabel); body.Children.Add(project);
        refresh.HorizontalAlignment = HorizontalAlignment.Left; refresh.Margin = new Thickness(0, 8, 0, 8); body.Children.Add(refresh);
        var sessionLabel = new Label { Content = "真实 Session", Target = sessions, Padding = new Thickness(0, 4, 0, 4) };
        sessionLabel.SetResourceReference(ForegroundProperty, "Ink"); body.Children.Add(sessionLabel); body.Children.Add(sessions); body.Children.Add(selectedMetadata);
        var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) }; actions.Children.Add(save); actions.Children.Add(clear); body.Children.Add(actions);
        feedback.Visibility = Visibility.Collapsed; body.Children.Add(feedback);
        Content = body;
        project.TextChanged += (_, _) =>
        {
            if (loading || disposed) return;
            if (!SameProject(project.Text.Trim(), catalogProject))
            { choices = Array.Empty<SessionChoiceUi>(); sessions.ItemsSource = choices; selectedMetadata.Text = "项目路径已改变，请重新查询并手动选择 Session。"; }
            ShowFeedback("草稿未保存；当前已保存绑定仍有效。", false); UpdateAvailability();
        };
        sessions.SelectionChanged += (_, _) =>
        {
            if (disposed) return;
            if (sessions.SelectedItem is SessionChoiceUi selected)
                selectedMetadata.Text = $"已选择：{selected.SessionId}\n元数据：{selected.MetadataPath}\n活动：{Value(selected.LastActivity)}\n最后事件：{Value(selected.LastEvent)}";
            else selectedMetadata.Text = "没有选择 Session；不会自动绑定列表中最新的一项。";
            UpdateAvailability();
        };
        refresh.Click += async (_, _) => await RefreshCatalogAsync(); save.Click += (_, _) => Save(); clear.Click += (_, _) => Clear();
        RefreshState();
    }

    static TextBlock Text(string text, double size, bool muted = false)
    {
        var block = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 3), FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI") };
        block.SetResourceReference(TextBlock.ForegroundProperty, muted ? "Muted" : "Ink"); return block;
    }
    static Button Button(string text, string hint)
    {
        var button = new Button { Content = text, MinHeight = 40, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 8, 4) };
        AutomationProperties.SetName(button, hint); return button;
    }
    static string Value(string text) => string.IsNullOrWhiteSpace(text) ? "未知" : text;
    // Same theme-aware choice structure as the existing local task panel, without its default selection.
    static readonly Lazy<ControlTemplate> SessionChoiceTemplate = new(() => (ControlTemplate)XamlReader.Parse("""
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
    static bool SameProject(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    public void RefreshState()
    {
        if (disposed) return;
        if (!Dispatcher.CheckAccess()) { _ = Dispatcher.InvokeAsync(RefreshState); return; }
        try
        {
            var state = controller?.GetBindingState() ?? new SessionBindingUiState { Status = "预览模式：Session 绑定未连接。" };
            DisplaySaved(state);
            if (!initialized)
            {
                initialized = true; loading = true; loadedProject = state.ProjectRoot; project.Text = loadedProject; loading = false;
            }
        }
        catch (Exception) { saved.Text = "已保存绑定暂不可读取；输入草稿和已选 Session 保留。"; }
        UpdateAvailability();
    }
    void DisplaySaved(SessionBindingUiState state)
    {
        savedState = state;
        saved.Text = state.IsBound ? $"当前绑定项目：{state.ProjectRoot}\nSession：{state.SessionId}\n元数据：{state.MetadataPath}\n{state.Status}" : state.Status;
    }
    async Task RefreshCatalogAsync()
    {
        if (disposed || busy || externalBusy || controller is null || string.IsNullOrWhiteSpace(project.Text)) return;
        var requestProject = project.Text.Trim(); var selectedId = (sessions.SelectedItem as SessionChoiceUi)?.SessionId; var run = lifetime;
        busy = true; refresh.Content = "正在读取 Session…"; UpdateAvailability();
        CatalogBusyChanged?.Invoke();
        ShowFeedback("正在读取候选元数据；不会自动选择 Session。", false);
        try
        {
            var result = await controller.RefreshSessionsAsync(requestProject);
            if (disposed || lifetime != run) return;
            if (!SameProject(project.Text.Trim(), requestProject)) { ShowFeedback("项目草稿已变化；忽略旧查询结果。", false); return; }
            if (!result.Success)
            { choices = Array.Empty<SessionChoiceUi>(); sessions.ItemsSource = choices; catalogProject = ""; ShowFeedback(result.Message, true); return; }
            catalogProject = requestProject;
            choices = result.Choices.Where(choice => !string.IsNullOrWhiteSpace(choice.SessionId)).DistinctBy(choice => choice.SessionId).Take(200).ToArray();
            sessions.ItemsSource = choices;
            // Preserve an explicit prior choice only. Never take First/Last/the newest catalog item.
            sessions.SelectedItem = selectedId is null ? null : choices.FirstOrDefault(choice => choice.SessionId == selectedId);
            ShowFeedback(choices.Count == 0 ? result.Message + " 未找到候选；现有绑定保持不变。" : result.Message + " 请手动选择后保存。", false);
        }
        catch (Exception)
        {
            if (!disposed && lifetime == run)
            { choices = Array.Empty<SessionChoiceUi>(); sessions.ItemsSource = choices; catalogProject = ""; ShowFeedback("Session 查询失败；没有修改已保存绑定。", true); }
        }
        finally
        {
            if (!disposed && lifetime == run) { busy = false; refresh.Content = "查找项目 Session"; UpdateAvailability(); CatalogBusyChanged?.Invoke(); }
        }
    }
    void Save()
    {
        if (disposed || busy || externalBusy || controller is null || sessions.SelectedItem is not SessionChoiceUi selected
            || !SameProject(project.Text.Trim(), catalogProject) || !choices.Any(choice => choice.SessionId == selected.SessionId)) return;
        try
        {
            var result = controller.SaveBinding(project.Text.Trim(), selected.SessionId);
            DisplaySaved(result.State); ShowFeedback(result.Message, !result.Success);
            if (result.Success)
            { loading = true; loadedProject = result.State.ProjectRoot; project.Text = loadedProject; catalogProject = loadedProject; loading = false; }
        }
        catch (Exception) { ShowFeedback("绑定未保存；原绑定、项目草稿和所选 Session 保留。", true); }
        UpdateAvailability();
    }
    void Clear()
    {
        if (disposed || busy || externalBusy || controller is null || !savedState.IsBound) return;
        try { var result = controller.ClearBinding(); DisplaySaved(result.State); ShowFeedback(result.Message, !result.Success); }
        catch (Exception) { ShowFeedback("解除绑定失败；原绑定保持不变。", true); }
        UpdateAvailability();
    }
    void ShowFeedback(string text, bool error)
    {
        feedback.Text = text; feedback.Visibility = Visibility.Visible;
        feedback.SetResourceReference(TextBlock.ForegroundProperty, error ? "Danger" : "Ink");
    }
    void UpdateAvailability()
    {
        var editable = controller is not null && !disposed && !busy && !externalBusy;
        project.IsEnabled = editable; sessions.IsEnabled = editable && choices.Count > 0;
        refresh.IsEnabled = editable && !string.IsNullOrWhiteSpace(project.Text);
        save.IsEnabled = editable && sessions.SelectedItem is SessionChoiceUi selected && SameProject(project.Text.Trim(), catalogProject)
            && choices.Any(choice => choice.SessionId == selected.SessionId);
        clear.IsEnabled = editable && savedState.IsBound;
    }
    internal void SetExternalBusy(bool value) { externalBusy = value; UpdateAvailability(); }
    internal bool BusyForCheck => busy;
    public void Dispose() { disposed = true; lifetime++; UpdateAvailability(); }
}

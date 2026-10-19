using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Markup;
using CodexQuotaWidget.Core.ControlCenter;

namespace CodexQuotaWidget;

/// <summary>Edits a task draft only. Saving never connects to or dispatches work to either model.</summary>
public sealed class TaskEditorDialog : Window
{
    readonly WorkflowLoopConfiguration original;
    readonly Guid taskId;
    readonly TextBox goal = Input("WorkflowTaskGoal", "任务目标", true);
    readonly TextBox project = Input("WorkflowTaskProject", "项目路径");
    readonly TextBox writePaths = Input("WorkflowTaskWritePaths", "允许修改的文件或目录，项目内相对路径，一行一项", true);
    readonly TextBox target = Input("WorkflowTaskGptSession", "GPT会话ID");
    readonly TextBox caller = Input("WorkflowTaskCodexSession", "Codex控制会话ID");
    readonly TextBox buildProject = Input("WorkflowTaskBuildProject", "本地验证项目，项目内相对csproj路径");
    readonly TextBox checksProject = Input("WorkflowTaskChecksProject", "检查项目，项目内相对csproj路径");
    readonly TextBlock feedback = Copy("");
    readonly Expander advanced;
    readonly ComboBox backend = new() { Name = "WorkflowTaskBackend", MinHeight = 44 };
    readonly CheckBox experimental = new() { Name = "WorkflowTaskSiwcEnabled", Content = "为新任务启用 SIWC 实验模式（当前仅离线准备）", MinHeight = 44 };
    readonly StackPanel siwcSettings = new();
    readonly TextBox advisorPrompt = Input("WorkflowTaskAdvisorPrompt", "独立建议角色Prompt", true);
    readonly TextBox reviewerPrompt = Input("WorkflowTaskReviewerPrompt", "独立复核角色Prompt", true);
    readonly bool isNewTask;
    public WorkflowLoopConfiguration? Result { get; private set; }

    public TaskEditorDialog(WorkflowLoopConfiguration configuration, bool isNew)
    {
        original = configuration; isNewTask = isNew; taskId = isNew || configuration.TaskId == Guid.Empty ? Guid.NewGuid() : configuration.TaskId;
        Title = isNew ? "新建任务" : "编辑任务";
        Name = "WorkflowTaskEditor"; Width = 580; Height = 640; MinWidth = 440; MinHeight = 460;
        MaxHeight = Math.Max(460, SystemParameters.WorkArea.Height - 24);
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "Glass"); SetResourceReference(ForegroundProperty, "Ink");
        var root = new DockPanel { Margin = new Thickness(22) };
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = new Button { Content = "取消", IsCancel = true, MinHeight = 44, MinWidth = 76, Margin = new Thickness(0, 0, 8, 0) };
        var save = new Button { Content = "保存任务", IsDefault = true, Name = "WorkflowTaskSave", MinHeight = 44, MinWidth = 100 };
        AutomationProperties.SetName(cancel, "取消任务编辑，不发送任务"); AutomationProperties.SetName(save, "保存任务草稿，不启动执行");
        save.SetResourceReference(BorderBrushProperty, "Accent");
        actions.Children.Add(cancel); actions.Children.Add(save); DockPanel.SetDock(actions, Dock.Bottom); root.Children.Add(actions);
        var body = new StackPanel();
        var heading = Copy(Title); heading.FontSize = 20; heading.FontWeight = FontWeights.SemiBold; body.Children.Add(heading);
        body.Children.Add(Copy("写清楚目标、选择项目，再确认允许修改的范围。", true));
        AddField(body, "想完成什么？", goal); goal.MinHeight = 150; goal.MaxHeight = 300;
        goal.Text = isNew ? "" : configuration.Goal;
        project.Text = configuration.ProjectRoot; target.Text = configuration.ChatGptThreadId; caller.Text = configuration.CallerThreadId;
        var projectLabel = new Label { Content = "在哪个项目中执行？", Target = project, Padding = new Thickness(0, 12, 0, 4) };
        projectLabel.SetResourceReference(ForegroundProperty, "Ink"); body.Children.Add(projectLabel);
        var projectRow = new Grid(); projectRow.ColumnDefinitions.Add(new()); projectRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        projectRow.Children.Add(project);
        var browse = new Button { Name = "WorkflowTaskBrowseProject", Content = "选择文件夹", MinHeight = 44,
            Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(8, 0, 0, 0) };
        AutomationProperties.SetName(browse, "选择任务项目文件夹"); Grid.SetColumn(browse, 1); projectRow.Children.Add(browse); body.Children.Add(projectRow);
        browse.Click += (_, _) => ChooseProjectFolder();
        AddField(body, "允许修改的文件或目录（项目内相对路径，一行一项）", writePaths);
        writePaths.MinHeight = 88; writePaths.MaxHeight = 180;
        writePaths.Text = isNew ? "" : string.Join(Environment.NewLine, configuration.AuthorizedWritePaths);
        body.Children.Add(Copy("留空为只读任务，不能自动执行修改。目录以 / 结尾；保存后仍需勾选本地授权并点击开始。", true));
        var advancedBody = new StackPanel();
        backend.Items.Add("Legacy Desktop（兼容；正文可能不可读）");
        backend.Items.Add("SIWC GPT Reviewer（实验；第一阶段仅离线）");
        backend.SelectedIndex = !isNew && configuration.GptBackend == WorkflowBackend.SiwcGptReviewer ? 1 : 0;
        backend.IsEnabled = isNew || configuration.TaskId == Guid.Empty;
        AutomationProperties.SetName(backend, "GPT建议复核Backend；已有任务禁止迁移");
        advancedBody.Children.Add(Copy("GPT 建议 / 复核连接方式")); advancedBody.Children.Add(backend);
        advancedBody.Children.Add(Copy("Codex Executor：沿用官方执行连接器；独立于GPT账户和Session。已有任务不可迁移连接方式。", true));
        siwcSettings.Children.Add(experimental); experimental.IsChecked = !isNew && configuration.Siwc.Enabled;
        siwcSettings.Children.Add(Copy("SIWC 是应用自己的独立授权，不能读取旧Desktop聊天。第一阶段不启动登录、不请求模型；额度 Unknown 时禁止循环。", true));
        var login = new Button { Name = "WorkflowTaskSiwcLogin", Content = "Continue with ChatGPT（实验，待第二阶段批准）", MinHeight = 44, IsEnabled = false };
        AutomationProperties.SetHelpText(login, "真实账户授权尚未批准；此按钮不会打开浏览器或读取现有Codex登录。"); siwcSettings.Children.Add(login);
        siwcSettings.Children.Add(Copy(configuration.Siwc.AccountId.Length > 0 && !isNew ? "已绑定应用SIWC注册；本阶段不读取凭据、不验证在线状态。" : "账户：未授权 · 模型：尚未从账户目录选择 · SIWC额度：Unknown", true));
        advisorPrompt.Text = configuration.Siwc.AdvisorPrompt; reviewerPrompt.Text = configuration.Siwc.ReviewerPrompt;
        AddField(siwcSettings, "建议角色 Prompt（仅文本）", advisorPrompt); AddField(siwcSettings, "复核角色 Prompt（仅文本）", reviewerPrompt);
        advisorPrompt.MaxHeight = reviewerPrompt.MaxHeight = 120;
        advancedBody.Children.Add(siwcSettings);
        void RefreshBackend()
        {
            var selected = backend.SelectedIndex == 1;
            siwcSettings.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            target.IsEnabled = caller.IsEnabled = !selected;
        }
        backend.SelectionChanged += (_, _) => RefreshBackend(); RefreshBackend();
        AddField(advancedBody, "GPT 会话 ID", target); AddField(advancedBody, "Codex 控制会话 ID", caller);
        advancedBody.Children.Add(Copy("沿用现有会话配置。首次配置或更换会话时填写；连接时仍会核验身份，不会自动选择其他会话。", true));
        buildProject.Text = configuration.BuildProjectPath; checksProject.Text = configuration.ChecksProjectPath;
        AddField(advancedBody, "本地验证项目（项目内相对 .csproj 路径）", buildProject);
        AddField(advancedBody, "检查项目（项目内相对 .csproj 路径）", checksProject);
        advancedBody.Children.Add(Copy("修改任务必须明确指定这两个真实项目；程序不会自动选择默认项目或把未知项目当作验证通过。", true));
        advanced = DisclosureSection.Create("高级设置", advancedBody, "WorkflowTaskAdvanced");
        advanced.Margin = new Thickness(0, 12, 0, 8);
        body.Children.Add(advanced);
        feedback.Name = "WorkflowTaskFeedback";
        AutomationProperties.SetName(feedback, "任务编辑结果"); AutomationProperties.SetLiveSetting(feedback, AutomationLiveSetting.Polite);
        body.Children.Add(feedback);
        root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }); Content = root;
        Loaded += (_, _) => goal.Focus();
        save.Click += (_, _) =>
        {
            if (TrySaveDraft()) DialogResult = true;
        };
    }

    internal bool TrySaveDraft()
    {
        Result = null;
        if (string.IsNullOrWhiteSpace(goal.Text)) return ShowRequired(goal, "请填写任务目标。", false);
        if (string.IsNullOrWhiteSpace(project.Text)) return ShowRequired(project, "请选择或填写任务项目路径。", false);
        var useSiwc = backend.SelectedIndex == 1;
        if (!isNewTask && original.TaskId != Guid.Empty && useSiwc != (original.GptBackend == WorkflowBackend.SiwcGptReviewer))
        { feedback.Text = "已有任务不能迁移Backend，请新建独立任务。"; return false; }
        if (useSiwc && experimental.IsChecked != true) { feedback.Text = "请明确勾选实验模式，或保留Legacy Desktop。"; advanced.IsExpanded = true; return false; }
        if (!useSiwc && string.IsNullOrWhiteSpace(target.Text)) return ShowRequired(target, "请填写本任务的 GPT 会话 ID。", true);
        if (!useSiwc && string.IsNullOrWhiteSpace(caller.Text)) return ShowRequired(caller, "请填写本任务的 Codex 控制会话 ID。", true);
        if (useSiwc && (string.IsNullOrWhiteSpace(advisorPrompt.Text) || string.IsNullOrWhiteSpace(reviewerPrompt.Text)))
            return ShowRequired(advisorPrompt, "请填写独立建议和复核角色Prompt。", true);
        var enteredPaths = writePaths.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(value => value.Trim())
            .Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        string[] paths;
        try { paths = WorkflowAuthorization.NormalizeWritePaths(enteredPaths); }
        catch (WorkflowRpcException) { return ShowRequired(writePaths, "请填写项目内具体相对路径；目录以 / 结尾，不能包含根目录、上级路径、通配符或受保护目录。", false); }
        if (paths.Length > 0 && string.IsNullOrWhiteSpace(buildProject.Text))
            return ShowRequired(buildProject, "请填写本任务的本地验证项目相对路径。", true);
        if (paths.Length > 0 && string.IsNullOrWhiteSpace(checksProject.Text))
            return ShowRequired(checksProject, "请填写本任务的检查项目相对路径。", true);
        if (!string.IsNullOrWhiteSpace(buildProject.Text) && !ValidProjectPath(buildProject.Text.Trim()))
            return ShowRequired(buildProject, "本地验证项目必须填写项目内相对 .csproj 路径。", true);
        if (!string.IsNullOrWhiteSpace(checksProject.Text) && !ValidProjectPath(checksProject.Text.Trim()))
            return ShowRequired(checksProject, "检查项目必须填写项目内相对 .csproj 路径。", true);
        var options = useSiwc ? original.Siwc with { Enabled = true,
            AccountId = isNewTask ? "" : original.Siwc.AccountId, Model = isNewTask ? "" : original.Siwc.Model,
            AdviceSessionId = isNewTask || original.Siwc.AdviceSessionId.Length == 0 ? "siwc-advice-" + Guid.NewGuid().ToString("N") : original.Siwc.AdviceSessionId,
            ReviewSessionId = isNewTask || original.Siwc.ReviewSessionId.Length == 0 ? "siwc-review-" + Guid.NewGuid().ToString("N") : original.Siwc.ReviewSessionId,
            AdvisorPrompt = advisorPrompt.Text.Trim(), ReviewerPrompt = reviewerPrompt.Text.Trim() } : new SiwcWorkflowOptions();
        Result = original with { TaskId = taskId, GptBackend = useSiwc ? WorkflowBackend.SiwcGptReviewer : WorkflowBackend.LegacyDesktop, Siwc = options,
            Goal = goal.Text.Trim(), ProjectRoot = project.Text.Trim(), ChatGptThreadId = useSiwc ? "" : target.Text.Trim(),
            CallerThreadId = useSiwc ? "" : caller.Text.Trim(), AuthorizedWritePaths = paths, BuildProjectPath = buildProject.Text.Trim(),
            ChecksProjectPath = checksProject.Text.Trim(), LocalExecutionAuthorized = false };
        return true;
    }

    static bool ValidProjectPath(string value) => !Path.IsPathRooted(value) && !value.Contains(':')
        && value.IndexOfAny(new[] { '*', '?' }) < 0 && value.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
        && !value.Split(new[] { '/', '\\' }).Any(segment => segment is "." or ".." or "");

    bool ShowRequired(TextBox input, string message, bool revealSessions)
    {
        Result = null; feedback.Text = message;
        if (revealSessions) advanced.IsExpanded = true;
        UpdateLayout(); input.BringIntoView(); input.Focus(); return false;
    }

    void ChooseProjectFolder()
    {
        try
        {
            var picker = new Microsoft.Win32.OpenFolderDialog { Title = "选择任务项目", Multiselect = false };
            if (Directory.Exists(project.Text.Trim())) picker.InitialDirectory = project.Text.Trim();
            if (picker.ShowDialog(this) == true) { project.Text = picker.FolderName; feedback.Text = "项目已选择，保存后生效。"; }
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { feedback.Text = "暂时无法打开文件夹选择器，请直接填写项目路径。"; project.Focus(); }
    }

    static void AddField(Panel panel, string caption, TextBox input)
    {
        var label = new Label { Content = caption, Target = input, Padding = new Thickness(0, 8, 0, 4) };
        label.SetResourceReference(ForegroundProperty, "Ink"); panel.Children.Add(label); panel.Children.Add(input);
    }
    static TextBox Input(string name, string caption, bool multiline = false)
    {
        var input = new TextBox { Name = name, MinHeight = 44, Padding = new Thickness(8), FontSize = 13,
            MaxLength = multiline ? 4000 : 1024, AcceptsReturn = multiline, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled };
        input.Template = InputTemplate.Value; input.SetResourceReference(BackgroundProperty, "Surface");
        input.SetResourceReference(ForegroundProperty, "Ink"); input.SetResourceReference(BorderBrushProperty, "Line");
        input.SetResourceReference(TextBox.CaretBrushProperty, "Ink"); AutomationProperties.SetName(input, caption); return input;
    }
    static TextBlock Copy(string text, bool muted = false)
    {
        var block = new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) };
        block.SetResourceReference(TextBlock.ForegroundProperty, muted ? "Muted" : "Ink"); return block;
    }
    static readonly Lazy<ControlTemplate> InputTemplate = new(() => (ControlTemplate)XamlReader.Parse("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="TextBox">
          <Border x:Name="InputBorder" CornerRadius="5" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                  BorderThickness="1" Padding="{TemplateBinding Padding}"><ScrollViewer x:Name="PART_ContentHost"/></Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="InputBorder" Property="BorderBrush" Value="{DynamicResource Accent}"/></Trigger>
            <Trigger Property="IsEnabled" Value="False"><Setter TargetName="InputBorder" Property="Opacity" Value="0.6"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
        """));
}

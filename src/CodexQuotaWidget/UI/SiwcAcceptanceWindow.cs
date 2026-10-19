using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using CodexQuotaWidget.Core.ControlCenter;

namespace CodexQuotaWidget;

/// <summary>Explicit acceptance window; selections and sending require an authorized affirmative UI action.</summary>
internal sealed class SiwcAcceptanceWindow : Window
{
    readonly SiwcAcceptanceController controller;
    readonly Button login = Action("Continue with ChatGPT（实验）", "SiwcAcceptanceLogin");
    readonly Button reload = Action("读取已授权账户 / 刷新模型", "SiwcAcceptanceLoadAccount");
    readonly Button send = Action("确认并发送唯一一条测试", "SiwcAcceptanceSendOnce");
    readonly Button cancel = Action("取消当前操作", "SiwcAcceptanceCancel");
    readonly ComboBox models = new() { Name = "SiwcAcceptanceModels", MinHeight = 44, IsEditable = false, Margin = new Thickness(0, 4, 0, 8), DisplayMemberPath = "DisplayName" };
    readonly CheckBox billing = Confirm("我已查看官方用量设置；如仍存在费用不确定性，同意本次单条请求可能消耗账户已允许的积分。不会购买积分或使用API Key。", "SiwcAcceptanceBilling");
    readonly CheckBox quota = Confirm("精确SIWC额度仍为Unknown：仅为当前RequestId允许一次验收例外，正常循环的额度保护保持不变。", "SiwcAcceptanceQuotaException");
    readonly CheckBox confirm = Confirm("确认只发送下方固定短文本，不执行Codex任务，不开始两轮循环，不自动再发第二条。", "SiwcAcceptanceConfirm");
    readonly TextBlock account = Copy("账户：未授权"), message = Copy("尚未登录；没有发送模型请求。"), identity = Copy(""), result = Copy("");
    readonly TextBlock modelStatus = Copy("请先完成授权，再读取当前账户的模型列表。", true);
    readonly TextBox body = new() { Name = "SiwcAcceptanceActualBody", IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MinHeight = 68,
        MaxHeight = 180, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 8, 0, 12), Padding = new Thickness(10), Text = "尚未获得正文" };
    SiwcModelChoice[] lastModels = [];
    public SiwcAcceptanceWindow()
    {
        controller = new(); Title = "AI Control Center · SIWC 单条验收（独立实验）";
        Name = "SiwcAcceptanceWindow"; Width = 740; Height = 900; MinWidth = 560; MinHeight = 580;
        MaxHeight = Math.Max(580, SystemParameters.WorkArea.Height - 32); WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SetResourceReference(BackgroundProperty, "Glass"); SetResourceReference(ForegroundProperty, "Ink");
        const string icon = @"D:\codex\Tools\Icons\CodexQuotaWidget-20260924-223636.ico";
        if (File.Exists(icon)) Icon = BitmapFrame.Create(new Uri(icon));
        var root = new DockPanel { Margin = new Thickness(24) };
        var footer = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) }; footer.Children.Add(send); footer.Children.Add(cancel);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var content = new StackPanel(); var title = Copy("独立 GPT 通信验收"); title.FontSize = 24; title.FontWeight = FontWeights.SemiBold; content.Children.Add(title);
        content.Children.Add(Copy("第二阶段实验 · 只允许一个新RequestId、一条固定纯文本 · 正式版1.12.7保持运行", true));
        content.Children.Add(Heading("1. 亲自完成官方授权"));
        var authButtons = new WrapPanel(); authButtons.Children.Add(login); authButtons.Children.Add(reload); content.Children.Add(authButtons);
        content.Children.Add(account); content.Children.Add(Copy("独立应用OAuth；不导入Codex或ChatGPT Desktop令牌。模型目录可见不等于真实推理已成功。", true));
        content.Children.Add(Heading("2. 选择当前账户返回的模型")); content.Children.Add(models); content.Children.Add(modelStatus);
        content.Children.Add(Heading("3. 核对用量并逐项确认"));
        content.Children.Add(Copy("官方套餐可能允许使用积分。本工具无法从公开协议精确核对本次额度百分比、积分差额或金额，当前均为Unknown。如不接受这项不确定性，请不要发送。", true));
        var usage = Action("查看 ChatGPT 官方用量设置", "SiwcAcceptanceOpenUsage"); content.Children.Add(usage);
        usage.Click += (_, _) => OpenUsage();
        content.Children.Add(billing); content.Children.Add(quota); content.Children.Add(confirm);
        content.Children.Add(Copy("将发送：")); content.Children.Add(Card(Copy(SiwcSingleRequestAcceptance.Prompt)));
        content.Children.Add(Copy("严格比对目标：" + SiwcSingleRequestAcceptance.ExpectedBody, true));
        identity.Name = "SiwcAcceptanceIdentity"; content.Children.Add(identity);
        message.Name = "SiwcAcceptanceMessage"; AutomationProperties.SetLiveSetting(message, AutomationLiveSetting.Polite); content.Children.Add(Card(message));
        content.Children.Add(Heading("真实返回正文")); content.Children.Add(body); content.Children.Add(result);
        content.Children.Add(Copy("证据保存在D盘独立验收目录。请求一旦尝试，即使断流、关闭窗口或重开程序也不会重发。单条成功不代表自动循环成功。", true));
        root.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }); Content = root;
        models.Template = WorkflowLoopPanel.ChoiceControlTemplate;
        models.SetResourceReference(BackgroundProperty, "Surface"); models.SetResourceReference(ForegroundProperty, "Ink");
        body.SetResourceReference(BackgroundProperty, "Surface"); body.SetResourceReference(ForegroundProperty, "Ink");
        login.Click += async (_, _) => { ClearConsents(); await controller.SignInAsync(); Refresh(); };
        reload.Click += async (_, _) => { ClearConsents(); await controller.LoadSavedAsync(); Refresh(); };
        models.SelectionChanged += (_, _) => { ClearConsents(); RefreshButtons(); };
        foreach (var check in new[] { billing, quota, confirm }) { check.Checked += (_, _) => RefreshButtons(); check.Unchecked += (_, _) => RefreshButtons(); }
        send.Click += async (_, _) =>
        {
            if (!CanSend() || models.SelectedItem is not SiwcModelChoice selected) return;
            // An affirmative, authorized UI action is required; there is no command-line send option.
            await controller.SendAsync(selected.Slug, confirm.IsChecked == true, quota.IsChecked == true, billing.IsChecked == true); Refresh();
        };
        cancel.Click += (_, _) => controller.Cancel();
        controller.Changed += OnChanged;
        Closed += (_, _) => { controller.Changed -= OnChanged; controller.Dispose(); };
        Refresh();
    }
    void OnChanged() { if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new System.Action(Refresh)); }
    void ClearConsents() { billing.IsChecked = quota.IsChecked = confirm.IsChecked = false; }
    bool CanSend() => !controller.Busy && !controller.HasAttempt && controller.Authenticated && controller.PlanScopeValidated
        && models.SelectedItem is SiwcModelChoice && billing.IsChecked == true && quota.IsChecked == true && confirm.IsChecked == true;
    void RefreshButtons()
    {
        send.IsEnabled = CanSend(); login.IsEnabled = !controller.Busy && !controller.HasAttempt;
        reload.IsEnabled = !controller.Busy && !controller.HasAttempt && (controller.HasSavedAccount || controller.Authenticated);
        models.IsEnabled = !controller.Busy && !controller.HasAttempt && controller.PlanScopeValidated && controller.Models.Length > 0;
        billing.IsEnabled = quota.IsEnabled = confirm.IsEnabled = !controller.Busy && !controller.HasAttempt;
        cancel.IsEnabled = controller.Busy;
    }
    void Refresh()
    {
        try { RefreshCore(); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { message.Text = "验收记录不可核验，已阻断；不会创建新请求或重发。"; send.IsEnabled = login.IsEnabled = reload.IsEnabled = models.IsEnabled = false; }
    }
    void RefreshCore()
    {
        var r = controller.Report;
        if (!lastModels.SequenceEqual(controller.Models)) { lastModels = controller.Models.ToArray(); models.ItemsSource = lastModels; models.SelectedIndex = -1; ClearConsents(); }
        account.Text = controller.HasAttempt && !controller.Authenticated && r.AccountId.Length >= 12
            ? "验收账户：" + r.AccountId[..12] + "… · 发送前身份/套餐scope：" + (r.PlanScopeValidated ? "已验证（历史证据）" : "未确认") + "；当前连接未重新检查。"
            : "账户：" + controller.AccountLabel + " · 身份：" + (controller.Authenticated ? "已验证" : "未验证")
                + " · 套餐scope：" + (controller.PlanScopeValidated ? "通过" : "未确认");
        identity.Text = $"RequestId：{controller.Ticket.RequestId}\n本地验收Session（非Desktop会话）：{controller.Ticket.SessionId}";
        message.Text = controller.Message + (r.DiagnosticCode.Length > 0 ? "\n诊断：" + r.DiagnosticCode : "");
        modelStatus.Text = controller.HasAttempt ? "本次验收已尝试，模型：" + r.Model + "；已锁定，不能重新选择或发送。"
            : controller.Models.Length > 0 ? $"已读取 {controller.Models.Length} 个可选模型；请选择一个模型。"
            : controller.Busy ? "正在读取官方模型目录，请稍候…"
            : controller.PlanScopeValidated ? "模型列表尚未加载成功。" + controller.Message + " 请点击上方“读取已授权账户 / 刷新模型”。"
            : "请先完成授权，再读取当前账户的模型列表。";
        body.Text = r.BodyReadable ? r.Body : "尚未获得可确认的完整正文";
        result.Text = $"状态：{r.Status}\n完成事件：{(r.ResponseCompleted ? "response.completed" : "未确认")} · 正文匹配：{(r.BodyReadable ? r.ExactMatch ? "严格一致" : "不一致" : "未验证")}"
            + $"\n来源：{r.EvidenceKind} · 模型：{r.Model}\nResponseId：{r.ResponseId}\n用量Token：{r.Usage?.TotalTokens?.ToString() ?? "Unknown"}（不是积分/金额）"
            + "\n精确额度/积分消耗：Unknown；不会报告零费用。";
        RefreshButtons();
    }
    void OpenUsage()
    {
        try { Process.Start(new ProcessStartInfo("https://chatgpt.com/settings/usage") { UseShellExecute = true }); }
        catch { message.Text = "暂时无法打开官方用量设置，请在现有浏览器中手动查看。未发送模型请求。"; }
    }
    static TextBlock Copy(string text, bool muted = false)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(0, 5, 0, 5) };
        t.SetResourceReference(TextBlock.ForegroundProperty, muted ? "Muted" : "Ink"); return t;
    }
    static TextBlock Heading(string text) { var t = Copy(text); t.FontSize = 16; t.FontWeight = FontWeights.SemiBold; t.Margin = new Thickness(0, 18, 0, 6); return t; }
    static Button Action(string text, string name)
    {
        var b = new Button { Content = text, Name = name, MinHeight = 44, Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(0, 4, 10, 4) };
        AutomationProperties.SetName(b, text); return b;
    }
    static CheckBox Confirm(string text, string name)
    { var c = new CheckBox { Name = name, Content = Copy(text), MinHeight = 44, VerticalContentAlignment = VerticalAlignment.Center }; c.SetResourceReference(ForegroundProperty, "Ink"); return c; }
    static Border Card(UIElement child)
    { var b = new Border { Child = child, Padding = new Thickness(12), CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 8, 0, 8) }; b.SetResourceReference(Border.BackgroundProperty, "Surface"); return b; }
}

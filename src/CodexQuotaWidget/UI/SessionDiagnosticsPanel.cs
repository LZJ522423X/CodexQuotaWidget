using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace CodexQuotaWidget;

/// <summary>Evidence fields only: labels do not derive task state from quota, process presence, or local queue flags.</summary>
public sealed class SessionDiagnosticsPanel : UserControl
{
    readonly Dictionary<string, TextBlock> fields = new(StringComparer.Ordinal);
    public SessionDiagnosticsPanel()
    {
        Name = "SessionDiagnosticsPanel"; NameScope.SetNameScope(this, new NameScope());
        SetResourceReference(ForegroundProperty, "Ink");
        AutomationProperties.SetName(this, "绑定 Session 的真实状态证据");
        var body = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        var title = Text("状态证据", 14); title.FontWeight = FontWeights.SemiBold; body.Children.Add(title);
        foreach (var name in new[] { "State", "Project", "Session", "Turn", "Activity", "Event", "Source", "Trust", "Evidence", "Sequence", "UnknownReason", "ProjectMatch", "Timeline", "AgentAck" })
        {
            var block = Text("", 12); var controlName = "SessionDiagnostic" + name;
            block.Name = controlName; RegisterName(controlName, block); fields.Add(name, block); body.Children.Add(block);
        }
        body.Children.Add(Text("Completed 仅代表当前 Turn 完成。历史仅含本工具观察到的状态，可能跳过轮询之间的变化。", 12, true));
        body.Children.Add(Text("事件序号用于排序；证据等级不授权操作；Agent 回执是未经认证的接收声明，不证明任务停止或动作成功。", 12, true));
        body.Children.Add(Text("这些字段来自绑定记录与可核验证据。未知不会被推断为完成、崩溃或耗尽；未连接时保留已绑定 Session 标识。", 12, true));
        Content = body; Update(new());
    }
    static TextBlock Text(string text, double size, bool muted = false)
    {
        var block = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4), FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI") };
        block.SetResourceReference(TextBlock.ForegroundProperty, muted ? "Muted" : "Ink"); return block;
    }
    static string Value(string? text) => string.IsNullOrWhiteSpace(text) ? "未知 / 尚无证据" : text;
    internal static string ExplainState(string? value) => value switch
    {
        "Working" => "运行中（Working）", "Waiting" => "等待输入（Waiting）", "Completed" => "本轮完成（Completed）",
        "Failed" => "任务失败（Failed）", "Exhausted" => "额度耗尽（Exhausted）", "Unknown" or null or "" => "未知（Unknown）", _ => value
    };
    public void Update(ReliabilityUiState state)
    {
        fields["State"].Text = "真实状态：" + ExplainState(state.CodexState);
        fields["Project"].Text = "绑定项目：" + Value(state.BindingProject);
        fields["Session"].Text = "绑定 Session：" + Value(state.SessionId);
        fields["Turn"].Text = "当前 Turn：" + Value(state.TurnId);
        fields["Activity"].Text = "最后活动：" + Value(state.LastActivity);
        fields["Event"].Text = "最后事件：" + Value(state.LastEvent);
        fields["Source"].Text = "状态来源：" + Value(state.StateSource);
        fields["Trust"].Text = "可信等级：" + Value(state.TrustLevel);
        fields["Evidence"].Text = "核验证据：" + Value(state.EvidenceDescription);
        fields["Sequence"].Text = "当前事件序号：" + Value(state.EventSequence);
        fields["UnknownReason"].Text = "Unknown 原因：" + Value(state.UnknownReason);
        fields["ProjectMatch"].Text = "原日志项目声明：" + Value(state.ProjectMatchDescription);
        fields["Timeline"].Text = "最近已观察状态：\n" + Value(state.StateTimeline);
        fields["AgentAck"].Text = "外部接收声明：" + Value(state.AgentAcknowledgement);
    }
}

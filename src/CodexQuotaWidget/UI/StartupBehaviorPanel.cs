using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using CodexQuotaWidget.Core;
using CodexQuotaWidget.Core.Startup;

namespace CodexQuotaWidget;

public sealed class StartupBehaviorPanel : StackPanel
{
    readonly IStartupBehaviorController controller;
    readonly Settings settings;
    readonly Action save;
    readonly Func<bool> readClose;
    readonly CheckBox logon = new() { Content = "开机自动启动", Name = "WindowsLogonStartupCheckBox", Margin = new(0, 0, 0, 2) };
    readonly CheckBox follow = new() { Content = "跟随 Codex 启动", Name = "FollowCodexStartupCheckBox", Margin = new(0, 10, 0, 2) };
    readonly CheckBox close = new() { Content = "跟随 Codex 关闭", Name = "FollowCodexCloseCheckBox", Margin = new(0, 10, 0, 2) };
    readonly TextBlock feedback = new() { Name = "StartupBehaviorFeedback", TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new(0, 6, 0, 4) };
    readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromSeconds(3) };
    bool updating, changing;

    public StartupBehaviorPanel(Settings settings, Action save, IStartupBehaviorController? controller = null, Func<bool>? readClose = null)
    {
        this.settings = settings; this.save = save; this.controller = controller ?? StartupIntegration.CreateController();
        this.readClose = readClose ?? (() => AtomicStore.Read<Settings>(Path.Combine(Paths.Data, "settings.json"))?.CloseWithCodex == true);
        Margin = new(0, 12, 0, 12);
        var heading = new TextBlock { Text = "启动行为", FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 10) };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "Ink"); Children.Add(heading);
        AddOption(logon, "Windows登录后自动启动AI Control Center");
        AddOption(follow, "Codex Desktop启动时自动打开AI Control Center");
        AddOption(close, "Codex Desktop完全退出后保存进度并退出小组件；不停止跟随启动监听");
        feedback.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        AutomationProperties.SetLiveSetting(feedback, AutomationLiveSetting.Polite); Children.Add(feedback);
        logon.Checked += (_, _) => Change(StartupBehaviorOption.WindowsLogon, true);
        logon.Unchecked += (_, _) => Change(StartupBehaviorOption.WindowsLogon, false);
        follow.Checked += (_, _) => Change(StartupBehaviorOption.FollowCodex, true);
        follow.Unchecked += (_, _) => Change(StartupBehaviorOption.FollowCodex, false);
        close.Checked += (_, _) => ChangeClose(true);
        close.Unchecked += (_, _) => ChangeClose(false);
        refresh.Tick += (_, _) => { if(IsVisible && !changing) RefreshState(); };
        Loaded += (_, _) => { RefreshState(); refresh.Start(); };
        Unloaded += (_, _) => refresh.Stop();
        RefreshState();
    }
    void AddOption(CheckBox checkbox, string description)
    {
        AutomationProperties.SetName(checkbox, checkbox.Content.ToString());
        AutomationProperties.SetHelpText(checkbox, description);
        Children.Add(checkbox);
        var help = new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new(24, 0, 0, 0) };
        help.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); Children.Add(help);
    }
    void Change(StartupBehaviorOption option, bool enabled)
    {
        if(updating || changing) return;
        changing = true; logon.IsEnabled = false; follow.IsEnabled = false;
        try
        {
            controller.SetEnabled(option, enabled);
            var state = controller.Read();
            settings.AutoStart = state.WindowsLogon.Enabled; settings.StartWithCodex = state.FollowCodex.Enabled;
            save(); feedback.Text = "";
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or InvalidOperationException or COMException or System.ComponentModel.Win32Exception or System.Security.SecurityException)
        { feedback.Text = error.Message; }
        finally { changing = false; RefreshState(); }
    }
    void ChangeClose(bool enabled)
    {
        if(updating || changing) return;
        changing = true;
        var previous = settings.CloseWithCodex;
        try
        {
            settings.CloseWithCodex = enabled; save();
            feedback.Text = readClose() == enabled ? "关闭行为已保存。" : "关闭行为未能保存，保留原设置。";
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or InvalidOperationException)
        { settings.CloseWithCodex = previous; feedback.Text = "关闭行为未能保存：" + error.Message; }
        finally { changing = false; RefreshState(); }
    }
    public void RefreshState()
    {
        if(changing) return;
        updating = true;
        try { close.IsEnabled = true; close.IsChecked = settings.CloseWithCodex = readClose(); }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or InvalidOperationException)
        { close.IsEnabled = false; close.IsChecked = settings.CloseWithCodex = false; feedback.Text = "无法读取关闭行为：" + error.Message; }
        try
        {
            var state = controller.Read();
            logon.IsChecked = state.WindowsLogon.Enabled; follow.IsChecked = state.FollowCodex.Enabled;
            logon.IsEnabled = state.WindowsLogon.Available; follow.IsEnabled = state.FollowCodex.Available;
            settings.AutoStart = state.WindowsLogon.Enabled; settings.StartWithCodex = state.FollowCodex.Enabled;
            if(string.IsNullOrEmpty(feedback.Text))
            {
                if(!state.WindowsLogon.Available) feedback.Text = state.WindowsLogon.Message;
                else if(!state.FollowCodex.Available) feedback.Text = state.FollowCodex.Message;
            }
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or InvalidOperationException or COMException or System.Security.SecurityException)
        {
            logon.IsChecked = false; follow.IsChecked = false; logon.IsEnabled = false; follow.IsEnabled = false;
            feedback.Text = "无法读取 Windows 启动状态：" + error.Message;
        }
        finally { updating = false; }
    }
    public void Stop() => refresh.Stop();
}

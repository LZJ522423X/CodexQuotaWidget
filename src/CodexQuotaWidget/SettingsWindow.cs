using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using CodexQuotaWidget.Core;
using Microsoft.Win32;

namespace CodexQuotaWidget;
public sealed class SettingsWindow : Window
{
    public SettingsWindow(Settings settings,QuotaMonitor monitor,Action save)
    {
        Title="Codex 额度设置";Width=480;SizeToContent=SizeToContent.Height;ResizeMode=ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.CenterScreen;
        SetResourceReference(BackgroundProperty,"Surface");
        MaxHeight=Math.Max(400,SystemParameters.WorkArea.Height-40);
        var panel=new StackPanel{Margin=new Thickness(22)};Content=new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
        panel.Children.Add(new TextBlock{Text="自动刷新间隔",FontWeight=FontWeights.SemiBold});
        var interval=new ComboBox{ItemsSource=new[]{60,120,300},SelectedItem=settings.RefreshSeconds,Margin=new Thickness(0,8,0,12),Height=30};panel.Children.Add(interval);
        var follow=new CheckBox{Content="跟随 Codex 运行状态",IsChecked=settings.FollowCodex};panel.Children.Add(follow);
        panel.Children.Add(new StartupBehaviorPanel(settings,save));
        panel.Children.Add(new SkillUpdatePanel(()=>settings.EnableAnimations));
        panel.Children.Add(new TextBlock{Text="官方 Codex CLI 备用路径",Margin=new Thickness(0,14,0,6)});
        var path=new TextBox{Text=settings.ManualCli??"",IsReadOnly=true,TextWrapping=TextWrapping.Wrap,MinHeight=34};panel.Children.Add(path);
        var browse=new Button{Content="定位 codex.exe",Margin=new Thickness(0,8,0,12)};panel.Children.Add(browse);
        browse.Click+=async(_,_)=>
        {
            var picker=new OpenFileDialog{Filter="Codex CLI|codex.exe",CheckFileExists=true};if(picker.ShowDialog(this)!=true)return;
            browse.IsEnabled=false;try{if(await AppServer.GetVersion(picker.FileName) is null)MessageBox.Show(this,"未通过官方签名或版本验证。","文件不可用");else path.Text=picker.FileName;}finally{browse.IsEnabled=true;}
        };
        panel.Children.Add(new TextBlock{Text=$"认证目录：{Paths.Home}\nCLI：{monitor.CliVersion}\n版本：{ReleaseUpdates.CurrentLabel}",FontSize=11,Margin=new Thickness(0,0,0,10)});
        var login=new Button{Content="登录／重新绑定独立账号",Margin=new Thickness(0,0,0,12)};panel.Children.Add(login);
        login.Click+=async(_,_)=>
        {
            if(MessageBox.Show(this,"将打开小组件独立账号的官方登录页面。账号选择和授权由你完成。是否继续？","独立账号",MessageBoxButton.OKCancel)!=MessageBoxResult.OK)return;
            login.IsEnabled=false;try{await monitor.LoginAsync();}finally{login.IsEnabled=true;}
        };
        var apply=new Button{Content="保存",HorizontalAlignment=HorizontalAlignment.Right,MinWidth=90};panel.Children.Add(apply);
        apply.Click+=(_,_)=>
        {
            try
            {
                settings.RefreshSeconds=(int)(interval.SelectedItem??60);settings.FollowCodex=follow.IsChecked==true;settings.ManualCli=string.IsNullOrWhiteSpace(path.Text)?null:path.Text;
                save();Close();
            }
            catch(Exception e)when(e is IOException or UnauthorizedAccessException or COMException){MessageBox.Show(this,"保存启动项失败，未修改其他设置。","设置");}
        };
    }
}

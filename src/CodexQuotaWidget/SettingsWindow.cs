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
        var panel=new StackPanel{Margin=new Thickness(22)};Content=panel;
        panel.Children.Add(new TextBlock{Text="自动刷新间隔",FontWeight=FontWeights.SemiBold});
        var interval=new ComboBox{ItemsSource=new[]{60,120,300},SelectedItem=settings.RefreshSeconds,Margin=new Thickness(0,8,0,12),Height=30};panel.Children.Add(interval);
        var follow=new CheckBox{Content="跟随 Codex 运行状态",IsChecked=settings.FollowCodex};panel.Children.Add(follow);
        var startup=new CheckBox{Content="登录 Windows 时启动",IsChecked=settings.AutoStart};panel.Children.Add(startup);
        panel.Children.Add(new TextBlock{Text="官方 Codex CLI 备用路径",Margin=new Thickness(0,14,0,6)});
        var path=new TextBox{Text=settings.ManualCli??"",IsReadOnly=true,TextWrapping=TextWrapping.Wrap,MinHeight=34};panel.Children.Add(path);
        var browse=new Button{Content="定位 codex.exe",Margin=new Thickness(0,8,0,12)};panel.Children.Add(browse);
        browse.Click+=async(_,_)=>
        {
            var picker=new OpenFileDialog{Filter="Codex CLI|codex.exe",CheckFileExists=true};if(picker.ShowDialog(this)!=true)return;
            browse.IsEnabled=false;try{if(await AppServer.GetVersion(picker.FileName) is null)MessageBox.Show(this,"未通过官方签名或版本验证。","文件不可用");else path.Text=picker.FileName;}finally{browse.IsEnabled=true;}
        };
        panel.Children.Add(new TextBlock{Text=$"认证目录：{Paths.Home}\nCLI：{monitor.CliVersion}\n版本：0.2.0",FontSize=11,Margin=new Thickness(0,0,0,10)});
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
                if(startup.IsChecked!=settings.AutoStart)SetStartup(startup.IsChecked==true);
                settings.RefreshSeconds=(int)(interval.SelectedItem??60);settings.FollowCodex=follow.IsChecked==true;settings.AutoStart=startup.IsChecked==true;settings.ManualCli=string.IsNullOrWhiteSpace(path.Text)?null:path.Text;
                save();Close();
            }
            catch(Exception e)when(e is IOException or UnauthorizedAccessException or COMException){MessageBox.Show(this,"保存启动项失败，未修改其他设置。","设置");}
        };
    }
    static void SetStartup(bool enabled)
    {
        var shortcut=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup),"CodexQuotaWidget.lnk");
        if(File.Exists(shortcut))
        {
            Directory.CreateDirectory(Path.Combine(Paths.Data,"backups"));
            File.Copy(shortcut,Path.Combine(Paths.Data,"backups",$"startup-{DateTime.Now:yyyyMMdd-HHmmss}.lnk"));
        }
        if(!enabled){if(File.Exists(shortcut))File.Delete(shortcut);return;}
        var type=Type.GetTypeFromProgID("WScript.Shell")??throw new COMException();dynamic shell=Activator.CreateInstance(type)!;dynamic link=shell.CreateShortcut(shortcut);
        try{link.TargetPath=Environment.ProcessPath!;link.WorkingDirectory=Path.GetDirectoryName(Environment.ProcessPath)!;link.Description="Codex Quota Widget";link.Save();}
        finally{Marshal.FinalReleaseComObject(link);Marshal.FinalReleaseComObject(shell);}
    }
}

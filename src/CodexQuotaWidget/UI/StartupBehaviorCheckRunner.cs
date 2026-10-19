using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodexQuotaWidget.Core;
using CodexQuotaWidget.Core.Startup;

namespace CodexQuotaWidget;
internal static class StartupBehaviorCheckRunner
{
    sealed class Fixture : IStartupBehaviorController
    {
        public bool Logon, Follow, Fail, NativeFail;
        public int Writes;
        public StartupBehaviorState Read() => new(new(true, Logon, true, true, ""), new(Follow, Follow, true, true, ""));
        public void SetEnabled(StartupBehaviorOption option, bool enabled)
        { Writes++; if(NativeFail) throw new System.ComponentModel.Win32Exception(5,"模拟启动器拒绝访问"); if(Fail) throw new IOException("模拟写入失败"); if(option == StartupBehaviorOption.WindowsLogon) Logon=enabled; else Follow=enabled; }
        public void RefreshPaths() { }
    }
    public static async Task<int> RunAsync()
    {
        var output = Environment.GetEnvironmentVariable("CQW_STARTUP_UI_CHECK_DIR") ?? @"D:\CodexQuotaWidget-GitHub\artifacts\startup-behavior-1.10.0";
        if(!Path.GetFullPath(output).StartsWith(@"D:\CodexQuotaWidget-GitHub\artifacts\",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Isolated D UI output required.");
        Directory.CreateDirectory(output); int count=0;
        void Check(bool result, string name) { if(!result) throw new InvalidOperationException(name); count++; }
        var fixture = new Fixture { Logon=true };
        var settings = new Settings { AutoStart=false, StartWithCodex=true, CloseWithCodex=true }; var saves=0;var saveFailure=false;
        var path=Path.Combine(output,"fixture-settings.json");AtomicStore.Write(path,settings);
        var panel = new StartupBehaviorPanel(settings, () => {if(saveFailure)throw new IOException("模拟设置保存失败");AtomicStore.Write(path,settings);saves++;}, fixture,
            ()=>AtomicStore.Read<Settings>(path)?.CloseWithCodex==true);
        var choices = panel.Children.OfType<CheckBox>().ToArray();
        Check(choices.Length==3, "three independent options");
        Check(choices[2].IsChecked==true && settings.CloseWithCodex,"saved follow-close choice preserved");
        Check(choices[0].IsChecked==true && choices[1].IsChecked==false, "Windows truth overrides JSON");
        Check(fixture.Writes==0, "reading UI must not register startup");
        choices[1].IsChecked=true;
        Check(fixture.Follow && fixture.Logon && saves==1, "both can be enabled");
        choices[0].IsChecked=false;
        Check(!fixture.Logon && fixture.Follow && saves==2, "independent cancellation");
        fixture.Fail=true; choices[0].IsChecked=true;
        Check(choices[0].IsChecked==false && !fixture.Logon && saves==2, "failed write rereads actual state");
        Check(panel.Children.OfType<TextBlock>().Any(item=>item.Text=="模拟写入失败"), "visible error");
        fixture.Fail=false;fixture.NativeFail=true;choices[0].IsChecked=true;
        Check(choices[0].IsChecked==false && saves==2,"native launch failure does not crash settings");
        fixture.NativeFail=false;
        fixture.Fail=false; fixture.Logon=true; fixture.Follow=false; var writes=fixture.Writes;
        panel.RefreshState();
        Check(choices[0].IsChecked==true && choices[1].IsChecked==false && fixture.Writes==writes, "external state refresh is read-only");
        choices[2].IsChecked=false;
        Check(!settings.CloseWithCodex && AtomicStore.Read<Settings>(path)?.CloseWithCodex==false && fixture.Writes==writes,
            "follow-close cancellation persists without touching either startup registration");
        choices[2].IsChecked=true;
        Check(settings.CloseWithCodex && AtomicStore.Read<Settings>(path)?.CloseWithCodex==true && fixture.Writes==writes,
            "follow-close enabling persists independently of startup switches");
        saveFailure=true;choices[2].IsChecked=false;
        Check(settings.CloseWithCodex && choices[2].IsChecked==true && AtomicStore.Read<Settings>(path)?.CloseWithCodex==true,
            "failed close-setting save restores actual persisted choice");
        saveFailure=false;
        foreach(var checkbox in choices)
        {
            Check(checkbox.ToolTip is null, "no blank tooltip windows");
            Check(!string.IsNullOrEmpty(AutomationProperties.GetName(checkbox)) && !string.IsNullOrEmpty(AutomationProperties.GetHelpText(checkbox)), "accessible labels");
            Check(checkbox.Focusable, "keyboard focus");
        }
        var host = new Window { Width=470, Height=420, ShowInTaskbar=false, ShowActivated=false, Content=panel };
        host.SetResourceReference(Window.BackgroundProperty,"Surface");
        foreach(var theme in new[] { "Dark", "Light" })
        {
            ((App)Application.Current).ConfigurePreviewTheme(new Settings { Theme=theme });
            host.Show(); host.UpdateLayout(); await Task.Delay(80);
            Check(panel.ActualWidth>0 && panel.ActualHeight<host.ActualHeight, theme+" layout");
            var bitmap=new RenderTargetBitmap(470,420,96,96,PixelFormats.Pbgra32); bitmap.Render(host);
            var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file=File.Create(Path.Combine(output,"startup-"+theme.ToLowerInvariant()+".png")); encoder.Save(file);
        }
        panel.Stop(); host.Close();
        File.WriteAllText(Path.Combine(output,"ui-check-result.json"), System.Text.Json.JsonSerializer.Serialize(new { Passed=true, Checks=count, Mode="Synthetic controller; real WPF controls/rendering; no OS registration changes" }));
        Console.WriteLine($"PASS startup-behavior-check: {count} checks"); return 0;
    }
}

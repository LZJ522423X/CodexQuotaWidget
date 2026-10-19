using System.Runtime.InteropServices;
using CodexQuotaWidget.Core.Startup;

static class StartupBehaviorChecks
{
    public static void RunHost(string widget, string watcher)
    {
        // Explicit acceptance mode touches only our two canonical links and approval values.
        const string approvalPath=@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";
        var startup=Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        var names=Enum.GetValues<StartupBehaviorOption>().Select(WindowsStartupRegistration.LinkName).ToArray();
        var originals=names.ToDictionary(name=>name,name=>File.Exists(Path.Combine(startup,name))?File.ReadAllBytes(Path.Combine(startup,name)):null);
        using var key=Microsoft.Win32.Registry.CurrentUser.OpenSubKey(approvalPath);
        var approvals=names.ToDictionary(name=>name,name=>key?.GetValue(name));
        var store=new WindowsStartupRegistration(@"D:\CodexQuotaWidgetData",widget,watcher);
        var initial=store.Read();
        if(initial.WindowsLogon.Registered&&!initial.WindowsLogon.Owned || initial.FollowCodex.Registered&&!initial.FollowCodex.Owned)
            throw new InvalidOperationException("Foreign startup entries: host test refused.");
        var checks=new List<string>();
        void Check(bool value,string name){if(!value)throw new InvalidOperationException(name);checks.Add(name);}
        try
        {
            store.SetEnabled(StartupBehaviorOption.WindowsLogon,false);
            Check(!store.Read().WindowsLogon.Registered,"current-user cancellation");
            store.SetEnabled(StartupBehaviorOption.WindowsLogon,true);
            Check(store.Read().WindowsLogon.Enabled,"current-user registration");
            var path=Path.Combine(startup,names[0]);var before=File.ReadAllBytes(path);var write=File.GetLastWriteTimeUtc(path);
            store.SetEnabled(StartupBehaviorOption.WindowsLogon,true);
            Check(before.SequenceEqual(File.ReadAllBytes(path))&&write==File.GetLastWriteTimeUtc(path),"duplicate registration is a no-op");
            store.SetEnabled(StartupBehaviorOption.FollowCodex,true);
            Check(store.Read().WindowsLogon.Enabled&&store.Read().FollowCodex.Enabled,"independent options enabled together");
            // Simulate only the Windows approval byte, using our own entry; restore afterward.
            using(var approval=Microsoft.Win32.Registry.CurrentUser.CreateSubKey(approvalPath))
                approval.SetValue(names[0],new byte[]{3,0,0,0,0,0,0,0,0,0,0,0},Microsoft.Win32.RegistryValueKind.Binary);
            Check(!store.Read().WindowsLogon.Enabled,"Windows disabled status read accurately");
            store.SetEnabled(StartupBehaviorOption.WindowsLogon,true);
            Check(store.Read().WindowsLogon.Enabled,"reenabling removes only owned approval veto");
            store.SetEnabled(StartupBehaviorOption.FollowCodex,false);
            Check(!store.Read().FollowCodex.Registered&&store.Read().WindowsLogon.Enabled,"watcher registration cancellation");
        }
        finally
        {
            foreach(var name in names)
            {
                var path=Path.Combine(startup,name);
                if(originals[name] is null) { if(File.Exists(path))File.Delete(path); } else File.WriteAllBytes(path,originals[name]!);
                using var approval=Microsoft.Win32.Registry.CurrentUser.CreateSubKey(approvalPath);
                if(approvals[name] is null)approval.DeleteValue(name,false);else approval.SetValue(name,approvals[name]!,Microsoft.Win32.RegistryValueKind.Binary);
            }
        }
        File.WriteAllText(@"D:\CodexQuotaWidget-GitHub\artifacts\startup-behavior-1.10.0\native-registration.json",System.Text.Json.JsonSerializer.Serialize(new{Passed=true,Checks=checks,Initial=initial,Final=store.Read(),Restored=true},new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"PASS native owned registration: {checks.Count} checks; initial state restored");
    }
    public static int Run()
    {
        var root=@"D:\CodexQuotaWidget-GitHub\artifacts\startup-behavior-1.10.0\fixture-"+Guid.NewGuid().ToString("N");
        var startup=Path.Combine(root,"Startup"); var v1=Path.Combine(root,"publish","v1"); var v2=Path.Combine(root,"publish","v2");
        Directory.CreateDirectory(startup); Directory.CreateDirectory(v1); Directory.CreateDirectory(v2);
        string Widget(string version)=>Path.Combine(version,"CodexQuotaWidget.exe");
        string Watcher(string version)=>Path.Combine(version,"AIControlCenter.StartupWatcher.exe");
        foreach(var path in new[]{Widget(v1),Watcher(v1),Widget(v2),Watcher(v2)})File.WriteAllText(path,"synthetic fixture, never executed");
        var store=new WindowsStartupRegistration(root,Widget(v1),Watcher(v1),startup,false,Path.Combine(root,"publish")); int count=0;
        void Check(bool value,string name){if(!value)throw new InvalidOperationException(name);count++;}
        Check(!store.Read().WindowsLogon.Enabled&&!store.Read().FollowCodex.Enabled,"initial absent");
        store.SetEnabled(StartupBehaviorOption.WindowsLogon,true);
        Check(store.Read().WindowsLogon.Enabled&&!store.Read().FollowCodex.Enabled,"independent registration");
        var link=Path.Combine(startup,WindowsStartupRegistration.LinkName(StartupBehaviorOption.WindowsLogon));
        var bytes=File.ReadAllBytes(link);var time=File.GetLastWriteTimeUtc(link);
        store.SetEnabled(StartupBehaviorOption.WindowsLogon,true);
        Check(bytes.SequenceEqual(File.ReadAllBytes(link))&&time==File.GetLastWriteTimeUtc(link),"idempotent duplicate protection");
        store.SetEnabled(StartupBehaviorOption.FollowCodex,true);
        Check(store.Read().WindowsLogon.Enabled&&store.Read().FollowCodex.Enabled&&Directory.GetFiles(startup,"*.lnk").Length==2,"two canonical entries");
        var updated=new WindowsStartupRegistration(root,Widget(v2),Watcher(v2),startup,false,Path.Combine(root,"publish"));updated.RefreshPaths();
        Check(updated.Read().WindowsLogon.TargetPath==Widget(v2)&&updated.Read().FollowCodex.TargetPath==Watcher(v2),"upgrade removes old target paths");
        updated.SetEnabled(StartupBehaviorOption.FollowCodex,false);
        Check(updated.Read().WindowsLogon.Enabled&&!updated.Read().FollowCodex.Registered,"only watcher cancelled");
        updated.SetEnabled(StartupBehaviorOption.WindowsLogon,false);
        Check(Directory.GetFiles(startup,"*.lnk").Length==0,"registration cancellation");
        // Unknown same-name shortcut must remain byte-for-byte untouched.
        object shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        object foreign=((dynamic)shell).CreateShortcut(link);
        ((dynamic)foreign).TargetPath=Path.Combine(root,"unknown.exe");((dynamic)foreign).Description="unknown";((dynamic)foreign).Save();
        Marshal.FinalReleaseComObject(foreign);Marshal.FinalReleaseComObject(shell);bytes=File.ReadAllBytes(link);
        Check(!updated.Read().WindowsLogon.Owned&&!updated.Read().WindowsLogon.Available,"foreign entry protected");
        foreach(var enabled in new[]{true,false})
        {bool rejected=false;try{updated.SetEnabled(StartupBehaviorOption.WindowsLogon,enabled);}catch(InvalidOperationException){rejected=true;}Check(rejected&&bytes.SequenceEqual(File.ReadAllBytes(link)),"foreign write/delete denied");}
        var missing=new WindowsStartupRegistration(root,Widget(v2),Path.Combine(v2,"absent.exe"),Path.Combine(root,"missing-startup"),false,Path.Combine(root,"publish"));
        Check(!missing.Read().FollowCodex.Available,"missing watcher unavailable");
        var policy=new StartupWatchPolicy();
        var first=new DesktopRunIdentity(41,1000);var second=new DesktopRunIdentity(42,2000);
        Check(!policy.Evaluate(Array.Empty<DesktopRunIdentity>(),false).ShouldLaunch,"fake Codex cannot launch");
        Check(policy.Evaluate(new[]{first},false).ShouldLaunch,"verified Codex first lifetime launches");
        Check(!policy.Evaluate(new[]{first},false).ShouldLaunch,"same Codex does not repeatedly restart closed widget");
        Check(!policy.Evaluate(Array.Empty<DesktopRunIdentity>(),false).ShouldLaunch&&policy.Evaluate(new[]{second},false).ShouldLaunch,"next Codex lifetime permitted");
        Check(policy.Evaluate(new[]{new DesktopRunIdentity(43,3000)},false).ShouldLaunch,"identity replacement without absent sample launches");
        Check(policy.Evaluate(new[]{new DesktopRunIdentity(43,4000)},false).ShouldLaunch,"PID reuse with different start time is a new lifetime");
        Check(!policy.Evaluate(new[]{new DesktopRunIdentity(0,0)},false).ShouldLaunch,"invalid process identity rejected");
        policy=new();Check(!policy.Evaluate(new[]{first},true).ShouldLaunch&&!policy.Evaluate(new[]{first},false).ShouldLaunch,"already running widget consumes lifetime");
        Check(WindowsStartupRegistration.Quote(@"D:\")=="\"D:\\\\\"","root path quote preserves trailing slash");
        Console.WriteLine($"PASS startup registration/policy: {count} checks (D fixtures only)");return count;
    }
}

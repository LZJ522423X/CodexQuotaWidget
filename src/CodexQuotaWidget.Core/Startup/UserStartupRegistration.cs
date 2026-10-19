using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
using CodexQuotaWidget.Core.Automation;

namespace CodexQuotaWidget.Core.Startup;

public enum StartupBehaviorOption { WindowsLogon, FollowCodex }
public record StartupEntryState(bool Registered, bool Enabled, bool Owned, bool Available, string Message, string? TargetPath = null);
public record StartupBehaviorState(StartupEntryState WindowsLogon, StartupEntryState FollowCodex);
public interface IStartupBehaviorController
{
    StartupBehaviorState Read();
    void SetEnabled(StartupBehaviorOption option, bool enabled);
    void RefreshPaths();
}

/// <summary>Only two owned current-user Startup links. Windows, never cached settings, is authoritative.</summary>
public sealed class WindowsStartupRegistration : IStartupBehaviorController
{
    const string ApprovalKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";
    readonly string dataRoot, widget, watcher, folder, installationRoot;
    readonly bool useWindowsApproval;
    public WindowsStartupRegistration(string dataRoot, string widgetExecutable, string watcherExecutable,
        string? startupFolder = null, bool useWindowsApproval = true, string? installationRoot = null)
    {
        this.dataRoot = Path.GetFullPath(dataRoot); widget = Path.GetFullPath(widgetExecutable);
        watcher = Path.GetFullPath(watcherExecutable);
        folder = startupFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        this.useWindowsApproval = useWindowsApproval;
        this.installationRoot = Path.GetFullPath(installationRoot ?? Path.GetDirectoryName(widget)!);
    }
    public static string LinkName(StartupBehaviorOption option) => option == StartupBehaviorOption.WindowsLogon
        ? "CodexQuotaWidget.lnk" : "AIControlCenter.StartupWatcher.lnk";
    string LinkPath(StartupBehaviorOption option) => Path.Combine(folder, LinkName(option));
    string Executable(StartupBehaviorOption option) => option == StartupBehaviorOption.WindowsLogon ? widget : watcher;
    string Arguments(StartupBehaviorOption option) => option == StartupBehaviorOption.WindowsLogon
        ? "--startup --data-dir " + Quote(dataRoot)
        : "--data-dir " + Quote(dataRoot) + " --widget " + Quote(widget);
    public StartupBehaviorState Read() => new(ReadEntry(StartupBehaviorOption.WindowsLogon), ReadEntry(StartupBehaviorOption.FollowCodex));
    public StartupEntryState ReadEntry(StartupBehaviorOption option)
    {
        var path = LinkPath(option); var exists = File.Exists(path);
        if(!exists) return new(false, false, true, File.Exists(Executable(option)), File.Exists(Executable(option)) ? "" : "已发布程序缺失，暂不能启用启动项。");
        var link = ReadLink(path);
        var owned = IsOwned(link.Target, link.Description, option);
        var approval = ReadApproval(option);
        var knownApproval = approval is null || approval.Length >= 1 && approval[0] is 2 or 3 or 6 or 7;
        var enabled = owned && File.Exists(link.Target) && (approval is null || approval.Length >= 1 && approval[0] is 2 or 6);
        return new(true, enabled, owned, owned && knownApproval, !owned ? "发现同名但来源不明的启动项，未修改。" : !knownApproval ? "Windows 启动审批状态无法识别，未修改。" : enabled ? "" : "Windows 已禁用启动项或目标文件不可用。", link.Target);
    }
    bool IsOwned(string target, string description, StartupBehaviorOption option)
    {
        var expected = option == StartupBehaviorOption.WindowsLogon ? "CodexQuotaWidget.exe" : "AIControlCenter.StartupWatcher.exe";
        if(string.IsNullOrWhiteSpace(target) || !Path.IsPathFullyQualified(target)) return false;
        var full = Path.GetFullPath(target);
        if(!AutomationStore.IsSafeLocalPath(full) || !Path.GetFileName(full).Equals(expected, StringComparison.OrdinalIgnoreCase)) return false;
        return full.StartsWith(installationRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && description is "Codex AI Control Center" or "Codex Quota Widget" or "AI Control Center Startup Watcher";
    }
    public void SetEnabled(StartupBehaviorOption option, bool enabled)
    {
        var state = ReadEntry(option);
        if(state.Registered && (!state.Owned || !state.Available)) throw new InvalidOperationException(state.Message);
        if(enabled && (!File.Exists(Executable(option)) || !AutomationStore.IsSafeLocalPath(Executable(option))))
            throw new InvalidOperationException("已发布程序缺失或路径不安全，未注册启动项。");
        var current = state.Registered ? ReadLink(LinkPath(option)) : null;
        if(enabled && state.Enabled && current!.Target.Equals(Executable(option), StringComparison.OrdinalIgnoreCase) && current.Arguments == Arguments(option)) return;
        if(!enabled && !state.Registered) return;
        Mutate(option, enabled, preserveApproval: false);
    }
    public void RefreshPaths()
    {
        foreach(var option in Enum.GetValues<StartupBehaviorOption>())
        {
            var state = ReadEntry(option);
            if(!state.Registered || !state.Owned || !state.Available || !File.Exists(Executable(option))) continue;
            var link = ReadLink(LinkPath(option));
            if(!link.Target.Equals(Executable(option), StringComparison.OrdinalIgnoreCase) || link.Arguments != Arguments(option))
                Mutate(option, true, preserveApproval: true);
        }
    }
    void Mutate(StartupBehaviorOption option, bool enabled, bool preserveApproval)
    {
        if(!AutomationStore.IsSafeLocalPath(dataRoot)) throw new IOException("启动项备份目录必须位于 D 盘普通目录。");
        var path = LinkPath(option);
        var bytes = File.Exists(path) ? File.ReadAllBytes(path) : null;
        var approval = ReadApproval(option);
        var backup = Path.Combine(dataRoot, "startup", "backups", option.ToString());
        Directory.CreateDirectory(backup);
        // One bounded rollback snapshot per option, not growing historical backups.
        File.WriteAllText(Path.Combine(backup, "before.json"), JsonSerializer.Serialize(new { Path = path, Link = bytes, Approval = approval }));
        var staged = Path.Combine(backup, "pending.lnk");
        try
        {
            if(enabled)
            {
                CreateLink(staged, Executable(option), Arguments(option), option == StartupBehaviorOption.WindowsLogon ? "Codex AI Control Center" : "AI Control Center Startup Watcher");
                Directory.CreateDirectory(folder); File.Move(staged, path, true);
                if(!preserveApproval && approval is not null) DeleteApproval(option);
                var actual = ReadEntry(option);
                if(!actual.Owned || !actual.Registered || !preserveApproval && !actual.Enabled) throw new IOException("Windows 启动项注册验证失败。");
            }
            else { File.Delete(path); DeleteApproval(option); }
        }
        catch
        {
            if(bytes is null) { if(File.Exists(path)) File.Delete(path); } else File.WriteAllBytes(path, bytes);
            RestoreApproval(option, approval); throw;
        }
        finally { if(File.Exists(staged)) File.Delete(staged); }
    }
    byte[]? ReadApproval(StartupBehaviorOption option)
    {
        if(!useWindowsApproval) return null;
        using var key = Registry.CurrentUser.OpenSubKey(ApprovalKey);
        var value = key?.GetValue(LinkName(option));
        if(value is null) return null;
        return value as byte[] ?? [0]; // Unknown Windows value cannot be treated as enabled.
    }
    void DeleteApproval(StartupBehaviorOption option)
    {
        if(!useWindowsApproval) return;
        using var key = Registry.CurrentUser.OpenSubKey(ApprovalKey, true); key?.DeleteValue(LinkName(option), false);
    }
    void RestoreApproval(StartupBehaviorOption option, byte[]? value)
    {
        if(!useWindowsApproval) return;
        if(value is null) { DeleteApproval(option); return; }
        using var key = Registry.CurrentUser.CreateSubKey(ApprovalKey); key.SetValue(LinkName(option), value, RegistryValueKind.Binary);
    }
    record Link(string Target, string Arguments, string Description);
    static Link ReadLink(string path)
    {
        object? shell = null, link = null;
        try { shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!); link = ((dynamic)shell!).CreateShortcut(path);
            return new((string)((dynamic)link).TargetPath, (string)((dynamic)link).Arguments, (string)((dynamic)link).Description); }
        finally { Release(link); Release(shell); }
    }
    static void CreateLink(string path, string executable, string arguments, string description)
    {
        object? shell = null, link = null;
        try { shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!); link = ((dynamic)shell!).CreateShortcut(path);
            dynamic item = link; item.TargetPath = executable; item.Arguments = arguments; item.Description = description;
            item.WorkingDirectory = Path.GetDirectoryName(executable); item.IconLocation = executable + ",0"; item.Save(); }
        finally { Release(link); Release(shell); }
    }
    static void Release(object? value) { if(value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
    public static string Quote(string value)
    {
        var result = new System.Text.StringBuilder("\""); var slashes = 0;
        foreach(var character in value)
        {
            if(character == '\\') { slashes++; continue; }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes); slashes = 0; result.Append(character);
        }
        result.Append('\\', slashes * 2); return result.Append('"').ToString();
    }
}

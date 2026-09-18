using System.Text.Json;

namespace CodexQuotaWidget.Core;

public static class Paths
{
    public static string Data { get; } = ResolveData();
    public static string Home => Path.Combine(Data, "codex-home");
    public static string? PreferredCli => ResolveOptionalPath("CODEX_QUOTA_WIDGET_CLI");

    static string ResolveData()
        => ResolveOptionalPath("CODEX_QUOTA_WIDGET_DATA")
           ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexQuotaWidget");

    static string? ResolveOptionalPath(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(value)) return null;
        try { return Path.GetFullPath(value); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
}
public sealed class Settings
{
    public int RefreshSeconds { get; set; } = 60;
    public bool FollowCodex { get; set; } = true;
    public bool AutoStart { get; set; }
    public string? ManualCli { get; set; }
    public string Theme { get; set; } = "Dark";
    public string Accent { get; set; } = "Mint";
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double Width { get; set; } = 440;
    public double Height { get; set; } = 610;
    public void Validate()
    {
        if (RefreshSeconds is not (60 or 120 or 300)) RefreshSeconds = 60;
        if (Theme is not ("Dark" or "Light" or "System")) Theme = "Dark";
        if (Accent is not ("Mint" or "Blue" or "Violet")) Accent = "Mint";
        Width = double.IsFinite(Width) ? Math.Clamp(Width,400,1200) : 440;
        Height = double.IsFinite(Height) ? Math.Clamp(Height,420,1600) : 610;
        if (Left is { } x && !double.IsFinite(x)) Left = null;
        if (Top is { } y && !double.IsFinite(y)) Top = null;
    }
}
public static class AtomicStore
{
    private static readonly object Gate = new();
    public static T? Read<T>(string path)
    {
        foreach (var candidate in new[]{path,path+".bak"})
        {
            try { if (File.Exists(candidate)) return JsonSerializer.Deserialize<T>(File.ReadAllText(candidate)); }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        }
        return default;
    }
    public static void Write<T>(string path, T value)
    {
        lock(Gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var f = new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough))
                { JsonSerializer.Serialize(f,value,new JsonSerializerOptions{WriteIndented=true}); f.Flush(true); }
                if (File.Exists(path)) File.Replace(temp,path,path+".bak");
                else File.Move(temp,path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}
public enum LogEvent { Started, Stopped, ReadSucceeded, NetworkFailure, AuthenticationRequired, ProtocolFailure, StorageFailure, ServerStopped, ServerForcedStop, LoginStarted, LoginSucceeded, LoginFailed }
public static class SafeLog
{
    private static readonly object Gate = new();
    // Only fixed event identifiers and integers are accepted; never log RPC payloads.
    public static void Write(LogEvent kind, int code=0)
    {
        lock(Gate) try
        {
            var dir=Path.Combine(Paths.Data,"logs"); Directory.CreateDirectory(dir);
            var files=new DirectoryInfo(dir).GetFiles("widget-*.log").OrderBy(f=>f.LastWriteTimeUtc).ToList();
            long size=files.Sum(f=>f.Length);
            foreach(var f in files)
                if(f.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-7) || size > 2*1024*1024)
                { size-=f.Length; f.Delete(); }
            var path=Path.Combine(dir,$"widget-{DateTime.UtcNow:yyyyMMdd}.log");
            if(File.Exists(path) && new FileInfo(path).Length > 256*1024) return;
            File.AppendAllText(path,$"{DateTimeOffset.UtcNow:O} {kind} {code}\n");
        }
        catch(IOException) { } catch(UnauthorizedAccessException) { }
    }
}

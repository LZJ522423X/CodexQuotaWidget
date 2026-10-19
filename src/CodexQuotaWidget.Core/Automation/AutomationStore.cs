using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexQuotaWidget.Core.Automation;

public sealed record AutomationConfiguration
{
    public int SchemaVersion { get; init; } = 1;
    public bool Enabled { get; init; }
    public string Text { get; init; } = "";
    public LocalActionPlan? Plan { get; init; }
    public string ProjectRoot { get; init; } = "";
}
public sealed record AutomationConfigurationReadResult(bool Success, string Message, AutomationConfiguration Configuration);

/// <summary>Local, bounded state. A corrupt primary configuration never revives an enabled .bak configuration.</summary>
public sealed class AutomationStore
{
    const int MaximumBytes = 256 * 1024;
    readonly object gate = new();
    readonly string dataRoot;
    public string ConfigurationPath => Path.Combine(dataRoot, "automation-config.json");
    public string PendingReceiptPath => Path.Combine(dataRoot, "automation-pending.json");
    public string HandledCyclesPath => Path.Combine(dataRoot, "automation-handled-cycles.json");

    public AutomationStore(string dataRoot)
    {
        if (!IsSafeDPath(dataRoot)) throw new ArgumentException("自动化数据目录必须是 D 盘普通绝对路径", nameof(dataRoot));
        this.dataRoot = Path.GetFullPath(dataRoot).TrimEnd(Path.DirectorySeparatorChar);
    }

    public AutomationConfigurationReadResult LoadConfiguration()
    {
        lock (gate)
        {
            if (!File.Exists(ConfigurationPath))
                return new(true, "耗尽后自动执行尚未启用", new());
            try
            {
                var configuration = ReadPrimary<AutomationConfiguration>(ConfigurationPath)
                    ?? throw new InvalidDataException("配置为空");
                if (!ValidateConfiguration(configuration, out var message))
                    return new(false, message + "；自动执行已停用", new());
                return new(true, "本地操作计划已加载", configuration);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or ArgumentException)
            { return new(false, "自动化主配置不可用；自动执行已停用，请重新保存设置", new()); }
        }
    }

    public bool SaveConfiguration(AutomationConfiguration configuration, out string message)
    {
        lock (gate)
        {
            if (!ValidateConfiguration(configuration, out message)) return false;
            try
            {
                EnsureSafeStore();
                AtomicStore.Write(ConfigurationPath, configuration);
                message = "本地操作计划已保存";
                return true;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or ArgumentException)
            { message = "本地操作计划保存失败，未启用新配置"; return false; }
        }
    }

    static bool ValidateConfiguration(AutomationConfiguration configuration, out string message)
    {
        message = "配置结构无效";
        if (configuration.SchemaVersion != 1 || configuration.Text is null || configuration.ProjectRoot is null) return false;
        if (configuration.Plan is null)
        {
            if (configuration.Enabled || !string.IsNullOrWhiteSpace(configuration.Text)) return false;
            message = "自动执行未启用";
            return string.IsNullOrWhiteSpace(configuration.ProjectRoot) || IsSafeDPath(configuration.ProjectRoot);
        }
        var result = ActionPlanParser.Validate(configuration.Plan);
        if (!result.Success) { message = result.Message; return false; }
        if (!string.Equals(configuration.Text.Trim(), configuration.Plan.Text, StringComparison.Ordinal))
        { message = "操作文本与本地计划不一致"; return false; }
        if (!IsSafeDPath(configuration.ProjectRoot) || !Directory.Exists(configuration.ProjectRoot))
        { message = "项目目录必须是 D 盘现有普通目录"; return false; }
        message = "配置验证通过";
        return true;
    }

    public ExhaustionReceipt? ReadPendingReceipt()
    {
        lock (gate)
        {
            try { return ReadPrimary<ExhaustionReceipt>(PendingReceiptPath); }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or ArgumentException)
            { return null; }
        }
    }

    public void SavePendingReceipt(ExhaustionReceipt receipt)
    {
        lock (gate)
        {
            if (receipt.SchemaVersion != 1 || receipt.EventId == Guid.Empty || !ValidCycle(receipt.CycleKey)
                || receipt.Source != "LiveCodexQuota" || receipt.PlanFingerprint is null || !Regex.IsMatch(receipt.PlanFingerprint, "^[A-F0-9]{64}$"))
                throw new InvalidDataException("耗尽执行凭据结构无效");
            EnsureSafeStore();
            AtomicStore.Write(PendingReceiptPath, receipt);
        }
    }

    public IReadOnlyList<string> LoadHandledCycles()
    {
        lock (gate)
        {
            if (!File.Exists(HandledCyclesPath)) return Array.Empty<string>();
            var state = ReadPrimary<HandledCycleState>(HandledCyclesPath)
                ?? throw new InvalidDataException("耗尽周期记录不可用");
            if (state.SchemaVersion != 1 || state.Cycles is null || state.Cycles.Count > 64 || state.Cycles.Any(key => !ValidCycle(key)))
                throw new InvalidDataException("耗尽周期记录无效；禁止自动执行");
            return state.Cycles.Distinct(StringComparer.Ordinal).ToArray();
        }
    }

    public void MarkCycleHandled(string cycleKey)
    {
        lock (gate)
        {
            if (!ValidCycle(cycleKey)) throw new InvalidDataException("耗尽周期标识无效");
            var cycles = LoadHandledCycles().Where(key => key != cycleKey).Append(cycleKey).TakeLast(64).ToList();
            EnsureSafeStore();
            AtomicStore.Write(HandledCyclesPath, new HandledCycleState { Cycles = cycles });
        }
    }

    void EnsureSafeStore()
    {
        if (!IsSafeDPath(dataRoot) || new[] { ConfigurationPath, PendingReceiptPath, HandledCyclesPath }
            .Any(path => !IsSafeDPath(path) || !IsSafeDPath(path + ".bak")))
            throw new InvalidDataException("自动化目录中存在异常链接或路径");
        Directory.CreateDirectory(dataRoot);
    }

    T? ReadPrimary<T>(string path)
    {
        if (!File.Exists(path)) return default;
        if (!IsSafeDPath(path) || new FileInfo(path).Length > MaximumBytes)
            throw new InvalidDataException("自动化状态文件异常");
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path));
    }

    static bool ValidCycle(string? key) => key is not null && Regex.IsMatch(key, @"^codex:300:\d{1,12}$");

    public static bool IsSafeDPath(string? path) => IsSafeLocalPath(path)
        && Path.GetFullPath(path!).StartsWith(@"D:\", StringComparison.OrdinalIgnoreCase);

    // For passive startup integration only. This does not grant automation authority.
    public static bool IsSafeLocalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal)
            || path.IndexOfAny(['\r', '\n', '\0']) >= 0) return false;
        try
        {
            var full = Path.GetFullPath(path);
            if (Path.GetPathRoot(full)?.Length != 3 || full.Length <= 3 || full.AsSpan(2).Contains(':')) return false;
            for (string? current = full; current is not null; current = Path.GetDirectoryName(current))
            {
                if (File.Exists(current) || Directory.Exists(current))
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
            }
            return true;
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        { return false; }
    }

    public sealed record HandledCycleState
    {
        public int SchemaVersion { get; init; } = 1;
        public List<string> Cycles { get; init; } = [];
    }
}

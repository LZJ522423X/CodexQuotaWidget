using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexQuotaWidget.Core.Automation;

public enum LocalActionKind
{
    CreateRecoverySnapshot, SaveResumeState, CloseApplicationGracefully, CloseProcess,
    StopWindowsService, Wait, VerifyProcessStopped, VerifyServiceStopped,
    ShutdownWindows, RestartWindows, SleepWindows, HibernateWindows
}

public enum LocalTarget { None, CodexDesktop, CodexCli, VisualStudio, VSCode, Rider, ClashVerge, Mihomo, ClashService }
public sealed record LocalAction(LocalActionKind Kind, LocalTarget Target = LocalTarget.None, int Seconds = 0);
public sealed record LocalActionPlan
{
    public int SchemaVersion { get; init; } = 1;
    public string Text { get; init; } = "";
    public List<LocalAction> Actions { get; init; } = [];
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
public sealed record ActionPlanParseResult(bool Success, string Message, LocalActionPlan? Plan = null);

/// <summary>Finite, complete-clause parser. No executable paths, arbitrary commands or shell text are accepted.</summary>
public static class ActionPlanParser
{
    const int MaximumTextLength = 2048;
    static readonly HashSet<LocalTarget> MandatoryTargets = [LocalTarget.VisualStudio, LocalTarget.VSCode, LocalTarget.Rider, LocalTarget.CodexDesktop, LocalTarget.CodexCli];
    static readonly Dictionary<string, LocalTarget> Targets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Codex"] = LocalTarget.CodexDesktop, ["Codex Desktop"] = LocalTarget.CodexDesktop,
        ["Codex CLI"] = LocalTarget.CodexCli, ["Visual Studio"] = LocalTarget.VisualStudio,
        ["VS"] = LocalTarget.VisualStudio, ["VS Code"] = LocalTarget.VSCode, ["VSCode"] = LocalTarget.VSCode,
        ["Rider"] = LocalTarget.Rider, ["Clash"] = LocalTarget.ClashVerge, ["Clash Verge"] = LocalTarget.ClashVerge,
        ["clash-verge"] = LocalTarget.ClashVerge, ["mihomo"] = LocalTarget.Mihomo,
        ["verge-mihomo"] = LocalTarget.Mihomo, ["Clash服务"] = LocalTarget.ClashService,
        ["Clash Verge服务"] = LocalTarget.ClashService, ["clash_verge_service"] = LocalTarget.ClashService
    };

    public static ActionPlanParseResult Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaximumTextLength)
            return Reject("请输入支持的操作（最多 2048 字符）");
        // Each separator denotes a full instruction; unsupported remainder cannot disappear in matching.
        var parts = Regex.Split(text.Trim(), @"(?:\r?\n|[，,；;。]|然后|接着|再)+")
            .Select(part => part.Trim()).Where(part => part.Length > 0).ToArray();
        if (parts.Length is 0 or >32) return Reject("操作数量无效");
        var requested = new List<LocalAction>();
        var closedTargets = new HashSet<LocalTarget>();
        var hasPower = false;
        foreach (var part in parts)
        {
            if (hasPower) return Reject("电源操作必须是最后一步，且只能指定一种");
            if (TryPower(part, out var power))
            {
                requested.Add(new(power)); hasPower = true; continue;
            }
            if (part is "保存项目" or "保存代码" or "创建恢复快照" or "保存恢复快照" or "CreateRecoverySnapshot")
            { requested.Add(new(LocalActionKind.CreateRecoverySnapshot)); continue; }
            if (part is "保存恢复记录" or "保存恢复状态" or "SaveResumeState")
            { requested.Add(new(LocalActionKind.SaveResumeState)); continue; }
            var wait = Regex.Match(part, @"^(?:等待|Wait\s+)(\d{1,3})(?:\s*秒|\s*seconds?)?$", RegexOptions.IgnoreCase);
            if (wait.Success)
            {
                var seconds = int.Parse(wait.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                if (seconds is <1 or >120) return Reject("等待仅支持 1 至 120 秒");
                requested.Add(new(LocalActionKind.Wait, Seconds: seconds)); continue;
            }
            var close = Regex.Match(part, @"^(?:关闭|退出|结束|停止|Close\s+)(.+)$", RegexOptions.IgnoreCase);
            var verify = Regex.Match(part, @"^(?:确认|检查|验证)(.+?)(?:已关闭|已停止|停止|关闭)$", RegexOptions.IgnoreCase);
            if (close.Success || verify.Success)
            {
                var names = Regex.Split((close.Success ? close : verify).Groups[1].Value.Trim(), @"\s*(?:和|及|与|、|\s+and\s+)\s*", RegexOptions.IgnoreCase);
                if (names.Length == 0) return Reject("目标名称不完整");
                foreach (var name in names)
                {
                    if (!Targets.TryGetValue(name.Trim(), out var target)) return Reject("仅支持列表中的应用与服务，请检查目标名称");
                    // These targets already have a single canonical close/wait/verify group.
                    if (MandatoryTargets.Contains(target)) continue;
                    if (verify.Success)
                        requested.Add(new(target == LocalTarget.ClashService ? LocalActionKind.VerifyServiceStopped : LocalActionKind.VerifyProcessStopped, target));
                    else if (closedTargets.Add(target is LocalTarget.ClashService or LocalTarget.Mihomo ? LocalTarget.Mihomo : target))
                        AddClose(requested, target);
                }
                continue;
            }
            return Reject("存在无法识别的指令，请使用已列出的本地操作");
        }
        var actions = new List<LocalAction> { new(LocalActionKind.CreateRecoverySnapshot), new(LocalActionKind.SaveResumeState) };
        // Saving is guaranteed before any process closure. Codex closure does not require Codex assistance.
        AddClose(actions, LocalTarget.VisualStudio);
        AddClose(actions, LocalTarget.VSCode);
        AddClose(actions, LocalTarget.Rider);
        AddClose(actions, LocalTarget.CodexDesktop);
        AddClose(actions, LocalTarget.CodexCli);
        foreach (var action in requested)
        {
            if (action.Kind is LocalActionKind.CreateRecoverySnapshot or LocalActionKind.SaveResumeState) continue;
            if (MandatoryTargets.Contains(action.Target)) continue;
            actions.Add(action);
        }
        if (actions.Count > 96 || actions.Where(action => action.Kind == LocalActionKind.Wait).Sum(action => action.Seconds) > 600)
            return Reject("操作或等待总量超出限制");
        return new(true, "已转换为本地白名单操作计划", new() { Text = text.Trim(), Actions = actions });
    }

    static ActionPlanParseResult Reject(string detail) => new(false, "不支持的操作：" + detail);
    static bool TryPower(string text, out LocalActionKind kind)
    {
        kind = text.ToLowerInvariant() switch
        {
            "关机" or "正常关机" or "关闭电脑" or "shutdownwindows" => LocalActionKind.ShutdownWindows,
            "重启" or "重新启动电脑" or "restartwindows" => LocalActionKind.RestartWindows,
            "睡眠" or "进入睡眠" or "sleepwindows" => LocalActionKind.SleepWindows,
            "休眠" or "进入休眠" or "hibernatewindows" => LocalActionKind.HibernateWindows,
            _ => (LocalActionKind)(-1)
        };
        return Enum.IsDefined(kind);
    }

    static void AddClose(List<LocalAction> actions, LocalTarget target)
    {
        if (target is LocalTarget.ClashService or LocalTarget.Mihomo)
        {
            actions.Add(new(LocalActionKind.StopWindowsService, LocalTarget.ClashService));
            actions.Add(new(LocalActionKind.CloseProcess, LocalTarget.Mihomo));
            actions.Add(new(LocalActionKind.VerifyProcessStopped, LocalTarget.Mihomo));
            actions.Add(new(LocalActionKind.VerifyServiceStopped, LocalTarget.ClashService));
            return;
        }
        actions.Add(new(LocalActionKind.CloseApplicationGracefully, target));
        actions.Add(new(LocalActionKind.Wait, Seconds: 3));
        // Editors may contain unsaved work: they are never force-closed by the planner.
        if (target is LocalTarget.CodexDesktop or LocalTarget.CodexCli or LocalTarget.ClashVerge)
            actions.Add(new(LocalActionKind.CloseProcess, target));
        actions.Add(new(LocalActionKind.VerifyProcessStopped, target));
    }

    public static ActionPlanParseResult Validate(LocalActionPlan? plan)
    {
        if (plan is null || plan.SchemaVersion != 1 || plan.Actions is null || plan.Actions.Count is <1 or >96)
            return Reject("计划结构无效");
        var parsed = Parse(plan.Text);
        if (!parsed.Success || parsed.Plan is null || !parsed.Plan.Actions.SequenceEqual(plan.Actions))
            return Reject("计划与已保存文本不一致，请重新保存设置");
        return new(true, "计划验证通过", plan);
    }

    public static string Fingerprint(LocalActionPlan plan)
    {
        var canonical = JsonSerializer.Serialize(new { plan.SchemaVersion, plan.Text, plan.Actions });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static string Describe(LocalAction action)
    {
        var target = action.Target switch
        {
            LocalTarget.CodexDesktop => "Codex Desktop", LocalTarget.CodexCli => "Codex CLI",
            LocalTarget.VisualStudio => "Visual Studio", LocalTarget.VSCode => "VS Code", LocalTarget.Rider => "Rider",
            LocalTarget.ClashVerge => "Clash Verge", LocalTarget.Mihomo => "mihomo", LocalTarget.ClashService => "Clash Verge 服务",
            _ => ""
        };
        return action.Kind switch
        {
            LocalActionKind.CreateRecoverySnapshot => "创建本地项目恢复快照",
            LocalActionKind.SaveResumeState => "保存恢复记录并验证完整性",
            LocalActionKind.CloseApplicationGracefully => "正常关闭 " + target,
            LocalActionKind.CloseProcess => "检查并结束残留 " + target + " 进程",
            LocalActionKind.StopWindowsService => "停止 " + target + "（需要相应权限）",
            LocalActionKind.Wait => $"等待 {action.Seconds} 秒",
            LocalActionKind.VerifyProcessStopped => "确认 " + target + " 进程已退出",
            LocalActionKind.VerifyServiceStopped => "确认 " + target + " 已停止",
            LocalActionKind.ShutdownWindows => "Windows 正常关机",
            LocalActionKind.RestartWindows => "Windows 正常重启",
            LocalActionKind.SleepWindows => "Windows 睡眠",
            LocalActionKind.HibernateWindows => "Windows 休眠",
            _ => "不支持的操作"
        };
    }
}

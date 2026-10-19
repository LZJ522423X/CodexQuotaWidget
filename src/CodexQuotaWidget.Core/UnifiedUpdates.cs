using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CodexQuotaWidget.Core;

public enum UpdateCheckStatus { Available, UpToDate, Unavailable, UnknownSource }
public sealed record UpdateCheckItem(string Name, string Kind, UpdateCheckStatus Status, string? CurrentVersion, string? LatestVersion, string Reason, Uri? Page = null);
public sealed record SkillCheckItem
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    [JsonRequired] public UpdateCheckStatus Status { get; init; }
    public string? CurrentVersion { get; init; }
    public string? LatestVersion { get; init; }
    public string Reason { get; init; } = "";
    public string? Repo { get; init; }
    public string? RepoId { get; init; }
    public string? Branch { get; init; }
    public string? FromCommit { get; init; }
    public string? ToCommit { get; init; }
}
public sealed record SkillCheckSnapshot
{
    public int SchemaVersion { get; init; }
    public Guid CheckId { get; init; }
    public DateTimeOffset CheckedAt { get; init; }
    public string ProvenanceHash { get; init; } = "";
    public List<SkillCheckItem> Items { get; init; } = [];
    public bool IsFresh(DateTimeOffset now) => CheckedAt <= now.AddMinutes(1) && CheckedAt >= now.AddMinutes(-30);
    public static SkillCheckSnapshot Parse(string json)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        options.Converters.Add(new JsonStringEnumConverter<UpdateCheckStatus>(allowIntegerValues: false));
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("Items", out var items) || items.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Skill 检查未返回完整列表。");
        var result = JsonSerializer.Deserialize<SkillCheckSnapshot>(json, options) ?? throw new InvalidDataException("Skill 检查无结果。");
        if (result.SchemaVersion != 1 || result.CheckId == Guid.Empty || !result.IsFresh(DateTimeOffset.UtcNow)
            || !Regex.IsMatch(result.ProvenanceHash, "\\A[0-9A-Fa-f]{64}\\z") || result.Items.Count > 1500
            || result.Items.Select(i => i.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Items.Count)
            throw new InvalidDataException("Skill 检查身份、时间或来源摘要无效。");
        foreach (var item in result.Items)
        {
            if (string.IsNullOrWhiteSpace(item.Name) || item.Name.Length > 128 || item.Name.Any(char.IsControl)
                || !System.IO.Path.IsPathFullyQualified(item.Path) || !System.IO.Path.GetFileName(item.Path).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase)
                || !Enum.IsDefined(item.Status) || string.IsNullOrWhiteSpace(item.Reason) || item.Reason.Length > 500
                || item.CurrentVersion?.Length > 100 || item.LatestVersion?.Length > 100)
                throw new InvalidDataException("Skill 检查条目不完整。");
            if (item.Status is UpdateCheckStatus.Available or UpdateCheckStatus.UpToDate)
            {
                if (!Reliable(item) || (item.Status == UpdateCheckStatus.UpToDate) != (item.FromCommit == item.ToCommit))
                    throw new InvalidDataException("Skill 缺少可靠的版本或来源证据。");
            }
        }
        return result;
    }
    static bool Reliable(SkillCheckItem item) => item.Name is "humanizer" or "design-taste-frontend" or "career-ops" or "ui-ux-pro-max"
        && Regex.IsMatch(item.Repo ?? "", "\\A[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\\z")
        && Regex.IsMatch(item.RepoId ?? "", "\\A[0-9]+\\z") && Regex.IsMatch(item.Branch ?? "", "\\A[A-Za-z0-9_.-]+\\z")
        && Regex.IsMatch(item.FromCommit ?? "", "\\A[0-9a-f]{40}\\z") && Regex.IsMatch(item.ToCommit ?? "", "\\A[0-9a-f]{40}\\z")
        && item.CurrentVersion == "提交 " + item.FromCommit![..12] && item.LatestVersion == "提交 " + item.ToCommit![..12];
    public object Approve(IEnumerable<string> names, DateTimeOffset now)
    {
        var selected = names.ToArray();
        if (!IsFresh(now) || selected.Length == 0 || selected.Distinct(StringComparer.Ordinal).Count() != selected.Length)
            throw new InvalidDataException("请先检查更新并选择需要安装的 Skill。");
        var rows = selected.Select(name => Items.SingleOrDefault(i => i.Name == name && i.Status == UpdateCheckStatus.Available && Reliable(i))
            ?? throw new InvalidDataException("选择中包含未经确认的更新。")).ToArray();
        return new { SchemaVersion, CheckId, CheckedAt, ProvenanceHash, Confirmed = true, Items = rows };
    }
}
public sealed record UnifiedUpdateCheck(IReadOnlyList<UpdateCheckItem> Items, SkillCheckSnapshot? Skills)
{
    public string Summary => string.Join(" · ", Enum.GetValues<UpdateCheckStatus>().Select(s => Label(s) + " " + Items.Count(i => i.Status == s)));
    public static string Label(UpdateCheckStatus status) => status switch
    { UpdateCheckStatus.Available => "有更新", UpdateCheckStatus.UpToDate => "已是最新", UpdateCheckStatus.UnknownSource => "来源不明", _ => "无法检查" };
    public static async Task<UnifiedUpdateCheck> CheckAsync(Func<CancellationToken, Task<SkillCheckSnapshot>> skills,
        CancellationToken token = default, Func<CancellationToken, Task<ReleaseUpdate>>? release = null)
    {
        async Task<UpdateCheckItem> Widget()
        {
            try
            {
                var r = await (release ?? ReleaseUpdates.CheckAsync)(token).ConfigureAwait(false);
                return new("AI Control Center", "组件", r.State == UpdateAvailability.Available ? UpdateCheckStatus.Available
                    : r.State == UpdateAvailability.NotPublished ? UpdateCheckStatus.Unavailable : UpdateCheckStatus.UpToDate,
                    r.CurrentVersion, r.LatestVersion, r.State == UpdateAvailability.NotPublished ? "仓库暂无稳定公开 Release。"
                    : r.State == UpdateAvailability.LocalNewer ? "本地版本高于公开发布版，不建议覆盖。" : "已核验固定 GitHub 仓库的稳定发布版。", r.Page);
            }
            catch (Exception e) when (e is HttpRequestException or IOException or JsonException or OperationCanceledException or InvalidOperationException or KeyNotFoundException or FormatException)
            { return new("AI Control Center", "组件", UpdateCheckStatus.Unavailable, ReleaseUpdates.CurrentLabel, null, "发布信息检查失败或超时；当前版本未改变。"); }
        }
        async Task<SkillCheckSnapshot?> Skills()
        { try { return await skills(token).ConfigureAwait(false); } catch (Exception e) when (e is IOException or JsonException or OperationCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; } }
        var widget = Widget(); var scan = Skills(); await Task.WhenAll(widget, scan).ConfigureAwait(false);
        var snapshot = await scan;
        List<UpdateCheckItem> rows = [await widget];
        if (snapshot is null) rows.Add(new("Skills", "Skill", UpdateCheckStatus.Unavailable, null, null, "只读更新器无法检查；未使用旧结果或执行安装。"));
        else rows.AddRange(snapshot.Items.Select(i => new UpdateCheckItem(i.Name, "Skill", i.Status, i.CurrentVersion, i.LatestVersion, i.Reason)));
        return new(rows, snapshot);
    }
}

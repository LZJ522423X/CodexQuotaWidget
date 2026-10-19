using System.Text.Json;
using System.Text.Json.Serialization;
using CodexQuotaWidget.Core;

internal static class UnifiedUpdateChecks
{
    public static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); count++; }
        var now = DateTimeOffset.UtcNow;
        var item = new SkillCheckItem { Name = "humanizer", Path = @"C:\Users\FixtureUser\.codex\skills\humanizer\SKILL.md",
            Status = UpdateCheckStatus.Available, Repo = "blader/humanizer", RepoId = "123", Branch = "main",
            FromCommit = new string('a', 40), ToCommit = new string('b', 40), CurrentVersion = "提交 " + new string('a', 12),
            LatestVersion = "提交 " + new string('b', 12), Reason = "offline" };
        var snapshot = new SkillCheckSnapshot { SchemaVersion = 1, CheckId = Guid.NewGuid(), CheckedAt = now,
            ProvenanceHash = new string('c', 64), Items = [item] };
        var options = new JsonSerializerOptions(); options.Converters.Add(new JsonStringEnumConverter());
        string Json(SkillCheckSnapshot s) => JsonSerializer.Serialize(s, options);
        void Reject(Action action, string why)
        { try { action(); } catch (Exception e) when (e is InvalidDataException or JsonException) { count++; return; } throw new InvalidOperationException(why); }
        Check(SkillCheckSnapshot.Parse(Json(snapshot)).Items.Single() == item, "exact source and commit evidence survives read-only protocol");
        Reject(() => SkillCheckSnapshot.Parse("{}"), "missing result cannot mean latest");
        Reject(() => SkillCheckSnapshot.Parse(Json(snapshot).Replace("\"Status\":\"Available\",", "", StringComparison.Ordinal)), "missing status cannot default to available");
        Reject(() => SkillCheckSnapshot.Parse(Json(snapshot with { SchemaVersion = 2 })), "unknown schema");
        Reject(() => SkillCheckSnapshot.Parse(Json(snapshot with { CheckId = Guid.Empty })), "missing check identity");
        Reject(() => SkillCheckSnapshot.Parse(Json(snapshot with { ProvenanceHash = "unknown" })), "unknown provenance");
        Reject(() => SkillCheckSnapshot.Parse(Json(snapshot with { CheckedAt = now.AddHours(-1) })), "stale result");
        Reject(() => SkillCheckSnapshot.Parse(Json(snapshot with { CheckedAt = now.AddHours(1) })), "future result");
        Reject(() => SkillCheckSnapshot.Parse(Json(snapshot with { Items = [item, item] })), "duplicate source path");
        foreach (var bad in new[] { item with { Repo = null }, item with { RepoId = null }, item with { Branch = "../" }, item with { ToCommit = "latest" },
            item with { CurrentVersion = "v9 guessed" }, item with { LatestVersion = null }, item with { Name = "unknown-new-skill" }, item with { FromCommit = item.ToCommit },
            item with { Status = UpdateCheckStatus.UpToDate }, item with { Reason = "" }, item with { Path = "relative/SKILL.md" } })
            Reject(() => SkillCheckSnapshot.Parse(Json(snapshot with { Items = [bad] })), "unreliable evidence cannot be installable/latest");
        var latest = item with { ToCommit = item.FromCommit, LatestVersion = item.CurrentVersion, Status = UpdateCheckStatus.UpToDate };
        var unavailable = item with { Name = "local", Status = UpdateCheckStatus.Unavailable, FromCommit = null, ToCommit = null, CurrentVersion = null, LatestVersion = null };
        var unknown = unavailable with { Name = "unregistered", Status = UpdateCheckStatus.UnknownSource };
        foreach (var row in new[] { latest, unavailable, unknown }) Check(SkillCheckSnapshot.Parse(Json(snapshot with { Items = [row] })).Items[0].Status == row.Status, "distinct check outcome retained");
        Reject(() => snapshot.Approve([], now), "no selection cannot install");
        Reject(() => snapshot.Approve(["humanizer", "humanizer"], now), "duplicate selection");
        Reject(() => snapshot.Approve(["unregistered"], now), "unknown cannot install");
        Reject(() => snapshot.Approve(["humanizer"], now.AddHours(1)), "expired approval");
        foreach (var row in new[] { latest, unavailable, unknown }) Reject(() => (snapshot with { Items = [row] }).Approve([row.Name], now), "non-update cannot install");
        using var approval = JsonDocument.Parse(JsonSerializer.Serialize(snapshot.Approve(["humanizer"], now), options));
        Check(approval.RootElement.GetProperty("Items").GetArrayLength() == 1
            && approval.RootElement.GetProperty("Items")[0].GetProperty("ToCommit").GetString() == item.ToCommit, "approval includes selected pinned commit only");
        foreach (var state in Enum.GetValues<UpdateAvailability>())
        {
            var result = await UnifiedUpdateCheck.CheckAsync(_ => Task.FromResult(snapshot), release: _ => Task.FromResult(new ReleaseUpdate(state, "1.12.7", "1.12.8", null)));
            Check(result.Items.Count == 2 && result.Skills == snapshot, "component and Skill share check result");
            Check(result.Items[0].Status == (state == UpdateAvailability.Available ? UpdateCheckStatus.Available
                : state == UpdateAvailability.NotPublished ? UpdateCheckStatus.Unavailable : UpdateCheckStatus.UpToDate), "release states mapped honestly");
        }
        foreach (var error in new Exception[] { new HttpRequestException("offline"), new TaskCanceledException("timeout"), new JsonException("bad payload"), new KeyNotFoundException("missing tag") })
        {
            var result = await UnifiedUpdateCheck.CheckAsync(_ => Task.FromResult(snapshot), release: _ => Task.FromException<ReleaseUpdate>(error));
            Check(result.Items[0].Status == UpdateCheckStatus.Unavailable && result.Items[1].Status == UpdateCheckStatus.Available, "component failure does not hide Skill result or become latest");
        }
        var brokenSkills = await UnifiedUpdateCheck.CheckAsync(_ => Task.FromException<SkillCheckSnapshot>(new IOException("fixture")),
            release: _ => Task.FromResult(new ReleaseUpdate(UpdateAvailability.UpToDate, "1.12.7", "1.12.7", null)));
        Check(brokenSkills.Skills is null && brokenSkills.Items[0].Status == UpdateCheckStatus.UpToDate && brokenSkills.Items[1].Status == UpdateCheckStatus.Unavailable, "Skill failure independent of component");
        Check(Enum.GetValues<UpdateCheckStatus>().Select(UnifiedUpdateCheck.Label).Distinct().Count() == 4, "four distinct user-facing outcomes");
        return count;
    }
}

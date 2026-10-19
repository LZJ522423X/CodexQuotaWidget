using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace CodexQuotaWidget.Core;

public enum UpdateAvailability { UpToDate, Available, LocalNewer, NotPublished }
public sealed record ReleaseUpdate(UpdateAvailability State, string CurrentVersion, string? LatestVersion, Uri? Page);

public static class ReleaseUpdates
{
    public const string RepositoryPage = "https://github.com/LZJ522423X/CodexQuotaWidget";
    public static Version CurrentVersion => typeof(ReleaseUpdates).Assembly.GetName().Version!;
    public static string CurrentLabel => CurrentVersion.ToString(3);
    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false })
            { Timeout = TimeSpan.FromSeconds(12), MaxResponseContentBufferSize = 1024 * 1024 };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CodexQuotaWidget/" + CurrentLabel);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public static async Task<ReleaseUpdate> CheckAsync(CancellationToken cancellation = default)
    {
        using var response = await Client.GetAsync("https://api.github.com/repos/LZJ522423X/CodexQuotaWidget/releases/latest", cancellation);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return new(UpdateAvailability.NotPublished, CurrentLabel, null, null);
        response.EnsureSuccessStatusCode();
        return Parse(await response.Content.ReadAsStringAsync(cancellation), CurrentVersion);
    }

    public static ReleaseUpdate Parse(string json, Version current)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("prerelease", out var preview) && preview.ValueKind == JsonValueKind.True ||
            root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
            throw new InvalidDataException("A stable published release was expected.");
        var tag = root.GetProperty("tag_name").GetString();
        if (tag is null || tag.Length > 64 || !Version.TryParse(tag.TrimStart('v', 'V'), out var latest))
            throw new InvalidDataException("Unsupported release version.");
        static Version Normalize(Version value) => new(value.Major, value.Minor, Math.Max(0, value.Build), Math.Max(0, value.Revision));
        var comparison = Normalize(latest).CompareTo(Normalize(current));
        var state = comparison > 0 ? UpdateAvailability.Available : comparison < 0 ? UpdateAvailability.LocalNewer : UpdateAvailability.UpToDate;
        // Construct the destination from the fixed repository, never from response-provided URLs.
        var page = new Uri(RepositoryPage + "/releases/tag/" + Uri.EscapeDataString(tag));
        return new(state, Normalize(current).ToString(3), Normalize(latest).ToString(3), page);
    }
}

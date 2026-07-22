using System.Net.Http.Json;
using System.Net.Http;
using System.Net;
using System.Reflection;
using System.Text.Json.Serialization;

namespace ListaryOpen.Infrastructure.AppData;

public sealed record UpdateCheckResult(
    bool UpdateAvailable,
    string CurrentVersion,
    string LatestVersion,
    string ReleaseUrl,
    string Message);

public sealed class GitHubUpdateChecker
{
    public const string LatestReleaseEndpoint =
        "https://api.github.com/repos/kermit-r-wood/listary-open/releases/latest";

    private readonly HttpClient _httpClient;
    private readonly Func<Version> _currentVersion;

    public GitHubUpdateChecker(HttpClient? httpClient = null, Func<Version>? currentVersion = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ListaryOpen/1.0");
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        _currentVersion = currentVersion ?? (() => Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0));
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        ReleaseDocument? release;
        try
        {
            release = await _httpClient.GetFromJsonAsync<ReleaseDocument>(LatestReleaseEndpoint, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            var currentText = _currentVersion().ToString(3);
            return new UpdateCheckResult(
                false,
                currentText,
                currentText,
                "https://github.com/kermit-r-wood/listary-open/releases",
                "No published GitHub Releases were found.");
        }

        if (release is null || string.IsNullOrWhiteSpace(release.TagName))
        {
            throw new InvalidOperationException("GitHub returned an empty release response.");
        }

        var current = _currentVersion();
        var latestText = release.TagName.Trim().TrimStart('v', 'V');
        var latest = Version.TryParse(latestText, out var parsed) ? parsed : new Version(0, 0);
        var available = latest > current;
        return new UpdateCheckResult(
            available,
            current.ToString(3),
            release.TagName,
            release.HtmlUrl ?? "https://github.com/kermit-r-wood/listary-open/releases",
            available ? $"Update {release.TagName} is available." : "ListaryOpen is up to date.");
    }

    private sealed record ReleaseDocument(
        [property: JsonPropertyName("tag_name")] string? TagName,
        [property: JsonPropertyName("html_url")] string? HtmlUrl);
}

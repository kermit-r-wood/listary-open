using System.Net;
using System.Net.Http;
using System.Text;
using ListaryOpen.Infrastructure.AppData;

namespace ListaryOpen.Infrastructure.Tests.App;

public sealed class GitHubUpdateCheckerTests
{
    [Fact]
    public async Task LatestReleaseResponseIsComparedWithCurrentVersion()
    {
        using var client = new HttpClient(new JsonHandler("""{"tag_name":"v2.1.0","html_url":"https://github.com/example/release"}"""));
        var checker = new GitHubUpdateChecker(client, () => new Version(2, 0, 0));

        var result = await checker.CheckAsync(CancellationToken.None);

        Assert.True(result.UpdateAvailable);
        Assert.Equal("v2.1.0", result.LatestVersion);
        Assert.Equal("https://github.com/example/release", result.ReleaseUrl);
    }

    [Fact]
    public async Task MissingLatestReleaseIsReportedAsNoPublishedRelease()
    {
        using var client = new HttpClient(new StatusHandler(HttpStatusCode.NotFound));
        var checker = new GitHubUpdateChecker(client, () => new Version(1, 2, 3));

        var result = await checker.CheckAsync(CancellationToken.None);

        Assert.False(result.UpdateAvailable);
        Assert.Equal("1.2.3", result.CurrentVersion);
        Assert.Contains("No published", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("/releases", result.ReleaseUrl, StringComparison.Ordinal);
    }

    private sealed class JsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
    }

    private sealed class StatusHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode));
    }
}

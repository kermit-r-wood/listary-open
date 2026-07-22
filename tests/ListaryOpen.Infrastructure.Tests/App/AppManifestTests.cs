namespace ListaryOpen.Infrastructure.Tests.App;

public sealed class AppManifestTests
{
    [Fact]
    public void DefaultAppManifestRequestsAdministratorOnceAtStartup()
    {
        var manifest = File.ReadAllText(GetRepositoryPath("src", "ListaryOpen.App", "app.manifest"));

        Assert.Contains("requestedExecutionLevel level=\"requireAdministrator\"", manifest);
    }

    [Fact]
    public void NoAdminTestManifestRunsAsInvoker()
    {
        var manifest = File.ReadAllText(GetRepositoryPath("src", "ListaryOpen.App", "app.no-admin.manifest"));

        Assert.Contains("requestedExecutionLevel level=\"asInvoker\"", manifest);
        Assert.DoesNotContain("requireAdministrator", manifest);
    }

    [Fact]
    public void AppProjectSupportsAppOnlyManifestOverride()
    {
        var project = File.ReadAllText(GetRepositoryPath("src", "ListaryOpen.App", "ListaryOpen.App.csproj"));

        Assert.Contains("ListaryOpenAppManifest", project);
        Assert.Contains("$(ListaryOpenAppManifest)", project);
        Assert.Contains(">app.manifest</ListaryOpenAppManifest>", project);
    }

    [Theory]
    [InlineData("app.manifest")]
    [InlineData("app.no-admin.manifest")]
    public void AppManifestsEnablePerMonitorV2DpiAwareness(string manifestName)
    {
        var manifest = File.ReadAllText(GetRepositoryPath("src", "ListaryOpen.App", manifestName));

        Assert.Contains(">true/pm</dpiAware>", manifest);
        Assert.Contains(">PerMonitorV2,PerMonitor</dpiAwareness>", manifest);
    }

    private static string GetRepositoryPath(params string[] segments)
    {
        return Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            Path.Combine(segments)));
    }
}

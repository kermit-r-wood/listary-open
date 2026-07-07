namespace ListaryOpen.Infrastructure.Tests.App;

public sealed class AppManifestTests
{
    [Fact]
    public void DefaultAppManifestRequiresAdministrator()
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
        Assert.Contains("app.manifest", project);
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

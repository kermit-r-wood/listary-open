namespace ListaryOpen.Infrastructure.Tests.App;

public sealed class AppIconTests
{
    [Fact]
    public void AppProjectDefinesExecutableIcon()
    {
        var project = File.ReadAllText(GetRepositoryPath("src", "ListaryOpen.App", "ListaryOpen.App.csproj"));

        Assert.Contains("<ApplicationIcon>Assets\\ListaryOpen.ico</ApplicationIcon>", project);
        Assert.Contains("<Resource Include=\"Assets\\ListaryOpen.ico\" />", project);
        Assert.True(File.Exists(GetRepositoryPath("src", "ListaryOpen.App", "Assets", "ListaryOpen.ico")));
    }

    [Fact]
    public void AppDefinesSharedIconResource()
    {
        var appXaml = File.ReadAllText(GetRepositoryPath("src", "ListaryOpen.App", "App.xaml"));

        Assert.Contains("ListaryOpenIcon", appXaml);
        Assert.Contains("pack://application:,,,/Assets/ListaryOpen.ico", appXaml);
    }

    [Theory]
    [InlineData("MainWindow.xaml")]
    [InlineData("SearchPanel.xaml")]
    public void WindowsUseSharedIconResource(string fileName)
    {
        var xaml = File.ReadAllText(GetRepositoryPath("src", "ListaryOpen.App", fileName));

        Assert.Contains("Icon=\"{StaticResource ListaryOpenIcon}\"", xaml);
    }

    [Fact]
    public void TrayControllerUsesSharedIconResource()
    {
        var source = File.ReadAllText(GetRepositoryPath("src", "ListaryOpen.App", "Tray", "TrayController.cs"));

        Assert.Contains("ListaryOpenIcon", source);
        Assert.Contains("IconSource", source);
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

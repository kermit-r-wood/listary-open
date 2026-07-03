namespace ListaryOpen.Infrastructure.Tests.App;

public sealed class AppIconTests
{
    [Fact]
    public void AppDefinesSharedIconResource()
    {
        var appXaml = File.ReadAllText(GetRepositoryPath("src", "ListaryOpen.App", "App.xaml"));

        Assert.Contains("ListaryOpenIcon", appXaml);
        Assert.Contains("DrawingImage", appXaml);
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

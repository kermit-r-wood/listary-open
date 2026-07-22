namespace ListaryOpen.Infrastructure.Tests.App;

public sealed class QuickSwitchBarXamlTests
{
    [Fact]
    public void QuickSwitchBarUsesDedicatedCompactTransparentDesign()
    {
        var xaml = File.ReadAllText(GetRepositoryPath("src", "ListaryOpen.App", "QuickSwitchBarWindow.xaml"));

        Assert.Contains("AllowsTransparency=\"True\"", xaml);
        Assert.Contains("WindowStyle=\"None\"", xaml);
        Assert.Contains("CornerRadius=\"0,0,9,9\"", xaml);
        Assert.Contains("Search folders or enter a path", xaml);
        Assert.Contains("Style=\"{StaticResource OverlaySearchTextBoxStyle}\"", xaml);
        Assert.Contains("MaxHeight=\"312\"", xaml);
        Assert.Contains("ScrollViewer.HorizontalScrollBarVisibility=\"Disabled\"", xaml);
        Assert.Contains("Header=\"Switch to this folder\"", xaml);
        Assert.Contains("Header=\"Show in File Explorer\"", xaml);
        Assert.Contains("Header=\"Copy full path\"", xaml);
        Assert.Contains("AreResultsVisible", xaml);
        Assert.Contains("MouseLeftButtonUp=\"Results_MouseLeftButtonUp\"", xaml);
        Assert.Contains("PreviewMouseRightButtonUp=\"Results_PreviewMouseRightButtonUp\"", xaml);
        Assert.Contains("PreviewMouseLeftButtonDown=\"Results_PreviewMouseLeftButtonDown\"", xaml);
        Assert.Contains("StaysOpen=\"False\"", xaml);
        Assert.DoesNotContain("PerformanceText", xaml);
        Assert.DoesNotContain("ModeDisplayText", xaml);
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

namespace ListaryOpen.Infrastructure.Tests.App;

public sealed class SettingsXamlTests
{
    [Fact]
    public void SettingsWindowDefinesOperationalSectionsAndPresentationBindings()
    {
        var xaml = File.ReadAllText(GetRepositoryPath("src", "ListaryOpen.App", "MainWindow.xaml"));

        Assert.Contains("x:Name=\"IndexingSection\"", xaml);
        Assert.Contains("x:Name=\"HotkeysSection\"", xaml);
        Assert.Contains("x:Name=\"QuickSwitchSection\"", xaml);
        Assert.Contains("x:Name=\"GeneralSection\"", xaml);
        Assert.Contains("IndexingBadgeText", xaml);
        Assert.Contains("NtfsFastIndexingBadgeText", xaml);
        Assert.Contains("NtfsFastIndexingActionText", xaml);
        Assert.Contains("HookQuickSwitchBadgeText", xaml);
        Assert.Contains("HookQuickSwitchActionText", xaml);
        Assert.Contains("QuickSaveOpenBadgeText", xaml);
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

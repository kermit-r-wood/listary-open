using ListaryOpen.Core.Settings;

namespace ListaryOpen.Core.Tests.Settings;

public sealed class AppSettingsTests
{
    [Fact]
    public void DefaultsUseExpectedStartupSettings()
    {
        var settings = AppSettings.Defaults();

        Assert.Equal(new[] { Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) }, settings.IndexedRoots);
        Assert.Empty(settings.ExcludedPaths);
        Assert.Empty(settings.PinnedFolders);
        Assert.Equal("Ctrl+Space", settings.SearchHotkey);
        Assert.Equal("Ctrl+G", settings.DialogHotkey);
        Assert.True(settings.QuickSaveOpenEnabled);
    }
}

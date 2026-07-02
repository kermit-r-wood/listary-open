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

    [Fact]
    public void ConstructorCopiesIndexedRoots()
    {
        var indexedRoots = new List<string> { "C:\\Users" };
        var settings = new AppSettings(
            indexedRoots,
            Array.Empty<string>(),
            Array.Empty<string>(),
            "Ctrl+Space",
            "Ctrl+G",
            true);

        indexedRoots.Add("D:\\Projects");

        Assert.Equal(new[] { "C:\\Users" }, settings.IndexedRoots);
    }

    [Fact]
    public void ConstructorCopiesExcludedPaths()
    {
        var excludedPaths = new List<string> { "C:\\Temp" };
        var settings = new AppSettings(
            Array.Empty<string>(),
            excludedPaths,
            Array.Empty<string>(),
            "Ctrl+Space",
            "Ctrl+G",
            true);

        excludedPaths.Add("D:\\Cache");

        Assert.Equal(new[] { "C:\\Temp" }, settings.ExcludedPaths);
    }

    [Fact]
    public void ConstructorCopiesPinnedFolders()
    {
        var pinnedFolders = new List<string> { "C:\\Work" };
        var settings = new AppSettings(
            Array.Empty<string>(),
            Array.Empty<string>(),
            pinnedFolders,
            "Ctrl+Space",
            "Ctrl+G",
            true);

        pinnedFolders.Add("D:\\Archive");

        Assert.Equal(new[] { "C:\\Work" }, settings.PinnedFolders);
    }

    [Fact]
    public void DefaultsExposeReadOnlyCollectionSnapshots()
    {
        var settings = AppSettings.Defaults();

        Assert.False(settings.IndexedRoots is string[]);
        Assert.False(settings.IndexedRoots is List<string>);
        Assert.False(settings.ExcludedPaths is string[]);
        Assert.False(settings.ExcludedPaths is List<string>);
        Assert.False(settings.PinnedFolders is string[]);
        Assert.False(settings.PinnedFolders is List<string>);
    }
}

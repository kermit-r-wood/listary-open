using ListaryOpen.Core.Settings;

namespace ListaryOpen.Core.Tests.Settings;

public sealed class AppSettingsTests
{
    [Fact]
    public void DefaultsUseExpectedStartupSettings()
    {
        var settings = AppSettings.Defaults();

        var readyDriveRoots = DriveInfo.GetDrives()
            .Where(drive => drive.IsReady)
            .Select(drive => drive.Name)
            .ToArray();
        Assert.Equal(readyDriveRoots, settings.IndexedRoots);
        Assert.NotEmpty(settings.IndexedRoots);
        Assert.Empty(settings.ExcludedPaths);
        Assert.Empty(settings.PinnedFolders);
        Assert.Equal("Ctrl+Space", settings.SearchHotkey);
        Assert.Equal("Ctrl+G", settings.DialogHotkey);
        Assert.True(settings.QuickSaveOpenEnabled);
        Assert.Equal(AppTheme.System, settings.Theme);
        Assert.Equal(IndexUpdateFrequency.StartupOnly, settings.IndexFrequency);
        Assert.NotEmpty(settings.QuickMenuEntries);
        Assert.True(settings.CheckForUpdates);
        Assert.Equal(AppLanguage.System, settings.Language);
    }

    [Fact]
    public void PreferenceUpdatePreservesAndCanChangeInterfaceLanguage()
    {
        var defaults = AppSettings.Defaults();
        var chinese = defaults.WithPreferences(
            defaults.IndexedRoots,
            defaults.ExcludedPaths,
            defaults.Theme,
            defaults.IndexFrequency,
            defaults.QuickMenuEntries,
            defaults.CheckForUpdates,
            language: AppLanguage.SimplifiedChinese);

        Assert.Equal(AppLanguage.SimplifiedChinese, chinese.Language);
        Assert.Equal(AppLanguage.SimplifiedChinese, chinese.WithHotkeys("Ctrl+F", "Ctrl+G").Language);
    }

    [Fact]
    public void UpdateCheckPreferenceCanBeChangedWithoutChangingOtherSettings()
    {
        var settings = AppSettings.Defaults().WithPreferences(
            AppSettings.Defaults().IndexedRoots,
            ["*.tmp"],
            AppTheme.Dark,
            IndexUpdateFrequency.Hourly,
            AppSettings.Defaults().QuickMenuEntries,
            true,
            language: AppLanguage.SimplifiedChinese);

        var updated = settings.WithCheckForUpdates(false);

        Assert.False(updated.CheckForUpdates);
        Assert.Equal(settings.IndexedRoots, updated.IndexedRoots);
        Assert.Equal(settings.ExcludedPaths, updated.ExcludedPaths);
        Assert.Equal(settings.Theme, updated.Theme);
        Assert.Equal(settings.IndexFrequency, updated.IndexFrequency);
        Assert.Equal(settings.Language, updated.Language);
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

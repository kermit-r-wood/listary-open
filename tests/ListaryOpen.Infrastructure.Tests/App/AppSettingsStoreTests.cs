using ListaryOpen.Core.Settings;
using ListaryOpen.Infrastructure.AppData;

namespace ListaryOpen.Infrastructure.Tests.App;

public sealed class AppSettingsStoreTests
{
    [Fact]
    public void SaveAndLoadRoundTripsCustomHotkeys()
    {
        var directory = Directory.CreateTempSubdirectory("listary-settings-");
        var path = Path.Combine(directory.FullName, "settings.json");
        try
        {
            var store = new AppSettingsStore();
            store.Save(path, AppSettings.Defaults().WithHotkeys("Alt+F1", "Ctrl+Shift+D"));

            var loaded = store.Load(path);

            Assert.Equal("Alt+F1", loaded.SearchHotkey);
            Assert.Equal("Ctrl+Shift+D", loaded.DialogHotkey);
            Assert.Contains("\"version\": 4", File.ReadAllText(path));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void LoadMigratesLegacyUserProfileDefaultToAllReadyDrives()
    {
        var path = Path.GetTempFileName();
        try
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                .Replace("\\", "\\\\", StringComparison.Ordinal);
            File.WriteAllText(path, $$"""
                { "version": 3, "indexedRoots": ["{{userProfile}}"] }
                """);

            var loaded = new AppSettingsStore().Load(path);

            Assert.Equal(AppSettings.Defaults().IndexedRoots, loaded.IndexedRoots);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadPreservesExplicitVersionFourUserProfileScope()
    {
        var path = Path.GetTempFileName();
        try
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                .Replace("\\", "\\\\", StringComparison.Ordinal);
            File.WriteAllText(path, $$"""
                { "version": 4, "indexedRoots": ["{{userProfile}}"] }
                """);

            var loaded = new AppSettingsStore().Load(path);

            Assert.Equal(new[] { Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) }, loaded.IndexedRoots);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SaveAndLoadRoundTripsAppearanceIndexAndMenuSettings()
    {
        var directory = Directory.CreateTempSubdirectory("listary-settings-v2-");
        var path = Path.Combine(directory.FullName, "settings.json");
        try
        {
            var customized = AppSettings.Defaults().WithPreferences(
                ["C:\\", "D:\\Projects"],
                ["node_modules", "*.tmp"],
                AppTheme.Geek,
                IndexUpdateFrequency.Hourly,
                [new QuickMenuEntry(
                    "work",
                    "Work",
                    QuickMenuAction.RunCommand,
                    "code.exe",
                    Arguments: ".",
                    WorkingDirectory: "D:\\Work",
                    Silent: true,
                    RunAsAdmin: true)],
                checkForUpdates: false,
                quickLaunchEntries:
                [
                    new QuickLaunchEntry(
                        "launch-note",
                        "note",
                        "Notepad",
                        "%SystemRoot%\\System32\\notepad.exe",
                        Arguments: "readme.txt",
                        WorkingDirectory: "D:\\Work")
                ],
                searchTransliteration: SearchTransliterationMode.ChinesePinyin,
                language: AppLanguage.SimplifiedChinese);

            var store = new AppSettingsStore();
            store.Save(path, customized);
            var loaded = store.Load(path);

            Assert.Equal(AppTheme.Geek, loaded.Theme);
            Assert.Equal(IndexUpdateFrequency.Hourly, loaded.IndexFrequency);
            Assert.Equal(new[] { "C:\\", "D:\\Projects" }, loaded.IndexedRoots);
            Assert.Equal(new[] { "node_modules", "*.tmp" }, loaded.ExcludedPaths);
            var menuEntry = Assert.Single(loaded.QuickMenuEntries);
            Assert.Equal("Work", menuEntry.Title);
            Assert.Equal(QuickMenuAction.RunCommand, menuEntry.Action);
            Assert.Equal(".", menuEntry.Arguments);
            Assert.Equal("D:\\Work", menuEntry.WorkingDirectory);
            Assert.True(menuEntry.Silent);
            Assert.True(menuEntry.RunAsAdmin);
            var launchEntry = Assert.Single(loaded.QuickLaunchEntries);
            Assert.Equal("note", launchEntry.Keyword);
            Assert.Equal("Notepad", launchEntry.Title);
            Assert.Equal("readme.txt", launchEntry.Arguments);
            Assert.Equal("D:\\Work", launchEntry.WorkingDirectory);
            Assert.False(loaded.CheckForUpdates);
            Assert.Equal(SearchTransliterationMode.ChinesePinyin, loaded.SearchTransliteration);
            Assert.Equal(AppLanguage.SimplifiedChinese, loaded.Language);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void LoadFallsBackToDefaultsForInvalidHotkeyDocument()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """
                { "version": 1, "searchHotkey": "invalid", "dialogHotkey": "also-invalid" }
                """);

            var loaded = new AppSettingsStore().Load(path);

            Assert.Equal(AppSettings.Defaults().SearchHotkey, loaded.SearchHotkey);
            Assert.Equal(AppSettings.Defaults().DialogHotkey, loaded.DialogHotkey);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

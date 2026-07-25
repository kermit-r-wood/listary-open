namespace ListaryOpen.Infrastructure.Tests.App;

public sealed class SettingsXamlTests
{
    [Fact]
    public void SettingsWindowDefinesOperationalSectionsAndPresentationBindings()
    {
        var xaml = File.ReadAllText(GetRepositoryPath("src", "ListaryOpen.App", "MainWindow.xaml"));

        Assert.Contains("Header=\"General\" Tag=\"&#xE713;\"", xaml);
        Assert.Contains("Header=\"Appearance\"", xaml);
        Assert.Contains("Tag=\"&#xE790;\"", xaml);
        Assert.Contains("Header=\"Hotkeys\" Tag=\"&#xE765;\"", xaml);
        Assert.Contains("Header=\"File Search\" Tag=\"&#xE721;\"", xaml);
        Assert.Contains("Header=\"Language\" Tag=\"&#xE8C1;\"", xaml);
        Assert.Contains("Header=\"Actions\" Tag=\"&#xE74C;\"", xaml);
        Assert.Contains("Header=\"Quick Launch\" Tag=\"&#xE768;\"", xaml);
        Assert.Contains("Header=\"Menu\" Tag=\"&#xE700;\"", xaml);
        Assert.Contains("Header=\"About\" Tag=\"&#xE946;\"", xaml);
        Assert.Contains("IsIndeterminate=\"True\"", xaml);
        Assert.Contains("IndexingProgressText", xaml);
        Assert.Contains("CurrentIndexRootText", xaml);
        Assert.Contains("NtfsFastIndexingActionText", xaml);
        Assert.Contains("HookQuickSwitchActionText", xaml);
        Assert.Contains("BooleanToVisibilityConverter", xaml);
        Assert.Contains("SearchHotkeyText", xaml);
        Assert.Contains("DialogHotkeyText", xaml);
        Assert.Contains("SaveHotkeysCommand", xaml);
        Assert.Contains("PreviewKeyDown=\"HotkeyBox_PreviewKeyDown\"", xaml);
        Assert.Contains("SelectedTheme", xaml);
        Assert.Contains("ExcludedPathsText", xaml);
        Assert.Contains("SelectedIndexFrequency", xaml);
        Assert.Contains("Display Language", xaml);
        Assert.Contains("UiLanguageSelector", xaml);
        Assert.Contains("SelectedLanguage", xaml);
        Assert.DoesNotContain("Search Languages", xaml);
        Assert.Contains("Full-disk indexing", xaml);
        Assert.Contains("File changes are monitored continuously", xaml);
        Assert.Contains("fallback checks for missed changes", xaml);
        Assert.Contains("MenuEntries", xaml);
        Assert.Contains("SelectedMenuEntry.Arguments", xaml);
        Assert.Contains("SelectedMenuEntry.WorkingDirectory", xaml);
        Assert.Contains("SelectedMenuEntry.RunAsAdmin", xaml);
        Assert.Contains("SettingsOptionDisplayConverter", xaml);
        Assert.Contains("x:Name=\"SettingsNavigation\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"SettingsWindow\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"SettingsNavigation\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"SettingsAppearanceTab\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"SettingsThemeSelector\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"SettingsSaveAppearance\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"SettingsSearchHotkey\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"SettingsDialogHotkey\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"SettingsApplyHotkeys\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"SettingsLanguageSelector\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"SettingsSaveLanguage\"", xaml);
        Assert.Contains("ContentSource=\"Header\"", xaml);
        Assert.Contains("SettingsPageScrollViewerStyle", xaml);
        Assert.Contains("HorizontalContentAlignment=\"Stretch\"", xaml);
        Assert.Contains("SettingsStatusBadgeStyle", xaml);
        Assert.Contains("IsNtfsFastIndexingActionVisible", xaml);
        Assert.Contains("IsHookQuickSwitchActionVisible", xaml);
        Assert.Contains("HookQuickSwitchBadgeText", xaml);
        Assert.Contains("CheckForUpdatesCommand", xaml);
        Assert.Contains("SaveUpdatePreferenceCommand", xaml);
        Assert.Contains("Changes to this option are saved immediately.", xaml);
        Assert.DoesNotContain("Content=\"Apply settings\"", xaml);
        Assert.Contains("OpenReleaseCommand", xaml);
        Assert.Contains("HasReleaseUrl", xaml);
    }

    [Fact]
    public void SettingsWindowUsesWpfAntiAliasedRoundedChrome()
    {
        var xaml = File.ReadAllText(GetRepositoryPath("src", "ListaryOpen.App", "MainWindow.xaml"));
        var code = File.ReadAllText(GetRepositoryPath("src", "ListaryOpen.App", "MainWindow.xaml.cs"));

        // Per-pixel alpha + Border.CornerRadius (not SetWindowRgn) keeps Win10 corners smooth.
        Assert.Contains("WindowStyle=\"None\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AllowsTransparency=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Background=\"Transparent\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"WindowChromeBorder\"", xaml, StringComparison.Ordinal);
        Assert.Contains("CornerRadius=\"12\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SettingsTitleBar\"", xaml, StringComparison.Ordinal);
        Assert.Contains("NativeWindowCorner.ClearRoundedChrome", code, StringComparison.Ordinal);
        Assert.DoesNotContain("NativeWindowCorner.ApplyRounded", code, StringComparison.Ordinal);
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

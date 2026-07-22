using System.ComponentModel;
using ListaryOpen.App.ViewModels;
using ListaryOpen.Core.Settings;
using ListaryOpen.Infrastructure.Indexing;

namespace ListaryOpen.Infrastructure.Tests.App;

public sealed class SettingsViewModelTests
{
    [Fact]
    public void SavePreferencesPersistsThemeFrequencyExclusionsAndMenu()
    {
        AppSettings? applied = null;
        var viewModel = new SettingsViewModel(
            AppSettings.Defaults(),
            enableNtfsFastIndexing: null,
            enableHookQuickSwitch: null,
            applySettings: settings => { applied = settings; return null; });
        viewModel.SelectedTheme = AppTheme.Dark;
        viewModel.SelectedIndexFrequency = IndexUpdateFrequency.Every15Minutes;
        viewModel.ExcludedPathsText = "node_modules\n*.tmp";
        viewModel.MenuEntries[0].Title = "Favorites";

        viewModel.SavePreferencesCommand.Execute(null);

        Assert.NotNull(applied);
        Assert.Equal(AppTheme.Dark, applied!.Theme);
        Assert.Equal(IndexUpdateFrequency.Every15Minutes, applied.IndexFrequency);
        Assert.Equal(new[] { "node_modules", "*.tmp" }, applied.ExcludedPaths);
        Assert.Equal("Favorites", applied.QuickMenuEntries[0].Title);
    }

    [Fact]
    public void SavePreferencesPersistsSearchTransliterationMode()
    {
        AppSettings? applied = null;
        var viewModel = new SettingsViewModel(
            AppSettings.Defaults(),
            enableNtfsFastIndexing: null,
            enableHookQuickSwitch: null,
            applySettings: settings => { applied = settings; return null; });

        viewModel.SelectedSearchTransliteration = SearchTransliterationMode.ChinesePinyin;
        viewModel.SavePreferencesCommand.Execute(null);

        Assert.Equal(SearchTransliterationMode.ChinesePinyin, applied!.SearchTransliteration);
        Assert.Equal(3, viewModel.SearchTransliterationOptions.Count);
    }

    [Fact]
    public void UpdatePreferenceSavesImmediatelyWithoutApplyingDraftsFromOtherPages()
    {
        AppSettings? applied = null;
        var defaults = AppSettings.Defaults();
        var viewModel = new SettingsViewModel(
            defaults,
            enableNtfsFastIndexing: null,
            enableHookQuickSwitch: null,
            applySettings: settings => { applied = settings; return null; });
        viewModel.SelectedTheme = AppTheme.Dark;
        viewModel.CheckForUpdates = false;

        viewModel.SaveUpdatePreferenceCommand.Execute(null);

        Assert.NotNull(applied);
        Assert.False(applied!.CheckForUpdates);
        Assert.Equal(defaults.Theme, applied.Theme);
        Assert.Equal(AppTheme.Dark, viewModel.SelectedTheme);
        Assert.Equal("Update preference saved.", viewModel.UpdatePreferenceStatusText);
    }

    [Fact]
    public void FailedUpdatePreferenceSaveRestoresThePersistedValue()
    {
        var viewModel = new SettingsViewModel(
            AppSettings.Defaults(),
            enableNtfsFastIndexing: null,
            enableHookQuickSwitch: null,
            applySettings: _ => "Could not save update preference.");
        viewModel.CheckForUpdates = false;

        viewModel.SaveUpdatePreferenceCommand.Execute(null);

        Assert.True(viewModel.CheckForUpdates);
        Assert.Contains("Could not save", viewModel.UpdatePreferenceStatusText);
    }

    [Fact]
    public void ThemeSelectionIsPreviewedImmediatelyAndSavedWithoutASecondApply()
    {
        var appliedThemes = new List<AppTheme>();
        var viewModel = new SettingsViewModel(
            AppSettings.Defaults(),
            enableNtfsFastIndexing: null,
            enableHookQuickSwitch: null,
            applySettings: _ => null,
            applyTheme: appliedThemes.Add);

        viewModel.SelectedTheme = AppTheme.Dark;

        Assert.Equal(new[] { AppTheme.Dark }, appliedThemes);

        viewModel.SavePreferencesCommand.Execute(null);

        Assert.Equal(new[] { AppTheme.Dark }, appliedThemes);
    }

    [Fact]
    public void InterfaceLanguageAppliesOnlyAfterSuccessfulSave()
    {
        AppSettings? applied = null;
        var viewModel = new SettingsViewModel(
            AppSettings.Defaults(),
            enableNtfsFastIndexing: null,
            enableHookQuickSwitch: null,
            applySettings: settings => { applied = settings; return null; });

        viewModel.SelectedLanguage = AppLanguage.SimplifiedChinese;

        Assert.Null(applied);
        Assert.Equal(3, viewModel.LanguageOptions.Count);

        viewModel.SavePreferencesCommand.Execute(null);

        Assert.Equal(AppLanguage.SimplifiedChinese, applied!.Language);
    }

    [Fact]
    public void DefaultDriveOptionsSelectEveryReadyDrive()
    {
        var viewModel = new SettingsViewModel(AppSettings.Defaults());

        Assert.NotEmpty(viewModel.Drives);
        Assert.All(viewModel.Drives, drive => Assert.True(drive.IsSelected));
        Assert.DoesNotContain(
            viewModel.Drives.Select(drive => drive.RootPath),
            driveRoot => viewModel.IndexedRootsText
                .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                .Contains(driveRoot, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void AdditionalFolderTextOmitsFoldersCoveredByASelectedDrive()
    {
        var selectedDrive = Assert.Single(
            DriveInfo.GetDrives().Where(drive => drive.IsReady).Take(1));
        var coveredFolder = Path.Combine(selectedDrive.Name, "Users", "Example");
        var settings = AppSettings.Defaults().WithPreferences(
            [selectedDrive.Name, coveredFolder],
            [],
            AppTheme.System,
            IndexUpdateFrequency.StartupOnly,
            AppSettings.Defaults().QuickMenuEntries,
            true);

        var viewModel = new SettingsViewModel(settings);

        Assert.DoesNotContain(selectedDrive.Name, viewModel.IndexedRootsText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(coveredFolder, viewModel.IndexedRootsText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SavePreferencesPersistsQuickLaunchEntries()
    {
        AppSettings? applied = null;
        var viewModel = new SettingsViewModel(
            AppSettings.Defaults(),
            enableNtfsFastIndexing: null,
            enableHookQuickSwitch: null,
            applySettings: settings => { applied = settings; return null; });
        viewModel.AddQuickLaunchEntryCommand.Execute(null);
        var entry = Assert.IsType<QuickLaunchEntryEditor>(viewModel.SelectedQuickLaunchEntry);
        entry.Keyword = "note";
        entry.Title = "Notepad";
        entry.Path = "notepad.exe";

        viewModel.SavePreferencesCommand.Execute(null);

        var saved = Assert.Single(Assert.IsType<AppSettings>(applied).QuickLaunchEntries);
        Assert.Equal("note", saved.Keyword);
        Assert.Equal("notepad.exe", saved.Path);
    }

    [Fact]
    public void DuplicateEnabledQuickLaunchKeywordBlocksSave()
    {
        var applyCount = 0;
        var settings = AppSettings.Defaults().WithPreferences(
            AppSettings.Defaults().IndexedRoots,
            [],
            AppTheme.System,
            IndexUpdateFrequency.StartupOnly,
            AppSettings.Defaults().QuickMenuEntries,
            true,
            [
                new QuickLaunchEntry("one", "note", "One", "one.exe"),
                new QuickLaunchEntry("two", "NOTE", "Two", "two.exe")
            ]);
        var viewModel = new SettingsViewModel(
            settings,
            enableNtfsFastIndexing: null,
            enableHookQuickSwitch: null,
            applySettings: _ => { applyCount++; return null; });

        viewModel.SavePreferencesCommand.Execute(null);

        Assert.Equal(0, applyCount);
        Assert.Contains("duplicated", viewModel.SettingsStatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SuccessfulUpdateCheckExposesAndOpensReleasePage()
    {
        string? openedUri = null;
        var viewModel = new SettingsViewModel(
            AppSettings.Defaults(),
            enableNtfsFastIndexing: null,
            enableHookQuickSwitch: null,
            checkUpdates: _ => Task.FromResult(new ListaryOpen.Infrastructure.AppData.UpdateCheckResult(
                true,
                "1.0.0",
                "v2.0.0",
                "https://github.com/example/releases/tag/v2.0.0",
                "Update v2.0.0 is available.")),
            openUri: uri => openedUri = uri);

        viewModel.CheckForUpdatesCommand.Execute(null);
        viewModel.OpenReleaseCommand.Execute(null);

        Assert.True(viewModel.HasReleaseUrl);
        Assert.Equal("https://github.com/example/releases/tag/v2.0.0", openedUri);
    }

    [Fact]
    public void SaveHotkeysCommandAppliesAndUpdatesSettings()
    {
        var applied = new List<string>();
        var viewModel = new SettingsViewModel(
            AppSettings.Defaults(),
            enableNtfsFastIndexing: null,
            enableHookQuickSwitch: null,
            applyHotkeys: (search, dialog) =>
            {
                applied.Add(search + "|" + dialog);
                return null;
            });
        viewModel.SearchHotkeyText = "Alt+F1";
        viewModel.DialogHotkeyText = "Ctrl+Shift+D";

        viewModel.SaveHotkeysCommand.Execute(null);

        Assert.Equal(new[] { "Alt+F1|Ctrl+Shift+D" }, applied);
        Assert.Equal("Alt+F1", viewModel.Settings.SearchHotkey);
        Assert.Equal("Ctrl+Shift+D", viewModel.Settings.DialogHotkey);
        Assert.Contains("saved", viewModel.HotkeyStatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SaveHotkeysCommandKeepsSettingsWhenApplyFails()
    {
        var viewModel = new SettingsViewModel(
            AppSettings.Defaults(),
            enableNtfsFastIndexing: null,
            enableHookQuickSwitch: null,
            applyHotkeys: (_, _) => "Conflict");
        viewModel.SearchHotkeyText = "Alt+F1";

        viewModel.SaveHotkeysCommand.Execute(null);

        Assert.Equal("Ctrl+Space", viewModel.Settings.SearchHotkey);
        Assert.Equal("Conflict", viewModel.HotkeyStatusText);
    }

    [Fact]
    public void ConstructorStartsWithIdleIndexingStatus()
    {
        var viewModel = new SettingsViewModel(AppSettings.Defaults());

        Assert.Equal("Indexing: idle", viewModel.IndexingStatusText);
    }

    [Fact]
    public void UpdateIndexingStatusFormatsStatusAndRaisesPropertyChanged()
    {
        var viewModel = new SettingsViewModel(AppSettings.Defaults());
        var changedProperties = new List<string?>();
        viewModel.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

        viewModel.UpdateIndexingStatus(new IndexingStatus(IndexingRunState.Indexing, "Indexing started.", 7));

        Assert.Equal("Indexing started.", viewModel.IndexingStatusText);
        Assert.Equal("7 items indexed", viewModel.IndexingProgressText);
        Assert.True(viewModel.IsIndexing);
        Assert.Contains(nameof(SettingsViewModel.IndexingStatusText), changedProperties);
        Assert.IsAssignableFrom<INotifyPropertyChanged>(viewModel);
    }

    [Fact]
    public void UpdateIndexingStatusShowsRootProgressWithoutInventingAPercentage()
    {
        var viewModel = new SettingsViewModel(AppSettings.Defaults());

        viewModel.UpdateIndexingStatus(new IndexingStatus(
            IndexingRunState.Indexing,
            "Scanning with NTFS...",
            12500,
            "C:\\Users\\Paul",
            CurrentRootNumber: 2,
            TotalRoots: 3));

        Assert.Equal("12,500 items indexed · Location 2 of 3", viewModel.IndexingProgressText);
        Assert.Equal("C:\\Users\\Paul", viewModel.CurrentIndexRootText);
        Assert.True(viewModel.HasCurrentIndexRoot);
    }

    [Fact]
    public void EnableNtfsFastIndexingCommandInvokesCallbackAndUpdatesState()
    {
        var enableCount = 0;
        var viewModel = new SettingsViewModel(AppSettings.Defaults(), () => { enableCount++; return true; });
        var changedProperties = new List<string?>();
        viewModel.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

        viewModel.EnableNtfsFastIndexingCommand.Execute(null);
        viewModel.EnableNtfsFastIndexingCommand.Execute(null);

        Assert.Equal(1, enableCount);
        Assert.True(viewModel.NtfsFastIndexingEnabled);
        Assert.Equal("NTFS fast indexing: enabled for this session", viewModel.NtfsFastIndexingStatusText);
        Assert.False(viewModel.EnableNtfsFastIndexingCommand.CanExecute(null));
        Assert.Contains(nameof(SettingsViewModel.NtfsFastIndexingEnabled), changedProperties);
        Assert.Contains(nameof(SettingsViewModel.NtfsFastIndexingStatusText), changedProperties);
    }

    [Fact]
    public void EnableNtfsFastIndexingCommandStaysDisabledWhenHelperIsUnavailable()
    {
        var viewModel = new SettingsViewModel(AppSettings.Defaults(), () => false);

        viewModel.EnableNtfsFastIndexingCommand.Execute(null);

        Assert.False(viewModel.NtfsFastIndexingEnabled);
        Assert.Equal(
            "NTFS fast indexing: unavailable (elevated helper bundle is missing).",
            viewModel.NtfsFastIndexingStatusText);
        Assert.True(viewModel.EnableNtfsFastIndexingCommand.CanExecute(null));
    }

    [Fact]
    public void ConstructorStartsWithPresentationBadges()
    {
        var viewModel = new SettingsViewModel(AppSettings.Defaults());

        Assert.Equal("Idle", viewModel.IndexingBadgeText);
        Assert.Equal("Disabled", viewModel.NtfsFastIndexingBadgeText);
        Assert.Equal("Enable", viewModel.NtfsFastIndexingActionText);
        Assert.True(viewModel.IsNtfsFastIndexingActionVisible);
        Assert.Equal("Enabled", viewModel.QuickSaveOpenBadgeText);
    }

    [Fact]
    public void UpdateIndexingStatusUpdatesPresentationBadge()
    {
        var viewModel = new SettingsViewModel(AppSettings.Defaults());
        var changedProperties = new List<string?>();
        viewModel.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

        viewModel.UpdateIndexingStatus(new IndexingStatus(IndexingRunState.Completed, "Indexing completed.", 42));

        Assert.Equal("Completed", viewModel.IndexingBadgeText);
        Assert.False(viewModel.IsIndexing);
        Assert.Contains(nameof(SettingsViewModel.IndexingBadgeText), changedProperties);
    }

    [Fact]
    public void UpdateIndexingStatusShowsCanceledPresentationBadge()
    {
        var viewModel = new SettingsViewModel(AppSettings.Defaults());

        viewModel.UpdateIndexingStatus(new IndexingStatus(IndexingRunState.Canceled, "Indexing canceled.", 0));

        Assert.Equal("Canceled", viewModel.IndexingBadgeText);
    }

    [Fact]
    public void EnableNtfsFastIndexingCommandUpdatesPresentationBadges()
    {
        var viewModel = new SettingsViewModel(AppSettings.Defaults(), () => true);
        var changedProperties = new List<string?>();
        viewModel.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

        viewModel.EnableNtfsFastIndexingCommand.Execute(null);

        Assert.Equal("Enabled", viewModel.NtfsFastIndexingBadgeText);
        Assert.Equal("Enabled", viewModel.NtfsFastIndexingActionText);
        Assert.False(viewModel.IsNtfsFastIndexingActionVisible);
        Assert.Contains(nameof(SettingsViewModel.NtfsFastIndexingBadgeText), changedProperties);
        Assert.Contains(nameof(SettingsViewModel.NtfsFastIndexingActionText), changedProperties);
        Assert.Contains(nameof(SettingsViewModel.IsNtfsFastIndexingActionVisible), changedProperties);
    }

    [Fact]
    public void ConstructorCanStartWithNtfsFastIndexingEnabled()
    {
        var enableCount = 0;
        var viewModel = new SettingsViewModel(
            AppSettings.Defaults(),
            () => { enableCount++; return true; },
            enableHookQuickSwitch: null,
            ntfsFastIndexingEnabled: true);

        viewModel.EnableNtfsFastIndexingCommand.Execute(null);

        Assert.True(viewModel.NtfsFastIndexingEnabled);
        Assert.Equal("NTFS fast indexing: enabled for this session", viewModel.NtfsFastIndexingStatusText);
        Assert.False(viewModel.EnableNtfsFastIndexingCommand.CanExecute(null));
        Assert.False(viewModel.IsNtfsFastIndexingActionVisible);
        Assert.Equal(0, enableCount);
    }
}

using ListaryOpen.Core.Settings;
using ListaryOpen.Infrastructure.Hooks;
using ListaryOpen.Infrastructure.Indexing;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.IO;
using System.Net.Http;
using System.Diagnostics;
using System.Text.Json;
using ListaryOpen.Infrastructure.AppData;

namespace ListaryOpen.App.ViewModels;

public sealed class SettingsViewModel : INotifyPropertyChanged
{
    private readonly Action _enableHookQuickSwitch;
    private readonly Func<bool> _enableNtfsFastIndexing;
    private readonly Func<string, string, string?> _applyHotkeys;
    private readonly Func<AppSettings, string?> _applySettings;
    private readonly Func<CancellationToken, Task<UpdateCheckResult>> _checkUpdates;
    private readonly Action<AppTheme> _applyTheme;
    private readonly Action<string> _openUri;
    private bool _hookQuickSwitchEnableInProgress;
    private HookQuickSwitchStatus _hookQuickSwitchStatus = HookQuickSwitchStatus.Disabled();
    private IndexingRunState _indexingState = IndexingRunState.Idle;
    private string _indexingStatusText = "Indexing: idle";
    private int _indexedCount;
    private string? _currentIndexRoot;
    private int _currentIndexRootNumber;
    private int _totalIndexRoots;
    private bool _ntfsFastIndexingEnabled;
    private bool _ntfsFastIndexingUnavailable;
    private string _searchHotkeyText;
    private string _dialogHotkeyText;
    private string _hotkeyStatusText = "Use modifiers plus a letter, number, Space, or F1-F24.";
    private AppTheme _selectedTheme;
    private AppLanguage _selectedLanguage;
    private IndexUpdateFrequency _selectedIndexFrequency;
    private SearchTransliterationMode _selectedSearchTransliteration;
    private string _indexedRootsText;
    private string _excludedPathsText;
    private bool _checkForUpdates;
    private string _settingsStatusText = "Changes are applied without restarting.";
    private string _updatePreferenceStatusText = string.Empty;
    private string _updateStatusText = "Updates have not been checked.";
    private string _latestReleaseUrl = string.Empty;
    private bool _checkingForUpdates;
    private QuickMenuEntryEditor? _selectedMenuEntry;
    private QuickLaunchEntryEditor? _selectedQuickLaunchEntry;

    public SettingsViewModel()
        : this(AppSettings.Defaults())
    {
    }

    public SettingsViewModel(AppSettings settings)
        : this(settings, null)
    {
    }

    public SettingsViewModel(AppSettings settings, Func<bool>? enableNtfsFastIndexing)
        : this(settings, enableNtfsFastIndexing, null)
    {
    }

    public SettingsViewModel(
        AppSettings settings,
        Func<bool>? enableNtfsFastIndexing,
        Action? enableHookQuickSwitch,
        bool ntfsFastIndexingEnabled = false,
        Func<string, string, string?>? applyHotkeys = null,
        Func<AppSettings, string?>? applySettings = null,
        Func<CancellationToken, Task<UpdateCheckResult>>? checkUpdates = null,
        Action<AppTheme>? applyTheme = null,
        Action<string>? openUri = null)
    {
        Settings = settings;
        _searchHotkeyText = settings.SearchHotkey;
        _dialogHotkeyText = settings.DialogHotkey;
        _enableNtfsFastIndexing = enableNtfsFastIndexing ?? (() => false);
        _enableHookQuickSwitch = enableHookQuickSwitch ?? (() => { });
        _applyHotkeys = applyHotkeys ?? ((_, _) => "Hotkey saving is unavailable.");
        _applySettings = applySettings ?? (_ => "Settings saving is unavailable.");
        _checkUpdates = checkUpdates ?? (_ => Task.FromResult(new UpdateCheckResult(false, "0.0.0", "0.0.0", string.Empty, "Update checking is unavailable.")));
        _applyTheme = applyTheme ?? (_ => { });
        _openUri = openUri ?? (uri => Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }));
        _selectedTheme = settings.Theme;
        _selectedLanguage = settings.Language;
        _selectedIndexFrequency = settings.IndexFrequency;
        _selectedSearchTransliteration = settings.SearchTransliteration;
        _indexedRootsText = string.Join(Environment.NewLine, settings.IndexedRoots);
        _excludedPathsText = string.Join(Environment.NewLine, settings.ExcludedPaths);
        _checkForUpdates = settings.CheckForUpdates;
        Drives = new ObservableCollection<DriveOptionViewModel>(CreateDriveOptions(settings.IndexedRoots));
        MenuEntries = new ObservableCollection<QuickMenuEntryEditor>(settings.QuickMenuEntries.Select(entry => new QuickMenuEntryEditor(entry)));
        QuickLaunchEntries = new ObservableCollection<QuickLaunchEntryEditor>(settings.QuickLaunchEntries.Select(entry => new QuickLaunchEntryEditor(entry)));
        SelectedMenuEntry = MenuEntries.FirstOrDefault();
        SelectedQuickLaunchEntry = QuickLaunchEntries.FirstOrDefault();
        _ntfsFastIndexingEnabled = ntfsFastIndexingEnabled;
        EnableNtfsFastIndexingCommand = new RelayCommand(
            EnableNtfsFastIndexing,
            () => !NtfsFastIndexingEnabled);
        EnableHookQuickSwitchCommand = new RelayCommand(
            EnableHookQuickSwitch,
            () => !_hookQuickSwitchEnableInProgress);
        SaveHotkeysCommand = new RelayCommand(SaveHotkeys, () => true);
        SavePreferencesCommand = new RelayCommand(SavePreferences, () => true);
        SaveUpdatePreferenceCommand = new RelayCommand(SaveUpdatePreference, () => true);
        CheckForUpdatesCommand = new AsyncRelayCommand(CheckForUpdatesAsync, () => !_checkingForUpdates);
        OpenReleaseCommand = new RelayCommand(OpenRelease, () => HasReleaseUrl);
        AddMenuEntryCommand = new RelayCommand(AddMenuEntry, () => true);
        RemoveMenuEntryCommand = new RelayCommand(RemoveMenuEntry, () => SelectedMenuEntry is not null);
        MoveMenuEntryUpCommand = new RelayCommand(() => MoveMenuEntry(-1), () => CanMoveMenuEntry(-1));
        MoveMenuEntryDownCommand = new RelayCommand(() => MoveMenuEntry(1), () => CanMoveMenuEntry(1));
        AddQuickLaunchEntryCommand = new RelayCommand(AddQuickLaunchEntry, () => true);
        RemoveQuickLaunchEntryCommand = new RelayCommand(RemoveQuickLaunchEntry, () => SelectedQuickLaunchEntry is not null);
        MoveQuickLaunchEntryUpCommand = new RelayCommand(() => MoveQuickLaunchEntry(-1), () => CanMoveQuickLaunchEntry(-1));
        MoveQuickLaunchEntryDownCommand = new RelayCommand(() => MoveQuickLaunchEntry(1), () => CanMoveQuickLaunchEntry(1));
    }

    public AppSettings Settings { get; private set; }

    public ICommand EnableHookQuickSwitchCommand { get; }

    public ICommand EnableNtfsFastIndexingCommand { get; }

    public ICommand SaveHotkeysCommand { get; }
    public ICommand SavePreferencesCommand { get; }
    public ICommand SaveUpdatePreferenceCommand { get; }
    public ICommand CheckForUpdatesCommand { get; }
    public ICommand OpenReleaseCommand { get; }
    public ICommand AddMenuEntryCommand { get; }
    public ICommand RemoveMenuEntryCommand { get; }
    public ICommand MoveMenuEntryUpCommand { get; }
    public ICommand MoveMenuEntryDownCommand { get; }
    public ICommand AddQuickLaunchEntryCommand { get; }
    public ICommand RemoveQuickLaunchEntryCommand { get; }
    public ICommand MoveQuickLaunchEntryUpCommand { get; }
    public ICommand MoveQuickLaunchEntryDownCommand { get; }

    public IReadOnlyList<AppTheme> ThemeOptions { get; } = Enum.GetValues<AppTheme>();
    public IReadOnlyList<AppLanguage> LanguageOptions { get; } = Enum.GetValues<AppLanguage>();
    public IReadOnlyList<IndexUpdateFrequency> IndexFrequencyOptions { get; } = Enum.GetValues<IndexUpdateFrequency>();
    public IReadOnlyList<SearchTransliterationMode> SearchTransliterationOptions { get; } = Enum.GetValues<SearchTransliterationMode>();
    public IReadOnlyList<QuickMenuAction> MenuActionOptions { get; } = Enum.GetValues<QuickMenuAction>();
    public ObservableCollection<DriveOptionViewModel> Drives { get; }
    public ObservableCollection<QuickMenuEntryEditor> MenuEntries { get; }
    public ObservableCollection<QuickLaunchEntryEditor> QuickLaunchEntries { get; }

    public AppTheme SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (_selectedTheme == value) return;
            _selectedTheme = value;
            OnPropertyChanged();
            _applyTheme(value);
        }
    }

    public AppLanguage SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (_selectedLanguage == value) return;
            _selectedLanguage = value;
            OnPropertyChanged();
        }
    }

    public IndexUpdateFrequency SelectedIndexFrequency
    {
        get => _selectedIndexFrequency;
        set { if (_selectedIndexFrequency != value) { _selectedIndexFrequency = value; OnPropertyChanged(); } }
    }

    public SearchTransliterationMode SelectedSearchTransliteration
    {
        get => _selectedSearchTransliteration;
        set
        {
            if (_selectedSearchTransliteration != value)
            {
                _selectedSearchTransliteration = value;
                OnPropertyChanged();
            }
        }
    }

    public string IndexedRootsText { get => _indexedRootsText; set => SetField(ref _indexedRootsText, value); }
    public string ExcludedPathsText { get => _excludedPathsText; set => SetField(ref _excludedPathsText, value); }
    public bool CheckForUpdates { get => _checkForUpdates; set { if (_checkForUpdates != value) { _checkForUpdates = value; OnPropertyChanged(); } } }
    public string SettingsStatusText { get => L(_settingsStatusText); private set => SetField(ref _settingsStatusText, value); }
    public string UpdatePreferenceStatusText { get => L(_updatePreferenceStatusText); private set => SetField(ref _updatePreferenceStatusText, value); }
    public string UpdateStatusText { get => L(_updateStatusText); private set => SetField(ref _updateStatusText, value); }
    public bool HasReleaseUrl => !string.IsNullOrWhiteSpace(_latestReleaseUrl);

    public QuickMenuEntryEditor? SelectedMenuEntry
    {
        get => _selectedMenuEntry;
        set
        {
            if (ReferenceEquals(_selectedMenuEntry, value)) return;
            _selectedMenuEntry = value;
            OnPropertyChanged();
            RaiseMenuCommandStates();
        }
    }

    public QuickLaunchEntryEditor? SelectedQuickLaunchEntry
    {
        get => _selectedQuickLaunchEntry;
        set
        {
            if (ReferenceEquals(_selectedQuickLaunchEntry, value)) return;
            _selectedQuickLaunchEntry = value;
            OnPropertyChanged();
            RaiseQuickLaunchCommandStates();
        }
    }

    public string SearchHotkeyText
    {
        get => _searchHotkeyText;
        set => SetField(ref _searchHotkeyText, value);
    }

    public string DialogHotkeyText
    {
        get => _dialogHotkeyText;
        set => SetField(ref _dialogHotkeyText, value);
    }

    public string HotkeyStatusText
    {
        get => L(_hotkeyStatusText);
        private set => SetField(ref _hotkeyStatusText, value);
    }

    public string HookQuickSwitchStatusText => L(_hookQuickSwitchStatus.DisplayText);

    public string IndexingStatusText
    {
        get => L(_indexingStatusText);
        private set
        {
            if (string.Equals(_indexingStatusText, value, StringComparison.Ordinal))
            {
                return;
            }

            _indexingStatusText = value;
            OnPropertyChanged();
        }
    }

    public string IndexingBadgeText => L(_indexingState switch
    {
        IndexingRunState.Idle => "Idle",
        IndexingRunState.Indexing => "Indexing",
        IndexingRunState.Completed => "Completed",
        IndexingRunState.Canceled => "Canceled",
        IndexingRunState.Failed => "Failed",
        _ => "Idle"
    });

    public bool IsIndexing => _indexingState == IndexingRunState.Indexing;

    public string IndexingProgressText
    {
        get
        {
            if (global::ListaryOpen.App.LocalizationManager.EffectiveLanguage == AppLanguage.SimplifiedChinese)
            {
                var chineseCount = $"已索引 {_indexedCount.ToString("N0", CultureInfo.InvariantCulture)} 个项目";
                return _currentIndexRootNumber > 0 && _totalIndexRoots > 0
                    ? $"{chineseCount} · 位置 {_currentIndexRootNumber}/{_totalIndexRoots}"
                    : chineseCount;
            }

            var countText = $"{_indexedCount.ToString("N0", CultureInfo.InvariantCulture)} items indexed";
            return _currentIndexRootNumber > 0 && _totalIndexRoots > 0
                ? $"{countText} · Location {_currentIndexRootNumber} of {_totalIndexRoots}"
                : countText;
        }
    }

    public string CurrentIndexRootText => _currentIndexRoot ?? string.Empty;

    public bool HasCurrentIndexRoot => !string.IsNullOrWhiteSpace(_currentIndexRoot);

    public string NtfsFastIndexingBadgeText => L(NtfsFastIndexingEnabled ? "Enabled" : "Disabled");

    public string NtfsFastIndexingBadgeKind => NtfsFastIndexingEnabled ? "Enabled" : "Disabled";

    public string NtfsFastIndexingActionText => L(NtfsFastIndexingEnabled ? "Enabled" : "Enable");

    public bool IsNtfsFastIndexingActionVisible => !NtfsFastIndexingEnabled;

    public string HookQuickSwitchBadgeText
    {
        get
        {
            if (!_hookQuickSwitchStatus.Enabled)
            {
                return L("Disabled");
            }

            if (IsHookQuickSwitchReady)
            {
                return L("Ready");
            }

            if (_hookQuickSwitchStatus.X64.HostRunning || _hookQuickSwitchStatus.X86.HostRunning)
            {
                return L("Degraded");
            }

            return L("Failed");
        }
    }

    public string HookQuickSwitchBadgeKind
    {
        get
        {
            if (!_hookQuickSwitchStatus.Enabled)
            {
                return "Disabled";
            }

            if (IsHookQuickSwitchReady)
            {
                return "Ready";
            }

            return _hookQuickSwitchStatus.X64.HostRunning || _hookQuickSwitchStatus.X86.HostRunning
                ? "Degraded"
                : "Failed";
        }
    }

    public string HookQuickSwitchActionText
    {
        get
        {
            if (_hookQuickSwitchEnableInProgress)
            {
                return L("Enabling");
            }

            if (!_hookQuickSwitchStatus.Enabled)
            {
                return L("Enable");
            }

            return L(IsHookQuickSwitchReady ? "Enabled" : "Retry");
        }
    }

    public bool IsHookQuickSwitchActionVisible => _hookQuickSwitchEnableInProgress
        || !_hookQuickSwitchStatus.Enabled
        || !IsHookQuickSwitchReady;

    public string QuickSaveOpenBadgeText => L(Settings.QuickSaveOpenEnabled ? "Enabled" : "Disabled");

    public bool NtfsFastIndexingEnabled
    {
        get => _ntfsFastIndexingEnabled;
        private set
        {
            if (_ntfsFastIndexingEnabled == value)
            {
                return;
            }

            _ntfsFastIndexingEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(NtfsFastIndexingStatusText));
            OnPropertyChanged(nameof(NtfsFastIndexingBadgeText));
            OnPropertyChanged(nameof(NtfsFastIndexingBadgeKind));
            OnPropertyChanged(nameof(NtfsFastIndexingActionText));
            OnPropertyChanged(nameof(IsNtfsFastIndexingActionVisible));
            if (EnableNtfsFastIndexingCommand is RelayCommand command)
            {
                command.RaiseCanExecuteChanged();
            }
        }
    }

    public string NtfsFastIndexingStatusText => L(NtfsFastIndexingEnabled
        ? "NTFS fast indexing: enabled for this session"
        : _ntfsFastIndexingUnavailable
            ? "NTFS fast indexing: unavailable (elevated helper bundle is missing)."
            : "NTFS fast indexing: disabled");

    public event PropertyChangedEventHandler? PropertyChanged;

    public void UpdateIndexingStatus(IndexingStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        var indexingStateChanged = _indexingState != status.State;
        _indexingState = status.State;
        _indexedCount = status.IndexedCount;
        _currentIndexRoot = status.CurrentRoot;
        _currentIndexRootNumber = status.CurrentRootNumber;
        _totalIndexRoots = status.TotalRoots;
        IndexingStatusText = status.Message;

        OnPropertyChanged(nameof(IndexingProgressText));
        OnPropertyChanged(nameof(CurrentIndexRootText));
        OnPropertyChanged(nameof(HasCurrentIndexRoot));

        if (indexingStateChanged)
        {
            OnPropertyChanged(nameof(IndexingBadgeText));
            OnPropertyChanged(nameof(IsIndexing));
        }
    }

    public void UpdateHookQuickSwitchStatus(HookQuickSwitchStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        if (!Equals(_hookQuickSwitchStatus, status))
        {
            _hookQuickSwitchStatus = status;
            OnPropertyChanged(nameof(HookQuickSwitchStatusText));
            OnPropertyChanged(nameof(HookQuickSwitchBadgeText));
            OnPropertyChanged(nameof(HookQuickSwitchBadgeKind));
            if (!_hookQuickSwitchEnableInProgress)
            {
                OnPropertyChanged(nameof(HookQuickSwitchActionText));
                OnPropertyChanged(nameof(IsHookQuickSwitchActionVisible));
            }
        }

        SetHookQuickSwitchEnableInProgress(false);
    }

    private void EnableHookQuickSwitch()
    {
        SetHookQuickSwitchEnableInProgress(true);
        try
        {
            _enableHookQuickSwitch();
        }
        catch
        {
            SetHookQuickSwitchEnableInProgress(false);
            throw;
        }
    }

    private void SetHookQuickSwitchEnableInProgress(bool value)
    {
        if (_hookQuickSwitchEnableInProgress == value)
        {
            return;
        }

        _hookQuickSwitchEnableInProgress = value;
        OnPropertyChanged(nameof(HookQuickSwitchActionText));
        OnPropertyChanged(nameof(IsHookQuickSwitchActionVisible));
        if (EnableHookQuickSwitchCommand is RelayCommand command)
        {
            command.RaiseCanExecuteChanged();
        }
    }

    private void EnableNtfsFastIndexing()
    {
        if (NtfsFastIndexingEnabled)
        {
            return;
        }

        if (_enableNtfsFastIndexing())
        {
            NtfsFastIndexingEnabled = true;
            return;
        }

        _ntfsFastIndexingUnavailable = true;
        OnPropertyChanged(nameof(NtfsFastIndexingStatusText));
    }

    private void SaveHotkeys()
    {
        var error = _applyHotkeys(SearchHotkeyText.Trim(), DialogHotkeyText.Trim());
        if (error is not null)
        {
            HotkeyStatusText = error;
            return;
        }

        Settings = Settings.WithHotkeys(SearchHotkeyText.Trim(), DialogHotkeyText.Trim());
        OnPropertyChanged(nameof(Settings));
        HotkeyStatusText = "Hotkeys saved and active.";
    }

    private void SaveUpdatePreference()
    {
        var updated = Settings.WithCheckForUpdates(CheckForUpdates);
        var error = _applySettings(updated);
        if (error is not null)
        {
            CheckForUpdates = Settings.CheckForUpdates;
            UpdatePreferenceStatusText = error;
            return;
        }

        Settings = updated;
        OnPropertyChanged(nameof(Settings));
        UpdatePreferenceStatusText = "Update preference saved.";
    }

    private void SavePreferences()
    {
        var selectedDriveRoots = Drives.Where(drive => drive.IsSelected).Select(drive => drive.RootPath).ToArray();
        var roots = ParseLines(IndexedRootsText)
            .Where(path => !IsCoveredBySelectedDrive(path, selectedDriveRoots))
            .Concat(selectedDriveRoots)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var exclusions = ParseLines(ExcludedPathsText);
        var menuEntries = MenuEntries.Select(entry => entry.ToSettings()).ToArray();
        var quickLaunchEntries = QuickLaunchEntries.Select(entry => entry.ToSettings()).ToArray();
        var invalidQuickLaunch = ValidateQuickLaunchEntries(quickLaunchEntries);
        if (invalidQuickLaunch is not null)
        {
            SettingsStatusText = invalidQuickLaunch;
            return;
        }
        var updated = Settings.WithPreferences(
            roots,
            exclusions,
            SelectedTheme,
            SelectedIndexFrequency,
            menuEntries,
            CheckForUpdates,
            quickLaunchEntries,
            searchTransliteration: SelectedSearchTransliteration,
            language: SelectedLanguage);
        var error = _applySettings(updated);
        if (error is not null)
        {
            SettingsStatusText = error;
            return;
        }

        Settings = updated;
        OnPropertyChanged(nameof(Settings));
        SettingsStatusText = "Settings saved and applied.";
    }

    private async Task CheckForUpdatesAsync()
    {
        _checkingForUpdates = true;
        (CheckForUpdatesCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        UpdateStatusText = "Checking GitHub Releases...";
        try
        {
            var result = await _checkUpdates(CancellationToken.None);
            _latestReleaseUrl = result.ReleaseUrl;
            OnPropertyChanged(nameof(HasReleaseUrl));
            (OpenReleaseCommand as RelayCommand)?.RaiseCanExecuteChanged();
            UpdateStatusText = result.Message;
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or TaskCanceledException or JsonException)
        {
            UpdateStatusText = $"Update check failed: {exception.Message}";
        }
        finally
        {
            _checkingForUpdates = false;
            (CheckForUpdatesCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    private void OpenRelease()
    {
        if (!HasReleaseUrl)
        {
            return;
        }

        try
        {
            _openUri(_latestReleaseUrl);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            UpdateStatusText = $"Could not open the release page: {exception.Message}";
        }
    }

    private void AddMenuEntry()
    {
        var editor = new QuickMenuEntryEditor(new QuickMenuEntry(
            Guid.NewGuid().ToString("N"),
            "Custom folder",
            QuickMenuAction.OpenPath,
            "%USERPROFILE%"));
        MenuEntries.Add(editor);
        SelectedMenuEntry = editor;
    }

    private void RemoveMenuEntry()
    {
        var selected = SelectedMenuEntry;
        if (selected is null) return;
        var index = MenuEntries.IndexOf(selected);
        MenuEntries.Remove(selected);
        SelectedMenuEntry = MenuEntries.ElementAtOrDefault(Math.Min(index, MenuEntries.Count - 1));
    }

    private bool CanMoveMenuEntry(int delta) => SelectedMenuEntry is not null &&
        MenuEntries.IndexOf(SelectedMenuEntry) + delta >= 0 &&
        MenuEntries.IndexOf(SelectedMenuEntry) + delta < MenuEntries.Count;

    private void MoveMenuEntry(int delta)
    {
        if (!CanMoveMenuEntry(delta) || SelectedMenuEntry is null) return;
        var index = MenuEntries.IndexOf(SelectedMenuEntry);
        MenuEntries.Move(index, index + delta);
        RaiseMenuCommandStates();
    }

    private void RaiseMenuCommandStates()
    {
        (RemoveMenuEntryCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (MoveMenuEntryUpCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (MoveMenuEntryDownCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void AddQuickLaunchEntry()
    {
        var editor = new QuickLaunchEntryEditor(new QuickLaunchEntry(
            Guid.NewGuid().ToString("N"),
            "keyword",
            "New quick launch",
            string.Empty));
        QuickLaunchEntries.Add(editor);
        SelectedQuickLaunchEntry = editor;
    }

    private void RemoveQuickLaunchEntry()
    {
        var selected = SelectedQuickLaunchEntry;
        if (selected is null) return;
        var index = QuickLaunchEntries.IndexOf(selected);
        QuickLaunchEntries.Remove(selected);
        SelectedQuickLaunchEntry = QuickLaunchEntries.ElementAtOrDefault(Math.Min(index, QuickLaunchEntries.Count - 1));
    }

    private bool CanMoveQuickLaunchEntry(int delta) => SelectedQuickLaunchEntry is not null &&
        QuickLaunchEntries.IndexOf(SelectedQuickLaunchEntry) + delta >= 0 &&
        QuickLaunchEntries.IndexOf(SelectedQuickLaunchEntry) + delta < QuickLaunchEntries.Count;

    private void MoveQuickLaunchEntry(int delta)
    {
        if (!CanMoveQuickLaunchEntry(delta) || SelectedQuickLaunchEntry is null) return;
        var index = QuickLaunchEntries.IndexOf(SelectedQuickLaunchEntry);
        QuickLaunchEntries.Move(index, index + delta);
        RaiseQuickLaunchCommandStates();
    }

    private void RaiseQuickLaunchCommandStates()
    {
        (RemoveQuickLaunchEntryCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (MoveQuickLaunchEntryUpCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (MoveQuickLaunchEntryDownCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private static string? ValidateQuickLaunchEntries(IReadOnlyList<QuickLaunchEntry> entries)
    {
        var enabled = entries.Where(entry => entry.Enabled).ToArray();
        if (enabled.Any(entry => string.IsNullOrWhiteSpace(entry.Keyword)))
        {
            return "Each enabled quick launch needs a keyword.";
        }

        if (enabled.Any(entry => string.IsNullOrWhiteSpace(entry.Path)))
        {
            return "Each enabled quick launch needs a program or file path.";
        }

        var duplicate = enabled
            .GroupBy(entry => entry.Keyword.Trim(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        return duplicate is null ? null : $"Quick launch keyword '{duplicate.Key}' is duplicated.";
    }

    private static string[] ParseLines(string text) => (text ?? string.Empty)
        .Split(['\r', '\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .ToArray();

    private static bool IsCoveredBySelectedDrive(string path, IReadOnlyList<string> selectedDriveRoots)
    {
        try
        {
            var root = Path.GetPathRoot(Environment.ExpandEnvironmentVariables(path));
            return selectedDriveRoots.Any(driveRoot =>
                string.Equals(root, driveRoot, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            // Preserve malformed input so AppSettingsStore can report one actionable validation error.
            return false;
        }
    }

    private static IEnumerable<DriveOptionViewModel> CreateDriveOptions(IReadOnlyList<string> roots)
    {
        foreach (var drive in DriveInfo.GetDrives().Where(drive => drive.IsReady))
        {
            var fullDiskSelected = roots.Any(root => IsFullDriveRoot(root, drive.Name));
            var containsIndexedFolder = !fullDiskSelected && roots.Any(root => IsOnDrive(root, drive.Name));
            var scope = containsIndexedFolder ? " · folders listed below only" : string.Empty;
            yield return new DriveOptionViewModel(
                drive.Name,
                $"{drive.Name}  {drive.DriveType}  {drive.AvailableFreeSpace / 1024d / 1024 / 1024:F1} GB free{scope}",
                fullDiskSelected);
        }
    }

    private static bool IsOnDrive(string path, string driveRoot)
    {
        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(path);
            return string.Equals(Path.GetPathRoot(expanded), driveRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsFullDriveRoot(string path, string driveRoot)
    {
        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(path);
            return IsOnDrive(expanded, driveRoot) && string.Equals(
                Path.TrimEndingDirectorySeparator(expanded),
                Path.TrimEndingDirectorySeparator(driveRoot),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private bool IsHookQuickSwitchReady => _hookQuickSwitchStatus.Enabled
        && _hookQuickSwitchStatus.X64.HostRunning
        && _hookQuickSwitchStatus.X86.HostRunning;

    private static string L(string text) =>
        global::ListaryOpen.App.LocalizationManager.Translate(text);

    internal void RefreshLocalization() => RaiseLocalizedProperties();

    private void RaiseLocalizedProperties()
    {
        OnPropertyChanged(nameof(SettingsStatusText));
        OnPropertyChanged(nameof(UpdateStatusText));
        OnPropertyChanged(nameof(HotkeyStatusText));
        OnPropertyChanged(nameof(HookQuickSwitchStatusText));
        OnPropertyChanged(nameof(IndexingStatusText));
        OnPropertyChanged(nameof(IndexingBadgeText));
        OnPropertyChanged(nameof(IndexingProgressText));
        OnPropertyChanged(nameof(NtfsFastIndexingBadgeText));
        OnPropertyChanged(nameof(NtfsFastIndexingActionText));
        OnPropertyChanged(nameof(NtfsFastIndexingStatusText));
        OnPropertyChanged(nameof(HookQuickSwitchBadgeText));
        OnPropertyChanged(nameof(HookQuickSwitchActionText));
        OnPropertyChanged(nameof(QuickSaveOpenBadgeText));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void SetField(ref string field, string value, [CallerMemberName] string? propertyName = null)
    {
        if (string.Equals(field, value, StringComparison.Ordinal))
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);
    }
}

public sealed class DriveOptionViewModel : INotifyPropertyChanged
{
    private bool _isSelected;
    public DriveOptionViewModel(string rootPath, string displayText, bool isSelected) { RootPath = rootPath; DisplayText = displayText; _isSelected = isSelected; }
    public string RootPath { get; }
    public string DisplayText { get; }
    public bool IsSelected { get => _isSelected; set { if (_isSelected != value) { _isSelected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); } } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class QuickMenuEntryEditor : INotifyPropertyChanged
{
    private string _title;
    private QuickMenuAction _action;
    private string _path;
    private bool _enabled;
    private string _arguments;
    private string _workingDirectory;
    private bool _silent;
    private bool _runAsAdmin;
    public QuickMenuEntryEditor(QuickMenuEntry settings)
    {
        Id = settings.Id;
        _title = settings.Title;
        _action = settings.Action;
        _path = settings.Path;
        _enabled = settings.Enabled;
        _arguments = settings.Arguments;
        _workingDirectory = settings.WorkingDirectory;
        _silent = settings.Silent;
        _runAsAdmin = settings.RunAsAdmin;
    }
    public string Id { get; }
    public string Title { get => _title; set { if (_title != value) { _title = value; Changed(); } } }
    public QuickMenuAction Action { get => _action; set { if (_action != value) { _action = value; Changed(); } } }
    public string Path { get => _path; set { if (_path != value) { _path = value; Changed(); } } }
    public bool Enabled { get => _enabled; set { if (_enabled != value) { _enabled = value; Changed(); } } }
    public string Arguments { get => _arguments; set { if (_arguments != value) { _arguments = value; Changed(); } } }
    public string WorkingDirectory { get => _workingDirectory; set { if (_workingDirectory != value) { _workingDirectory = value; Changed(); } } }
    public bool Silent { get => _silent; set { if (_silent != value) { _silent = value; Changed(); } } }
    public bool RunAsAdmin { get => _runAsAdmin; set { if (_runAsAdmin != value) { _runAsAdmin = value; Changed(); } } }
    public QuickMenuEntry ToSettings() => new(
        Id,
        Title.Trim(),
        Action,
        Path.Trim(),
        Enabled,
        Arguments.Trim(),
        WorkingDirectory.Trim(),
        Silent,
        RunAsAdmin);
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class QuickLaunchEntryEditor : INotifyPropertyChanged
{
    private string _keyword;
    private string _title;
    private string _path;
    private bool _enabled;
    private string _arguments;
    private string _workingDirectory;
    private bool _silent;
    private bool _runAsAdmin;

    public QuickLaunchEntryEditor(QuickLaunchEntry settings)
    {
        Id = settings.Id;
        _keyword = settings.Keyword;
        _title = settings.Title;
        _path = settings.Path;
        _enabled = settings.Enabled;
        _arguments = settings.Arguments;
        _workingDirectory = settings.WorkingDirectory;
        _silent = settings.Silent;
        _runAsAdmin = settings.RunAsAdmin;
    }

    public string Id { get; }
    public string Keyword { get => _keyword; set => Set(ref _keyword, value); }
    public string Title { get => _title; set => Set(ref _title, value); }
    public string Path { get => _path; set => Set(ref _path, value); }
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public string Arguments { get => _arguments; set => Set(ref _arguments, value); }
    public string WorkingDirectory { get => _workingDirectory; set => Set(ref _workingDirectory, value); }
    public bool Silent { get => _silent; set => Set(ref _silent, value); }
    public bool RunAsAdmin { get => _runAsAdmin; set => Set(ref _runAsAdmin, value); }

    public QuickLaunchEntry ToSettings() => new(
        Id,
        Keyword.Trim(),
        Title.Trim(),
        Path.Trim(),
        Enabled,
        Arguments.Trim(),
        WorkingDirectory.Trim(),
        Silent,
        RunAsAdmin);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

internal sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool> _canExecute;

    public RelayCommand(Action execute, Func<bool> canExecute)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute ?? throw new ArgumentNullException(nameof(canExecute));
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute();

    public void Execute(object? parameter)
    {
        if (CanExecute(parameter))
        {
            _execute();
        }
    }

    public void RaiseCanExecuteChanged()
    {
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}

internal sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool> _canExecute;
    public AsyncRelayCommand(Func<Task> execute, Func<bool> canExecute) { _execute = execute; _canExecute = canExecute; }
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => _canExecute();
    public async void Execute(object? parameter) { if (CanExecute(parameter)) await _execute(); }
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

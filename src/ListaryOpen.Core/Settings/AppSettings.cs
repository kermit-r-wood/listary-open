namespace ListaryOpen.Core.Settings;

public enum AppTheme
{
    System,
    Light,
    Dark,
    Geek
}

public enum IndexUpdateFrequency
{
    StartupOnly,
    Every15Minutes,
    Hourly,
    Every6Hours,
    Daily
}

public enum SearchTransliterationMode
{
    Disabled,
    ChinesePinyin,
    Multilingual
}

public enum AppLanguage
{
    System,
    English,
    SimplifiedChinese
}

public enum QuickMenuAction
{
    QuickAccess,
    ThisPc,
    OpenPath,
    History,
    OpenFolders,
    PowerShell,
    RunCommand,
    Commands,
    Options
}

public sealed record QuickMenuEntry(
    string Id,
    string Title,
    QuickMenuAction Action,
    string Path,
    bool Enabled = true,
    string Arguments = "",
    string WorkingDirectory = "",
    bool Silent = false,
    bool RunAsAdmin = false);

public sealed record QuickLaunchEntry(
    string Id,
    string Keyword,
    string Title,
    string Path,
    bool Enabled = true,
    string Arguments = "",
    string WorkingDirectory = "",
    bool Silent = false,
    bool RunAsAdmin = false);

public sealed record AppSettings
{
    public AppSettings(
        IReadOnlyList<string> IndexedRoots,
        IReadOnlyList<string> ExcludedPaths,
        IReadOnlyList<string> PinnedFolders,
        string SearchHotkey,
        string DialogHotkey,
        bool QuickSaveOpenEnabled,
        AppTheme Theme = AppTheme.System,
        IndexUpdateFrequency IndexFrequency = IndexUpdateFrequency.StartupOnly,
        IReadOnlyList<QuickMenuEntry>? QuickMenuEntries = null,
        bool CheckForUpdates = true,
        IReadOnlyList<QuickLaunchEntry>? QuickLaunchEntries = null,
        SearchTransliterationMode SearchTransliteration = SearchTransliterationMode.Multilingual,
        AppLanguage Language = AppLanguage.System)
    {
        this.IndexedRoots = CreateReadOnlySnapshot(IndexedRoots);
        this.ExcludedPaths = CreateReadOnlySnapshot(ExcludedPaths);
        this.PinnedFolders = CreateReadOnlySnapshot(PinnedFolders);
        this.SearchHotkey = SearchHotkey;
        this.DialogHotkey = DialogHotkey;
        this.QuickSaveOpenEnabled = QuickSaveOpenEnabled;
        this.Theme = Theme;
        this.IndexFrequency = IndexFrequency;
        this.QuickMenuEntries = CreateReadOnlySnapshot(QuickMenuEntries ?? CreateDefaultQuickMenuEntries());
        this.CheckForUpdates = CheckForUpdates;
        this.QuickLaunchEntries = CreateReadOnlySnapshot(QuickLaunchEntries ?? Array.Empty<QuickLaunchEntry>());
        this.SearchTransliteration = SearchTransliteration;
        this.Language = Language;
    }

    public IReadOnlyList<string> IndexedRoots { get; }

    public IReadOnlyList<string> ExcludedPaths { get; }

    public IReadOnlyList<string> PinnedFolders { get; }

    public string SearchHotkey { get; }

    public string DialogHotkey { get; }

    public bool QuickSaveOpenEnabled { get; }

    public AppTheme Theme { get; }

    public IndexUpdateFrequency IndexFrequency { get; }

    public IReadOnlyList<QuickMenuEntry> QuickMenuEntries { get; }

    public bool CheckForUpdates { get; }

    public IReadOnlyList<QuickLaunchEntry> QuickLaunchEntries { get; }

    public SearchTransliterationMode SearchTransliteration { get; }

    public AppLanguage Language { get; }

    public static AppSettings Defaults() => new(
        GetDefaultIndexedRoots(),
        Array.Empty<string>(),
        Array.Empty<string>(),
        "Ctrl+Space",
        "Ctrl+G",
        true,
        AppTheme.System,
        IndexUpdateFrequency.StartupOnly,
        CreateDefaultQuickMenuEntries(),
        true,
        Array.Empty<QuickLaunchEntry>(),
        SearchTransliterationMode.Multilingual,
        AppLanguage.System);

    public static IReadOnlyList<string> GetDefaultIndexedRoots()
    {
        try
        {
            return DriveInfo.GetDrives()
                .Where(drive => drive.IsReady)
                .Select(drive => drive.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (IOException)
        {
            return [Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\"];
        }
        catch (UnauthorizedAccessException)
        {
            return [Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\"];
        }
    }

    public AppSettings WithHotkeys(string searchHotkey, string dialogHotkey) => new(
        IndexedRoots,
        ExcludedPaths,
        PinnedFolders,
        searchHotkey,
        dialogHotkey,
        QuickSaveOpenEnabled,
        Theme,
        IndexFrequency,
        QuickMenuEntries,
        CheckForUpdates,
        QuickLaunchEntries,
        SearchTransliteration,
        Language);

    public AppSettings WithCheckForUpdates(bool checkForUpdates) => new(
        IndexedRoots,
        ExcludedPaths,
        PinnedFolders,
        SearchHotkey,
        DialogHotkey,
        QuickSaveOpenEnabled,
        Theme,
        IndexFrequency,
        QuickMenuEntries,
        checkForUpdates,
        QuickLaunchEntries,
        SearchTransliteration,
        Language);

    public AppSettings WithPreferences(
        IReadOnlyList<string> indexedRoots,
        IReadOnlyList<string> excludedPaths,
        AppTheme theme,
        IndexUpdateFrequency indexFrequency,
        IReadOnlyList<QuickMenuEntry> quickMenuEntries,
        bool checkForUpdates,
        IReadOnlyList<QuickLaunchEntry>? quickLaunchEntries = null,
        SearchTransliterationMode? searchTransliteration = null,
        AppLanguage? language = null) => new(
        indexedRoots,
        excludedPaths,
        PinnedFolders,
        SearchHotkey,
        DialogHotkey,
        QuickSaveOpenEnabled,
        theme,
        indexFrequency,
        quickMenuEntries,
        checkForUpdates,
        quickLaunchEntries ?? QuickLaunchEntries,
        searchTransliteration ?? SearchTransliteration,
        language ?? Language);

    public static IReadOnlyList<QuickMenuEntry> CreateDefaultQuickMenuEntries() =>
    [
        new("quick-access", "Quick access", QuickMenuAction.QuickAccess, string.Empty),
        new("this-pc", "This PC", QuickMenuAction.ThisPc, string.Empty),
        new("documents", "My Documents", QuickMenuAction.OpenPath, "%USERPROFILE%\\Documents"),
        new("history", "History", QuickMenuAction.History, string.Empty),
        new("open-folders", "Currently Opened Folders", QuickMenuAction.OpenFolders, string.Empty),
        new("powershell", "PowerShell here", QuickMenuAction.PowerShell, string.Empty),
        new("commands", "Commands", QuickMenuAction.Commands, string.Empty),
        new("options", "Options", QuickMenuAction.Options, string.Empty)
    ];

    public static IReadOnlyList<string> GetBuiltInShortcutRoots() =>
    [
        Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms)
    ];

    private static IReadOnlyList<T> CreateReadOnlySnapshot<T>(IReadOnlyList<T> values) =>
        Array.AsReadOnly(values.ToArray());
}

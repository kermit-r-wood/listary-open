using System.Text.Json;
using System.IO;
using ListaryOpen.Core.Settings;
using ListaryOpen.Infrastructure.Windows;

namespace ListaryOpen.Infrastructure.AppData;

public sealed class AppSettingsStore
{
    private const int CurrentVersion = 4;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public AppSettings Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            return AppSettings.Defaults();
        }

        try
        {
            var document = JsonSerializer.Deserialize<SettingsDocument>(File.ReadAllText(path), JsonOptions);
            if (document is null || document.Version > CurrentVersion)
            {
                return AppSettings.Defaults();
            }

            var defaults = AppSettings.Defaults();
            var searchHotkey = string.IsNullOrWhiteSpace(document.SearchHotkey) ? defaults.SearchHotkey : document.SearchHotkey;
            var dialogHotkey = string.IsNullOrWhiteSpace(document.DialogHotkey) ? defaults.DialogHotkey : document.DialogHotkey;
            if (!HotkeyGesture.TryParse(searchHotkey, out _) || !HotkeyGesture.TryParse(dialogHotkey, out _))
            {
                searchHotkey = defaults.SearchHotkey;
                dialogHotkey = defaults.DialogHotkey;
            }

            var indexedRoots = MigrateIndexedRoots(document, defaults);
            return new AppSettings(
                indexedRoots,
                document.ExcludedPaths ?? defaults.ExcludedPaths,
                document.PinnedFolders ?? defaults.PinnedFolders,
                searchHotkey,
                dialogHotkey,
                document.QuickSaveOpenEnabled ?? defaults.QuickSaveOpenEnabled,
                document.Theme ?? defaults.Theme,
                document.IndexFrequency ?? defaults.IndexFrequency,
                document.QuickMenuEntries ?? defaults.QuickMenuEntries,
                document.CheckForUpdates ?? defaults.CheckForUpdates,
                document.QuickLaunchEntries ?? defaults.QuickLaunchEntries,
                document.SearchTransliteration ?? defaults.SearchTransliteration,
                document.Language ?? defaults.Language);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return AppSettings.Defaults();
        }
    }

    private static IReadOnlyList<string> MigrateIndexedRoots(SettingsDocument document, AppSettings defaults)
    {
        var roots = document.IndexedRoots ?? defaults.IndexedRoots;
        if (document.Version >= CurrentVersion || roots.Count != 1 || string.IsNullOrWhiteSpace(roots[0]))
        {
            return roots;
        }

        var legacyDefault = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.Equals(
                Path.TrimEndingDirectorySeparator(roots[0]),
                Path.TrimEndingDirectorySeparator(legacyDefault),
                StringComparison.OrdinalIgnoreCase)
            ? defaults.IndexedRoots
            : roots;
    }

    public void Save(string path, AppSettings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(settings);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var document = new SettingsDocument(
            CurrentVersion,
            settings.IndexedRoots,
            settings.ExcludedPaths,
            settings.PinnedFolders,
            settings.SearchHotkey,
            settings.DialogHotkey,
            settings.QuickSaveOpenEnabled,
            settings.Theme,
            settings.IndexFrequency,
            settings.QuickMenuEntries,
            settings.CheckForUpdates,
            settings.QuickLaunchEntries,
            settings.SearchTransliteration,
            settings.Language);
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(document, JsonOptions));
        File.Move(temporaryPath, path, overwrite: true);
    }

    private sealed record SettingsDocument(
        int Version,
        IReadOnlyList<string>? IndexedRoots,
        IReadOnlyList<string>? ExcludedPaths,
        IReadOnlyList<string>? PinnedFolders,
        string? SearchHotkey,
        string? DialogHotkey,
        bool? QuickSaveOpenEnabled,
        AppTheme? Theme,
        IndexUpdateFrequency? IndexFrequency,
        IReadOnlyList<QuickMenuEntry>? QuickMenuEntries,
        bool? CheckForUpdates,
        IReadOnlyList<QuickLaunchEntry>? QuickLaunchEntries,
        SearchTransliterationMode? SearchTransliteration,
        AppLanguage? Language);
}

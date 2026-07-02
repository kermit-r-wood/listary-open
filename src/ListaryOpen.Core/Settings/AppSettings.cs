namespace ListaryOpen.Core.Settings;

public sealed record AppSettings(
    IReadOnlyList<string> IndexedRoots,
    IReadOnlyList<string> ExcludedPaths,
    IReadOnlyList<string> PinnedFolders,
    string SearchHotkey,
    string DialogHotkey,
    bool QuickSaveOpenEnabled)
{
    public static AppSettings Defaults() => new(
        new[] { Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) },
        Array.Empty<string>(),
        Array.Empty<string>(),
        "Ctrl+Space",
        "Ctrl+G",
        true);
}

namespace ListaryOpen.Core.Settings;

public sealed record AppSettings
{
    public AppSettings(
        IReadOnlyList<string> IndexedRoots,
        IReadOnlyList<string> ExcludedPaths,
        IReadOnlyList<string> PinnedFolders,
        string SearchHotkey,
        string DialogHotkey,
        bool QuickSaveOpenEnabled)
    {
        this.IndexedRoots = CreateReadOnlySnapshot(IndexedRoots);
        this.ExcludedPaths = CreateReadOnlySnapshot(ExcludedPaths);
        this.PinnedFolders = CreateReadOnlySnapshot(PinnedFolders);
        this.SearchHotkey = SearchHotkey;
        this.DialogHotkey = DialogHotkey;
        this.QuickSaveOpenEnabled = QuickSaveOpenEnabled;
    }

    public IReadOnlyList<string> IndexedRoots { get; }

    public IReadOnlyList<string> ExcludedPaths { get; }

    public IReadOnlyList<string> PinnedFolders { get; }

    public string SearchHotkey { get; }

    public string DialogHotkey { get; }

    public bool QuickSaveOpenEnabled { get; }

    public static AppSettings Defaults() => new(
        new[] { Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) },
        Array.Empty<string>(),
        Array.Empty<string>(),
        "Ctrl+Space",
        "Ctrl+G",
        true);

    private static IReadOnlyList<string> CreateReadOnlySnapshot(IReadOnlyList<string> values) =>
        Array.AsReadOnly(values.ToArray());
}

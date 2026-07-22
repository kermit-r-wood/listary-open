namespace ListaryOpen.Core.Search;

public sealed record SearchQuery
{
    public const int MaximumLimit = 500;

    public SearchQuery(
        string text,
        SearchMode mode,
        int limit = 50,
        string? preferredRoot = null,
        bool? isDirectory = null,
        IReadOnlyList<string>? requiredExtensions = null,
        DateTimeOffset? modifiedAfter = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Search text is required.", nameof(text));
        }

        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be positive.");
        }

        Text = text;
        Mode = mode;
        Limit = Math.Min(limit, MaximumLimit);
        Parsed = ParsedSearchQuery.Parse(text);
        PreferredRoot = NormalizePreferredRoot(preferredRoot);
        IsDirectory = isDirectory;
        RequiredExtensions = (requiredExtensions ?? Array.Empty<string>())
            .Select(extension => extension.Trim().TrimStart('.').ToLowerInvariant())
            .Where(extension => extension.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        ModifiedAfter = modifiedAfter;
    }

    public string Text { get; }

    public SearchMode Mode { get; }

    public SearchMode EffectiveMode => Parsed.FileOnly ? SearchMode.FilesAndFolders : Parsed.ModeOverride ?? Mode;

    public int Limit { get; }

    public string? PreferredRoot { get; }

    public ParsedSearchQuery Parsed { get; }

    public bool? IsDirectory { get; }

    public IReadOnlyList<string> RequiredExtensions { get; }

    public DateTimeOffset? ModifiedAfter { get; }

    public string NormalizedText => Parsed.RankingText;

    private static string? NormalizePreferredRoot(string? preferredRoot)
    {
        if (string.IsNullOrWhiteSpace(preferredRoot))
        {
            return null;
        }

        var trimmed = preferredRoot.Trim();
        if (!Path.IsPathFullyQualified(trimmed))
        {
            throw new ArgumentException("Preferred root must be fully qualified.", nameof(preferredRoot));
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed));
    }
}

public enum SearchItemTypeFilter
{
    All,
    Folders,
    Files,
    Documents,
    Images,
    Videos
}

public enum SearchDateFilter
{
    AnyTime,
    Today,
    Last7Days,
    Last30Days,
    LastYear
}

public enum SearchMode
{
    FilesAndFolders,
    FoldersOnly
}

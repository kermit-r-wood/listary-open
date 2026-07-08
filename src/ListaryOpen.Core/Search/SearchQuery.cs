namespace ListaryOpen.Core.Search;

public sealed record SearchQuery
{
    public const int MaximumLimit = 500;

    public SearchQuery(string text, SearchMode mode, int limit = 50)
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
    }

    public string Text { get; }

    public SearchMode Mode { get; }

    public SearchMode EffectiveMode => Parsed.FileOnly ? SearchMode.FilesAndFolders : Parsed.ModeOverride ?? Mode;

    public int Limit { get; }

    public ParsedSearchQuery Parsed { get; }

    public string NormalizedText => Parsed.RankingText;
}

public enum SearchMode
{
    FilesAndFolders,
    FoldersOnly
}

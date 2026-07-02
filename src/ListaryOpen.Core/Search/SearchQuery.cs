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
    }

    public string Text { get; }

    public SearchMode Mode { get; }

    public int Limit { get; }

    public string NormalizedText => Text.Trim();
}

public enum SearchMode
{
    FilesAndFolders,
    FoldersOnly
}

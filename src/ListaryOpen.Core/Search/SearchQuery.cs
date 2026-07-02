namespace ListaryOpen.Core.Search;

public sealed record SearchQuery(string Text, SearchMode Mode, int Limit = 50)
{
    public string NormalizedText => Text.Trim();
}

public enum SearchMode
{
    FilesAndFolders,
    FoldersOnly
}

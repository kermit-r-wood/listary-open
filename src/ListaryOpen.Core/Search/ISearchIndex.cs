using ListaryOpen.Core.Indexing;

namespace ListaryOpen.Core.Search;

public interface ISearchIndex
{
    Task UpsertAsync(FileRecord record, CancellationToken cancellationToken);
    Task DeleteAsync(string fullPath, CancellationToken cancellationToken);
    Task RecordUsageAsync(string fullPath, CancellationToken cancellationToken);
    Task<IReadOnlyList<SearchResult>> GetRecentAsync(int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SearchResult>>(Array.Empty<SearchResult>());
    Task<IReadOnlyList<SearchResult>> SearchAsync(SearchQuery query, CancellationToken cancellationToken);
}

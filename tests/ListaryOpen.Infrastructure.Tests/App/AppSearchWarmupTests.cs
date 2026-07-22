using ListaryOpen.Core.Indexing;
using ListaryOpen.Core.Search;

namespace ListaryOpen.Infrastructure.Tests.App;

public sealed class AppSearchWarmupTests
{
    [Fact]
    public async Task WarmSearchIndexRunsSmallBackgroundQueryWithoutRecordingUsage()
    {
        var index = new RecordingSearchIndex();

        await ListaryOpen.App.App.WarmSearchIndexAsync(index, CancellationToken.None);

        var query = Assert.Single(index.Queries);
        Assert.Equal(1, query.Limit);
        Assert.Equal("__listary_open_warmup__", query.NormalizedText);
        Assert.Equal(0, index.RecordUsageCount);
    }

    private sealed class RecordingSearchIndex : ISearchIndex
    {
        public List<SearchQuery> Queries { get; } = new();

        public int RecordUsageCount { get; private set; }

        public Task UpsertAsync(FileRecord record, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeleteAsync(string fullPath, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<SearchResult>> SearchAsync(
            SearchQuery query,
            CancellationToken cancellationToken)
        {
            Queries.Add(query);
            return Task.FromResult<IReadOnlyList<SearchResult>>(Array.Empty<SearchResult>());
        }

        public Task RecordUsageAsync(string fullPath, CancellationToken cancellationToken)
        {
            RecordUsageCount++;
            return Task.CompletedTask;
        }
    }
}

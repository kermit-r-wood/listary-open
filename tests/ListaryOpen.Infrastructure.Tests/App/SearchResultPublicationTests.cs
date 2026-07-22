using ListaryOpen.App;
using ListaryOpen.App.ViewModels;
using ListaryOpen.Core.Indexing;
using ListaryOpen.Core.Search;

namespace ListaryOpen.Infrastructure.Tests.App;

public sealed class SearchResultPublicationTests
{
    [Fact]
    public void CompleteSnapshotIsSwappedBeforeBindingsAreNotified()
    {
        var viewModel = new SearchPanelViewModel(new SnapshotSearchIndex());
        var previousSnapshot = viewModel.Results;
        var observedCounts = new List<int>();
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(SearchPanelViewModel.Results))
            {
                observedCounts.Add(viewModel.Results.Count);
            }
        };
        var results = Enumerable.Range(0, 50)
            .Select(index => new SearchResult(
                FileRecord.Create($"C:\\Results\\item-{index}.txt", false, 1, DateTimeOffset.UtcNow),
                1,
                "test"))
            .ToArray();

        viewModel.SetResultsForTesting(results);

        Assert.Empty(previousSnapshot);
        Assert.Equal([50], observedCounts);
        Assert.Equal(results, viewModel.Results);
        Assert.NotSame(previousSnapshot, viewModel.Results);
    }

    private sealed class SnapshotSearchIndex : ISearchIndex
    {
        public Task UpsertAsync(FileRecord record, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteAsync(string fullPath, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RecordUsageAsync(string fullPath, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<SearchResult>> SearchAsync(SearchQuery query, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SearchResult>>(Array.Empty<SearchResult>());
    }
}

using ListaryOpen.App.ViewModels;
using ListaryOpen.Core.Indexing;
using ListaryOpen.Core.Search;

namespace ListaryOpen.Infrastructure.Tests.App;

public sealed class SearchPanelViewModelTests
{
    [Fact]
    public async Task ActivateFolderSearchAsyncShowsTrackedFolderForBlankQueryWithoutSearchingIndex()
    {
        var trackedFolder = Directory.CreateTempSubdirectory("listary-open-dialog-");
        var index = new RecordingSearchIndex(Array.Empty<SearchResult>());
        var viewModel = new SearchPanelViewModel(index);

        try
        {
            await viewModel.ActivateFolderSearchAsync(trackedFolder.FullName);

            var result = Assert.Single(viewModel.Results);
            Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(trackedFolder.FullName)), result.Record.FullPath);
            Assert.True(result.Record.IsDirectory);
            Assert.Empty(index.ObservedQueries);
        }
        finally
        {
            trackedFolder.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ActivateFolderSearchAsyncPrependsTrackedFolderAndQueriesFoldersOnly()
    {
        var trackedFolder = Directory.CreateTempSubdirectory("listary-open-dialog-");
        var now = DateTimeOffset.UtcNow;
        var trackedRecord = FileRecord.Create(trackedFolder.FullName, true, 0, now);
        var indexedFolder = FileRecord.Create("C:\\Docs\\Invoices", true, 0, now);
        var indexedFile = FileRecord.Create("C:\\Docs\\Invoice.xlsx", false, 10, now);
        var index = new RecordingSearchIndex(new[]
        {
            new SearchResult(indexedFile, 150, "index"),
            new SearchResult(trackedRecord, 125, "index"),
            new SearchResult(indexedFolder, 100, "index")
        });
        var viewModel = new SearchPanelViewModel(index)
        {
            QueryText = "invoice"
        };

        try
        {
            await index.WaitForSearchCountAsync(1);
            index.ClearObservedQueries();

            await viewModel.ActivateFolderSearchAsync(trackedFolder.FullName);

            var query = Assert.Single(index.ObservedQueries);
            Assert.Equal(SearchMode.FoldersOnly, query.Mode);
            Assert.Equal("invoice", query.NormalizedText);
            Assert.Equal(
                new[] { trackedRecord.FullPath, indexedFolder.FullPath },
                viewModel.Results.Select(result => result.Record.FullPath));
        }
        finally
        {
            trackedFolder.Delete(recursive: true);
        }
    }

    private sealed class RecordingSearchIndex : ISearchIndex
    {
        private readonly object _lock = new();
        private readonly IReadOnlyList<SearchResult> _results;
        private readonly List<SearchQuery> _queries = new();
        private TaskCompletionSource _searchObserved = CreateCompletionSource();

        public RecordingSearchIndex(IReadOnlyList<SearchResult> results)
        {
            _results = results;
        }

        public IReadOnlyList<SearchQuery> ObservedQueries
        {
            get
            {
                lock (_lock)
                {
                    return _queries.ToArray();
                }
            }
        }

        public Task UpsertAsync(FileRecord record, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string fullPath, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SearchResult>> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                _queries.Add(query);
                _searchObserved.TrySetResult();
            }

            return Task.FromResult(_results);
        }

        public void ClearObservedQueries()
        {
            lock (_lock)
            {
                _queries.Clear();
                _searchObserved = CreateCompletionSource();
            }
        }

        public async Task WaitForSearchCountAsync(int count)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            while (true)
            {
                Task observedTask;
                lock (_lock)
                {
                    if (_queries.Count >= count)
                    {
                        return;
                    }

                    observedTask = _searchObserved.Task;
                }

                var completed = await Task.WhenAny(observedTask, Task.Delay(Timeout.InfiniteTimeSpan, timeout.Token));
                if (completed != observedTask)
                {
                    throw new TimeoutException("The expected search was not observed.");
                }
            }
        }

        private static TaskCompletionSource CreateCompletionSource()
        {
            return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}

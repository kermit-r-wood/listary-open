using ListaryOpen.App.Search;
using ListaryOpen.App.ViewModels;
using ListaryOpen.Core.Indexing;
using ListaryOpen.Core.Search;

namespace ListaryOpen.Infrastructure.Tests.App;

public sealed class SearchResultDeleteTests
{
    [Fact]
    public async Task DeleteSelectedRemovesTempFileViaShippedActivatorAndIndex()
    {
        var path = Path.Combine(Path.GetTempPath(), "listary-delete-vm-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(path, "delete-me");
        var record = FileRecord.Create(path, false, 8, DateTimeOffset.UtcNow);
        var result = new SearchResult(record, 1, "exact-name");
        var index = new RecordingIndex();
        var activation = new RecordingActivation();
        var viewModel = new SearchPanelViewModel(index, activation)
        {
            SelectedResult = result
        };
        viewModel.Results.Add(result);

        var deleted = await viewModel.DeleteSelectedAsync(confirm: false);

        Assert.True(deleted);
        Assert.Contains(path, activation.DeletedPaths);
        Assert.Contains(path, index.DeletedPaths);
        Assert.DoesNotContain(viewModel.Results, item => item.Record.FullPath == path);
        Assert.Contains("Deleted", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SearchResultActivatorDeletesTempFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "listary-shell-delete-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(path, "shell-delete");
        Assert.True(File.Exists(path));

        var activator = new SearchResultActivator();
        await activator.DeleteAsync(path, isDirectory: false, allowUndo: true);

        Assert.False(File.Exists(path));
    }

    private sealed class RecordingActivation : ISearchResultActivationService
    {
        public List<string> DeletedPaths { get; } = new();

        public Task OpenAsync(string path) => Task.CompletedTask;

        public Task RevealAsync(string path, bool isDirectory) => Task.CompletedTask;

        public void CopyPath(string path)
        {
        }

        public Task DeleteAsync(string path, bool isDirectory, bool allowUndo = true)
        {
            DeletedPaths.Add(path);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingIndex : ISearchIndex
    {
        public List<string> DeletedPaths { get; } = new();

        public Task UpsertAsync(FileRecord record, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task UpsertManyAsync(IEnumerable<FileRecord> records, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeleteAsync(string fullPath, CancellationToken cancellationToken)
        {
            DeletedPaths.Add(fullPath);
            return Task.CompletedTask;
        }

        public Task RecordUsageAsync(string fullPath, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<SearchResult>> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<SearchResult>>(Array.Empty<SearchResult>());
    }
}

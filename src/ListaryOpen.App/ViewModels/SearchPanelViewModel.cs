using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using ListaryOpen.Core.Indexing;
using ListaryOpen.Core.Search;

namespace ListaryOpen.App.ViewModels;

public sealed class SearchPanelViewModel : INotifyPropertyChanged
{
    private readonly ISearchIndex _index;
    private int _refreshVersion;
    private string _queryText = string.Empty;
    private SearchMode _searchMode = SearchMode.FilesAndFolders;
    private string? _trackedFolder;

    public SearchPanelViewModel(ISearchIndex index)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<SearchResult> Results { get; } = new();

    public string QueryText
    {
        get => _queryText;
        set
        {
            if (_queryText == value)
            {
                return;
            }

            _queryText = value;
            OnPropertyChanged();
            _ = RefreshAsync();
        }
    }

    public Task ActivateFilesAndFoldersSearchAsync()
    {
        _searchMode = SearchMode.FilesAndFolders;
        _trackedFolder = null;
        return RefreshAsync();
    }

    public Task ActivateFolderSearchAsync(string? trackedFolder)
    {
        _searchMode = SearchMode.FoldersOnly;
        _trackedFolder = NormalizeExistingFolder(trackedFolder);
        return RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        var version = Interlocked.Increment(ref _refreshVersion);
        var queryText = _queryText;
        var searchMode = _searchMode;
        var trackedFolder = _trackedFolder;
        var trackedFolderResult = searchMode == SearchMode.FoldersOnly
            ? CreateTrackedFolderResult(trackedFolder)
            : null;

        Results.Clear();
        AddTrackedFolderResult(trackedFolderResult);

        if (string.IsNullOrWhiteSpace(queryText))
        {
            return;
        }

        IReadOnlyList<SearchResult> results;
        try
        {
            results = await _index.SearchAsync(
                new SearchQuery(queryText, searchMode),
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            Trace.TraceError(exception.ToString());

            if (version == _refreshVersion)
            {
                Results.Clear();
                AddTrackedFolderResult(trackedFolderResult);
            }

            return;
        }

        if (version != _refreshVersion)
        {
            return;
        }

        Results.Clear();
        AddTrackedFolderResult(trackedFolderResult);
        foreach (var result in results)
        {
            if (searchMode == SearchMode.FoldersOnly && !result.Record.IsDirectory)
            {
                continue;
            }

            if (trackedFolderResult is not null &&
                string.Equals(result.Record.PathKey, trackedFolderResult.Record.PathKey, StringComparison.Ordinal))
            {
                continue;
            }

            Results.Add(result);
        }
    }

    private void AddTrackedFolderResult(SearchResult? result)
    {
        if (result is not null)
        {
            Results.Add(result);
        }
    }

    private static SearchResult? CreateTrackedFolderResult(string? folderPath)
    {
        if (folderPath is null || !Directory.Exists(folderPath))
        {
            return null;
        }

        var record = FileRecord.Create(
            folderPath,
            isDirectory: true,
            sizeBytes: 0,
            new DateTimeOffset(Directory.GetLastWriteTimeUtc(folderPath), TimeSpan.Zero));

        return new SearchResult(record, double.MaxValue, "explorer");
    }

    private static string? NormalizeExistingFolder(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            return null;
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderPath.Trim()));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

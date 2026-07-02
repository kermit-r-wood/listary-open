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
    private readonly Func<string?, string?> _normalizeExistingFolder;
    private readonly Func<string, bool> _folderExists;
    private readonly Func<string, DateTimeOffset> _getFolderLastWriteTime;
    private int _refreshVersion;
    private string _queryText = string.Empty;
    private SearchMode _searchMode = SearchMode.FilesAndFolders;
    private string? _trackedFolder;

    public SearchPanelViewModel(ISearchIndex index)
        : this(index, NormalizeExistingFolder, Directory.Exists, GetFolderLastWriteTime)
    {
    }

    internal SearchPanelViewModel(
        ISearchIndex index,
        Func<string?, string?> normalizeExistingFolder,
        Func<string, bool> folderExists,
        Func<string, DateTimeOffset> getFolderLastWriteTime)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _normalizeExistingFolder = normalizeExistingFolder ?? throw new ArgumentNullException(nameof(normalizeExistingFolder));
        _folderExists = folderExists ?? throw new ArgumentNullException(nameof(folderExists));
        _getFolderLastWriteTime = getFolderLastWriteTime ?? throw new ArgumentNullException(nameof(getFolderLastWriteTime));
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
        _trackedFolder = TryNormalizeExistingFolder(trackedFolder);
        return RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        var version = Interlocked.Increment(ref _refreshVersion);
        var queryText = _queryText;
        var searchMode = _searchMode;
        var trackedFolder = _trackedFolder;
        var trackedFolderResult = searchMode == SearchMode.FoldersOnly
            ? TryCreateTrackedFolderResult(trackedFolder)
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

    private SearchResult? TryCreateTrackedFolderResult(string? folderPath)
    {
        if (folderPath is null)
        {
            return null;
        }

        try
        {
            if (!_folderExists(folderPath))
            {
                return null;
            }

            var record = FileRecord.Create(
                folderPath,
                isDirectory: true,
                sizeBytes: 0,
                TryGetFolderLastWriteTime(folderPath));

            return new SearchResult(record, double.MaxValue, "explorer");
        }
        catch (Exception exception) when (IsExpectedFolderPathException(exception))
        {
            Trace.TraceError(exception.ToString());
            return null;
        }
    }

    private string? TryNormalizeExistingFolder(string? folderPath)
    {
        try
        {
            return _normalizeExistingFolder(folderPath);
        }
        catch (Exception exception) when (IsExpectedFolderPathException(exception))
        {
            Trace.TraceError(exception.ToString());
            return null;
        }
    }

    private DateTimeOffset TryGetFolderLastWriteTime(string folderPath)
    {
        try
        {
            return _getFolderLastWriteTime(folderPath);
        }
        catch (Exception exception) when (IsExpectedFolderPathException(exception))
        {
            Trace.TraceError(exception.ToString());
            return DateTimeOffset.UnixEpoch;
        }
    }

    private static string? NormalizeExistingFolder(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            return null;
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderPath.Trim()));
    }

    private static DateTimeOffset GetFolderLastWriteTime(string folderPath)
    {
        return new DateTimeOffset(Directory.GetLastWriteTimeUtc(folderPath), TimeSpan.Zero);
    }

    private static bool IsExpectedFolderPathException(Exception exception)
    {
        return exception is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

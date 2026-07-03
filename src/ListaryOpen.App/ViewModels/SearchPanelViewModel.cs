using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using ListaryOpen.App.Search;
using ListaryOpen.Core.Indexing;
using ListaryOpen.Core.Search;

namespace ListaryOpen.App.ViewModels;

public sealed class SearchPanelViewModel : INotifyPropertyChanged
{
    private readonly ISearchIndex _index;
    private readonly Func<string?, string?> _normalizeExistingFolder;
    private readonly Func<string, bool> _folderExists;
    private readonly Func<string, DateTimeOffset> _getFolderLastWriteTime;
    private readonly ISearchResultActivationService _activationService;
    private int _refreshVersion;
    private string _queryText = string.Empty;
    private string _statusText = string.Empty;
    private SearchMode _searchMode = SearchMode.FilesAndFolders;
    private SearchResult? _selectedResult;
    private string? _trackedFolder;

    public SearchPanelViewModel(ISearchIndex index)
        : this(index, NormalizeExistingFolder, Directory.Exists, GetFolderLastWriteTime, new SearchResultActivator())
    {
    }

    internal SearchPanelViewModel(
        ISearchIndex index,
        ISearchResultActivationService activationService)
        : this(index, NormalizeExistingFolder, Directory.Exists, GetFolderLastWriteTime, activationService)
    {
    }

    internal SearchPanelViewModel(
        ISearchIndex index,
        Func<string?, string?> normalizeExistingFolder,
        Func<string, bool> folderExists,
        Func<string, DateTimeOffset> getFolderLastWriteTime)
        : this(index, normalizeExistingFolder, folderExists, getFolderLastWriteTime, new SearchResultActivator())
    {
    }

    internal SearchPanelViewModel(
        ISearchIndex index,
        Func<string?, string?> normalizeExistingFolder,
        Func<string, bool> folderExists,
        Func<string, DateTimeOffset> getFolderLastWriteTime,
        ISearchResultActivationService activationService)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _normalizeExistingFolder = normalizeExistingFolder ?? throw new ArgumentNullException(nameof(normalizeExistingFolder));
        _folderExists = folderExists ?? throw new ArgumentNullException(nameof(folderExists));
        _getFolderLastWriteTime = getFolderLastWriteTime ?? throw new ArgumentNullException(nameof(getFolderLastWriteTime));
        _activationService = activationService ?? throw new ArgumentNullException(nameof(activationService));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<SearchResult> Results { get; } = new();

    public SearchResult? SelectedResult
    {
        get => _selectedResult;
        set
        {
            if (Equals(_selectedResult, value))
            {
                return;
            }

            _selectedResult = value;
            OnPropertyChanged();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (_statusText == value)
            {
                return;
            }

            _statusText = value;
            OnPropertyChanged();
        }
    }

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
        StatusText = "Search files and folders.";
        return RefreshAsync();
    }

    public Task ActivateFolderSearchAsync(string? trackedFolder)
    {
        _searchMode = SearchMode.FoldersOnly;
        _trackedFolder = TryNormalizeExistingFolder(trackedFolder);
        StatusText = "Select a folder to jump after dialog integration is enabled.";
        return RefreshAsync();
    }

    public async Task ActivateSelectedAsync()
    {
        var selected = TryGetCurrentSelectedResult();
        if (selected is null)
        {
            return;
        }

        var path = selected.Record.FullPath;
        if (_searchMode == SearchMode.FoldersOnly && selected.Record.IsDirectory)
        {
            StatusText = "Select a folder to jump after dialog integration is enabled.";
            return;
        }

        try
        {
            await _activationService.OpenAsync(path);
            StatusText = $"Opened {path}";
        }
        catch (Exception exception) when (IsExpectedActivationException(exception))
        {
            Trace.TraceError(exception.ToString());
            StatusText = $"Could not open {path}: {exception.Message}";
        }
    }

    public async Task RevealSelectedAsync()
    {
        var selected = TryGetCurrentSelectedResult();
        if (selected is null)
        {
            return;
        }

        var path = selected.Record.FullPath;
        try
        {
            await _activationService.RevealAsync(path, selected.Record.IsDirectory);
            StatusText = $"Revealed {path}";
        }
        catch (Exception exception) when (IsExpectedActivationException(exception))
        {
            Trace.TraceError(exception.ToString());
            StatusText = $"Could not reveal {path}: {exception.Message}";
        }
    }

    public void CopySelectedPath()
    {
        var selected = TryGetCurrentSelectedResult();
        if (selected is null)
        {
            return;
        }

        var path = selected.Record.FullPath;
        try
        {
            _activationService.CopyPath(path);
            StatusText = $"Copied {path}";
        }
        catch (Exception exception) when (IsExpectedActivationException(exception))
        {
            Trace.TraceError(exception.ToString());
            StatusText = $"Could not copy {path}: {exception.Message}";
        }
    }

    internal void ReportUnexpectedInteractionError(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        Trace.TraceError(exception.ToString());
        StatusText = $"Unexpected interaction error: {exception.Message}";
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
        var hasQuery = !string.IsNullOrWhiteSpace(queryText);

        if (hasQuery)
        {
            StatusText = "Searching...";
        }

        Results.Clear();
        AddTrackedFolderResult(trackedFolderResult);
        UpdateSelectedResultAfterRefresh();

        if (!hasQuery)
        {
            StatusText = searchMode == SearchMode.FoldersOnly
                ? "Select a folder to jump after dialog integration is enabled."
                : "Type to search.";
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
                UpdateSelectedResultAfterRefresh();
                StatusText = $"Search failed: {exception.Message}";
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

        UpdateSelectedResultAfterRefresh();
        StatusText = Results.Count == 1 ? "1 result." : $"{Results.Count} results.";
    }

    private void UpdateSelectedResultAfterRefresh()
    {
        var selected = SelectedResult;
        if (selected is not null)
        {
            var refreshedSelection = Results.FirstOrDefault(result =>
                string.Equals(result.Record.PathKey, selected.Record.PathKey, StringComparison.Ordinal));
            if (refreshedSelection is not null)
            {
                SelectedResult = refreshedSelection;
                return;
            }
        }

        SelectedResult = Results.FirstOrDefault();
    }

    private SearchResult? TryGetCurrentSelectedResult()
    {
        var selected = SelectedResult;
        if (selected is null)
        {
            StatusText = "Select a result first.";
            return null;
        }

        var current = Results.FirstOrDefault(result =>
            string.Equals(result.Record.PathKey, selected.Record.PathKey, StringComparison.Ordinal));
        if (current is null)
        {
            UpdateSelectedResultAfterRefresh();
            StatusText = "Select a current result first.";
            return null;
        }

        if (!Equals(selected, current))
        {
            SelectedResult = current;
        }

        return current;
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

    private static bool IsExpectedActivationException(Exception exception)
    {
        return exception is ArgumentException or IOException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.ExternalException;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

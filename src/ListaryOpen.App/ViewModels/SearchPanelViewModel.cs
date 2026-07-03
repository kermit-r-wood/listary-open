using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using ListaryOpen.App.Search;
using ListaryOpen.Core.Indexing;
using ListaryOpen.Core.Search;
using ListaryOpen.Infrastructure.Dialog;
using ListaryOpen.Infrastructure.Windows;

namespace ListaryOpen.App.ViewModels;

public sealed class SearchPanelViewModel : INotifyPropertyChanged
{
    private readonly ISearchIndex _index;
    private readonly Func<string?, string?> _normalizeExistingFolder;
    private readonly Func<string, bool> _folderExists;
    private readonly Func<string, DateTimeOffset> _getFolderLastWriteTime;
    private readonly ISearchResultActivationService _activationService;
    private readonly Func<string, CancellationToken, Task<DialogJumpResult>> _dialogFolderActivation;
    private int _refreshVersion;
    private string _queryText = string.Empty;
    private string _statusText = string.Empty;
    private SearchMode _searchMode = SearchMode.FilesAndFolders;
    private SearchResult? _selectedResult;
    private IReadOnlyList<string> _pinnedFolderPaths = Array.Empty<string>();

    public SearchPanelViewModel(ISearchIndex index)
        : this(index, NormalizeExistingFolder, Directory.Exists, GetFolderLastWriteTime, new SearchResultActivator(), DialogJumpNotConfiguredAsync)
    {
    }

    internal SearchPanelViewModel(
        ISearchIndex index,
        ISearchResultActivationService activationService)
        : this(index, NormalizeExistingFolder, Directory.Exists, GetFolderLastWriteTime, activationService, DialogJumpNotConfiguredAsync)
    {
    }

    internal SearchPanelViewModel(
        ISearchIndex index,
        Func<string, CancellationToken, Task<DialogJumpResult>> dialogFolderActivation)
        : this(index, NormalizeExistingFolder, Directory.Exists, GetFolderLastWriteTime, new SearchResultActivator(), dialogFolderActivation)
    {
    }

    internal SearchPanelViewModel(
        ISearchIndex index,
        ISearchResultActivationService activationService,
        Func<string, CancellationToken, Task<DialogJumpResult>> dialogFolderActivation)
        : this(index, NormalizeExistingFolder, Directory.Exists, GetFolderLastWriteTime, activationService, dialogFolderActivation)
    {
    }

    internal SearchPanelViewModel(
        ISearchIndex index,
        Func<string?, string?> normalizeExistingFolder,
        Func<string, bool> folderExists,
        Func<string, DateTimeOffset> getFolderLastWriteTime)
        : this(index, normalizeExistingFolder, folderExists, getFolderLastWriteTime, new SearchResultActivator(), DialogJumpNotConfiguredAsync)
    {
    }

    internal SearchPanelViewModel(
        ISearchIndex index,
        Func<string?, string?> normalizeExistingFolder,
        Func<string, bool> folderExists,
        Func<string, DateTimeOffset> getFolderLastWriteTime,
        ISearchResultActivationService activationService,
        Func<string, CancellationToken, Task<DialogJumpResult>> dialogFolderActivation)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _normalizeExistingFolder = normalizeExistingFolder ?? throw new ArgumentNullException(nameof(normalizeExistingFolder));
        _folderExists = folderExists ?? throw new ArgumentNullException(nameof(folderExists));
        _getFolderLastWriteTime = getFolderLastWriteTime ?? throw new ArgumentNullException(nameof(getFolderLastWriteTime));
        _activationService = activationService ?? throw new ArgumentNullException(nameof(activationService));
        _dialogFolderActivation = dialogFolderActivation ?? throw new ArgumentNullException(nameof(dialogFolderActivation));
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
        _pinnedFolderPaths = Array.Empty<string>();
        StatusText = "Search files and folders.";
        return RefreshAsync();
    }

    public Task ActivateFolderSearchAsync(string? trackedFolder)
    {
        _searchMode = SearchMode.FoldersOnly;
        var normalizedFolder = TryNormalizeExistingFolder(trackedFolder);
        _pinnedFolderPaths = normalizedFolder is null
            ? Array.Empty<string>()
            : new[] { normalizedFolder };
        StatusText = "Select a folder to jump the dialog.";
        return RefreshAsync();
    }

    public Task ActivateFolderSearchAsync<TCandidates>(TCandidates candidates)
        where TCandidates : IReadOnlyList<QuickSwitchFolderCandidate>
    {
        ArgumentNullException.ThrowIfNull(candidates);

        _searchMode = SearchMode.FoldersOnly;
        _pinnedFolderPaths = NormalizePinnedFolderPaths(candidates);
        SelectedResult = null;
        StatusText = "Select a folder to jump the dialog.";
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
        if (_searchMode == SearchMode.FoldersOnly)
        {
            if (!selected.Record.IsDirectory)
            {
                StatusText = "Select a folder result to jump the dialog.";
                return;
            }

            await ActivateDialogFolderAsync(path);
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
        var pinnedFolderResults = searchMode == SearchMode.FoldersOnly
            ? CreatePinnedFolderResults(_pinnedFolderPaths)
            : Array.Empty<SearchResult>();
        var pinnedFolderPathKeys = new HashSet<string>(
            pinnedFolderResults.Select(result => result.Record.PathKey),
            StringComparer.Ordinal);
        var hasQuery = !string.IsNullOrWhiteSpace(queryText);

        if (hasQuery)
        {
            StatusText = "Searching...";
        }

        Results.Clear();
        AddPinnedFolderResults(pinnedFolderResults);
        UpdateSelectedResultAfterRefresh();

        if (!hasQuery)
        {
            StatusText = searchMode == SearchMode.FoldersOnly
                ? "Select a folder to jump the dialog."
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
                AddPinnedFolderResults(pinnedFolderResults);
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
        AddPinnedFolderResults(pinnedFolderResults);
        foreach (var result in results)
        {
            if (searchMode == SearchMode.FoldersOnly && !result.Record.IsDirectory)
            {
                continue;
            }

            if (pinnedFolderPathKeys.Contains(result.Record.PathKey))
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

    private void AddPinnedFolderResults(IReadOnlyList<SearchResult> results)
    {
        foreach (var result in results)
        {
            Results.Add(result);
        }
    }

    private IReadOnlyList<SearchResult> CreatePinnedFolderResults(IReadOnlyList<string> folderPaths)
    {
        var results = new List<SearchResult>(folderPaths.Count);
        var pathKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var folderPath in folderPaths)
        {
            var result = TryCreateTrackedFolderResult(folderPath);
            if (result is null || !pathKeys.Add(result.Record.PathKey))
            {
                continue;
            }

            results.Add(result);
        }

        return results;
    }

    private IReadOnlyList<string> NormalizePinnedFolderPaths(IReadOnlyList<QuickSwitchFolderCandidate> candidates)
    {
        var normalizedFolders = new List<string>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var normalizedFolder = TryNormalizeExistingFolder(candidate.FolderPath);
            if (normalizedFolder is not null)
            {
                normalizedFolders.Add(normalizedFolder);
            }
        }

        return normalizedFolders;
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

    private async Task ActivateDialogFolderAsync(string folderPath)
    {
        try
        {
            var result = await _dialogFolderActivation(folderPath, CancellationToken.None);
            StatusText = result.Status == DialogJumpStatus.Success
                ? (string.IsNullOrWhiteSpace(result.Message) ? "Dialog folder changed." : result.Message)
                : FormatDialogJumpFailure(result);
        }
        catch (Exception exception)
        {
            Trace.TraceError(exception.ToString());
            StatusText = $"Dialog jump failed: {exception.Message}";
        }
    }

    private static Task<DialogJumpResult> DialogJumpNotConfiguredAsync(string folderPath, CancellationToken cancellationToken)
    {
        return Task.FromResult(new DialogJumpResult(DialogJumpStatus.Failed, "Dialog jump is not configured."));
    }

    private static string FormatDialogJumpFailure(DialogJumpResult result)
    {
        return string.IsNullOrWhiteSpace(result.Message)
            ? $"Dialog jump failed: {result.Status}."
            : $"Dialog jump failed: {result.Status}: {result.Message}";
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

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
    private static readonly TimeSpan DefaultSearchDelay = TimeSpan.FromMilliseconds(200);

    private readonly ISearchIndex _index;
    private readonly Func<string?, string?> _normalizeExistingFolder;
    private readonly Func<string, bool> _folderExists;
    private readonly Func<string, DateTimeOffset> _getFolderLastWriteTime;
    private readonly ISearchResultActivationService _activationService;
    private readonly Func<string, CancellationToken, Task<DialogJumpResult>> _defaultDialogFolderActivation;
    private Func<string, CancellationToken, Task<DialogJumpResult>> _dialogFolderActivation;
    private readonly TimeSpan _searchDelay;
    private readonly object _refreshCancellationGate = new();
    private int _refreshVersion;
    private CancellationTokenSource? _refreshCancellation;
    private string _queryText = string.Empty;
    private string _statusText = string.Empty;
    private SearchMode _searchMode = SearchMode.FilesAndFolders;
    private SearchPanelPresentationMode _presentationMode = SearchPanelPresentationMode.Search;
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
        Func<string, CancellationToken, Task<DialogJumpResult>> dialogFolderActivation,
        TimeSpan? searchDelay = null)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _normalizeExistingFolder = normalizeExistingFolder ?? throw new ArgumentNullException(nameof(normalizeExistingFolder));
        _folderExists = folderExists ?? throw new ArgumentNullException(nameof(folderExists));
        _getFolderLastWriteTime = getFolderLastWriteTime ?? throw new ArgumentNullException(nameof(getFolderLastWriteTime));
        _activationService = activationService ?? throw new ArgumentNullException(nameof(activationService));
        _defaultDialogFolderActivation = dialogFolderActivation ?? throw new ArgumentNullException(nameof(dialogFolderActivation));
        _dialogFolderActivation = _defaultDialogFolderActivation;
        _searchDelay = searchDelay ?? DefaultSearchDelay;
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

    public string ModeDisplayText => _presentationMode switch
    {
        SearchPanelPresentationMode.Search => "Search",
        SearchPanelPresentationMode.DialogJump => "Dialog Jump",
        SearchPanelPresentationMode.QuickSwitch => "Quick Switch",
        _ => throw new ArgumentOutOfRangeException(
            nameof(_presentationMode),
            _presentationMode,
            "Unknown search panel presentation mode.")
    };

    public string QueryPlaceholderText => _presentationMode switch
    {
        SearchPanelPresentationMode.Search => "Search files and folders",
        SearchPanelPresentationMode.DialogJump => "Jump dialog to folder",
        SearchPanelPresentationMode.QuickSwitch => "Quick switch to folder",
        _ => throw new ArgumentOutOfRangeException(
            nameof(_presentationMode),
            _presentationMode,
            "Unknown search panel presentation mode.")
    };

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
            _ = RefreshAsync(delaySearch: true);
        }
    }

    public Task ActivateFilesAndFoldersSearchAsync()
    {
        _dialogFolderActivation = _defaultDialogFolderActivation;
        _searchMode = SearchMode.FilesAndFolders;
        _pinnedFolderPaths = Array.Empty<string>();
        StatusText = "Search files and folders.";
        SetPresentationMode(SearchPanelPresentationMode.Search);
        return RefreshAsync();
    }

    public Task ActivateFolderSearchAsync(string? trackedFolder)
    {
        _dialogFolderActivation = _defaultDialogFolderActivation;
        _searchMode = SearchMode.FoldersOnly;
        var normalizedFolder = TryNormalizeExistingFolder(trackedFolder);
        _pinnedFolderPaths = normalizedFolder is null
            ? Array.Empty<string>()
            : new[] { normalizedFolder };
        StatusText = "Select a folder to jump the dialog.";
        SetPresentationMode(SearchPanelPresentationMode.DialogJump);
        return RefreshAsync();
    }

    public Task ActivateQuickSwitchFolderSearchAsync(IReadOnlyList<QuickSwitchFolderCandidate> candidates) =>
        ActivateQuickSwitchFolderSearchAsync(candidates, _defaultDialogFolderActivation);

    internal Task ActivateQuickSwitchFolderSearchAsync(
        IReadOnlyList<QuickSwitchFolderCandidate> candidates,
        Func<string, CancellationToken, Task<DialogJumpResult>> dialogFolderActivation)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(dialogFolderActivation);

        _dialogFolderActivation = dialogFolderActivation;
        _searchMode = SearchMode.FoldersOnly;
        _pinnedFolderPaths = NormalizePinnedFolderPaths(candidates);
        SelectedResult = null;
        StatusText = "Select a folder to jump the dialog.";
        SetPresentationMode(SearchPanelPresentationMode.QuickSwitch);
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
            await RecordUsageAsync(path);
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

    internal void ReportDialogJumpResult(DialogJumpResult result)
    {
        StatusText = result.Status == DialogJumpStatus.Success
            ? (string.IsNullOrWhiteSpace(result.Message) ? "Dialog folder changed." : result.Message)
            : FormatDialogJumpFailure(result);
    }

    private async Task RecordUsageAsync(string path)
    {
        try
        {
            await _index.RecordUsageAsync(path, CancellationToken.None);
        }
        catch (Exception exception)
        {
            Trace.TraceError(exception.ToString());
        }
    }

    internal void MoveSelection(int delta)
    {
        if (delta == 0 || Results.Count == 0)
        {
            return;
        }

        var selectedIndex = -1;
        for (var index = 0; index < Results.Count; index++)
        {
            if (Equals(Results[index], SelectedResult))
            {
                selectedIndex = index;
                break;
            }
        }

        if (selectedIndex < 0)
        {
            selectedIndex = delta > 0 ? -1 : Results.Count;
        }

        SelectedResult = Results[Math.Clamp(selectedIndex + delta, 0, Results.Count - 1)];
    }

    private async Task RefreshAsync(bool delaySearch = false)
    {
        using var refreshCancellation = BeginRefreshCancellation();
        var cancellationToken = refreshCancellation.Token;
        try
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

            if (!hasQuery)
            {
                Results.Clear();
                AddPinnedFolderResults(pinnedFolderResults);
                UpdateSelectedResultAfterRefresh();
                StatusText = searchMode == SearchMode.FoldersOnly
                    ? "Select a folder to jump the dialog."
                    : "Type to search.";
                return;
            }

            var searchQuery = new SearchQuery(queryText, searchMode);
            if (searchMode == SearchMode.FoldersOnly && searchQuery.Parsed.FileOnly)
            {
                StatusText = "File-only search is unavailable while selecting a folder.";
                return;
            }

            IReadOnlyList<SearchResult> results;
            try
            {
                if (delaySearch && _searchDelay > TimeSpan.Zero)
                {
                    await Task.Delay(_searchDelay, cancellationToken);
                }

                StatusText = "Searching...";
                results = await _index.SearchAsync(searchQuery, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                Trace.TraceError(exception.ToString());

                if (version == _refreshVersion)
                {
                    StatusText = $"Search failed: {exception.Message}";
                }

                return;
            }

            if (version != _refreshVersion || cancellationToken.IsCancellationRequested)
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
        finally
        {
            CompleteRefreshCancellation(refreshCancellation);
        }
    }

    private CancellationTokenSource BeginRefreshCancellation()
    {
        var cancellation = new CancellationTokenSource();
        CancellationTokenSource? previousCancellation;

        lock (_refreshCancellationGate)
        {
            previousCancellation = _refreshCancellation;
            _refreshCancellation = cancellation;
            previousCancellation?.Cancel();
        }

        return cancellation;
    }

    private void CompleteRefreshCancellation(CancellationTokenSource cancellation)
    {
        lock (_refreshCancellationGate)
        {
            if (ReferenceEquals(_refreshCancellation, cancellation))
            {
                _refreshCancellation = null;
            }
        }
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
            ReportDialogJumpResult(result);
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

    private void SetPresentationMode(SearchPanelPresentationMode presentationMode)
    {
        if (_presentationMode == presentationMode)
        {
            return;
        }

        _presentationMode = presentationMode;
        OnPropertyChanged(nameof(ModeDisplayText));
        OnPropertyChanged(nameof(QueryPlaceholderText));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

internal enum SearchPanelPresentationMode
{
    Search,
    DialogJump,
    QuickSwitch
}

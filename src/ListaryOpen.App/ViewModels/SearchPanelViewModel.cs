using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using ListaryOpen.App.Search;
using ListaryOpen.Core.Indexing;
using ListaryOpen.Core.Search;
using ListaryOpen.Infrastructure.Dialog;
using ListaryOpen.Infrastructure.Windows;
using ListaryOpen.Core.Settings;

namespace ListaryOpen.App.ViewModels;

public sealed class SearchPanelViewModel : INotifyPropertyChanged
{
    private const int CancellationCheckIntervalMask = 63;
    private const int SearchResultPageSize = 50;
    private static readonly TimeSpan DefaultSearchDelay = TimeSpan.FromMilliseconds(80);
    private static readonly TimeSpan QuickLaunchActivationDelay = TimeSpan.FromMilliseconds(320);
    private static readonly TimeSpan DefaultCurrentFolderSnapshotLifetime = TimeSpan.FromSeconds(2);

    private readonly ISearchIndex _index;
    private readonly Func<string?, string?> _normalizeExistingFolder;
    private readonly Func<string, bool> _folderExists;
    private readonly Func<string, DateTimeOffset> _getFolderLastWriteTime;
    private readonly Func<string, CancellationToken, IReadOnlyList<FileRecord>> _enumerateCurrentFolderEntries;
    private readonly ISearchResultActivationService _activationService;
    private readonly Func<string, CancellationToken, Task<DialogJumpResult>> _defaultDialogFolderActivation;
    private Func<string, CancellationToken, Task<DialogJumpResult>> _dialogFolderActivation;
    private readonly TimeSpan _searchDelay;
    private readonly TimeSpan _currentFolderSnapshotLifetime;
    private readonly Func<IReadOnlyList<QuickLaunchEntry>> _quickLaunchEntries;
    private readonly IQuickLaunchExecutor _quickLaunchExecutor;
    private readonly object _refreshCancellationGate = new();
    private readonly object _currentFolderSessionGate = new();
    private int _refreshVersion;
    private CancellationTokenSource? _refreshCancellation;
    private CurrentFolderSession? _currentFolderSession;
    private string _queryText = string.Empty;
    private string? _queryTextBeforeExplorerTypeSearch;
    private string _statusText = string.Empty;
    private string _performanceText = string.Empty;
    private SearchMode _searchMode = SearchMode.FilesAndFolders;
    private SearchPanelPresentationMode _presentationMode = SearchPanelPresentationMode.Search;
    private SearchResult? _selectedResult;
    private IReadOnlyList<QuickSwitchFolderCandidate> _pinnedFolders = Array.Empty<QuickSwitchFolderCandidate>();
    private string? _preferredSearchRoot;
    private bool _isExplorerTypeSearchMode;
    private bool _isQuickSwitchBarCollapsed;
    private int _resultLimit = SearchResultPageSize;
    private bool _canLoadMore;
    private int _loadMoreRunning;
    private string? _lastExecutedQuickLaunchKeyword;
    private SearchItemTypeFilter _selectedItemTypeFilter;
    private SearchDateFilter _selectedDateFilter;

    private static readonly string[] DocumentExtensions = ["doc", "docx", "odt", "pdf", "ppt", "pptx", "rtf", "txt", "xls", "xlsx"];
    private static readonly string[] ImageExtensions = ["bmp", "gif", "jpeg", "jpg", "png", "svg", "tif", "tiff", "webp"];
    private static readonly string[] VideoExtensions = ["avi", "m4v", "mkv", "mov", "mp4", "mpeg", "mpg", "webm", "wmv"];

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
        Func<string, CancellationToken, Task<DialogJumpResult>> dialogFolderActivation,
        Func<IReadOnlyList<QuickLaunchEntry>> quickLaunchEntries,
        IQuickLaunchExecutor? quickLaunchExecutor = null)
        : this(
            index,
            NormalizeExistingFolder,
            Directory.Exists,
            GetFolderLastWriteTime,
            new SearchResultActivator(),
            dialogFolderActivation,
            quickLaunchEntries: quickLaunchEntries,
            quickLaunchExecutor: quickLaunchExecutor)
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
        TimeSpan? searchDelay = null,
        Func<string, CancellationToken, IReadOnlyList<FileRecord>>? enumerateCurrentFolderEntries = null,
        TimeSpan? currentFolderSnapshotLifetime = null,
        Func<IReadOnlyList<QuickLaunchEntry>>? quickLaunchEntries = null,
        IQuickLaunchExecutor? quickLaunchExecutor = null)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _normalizeExistingFolder = normalizeExistingFolder ?? throw new ArgumentNullException(nameof(normalizeExistingFolder));
        _folderExists = folderExists ?? throw new ArgumentNullException(nameof(folderExists));
        _getFolderLastWriteTime = getFolderLastWriteTime ?? throw new ArgumentNullException(nameof(getFolderLastWriteTime));
        _enumerateCurrentFolderEntries = enumerateCurrentFolderEntries ?? EnumerateCurrentFolderEntries;
        _activationService = activationService ?? throw new ArgumentNullException(nameof(activationService));
        _defaultDialogFolderActivation = dialogFolderActivation ?? throw new ArgumentNullException(nameof(dialogFolderActivation));
        _dialogFolderActivation = _defaultDialogFolderActivation;
        _searchDelay = searchDelay ?? DefaultSearchDelay;
        _currentFolderSnapshotLifetime = currentFolderSnapshotLifetime ?? DefaultCurrentFolderSnapshotLifetime;
        _quickLaunchEntries = quickLaunchEntries ?? (() => Array.Empty<QuickLaunchEntry>());
        _quickLaunchExecutor = quickLaunchExecutor ?? new QuickLaunchExecutor();
        _results = new SearchResultSnapshot(this, Array.Empty<SearchResult>());
        if (_currentFolderSnapshotLifetime < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(currentFolderSnapshotLifetime),
                "Current-folder snapshot lifetime cannot be negative.");
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? QuickLaunchExecuted;

    private SearchResultSnapshot _results;

    public SearchResultSnapshot Results => _results;

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
        get => global::ListaryOpen.App.LocalizationManager.Translate(_statusText);
        private set
        {
            if (_statusText == value)
            {
                return;
            }

            _statusText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(QuickSwitchInlineStatus));
        }
    }

    public string PerformanceText
    {
        get => _performanceText;
        private set
        {
            if (_performanceText == value)
            {
                return;
            }

            _performanceText = value;
            OnPropertyChanged();
        }
    }

    public string ModeDisplayText => global::ListaryOpen.App.LocalizationManager.Translate(_presentationMode switch
    {
        SearchPanelPresentationMode.Search => "Search",
        SearchPanelPresentationMode.DialogJump => "Dialog Jump",
        SearchPanelPresentationMode.QuickSwitch => "Quick Switch",
        _ => throw new ArgumentOutOfRangeException(
            nameof(_presentationMode),
            _presentationMode,
            "Unknown search panel presentation mode.")
    });

    public string QueryPlaceholderText => global::ListaryOpen.App.LocalizationManager.Translate(_presentationMode switch
    {
        SearchPanelPresentationMode.Search => "Search files and folders",
        SearchPanelPresentationMode.DialogJump => "Jump dialog to folder",
        SearchPanelPresentationMode.QuickSwitch => "Quick switch to folder",
        _ => throw new ArgumentOutOfRangeException(
            nameof(_presentationMode),
            _presentationMode,
            "Unknown search panel presentation mode.")
    });

    internal void RefreshLocalization()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ModeDisplayText));
        OnPropertyChanged(nameof(QueryPlaceholderText));
        OnPropertyChanged(nameof(QuickSwitchInlineStatus));
    }

    public bool IsFolderSelectionMode => _searchMode == SearchMode.FoldersOnly;

    public bool IsQuickSwitchMode => _presentationMode == SearchPanelPresentationMode.QuickSwitch;

    public bool IsExplorerTypeSearchMode => _isExplorerTypeSearchMode;

    public bool IsQuickSwitchBarCollapsed
    {
        get => _isQuickSwitchBarCollapsed;
        private set
        {
            if (_isQuickSwitchBarCollapsed == value)
            {
                return;
            }

            _isQuickSwitchBarCollapsed = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AreResultsVisible));
        }
    }

    public bool AreResultsVisible => Results.Count > 0 && (!IsQuickSwitchMode || !IsQuickSwitchBarCollapsed);

    public bool CanLoadMore
    {
        get => _canLoadMore;
        private set
        {
            if (_canLoadMore == value)
            {
                return;
            }

            _canLoadMore = value;
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<SearchItemTypeFilter> ItemTypeFilterOptions { get; } = Enum.GetValues<SearchItemTypeFilter>();

    public IReadOnlyList<SearchDateFilter> DateFilterOptions { get; } = Enum.GetValues<SearchDateFilter>();

    public SearchItemTypeFilter SelectedItemTypeFilter
    {
        get => _selectedItemTypeFilter;
        set
        {
            if (_selectedItemTypeFilter == value) return;
            _selectedItemTypeFilter = value;
            ResetResultLimit();
            OnPropertyChanged();
            _ = RefreshAsync(delaySearch: true);
        }
    }

    public SearchDateFilter SelectedDateFilter
    {
        get => _selectedDateFilter;
        set
        {
            if (_selectedDateFilter == value) return;
            _selectedDateFilter = value;
            ResetResultLimit();
            OnPropertyChanged();
            _ = RefreshAsync(delaySearch: true);
        }
    }

    internal void SetQuickSwitchBarCollapsed(bool collapsed)
    {
        IsQuickSwitchBarCollapsed = IsQuickSwitchMode && collapsed;
    }

    internal void ResetQuickSwitchQuery()
    {
        if (!IsQuickSwitchMode)
        {
            return;
        }

        SelectedResult = null;
        QueryText = string.Empty;
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
            ResetResultLimit();
            OnPropertyChanged();
            OnPropertyChanged(nameof(QuickSwitchInlineStatus));
            _ = RefreshAsync(delaySearch: true);
        }
    }

    public string QuickSwitchInlineStatus => string.IsNullOrWhiteSpace(QueryText) ? string.Empty : StatusText;

    public Task ActivateFilesAndFoldersSearchAsync()
    {
        ResetResultLimit();
        RestoreQueryBeforeExplorerTypeSearch();
        _dialogFolderActivation = _defaultDialogFolderActivation;
        _searchMode = SearchMode.FilesAndFolders;
        _pinnedFolders = Array.Empty<QuickSwitchFolderCandidate>();
        _preferredSearchRoot = null;
        _isExplorerTypeSearchMode = false;
        _lastExecutedQuickLaunchKeyword = null;
        StatusText = "Search files and folders.";
        SetPresentationMode(SearchPanelPresentationMode.Search);
        return RefreshAsync();
    }

    public Task ActivateExplorerSearchAsync(string initialQuery, string currentFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(initialQuery);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentFolder);

        var normalizedSearchRoot = TryNormalizeExistingFolder(currentFolder);
        var startsNewExplorerSession = !_isExplorerTypeSearchMode ||
            !string.Equals(_preferredSearchRoot, normalizedSearchRoot, StringComparison.OrdinalIgnoreCase);
        if (!_isExplorerTypeSearchMode)
        {
            _queryTextBeforeExplorerTypeSearch = _queryText;
        }

        _dialogFolderActivation = _defaultDialogFolderActivation;
        _searchMode = SearchMode.FilesAndFolders;
        _pinnedFolders = Array.Empty<QuickSwitchFolderCandidate>();
        _preferredSearchRoot = normalizedSearchRoot;
        _isExplorerTypeSearchMode = true;
        if (startsNewExplorerSession)
        {
            ResetResultLimit();
            StartCurrentFolderSession(normalizedSearchRoot);
        }

        SelectedResult = null;
        SetPresentationMode(SearchPanelPresentationMode.Search);

        var normalizedQuery = initialQuery.Trim();
        if (!string.Equals(_queryText, normalizedQuery, StringComparison.Ordinal))
        {
            _queryText = normalizedQuery;
            OnPropertyChanged(nameof(QueryText));
            OnPropertyChanged(nameof(QuickSwitchInlineStatus));
        }

        StatusText = _preferredSearchRoot is null
            ? "Searching files and folders."
            : $"Searching {_preferredSearchRoot} first.";
        return RefreshAsync();
    }

    public void DeactivateExplorerSearch()
    {
        if (!_isExplorerTypeSearchMode)
        {
            return;
        }

        CancelActiveRefresh();
        RestoreQueryBeforeExplorerTypeSearch();
        _preferredSearchRoot = null;
        PerformanceText = string.Empty;
        PublishResults(Array.Empty<SearchResult>());
        SelectedResult = null;
        StatusText = "Type to search.";
    }

    public Task ActivateFolderSearchAsync(string? trackedFolder)
    {
        ResetResultLimit();
        RestoreQueryBeforeExplorerTypeSearch();
        _dialogFolderActivation = _defaultDialogFolderActivation;
        _searchMode = SearchMode.FoldersOnly;
        _preferredSearchRoot = null;
        _isExplorerTypeSearchMode = false;
        var normalizedFolder = TryNormalizeExistingFolder(trackedFolder);
        _pinnedFolders = normalizedFolder is null
            ? Array.Empty<QuickSwitchFolderCandidate>()
            : new[] { new QuickSwitchFolderCandidate(normalizedFolder, "Tracked", IntPtr.Zero, true) };
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

        ResetResultLimit();
        RestoreQueryBeforeExplorerTypeSearch();
        _dialogFolderActivation = dialogFolderActivation;
        _searchMode = SearchMode.FoldersOnly;
        _preferredSearchRoot = null;
        _isExplorerTypeSearchMode = false;
        _pinnedFolders = NormalizePinnedFolders(candidates);
        SelectedResult = null;
        StatusText = "Select a folder to jump the dialog.";
        SetPresentationMode(SearchPanelPresentationMode.QuickSwitch);
        return RefreshAsync();
    }

    public async Task LoadMoreAsync()
    {
        if (!CanLoadMore || Interlocked.CompareExchange(ref _loadMoreRunning, 1, 0) != 0)
        {
            return;
        }

        try
        {
            CanLoadMore = false;
            _resultLimit = Math.Min(_resultLimit + SearchResultPageSize, SearchQuery.MaximumLimit);
            await RefreshAsync();
        }
        finally
        {
            Volatile.Write(ref _loadMoreRunning, 0);
        }
    }

    public async Task<bool> ActivateSelectedAsync()
    {
        var selected = TryGetCurrentSelectedResult();
        if (selected is null)
        {
            return false;
        }

        var path = selected.Record.FullPath;
        if (_searchMode == SearchMode.FoldersOnly)
        {
            if (!selected.Record.IsDirectory)
            {
                StatusText = "Select a folder result to jump the dialog.";
                return false;
            }

            return await ActivateDialogFolderAsync(path);
        }

        try
        {
            await _activationService.OpenAsync(path);
            StatusText = $"Opened {path}";
            await RecordUsageAsync(path);
            return true;
        }
        catch (Exception exception) when (IsExpectedActivationException(exception))
        {
            Trace.TraceError(exception.ToString());
            StatusText = $"Could not open {path}: {exception.Message}";
            return false;
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
            var preferredSearchRoot = _preferredSearchRoot;
            var searchCurrentFolderEntries = _isExplorerTypeSearchMode
                && searchMode == SearchMode.FilesAndFolders
                && preferredSearchRoot is not null;
            IReadOnlyList<SearchResult> pinnedFolderResults = searchMode == SearchMode.FoldersOnly
                ? CreatePinnedFolderResults(_pinnedFolders)
                : Array.Empty<SearchResult>();
            var hasQuery = !string.IsNullOrWhiteSpace(queryText);

            if (!hasQuery)
            {
                CanLoadMore = false;
                PerformanceText = string.Empty;
                var recentResults = _isExplorerTypeSearchMode
                    ? Array.Empty<SearchResult>()
                    : await _index.GetRecentAsync(12, cancellationToken);
                if (version != _refreshVersion || cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                var emptyQueryResults = new List<SearchResult>(pinnedFolderResults);
                var seenPathKeys = new HashSet<string>(
                    pinnedFolderResults.Select(result => result.Record.PathKey),
                    StringComparer.Ordinal);
                foreach (var recent in recentResults.Where(result => MatchesQuickFilters(result.Record)))
                {
                    if (searchMode == SearchMode.FoldersOnly && !recent.Record.IsDirectory)
                    {
                        continue;
                    }

                    if (seenPathKeys.Add(recent.Record.PathKey))
                    {
                        emptyQueryResults.Add(recent);
                    }
                }

                PublishResults(emptyQueryResults);
                UpdateSelectedResultAfterRefresh();
                StatusText = emptyQueryResults.Count == 0
                    ? searchMode == SearchMode.FoldersOnly
                        ? "Select a folder to jump the dialog."
                        : "Type to search."
                    : searchMode == SearchMode.FoldersOnly
                        ? "Recommended and recent folders for this dialog."
                        : "Recent files and folders.";
                return;
            }

            var quickLaunchCandidate = !_isExplorerTypeSearchMode &&
                _presentationMode == SearchPanelPresentationMode.Search &&
                FindQuickLaunchEntry(queryText) is not null;
            if (quickLaunchCandidate)
            {
                // Exact keywords are automatic, but only after the text has remained
                // stable briefly. A short keyword such as "op" must not fire while
                // the user is still typing "open".
                if (delaySearch)
                {
                    await Task.Delay(QuickLaunchActivationDelay, cancellationToken);
                }

                if (version != _refreshVersion || cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                if (await TryExecuteQuickLaunchAsync(queryText, cancellationToken))
                {
                    return;
                }
            }

            using var metrics = PerformanceMetrics.Begin("search.query");
            PerformanceMetrics.SetCounter("query_length", queryText.Length);
            SearchQuery searchQuery;
            using (PerformanceMetrics.MeasureStage("query.parse"))
            {
                searchQuery = new SearchQuery(
                    queryText,
                    searchMode,
                    limit: _resultLimit,
                    preferredRoot: preferredSearchRoot,
                    isDirectory: GetDirectoryFilter(),
                    requiredExtensions: GetRequiredExtensions(),
                    modifiedAfter: GetModifiedAfter());
            }

            pinnedFolderResults = pinnedFolderResults
                .Where(result => PinnedFolderMatchesQuery(result, searchQuery.Parsed))
                .ToArray();
            PerformanceMetrics.SetCounter("pinned_count", pinnedFolderResults.Count);
            var pinnedFolderPathKeys = new HashSet<string>(
                pinnedFolderResults.Select(result => result.Record.PathKey),
                StringComparer.Ordinal);

            if (searchMode == SearchMode.FoldersOnly && searchQuery.Parsed.FileOnly)
            {
                metrics.Complete("invalid_filter");
                StatusText = "File-only search is unavailable while selecting a folder.";
                return;
            }

            IReadOnlyList<SearchResult> results;
            IReadOnlyList<SearchResult> currentFolderResults = Array.Empty<SearchResult>();
            Task<IReadOnlyList<SearchResult>>? indexSearchTask = null;
            try
            {
                if (delaySearch && _searchDelay > TimeSpan.Zero)
                {
                    using (PerformanceMetrics.MeasureStage("ui.debounce"))
                    {
                        await Task.Delay(_searchDelay, cancellationToken);
                    }
                }

                StatusText = "Searching...";
                indexSearchTask = Task.Run(
                    async () =>
                    {
                        using (PerformanceMetrics.MeasureStage("index.total"))
                        {
                            return await _index.SearchAsync(searchQuery, cancellationToken).ConfigureAwait(false);
                        }
                    },
                    cancellationToken);
                var currentFolderSearchTask = searchCurrentFolderEntries
                    ? SearchCurrentFolderAsync(searchQuery, preferredSearchRoot!, cancellationToken)
                    : Task.FromResult<IReadOnlyList<SearchResult>>(Array.Empty<SearchResult>());

                using (PerformanceMetrics.MeasureStage("current_folder.total"))
                {
                    currentFolderResults = await currentFolderSearchTask;
                }

                if (searchCurrentFolderEntries &&
                    !indexSearchTask.IsCompleted &&
                    version == _refreshVersion &&
                    !cancellationToken.IsCancellationRequested)
                {
                    ReplaceResultsIfChanged(currentFolderResults);
                    UpdateSelectedResultAfterRefresh();
                    StatusText = currentFolderResults.Count switch
                    {
                        0 => "No current-folder results · checking index.",
                        1 => "1 current-folder result · checking index.",
                        _ => $"{currentFolderResults.Count} current-folder results · checking index."
                    };
                }

                IReadOnlyList<SearchResult> indexResults;
                indexResults = await indexSearchTask;

                results = MergeCurrentFolderResults(currentFolderResults, indexResults)
                    .Take(searchQuery.Limit)
                    .ToArray();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                ObserveTaskFailure(indexSearchTask);
                metrics.Complete("cancelled");
                return;
            }
            catch (Exception exception)
            {
                refreshCancellation.Cancel();
                ObserveTaskFailure(indexSearchTask);
                metrics.Complete("failed");
                Trace.TraceError(exception.ToString());

                if (version == _refreshVersion)
                {
                    StatusText = currentFolderResults.Count > 0
                        ? $"Showing current-folder results · index search failed: {exception.Message}"
                        : $"Search failed: {exception.Message}";
                }

                return;
            }

            if (version != _refreshVersion || cancellationToken.IsCancellationRequested)
            {
                metrics.Complete("superseded");
                return;
            }

            using (PerformanceMetrics.MeasureStage("ui.merge_results"))
            {
                var publishedResults = new List<SearchResult>(pinnedFolderResults);
                foreach (var result in PrioritizeResultsForRoot(results, preferredSearchRoot))
                {
                    if (searchMode == SearchMode.FoldersOnly && !result.Record.IsDirectory)
                    {
                        continue;
                    }

                    if (pinnedFolderPathKeys.Contains(result.Record.PathKey))
                    {
                        continue;
                    }

                    publishedResults.Add(result);
                }

                ReplaceResultsIfChanged(publishedResults);
                UpdateSelectedResultAfterRefresh();
            }

            CanLoadMore = results.Count >= searchQuery.Limit && searchQuery.Limit < SearchQuery.MaximumLimit;

            var snapshot = metrics.Complete("success", Results.Count);
            var indexMilliseconds = snapshot.Stages.GetValueOrDefault("index.total");
            PerformanceText = $"{snapshot.TotalMilliseconds:F1} ms total · {indexMilliseconds:F1} ms search";
            StatusText = preferredSearchRoot is null
                ? Results.Count == 1 ? "1 result." : $"{Results.Count} results."
                : Results.Count == 1
                    ? "1 result · current folder first."
                    : $"{Results.Count} results · current folder first.";
        }
        finally
        {
            CompleteRefreshCancellation(refreshCancellation);
        }
    }

    private async Task<bool> TryExecuteQuickLaunchAsync(string queryText, CancellationToken cancellationToken)
    {
        var normalized = queryText.Trim();
        var entry = FindQuickLaunchEntry(normalized);
        if (entry is null)
        {
            _lastExecutedQuickLaunchKeyword = null;
            return false;
        }

        if (string.Equals(_lastExecutedQuickLaunchKeyword, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await _quickLaunchExecutor.ExecuteAsync(entry);
            _lastExecutedQuickLaunchKeyword = normalized;
            PublishResults(Array.Empty<SearchResult>());
            SelectedResult = null;
            StatusText = $"Launched {entry.Title}.";
            QuickLaunchExecuted?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception) when (IsExpectedActivationException(exception))
        {
            StatusText = $"Could not launch {entry.Title}: {exception.Message}";
        }

        return true;
    }

    private QuickLaunchEntry? FindQuickLaunchEntry(string queryText)
    {
        var normalized = queryText.Trim();
        return _quickLaunchEntries().FirstOrDefault(candidate =>
            candidate.Enabled &&
            string.Equals(candidate.Keyword.Trim(), normalized, StringComparison.OrdinalIgnoreCase));
    }

    private void ResetResultLimit()
    {
        _resultLimit = SearchResultPageSize;
        CanLoadMore = false;
    }

    private CancellationTokenSource BeginRefreshCancellation()
    {
        var cancellation = new CancellationTokenSource();
        CancellationTokenSource? previousCancellation;

        lock (_refreshCancellationGate)
        {
            previousCancellation = _refreshCancellation;
            _refreshCancellation = cancellation;
        }

        previousCancellation?.Cancel();

        return cancellation;
    }

    private void CancelActiveRefresh()
    {
        CancellationTokenSource? cancellation;
        lock (_refreshCancellationGate)
        {
            cancellation = _refreshCancellation;
            _refreshCancellation = null;
            Interlocked.Increment(ref _refreshVersion);
        }

        cancellation?.Cancel();
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

    private void RestoreQueryBeforeExplorerTypeSearch()
    {
        if (!_isExplorerTypeSearchMode)
        {
            return;
        }

        var restoredQuery = _queryTextBeforeExplorerTypeSearch ?? string.Empty;
        _queryTextBeforeExplorerTypeSearch = null;
        _isExplorerTypeSearchMode = false;
        StopCurrentFolderSession();
        if (string.Equals(_queryText, restoredQuery, StringComparison.Ordinal))
        {
            return;
        }

        _queryText = restoredQuery;
        OnPropertyChanged(nameof(QueryText));
        OnPropertyChanged(nameof(QuickSwitchInlineStatus));
    }

    private async Task<IReadOnlyList<SearchResult>> SearchCurrentFolderAsync(
        SearchQuery query,
        string currentFolder,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<FileRecord> records;
        try
        {
            var entriesTask = GetOrStartCurrentFolderEntries(currentFolder);
            records = await entriesTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedFolderPathException(exception))
        {
            Trace.TraceWarning("Could not enumerate the current Explorer folder: {0}", exception.Message);
            return Array.Empty<SearchResult>();
        }

        return await Task.Run(
                () => ResultRanker.Rank(
                    query,
                    FilterCurrentFolderRecords(query, records, cancellationToken),
                    Array.Empty<ListaryOpen.Core.Usage.UsageRecord>(),
                    Array.Empty<string>(),
                    cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal static IEnumerable<FileRecord> FilterCurrentFolderRecords(
        SearchQuery query,
        IEnumerable<FileRecord> records,
        CancellationToken cancellationToken)
    {
        var recordCount = 0;
        foreach (var record in records)
        {
            if ((recordCount++ & CancellationCheckIntervalMask) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (MatchesParsedFilters(query, record))
            {
                yield return record;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private Task<IReadOnlyList<FileRecord>> GetOrStartCurrentFolderEntries(string currentFolder)
    {
        lock (_currentFolderSessionGate)
        {
            if (_currentFolderSession is not null &&
                string.Equals(
                    _currentFolderSession.CurrentFolder,
                    currentFolder,
                    StringComparison.OrdinalIgnoreCase))
            {
                var session = _currentFolderSession;
                if (session.ShouldRefresh(_currentFolderSnapshotLifetime) &&
                    session.EntriesTask.IsCompleted)
                {
                    session.Refresh(
                        Task.Run(
                            () => EnumerateCurrentFolderEntriesForSession(
                                currentFolder,
                                session.Cancellation.Token),
                            session.Cancellation.Token));
                }

                return session.EntriesTask;
            }
        }

        StartCurrentFolderSession(currentFolder);
        lock (_currentFolderSessionGate)
        {
            return _currentFolderSession?.EntriesTask ??
                Task.FromResult<IReadOnlyList<FileRecord>>(Array.Empty<FileRecord>());
        }
    }

    private void StartCurrentFolderSession(string? currentFolder)
    {
        StopCurrentFolderSession();
        if (string.IsNullOrWhiteSpace(currentFolder))
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        var entriesTask = Task.Run(
            () => EnumerateCurrentFolderEntriesForSession(currentFolder, cancellation.Token),
            cancellation.Token);
        FileSystemWatcher? watcher = null;
        try
        {
            if (Directory.Exists(currentFolder))
            {
                watcher = new FileSystemWatcher(currentFolder)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                };
            }
        }
        catch (Exception exception) when (IsExpectedFolderPathException(exception))
        {
            Trace.TraceWarning("Could not watch the current Explorer folder: {0}", exception.Message);
            watcher?.Dispose();
            watcher = null;
        }

        CurrentFolderSession session;
        try
        {
            session = new CurrentFolderSession(currentFolder, cancellation, entriesTask, watcher);
        }
        catch (Exception exception) when (IsExpectedFolderPathException(exception))
        {
            Trace.TraceWarning("Could not start watching the current Explorer folder: {0}", exception.Message);
            watcher?.Dispose();
            session = new CurrentFolderSession(currentFolder, cancellation, entriesTask, watcher: null);
        }

        lock (_currentFolderSessionGate)
        {
            _currentFolderSession = session;
        }
    }

    private IReadOnlyList<FileRecord> EnumerateCurrentFolderEntriesForSession(
        string currentFolder,
        CancellationToken cancellationToken)
    {
        try
        {
            return _enumerateCurrentFolderEntries(currentFolder, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedFolderPathException(exception))
        {
            Trace.TraceWarning("Could not enumerate the current Explorer folder: {0}", exception.Message);
            return Array.Empty<FileRecord>();
        }
    }

    private void StopCurrentFolderSession()
    {
        CurrentFolderSession? session;
        lock (_currentFolderSessionGate)
        {
            session = _currentFolderSession;
            _currentFolderSession = null;
        }

        if (session is null)
        {
            return;
        }

        session.Cancellation.Cancel();
        session.DisposeWatcher();
        _ = session.EntriesTask.ContinueWith(
            completedTask =>
            {
                _ = completedTask.Exception;
                session.Cancellation.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void ReplaceResultsIfChanged(IReadOnlyList<SearchResult> results)
    {
        if (Results.SequenceEqual(results))
        {
            return;
        }

        PublishResults(results);
    }

    internal void AddResultForTesting(SearchResult result) =>
        PublishResults(Results.Append(result));

    internal void SetResultsForTesting(IEnumerable<SearchResult> results) =>
        PublishResults(results);

    private void PublishResults(IEnumerable<SearchResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        var replacement = results.ToArray();
        if (_results.SequenceEqual(replacement))
        {
            return;
        }

        _results = new SearchResultSnapshot(this, replacement);
        OnPropertyChanged(nameof(Results));
        OnPropertyChanged(nameof(AreResultsVisible));
    }

    private static void ObserveTaskFailure(Task? task)
    {
        if (task is null || task.IsCompletedSuccessfully)
        {
            return;
        }

        _ = task.ContinueWith(
            completedTask => _ = completedTask.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static bool MatchesParsedFilters(SearchQuery query, FileRecord record)
    {
        if (query.Parsed.FileOnly && record.IsDirectory)
        {
            return false;
        }

        if (query.EffectiveMode == SearchMode.FoldersOnly && !record.IsDirectory)
        {
            return false;
        }

        var extension = Path.GetExtension(record.Name).TrimStart('.');
        if (query.Parsed.Extensions.Count > 0 &&
            !query.Parsed.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (query.Parsed.ExcludedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (query.Parsed.PathTerms.Any(term =>
                !record.FullPath.Contains(term, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (query.Parsed.Phrases.Any(phrase =>
                !record.FullPath.Contains(phrase, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return query.Parsed.ExcludedTerms.All(term =>
            !record.FullPath.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    internal static IReadOnlyList<SearchResult> MergeCurrentFolderResults(
        IReadOnlyList<SearchResult> currentFolderResults,
        IReadOnlyList<SearchResult> indexResults)
    {
        ArgumentNullException.ThrowIfNull(currentFolderResults);
        ArgumentNullException.ThrowIfNull(indexResults);

        var merged = new List<SearchResult>(currentFolderResults.Count + indexResults.Count);
        var seenPathKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var result in currentFolderResults.Concat(indexResults))
        {
            if (seenPathKeys.Add(result.Record.PathKey))
            {
                merged.Add(result);
            }
        }

        return merged;
    }

    internal static IReadOnlyList<FileRecord> EnumerateCurrentFolderEntries(
        string currentFolder,
        CancellationToken cancellationToken)
    {
        var records = new List<FileRecord>();
        try
        {
            foreach (var entry in new DirectoryInfo(currentFolder).EnumerateFileSystemInfos())
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var isDirectory = entry is DirectoryInfo;
                    records.Add(FileRecord.Create(
                        entry.FullName,
                        isDirectory,
                        sizeBytes: 0,
                        DateTimeOffset.UnixEpoch));
                }
                catch (Exception exception) when (IsExpectedFolderPathException(exception))
                {
                    Trace.TraceWarning("Could not read a current-folder entry: {0}", exception.Message);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedFolderPathException(exception))
        {
            Trace.TraceWarning("Could not enumerate current-folder entries: {0}", exception.Message);
        }

        return records;
    }

    private sealed class CurrentFolderSession
    {
        private readonly FileSystemWatcher? _watcher;
        private DateTimeOffset _snapshotCreatedAt = DateTimeOffset.UtcNow;
        private int _dirty;

        public CurrentFolderSession(
            string currentFolder,
            CancellationTokenSource cancellation,
            Task<IReadOnlyList<FileRecord>> entriesTask,
            FileSystemWatcher? watcher)
        {
            CurrentFolder = currentFolder;
            Cancellation = cancellation;
            EntriesTask = entriesTask;
            _watcher = watcher;
            if (_watcher is not null)
            {
                _watcher.Created += OnFolderChanged;
                _watcher.Deleted += OnFolderChanged;
                _watcher.Renamed += OnFolderChanged;
                _watcher.Error += OnWatcherError;
                _watcher.EnableRaisingEvents = true;
            }
        }

        public string CurrentFolder { get; }

        public CancellationTokenSource Cancellation { get; }

        public Task<IReadOnlyList<FileRecord>> EntriesTask { get; private set; }

        public bool ShouldRefresh(TimeSpan lifetime) =>
            Volatile.Read(ref _dirty) != 0 || DateTimeOffset.UtcNow - _snapshotCreatedAt >= lifetime;

        public void Refresh(Task<IReadOnlyList<FileRecord>> entriesTask)
        {
            EntriesTask = entriesTask;
            _snapshotCreatedAt = DateTimeOffset.UtcNow;
            Volatile.Write(ref _dirty, 0);
        }

        public void DisposeWatcher()
        {
            if (_watcher is null)
            {
                return;
            }

            _watcher.EnableRaisingEvents = false;
            _watcher.Created -= OnFolderChanged;
            _watcher.Deleted -= OnFolderChanged;
            _watcher.Renamed -= OnFolderChanged;
            _watcher.Error -= OnWatcherError;
            _watcher.Dispose();
        }

        private void OnFolderChanged(object sender, FileSystemEventArgs e) => Volatile.Write(ref _dirty, 1);

        private void OnWatcherError(object sender, ErrorEventArgs e) => Volatile.Write(ref _dirty, 1);
    }

    internal static IReadOnlyList<SearchResult> PrioritizeResultsForRoot(
        IReadOnlyList<SearchResult> results,
        string? rootPath)
    {
        ArgumentNullException.ThrowIfNull(results);
        if (string.IsNullOrWhiteSpace(rootPath) || results.Count < 2)
        {
            return results;
        }

        string normalizedRoot;
        try
        {
            normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or IOException
                                          or NotSupportedException
                                          or UnauthorizedAccessException)
        {
            return results;
        }

        return results
            .OrderBy(result => GetRootPriority(result.Record, normalizedRoot))
            .ToArray();
    }

    private static int GetRootPriority(FileRecord record, string normalizedRoot)
    {
        if (string.Equals(
                Path.TrimEndingDirectorySeparator(record.ParentPath),
                normalizedRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        var rootPrefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        return record.FullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) ? 1 : 2;
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

    private IReadOnlyList<SearchResult> CreatePinnedFolderResults(IReadOnlyList<QuickSwitchFolderCandidate> folders)
    {
        var results = new List<SearchResult>(folders.Count);
        var pathKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var folder in folders)
        {
            var result = TryCreateTrackedFolderResult(folder.FolderPath, folder.SourceName);
            if (result is null || !pathKeys.Add(result.Record.PathKey))
            {
                continue;
            }

            results.Add(result);
        }

        return results;
    }

    private IReadOnlyList<QuickSwitchFolderCandidate> NormalizePinnedFolders(IReadOnlyList<QuickSwitchFolderCandidate> candidates)
    {
        var normalizedFolders = new List<QuickSwitchFolderCandidate>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var normalizedFolder = TryNormalizeExistingFolder(candidate.FolderPath);
            if (normalizedFolder is not null)
            {
                normalizedFolders.Add(candidate with { FolderPath = normalizedFolder });
            }
        }

        return normalizedFolders;
    }

    private SearchResult? TryCreateTrackedFolderResult(string? folderPath, string sourceName)
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

            return new SearchResult(record, double.MaxValue, sourceName);
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

    private async Task<bool> ActivateDialogFolderAsync(string folderPath)
    {
        try
        {
            var result = await _dialogFolderActivation(folderPath, CancellationToken.None);
            ReportDialogJumpResult(result);
            return result.Status == DialogJumpStatus.Success;
        }
        catch (Exception exception)
        {
            Trace.TraceError(exception.ToString());
            StatusText = $"Dialog jump failed: {exception.Message}";
            return false;
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

    private bool? GetDirectoryFilter() => SelectedItemTypeFilter switch
    {
        SearchItemTypeFilter.Folders => true,
        SearchItemTypeFilter.Files or SearchItemTypeFilter.Documents or SearchItemTypeFilter.Images or SearchItemTypeFilter.Videos => false,
        _ => null
    };

    private IReadOnlyList<string> GetRequiredExtensions() => SelectedItemTypeFilter switch
    {
        SearchItemTypeFilter.Documents => DocumentExtensions,
        SearchItemTypeFilter.Images => ImageExtensions,
        SearchItemTypeFilter.Videos => VideoExtensions,
        _ => Array.Empty<string>()
    };

    private DateTimeOffset? GetModifiedAfter() => SelectedDateFilter switch
    {
        SearchDateFilter.Today => DateTimeOffset.Now.Date,
        SearchDateFilter.Last7Days => DateTimeOffset.Now.AddDays(-7),
        SearchDateFilter.Last30Days => DateTimeOffset.Now.AddDays(-30),
        SearchDateFilter.LastYear => DateTimeOffset.Now.AddYears(-1),
        _ => null
    };

    private bool MatchesQuickFilters(FileRecord record)
    {
        var isDirectory = GetDirectoryFilter();
        var extensions = GetRequiredExtensions();
        var modifiedAfter = GetModifiedAfter();
        return (isDirectory is null || record.IsDirectory == isDirectory) &&
            (extensions.Count == 0 || (!record.IsDirectory && extensions.Contains(
                Path.GetExtension(record.Name).TrimStart('.'), StringComparer.OrdinalIgnoreCase))) &&
            (modifiedAfter is null || record.LastWriteTime >= modifiedAfter);
    }

    private static bool PinnedFolderMatchesQuery(SearchResult result, ParsedSearchQuery query)
    {
        var path = result.Record.FullPath;
        var requiredTerms = query.Terms.Concat(query.Phrases).Concat(query.PathTerms);
        if (requiredTerms.Any(term => !path.Contains(term, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return query.ExcludedTerms.All(term => !path.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private void SetPresentationMode(SearchPanelPresentationMode presentationMode)
    {
        if (_presentationMode == presentationMode)
        {
            return;
        }

        _presentationMode = presentationMode;
        if (presentationMode != SearchPanelPresentationMode.QuickSwitch)
        {
            IsQuickSwitchBarCollapsed = false;
        }
        OnPropertyChanged(nameof(ModeDisplayText));
        OnPropertyChanged(nameof(QueryPlaceholderText));
        OnPropertyChanged(nameof(IsQuickSwitchMode));
        OnPropertyChanged(nameof(AreResultsVisible));
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

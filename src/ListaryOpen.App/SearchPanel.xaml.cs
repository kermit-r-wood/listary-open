using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ListaryOpen.App.ViewModels;
using ListaryOpen.Core.Indexing;
using ListaryOpen.Core.Search;
using ListaryOpen.Infrastructure.Dialog;
using ListaryOpen.Infrastructure.Windows;

namespace ListaryOpen.App;

public partial class SearchPanel : Window
{
    private const double CompactSearchHeight = 52;
    private const double CompactSearchWithFiltersHeight = 106;
    /// <summary>Minimum body height so the compact preview can show an image, not only the file name.</summary>
    private const double CompactPreviewMinBodyHeight = 400;
    private const double CompactResultRowHeight = 62;
    private const double CompactSearchMaxBodyHeight = 620;
    private const double CompactGlobalSearchPreferredContentWidth = 840;
    private const double CompactGlobalSearchWorkAreaMargin = 48;
    private const double CompactGlobalSearchVerticalMargin = 32;
    private readonly IExplorerSelectionService _explorerSelectionService;
    private readonly IExplorerNavigationService _explorerNavigationService;
    private IntPtr _explorerSearchWindow;
    private string? _explorerSearchFolder;
    private string? _lastSyncedExplorerPath;
    private bool _resultSelectionNavigatedWithKeyboard;
    private bool _isCompactGlobalSearch;
    private bool _searchFiltersAllowed;
    private int _resultLayoutUpdateVersion;
    private bool _resultContextMenuOpen;
    private SearchResult? _selectedExplorerResultForHostInput;
    /// <summary>Last user-placed (or last applied) position for compact global search, in WPF DIPs.</summary>
    private Point? _compactGlobalSearchPosition;
    /// <summary>Ignore hide-on-deactivate while the panel is still being shown/focused.</summary>
    private long _suppressDeactivateDismissUntilTick;

    public SearchPanel()
        : this(new SearchPanelViewModel(new EmptySearchIndex()))
    {
    }

    public SearchPanel(ISearchIndex index)
        : this(new SearchPanelViewModel(index))
    {
    }

    public SearchPanel(SearchPanelViewModel viewModel)
        : this(viewModel, new ExplorerSelectionService(), new ExplorerNavigationService())
    {
    }

    internal SearchPanel(
        SearchPanelViewModel viewModel,
        IExplorerSelectionService explorerSelectionService)
        : this(viewModel, explorerSelectionService, new ExplorerNavigationService())
    {
    }

    internal SearchPanel(
        SearchPanelViewModel viewModel,
        IExplorerSelectionService explorerSelectionService,
        IExplorerNavigationService explorerNavigationService)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(explorerSelectionService);
        ArgumentNullException.ThrowIfNull(explorerNavigationService);

        InitializeComponent();
        ResultsList.AddHandler(
            ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler(ResultsList_ScrollChanged));
        _explorerSelectionService = explorerSelectionService;
        _explorerNavigationService = explorerNavigationService;
        DataContext = viewModel;
        viewModel.PropertyChanged += ViewModel_PropertyChanged;
        viewModel.QuickLaunchExecuted += ViewModel_QuickLaunchExecuted;
        LocalizationManager.LanguageChanged += LocalizationManager_LanguageChanged;
    }

    internal event EventHandler? ExplorerSearchSessionEnded;

    /// <summary>
    /// Raised after a successful Explorer folder jump so the host can arm
    /// follow-up type-to-search for the same window.
    /// </summary>
    internal event EventHandler<ExplorerFollowUpArming>? ExplorerFollowUpTypingRequested;

    internal event EventHandler? ResultContextMenuOpenStateChanged;

    internal bool IsResultContextMenuOpen => _resultContextMenuOpen;

    /// <summary>
    /// Immutable selection snapshot published by the UI thread for the global
    /// keyboard-hook thread. Never read WPF or ObservableCollection state from
    /// the hook thread while forwarding Enter.
    /// </summary>
    internal SearchResult? SelectedResultForHostInput =>
        Volatile.Read(ref _selectedExplorerResultForHostInput);

    public void ActivateSearch()
    {
        EndExplorerSearchSession();
        _isCompactGlobalSearch = true;
        _ = ViewModel.ActivateFilesAndFoldersSearchAsync();
        ShowCompactGlobalSearch();
    }

    public void ActivateFolderSearch(string? trackedFolder)
    {
        EndExplorerSearchSession();
        _isCompactGlobalSearch = false;
        _ = ViewModel.ActivateFolderSearchAsync(trackedFolder);
        ShowAndFocusQuery();
    }

    public void ActivateExplorerSearch(string initialQuery, string currentFolder, IntPtr explorerWindow)
    {
        Volatile.Write(ref _selectedExplorerResultForHostInput, null);
        _isCompactGlobalSearch = false;
        _explorerSearchWindow = explorerWindow;
        _explorerSearchFolder = currentFolder;
        _lastSyncedExplorerPath = null;
        _ = ViewModel.ActivateExplorerSearchAsync(initialQuery, currentFolder);
        ShowExplorerTypeSearch(explorerWindow);
    }

    public bool IsExplorerTypeSearchActive => ViewModel.IsExplorerTypeSearchMode;

    internal void DismissExplorerSearch()
    {
        if (!ViewModel.IsExplorerTypeSearchMode && _explorerSearchWindow == IntPtr.Zero)
        {
            return;
        }

        ViewModel.DeactivateExplorerSearch();
        EndExplorerSearchSession();
        Hide();
    }

    private void EndExplorerSearchSession()
    {
        var hadExplorerSession = _explorerSearchWindow != IntPtr.Zero || ViewModel.IsExplorerTypeSearchMode;
        Volatile.Write(ref _selectedExplorerResultForHostInput, null);
        _explorerSelectionService.CancelPending();
        _explorerSearchWindow = IntPtr.Zero;
        _explorerSearchFolder = null;
        _lastSyncedExplorerPath = null;
        if (hadExplorerSession)
        {
            ExplorerSearchSessionEnded?.Invoke(this, EventArgs.Empty);
        }
    }

    internal void MoveSearchSelection(int delta)
    {
        if (delta is not (-1 or 1))
        {
            return;
        }

        _resultSelectionNavigatedWithKeyboard = true;
        ViewModel.MoveSelection(delta);
        if (ViewModel.SelectedResult is not null)
        {
            ResultsList.ScrollIntoView(ViewModel.SelectedResult);
        }
    }

    internal async Task ActivateResultShortcutAsync(int resultIndex)
    {
        if (resultIndex < 0 || resultIndex >= ViewModel.Results.Count)
        {
            return;
        }

        ViewModel.SelectedResult = ViewModel.Results[resultIndex];
        await ActivateSelectedFromQuickSwitchAsync();
    }

    internal bool ContainsScreenPoint(int screenX, int screenY)
    {
        var windowHandle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (VisibleWindowBounds.TryGet(windowHandle, out var rectangle) &&
            IsPointInsideBounds(screenX, screenY, rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom))
        {
            return true;
        }

        var pointedWindow = WindowFromPoint(new ScreenPoint(screenX, screenY));
        return pointedWindow != IntPtr.Zero &&
            GetWindowThreadProcessId(pointedWindow, out var processId) != 0 &&
            processId == Environment.ProcessId;
    }

    public void AppendExplorerSearchText(string text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            if (QueryBox.SelectionLength > 0)
            {
                var selectionStart = QueryBox.SelectionStart;
                QueryBox.SelectedText = text;
                QueryBox.Select(selectionStart + text.Length, 0);
                QueryBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            }
            else
            {
                ViewModel.QueryText += text;
            }

            MoveQueryCaretToEnd();
        }
    }

    internal void ExecuteExplorerSearchEditCommand(GlobalEditCommand command)
    {
        if (!IsExplorerTypeSearchActive || !IsVisible)
        {
            return;
        }

        switch (command)
        {
            case GlobalEditCommand.SelectAll:
                QueryBox.SelectAll();
                break;
            case GlobalEditCommand.Copy:
                QueryBox.Copy();
                break;
            case GlobalEditCommand.Cut:
                QueryBox.Cut();
                QueryBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                break;
            case GlobalEditCommand.Backspace:
                ApplyExplorerSearchBackspace();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(command), command, "Unknown edit command.");
        }
    }

    private void ApplyExplorerSearchBackspace()
    {
        var selectionStart = QueryBox.SelectionStart;
        if (QueryBox.SelectionLength > 0)
        {
            QueryBox.SelectedText = string.Empty;
            QueryBox.Select(selectionStart, 0);
        }
        else if (selectionStart > 0)
        {
            var deleteStart = selectionStart - 1;
            if (deleteStart > 0 &&
                char.IsLowSurrogate(QueryBox.Text[deleteStart]) &&
                char.IsHighSurrogate(QueryBox.Text[deleteStart - 1]))
            {
                deleteStart--;
            }

            QueryBox.Text = QueryBox.Text.Remove(deleteStart, selectionStart - deleteStart);
            QueryBox.Select(deleteStart, 0);
        }

        QueryBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
    }

    public void ActivateQuickSwitchFolderSearch(IReadOnlyList<QuickSwitchFolderCandidate> candidates)
    {
        _ = ActivateQuickSwitchFolderSearchAsync(candidates);
    }

    public async Task ActivateQuickSwitchFolderSearchAsync(IReadOnlyList<QuickSwitchFolderCandidate> candidates)
    {
        EndExplorerSearchSession();
        await ViewModel.ActivateQuickSwitchFolderSearchAsync(candidates);
        ShowAndFocusQuery();
    }

    internal async Task ActivateQuickSwitchFolderSearchAsync(
        IReadOnlyList<QuickSwitchFolderCandidate> candidates,
        Func<string, CancellationToken, Task<DialogJumpResult>> dialogFolderActivation)
    {
        await ActivateAnchoredQuickSwitchFolderSearchAsync(candidates, dialogFolderActivation, IntPtr.Zero);
    }

    internal async Task ActivateAnchoredQuickSwitchFolderSearchAsync(
        IReadOnlyList<QuickSwitchFolderCandidate> candidates,
        Func<string, CancellationToken, Task<DialogJumpResult>> dialogFolderActivation,
        IntPtr anchorWindow)
    {
        EndExplorerSearchSession();
        await ViewModel.ActivateQuickSwitchFolderSearchAsync(candidates, dialogFolderActivation);
        ShowAndFocusQuery(anchorWindow, compactQuickSwitch: true);
    }

    public void ReportDialogJumpResult(DialogJumpResult result)
    {
        ViewModel.ReportDialogJumpResult(result);
    }

    private SearchPanelViewModel ViewModel => (SearchPanelViewModel)DataContext;

    private void ShowAndFocusQuery()
        => ShowAndFocusQuery(IntPtr.Zero, compactQuickSwitch: false);

    private void ShowAndFocusQuery(IntPtr anchorWindow, bool compactQuickSwitch)
    {
        Topmost = true;
        ShowInTaskbar = true;
        ConfigurePresentation(compactQuickSwitch, collapsedBar: false);
        SuppressDeactivateDismiss();
        ShowActivated = true;
        Show();
        if (anchorWindow != IntPtr.Zero)
        {
            PositionNextToWindow(anchorWindow);
        }
        else
        {
            PositionAtWorkAreaCenter();
        }

        Activate();
        QueryBox.Focus();
    }

    private void ShowExplorerTypeSearch(IntPtr explorerWindow)
    {
        ConfigurePreviewPane(visible: false);
        ConfigureSearchFilters(visible: false);
        ResetExpandedSearchChrome();
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        MinWidth = 520;
        MinHeight = 280;
        // Shadow margin around the chrome border.
        Width = 560 + 20;
        Height = 360 + 20;
        SuppressDeactivateDismiss();
        // Keep Explorer as the foreground input owner for the complete typing
        // burst. Switching focus to WPF between injected/physical keystrokes
        // creates a hand-off window where neither the Explorer hook nor the
        // query TextBox receives a key. A user click can still activate this
        // window normally after it has been shown.
        ShowActivated = false;
        Show();
        PositionAtWindowBottomRight(explorerWindow);
        MoveQueryCaretToEnd();
    }

    private void ShowCompactGlobalSearch()
    {
        ConfigurePreviewPane(visible: true);
        ConfigureSearchFilters(visible: true);
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        MinWidth = 0;
        MinHeight = 0;
        var workArea = SystemParameters.WorkArea;
        // Outer window is transparent; chrome border + shadow need a small margin.
        var chromeMargin = 20;
        Width = CalculateCompactGlobalSearchOuterWidth(workArea.Width, chromeMargin);
        Height = (ShouldShowSearchFilters() ? CompactSearchWithFiltersHeight : CompactSearchHeight) + chromeMargin;
        SearchPanelRoot.Margin = new Thickness(0);
        WindowChromeBorder.Margin = new Thickness(10);
        WindowChromeBorder.CornerRadius = new CornerRadius(12);
        WindowChromeBorder.BorderThickness = new Thickness(1);
        WindowChromeBorder.SetResourceReference(Border.BackgroundProperty, "Brush.Surface");
        WindowChromeBorder.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        QueryRow.Margin = new Thickness(0);
        ModeHeaderRow.Height = new GridLength(0);
        StatusRow.Height = new GridLength(0);
        // Avoid double chrome: input bar sits flush inside the outer rounded surface.
        SearchInputChrome.CornerRadius = new CornerRadius(12);
        SearchInputChrome.BorderThickness = new Thickness(0);
        SearchInputChrome.Effect = null;
        SuppressDeactivateDismiss();
        ShowActivated = true;
        Show();
        PositionCompactGlobalSearch();
        Activate();
        FocusQueryAtEnd();
    }

    private void SuppressDeactivateDismiss(int milliseconds = 500) =>
        _suppressDeactivateDismissUntilTick = Environment.TickCount64 + milliseconds;

    private void UpdateCompactGlobalSearchSize()
    {
        if (!_isCompactGlobalSearch || !IsVisible)
        {
            return;
        }

        const double chromeMargin = 20;
        var previewVisible = PreviewPane.Visibility == Visibility.Visible;
        var desiredHeight = CalculateCompactGlobalSearchOuterHeight(
            ViewModel.Results.Count,
            ShouldShowSearchFilters(),
            previewVisible,
            chromeMargin);
        var workArea = SystemParameters.WorkArea;
        var minimumOuterHeight = CompactSearchHeight + chromeMargin;
        var availableOuterHeight = Math.Max(
            minimumOuterHeight,
            workArea.Height - CompactGlobalSearchVerticalMargin);
        Height = Math.Min(desiredHeight, availableOuterHeight);
        // Keep the user's dragged placement; only re-clamp so growth stays on-screen.
        PositionCompactGlobalSearch();
    }

    /// <summary>
    /// Keep compact search comfortably narrower than a desktop while still
    /// shrinking it to fit small work areas.
    /// </summary>
    internal static double CalculateCompactGlobalSearchOuterWidth(
        double workAreaWidth,
        double chromeMargin = 20)
    {
        if (!double.IsFinite(workAreaWidth) || workAreaWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(workAreaWidth));
        }
        if (!double.IsFinite(chromeMargin) || chromeMargin < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chromeMargin));
        }

        var availableContentWidth = Math.Max(
            0,
            workAreaWidth - CompactGlobalSearchWorkAreaMargin);
        return Math.Min(
            CompactGlobalSearchPreferredContentWidth,
            availableContentWidth) + chromeMargin;
    }

    /// <summary>
    /// Compact global search used to size height as query+filters+one result row (~168px).
    /// With the preview column open that crushed the image surface to ~0 height, so PNG/JPG
    /// previews looked blank. Reserve a minimum body when preview is visible.
    /// </summary>
    internal static double CalculateCompactGlobalSearchOuterHeight(
        int resultCount,
        bool filtersVisible,
        bool previewVisible,
        double chromeMargin = 20)
    {
        var baseHeight = filtersVisible ? CompactSearchWithFiltersHeight : CompactSearchHeight;
        if (resultCount <= 0)
        {
            return baseHeight + chromeMargin;
        }

        var resultsHeight = Math.Min(8, resultCount) * CompactResultRowHeight;
        var bodyHeight = previewVisible
            ? Math.Max(resultsHeight, CompactPreviewMinBodyHeight)
            : resultsHeight;
        bodyHeight = Math.Min(CompactSearchMaxBodyHeight, bodyHeight);
        return baseHeight + chromeMargin + bodyHeight;
    }

    private void PositionCompactGlobalSearch()
    {
        var workArea = SystemParameters.WorkArea;
        var position = CalculateCompactGlobalSearchPosition(
            Width,
            Height,
            workArea.Left,
            workArea.Top,
            workArea.Right,
            workArea.Bottom,
            _compactGlobalSearchPosition);
        Left = position.X;
        Top = position.Y;
        // Persist the applied (possibly clamped) placement for the next open.
        _compactGlobalSearchPosition = position;
    }

    private void RememberCompactGlobalSearchPosition()
    {
        if (!_isCompactGlobalSearch)
        {
            return;
        }

        _compactGlobalSearchPosition = new Point(Left, Top);
    }

    private void FocusQueryAtEnd()
    {
        QueryBox.Focus();
        MoveQueryCaretToEnd();
    }

    private void MoveQueryCaretToEnd()
    {
        QueryBox.Select(QueryBox.Text.Length, 0);
    }

    private void ConfigurePresentation(bool compactQuickSwitch, bool collapsedBar)
    {
        _isCompactGlobalSearch = false;
        ConfigurePreviewPane(visible: !compactQuickSwitch && !ViewModel.IsFolderSelectionMode);
        ConfigureSearchFilters(visible: !compactQuickSwitch && !ViewModel.IsFolderSelectionMode);
        ResetExpandedSearchChrome();
        // Always borderless + transparent so CornerRadius stays anti-aliased on Win10.
        ResizeMode = compactQuickSwitch ? ResizeMode.NoResize : ResizeMode.CanResize;
        var chromeMargin = 20;
        MinWidth = (compactQuickSwitch ? 510 : ViewModel.IsFolderSelectionMode ? 620 : 760) + chromeMargin;
        var quickSwitchHeight = ViewModel.Results.Count > 0 ? 330 : 140;
        MinHeight = (compactQuickSwitch ? (collapsedBar ? 88 : 120) : 360) + chromeMargin;
        Width = (compactQuickSwitch ? 510 : ViewModel.IsFolderSelectionMode ? 760 : 1080) + chromeMargin;
        Height = (compactQuickSwitch ? (collapsedBar ? 88 : quickSwitchHeight) : 560) + chromeMargin;
    }

    private void ResetExpandedSearchChrome()
    {
        SearchPanelRoot.Margin = new Thickness(18);
        WindowChromeBorder.Margin = new Thickness(10);
        WindowChromeBorder.CornerRadius = new CornerRadius(12);
        WindowChromeBorder.BorderThickness = new Thickness(1);
        WindowChromeBorder.SetResourceReference(Border.BackgroundProperty, "Brush.AppBackground");
        WindowChromeBorder.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        SearchInputChrome.ClearValue(Border.CornerRadiusProperty);
        SearchInputChrome.ClearValue(Border.BorderThicknessProperty);
        SearchInputChrome.ClearValue(UIElement.EffectProperty);
        QueryRow.Margin = new Thickness(0, 0, 0, 14);
        ModeHeaderRow.Height = GridLength.Auto;
        StatusRow.Height = GridLength.Auto;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Transparent WPF chrome is anti-aliased; clear any legacy hard window region.
        NativeWindowCorner.ClearRoundedChrome(this);
    }

    private void QueueResultsLayoutUpdate()
    {
        var version = Interlocked.Increment(ref _resultLayoutUpdateVersion);
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Loaded,
            new Action(() =>
            {
                if (version != _resultLayoutUpdateVersion || !IsVisible)
                {
                    return;
                }

                UpdateLayoutAfterResultsPublished();
            }));
    }

    private void UpdateLayoutAfterResultsPublished()
    {
        if (_isCompactGlobalSearch)
        {
            UpdateCompactGlobalSearchSize();
            return;
        }

    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(SearchPanelViewModel.Results), StringComparison.Ordinal))
        {
            QueueResultsLayoutUpdate();
        }

        if (string.Equals(e.PropertyName, nameof(SearchPanelViewModel.SelectedResult), StringComparison.Ordinal))
        {
            Volatile.Write(
                ref _selectedExplorerResultForHostInput,
                ViewModel.IsExplorerTypeSearchMode && _explorerSearchWindow != IntPtr.Zero
                    ? ViewModel.SelectedResult
                    : null);
            SyncSelectedResultToExplorer();
            _ = PreviewPane.ShowPreviewAsync(
                PreviewPane.Visibility == Visibility.Visible ? ViewModel.SelectedResult?.Record : null);
        }

        if (string.Equals(e.PropertyName, nameof(SearchPanelViewModel.QueryText), StringComparison.Ordinal))
        {
            UpdateSearchFilterVisibility();
            UpdateCompactGlobalSearchSize();
        }
    }

    private void ConfigurePreviewPane(bool visible)
    {
        PreviewGapColumn.Width = visible ? new GridLength(6) : new GridLength(0);
        ResultsColumn.Width = visible ? new GridLength(62, GridUnitType.Star) : new GridLength(1, GridUnitType.Star);
        PreviewColumn.Width = visible ? new GridLength(38, GridUnitType.Star) : new GridLength(0);
        PreviewSplitter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        PreviewPane.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (visible)
        {
            _ = PreviewPane.ShowPreviewAsync(ViewModel.SelectedResult?.Record);
        }
        else
        {
            PreviewPane.Clear();
        }
    }

    private void ConfigureSearchFilters(bool visible)
    {
        _searchFiltersAllowed = visible;
        UpdateSearchFilterVisibility();
    }

    private void UpdateSearchFilterVisibility() =>
        SearchFilterBar.Visibility = ShouldShowSearchFilters() ? Visibility.Visible : Visibility.Collapsed;

    private bool ShouldShowSearchFilters() =>
        _searchFiltersAllowed && !string.IsNullOrWhiteSpace(ViewModel.QueryText);

    private void ViewModel_QuickLaunchExecuted(object? sender, EventArgs e)
    {
        if (_isCompactGlobalSearch && IsVisible)
        {
            Hide();
        }
    }

    private void SyncSelectedResultToExplorer()
    {
        var selectedPath = ViewModel.SelectedResult?.Record.FullPath;
        if (!ViewModel.IsExplorerTypeSearchMode ||
            _explorerSearchWindow == IntPtr.Zero ||
            string.IsNullOrWhiteSpace(_explorerSearchFolder))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            _lastSyncedExplorerPath = null;
            _explorerSelectionService.CancelPending();
            return;
        }

        if (string.Equals(selectedPath, _lastSyncedExplorerPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _lastSyncedExplorerPath = selectedPath;
        _explorerSelectionService.QueueSelectItem(
            _explorerSearchWindow,
            _explorerSearchFolder,
            selectedPath);
    }

    private void PositionNextToWindow(IntPtr anchorWindow)
    {
        if (!VisibleWindowBounds.TryGet(anchorWindow, out var rectangle))
        {
            var fallbackWorkArea = SystemParameters.WorkArea;
            Left = fallbackWorkArea.Left + Math.Max(0, (fallbackWorkArea.Width - Width) / 2);
            Top = fallbackWorkArea.Top + Math.Max(0, (fallbackWorkArea.Height - Height) / 2);
            return;
        }

        var fallbackDpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var dpiScale = VisibleWindowBounds.GetWindowDpiScale(anchorWindow, fallbackDpiScale);
        var workArea = GetAnchorWorkArea(anchorWindow, dpiScale);
        var anchorLeft = rectangle.Left / dpiScale;
        var anchorWidth = (rectangle.Right - rectangle.Left) / dpiScale;
        var anchorBottom = rectangle.Bottom / dpiScale;
        Left = CalculateCenteredLeft(anchorLeft, anchorWidth, Width, workArea.Left, workArea.Right);
        Top = anchorBottom + Height <= workArea.Bottom
            ? anchorBottom
            : Math.Max(workArea.Top, rectangle.Top / dpiScale - Height);
    }

    private void PositionAtWindowBottomRight(IntPtr anchorWindow)
    {
        if (!VisibleWindowBounds.TryGet(anchorWindow, out var rectangle))
        {
            var fallbackWorkArea = SystemParameters.WorkArea;
            var fallback = CalculateBottomRightPosition(
                fallbackWorkArea.Right,
                fallbackWorkArea.Bottom,
                Width,
                Height,
                fallbackWorkArea.Left,
                fallbackWorkArea.Top,
                fallbackWorkArea.Right,
                fallbackWorkArea.Bottom);
            Left = fallback.X;
            Top = fallback.Y;
            return;
        }

        var fallbackDpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var dpiScale = VisibleWindowBounds.GetWindowDpiScale(anchorWindow, fallbackDpiScale);
        var workArea = GetAnchorWorkArea(anchorWindow, dpiScale);
        var position = CalculateBottomRightPosition(
            rectangle.Right / dpiScale,
            rectangle.Bottom / dpiScale,
            Width,
            Height,
            workArea.Left,
            workArea.Top,
            workArea.Right,
            workArea.Bottom);
        Left = position.X;
        Top = position.Y;
    }

    private static Rect GetAnchorWorkArea(IntPtr anchorWindow, double dpiScale) =>
        VisibleWindowBounds.TryGetMonitorWorkArea(anchorWindow, out var physicalWorkArea)
            ? new Rect(
                physicalWorkArea.Left / dpiScale,
                physicalWorkArea.Top / dpiScale,
                (physicalWorkArea.Right - physicalWorkArea.Left) / dpiScale,
                (physicalWorkArea.Bottom - physicalWorkArea.Top) / dpiScale)
            : SystemParameters.WorkArea;

    private void PositionAtWorkAreaCenter()
    {
        var workArea = SystemParameters.WorkArea;
        var position = CalculateCenteredPosition(
            Width,
            Height,
            workArea.Left,
            workArea.Top,
            workArea.Right,
            workArea.Bottom);
        Left = position.X;
        Top = position.Y;
    }

    internal static Point CalculateBottomRightPosition(
        double anchorRight,
        double anchorBottom,
        double panelWidth,
        double panelHeight,
        double workAreaLeft,
        double workAreaTop,
        double workAreaRight,
        double workAreaBottom,
        double margin = 12)
    {
        var maximumLeft = Math.Max(workAreaLeft, workAreaRight - panelWidth);
        var maximumTop = Math.Max(workAreaTop, workAreaBottom - panelHeight);
        return new Point(
            Math.Clamp(anchorRight - panelWidth - margin, workAreaLeft, maximumLeft),
            Math.Clamp(anchorBottom - panelHeight - margin, workAreaTop, maximumTop));
    }

    internal static Point CalculateCenteredPosition(
        double panelWidth,
        double panelHeight,
        double workAreaLeft,
        double workAreaTop,
        double workAreaRight,
        double workAreaBottom)
    {
        var maximumLeft = Math.Max(workAreaLeft, workAreaRight - panelWidth);
        var maximumTop = Math.Max(workAreaTop, workAreaBottom - panelHeight);
        return new Point(
            Math.Clamp(workAreaLeft + (workAreaRight - workAreaLeft - panelWidth) / 2, workAreaLeft, maximumLeft),
            Math.Clamp(workAreaTop + (workAreaBottom - workAreaTop - panelHeight) / 2, workAreaTop, maximumTop));
    }

    /// <summary>
    /// Compact global search: restore a remembered placement when present, otherwise center
    /// in the work area (both axes). Always clamps so the panel stays on-screen.
    /// </summary>
    internal static Point CalculateCompactGlobalSearchPosition(
        double panelWidth,
        double panelHeight,
        double workAreaLeft,
        double workAreaTop,
        double workAreaRight,
        double workAreaBottom,
        Point? rememberedPosition)
    {
        if (rememberedPosition is { } remembered)
        {
            var maximumLeft = Math.Max(workAreaLeft, workAreaRight - panelWidth);
            var maximumTop = Math.Max(workAreaTop, workAreaBottom - panelHeight);
            return new Point(
                Math.Clamp(remembered.X, workAreaLeft, maximumLeft),
                Math.Clamp(remembered.Y, workAreaTop, maximumTop));
        }

        return CalculateCenteredPosition(
            panelWidth,
            panelHeight,
            workAreaLeft,
            workAreaTop,
            workAreaRight,
            workAreaBottom);
    }

    internal static double CalculateCenteredLeft(
        double anchorLeft,
        double anchorWidth,
        double panelWidth,
        double workAreaLeft,
        double workAreaRight)
    {
        var centeredLeft = anchorLeft + (anchorWidth - panelWidth) / 2;
        return Math.Clamp(centeredLeft, workAreaLeft, Math.Max(workAreaLeft, workAreaRight - panelWidth));
    }

    internal static bool IsPointInsideBounds(
        int screenX,
        int screenY,
        int left,
        int top,
        int right,
        int bottom) =>
        screenX >= left && screenX < right && screenY >= top && screenY < bottom;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!App.IsShuttingDown)
        {
            e.Cancel = true;
            if (ViewModel.IsExplorerTypeSearchMode || _explorerSearchWindow != IntPtr.Zero)
            {
                DismissExplorerSearch();
            }
            else
            {
                Hide();
            }

            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        PreviewPane.Clear();
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ViewModel.QuickLaunchExecuted -= ViewModel_QuickLaunchExecuted;
        LocalizationManager.LanguageChanged -= LocalizationManager_LanguageChanged;
        if (_explorerSelectionService is IDisposable disposableSelectionService)
        {
            disposableSelectionService.Dispose();
        }

        base.OnClosed(e);
    }

    private void LocalizationManager_LanguageChanged(object? sender, EventArgs e) =>
        ViewModel.RefreshLocalization();

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        // A WPF context menu owns a separate popup window. Let that popup keep
        // keyboard focus so Escape and menu navigation are delivered to it.
        if (_resultContextMenuOpen)
        {
            return;
        }

        if (Environment.TickCount64 < _suppressDeactivateDismissUntilTick)
        {
            var hasExplorerSearchSession =
                ViewModel.IsExplorerTypeSearchMode || _explorerSearchWindow != IntPtr.Zero;
            if (ShouldRestoreFocusAfterSuppressedDeactivation(IsVisible, hasExplorerSearchSession))
            {
                Topmost = true;
                Activate();
                if (_isCompactGlobalSearch)
                {
                    FocusQueryAtEnd();
                }
                else
                {
                    QueryBox.Focus();
                }
            }

            return;
        }

        if (!ViewModel.IsExplorerTypeSearchMode &&
            _explorerSearchWindow == IntPtr.Zero &&
            !_resultContextMenuOpen)
        {
            Topmost = false;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (Environment.TickCount64 < _suppressDeactivateDismissUntilTick)
                {
                    return;
                }

                if (IsVisible && !IsActive && !ViewModel.IsExplorerTypeSearchMode)
                {
                    Hide();
                }
            }));
            return;
        }

        if (!ViewModel.IsExplorerTypeSearchMode || _explorerSearchWindow == IntPtr.Zero)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (Environment.TickCount64 < _suppressDeactivateDismissUntilTick)
            {
                return;
            }

            var overlayWindow = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (ShouldDismissExplorerSearchAfterDeactivation(
                    IsVisible,
                    IsActive,
                    ViewModel.IsExplorerTypeSearchMode,
                    GetForegroundWindow(),
                    _explorerSearchWindow,
                    overlayWindow))
            {
                DismissExplorerSearch();
            }
        }));
    }

    /// <summary>
    /// Explorer type-to-search deliberately leaves Explorer in the foreground.
    /// Reactivating this window during the initial deactivation grace period
    /// creates a one-key hand-off gap between the global hook and the WPF edit.
    /// </summary>
    internal static bool ShouldRestoreFocusAfterSuppressedDeactivation(
        bool isVisible,
        bool hasExplorerSearchSession) =>
        isVisible && !hasExplorerSearchSession;

    internal static bool ShouldDismissExplorerSearchAfterDeactivation(
        bool isVisible,
        bool isActive,
        bool isExplorerSearchMode,
        IntPtr foregroundWindow,
        IntPtr explorerWindow,
        IntPtr overlayWindow) =>
        isVisible &&
        !isActive &&
        isExplorerSearchMode &&
        explorerWindow != IntPtr.Zero &&
        foregroundWindow != IntPtr.Zero &&
        foregroundWindow != explorerWindow &&
        foregroundWindow != overlayWindow;

    private async void ResultsList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange == 0 ||
            e.OriginalSource is not ScrollViewer scrollViewer ||
            !ShouldLoadMoreOnScroll(scrollViewer.VerticalOffset, scrollViewer.ScrollableHeight))
        {
            return;
        }

        await ViewModel.LoadMoreAsync();
    }

    internal static bool ShouldLoadMoreOnScroll(double verticalOffset, double scrollableHeight) =>
        scrollableHeight > 0 && verticalOffset >= scrollableHeight - 2;

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        if (e.Handled)
        {
            return;
        }

        var action = GetPreviewKeyAction(e.Key);
        if (action == SearchPanelPreviewKeyAction.HidePanel)
        {
            e.Handled = true;
            if (ViewModel.IsExplorerTypeSearchMode || _explorerSearchWindow != IntPtr.Zero)
            {
                DismissExplorerSearch();
            }
            else
            {
                Close();
            }
            return;
        }

        if (action is SearchPanelPreviewKeyAction.MoveSelectionUp or SearchPanelPreviewKeyAction.MoveSelectionDown)
        {
            e.Handled = true;
            MoveSearchSelection(action == SearchPanelPreviewKeyAction.MoveSelectionUp ? -1 : 1);

            return;
        }

        if (action == SearchPanelPreviewKeyAction.OpenSelectedMenu &&
            _resultSelectionNavigatedWithKeyboard &&
            OpenSelectedResultContextMenu())
        {
            e.Handled = true;
            return;
        }

        _resultSelectionNavigatedWithKeyboard = false;

        var isControlPressed = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shortcutIndex = GetQuickSwitchShortcutIndex(e.Key, isControlPressed);
        if (shortcutIndex >= 0 && shortcutIndex < ViewModel.Results.Count)
        {
            e.Handled = true;
            _ = ActivateResultShortcutAsync(shortcutIndex);
            return;
        }

        if (e.Key == Key.Enter && isControlPressed)
        {
            if (ImeInputGuard.ShouldDeferEnterToIme(Keyboard.FocusedElement as DependencyObject))
            {
                return;
            }

            e.Handled = true;
            _ = RunInteractionAsync(ViewModel, viewModel => viewModel.RevealSelectedAsync());
            return;
        }

        if (e.Key == Key.Enter)
        {
            // Let IME consume Enter while composing (e.g. Chinese candidate confirm).
            if (ImeInputGuard.ShouldDeferEnterToIme(Keyboard.FocusedElement as DependencyObject))
            {
                return;
            }

            e.Handled = true;
            _ = ActivateSelectedFromQuickSwitchAsync();
            return;
        }

        var focusedElement = Keyboard.FocusedElement as DependencyObject;
        if (e.Key == Key.C &&
            isControlPressed &&
            ShouldCopySelectedResultPath(IsFocusWithin(QueryBox, focusedElement), IsTextInputFocus(focusedElement)))
        {
            e.Handled = true;
            _ = RunInteractionAsync(
                ViewModel,
                viewModel =>
                {
                    viewModel.CopySelectedPath();
                    return Task.CompletedTask;
                });
        }
    }

    private void ResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        _ = ActivateSelectedFromQuickSwitchAsync();
    }

    private void ResultsList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!ViewModel.IsExplorerTypeSearchMode)
        {
            return;
        }

        var item = FindVisualAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (item is null)
        {
            return;
        }

        item.IsSelected = true;
        _ = ActivateSelectedFromQuickSwitchAsync();
    }

    private void ResultsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        CloseResultContextMenu();

    private void SearchPanel_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        CloseResultContextMenu();
        if (BorderlessWindowInteraction.TryBeginDrag(this, e))
        {
            RememberCompactGlobalSearchPosition();
        }
    }

    private void ResultsList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = FindVisualAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (item is not null)
        {
            CloseResultContextMenu();
            item.IsSelected = true;
            item.Focus();
            e.Handled = true;
        }
    }

    private void ResultsList_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var item = FindVisualAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (item is null || e.ChangedButton != MouseButton.Right)
        {
            return;
        }

        item.IsSelected = true;
        item.Focus();
        e.Handled = OpenSelectedResultContextMenu(PlacementMode.MousePoint, item);
    }

    private bool OpenSelectedResultContextMenu(
        PlacementMode placement = PlacementMode.Right,
        UIElement? placementTarget = null)
    {
        var selected = ViewModel.SelectedResult;
        var contextMenu = ResultsList.ContextMenu;
        if (selected is null || contextMenu is null)
        {
            return false;
        }

        ResultsList.ScrollIntoView(selected);
        ResultsList.UpdateLayout();
        var item = ResultsList.ItemContainerGenerator.ContainerFromItem(selected) as ListBoxItem;
        item?.Focus();
        contextMenu.PlacementTarget = placementTarget ?? (UIElement?)item ?? ResultsList;
        contextMenu.Placement = placement;
        // ContextMenu uses a separate popup HWND and deactivates this window.
        // Mark it open first so OnDeactivated leaves the popup focused.
        _resultContextMenuOpen = true;
        contextMenu.IsOpen = true;
        if (contextMenu.IsOpen)
        {
            return true;
        }

        _resultContextMenuOpen = false;
        return false;
    }

    private void ResultsContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        _resultContextMenuOpen = true;
        ResultContextMenuOpenStateChanged?.Invoke(this, EventArgs.Empty);
        OpenResultMenuItem.Header = ViewModel.IsFolderSelectionMode
            ? "Switch to this folder"
            : "Open";
        OpenResultMenuItem.Focus();
        Keyboard.Focus(OpenResultMenuItem);
    }

    private void ResultsContextMenu_Closed(object sender, RoutedEventArgs e)
    {
        _resultContextMenuOpen = false;
        ResultContextMenuOpenStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ResultsContextMenu_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        e.Handled = true;
        CloseResultContextMenu();
        Activate();
        QueryBox.Focus();
    }

    internal void CloseResultContextMenu()
    {
        if (ResultsList.ContextMenu is { IsOpen: true } contextMenu)
        {
            contextMenu.IsOpen = false;
        }

        if (_resultContextMenuOpen)
        {
            _resultContextMenuOpen = false;
            ResultContextMenuOpenStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OpenResultMenuItem_Click(object sender, RoutedEventArgs e)
    {
        CloseResultContextMenu();
        _ = ActivateSelectedFromQuickSwitchAsync();
    }

    private async Task ActivateSelectedFromQuickSwitchAsync()
        => await ActivateSelectedFromQuickSwitchAsync(ViewModel.SelectedResult);

    private async Task ActivateSelectedFromQuickSwitchAsync(SearchResult? selected)
    {
        var explorerWindow = _explorerSearchWindow;
        var wasExplorerTypeSearch = ViewModel.IsExplorerTypeSearchMode && explorerWindow != IntPtr.Zero;
        var explorerFolderBeforeActivation = _explorerSearchFolder;
        var activated = await RunInteractionAsync(
            ViewModel,
            viewModel => viewModel.ActivateSelectedAsync(
                selected,
                wasExplorerTypeSearch
                    ? (path, cancellationToken) =>
                        _explorerNavigationService.NavigateToFolderAsync(
                            explorerWindow,
                            path,
                            cancellationToken)
                    : null),
            fallback: false);
        if (!activated)
        {
            return;
        }

        if (wasExplorerTypeSearch)
        {
            DismissExplorerSearch();
            // Shell may reassign focus to the address/search edit after Navigate2
            // and after our overlay hides. Re-assert the folder view and ask the
            // host to arm follow-up type-to-search for the next printable key.
            // Prefer the navigated directory when Enter jumped into a folder; for
            // files keep the Explorer folder that was current before activation.
            var followUpFolder = selected?.Record.IsDirectory == true
                ? selected.Record.FullPath
                : explorerFolderBeforeActivation;
            PrepareExplorerHostForFollowUpTyping(explorerWindow, followUpFolder);
        }
    }

    /// <summary>
    /// After an in-place Explorer folder jump, restore list focus and arm the
    /// host so typing again reopens type-to-search even if focus briefly lands
    /// on a native text control.
    /// </summary>
    internal void PrepareExplorerHostForFollowUpTyping(IntPtr explorerWindow, string? folderPath = null)
    {
        if (explorerWindow == IntPtr.Zero)
        {
            return;
        }

        _ = _explorerNavigationService.TryFocusFolderView(explorerWindow);
        ExplorerFollowUpTypingRequested?.Invoke(
            this,
            new ExplorerFollowUpArming(explorerWindow, folderPath ?? string.Empty));

        // Shell address-band focus often arrives a tick after Navigate2 returns.
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            new Action(() => _ = _explorerNavigationService.TryFocusFolderView(explorerWindow)));
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.ApplicationIdle,
            new Action(() => _ = _explorerNavigationService.TryFocusFolderView(explorerWindow)));
    }

    internal void ConfirmSelectionFromHostInput() =>
        _ = ActivateSelectedFromQuickSwitchAsync(ViewModel.SelectedResult);

    internal void ConfirmSelectionFromHostInput(SearchResult? selectedResult) =>
        _ = ActivateSelectedFromQuickSwitchAsync(selectedResult);

    private void RevealResultMenuItem_Click(object sender, RoutedEventArgs e)
    {
        CloseResultContextMenu();
        _ = RunInteractionAsync(ViewModel, viewModel => viewModel.RevealSelectedAsync());
    }

    private void CopyPathMenuItem_Click(object sender, RoutedEventArgs e)
    {
        CloseResultContextMenu();
        _ = RunInteractionAsync(
            ViewModel,
            viewModel =>
            {
                viewModel.CopySelectedPath();
                return Task.CompletedTask;
            });
    }

    private void DeleteResultMenuItem_Click(object sender, RoutedEventArgs e)
    {
        CloseResultContextMenu();
        _ = RunInteractionAsync(ViewModel, viewModel => viewModel.DeleteSelectedAsync(confirm: true));
    }

    internal static bool ShouldCopySelectedResultPath(bool focusIsQueryBox, bool focusIsTextInput)
    {
        return !focusIsQueryBox && !focusIsTextInput;
    }

    internal static SearchPanelPreviewKeyAction GetPreviewKeyAction(Key key)
    {
        return key switch
        {
            Key.Escape => SearchPanelPreviewKeyAction.HidePanel,
            Key.Up => SearchPanelPreviewKeyAction.MoveSelectionUp,
            Key.Down => SearchPanelPreviewKeyAction.MoveSelectionDown,
            Key.Right => SearchPanelPreviewKeyAction.OpenSelectedMenu,
            _ => SearchPanelPreviewKeyAction.None
        };
    }

    internal static int GetQuickSwitchShortcutIndex(Key key, bool isControlPressed)
    {
        if (!isControlPressed)
        {
            return -1;
        }

        return key switch
        {
            >= Key.D1 and <= Key.D9 => key - Key.D1,
            >= Key.NumPad1 and <= Key.NumPad9 => key - Key.NumPad1,
            _ => -1
        };
    }

    internal static async Task RunInteractionAsync(
        SearchPanelViewModel viewModel,
        Func<SearchPanelViewModel, Task> interaction)
    {
        try
        {
            await interaction(viewModel);
        }
        catch (Exception exception)
        {
            viewModel.ReportUnexpectedInteractionError(exception);
        }
    }

    internal static async Task<T> RunInteractionAsync<T>(
        SearchPanelViewModel viewModel,
        Func<SearchPanelViewModel, Task<T>> interaction,
        T fallback)
    {
        try
        {
            return await interaction(viewModel);
        }
        catch (Exception exception)
        {
            viewModel.ReportUnexpectedInteractionError(exception);
            return fallback;
        }
    }

    private static bool IsTextInputFocus(DependencyObject? focusedElement)
    {
        return focusedElement is TextBoxBase or PasswordBox;
    }

    private static bool IsFocusWithin(DependencyObject ancestor, DependencyObject? focusedElement)
    {
        var current = focusedElement;
        while (current is not null)
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }

            current = LogicalTreeHelper.GetParent(current) ?? VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private static T? FindVisualAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        var current = element;
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private sealed class EmptySearchIndex : ISearchIndex
    {
        public Task UpsertAsync(FileRecord record, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string fullPath, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task RecordUsageAsync(string fullPath, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SearchResult>> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<SearchResult>>(Array.Empty<SearchResult>());
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(ScreenPoint point);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out int processId);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct ScreenPoint(int x, int y)
    {
        public readonly int X = x;
        public readonly int Y = y;
    }

}

internal enum SearchPanelPreviewKeyAction
{
    None,
    HidePanel,
    MoveSelectionUp,
    MoveSelectionDown,
    OpenSelectedMenu
}

/// <summary>
/// Arms post-jump type-to-search for an Explorer window, carrying the folder that
/// is currently shown so Backspace can navigate to its parent reliably.
/// </summary>
internal sealed record ExplorerFollowUpArming(IntPtr ExplorerWindow, string FolderPath);

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ListaryOpen.App.ViewModels;
using ListaryOpen.Core.Search;
using ListaryOpen.Infrastructure.Dialog;
using ListaryOpen.Infrastructure.Windows;

namespace ListaryOpen.App;

public partial class QuickSwitchBarWindow : Window
{
    private const double ChromeMargin = 8;
    private readonly Func<IntPtr, bool> _activateAnchorWindow;
    private IntPtr _anchorWindow;
    private IntPtr _windowHandle;
    private bool _contextMenuOpen;
    private bool _resultSelectionNavigatedWithKeyboard;
    private NativeRectangle _lastAnchorBounds;
    private double _lastAnchorDpiScale;
    private bool _hasCachedPosition;
    private bool _repositionInvalidated = true;
    private bool _followUpTypingArmed;
    private long _suppressCollapseUntilTick;

    internal event EventHandler? AttachmentChanged;

    internal event EventHandler? InputCaptureStateChanged;

    public QuickSwitchBarWindow(SearchPanelViewModel viewModel)
        : this(viewModel, SetForegroundWindow)
    {
    }

    internal QuickSwitchBarWindow(
        SearchPanelViewModel viewModel,
        Func<IntPtr, bool> activateAnchorWindow)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(activateAnchorWindow);
        _activateAnchorWindow = activateAnchorWindow;
        InitializeComponent();
        QuickSwitchResults.AddHandler(
            ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler(Results_ScrollChanged));
        DataContext = viewModel;
    }

    private SearchPanelViewModel ViewModel => (SearchPanelViewModel)DataContext;

    internal bool IsAttached { get; private set; }

    internal IntPtr AnchorWindow => _anchorWindow;

    internal IntPtr WindowHandle => _windowHandle;

    internal bool IsAnchorWindowAlive => IsAttached && VisibleWindowBounds.IsAlive(_anchorWindow);

    internal bool IsAnchorWindowAvailable => IsAttached && VisibleWindowBounds.IsAvailable(_anchorWindow);

    internal bool IsDialogSearchExpanded => IsAttached && !ViewModel.IsQuickSwitchBarCollapsed;

    internal bool IsFollowUpTypingArmed => IsAttached && _followUpTypingArmed;

    internal bool IsDialogTextInputCaptureActive =>
        IsAttached && (IsDialogSearchExpanded || _followUpTypingArmed);

    internal async Task AttachAsync(
        IReadOnlyList<QuickSwitchFolderCandidate> candidates,
        Func<string, CancellationToken, Task<DialogJumpResult>> activation,
        IntPtr anchorWindow)
    {
        await ViewModel.ActivateQuickSwitchFolderSearchAsync(candidates, activation);
        // The periodic dialog probe and Ctrl+G can both observe the same dialog.
        // If a probe refresh finishes after the direct jump has armed follow-up
        // typing, refreshing candidates must not silently disarm that next key.
        var preserveFollowUpTyping = IsAttached &&
            _anchorWindow == anchorWindow &&
            _followUpTypingArmed;
        ViewModel.ResetQuickSwitchQuery();
        var attachmentChanged = !IsAttached || _anchorWindow != anchorWindow;
        _anchorWindow = anchorWindow;
        IsAttached = true;
        if (attachmentChanged)
        {
            AttachmentChanged?.Invoke(this, EventArgs.Empty);
        }

        InvalidateReposition();
        Collapse();
        if (!IsVisible)
        {
            Show();
        }

        Reposition();
        if (preserveFollowUpTyping)
        {
            PrepareForFollowUpTyping();
        }
    }

    internal void Detach()
    {
        if (!IsAttached)
        {
            return;
        }

        IsAttached = false;
        _anchorWindow = IntPtr.Zero;
        _followUpTypingArmed = false;
        _hasCachedPosition = false;
        _repositionInvalidated = true;
        Hide();
        AttachmentChanged?.Invoke(this, EventArgs.Empty);
        InputCaptureStateChanged?.Invoke(this, EventArgs.Empty);
    }

    internal bool TryAppendDialogText(string text, IntPtr dialogWindow)
    {
        if (!IsAttached ||
            dialogWindow == IntPtr.Zero ||
            !GlobalTextInputService.IsConfiguredDialogInputWindow(dialogWindow, _anchorWindow) ||
            string.IsNullOrEmpty(text))
        {
            return false;
        }

        if (ViewModel.IsQuickSwitchBarCollapsed)
        {
            Expand();
        }

        QuickSwitchQueryBox.Focus();
        var selectionStart = Math.Clamp(
            QuickSwitchQueryBox.SelectionStart,
            0,
            QuickSwitchQueryBox.Text.Length);
        var selectionLength = Math.Clamp(
            QuickSwitchQueryBox.SelectionLength,
            0,
            QuickSwitchQueryBox.Text.Length - selectionStart);
        QuickSwitchQueryBox.Text = QuickSwitchQueryBox.Text
            .Remove(selectionStart, selectionLength)
            .Insert(selectionStart, text);
        QuickSwitchQueryBox.Select(selectionStart + text.Length, 0);
        QuickSwitchQueryBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        return true;
    }

    internal bool ContainsScreenPoint(int screenX, int screenY)
    {
        var windowHandle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        return VisibleWindowBounds.TryGet(windowHandle, out var rectangle) &&
            screenX >= rectangle.Left &&
            screenX < rectangle.Right &&
            screenY >= rectangle.Top &&
            screenY < rectangle.Bottom;
    }

    internal void MoveSelectionFromDialogInput(int delta, IntPtr dialogWindow)
    {
        if (!CanHandleDialogInput(dialogWindow))
        {
            return;
        }

        _resultSelectionNavigatedWithKeyboard = true;
        ViewModel.MoveSelection(delta);
        if (ViewModel.SelectedResult is not null)
        {
            QuickSwitchResults.ScrollIntoView(ViewModel.SelectedResult);
        }
    }

    internal void ExecuteEditCommandFromDialogInput(GlobalEditCommand command, IntPtr dialogWindow)
    {
        if (!CanHandleDialogInput(dialogWindow))
        {
            return;
        }

        switch (command)
        {
            case GlobalEditCommand.SelectAll:
                QuickSwitchQueryBox.SelectAll();
                break;
            case GlobalEditCommand.Copy:
                QuickSwitchQueryBox.Copy();
                break;
            case GlobalEditCommand.Cut:
                QuickSwitchQueryBox.Cut();
                QuickSwitchQueryBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                break;
            case GlobalEditCommand.Backspace:
                ApplyDialogInputBackspace();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(command), command, "Unknown edit command.");
        }
    }

    internal void ConfirmSelectionFromDialogInput(IntPtr dialogWindow)
    {
        if (CanHandleDialogInput(dialogWindow))
        {
            _ = ActivateSelectedAsync();
        }
    }

    internal void ActivateResultShortcutFromDialogInput(int resultIndex, IntPtr dialogWindow)
    {
        if (!CanHandleDialogInput(dialogWindow) || resultIndex < 0 || resultIndex >= ViewModel.Results.Count)
        {
            return;
        }

        ViewModel.SelectedResult = ViewModel.Results[resultIndex];
        _ = ActivateSelectedAsync();
    }

    internal void CollapseFromDialogInput(IntPtr dialogWindow)
    {
        if (IsDialogTextInputCaptureActive && IsInputFromAttachedDialog(dialogWindow))
        {
            Collapse(restoreAnchorFocus: true);
        }
    }

    internal void Reposition()
    {
        if (!IsAttached || !VisibleWindowBounds.TryGet(_anchorWindow, out var rectangle))
        {
            return;
        }

        // Attachment does not mean system-wide topmost. The periodic attachment
        // timer calls Reposition even when geometry is cached, so update this
        // before the fast return.
        Topmost = ShouldKeepAttachedBarTopmostAfterDeactivation(
            IsAttached,
            GetForegroundWindow(),
            _anchorWindow,
            _windowHandle);

        var fallbackDpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var dpiScale = VisibleWindowBounds.GetWindowDpiScale(_anchorWindow, fallbackDpiScale);
        if (CanReusePosition(
                _hasCachedPosition,
                _repositionInvalidated,
                _lastAnchorBounds,
                rectangle,
                _lastAnchorDpiScale,
                dpiScale))
        {
            return;
        }

        var anchorLeft = rectangle.Left / dpiScale;
        var anchorWidth = (rectangle.Right - rectangle.Left) / dpiScale;
        var anchorBottom = rectangle.Bottom / dpiScale;
        var targetWidth = Math.Clamp(anchorWidth * 0.65, 420, 640);
        if (!AreClose(Width, targetWidth))
        {
            Width = targetWidth;
        }

        UpdateLayout();
        var panelWidth = ActualWidth > 0 ? ActualWidth : Width;
        var panelHeight = ActualHeight;
        var workArea = VisibleWindowBounds.TryGetMonitorWorkArea(_anchorWindow, out var physicalWorkArea)
            ? new Rect(
                physicalWorkArea.Left / dpiScale,
                physicalWorkArea.Top / dpiScale,
                (physicalWorkArea.Right - physicalWorkArea.Left) / dpiScale,
                (physicalWorkArea.Bottom - physicalWorkArea.Top) / dpiScale)
            : SystemParameters.WorkArea;
        var targetLeft = SearchPanel.CalculateCenteredLeft(
            anchorLeft,
            anchorWidth,
            panelWidth,
            workArea.Left,
            workArea.Right);
        var targetTop = anchorBottom + panelHeight - ChromeMargin <= workArea.Bottom
            ? anchorBottom - ChromeMargin
            : Math.Max(workArea.Top, rectangle.Top / dpiScale - panelHeight + ChromeMargin);
        if (!AreClose(Left, targetLeft))
        {
            Left = targetLeft;
        }

        if (!AreClose(Top, targetTop))
        {
            Top = targetTop;
        }

        _lastAnchorBounds = rectangle;
        _lastAnchorDpiScale = dpiScale;
        _hasCachedPosition = true;
        _repositionInvalidated = false;
    }

    internal void Expand()
    {
        if (!IsAttached)
        {
            return;
        }

        _followUpTypingArmed = false;
        Topmost = true;
        ViewModel.SetQuickSwitchBarCollapsed(false);
        InvalidateReposition();
        // Expanding steals focus from the dialog; don't collapse mid-activation.
        _suppressCollapseUntilTick = Environment.TickCount64 + 500;
        ShowActivated = true;
        Activate();
        QuickSwitchQueryBox.Focus();
        Reposition();
        InputCaptureStateChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void Collapse(bool restoreAnchorFocus = false)
    {
        if (!IsAttached)
        {
            return;
        }

        _followUpTypingArmed = false;
        ViewModel.SetQuickSwitchBarCollapsed(true);
        InvalidateReposition();
        ShowActivated = false;
        Reposition();
        InputCaptureStateChanged?.Invoke(this, EventArgs.Empty);
        if (restoreAnchorFocus && _anchorWindow != IntPtr.Zero)
        {
            _ = _activateAnchorWindow(_anchorWindow);
        }
    }

    /// <summary>
    /// Arms the collapsed bar after Ctrl+G changes the dialog folder. The next
    /// printable key starts Quick Switch even if the native dialog restores focus
    /// to its file-name edit.
    /// </summary>
    internal void PrepareForFollowUpTyping()
    {
        if (!IsAttached)
        {
            return;
        }

        ViewModel.ResetQuickSwitchQuery();
        ViewModel.SetQuickSwitchBarCollapsed(true);
        _followUpTypingArmed = true;
        Topmost = true;
        InputCaptureStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool CanHandleDialogInput(IntPtr dialogWindow) =>
        IsDialogSearchExpanded &&
        IsInputFromAttachedDialog(dialogWindow);

    private bool IsInputFromAttachedDialog(IntPtr dialogWindow) =>
        App.ShouldHandleDialogQuickSwitchInput(dialogWindow, _anchorWindow, _windowHandle);

    private void ApplyDialogInputBackspace()
    {
        var selectionStart = QuickSwitchQueryBox.SelectionStart;
        if (QuickSwitchQueryBox.SelectionLength > 0)
        {
            QuickSwitchQueryBox.SelectedText = string.Empty;
            QuickSwitchQueryBox.Select(selectionStart, 0);
        }
        else if (selectionStart > 0)
        {
            var deleteStart = selectionStart - 1;
            if (deleteStart > 0 &&
                char.IsLowSurrogate(QuickSwitchQueryBox.Text[deleteStart]) &&
                char.IsHighSurrogate(QuickSwitchQueryBox.Text[deleteStart - 1]))
            {
                deleteStart--;
            }

            QuickSwitchQueryBox.Text = QuickSwitchQueryBox.Text.Remove(
                deleteStart,
                selectionStart - deleteStart);
            QuickSwitchQueryBox.Select(deleteStart, 0);
        }

        QuickSwitchQueryBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
    }

    internal void ReportDialogJumpResult(DialogJumpResult result) => ViewModel.ReportDialogJumpResult(result);

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!App.IsShuttingDown)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (_contextMenuOpen || !IsAttached)
        {
            return;
        }

        var foreground = GetForegroundWindow();
        var keepTopmost = ShouldKeepAttachedBarTopmostAfterDeactivation(
            IsAttached,
            foreground,
            _anchorWindow,
            _windowHandle);
        if (Environment.TickCount64 < _suppressCollapseUntilTick && keepTopmost)
        {
            Topmost = true;
            if (IsDialogSearchExpanded)
            {
                Activate();
                QuickSwitchQueryBox.Focus();
            }

            return;
        }

        if (ShouldKeepDialogSearchExpandedAfterDeactivation(
                IsAttached,
                IsDialogSearchExpanded,
                foreground,
                _anchorWindow,
                _windowHandle))
        {
            Topmost = true;
            return;
        }

        Topmost = keepTopmost;
        if (!ViewModel.IsQuickSwitchBarCollapsed)
        {
            Collapse();
        }
    }

    private async void Results_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange == 0 ||
            e.OriginalSource is not ScrollViewer scrollViewer ||
            !SearchPanel.ShouldLoadMoreOnScroll(scrollViewer.VerticalOffset, scrollViewer.ScrollableHeight))
        {
            return;
        }

        await ViewModel.LoadMoreAsync();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Collapse(restoreAnchorFocus: true);
            return;
        }

        if (e.Key is Key.Up or Key.Down)
        {
            e.Handled = true;
            _resultSelectionNavigatedWithKeyboard = true;
            ViewModel.MoveSelection(e.Key == Key.Up ? -1 : 1);
            if (ViewModel.SelectedResult is not null)
            {
                QuickSwitchResults.ScrollIntoView(ViewModel.SelectedResult);
            }

            return;
        }

        if (e.Key == Key.Right &&
            _resultSelectionNavigatedWithKeyboard &&
            OpenSelectedResultContextMenu())
        {
            e.Handled = true;
            return;
        }

        _resultSelectionNavigatedWithKeyboard = false;

        var shortcut = SearchPanel.GetQuickSwitchShortcutIndex(
            e.Key,
            Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
        if (shortcut >= 0 && shortcut < ViewModel.Results.Count)
        {
            e.Handled = true;
            ViewModel.SelectedResult = ViewModel.Results[shortcut];
            _ = ActivateSelectedAsync();
            return;
        }

        if (e.Key == Key.Enter)
        {
            if (ImeInputGuard.ShouldDeferEnterToIme(Keyboard.FocusedElement as DependencyObject))
            {
                return;
            }

            e.Handled = true;
            _ = ActivateSelectedAsync();
        }
    }

    private void QueryBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel.IsQuickSwitchBarCollapsed)
        {
            Expand();
        }
    }

    private void QueryBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsAttached &&
            ViewModel.IsQuickSwitchBarCollapsed &&
            !string.IsNullOrEmpty(QuickSwitchQueryBox.Text))
        {
            Expand();
        }
    }

    private void Results_MouseDoubleClick(object sender, MouseButtonEventArgs e) => _ = ActivateSelectedAsync();

    private void Results_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left &&
            FindVisualAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is not null)
        {
            e.Handled = true;
            _ = ActivateSelectedAsync();
        }
    }

    private void Results_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        CloseResultContextMenu();

    private void Results_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
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

    private void Results_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
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

    private void ResultsContextMenu_Opened(object sender, RoutedEventArgs e) => _contextMenuOpen = true;

    private void ResultsContextMenu_Closed(object sender, RoutedEventArgs e) => _contextMenuOpen = false;

    private void OpenResultMenuItem_Click(object sender, RoutedEventArgs e)
    {
        CloseResultContextMenu();
        _ = ActivateSelectedAsync();
    }

    private void RevealResultMenuItem_Click(object sender, RoutedEventArgs e)
    {
        CloseResultContextMenu();
        _ = SearchPanel.RunInteractionAsync(ViewModel, viewModel => viewModel.RevealSelectedAsync());
    }

    private void CopyPathMenuItem_Click(object sender, RoutedEventArgs e)
    {
        CloseResultContextMenu();
        _ = SearchPanel.RunInteractionAsync(
            ViewModel,
            viewModel =>
            {
                viewModel.CopySelectedPath();
                return Task.CompletedTask;
            });
    }

    private bool OpenSelectedResultContextMenu(
        PlacementMode placement = PlacementMode.Right,
        UIElement? placementTarget = null)
    {
        var selected = ViewModel.SelectedResult;
        var contextMenu = QuickSwitchResults.ContextMenu;
        if (selected is null || contextMenu is null)
        {
            return false;
        }

        QuickSwitchResults.ScrollIntoView(selected);
        QuickSwitchResults.UpdateLayout();
        var item = QuickSwitchResults.ItemContainerGenerator.ContainerFromItem(selected) as ListBoxItem;
        item?.Focus();
        contextMenu.PlacementTarget = placementTarget ?? (UIElement?)item ?? QuickSwitchResults;
        contextMenu.Placement = placement;
        contextMenu.IsOpen = true;
        return true;
    }

    internal void CloseResultContextMenu()
    {
        if (QuickSwitchResults.ContextMenu is { IsOpen: true } contextMenu)
        {
            contextMenu.IsOpen = false;
        }

        _contextMenuOpen = false;
    }

    private static T? FindVisualAncestor<T>(DependencyObject? source)
        where T : DependencyObject
    {
        var current = source;
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

    internal async Task ActivateSelectedAsync()
    {
        var activated = await SearchPanel.RunInteractionAsync(
            ViewModel,
            viewModel => viewModel.ActivateSelectedAsync(),
            fallback: false);
        if (!activated)
        {
            return;
        }

        ViewModel.ResetQuickSwitchQuery();
        Collapse(restoreAnchorFocus: true);
    }

    protected override void OnClosed(EventArgs e)
    {
        _windowHandle = IntPtr.Zero;
        InputCaptureStateChanged?.Invoke(this, EventArgs.Empty);
        base.OnClosed(e);
    }

    private void InvalidateReposition() => _repositionInvalidated = true;

    internal static bool CanReusePosition(
        bool hasCachedPosition,
        bool repositionInvalidated,
        NativeRectangle previousBounds,
        NativeRectangle currentBounds,
        double previousDpiScale,
        double currentDpiScale) =>
        hasCachedPosition
        && !repositionInvalidated
        && previousBounds.Left == currentBounds.Left
        && previousBounds.Top == currentBounds.Top
        && previousBounds.Right == currentBounds.Right
        && previousBounds.Bottom == currentBounds.Bottom
        && AreClose(previousDpiScale, currentDpiScale);

    internal static bool ShouldKeepDialogSearchExpandedAfterDeactivation(
        bool isAttached,
        bool isExpanded,
        IntPtr foregroundWindow,
        IntPtr anchorWindow,
        IntPtr quickSwitchWindow = default) =>
        isAttached
        && isExpanded
        && foregroundWindow != IntPtr.Zero
        && (foregroundWindow == quickSwitchWindow
            || GlobalTextInputService.IsConfiguredDialogInputWindow(foregroundWindow, anchorWindow));

    internal static bool ShouldKeepAttachedBarTopmostAfterDeactivation(
        bool isAttached,
        IntPtr foregroundWindow,
        IntPtr anchorWindow,
        IntPtr quickSwitchWindow = default) =>
        isAttached
        && foregroundWindow != IntPtr.Zero
        && (foregroundWindow == quickSwitchWindow
            || GlobalTextInputService.IsConfiguredDialogInputWindow(foregroundWindow, anchorWindow));

    private static bool AreClose(double left, double right) => Math.Abs(left - right) < 0.1;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _windowHandle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);

}

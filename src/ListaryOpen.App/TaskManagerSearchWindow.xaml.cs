using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ListaryOpen.App.ViewModels;
using ListaryOpen.Infrastructure.Windows;

namespace ListaryOpen.App;

public partial class TaskManagerSearchWindow : Window
{
    private readonly ITaskManagerAutomationService _automationService;
    private CancellationTokenSource? _snapshotCancellation;
    private IntPtr _taskManagerWindow;
    private int _activationVersion;
    /// <summary>Ignore deactivation dismissals while the overlay is still being shown/focused.</summary>
    private long _suppressDeactivateDismissUntilTick;

    public TaskManagerSearchWindow()
        : this(new TaskManagerAutomationService())
    {
    }

    internal TaskManagerSearchWindow(ITaskManagerAutomationService automationService)
    {
        _automationService = automationService ?? throw new ArgumentNullException(nameof(automationService));
        InitializeComponent();
        DataContext = new TaskManagerSearchViewModel();
        LocalizationManager.LanguageChanged += LocalizationManager_LanguageChanged;
        AddHandler(
            Keyboard.PreviewKeyDownEvent,
            new KeyEventHandler(HandlePreviewKeyDown),
            handledEventsToo: true);
    }

    internal event EventHandler? SearchSessionEnded;

    internal bool IsTaskManagerSearchActive => _taskManagerWindow != IntPtr.Zero && IsVisible;

    internal IntPtr TaskManagerWindow => _taskManagerWindow;

    internal async void ActivateSearch(string initialQuery, IntPtr taskManagerWindow)
    {
        if (taskManagerWindow == IntPtr.Zero || string.IsNullOrWhiteSpace(initialQuery))
        {
            return;
        }

        _snapshotCancellation?.Cancel();
        _snapshotCancellation?.Dispose();
        _snapshotCancellation = new CancellationTokenSource();
        var version = Interlocked.Increment(ref _activationVersion);
        _taskManagerWindow = taskManagerWindow;
        ViewModel.QueryText = initialQuery;
        // Show/Activate briefly fights Task Manager for focus; don't dismiss mid-activation.
        _suppressDeactivateDismissUntilTick = Environment.TickCount64 + 500;
        ShowActivated = true;
        Show();
        PositionAtWindowBottomRight(taskManagerWindow);
        ApplyBorderlessWindowChrome();
        Activate();
        FocusQueryAtEnd();
        // Keep Topmost so the overlay stays above elevated Task Manager chrome.
        Topmost = true;

        try
        {
            var items = await _automationService.GetItemsAsync(taskManagerWindow, _snapshotCancellation.Token);
            if (version == _activationVersion && taskManagerWindow == _taskManagerWindow)
            {
                ViewModel.SetItems(items);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Trace.TraceWarning("Could not read Task Manager rows: {0}", exception.Message);
            if (version == _activationVersion && taskManagerWindow == _taskManagerWindow)
            {
                ViewModel.SetItems(Array.Empty<TaskManagerItem>());
            }
        }
    }

    internal void AppendText(string text)
    {
        if (IsTaskManagerSearchActive && !string.IsNullOrEmpty(text))
        {
            if (TaskQueryBox.SelectionLength > 0)
            {
                var start = TaskQueryBox.SelectionStart;
                TaskQueryBox.SelectedText = text;
                TaskQueryBox.Select(start + text.Length, 0);
                TaskQueryBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            }
            else
            {
                ViewModel.QueryText += text;
            }

            FocusQueryAtEnd();
        }
    }

    internal void ExecuteEditCommand(GlobalEditCommand command)
    {
        if (!IsTaskManagerSearchActive)
        {
            return;
        }

        switch (command)
        {
            case GlobalEditCommand.SelectAll: TaskQueryBox.SelectAll(); break;
            case GlobalEditCommand.Copy: TaskQueryBox.Copy(); break;
            case GlobalEditCommand.Cut:
                TaskQueryBox.Cut();
                TaskQueryBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                break;
            case GlobalEditCommand.Backspace: ApplyBackspace(); break;
        }
    }

    internal void MoveSelection(int delta)
    {
        ViewModel.MoveSelection(delta);
        if (ViewModel.SelectedItem is not null)
        {
            TaskResults.ScrollIntoView(ViewModel.SelectedItem);
        }
    }

    private void TaskResults_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel.SelectedItem is null)
        {
            _automationService.CancelPending();
        }
    }

    internal void ActivateResultShortcut(int index)
    {
        if (index < 0 || index >= ViewModel.Results.Count)
        {
            return;
        }

        ViewModel.SelectedItem = ViewModel.Results[index];
        ConfirmSelection();
    }

    internal bool ContainsScreenPoint(int x, int y)
    {
        var handle = new WindowInteropHelper(this).Handle;
        return VisibleWindowBounds.TryGet(handle, out var bounds) &&
            x >= bounds.Left && x < bounds.Right && y >= bounds.Top && y < bounds.Bottom;
    }

    internal void DismissSearch() => EndSession(cancelPending: true);

    private TaskManagerSearchViewModel ViewModel => (TaskManagerSearchViewModel)DataContext;

    private void ApplyBackspace()
    {
        var start = TaskQueryBox.SelectionStart;
        if (TaskQueryBox.SelectionLength > 0)
        {
            TaskQueryBox.SelectedText = string.Empty;
        }
        else if (start > 0)
        {
            var deleteStart = start - 1;
            var deleteLength = 1;
            if (deleteStart > 0 &&
                char.IsLowSurrogate(TaskQueryBox.Text[deleteStart]) &&
                char.IsHighSurrogate(TaskQueryBox.Text[deleteStart - 1]))
            {
                deleteStart--;
                deleteLength = 2;
            }

            TaskQueryBox.Text = TaskQueryBox.Text.Remove(deleteStart, deleteLength);
            TaskQueryBox.Select(deleteStart, 0);
        }

        TaskQueryBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
    }

    private void FocusQueryAtEnd()
    {
        TaskQueryBox.Focus();
        TaskQueryBox.Select(TaskQueryBox.Text.Length, 0);
    }

    internal void ConfirmSelection()
    {
        if (_taskManagerWindow == IntPtr.Zero || ViewModel.SelectedItem is not { } item)
        {
            return;
        }

        var taskManagerWindow = _taskManagerWindow;
        EndSession(cancelPending: false);
        _automationService.QueueSelectItem(taskManagerWindow, item, activate: true);
    }

    private void EndSession(bool cancelPending)
    {
        if (_taskManagerWindow == IntPtr.Zero && !IsVisible)
        {
            return;
        }

        _snapshotCancellation?.Cancel();
        _snapshotCancellation?.Dispose();
        _snapshotCancellation = null;
        Interlocked.Increment(ref _activationVersion);
        _taskManagerWindow = IntPtr.Zero;
        ViewModel.CancelPendingRefresh();
        if (cancelPending)
        {
            _automationService.CancelPending();
        }

        Hide();
        SearchSessionEnded?.Invoke(this, EventArgs.Empty);
    }

    private void PositionAtWindowBottomRight(IntPtr anchorWindow)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var dpiX = dpi.DpiScaleX;
        var dpiY = dpi.DpiScaleY;
        var workArea = GetAnchorWorkArea(anchorWindow, dpiX, dpiY);
        if (!VisibleWindowBounds.TryGet(anchorWindow, out var rectangle))
        {
            Left = workArea.Right - Width - 12;
            Top = workArea.Bottom - Height - 12;
            return;
        }

        Left = Math.Clamp(rectangle.Right / dpiX - Width - 12, workArea.Left, Math.Max(workArea.Left, workArea.Right - Width));
        Top = Math.Clamp(rectangle.Bottom / dpiY - Height - 12, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - Height));
    }

    private static Rect GetAnchorWorkArea(IntPtr anchorWindow, double dpiScaleX, double dpiScaleY) =>
        VisibleWindowBounds.TryGetMonitorWorkArea(anchorWindow, out var physicalWorkArea)
            ? new Rect(
                physicalWorkArea.Left / dpiScaleX,
                physicalWorkArea.Top / dpiScaleY,
                (physicalWorkArea.Right - physicalWorkArea.Left) / dpiScaleX,
                (physicalWorkArea.Bottom - physicalWorkArea.Top) / dpiScaleY)
            : SystemParameters.WorkArea;

    private void HandlePreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (GetPreviewKeyAction(e.Key))
        {
            case TaskManagerSearchKeyAction.Dismiss:
                e.Handled = true;
                DismissSearch();
                return;
            case TaskManagerSearchKeyAction.MoveUp:
            case TaskManagerSearchKeyAction.MoveDown:
                e.Handled = true;
                MoveSelection(e.Key == Key.Up ? -1 : 1);
                FocusQueryAtEnd();
                return;
            case TaskManagerSearchKeyAction.Confirm:
                if (ImeInputGuard.ShouldDeferEnterToIme(Keyboard.FocusedElement as DependencyObject))
                {
                    return;
                }

                e.Handled = true;
                ConfirmSelection();
                return;
        }

        var shortcut = SearchPanel.GetQuickSwitchShortcutIndex(
            e.Key,
            Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
        if (shortcut >= 0)
        {
            e.Handled = true;
            ActivateResultShortcut(shortcut);
        }
    }

    internal static TaskManagerSearchKeyAction GetPreviewKeyAction(Key key) => key switch
    {
        Key.Escape => TaskManagerSearchKeyAction.Dismiss,
        Key.Up => TaskManagerSearchKeyAction.MoveUp,
        Key.Down => TaskManagerSearchKeyAction.MoveDown,
        Key.Enter => TaskManagerSearchKeyAction.Confirm,
        _ => TaskManagerSearchKeyAction.None
    };

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (Environment.TickCount64 < _suppressDeactivateDismissUntilTick)
            {
                // Re-assert focus while the overlay is still activating.
                if (IsVisible && _taskManagerWindow != IntPtr.Zero)
                {
                    Activate();
                    FocusQueryAtEnd();
                }

                return;
            }

            var overlayWindow = new WindowInteropHelper(this).Handle;
            if (ShouldDismissAfterDeactivation(
                    IsVisible,
                    IsActive,
                    GetForegroundWindow(),
                    _taskManagerWindow,
                    overlayWindow))
            {
                DismissSearch();
            }
        }));
    }

    internal static bool ShouldDismissAfterDeactivation(
        bool isVisible,
        bool isActive,
        IntPtr foregroundWindow,
        IntPtr taskManagerWindow,
        IntPtr overlayWindow) =>
        isVisible &&
        !isActive &&
        foregroundWindow != IntPtr.Zero &&
        foregroundWindow != taskManagerWindow &&
        foregroundWindow != overlayWindow;

    private void TaskResults_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        DependencyObject? current = e.OriginalSource as DependencyObject;
        while (current is not null && current is not ListBoxItem)
        {
            current = VisualTreeHelper.GetParent(current);
        }

        if (current is ListBoxItem)
        {
            ConfirmSelection();
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!App.IsShuttingDown)
        {
            e.Cancel = true;
            DismissSearch();
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        LocalizationManager.LanguageChanged -= LocalizationManager_LanguageChanged;
        ViewModel.Dispose();
        _automationService.Dispose();
        base.OnClosed(e);
    }

    private void LocalizationManager_LanguageChanged(object? sender, EventArgs e) =>
        ViewModel.RefreshLocalization();

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyBorderlessWindowChrome();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (IsVisible)
        {
            ApplyBorderlessWindowChrome();
        }
    }

    private void TaskManagerSearchWindow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        _ = BorderlessWindowInteraction.TryBeginDrag(this, e);

    private void ApplyBorderlessWindowChrome() =>
        NativeWindowCorner.ApplyRounded(this, radiusDip: 9);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}

internal enum TaskManagerSearchKeyAction
{
    None,
    Dismiss,
    MoveUp,
    MoveDown,
    Confirm
}

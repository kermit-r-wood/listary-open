using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ListaryOpen.App.ViewModels;
using ListaryOpen.Core.Indexing;
using ListaryOpen.Core.Search;
using ListaryOpen.Infrastructure.Windows;

namespace ListaryOpen.App;

public partial class SearchPanel : Window
{
    public SearchPanel()
        : this(new SearchPanelViewModel(new EmptySearchIndex()))
    {
    }

    public SearchPanel(ISearchIndex index)
        : this(new SearchPanelViewModel(index))
    {
    }

    public SearchPanel(SearchPanelViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();
        DataContext = viewModel;
    }

    public void ActivateSearch()
    {
        _ = ViewModel.ActivateFilesAndFoldersSearchAsync();
        ShowAndFocusQuery();
    }

    public void ActivateFolderSearch(string? trackedFolder)
    {
        _ = ViewModel.ActivateFolderSearchAsync(trackedFolder);
        ShowAndFocusQuery();
    }

    public void ActivateFolderSearch<TCandidates>(TCandidates candidates)
        where TCandidates : IReadOnlyList<QuickSwitchFolderCandidate>
    {
        _ = ViewModel.ActivateFolderSearchAsync(candidates);
        ShowAndFocusQuery();
    }

    private SearchPanelViewModel ViewModel => (SearchPanelViewModel)DataContext;

    private void ShowAndFocusQuery()
    {
        Show();
        Activate();
        QueryBox.Focus();
    }

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

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        if (e.Handled)
        {
            return;
        }

        var isControlPressed = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (e.Key == Key.Enter && isControlPressed)
        {
            e.Handled = true;
            _ = RunInteractionAsync(ViewModel, viewModel => viewModel.RevealSelectedAsync());
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _ = RunInteractionAsync(ViewModel, viewModel => viewModel.ActivateSelectedAsync());
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
        _ = RunInteractionAsync(ViewModel, viewModel => viewModel.ActivateSelectedAsync());
    }

    internal static bool ShouldCopySelectedResultPath(bool focusIsQueryBox, bool focusIsTextInput)
    {
        return !focusIsQueryBox && !focusIsTextInput;
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

        public Task<IReadOnlyList<SearchResult>> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<SearchResult>>(Array.Empty<SearchResult>());
        }
    }
}

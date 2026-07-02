using System.ComponentModel;
using System.Windows;
using ListaryOpen.App.ViewModels;
using ListaryOpen.Core.Indexing;
using ListaryOpen.Core.Search;

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
        InitializeComponent();
        DataContext = viewModel;
    }

    public void ActivateSearch()
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

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using ListaryOpen.Core.Search;

namespace ListaryOpen.App.ViewModels;

public sealed class SearchPanelViewModel : INotifyPropertyChanged
{
    private readonly ISearchIndex _index;
    private int _refreshVersion;
    private string _queryText = string.Empty;

    public SearchPanelViewModel(ISearchIndex index)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
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

    private async Task RefreshAsync()
    {
        var version = Interlocked.Increment(ref _refreshVersion);
        var queryText = _queryText;

        Results.Clear();
        if (string.IsNullOrWhiteSpace(queryText))
        {
            return;
        }

        IReadOnlyList<SearchResult> results;
        try
        {
            results = await _index.SearchAsync(
                new SearchQuery(queryText, SearchMode.FilesAndFolders),
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            Trace.TraceError(exception.ToString());

            if (version == _refreshVersion)
            {
                Results.Clear();
            }

            return;
        }

        if (version != _refreshVersion)
        {
            return;
        }

        Results.Clear();
        foreach (var result in results)
        {
            Results.Add(result);
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

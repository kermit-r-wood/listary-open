using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using ListaryOpen.Core.Search;

namespace ListaryOpen.App.ViewModels;

public sealed class SearchResultCollection : ObservableCollection<SearchResult>
{
    public void ReplaceAll(IEnumerable<SearchResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        Items.Clear();
        foreach (var result in results)
        {
            Items.Add(result);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

using System.Collections;
using ListaryOpen.Core.Search;

namespace ListaryOpen.App.ViewModels;

/// <summary>
/// An immutable result frame. Replacing the frame through Add/ReplaceAll keeps an
/// already-bound WPF ItemsControl on its previous, internally consistent frame
/// until the Results property notification installs the complete next frame.
/// </summary>
public sealed class SearchResultSnapshot : IReadOnlyList<SearchResult>
{
    private readonly SearchPanelViewModel _owner;
    private readonly IReadOnlyList<SearchResult> _items;

    internal SearchResultSnapshot(SearchPanelViewModel owner, IEnumerable<SearchResult> items)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _items = Array.AsReadOnly(items?.ToArray() ?? throw new ArgumentNullException(nameof(items)));
    }

    public int Count => _items.Count;

    public SearchResult this[int index] => _items[index];

    public void Add(SearchResult result) => _owner.AddResultForTesting(result);

    public void ReplaceAll(IEnumerable<SearchResult> results) => _owner.SetResultsForTesting(results);

    public IEnumerator<SearchResult> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

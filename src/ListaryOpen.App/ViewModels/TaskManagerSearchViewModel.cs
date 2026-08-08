using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ListaryOpen.Infrastructure.Windows;

namespace ListaryOpen.App.ViewModels;

public sealed class TaskManagerSearchViewModel : INotifyPropertyChanged, IDisposable
{
    private static readonly TimeSpan DefaultSearchDelay = TimeSpan.FromMilliseconds(80);
    private readonly TimeSpan _searchDelay;
    private CancellationTokenSource? _refreshCancellation;
    private Task _pendingRefresh = Task.CompletedTask;
    private IReadOnlyList<TaskManagerItem> _allItems = Array.Empty<TaskManagerItem>();
    private string _queryText = string.Empty;
    private TaskManagerItem? _selectedItem;
    private string _statusText = "Reading visible Task Manager items…";

    public TaskManagerSearchViewModel(TimeSpan? searchDelay = null)
    {
        _searchDelay = searchDelay ?? DefaultSearchDelay;
        if (_searchDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(searchDelay));
        }
    }

    public ObservableCollection<TaskManagerItem> Results { get; } = new();

    public string QueryText
    {
        get => _queryText;
        set
        {
            if (string.Equals(_queryText, value, StringComparison.Ordinal))
            {
                return;
            }

            _queryText = value ?? string.Empty;
            OnPropertyChanged();
            ScheduleRefresh();
        }
    }

    public TaskManagerItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (Equals(_selectedItem, value))
            {
                return;
            }

            _selectedItem = value;
            OnPropertyChanged();
        }
    }

    public string StatusText
    {
        get
        {
            if (global::ListaryOpen.App.LocalizationManager.EffectiveLanguage ==
                ListaryOpen.Core.Settings.AppLanguage.SimplifiedChinese)
            {
                return string.IsNullOrWhiteSpace(QueryText)
                    ? $"{_allItems.Count:N0} 个任务管理器项目可用"
                    : $"{Results.Count:N0} 个匹配的任务管理器项目";
            }

            return global::ListaryOpen.App.LocalizationManager.Translate(_statusText);
        }
        private set
        {
            if (string.Equals(_statusText, value, StringComparison.Ordinal))
            {
                return;
            }

            _statusText = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetItems(IReadOnlyList<TaskManagerItem> items)
    {
        _allItems = items ?? Array.Empty<TaskManagerItem>();
        Refresh();
    }

    public void CancelPendingRefresh()
    {
        _refreshCancellation?.Cancel();
        _refreshCancellation?.Dispose();
        _refreshCancellation = null;
    }

    public void Dispose() => CancelPendingRefresh();

    internal Task WaitForPendingRefreshAsync() => _pendingRefresh;

    internal void RefreshLocalization() => OnPropertyChanged(nameof(StatusText));

    public void MoveSelection(int delta)
    {
        if (delta is not (-1 or 1) || Results.Count == 0)
        {
            return;
        }

        var current = SelectedItem is null ? -1 : Results.IndexOf(SelectedItem);
        SelectedItem = Results[Math.Clamp(current + delta, 0, Results.Count - 1)];
    }

    internal static double MatchScore(TaskManagerItem item, string query)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (string.IsNullOrWhiteSpace(query))
        {
            return 0;
        }

        var normalized = query.Trim();
        var hasCanonicalAliases = !string.IsNullOrWhiteSpace(item.SearchText);
        var aliases = hasCanonicalAliases
            ? item.SearchText.Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [item.Name];
        var aliasScore = aliases
            .Select(alias => MatchAliasScore(alias, normalized))
            .DefaultIfEmpty(double.NegativeInfinity)
            .Max();
        if (!double.IsNegativeInfinity(aliasScore))
        {
            return aliasScore;
        }

        // Canonicalized rows deliberately do not search their display title/details:
        // those strings can be arbitrary document/window titles owned by pwsh,
        // browsers, terminals, and other unrelated processes.
        var detailIndex = hasCanonicalAliases
            ? -1
            : item.Details.IndexOf(normalized, StringComparison.OrdinalIgnoreCase);
        return detailIndex >= 0 ? 300 - detailIndex : double.NegativeInfinity;
    }

    private static double MatchAliasScore(string searchableName, string normalized)
    {
        if (string.Equals(searchableName, normalized, StringComparison.OrdinalIgnoreCase)) return 1000;
        if (searchableName.StartsWith(normalized, StringComparison.OrdinalIgnoreCase)) return 800;
        var nameIndex = searchableName.IndexOf(normalized, StringComparison.OrdinalIgnoreCase);
        if (nameIndex >= 0) return 600 - nameIndex;

        var candidate = searchableName.AsSpan();
        var querySpan = normalized.AsSpan();
        var queryIndex = 0;
        for (var index = 0; index < candidate.Length && queryIndex < querySpan.Length; index++)
        {
            if (char.ToUpperInvariant(candidate[index]) == char.ToUpperInvariant(querySpan[queryIndex]))
            {
                queryIndex++;
            }
        }

        return queryIndex == querySpan.Length ? 100 - candidate.Length : double.NegativeInfinity;
    }

    private void Refresh()
    {
        var previousId = SelectedItem?.Id;
        var query = QueryText.Trim();
        var matches = string.IsNullOrEmpty(query)
            ? Array.Empty<TaskManagerItem>()
            : _allItems
                .Select(item => (Item: item, Score: MatchScore(item, query)))
                .Where(candidate => !double.IsNegativeInfinity(candidate.Score))
                .OrderByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.Item.Name, StringComparer.OrdinalIgnoreCase)
                .Take(100)
                .Select(candidate => candidate.Item)
                .ToArray();

        ReplaceResults(matches);

        SelectedItem = Results.FirstOrDefault(item => string.Equals(item.Id, previousId, StringComparison.Ordinal))
            ?? Results.FirstOrDefault();
        StatusText = string.IsNullOrEmpty(query)
            ? $"{_allItems.Count:N0} Task Manager items available"
            : $"{Results.Count:N0} matching Task Manager items";
    }

    private void ScheduleRefresh()
    {
        CancelPendingRefresh();
        if (_searchDelay == TimeSpan.Zero)
        {
            Refresh();
            _pendingRefresh = Task.CompletedTask;
            return;
        }

        _refreshCancellation = new CancellationTokenSource();
        _pendingRefresh = RefreshAfterDelayAsync(_refreshCancellation.Token);
    }

    private async Task RefreshAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_searchDelay, cancellationToken);
            Refresh();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void ReplaceResults(IReadOnlyList<TaskManagerItem> matches)
    {
        for (var index = 0; index < matches.Count; index++)
        {
            var expected = matches[index];
            if (index < Results.Count && string.Equals(Results[index].Id, expected.Id, StringComparison.Ordinal))
            {
                if (!Equals(Results[index], expected)) Results[index] = expected;
                continue;
            }

            var existingIndex = -1;
            for (var candidateIndex = index + 1; candidateIndex < Results.Count; candidateIndex++)
            {
                if (string.Equals(Results[candidateIndex].Id, expected.Id, StringComparison.Ordinal))
                {
                    existingIndex = candidateIndex;
                    break;
                }
            }

            if (existingIndex >= 0)
            {
                Results.Move(existingIndex, index);
                if (!Equals(Results[index], expected)) Results[index] = expected;
            }
            else
            {
                Results.Insert(index, expected);
            }
        }

        while (Results.Count > matches.Count)
        {
            Results.RemoveAt(Results.Count - 1);
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

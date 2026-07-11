using ListaryOpen.Core.Indexing;
using ListaryOpen.Core.Usage;

namespace ListaryOpen.Core.Search;

public static class ResultRanker
{
    public static IReadOnlyList<SearchResult> Rank(
        SearchQuery query,
        IEnumerable<FileRecord> records,
        IEnumerable<UsageRecord> usageRecords,
        IEnumerable<string> pinnedFolders)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(usageRecords);
        ArgumentNullException.ThrowIfNull(pinnedFolders);

        var usage = usageRecords
            .GroupBy(record => record.PathKey, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(record => record.OpenCount)
                    .ThenByDescending(record => record.LastUsedAt)
                    .First(),
                StringComparer.Ordinal);

        var pinned = pinnedFolders
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(CreatePathKey)
            .ToHashSet(StringComparer.Ordinal);

        var now = DateTimeOffset.UtcNow;

        return records
            .Where(record => (query.EffectiveMode == SearchMode.FilesAndFolders || record.IsDirectory)
                && (!query.Parsed.FileOnly || !record.IsDirectory))
            .Select(record => RankRecord(query, record, usage, pinned, now))
            .Where(candidate => candidate.TextKey.IsMatch)
            .OrderBy(candidate => candidate.TextKey.Tier)
            .ThenByDescending(candidate => candidate.TextKey.Quality)
            .ThenBy(candidate => candidate.TextKey.LengthDifference)
            .ThenByDescending(candidate => candidate.IsPinned)
            .ThenByDescending(candidate => candidate.OpenCount)
            .ThenByDescending(candidate => candidate.Recency)
            .ThenBy(candidate => candidate.Result.Record.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.Result.Record.Name, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Result.Record.FullPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.Result.Record.FullPath, StringComparer.Ordinal)
            .Take(query.Limit)
            .Select(candidate => candidate.Result)
            .ToArray();
    }

    private static RankedCandidate RankRecord(
        SearchQuery query,
        FileRecord record,
        IReadOnlyDictionary<string, UsageRecord> usage,
        IReadOnlySet<string> pinned,
        DateTimeOffset now)
    {
        var queryText = query.NormalizedText;
        if (string.IsNullOrWhiteSpace(queryText))
        {
            return new RankedCandidate(
                new SearchResult(record, 1, "filter"),
                new TextMatchKey(0, 1, 0),
                pinned.Contains(record.PathKey),
                usage.TryGetValue(record.PathKey, out var filterUsage) ? filterUsage.OpenCount : 0,
                usage.TryGetValue(record.PathKey, out filterUsage) ? RecencyBoost(filterUsage.LastUsedAt, now) : 0);
        }

        var (textKey, reason) = CreateTextMatchKey(queryText, record);
        usage.TryGetValue(record.PathKey, out var used);
        var displayScore = textKey.IsMatch ? 10_000 - textKey.Tier * 1_000 + textKey.Quality : 0;
        return new RankedCandidate(
            new SearchResult(record, displayScore, reason),
            textKey,
            record.IsDirectory && pinned.Contains(record.PathKey),
            used?.OpenCount ?? 0,
            used is null ? 0 : RecencyBoost(used.LastUsedAt, now));
    }

    private static (TextMatchKey Key, string Reason) CreateTextMatchKey(string queryText, FileRecord record)
    {
        var query = queryText.Trim().ToLowerInvariant();
        var name = record.Name.Trim().ToLowerInvariant();
        if (name == query)
        {
            return (new TextMatchKey(0, query.Length, 0), "exact-name");
        }

        if (name.StartsWith(query, StringComparison.Ordinal))
        {
            return (new TextMatchKey(1, query.Length, name.Length - query.Length), "name-prefix");
        }

        if (name.Contains(query, StringComparison.Ordinal))
        {
            return (new TextMatchKey(2, query.Length, name.Length - query.Length), "name-substring");
        }

        var nameScore = FuzzyMatcher.Score(query, name);
        if (nameScore > 0)
        {
            return (new TextMatchKey(3, nameScore, Math.Abs(name.Length - query.Length)), "name-fuzzy");
        }

        var pinyinScore = PinyinMatcher.Score(query, record.Name);
        if (pinyinScore > 0)
        {
            return (new TextMatchKey(4, pinyinScore, Math.Abs(name.Length - query.Length)), "pinyin");
        }

        var pathScore = FuzzyMatcher.Score(query, record.ParentPath);
        return pathScore > 0
            ? (new TextMatchKey(5, pathScore, Math.Abs(record.ParentPath.Length - query.Length)), "path")
            : (TextMatchKey.NoMatch, "none");
    }

    private static double RecencyBoost(DateTimeOffset lastUsedAt, DateTimeOffset now)
    {
        var daysSinceUse = (now - lastUsedAt).TotalDays;
        return Math.Clamp(20 - daysSinceUse, 0, 20);
    }

    private static string CreatePathKey(string path)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
        return normalized.ToUpperInvariant();
    }

    private sealed record RankedCandidate(
        SearchResult Result,
        TextMatchKey TextKey,
        bool IsPinned,
        int OpenCount,
        double Recency);
}

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
            .Where(record => query.Mode == SearchMode.FilesAndFolders || record.IsDirectory)
            .Select(record => ScoreRecord(query, record, usage, pinned, now))
            .Where(result => result.Score > 0)
            .OrderByDescending(result => result.Score)
            .ThenBy(result => result.Record.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(result => result.Record.Name, StringComparer.Ordinal)
            .ThenBy(result => result.Record.FullPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(result => result.Record.FullPath, StringComparer.Ordinal)
            .Take(query.Limit)
            .ToArray();
    }

    private static SearchResult ScoreRecord(
        SearchQuery query,
        FileRecord record,
        IReadOnlyDictionary<string, UsageRecord> usage,
        IReadOnlySet<string> pinned,
        DateTimeOffset now)
    {
        var queryText = query.NormalizedText;
        var nameScore = FuzzyMatcher.Score(queryText, record.Name);
        var pathScore = FuzzyMatcher.Score(queryText, record.FullPath) * 0.6;
        var pinyinScore = PinyinMatcher.Score(queryText, record.Name) * 0.9;

        var score = nameScore;
        var reason = "name";

        if (pathScore > score)
        {
            score = pathScore;
            reason = "path";
        }

        if (pinyinScore > score)
        {
            score = pinyinScore;
            reason = "pinyin";
        }

        if (score <= 0)
        {
            return new SearchResult(record, 0, reason);
        }

        if (usage.TryGetValue(record.PathKey, out var used))
        {
            score += Math.Min(50, used.OpenCount * 5);
            score += RecencyBoost(used.LastUsedAt, now);
            reason = "usage";
        }

        if (record.IsDirectory && pinned.Contains(record.PathKey))
        {
            score += 40;
            reason = "pinned";
        }

        return new SearchResult(record, score, reason);
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
}

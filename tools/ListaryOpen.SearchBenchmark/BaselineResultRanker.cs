using ListaryOpen.Core.Indexing;
using ListaryOpen.Core.Search;
using ListaryOpen.Core.Usage;

namespace ListaryOpen.SearchBenchmark;

// Semantically equivalent to ResultRanker before its allocation/single-pass optimization.
internal static class BaselineResultRanker
{
    public static IReadOnlyList<SearchResult> Rank(
        SearchQuery query,
        IEnumerable<FileRecord> records,
        IEnumerable<UsageRecord> usageRecords,
        IEnumerable<string> pinnedFolders)
    {
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
        var preferredRoot = query.PreferredRoot;

        return records
            .Where(record => (query.EffectiveMode == SearchMode.FilesAndFolders || record.IsDirectory)
                && (!query.Parsed.FileOnly || !record.IsDirectory))
            .Select(record => RankRecord(query, record, usage, pinned, now))
            .Where(candidate => candidate.TextKey.IsMatch)
            .OrderBy(candidate => GetPreferredRootPriority(
                candidate.Result.Record,
                preferredRoot))
            .ThenBy(candidate => candidate.TextKey.Tier)
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
                new MatchKey(0, 1, 0),
                pinned.Contains(record.PathKey),
                usage.TryGetValue(record.PathKey, out var filterUsage) ? filterUsage.OpenCount : 0,
                usage.TryGetValue(record.PathKey, out filterUsage) ? RecencyBoost(filterUsage.LastUsedAt, now) : 0);
        }

        var (textKey, reason) = CreateTextMatchKey(query, record);
        usage.TryGetValue(record.PathKey, out var used);
        var displayScore = textKey.IsMatch ? 10_000 - textKey.Tier * 1_000 + textKey.Quality : 0;
        return new RankedCandidate(
            new SearchResult(record, displayScore, reason),
            textKey,
            record.IsDirectory && pinned.Contains(record.PathKey),
            used?.OpenCount ?? 0,
            used is null ? 0 : RecencyBoost(used.LastUsedAt, now));
    }

    private static (MatchKey Key, string Reason) CreateTextMatchKey(SearchQuery query, FileRecord record)
    {
        var matches = query.Parsed.Phrases
            .Select(phrase => CreateTermMatchKey(phrase, record, allowFuzzy: false))
            .Concat(query.Parsed.Terms.Select(term => CreateTermMatchKey(term, record, allowFuzzy: true)))
            .ToArray();
        if (matches.Length == 0 || matches.Any(match => !match.Key.IsMatch))
        {
            return (MatchKey.NoMatch, "none");
        }

        var worstMatch = matches
            .OrderByDescending(match => match.Key.Tier)
            .ThenBy(match => match.Key.Quality)
            .First();
        return (
            new MatchKey(
                matches.Max(match => match.Key.Tier),
                matches.Sum(match => match.Key.Quality),
                matches.Sum(match => match.Key.LengthDifference)),
            worstMatch.Reason);
    }

    private static (MatchKey Key, string Reason) CreateTermMatchKey(
        string queryText,
        FileRecord record,
        bool allowFuzzy)
    {
        var query = queryText.Trim().ToLowerInvariant();
        var name = record.Name.Trim().ToLowerInvariant();
        if (name == query)
        {
            return (new MatchKey(0, query.Length, 0), "exact-name");
        }

        if (name.StartsWith(query, StringComparison.Ordinal))
        {
            return (new MatchKey(1, query.Length, name.Length - query.Length), "name-prefix");
        }

        if (name.Contains(query, StringComparison.Ordinal))
        {
            return (new MatchKey(2, query.Length, name.Length - query.Length), "name-substring");
        }

        var nameScore = allowFuzzy ? FuzzyMatcher.Score(query, name) : 0;
        if (allowFuzzy && nameScore > 0)
        {
            return (new MatchKey(3, nameScore, Math.Abs(name.Length - query.Length)), "name-fuzzy");
        }

        var pinyinScore = allowFuzzy ? PinyinMatcher.Score(query, record.Name) : 0;
        if (allowFuzzy && pinyinScore > 0)
        {
            return (new MatchKey(4, pinyinScore, Math.Abs(name.Length - query.Length)), "pinyin");
        }

        var pathScore = allowFuzzy
            ? FuzzyMatcher.Score(query, record.ParentPath)
            : record.ParentPath.Contains(query, StringComparison.OrdinalIgnoreCase) ? query.Length : 0;
        return pathScore > 0
            ? (new MatchKey(5, pathScore, Math.Abs(record.ParentPath.Length - query.Length)), "path")
            : (MatchKey.NoMatch, "none");
    }

    private static int GetPreferredRootPriority(
        FileRecord record,
        string? preferredRoot)
    {
        if (string.IsNullOrWhiteSpace(preferredRoot))
        {
            return 0;
        }

        if (string.Equals(
                Path.TrimEndingDirectorySeparator(record.ParentPath),
                preferredRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return 1;
    }

    private static string CreatePathKey(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim())).ToUpperInvariant();

    private static double RecencyBoost(DateTimeOffset lastUsedAt, DateTimeOffset now)
        => Math.Clamp(20 - (now - lastUsedAt).TotalDays, 0, 20);

    private readonly record struct MatchKey(int Tier, double Quality, int LengthDifference)
    {
        public static MatchKey NoMatch => new(int.MaxValue, 0, int.MaxValue);
        public bool IsMatch => Tier != int.MaxValue;
    }

    private sealed record RankedCandidate(
        SearchResult Result,
        MatchKey TextKey,
        bool IsPinned,
        int OpenCount,
        double Recency);
}

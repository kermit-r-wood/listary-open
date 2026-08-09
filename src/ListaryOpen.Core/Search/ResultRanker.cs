using ListaryOpen.Core.Indexing;
using ListaryOpen.Core.Usage;

namespace ListaryOpen.Core.Search;

public static class ResultRanker
{
    private const int CancellationCheckIntervalMask = 63;

    public static IReadOnlyList<SearchResult> Rank(
        SearchQuery query,
        IEnumerable<FileRecord> records,
        IEnumerable<UsageRecord> usageRecords,
        IEnumerable<string> pinnedFolders,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(usageRecords);
        ArgumentNullException.ThrowIfNull(pinnedFolders);
        cancellationToken.ThrowIfCancellationRequested();

        var usage = CreateUsageLookup(usageRecords, cancellationToken);

        var pinned = pinnedFolders
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(CreatePathKey)
            .ToHashSet(StringComparer.Ordinal);
        cancellationToken.ThrowIfCancellationRequested();

        var now = DateTimeOffset.UtcNow;
        var preferredRoot = query.PreferredRoot;
        var rankedRecordCount = 0;

        var ranked = records
            .Select(record =>
            {
                if ((rankedRecordCount++ & CancellationCheckIntervalMask) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                return record;
            })
            .Where(record => (query.EffectiveMode == SearchMode.FilesAndFolders || record.IsDirectory)
                && (!query.Parsed.FileOnly || !record.IsDirectory)
                && (!query.Parsed.ApplicationsOnly
                    || ApplicationPath.IsApplication(record.FullPath, record.IsDirectory))
                && MatchesQuickFilters(query, record))
            .Select(record => RankRecord(query, record, usage, pinned, now))
            .Where(candidate => candidate.TextKey.IsMatch)
            .OrderBy(candidate => GetPreferredRootPriority(
                candidate.Result.Record,
                preferredRoot))
            .ThenBy(candidate => candidate.TextKey.Tier)
            .ThenByDescending(candidate => candidate.TextKey.Quality)
            .ThenBy(candidate => candidate.TextKey.LengthDifference)
            // Soft app boost only after text quality (apps with same tier beat non-apps).
            .ThenBy(candidate => ApplicationPath.IsApplication(
                candidate.Result.Record.FullPath,
                candidate.Result.Record.IsDirectory) ? 0 : 1)
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
        cancellationToken.ThrowIfCancellationRequested();
        return ranked;
    }

    private static Dictionary<string, UsageRecord> CreateUsageLookup(
        IEnumerable<UsageRecord> usageRecords,
        CancellationToken cancellationToken)
    {
        var usage = new Dictionary<string, UsageRecord>(StringComparer.Ordinal);
        var recordCount = 0;
        foreach (var record in usageRecords)
        {
            if ((recordCount++ & CancellationCheckIntervalMask) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (!usage.TryGetValue(record.PathKey, out var current) ||
                record.OpenCount > current.OpenCount ||
                (record.OpenCount == current.OpenCount && record.LastUsedAt > current.LastUsedAt))
            {
                usage[record.PathKey] = record;
            }
        }

        return usage;
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
            usage.TryGetValue(record.PathKey, out var filterUsage);
            return new RankedCandidate(
                new SearchResult(record, 1, "filter"),
                new TextMatchKey(0, 1, 0),
                pinned.Contains(record.PathKey),
                filterUsage?.OpenCount ?? 0,
                filterUsage is null ? 0 : RecencyBoost(filterUsage.LastUsedAt, now));
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

    private static (TextMatchKey Key, string Reason) CreateTextMatchKey(SearchQuery searchQuery, FileRecord record)
    {
        var name = record.Name.Trim().ToLowerInvariant();
        PinyinMatcher.CandidateForms? pinyinForms = null;
        var matchCount = 0;
        var maximumTier = int.MinValue;
        var qualitySum = 0d;
        var lengthDifferenceSum = 0;
        var worstTier = int.MinValue;
        var worstQuality = double.MaxValue;
        var worstReason = "none";

        foreach (var phrase in searchQuery.Parsed.Phrases)
        {
            var match = CreateTermMatchKey(
                phrase,
                record,
                name,
                allowFuzzy: false,
                ref pinyinForms);
            if (!AccumulateMatch(
                    match,
                    ref matchCount,
                    ref maximumTier,
                    ref qualitySum,
                    ref lengthDifferenceSum,
                    ref worstTier,
                    ref worstQuality,
                    ref worstReason))
            {
                return (TextMatchKey.NoMatch, "none");
            }
        }

        foreach (var term in searchQuery.Parsed.Terms)
        {
            var match = CreateTermMatchKey(
                term,
                record,
                name,
                allowFuzzy: true,
                ref pinyinForms);
            if (!AccumulateMatch(
                    match,
                    ref matchCount,
                    ref maximumTier,
                    ref qualitySum,
                    ref lengthDifferenceSum,
                    ref worstTier,
                    ref worstQuality,
                    ref worstReason))
            {
                return (TextMatchKey.NoMatch, "none");
            }
        }

        if (matchCount == 0)
        {
            return (TextMatchKey.NoMatch, "none");
        }

        return (
            new TextMatchKey(
                maximumTier,
                qualitySum,
                lengthDifferenceSum),
            worstReason);
    }

    private static bool AccumulateMatch(
        (TextMatchKey Key, string Reason) match,
        ref int matchCount,
        ref int maximumTier,
        ref double qualitySum,
        ref int lengthDifferenceSum,
        ref int worstTier,
        ref double worstQuality,
        ref string worstReason)
    {
        if (!match.Key.IsMatch)
        {
            return false;
        }

        matchCount++;
        maximumTier = Math.Max(maximumTier, match.Key.Tier);
        qualitySum += match.Key.Quality;
        lengthDifferenceSum += match.Key.LengthDifference;
        if (match.Key.Tier > worstTier ||
            (match.Key.Tier == worstTier && match.Key.Quality < worstQuality))
        {
            worstTier = match.Key.Tier;
            worstQuality = match.Key.Quality;
            worstReason = match.Reason;
        }

        return true;
    }

    private static (TextMatchKey Key, string Reason) CreateTermMatchKey(
        string queryText,
        FileRecord record,
        string name,
        bool allowFuzzy,
        ref PinyinMatcher.CandidateForms? pinyinForms)
    {
        var query = queryText;
        if (name == query)
        {
            return (new TextMatchKey(0, query.Length, 0), "exact-name");
        }

        // Prefer a clean decoded leaf (listary_open) over a raw percent-encoded
        // session folder name that only contains the query as a substring.
        var displayLeaf = PathDisplayName.UnescapeFileName(name);
        if (!string.Equals(displayLeaf, name, StringComparison.Ordinal)
            && string.Equals(displayLeaf, query, StringComparison.Ordinal))
        {
            // Still below a true on-disk exact basename so the real project folder wins.
            return (new TextMatchKey(1, query.Length, Math.Max(0, name.Length - query.Length)), "exact-name");
        }

        if (name.StartsWith(query, StringComparison.Ordinal))
        {
            return (new TextMatchKey(1, query.Length, name.Length - query.Length), "name-prefix");
        }

        if (name.Contains(query, StringComparison.Ordinal))
        {
            // Tooling caches often store whole paths as one %XX-encoded directory
            // name. Keep them discoverable but rank them after ordinary matches.
            if (PathDisplayName.LooksLikePercentEncodedPath(name))
            {
                return (new TextMatchKey(6, query.Length, name.Length - query.Length), "name-substring");
            }

            return (new TextMatchKey(2, query.Length, name.Length - query.Length), "name-substring");
        }

        // For short queries (pinyin initials), evaluate pinyin before fuzzy/substring burial
        // so "ht" ranks 合同 above whitelist/height Latin substring noise.
        var pinyinScore = 0d;
        var initialsExact = false;
        if (allowFuzzy)
        {
            pinyinForms ??= PinyinMatcher.CreateCandidateForms(record.Name);
            pinyinScore = PinyinMatcher.Score(query, pinyinForms.Value);
            initialsExact = pinyinForms.Value.Initials.Equals(query, StringComparison.OrdinalIgnoreCase)
                || pinyinForms.Value.Initials.StartsWith(query + ".", StringComparison.OrdinalIgnoreCase)
                || (" " + pinyinForms.Value.Initials + " ").Contains(" " + query + " ", StringComparison.OrdinalIgnoreCase)
                || pinyinForms.Value.Pinyin.StartsWith(query, StringComparison.OrdinalIgnoreCase);
        }

        if (allowFuzzy && initialsExact && pinyinScore > 0 && query.Length <= 3)
        {
            // Strong short-pinyin hit: better than name-substring (tier 2) for short queries.
            return (new TextMatchKey(1, 100 + pinyinScore, Math.Abs(name.Length - query.Length)), "pinyin");
        }

        var nameScore = allowFuzzy ? FuzzyMatcher.Score(query, name) : 0;
        if (allowFuzzy && nameScore > 0)
        {
            return (new TextMatchKey(3, nameScore, Math.Abs(name.Length - query.Length)), "name-fuzzy");
        }

        if (allowFuzzy && pinyinScore > 0)
        {
            return (new TextMatchKey(4, pinyinScore, Math.Abs(name.Length - query.Length)), "pinyin");
        }

        // A plain search term describes the item's name. Path matching is opt-in:
        // use a multi-segment expression (a\b or a/b), or the parsed path: filter.
        // Treating every ancestor folder as searchable text makes a query for a
        // project return all of its children (for example .git, bin, and x64).
        var pathSegmentScore = PathSegmentMatcher.Score(record.FullPath, query);
        if (pathSegmentScore > 0 && query.IndexOfAny(['\\', '/']) >= 0)
        {
            // Path-segment hits rank above generic fuzzy/pinyin noise (tier 2.5 → use tier 2 quality boost via tier 2).
            return (new TextMatchKey(2, pathSegmentScore, Math.Abs(record.ParentPath.Length - query.Length)), "path-segment");
        }

        return (TextMatchKey.NoMatch, "none");
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

    private static bool MatchesQuickFilters(SearchQuery query, FileRecord record) =>
        (query.IsDirectory is null || record.IsDirectory == query.IsDirectory) &&
        (query.RequiredExtensions.Count == 0 ||
            (!record.IsDirectory && query.RequiredExtensions.Contains(
                Path.GetExtension(record.Name).TrimStart('.'),
                StringComparer.OrdinalIgnoreCase))) &&
        (query.ModifiedAfter is null || record.LastWriteTime >= query.ModifiedAfter);

    private sealed record RankedCandidate(
        SearchResult Result,
        TextMatchKey TextKey,
        bool IsPinned,
        int OpenCount,
        double Recency);
}

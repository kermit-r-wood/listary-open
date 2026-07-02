namespace ListaryOpen.Core.Search;

public static class FuzzyMatcher
{
    public static double Score(string? query, string? candidate)
    {
        var normalizedQuery = Normalize(query);
        var normalizedCandidate = Normalize(candidate);

        if (normalizedQuery.Length == 0 || normalizedCandidate.Length == 0)
        {
            return 0;
        }

        if (normalizedCandidate.Contains(normalizedQuery, StringComparison.Ordinal))
        {
            return 200 + normalizedQuery.Length;
        }

        var queryIndex = 0;
        var score = 0d;
        var streak = 0;

        for (var candidateIndex = 0;
             candidateIndex < normalizedCandidate.Length && queryIndex < normalizedQuery.Length;
             candidateIndex++)
        {
            if (normalizedCandidate[candidateIndex] != normalizedQuery[queryIndex])
            {
                streak = 0;
                continue;
            }

            queryIndex++;
            streak++;
            score += 8 + streak * 2;

            if (candidateIndex == 0 || IsSeparator(normalizedCandidate[candidateIndex - 1]))
            {
                score += 10;
            }
        }

        return queryIndex == normalizedQuery.Length ? score : 0;
    }

    private static string Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();
    }

    private static bool IsSeparator(char value) => value is ' ' or '-' or '_' or '.' or '\\' or '/';
}

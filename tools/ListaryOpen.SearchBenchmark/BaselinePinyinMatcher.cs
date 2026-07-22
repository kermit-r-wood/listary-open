using ListaryOpen.Core.Search;

namespace ListaryOpen.SearchBenchmark;

// Previous two-enumeration implementation, retained only for semantic/performance A/B.
internal static class BaselinePinyinMatcher
{
    private static readonly IReadOnlyDictionary<char, string> KnownPinyin = new Dictionary<char, string>
    {
        ['发'] = "fa",
        ['票'] = "piao",
        ['合'] = "he",
        ['同'] = "tong",
        ['报'] = "bao",
        ['告'] = "gao",
        ['资'] = "zi",
        ['料'] = "liao",
        ['下'] = "xia",
        ['载'] = "zai",
        ['文'] = "wen",
        ['件'] = "jian",
        ['图'] = "tu",
        ['片'] = "pian",
        ['项'] = "xiang",
        ['目'] = "mu"
    };

    public static double Score(string? query, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(candidate))
        {
            return 0;
        }

        var pinyin = ToPinyin(candidate);
        var initials = ToInitials(candidate);
        return Math.Max(FuzzyMatcher.Score(query, pinyin), FuzzyMatcher.Score(query, initials));
    }

    public static string CreateSearchText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Trim().ToLowerInvariant();
        var forms = new[]
        {
            normalized,
            ToPinyin(normalized).ToLowerInvariant(),
            ToInitials(normalized).ToLowerInvariant()
        };
        return string.Join(' ', forms.Distinct(StringComparer.Ordinal));
    }

    private static string ToPinyin(string value)
        => string.Concat(value.Select(ch => KnownPinyin.TryGetValue(ch, out var pinyin)
            ? pinyin
            : ch.ToString()));

    private static string ToInitials(string value)
        => string.Concat(value.Select(ch => KnownPinyin.TryGetValue(ch, out var pinyin)
            ? pinyin[0].ToString()
            : ch.ToString()));
}

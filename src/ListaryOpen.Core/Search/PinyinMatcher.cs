namespace ListaryOpen.Core.Search;

public static class PinyinMatcher
{
    private static readonly IReadOnlyDictionary<char, string> KnownPinyin = new Dictionary<char, string>
    {
        ['发'] = "fa",
        ['票'] = "piao",
        ['文'] = "wen",
        ['件'] = "jian",
        ['图'] = "tu",
        ['片'] = "pian",
        ['项'] = "xiang",
        ['目'] = "mu"
    };

    public static double Score(string query, string candidate)
    {
        var pinyin = ToPinyin(candidate);
        var initials = ToInitials(candidate);

        return Math.Max(FuzzyMatcher.Score(query, pinyin), FuzzyMatcher.Score(query, initials));
    }

    private static string ToPinyin(string value)
    {
        return string.Concat(value.Select(ch => KnownPinyin.TryGetValue(ch, out var pinyin)
            ? pinyin
            : ch.ToString()));
    }

    private static string ToInitials(string value)
    {
        return string.Concat(value.Select(ch => KnownPinyin.TryGetValue(ch, out var pinyin)
            ? pinyin[0].ToString()
            : ch.ToString()));
    }
}

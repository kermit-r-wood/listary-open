using System.Text;
using AnyAscii;
using ListaryOpen.Core.Settings;
using TinyPinyin;

namespace ListaryOpen.Core.Search;

public static class PinyinMatcher
{
    private static int _mode = (int)SearchTransliterationMode.Multilingual;

    public static SearchTransliterationMode Mode => (SearchTransliterationMode)Volatile.Read(ref _mode);

    public static void Configure(SearchTransliterationMode mode)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        Volatile.Write(ref _mode, (int)mode);
    }

    public static double Score(string? query, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(candidate))
        {
            return 0;
        }

        return Score(query, CreateCandidateForms(candidate));
    }

    public static string CreateSearchText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Trim().ToLowerInvariant();
        var forms = CreateCandidateForms(normalized);
        var aliases = CreateSearchAliases(normalized, forms);
        return aliases.Length == 0 ? normalized : normalized + ' ' + aliases;
    }

    public static string CreateSearchAliases(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        // Classify before Trim/ToLowerInvariant. The overwhelming majority of indexed
        // paths are ASCII and need no aliases, so avoid allocating a lower-cased copy
        // for every record in a multi-million-file scan.
        if (ClassifyTransliterationNeed(value) == TransliterationNeed.None)
        {
            return string.Empty;
        }

        var normalized = value.Trim().ToLowerInvariant();
        return CreateSearchAliases(normalized, CreateCandidateForms(normalized));
    }

    private static string CreateSearchAliases(string normalized, CandidateForms forms)
    {
        var builder = new StringBuilder(normalized.Length);
        if (!string.Equals(forms.Pinyin, normalized, StringComparison.Ordinal))
        {
            builder.Append(forms.Pinyin);
        }

        if (!string.Equals(forms.Initials, normalized, StringComparison.Ordinal) &&
            !string.Equals(forms.Initials, forms.Pinyin, StringComparison.Ordinal))
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(forms.Initials);
        }

        return builder.ToString();
    }

    internal static CandidateForms CreateCandidateForms(string candidate)
    {
        if (Mode == SearchTransliterationMode.Disabled)
        {
            return new CandidateForms(candidate, candidate);
        }

        var need = ClassifyTransliterationNeed(candidate);
        if (need == TransliterationNeed.None)
        {
            return new CandidateForms(candidate, candidate);
        }

        // Non-CJK multilingual text (e.g. Cyrillic): AnyAscii only, skip TinyPinyin.
        if (need == TransliterationNeed.MultilingualOnly)
        {
            if (Mode != SearchTransliterationMode.Multilingual)
            {
                return new CandidateForms(candidate, candidate);
            }

            var multilingualOnly = candidate.Transliterate()
                .ToLowerInvariant()
                .Replace(" ", string.Empty, StringComparison.Ordinal);
            if (string.Equals(multilingualOnly, candidate, StringComparison.Ordinal) ||
                string.IsNullOrEmpty(multilingualOnly))
            {
                return new CandidateForms(candidate, candidate);
            }

            return new CandidateForms(multilingualOnly, multilingualOnly);
        }

        var pinyin = PinyinHelper.GetPinyin(candidate, string.Empty).ToLowerInvariant();
        var initials = PinyinHelper.GetPinyinInitials(candidate).ToLowerInvariant();
        if (Mode == SearchTransliterationMode.Multilingual)
        {
            var multilingual = candidate.Transliterate().ToLowerInvariant().Replace(" ", string.Empty, StringComparison.Ordinal);
            if (!string.Equals(multilingual, candidate, StringComparison.Ordinal) &&
                !string.Equals(multilingual, pinyin, StringComparison.Ordinal))
            {
                pinyin = pinyin + ' ' + multilingual;
            }
        }

        return new CandidateForms(pinyin, initials);
    }

    /// <summary>
    /// Classifies whether a string needs expensive transliteration work.
    /// Pure ASCII is the common indexing case and must stay free of TinyPinyin/AnyAscii.
    /// </summary>
    public static TransliterationNeed ClassifyTransliterationNeed(string value)
    {
        var need = TransliterationNeed.None;
        foreach (var ch in value)
        {
            if (IsCjkIdeograph(ch))
            {
                return TransliterationNeed.Chinese;
            }

            if (ch > 127)
            {
                need = TransliterationNeed.MultilingualOnly;
            }
        }

        return need;
    }

    /// <summary>
    /// CJK Unified Ideographs + Extension A + compatibility ideographs (BMP).
    /// </summary>
    public static bool IsCjkIdeograph(char ch) =>
        ch is (>= '\u3400' and <= '\u4DBF')
            or (>= '\u4E00' and <= '\u9FFF')
            or (>= '\uF900' and <= '\uFAFF');

    internal static double Score(string query, CandidateForms forms)
    {
        var pinyinScore = FuzzyMatcher.Score(query, forms.Pinyin);
        return string.Equals(forms.Initials, forms.Pinyin, StringComparison.Ordinal)
            ? pinyinScore
            : Math.Max(pinyinScore, FuzzyMatcher.Score(query, forms.Initials));
    }

    public enum TransliterationNeed
    {
        None,
        MultilingualOnly,
        Chinese
    }

    internal readonly record struct CandidateForms(string Pinyin, string Initials);
}

using System.Text;

namespace ListaryOpen.Core.Search;

public sealed record ParsedSearchQuery(
    IReadOnlyList<string> Terms,
    IReadOnlyList<string> Phrases,
    IReadOnlyList<string> PathTerms,
    IReadOnlyList<string> Extensions,
    IReadOnlyList<string> ExcludedTerms,
    IReadOnlyList<string> ExcludedExtensions,
    bool FileOnly,
    SearchMode? ModeOverride,
    bool ApplicationsOnly = false)
{
    public string RankingText
    {
        get
        {
            return string.Join(' ', Phrases
                .Concat(Terms)
                .Where(part => !string.IsNullOrWhiteSpace(part)));
        }
    }

    public static ParsedSearchQuery Parse(string text)
    {
        var terms = new List<string>();
        var phrases = new List<string>();
        var pathTerms = new List<string>();
        var extensions = new List<string>();
        var excludedTerms = new List<string>();
        var excludedExtensions = new List<string>();
        var fileOnly = false;
        var applicationsOnly = false;
        SearchMode? modeOverride = null;

        foreach (var token in Tokenize(text))
        {
            if (string.IsNullOrWhiteSpace(token.Value))
            {
                continue;
            }

            var value = token.Value.Trim();
            var excluded = value.StartsWith('!');
            if (excluded)
            {
                value = value[1..];
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (TryReadOperator(value, "ext:", out var extensionList))
            {
                // Support: ext:pdf  |  ext:pdf,docx  |  ext:pdf;docx
                foreach (var part in SplitExtensionList(extensionList))
                {
                    var normalizedExtension = NormalizeExtension(part);
                    if (!string.IsNullOrWhiteSpace(normalizedExtension))
                    {
                        Add(excluded ? excludedExtensions : extensions, normalizedExtension);
                    }
                }

                continue;
            }

            if (TryReadOperator(value, "path:", out var pathTerm))
            {
                Add(excluded ? excludedTerms : pathTerms, NormalizeTerm(pathTerm));
                continue;
            }

            if (IsModeToken(value, "folder:"))
            {
                modeOverride = SearchMode.FoldersOnly;
                fileOnly = false;
                continue;
            }

            if (IsModeToken(value, "file:"))
            {
                modeOverride = SearchMode.FilesAndFolders;
                fileOnly = true;
                continue;
            }

            if (IsModeToken(value, "app:") || IsModeToken(value, "apps:"))
            {
                applicationsOnly = true;
                fileOnly = true;
                modeOverride = SearchMode.FilesAndFolders;
                continue;
            }

            Add(excluded ? excludedTerms : token.WasQuoted ? phrases : terms, NormalizeTerm(value));
        }

        return new ParsedSearchQuery(
            terms,
            phrases,
            pathTerms,
            extensions,
            excludedTerms,
            excludedExtensions,
            fileOnly,
            modeOverride,
            applicationsOnly);
    }

    private static IEnumerable<QueryToken> Tokenize(string text)
    {
        var builder = new StringBuilder();
        var inQuote = false;
        var tokenWasQuoted = false;

        foreach (var ch in text)
        {
            if (ch == '"')
            {
                if (inQuote)
                {
                    yield return new QueryToken(builder.ToString(), WasQuoted: true);
                    builder.Clear();
                    inQuote = false;
                    tokenWasQuoted = false;
                    continue;
                }

                if (builder.Length > 0)
                {
                    yield return new QueryToken(builder.ToString(), tokenWasQuoted);
                    builder.Clear();
                }

                inQuote = true;
                tokenWasQuoted = true;
                continue;
            }

            if (char.IsWhiteSpace(ch) && !inQuote)
            {
                if (builder.Length > 0)
                {
                    yield return new QueryToken(builder.ToString(), tokenWasQuoted);
                    builder.Clear();
                    tokenWasQuoted = false;
                }

                continue;
            }

            builder.Append(ch);
        }

        if (builder.Length > 0)
        {
            yield return new QueryToken(builder.ToString(), tokenWasQuoted && !inQuote);
        }
    }

    private static bool TryReadOperator(string value, string prefix, out string operand)
    {
        if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            operand = value[prefix.Length..];
            return true;
        }

        operand = string.Empty;
        return false;
    }

    private static bool IsModeToken(string value, string token)
    {
        return string.Equals(value, token, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeExtension(string value)
    {
        return NormalizeTerm(value).TrimStart('.');
    }

    private static string NormalizeTerm(string value)
    {
        return value.Trim().ToLowerInvariant();
    }

    private static IEnumerable<string> SplitExtensionList(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield break;
        }

        foreach (var part in value.Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!string.IsNullOrWhiteSpace(part))
            {
                yield return part;
            }
        }
    }

    private static void Add(ICollection<string> values, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            values.Add(value);
        }
    }

    private sealed record QueryToken(string Value, bool WasQuoted);
}

namespace ListaryOpen.Core.Search;

/// <summary>
/// Ordered multi-segment path matching for path: filters and ranking.
/// Segments may be separated by \ or /; each segment must match a path component
/// in non-decreasing component order, without mid-token English false positives.
/// </summary>
public static class PathSegmentMatcher
{
    private static readonly char[] Separators = ['\\', '/'];

    public static bool Matches(string? fullPath, string? pathExpression)
    {
        if (string.IsNullOrWhiteSpace(fullPath) || string.IsNullOrWhiteSpace(pathExpression))
        {
            return false;
        }

        var path = fullPath.Trim();
        var expression = pathExpression.Trim();
        var segments = SplitSegments(expression);
        if (segments.Count == 0)
        {
            return false;
        }

        // Always use component-boundary matching. Do not short-circuit on raw
        // path.Contains(expression): that lets path:app match C:\application\.
        if (segments.Count == 1)
        {
            return PathComponents(path).Any(component => ComponentMatches(component, segments[0]));
        }

        // Multi-segment: also accept a contiguous separator-aligned path snippet
        // (e.g. expression "Projects\foo" inside "C:\Projects\foo\bar") only when
        // the match is bounded by separators / string edges — not mid-token.
        if (HasSeparatorAlignedSnippet(path, expression))
        {
            return true;
        }

        return MatchesOrderedComponents(path, segments);
    }

    public static double Score(string? fullPath, string? pathExpression)
    {
        if (!Matches(fullPath, pathExpression))
        {
            return 0;
        }

        var segments = SplitSegments(pathExpression ?? string.Empty);
        if (segments.Count == 0)
        {
            return pathExpression?.Length ?? 0;
        }

        return segments.Sum(segment => segment.Length) + segments.Count * 2;
    }

    public static IReadOnlyList<string> SplitSegments(string expression)
    {
        return expression
            .Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => part.Length > 0)
            .Select(part => part.ToLowerInvariant())
            .ToArray();
    }

    /// <summary>
    /// True when expression appears as a contiguous path snippet bounded by
    /// separators (or string edges), after normalizing / to \.
    /// </summary>
    internal static bool HasSeparatorAlignedSnippet(string path, string expression)
    {
        var normalizedPath = "\\" + path.Replace('/', '\\').Trim().TrimStart('\\') + "\\";
        var normalizedExpression = expression.Replace('/', '\\').Trim().Trim(Separators);
        if (string.IsNullOrEmpty(normalizedExpression))
        {
            return false;
        }

        // Require snippet to be separator-bounded: \snippet\ inside \path\
        var needle = "\\" + normalizedExpression.Trim(Separators) + "\\";
        return normalizedPath.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesOrderedComponents(string path, IReadOnlyList<string> segments)
    {
        var components = PathComponents(path);
        var componentIndex = 0;
        foreach (var segment in segments)
        {
            var found = false;
            while (componentIndex < components.Count)
            {
                var component = components[componentIndex];
                componentIndex++;
                if (ComponentMatches(component, segment))
                {
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                return false;
            }
        }

        return true;
    }

    private static IReadOnlyList<string> PathComponents(string path) => SplitSegments(path);

    internal static bool ComponentMatches(string component, string segment)
    {
        if (component.Equals(segment, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // ASCII letter prefix: only allow when the component ends at the segment
        // or the next character is not a letter (avoids src→source, app→application, a→ab).
        if (IsAsciiLetters(segment)
            && component.StartsWith(segment, StringComparison.OrdinalIgnoreCase)
            && component.Length > segment.Length)
        {
            return !char.IsLetter(component[segment.Length]);
        }

        if (component.StartsWith(segment, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Non-ASCII (e.g. Chinese folder nicknames): allow substring within a component.
        if (!IsAsciiLetters(segment)
            && component.Contains(segment, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static bool IsAsciiLetters(string value)
    {
        foreach (var ch in value)
        {
            if (ch > 127 || !char.IsLetter(ch))
            {
                return false;
            }
        }

        return value.Length > 0;
    }
}

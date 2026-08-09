namespace ListaryOpen.Core.Search;

/// <summary>
/// Human-readable basenames for search UI. Tooling sometimes stores paths as
/// single URL-encoded directory names (for example under <c>.grok\sessions</c>),
/// which otherwise render as unreadable percent-encoded noise.
/// </summary>
public static class PathDisplayName
{
    public static string FromFullPath(string? fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return string.Empty;
        }

        var name = string.Equals(Path.GetExtension(fullPath), ".lnk", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(fullPath)
            : Path.GetFileName(fullPath);

        return UnescapeFileName(name);
    }

    public static string UnescapeFileName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.IndexOf('%') < 0)
        {
            return name ?? string.Empty;
        }

        try
        {
            // Uri.UnescapeDataString leaves '+' intact (unlike UrlDecode), which
            // matches common file-name encodings used by session tooling.
            var decoded = Uri.UnescapeDataString(name);
            if (string.IsNullOrEmpty(decoded) || string.Equals(decoded, name, StringComparison.Ordinal))
            {
                return name;
            }

            // Encoded absolute paths decode to multi-segment text; surface the leaf.
            if (decoded.IndexOfAny(['\\', '/']) >= 0)
            {
                var normalized = decoded.Replace('/', Path.DirectorySeparatorChar);
                var leaf = Path.GetFileName(Path.TrimEndingDirectorySeparator(normalized));
                if (!string.IsNullOrEmpty(leaf))
                {
                    return leaf;
                }
            }

            return decoded;
        }
        catch (UriFormatException)
        {
            return name;
        }
        catch (ArgumentException)
        {
            return name;
        }
    }

    /// <summary>
    /// True when a basename looks like a percent-encoded absolute path rather than
    /// a normal file or folder name (e.g. <c>C%3A%5CUsers%5C...</c>).
    /// </summary>
    public static bool LooksLikePercentEncodedPath(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.IndexOf('%') < 0)
        {
            return false;
        }

        return name.Contains("%3A", StringComparison.OrdinalIgnoreCase)
            || name.Contains("%5C", StringComparison.OrdinalIgnoreCase)
            || name.Contains("%2F", StringComparison.OrdinalIgnoreCase);
    }
}

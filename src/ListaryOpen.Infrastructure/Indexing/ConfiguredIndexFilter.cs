using ListaryOpen.Core.Indexing;
using System.IO;

namespace ListaryOpen.Infrastructure.Indexing;

public static class ConfiguredIndexFilter
{
    public static bool ShouldInclude(FileRecord record, IReadOnlyList<string> excludedPaths)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(excludedPaths);

        foreach (var configuredValue in excludedPaths)
        {
            if (string.IsNullOrWhiteSpace(configuredValue))
            {
                continue;
            }

            var expanded = Environment.ExpandEnvironmentVariables(configuredValue.Trim());
            if (expanded.StartsWith("*.", StringComparison.Ordinal))
            {
                if (record.FullPath.EndsWith(expanded[1..], StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                continue;
            }

            if (!Path.IsPathFullyQualified(expanded))
            {
                if (record.FullPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Any(segment => string.Equals(segment, expanded, StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }

                continue;
            }

            try
            {
                var excluded = Path.TrimEndingDirectorySeparator(Path.GetFullPath(expanded));
                var excludedPrefix = excluded.EndsWith(Path.DirectorySeparatorChar)
                    || excluded.EndsWith(Path.AltDirectorySeparatorChar)
                        ? excluded
                        : excluded + Path.DirectorySeparatorChar;
                if (string.Equals(record.FullPath, excluded, StringComparison.OrdinalIgnoreCase) ||
                    record.FullPath.StartsWith(excludedPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
            {
            }
        }

        return true;
    }
}

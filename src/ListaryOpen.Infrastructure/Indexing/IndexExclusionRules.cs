using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ListaryOpen.Infrastructure.Indexing;

public sealed class IndexExclusionRules
{
    private readonly HashSet<string> _directoryNames;

    public IndexExclusionRules(IEnumerable<string> directoryNames)
    {
        ArgumentNullException.ThrowIfNull(directoryNames);

        _directoryNames = directoryNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    // Full-disk indexing must not silently omit conventional development folders.
    // User-visible exclusions are applied separately by ConfiguredIndexFilter.
    public static IndexExclusionRules Default { get; } = new(Array.Empty<string>());

    public bool ShouldExcludeDirectoryName(string directoryName)
    {
        return !string.IsNullOrWhiteSpace(directoryName)
            && _directoryNames.Contains(directoryName.Trim());
    }

    public bool ShouldExcludeDirectoryPath(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return false;
        }

        return ShouldExcludeDirectoryName(Path.GetFileName(Path.TrimEndingDirectorySeparator(directoryPath)));
    }
}

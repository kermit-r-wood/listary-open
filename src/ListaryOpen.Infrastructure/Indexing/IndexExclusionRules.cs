using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ListaryOpen.Infrastructure.Indexing;

public sealed class IndexExclusionRules
{
    private static readonly string[] DefaultDirectoryNames =
    [
        ".git",
        ".svn",
        ".hg",
        "node_modules",
        "bin",
        "obj",
        ".vs",
        ".idea",
        ".vscode",
        "packages",
        "dist",
        "build",
        ".cache",
        "__pycache__",
        ".pytest_cache",
        ".next",
        ".nuxt",
        "target"
    ];

    private readonly HashSet<string> _directoryNames;

    public IndexExclusionRules(IEnumerable<string> directoryNames)
    {
        ArgumentNullException.ThrowIfNull(directoryNames);

        _directoryNames = directoryNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public static IndexExclusionRules Default { get; } = new(DefaultDirectoryNames);

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

namespace ListaryOpen.Indexer.Elevated.Ntfs;

internal sealed record NtfsScanRoot(string RequestedRoot, string VolumeRoot, string VolumePath)
{
    public static NtfsScanRoot Create(string requestedRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedRoot);

        var trimmed = requestedRoot.Trim();
        if (!Path.IsPathFullyQualified(trimmed))
        {
            throw new ArgumentException("Scan root must be fully qualified.", nameof(requestedRoot));
        }

        var fullPath = Path.GetFullPath(trimmed);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root) || root.Length < 3 || root[1] != ':')
        {
            throw new ArgumentException("Scan root must be a drive-letter path.", nameof(requestedRoot));
        }

        var volumeRoot = EnsureTrailingDirectorySeparator(root);
        var normalizedRequestedRoot = IsRootPath(fullPath, volumeRoot)
            ? volumeRoot
            : Path.TrimEndingDirectorySeparator(fullPath);

        return new NtfsScanRoot(normalizedRequestedRoot, volumeRoot, @"\\.\" + volumeRoot[..2]);
    }

    private static bool IsRootPath(string path, string root)
    {
        return string.Equals(
            Path.TrimEndingDirectorySeparator(path),
            Path.TrimEndingDirectorySeparator(root),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsureTrailingDirectorySeparator(string path)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(path);
        return trimmed.EndsWith(Path.DirectorySeparatorChar)
            ? trimmed
            : trimmed + Path.DirectorySeparatorChar;
    }
}

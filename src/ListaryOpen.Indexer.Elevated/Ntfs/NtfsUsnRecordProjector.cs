using ListaryOpen.Core.Indexing;

namespace ListaryOpen.Indexer.Elevated.Ntfs;

internal sealed record NtfsUsnEntry(
    ulong FileReferenceNumber,
    ulong ParentFileReferenceNumber,
    string Name,
    bool IsDirectory);

internal static class NtfsUsnRecordProjector
{
    public static IEnumerable<FileRecord> CreateFileRecords(
        string volumeRoot,
        string requestedRoot,
        IEnumerable<NtfsUsnEntry> entries,
        INtfsFileMetadataReader metadataReader,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedRoot);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(metadataReader);

        var orderedEntries = entries.ToList();
        var entriesByReferenceNumber = orderedEntries
            .GroupBy(entry => entry.FileReferenceNumber)
            .ToDictionary(group => group.Key, group => group.Last());
        var resolvedPaths = new Dictionary<ulong, string>();

        foreach (var entry in orderedEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryResolvePath(entry, volumeRoot, entriesByReferenceNumber, resolvedPaths, new HashSet<ulong>(), out var fullPath)
                || !IsRequestedRootOrDescendant(fullPath, requestedRoot)
                || !metadataReader.TryRead(fullPath, entry.IsDirectory, cancellationToken, out var metadata))
            {
                continue;
            }

            yield return FileRecord.Create(fullPath, entry.IsDirectory, metadata.SizeBytes, metadata.LastWriteTime);
        }
    }

    private static bool TryResolvePath(
        NtfsUsnEntry entry,
        string volumeRoot,
        IReadOnlyDictionary<ulong, NtfsUsnEntry> entries,
        IDictionary<ulong, string> resolvedPaths,
        ISet<ulong> resolving,
        out string fullPath)
    {
        if (resolvedPaths.TryGetValue(entry.FileReferenceNumber, out fullPath!))
        {
            return true;
        }

        if (!resolving.Add(entry.FileReferenceNumber))
        {
            fullPath = string.Empty;
            return false;
        }

        try
        {
            if (IsRootEntry(entry))
            {
                fullPath = volumeRoot;
                resolvedPaths[entry.FileReferenceNumber] = fullPath;
                return true;
            }

            if (!entries.TryGetValue(entry.ParentFileReferenceNumber, out var parent)
                || !TryResolvePath(parent, volumeRoot, entries, resolvedPaths, resolving, out var parentPath)
                || string.IsNullOrWhiteSpace(entry.Name)
                || entry.Name == ".")
            {
                fullPath = string.Empty;
                return false;
            }

            fullPath = Path.Combine(parentPath, entry.Name);
            resolvedPaths[entry.FileReferenceNumber] = fullPath;
            return true;
        }
        finally
        {
            resolving.Remove(entry.FileReferenceNumber);
        }
    }

    private static bool IsRootEntry(NtfsUsnEntry entry)
    {
        return entry.FileReferenceNumber == entry.ParentFileReferenceNumber
            || entry.Name == ".";
    }

    private static bool IsRequestedRootOrDescendant(string fullPath, string requestedRoot)
    {
        var normalizedPath = NormalizePath(fullPath);
        var normalizedRequestedRoot = NormalizePath(requestedRoot);
        if (string.Equals(normalizedPath, normalizedRequestedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var requestedRootWithSeparator = normalizedRequestedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedRequestedRoot
            : normalizedRequestedRoot + Path.DirectorySeparatorChar;

        return normalizedPath.StartsWith(requestedRootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return Path.TrimEndingDirectorySeparator(fullPath);
    }
}

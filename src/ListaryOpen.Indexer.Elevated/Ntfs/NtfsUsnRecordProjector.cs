using ListaryOpen.Core.Indexing;
using ListaryOpen.Infrastructure.Indexing;

namespace ListaryOpen.Indexer.Elevated.Ntfs;

internal sealed record NtfsUsnEntry(
    ulong FileReferenceNumber,
    ulong ParentFileReferenceNumber,
    string Name,
    bool IsDirectory,
    long Usn = 0,
    uint Reason = 0);

internal static class NtfsUsnRecordProjector
{
    public static IEnumerable<FileRecord> CreateFileRecords(
        string volumeRoot,
        string requestedRoot,
        IEnumerable<NtfsUsnEntry> entries,
        INtfsFileMetadataReader metadataReader,
        CancellationToken cancellationToken,
        ulong? volumeRootFileReferenceNumber = null,
        IndexExclusionRules? exclusionRules = null,
        bool failOnSkippedRecords = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedRoot);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(metadataReader);

        var orderedEntries = entries.ToList();
        var directoriesByReferenceNumber = orderedEntries
            .Where(entry => entry.IsDirectory)
            .GroupBy(entry => GetMftSegmentReferenceNumber(entry.FileReferenceNumber))
            .ToDictionary(group => group.Key, group => group.Last());

        foreach (var record in CreateFileRecordsFromDirectoryMap(
                     volumeRoot,
                     requestedRoot,
                     directoriesByReferenceNumber,
                     orderedEntries,
                     metadataReader,
                     cancellationToken,
                     volumeRootFileReferenceNumber,
                     exclusionRules,
                     failOnSkippedRecords))
        {
            yield return record;
        }
    }

    public static IEnumerable<FileRecord> CreateFileRecordsFromDirectoryMap(
        string volumeRoot,
        string requestedRoot,
        IReadOnlyDictionary<ulong, NtfsUsnEntry> directoriesByReferenceNumber,
        IEnumerable<NtfsUsnEntry> entries,
        INtfsFileMetadataReader metadataReader,
        CancellationToken cancellationToken,
        ulong? volumeRootFileReferenceNumber = null,
        IndexExclusionRules? exclusionRules = null,
        bool failOnSkippedRecords = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedRoot);
        ArgumentNullException.ThrowIfNull(directoriesByReferenceNumber);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(metadataReader);

        var rules = exclusionRules ?? IndexExclusionRules.Default;
        var resolvedPaths = new Dictionary<ulong, string>();
        var normalizedDirectories = directoriesByReferenceNumber.Values
            .GroupBy(entry => GetMftSegmentReferenceNumber(entry.FileReferenceNumber))
            .ToDictionary(group => group.Key, group => group.Last());

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryResolvePath(
                    entry,
                    volumeRoot,
                    normalizedDirectories,
                    resolvedPaths,
                    new HashSet<ulong>(),
                    volumeRootFileReferenceNumber,
                    out var fullPath))
            {
                if (failOnSkippedRecords && !IsNtfsMetadataEntry(entry))
                {
                    throw new InvalidDataException(
                        $"NTFS record path could not be resolved for file reference {entry.FileReferenceNumber} " +
                        $"(parent {entry.ParentFileReferenceNumber}, name '{entry.Name}', directory {entry.IsDirectory}).");
                }

                continue;
            }

            if (IsExcludedByRules(fullPath, entry.IsDirectory, rules)
                || !IsRequestedRootOrDescendant(fullPath, requestedRoot))
            {
                continue;
            }

            if (!metadataReader.TryRead(fullPath, entry.IsDirectory, cancellationToken, out var metadata))
            {
                if (failOnSkippedRecords)
                {
                    throw new IOException($"NTFS record metadata could not be read for {fullPath}.");
                }

                continue;
            }

            yield return FileRecord.Create(fullPath, entry.IsDirectory, metadata.SizeBytes, metadata.LastWriteTime);
        }
    }

    private static bool IsExcludedByRules(string fullPath, bool isDirectory, IndexExclusionRules exclusionRules)
    {
        var normalizedPath = NormalizePath(fullPath);
        var root = Path.GetPathRoot(normalizedPath);
        var relativePath = string.IsNullOrWhiteSpace(root)
            ? normalizedPath
            : Path.GetRelativePath(root, normalizedPath);
        if (relativePath == ".")
        {
            return false;
        }

        var segments = relativePath.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        var segmentCount = isDirectory ? segments.Length : Math.Max(0, segments.Length - 1);
        for (var index = 0; index < segmentCount; index++)
        {
            if (exclusionRules.ShouldExcludeDirectoryName(segments[index]))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool TryResolvePath(
        NtfsUsnEntry entry,
        string volumeRoot,
        IReadOnlyDictionary<ulong, NtfsUsnEntry> entries,
        IDictionary<ulong, string> resolvedPaths,
        ISet<ulong> resolving,
        ulong? volumeRootFileReferenceNumber,
        out string fullPath)
    {
        if (!entry.IsDirectory)
        {
            return TryResolveFilePath(
                entry,
                volumeRoot,
                entries,
                resolvedPaths,
                resolving,
                volumeRootFileReferenceNumber,
                out fullPath);
        }

        var entryReferenceNumber = GetMftSegmentReferenceNumber(entry.FileReferenceNumber);
        if (resolvedPaths.TryGetValue(entryReferenceNumber, out fullPath!))
        {
            return true;
        }

        if (!resolving.Add(entryReferenceNumber))
        {
            fullPath = string.Empty;
            return false;
        }

        try
        {
            if (IsRootEntry(entry, volumeRootFileReferenceNumber))
            {
                fullPath = volumeRoot;
                resolvedPaths[entryReferenceNumber] = fullPath;
                return true;
            }

            if (IsDirectChildOfVolumeRoot(entry, volumeRootFileReferenceNumber)
                && IsUsableChildName(entry.Name))
            {
                fullPath = Path.Combine(volumeRoot, entry.Name);
                resolvedPaths[entryReferenceNumber] = fullPath;
                return true;
            }

            if (!entries.TryGetValue(GetMftSegmentReferenceNumber(entry.ParentFileReferenceNumber), out var parent)
                || !TryResolvePath(
                    parent,
                    volumeRoot,
                    entries,
                    resolvedPaths,
                    resolving,
                    volumeRootFileReferenceNumber,
                    out var parentPath)
                || !IsUsableChildName(entry.Name))
            {
                fullPath = string.Empty;
                return false;
            }

            fullPath = Path.Combine(parentPath, entry.Name);
            resolvedPaths[entryReferenceNumber] = fullPath;
            return true;
        }
        finally
        {
            resolving.Remove(entryReferenceNumber);
        }
    }

    private static bool TryResolveFilePath(
        NtfsUsnEntry entry,
        string volumeRoot,
        IReadOnlyDictionary<ulong, NtfsUsnEntry> entries,
        IDictionary<ulong, string> resolvedPaths,
        ISet<ulong> resolving,
        ulong? volumeRootFileReferenceNumber,
        out string fullPath)
    {
        if (!IsUsableChildName(entry.Name))
        {
            fullPath = string.Empty;
            return false;
        }

        if (IsDirectChildOfVolumeRoot(entry, volumeRootFileReferenceNumber))
        {
            fullPath = Path.Combine(volumeRoot, entry.Name);
            return true;
        }

        if (!entries.TryGetValue(GetMftSegmentReferenceNumber(entry.ParentFileReferenceNumber), out var parent)
            || !TryResolvePath(
                parent,
                volumeRoot,
                entries,
                resolvedPaths,
                resolving,
                volumeRootFileReferenceNumber,
                out var parentPath))
        {
            fullPath = string.Empty;
            return false;
        }

        fullPath = Path.Combine(parentPath, entry.Name);
        return true;
    }

    private static bool IsRootEntry(NtfsUsnEntry entry, ulong? volumeRootFileReferenceNumber)
    {
        return entry.FileReferenceNumber == entry.ParentFileReferenceNumber
            || IsSameFileReference(entry.FileReferenceNumber, volumeRootFileReferenceNumber)
            || entry.Name == ".";
    }

    private static bool IsDirectChildOfVolumeRoot(NtfsUsnEntry entry, ulong? volumeRootFileReferenceNumber)
    {
        return IsSameFileReference(entry.ParentFileReferenceNumber, volumeRootFileReferenceNumber);
    }

    private static bool IsSameFileReference(ulong referenceNumber, ulong? expectedReferenceNumber)
    {
        return expectedReferenceNumber is { } expected
            && (referenceNumber == expected
                || GetMftSegmentReferenceNumber(referenceNumber) == GetMftSegmentReferenceNumber(expected));
    }

    private static ulong GetMftSegmentReferenceNumber(ulong fileReferenceNumber)
    {
        const ulong mftSegmentReferenceNumberMask = 0x0000_FFFF_FFFF_FFFF;
        return fileReferenceNumber & mftSegmentReferenceNumberMask;
    }

    private static bool IsUsableChildName(string name)
    {
        return !string.IsNullOrWhiteSpace(name) && name != ".";
    }

    private static bool IsNtfsMetadataEntry(NtfsUsnEntry entry)
    {
        return entry.Name.StartsWith('$');
    }

    internal static bool IsRequestedRootOrDescendant(string fullPath, string requestedRoot)
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

using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ListaryOpen.Core.Indexing;
using ListaryOpen.Infrastructure.Indexing;

namespace ListaryOpen.Indexer.Elevated.Ntfs;

/// <summary>
/// Full MFT scanner: sequential $MFT read, all FILE_NAME attributes (hard links),
/// path expansion in memory, no per-file CreateFile for metadata.
/// </summary>
internal sealed class NtfsMftScanner
{
    private const int MaxMftBytesInMemory = 768 * 1024 * 1024;
    private const ulong RootDirectoryRecordNumber = 5;

    private readonly record struct DirectoryNode(ulong ParentRecordNumber, string Name);
    private readonly record struct NameLink(
        ulong RecordNumber,
        ulong ParentRecordNumber,
        string Name,
        bool IsDirectory,
        long SizeBytes,
        DateTimeOffset LastWriteTime);

    public async IAsyncEnumerable<FileRecord> EnumerateVolumeAsync(
        string volumeRoot,
        string requestedRoot,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        IndexExclusionRules? exclusionRules = null)
    {
        await Task.CompletedTask.ConfigureAwait(false);

        var scanRoot = NtfsScanRoot.Create(
            string.IsNullOrWhiteSpace(requestedRoot) ? volumeRoot : requestedRoot);
        var rules = exclusionRules ?? IndexExclusionRules.Default;
        var handle = OpenVolume(scanRoot.VolumePath);

        try
        {
            var volumeData = QueryVolumeData(handle);
            ValidateVolumeData(volumeData);

            var mftBytes = ReadEntireMft(handle, volumeData, cancellationToken);
            foreach (var record in ProjectRecords(
                         mftBytes,
                         volumeData,
                         scanRoot.VolumeRoot,
                         scanRoot.RequestedRoot,
                         rules,
                         cancellationToken))
            {
                yield return record;
            }
        }
        finally
        {
            NtfsNativeMethods.CloseHandle(handle);
        }
    }

    /// <summary>
    /// Lightweight probe: open volume, read geometry and $MFT record 0 runlist.
    /// Used to choose MFT scan vs ENUM_USN_DATA without yielding inside try/catch.
    /// </summary>
    internal static bool TryValidateVolume(string volumeRoot, out string? error)
    {
        error = null;
        try
        {
            var scanRoot = NtfsScanRoot.Create(volumeRoot);
            var handle = OpenVolume(scanRoot.VolumePath);
            try
            {
                var volumeData = QueryVolumeData(handle);
                ValidateVolumeData(volumeData);

                var bytesPerRecord = (int)volumeData.BytesPerFileRecordSegment;
                var bytesPerCluster = (int)volumeData.BytesPerCluster;
                var bytesPerSector = (int)volumeData.BytesPerSector;
                var firstRecordOffset = volumeData.MftStartLcn * bytesPerCluster;
                var firstRecord = ReadVolumeBytes(
                    handle,
                    firstRecordOffset,
                    bytesPerRecord,
                    alignTo: bytesPerSector);
                if (!NtfsMftRecordParser.TryApplyUpdateSequence(firstRecord, bytesPerSector))
                {
                    error = "Failed to apply update sequence on $MFT record 0.";
                    return false;
                }

                if (firstRecord.Length < 4
                    || firstRecord[0] != (byte)'F'
                    || firstRecord[1] != (byte)'I'
                    || firstRecord[2] != (byte)'L'
                    || firstRecord[3] != (byte)'E')
                {
                    error = "$MFT record 0 signature is invalid.";
                    return false;
                }

                if (volumeData.MftValidDataLength <= 0
                    || volumeData.MftValidDataLength > MaxMftBytesInMemory)
                {
                    error = "NTFS $MFT size is outside the supported in-memory range.";
                    return false;
                }

                return true;
            }
            finally
            {
                NtfsNativeMethods.CloseHandle(handle);
            }
        }
        catch (Exception exception) when (exception is InvalidDataException
                                              or Win32Exception
                                              or IOException
                                              or UnauthorizedAccessException
                                              or ArgumentException)
        {
            error = exception.Message;
            return false;
        }
    }

    internal static IEnumerable<FileRecord> ProjectRecords(
        byte[] mftBytes,
        NtfsNativeMethods.NtfsVolumeDataBuffer volumeData,
        string volumeRoot,
        string requestedRoot,
        IndexExclusionRules exclusionRules,
        CancellationToken cancellationToken)
    {
        var bytesPerRecord = (int)volumeData.BytesPerFileRecordSegment;
        var bytesPerSector = (int)volumeData.BytesPerSector;
        if (bytesPerRecord <= 0 || mftBytes.Length < bytesPerRecord)
        {
            yield break;
        }

        var recordCount = mftBytes.Length / bytesPerRecord;
        var parsedByRecord = new Dictionary<ulong, NtfsMftParsedRecord>(recordCount);

        for (var index = 0; index < recordCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var offset = index * bytesPerRecord;
            var recordSpan = mftBytes.AsSpan(offset, bytesPerRecord);
            if (!NtfsMftRecordParser.TryApplyUpdateSequence(recordSpan, bytesPerSector))
            {
                continue;
            }

            if (!NtfsMftRecordParser.TryParse(recordSpan, (ulong)index, out var parsed) || !parsed.InUse)
            {
                continue;
            }

            parsedByRecord[parsed.RecordNumber] = parsed;
        }

        var directories = new Dictionary<ulong, DirectoryNode>(Math.Max(16, parsedByRecord.Count / 8));
        var links = new List<NameLink>(parsedByRecord.Count);

        foreach (var parsed in parsedByRecord.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!parsed.IsBaseRecord)
            {
                continue;
            }

            var fileNames = CollectFileNames(parsed, parsedByRecord);
            var indexableNames = NtfsMftRecordParser.SelectIndexableFileNames(fileNames).ToList();
            if (indexableNames.Count == 0)
            {
                // $Root (record 5) has no useful Win32 name; still register as volume root.
                if (parsed.RecordNumber == RootDirectoryRecordNumber && parsed.IsDirectory)
                {
                    directories[parsed.RecordNumber] = new DirectoryNode(parsed.RecordNumber, string.Empty);
                }

                continue;
            }

            if (parsed.IsDirectory)
            {
                var primary = indexableNames[0];
                directories[parsed.RecordNumber] = new DirectoryNode(
                    NtfsMftRecordParser.GetMftSegmentReferenceNumber(primary.ParentFileReferenceNumber),
                    primary.Name);
            }

            foreach (var name in indexableNames)
            {
                links.Add(new NameLink(
                    parsed.RecordNumber,
                    NtfsMftRecordParser.GetMftSegmentReferenceNumber(name.ParentFileReferenceNumber),
                    name.Name,
                    parsed.IsDirectory,
                    parsed.SizeBytes,
                    parsed.LastWriteTime));
            }
        }

        // Ensure root is present even if only discovered via child parents.
        directories.TryAdd(RootDirectoryRecordNumber, new DirectoryNode(RootDirectoryRecordNumber, string.Empty));

        var resolvedDirectoryPaths = new Dictionary<ulong, string>();
        foreach (var link in links)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryBuildPath(
                    link.RecordNumber,
                    link.ParentRecordNumber,
                    link.Name,
                    link.IsDirectory,
                    volumeRoot,
                    directories,
                    resolvedDirectoryPaths,
                    out var fullPath))
            {
                continue;
            }

            if (IsNtfsMetadataPath(fullPath)
                || IsExcludedByRules(fullPath, link.IsDirectory, exclusionRules)
                || !NtfsUsnRecordProjector.IsRequestedRootOrDescendant(fullPath, requestedRoot))
            {
                continue;
            }

            FileRecord record;
            try
            {
                // Stamp 48-bit MFT segment so hard-link resync can purge stale names.
                record = FileRecord.Create(
                    fullPath,
                    link.IsDirectory,
                    link.SizeBytes,
                    link.LastWriteTime,
                    link.RecordNumber & 0x0000_FFFF_FFFF_FFFFUL);
            }
            catch (ArgumentException)
            {
                continue;
            }

            yield return record;
        }
    }

    internal static IReadOnlyList<NtfsMftFileName> CollectFileNames(
        NtfsMftParsedRecord baseRecord,
        IReadOnlyDictionary<ulong, NtfsMftParsedRecord> parsedByRecord)
    {
        var names = new List<NtfsMftFileName>(baseRecord.FileNames);
        foreach (var extensionRef in baseRecord.AttributeListFileReferences)
        {
            if (!parsedByRecord.TryGetValue(extensionRef, out var extension)
                || extension.RecordNumber == baseRecord.RecordNumber)
            {
                continue;
            }

            names.AddRange(extension.FileNames);
        }

        return names;
    }

    private static bool TryBuildPath(
        ulong recordNumber,
        ulong parentRecordNumber,
        string name,
        bool isDirectory,
        string volumeRoot,
        IReadOnlyDictionary<ulong, DirectoryNode> directories,
        IDictionary<ulong, string> resolvedDirectoryPaths,
        out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(name) && recordNumber != RootDirectoryRecordNumber)
        {
            return false;
        }

        if (recordNumber == RootDirectoryRecordNumber && isDirectory)
        {
            fullPath = volumeRoot;
            resolvedDirectoryPaths[recordNumber] = fullPath;
            return true;
        }

        if (!TryResolveDirectoryPath(
                parentRecordNumber,
                volumeRoot,
                directories,
                resolvedDirectoryPaths,
                new HashSet<ulong>(),
                out var parentPath))
        {
            return false;
        }

        fullPath = string.IsNullOrEmpty(name)
            ? parentPath
            : Path.Combine(parentPath, name);

        if (isDirectory)
        {
            resolvedDirectoryPaths[recordNumber] = fullPath;
        }

        return true;
    }

    private static bool TryResolveDirectoryPath(
        ulong recordNumber,
        string volumeRoot,
        IReadOnlyDictionary<ulong, DirectoryNode> directories,
        IDictionary<ulong, string> resolvedDirectoryPaths,
        ISet<ulong> resolving,
        out string fullPath)
    {
        if (resolvedDirectoryPaths.TryGetValue(recordNumber, out fullPath!))
        {
            return true;
        }

        if (recordNumber == RootDirectoryRecordNumber)
        {
            fullPath = volumeRoot;
            resolvedDirectoryPaths[recordNumber] = fullPath;
            return true;
        }

        if (!resolving.Add(recordNumber))
        {
            fullPath = string.Empty;
            return false;
        }

        try
        {
            if (!directories.TryGetValue(recordNumber, out var node))
            {
                fullPath = string.Empty;
                return false;
            }

            if (node.ParentRecordNumber == recordNumber
                || recordNumber == RootDirectoryRecordNumber)
            {
                fullPath = volumeRoot;
                resolvedDirectoryPaths[recordNumber] = fullPath;
                return true;
            }

            if (!TryResolveDirectoryPath(
                    node.ParentRecordNumber,
                    volumeRoot,
                    directories,
                    resolvedDirectoryPaths,
                    resolving,
                    out var parentPath))
            {
                fullPath = string.Empty;
                return false;
            }

            fullPath = string.IsNullOrEmpty(node.Name)
                ? parentPath
                : Path.Combine(parentPath, node.Name);
            resolvedDirectoryPaths[recordNumber] = fullPath;
            return true;
        }
        finally
        {
            resolving.Remove(recordNumber);
        }
    }

    private static byte[] ReadEntireMft(
        IntPtr volumeHandle,
        NtfsNativeMethods.NtfsVolumeDataBuffer volumeData,
        CancellationToken cancellationToken)
    {
        var bytesPerRecord = (int)volumeData.BytesPerFileRecordSegment;
        var bytesPerCluster = (int)volumeData.BytesPerCluster;
        var bytesPerSector = (int)volumeData.BytesPerSector;
        if (bytesPerRecord <= 0 || bytesPerCluster <= 0 || bytesPerSector <= 0)
        {
            throw new InvalidDataException("NTFS volume geometry is invalid.");
        }

        // Read $MFT record 0 from the start LCN to discover the full data runlist.
        var firstRecordOffset = volumeData.MftStartLcn * bytesPerCluster;
        var firstRecord = ReadVolumeBytes(volumeHandle, firstRecordOffset, bytesPerRecord, alignTo: bytesPerSector);
        if (!NtfsMftRecordParser.TryApplyUpdateSequence(firstRecord, bytesPerSector))
        {
            throw new InvalidDataException("Failed to apply update sequence on $MFT record 0.");
        }

        if (!NtfsMftDataRuns.TryGetUnnamedDataRuns(firstRecord, out var runs, out var validDataLength)
            || runs.Count == 0
            || validDataLength <= 0)
        {
            // Fall back to contiguous MFT zone when runlist is unavailable.
            validDataLength = volumeData.MftValidDataLength;
            if (validDataLength <= 0)
            {
                throw new InvalidDataException("NTFS $MFT valid data length is invalid.");
            }

            if (validDataLength > MaxMftBytesInMemory)
            {
                throw new InvalidDataException(
                    $"NTFS $MFT is too large to load into memory ({validDataLength} bytes).");
            }

            var contiguous = ReadVolumeBytes(
                volumeHandle,
                firstRecordOffset,
                AlignUp((int)Math.Min(validDataLength, int.MaxValue), bytesPerSector),
                alignTo: bytesPerSector);
            if (contiguous.Length > validDataLength)
            {
                Array.Resize(ref contiguous, (int)validDataLength);
            }

            return contiguous;
        }

        if (validDataLength > MaxMftBytesInMemory)
        {
            throw new InvalidDataException(
                $"NTFS $MFT is too large to load into memory ({validDataLength} bytes).");
        }

        var mftLength = (int)Math.Min(validDataLength, int.MaxValue);
        var buffer = new byte[mftLength];
        long written = 0;
        foreach (var run in runs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var runBytes = run.ClusterCount * bytesPerCluster;
            var remaining = mftLength - written;
            if (remaining <= 0)
            {
                break;
            }

            var toCopy = (int)Math.Min(runBytes, remaining);
            if (run.IsSparse)
            {
                // Sparse runs contribute zeroes; buffer is already zeroed.
                written += toCopy;
                continue;
            }

            var runOffset = run.StartLcn * bytesPerCluster;
            var alignedReadLength = AlignUp(toCopy, bytesPerSector);
            var chunk = ReadVolumeBytes(volumeHandle, runOffset, alignedReadLength, alignTo: bytesPerSector);
            var copyLength = Math.Min(toCopy, chunk.Length);
            Buffer.BlockCopy(chunk, 0, buffer, (int)written, copyLength);
            written += copyLength;
        }

        if (written < bytesPerRecord)
        {
            throw new InvalidDataException("NTFS $MFT read returned fewer bytes than one file record.");
        }

        if (written < mftLength)
        {
            Array.Resize(ref buffer, (int)written);
        }

        return buffer;
    }

    private static byte[] ReadVolumeBytes(IntPtr volumeHandle, long absoluteOffset, int length, int alignTo)
    {
        if (length <= 0)
        {
            return Array.Empty<byte>();
        }

        var alignedOffset = absoluteOffset - (absoluteOffset % alignTo);
        var skip = (int)(absoluteOffset - alignedOffset);
        var alignedLength = AlignUp(skip + length, alignTo);
        var buffer = new byte[alignedLength];

        if (!NtfsNativeMethods.SetFilePointerEx(
                volumeHandle,
                alignedOffset,
                out _,
                NtfsNativeMethods.FileBegin))
        {
            throw CreateWin32Exception("Failed to seek NTFS volume for $MFT read.");
        }

        var totalRead = 0;
        while (totalRead < alignedLength)
        {
            var chunkSize = alignedLength - totalRead;
            var chunk = totalRead == 0 ? buffer : new byte[chunkSize];
            if (!NtfsNativeMethods.ReadFile(
                    volumeHandle,
                    chunk,
                    chunkSize,
                    out var bytesRead,
                    IntPtr.Zero))
            {
                if (totalRead > skip)
                {
                    break;
                }

                throw CreateWin32Exception("Failed to read NTFS volume for $MFT.");
            }

            if (bytesRead <= 0)
            {
                break;
            }

            if (totalRead == 0)
            {
                totalRead = bytesRead;
            }
            else
            {
                Buffer.BlockCopy(chunk, 0, buffer, totalRead, bytesRead);
                totalRead += bytesRead;
            }
        }

        var available = Math.Max(0, totalRead - skip);
        var resultLength = Math.Min(length, available);
        if (resultLength <= 0)
        {
            throw new InvalidDataException("NTFS volume read returned no data for the requested $MFT range.");
        }

        if (skip == 0 && resultLength == buffer.Length)
        {
            return buffer;
        }

        var result = new byte[resultLength];
        Buffer.BlockCopy(buffer, skip, result, 0, resultLength);
        return result;
    }

    private static NtfsNativeMethods.NtfsVolumeDataBuffer QueryVolumeData(IntPtr handle)
    {
        var size = Marshal.SizeOf<NtfsNativeMethods.NtfsVolumeDataBuffer>();
        var success = NtfsNativeMethods.DeviceIoControlGetNtfsVolumeData(
            handle,
            NtfsNativeMethods.FsctlGetNtfsVolumeData,
            IntPtr.Zero,
            0,
            out var volumeData,
            size,
            out _,
            IntPtr.Zero);

        if (!success)
        {
            throw CreateWin32Exception("Failed to query NTFS volume data.");
        }

        return volumeData;
    }

    private static void ValidateVolumeData(NtfsNativeMethods.NtfsVolumeDataBuffer volumeData)
    {
        if (volumeData.BytesPerSector == 0
            || volumeData.BytesPerCluster == 0
            || volumeData.BytesPerFileRecordSegment == 0
            || volumeData.MftStartLcn < 0)
        {
            throw new InvalidDataException("NTFS volume data is incomplete.");
        }
    }

    private static IntPtr OpenVolume(string volumePath)
    {
        var handle = NtfsNativeMethods.CreateFileW(
            volumePath,
            NtfsNativeMethods.GenericRead,
            NtfsNativeMethods.FileShareRead | NtfsNativeMethods.FileShareWrite | NtfsNativeMethods.FileShareDelete,
            IntPtr.Zero,
            NtfsNativeMethods.OpenExisting,
            0,
            IntPtr.Zero);

        if (handle == NtfsNativeMethods.InvalidHandleValue)
        {
            throw CreateWin32Exception("Failed to open NTFS volume for MFT scan.");
        }

        return handle;
    }

    private static bool IsExcludedByRules(string fullPath, bool isDirectory, IndexExclusionRules exclusionRules)
    {
        var normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(fullPath));
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

    private static bool IsNtfsMetadataPath(string fullPath)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(fullPath));
        return name.StartsWith('$');
    }

    private static int AlignUp(int value, int alignment)
    {
        if (alignment <= 1)
        {
            return value;
        }

        var remainder = value % alignment;
        return remainder == 0 ? value : value + (alignment - remainder);
    }

    private static Win32Exception CreateWin32Exception(string message)
    {
        return new Win32Exception(Marshal.GetLastWin32Error(), message);
    }
}

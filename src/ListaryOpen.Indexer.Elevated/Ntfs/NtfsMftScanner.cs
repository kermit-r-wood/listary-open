using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ListaryOpen.Core.Indexing;
using ListaryOpen.Infrastructure.Indexing;

namespace ListaryOpen.Indexer.Elevated.Ntfs;

/// <summary>
/// Full MFT scanner: sequential $MFT stream parse, all FILE_NAME attributes (hard links),
/// path expansion in memory, no per-file CreateFile for metadata.
/// Raw MFT bytes are processed in chunks and discarded so peak memory tracks the
/// parsed record map rather than the full raw $MFT image.
/// </summary>
internal sealed class NtfsMftScanner
{
    /// <summary>Stream chunk size: multiple of typical 1 KiB records, keeps parse locality high.</summary>
    private const int MftStreamChunkBytes = 4 * 1024 * 1024;
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

            // Stream-parse $MFT into a lightweight record map without retaining raw bytes.
            var parsedByRecord = StreamParseMftRecords(handle, volumeData, cancellationToken);
            foreach (var record in ProjectParsedRecords(
                         parsedByRecord,
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

                if (volumeData.MftValidDataLength <= 0)
                {
                    error = "NTFS $MFT valid data length is invalid.";
                    return false;
                }

                // Large $MFT images are streamed in chunks; size alone is not a disqualifier.
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

    /// <summary>Test helper: parse a contiguous in-memory $MFT image (non-streaming).</summary>
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

        foreach (var record in ProjectParsedRecords(
                     parsedByRecord,
                     volumeRoot,
                     requestedRoot,
                     exclusionRules,
                     cancellationToken))
        {
            yield return record;
        }
    }

    internal static IEnumerable<FileRecord> ProjectParsedRecords(
        IReadOnlyDictionary<ulong, NtfsMftParsedRecord> parsedByRecord,
        string volumeRoot,
        string requestedRoot,
        IndexExclusionRules exclusionRules,
        CancellationToken cancellationToken)
    {
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

    /// <summary>
    /// Streams $MFT data runs in chunks, parsing FILE records into a map and discarding
    /// raw bytes so peak memory is dominated by the parsed map rather than the full image.
    /// </summary>
    internal static Dictionary<ulong, NtfsMftParsedRecord> StreamParseMftRecords(
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

        var firstRecordOffset = volumeData.MftStartLcn * bytesPerCluster;
        var firstRecord = ReadVolumeBytes(volumeHandle, firstRecordOffset, bytesPerRecord, alignTo: bytesPerSector);
        if (!NtfsMftRecordParser.TryApplyUpdateSequence(firstRecord, bytesPerSector))
        {
            throw new InvalidDataException("Failed to apply update sequence on $MFT record 0.");
        }

        IReadOnlyList<NtfsDataRun> runs;
        long validDataLength;
        if (!NtfsMftDataRuns.TryGetUnnamedDataRuns(firstRecord, out runs, out validDataLength)
            || runs.Count == 0
            || validDataLength <= 0)
        {
            validDataLength = volumeData.MftValidDataLength;
            if (validDataLength <= 0)
            {
                throw new InvalidDataException("NTFS $MFT valid data length is invalid.");
            }

            runs = new[]
            {
                new NtfsDataRun(
                    volumeData.MftStartLcn,
                    (validDataLength + bytesPerCluster - 1) / bytesPerCluster,
                    IsSparse: false)
            };
        }

        // Stream $MFT data runs; peak memory is dominated by the parsed record map,
        // not the raw $MFT image size.
        var estimatedRecords = (int)Math.Min(validDataLength / Math.Max(bytesPerRecord, 1), int.MaxValue);
        var parsedByRecord = new Dictionary<ulong, NtfsMftParsedRecord>(Math.Max(16, estimatedRecords / 2));
        long bytesConsumed = 0;
        ulong nextRecordNumber = 0;
        var carry = Array.Empty<byte>();
        var carryLength = 0;
        var chunkSize = AlignUp(Math.Max(MftStreamChunkBytes, bytesPerRecord), bytesPerRecord);

        foreach (var run in runs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (bytesConsumed >= validDataLength)
            {
                break;
            }

            var runBytes = run.ClusterCount * bytesPerCluster;
            var remainingValid = validDataLength - bytesConsumed;
            var runReadable = (int)Math.Min(runBytes, remainingValid);
            if (runReadable <= 0)
            {
                break;
            }

            if (run.IsSparse)
            {
                // Sparse $MFT runs are unexpected for in-use FILE records; skip zeros.
                bytesConsumed += runReadable;
                nextRecordNumber += (ulong)(runReadable / bytesPerRecord);
                continue;
            }

            long runFileOffset = 0;
            while (runFileOffset < runReadable)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var toRead = (int)Math.Min(chunkSize, runReadable - runFileOffset);
                var absoluteOffset = (run.StartLcn * bytesPerCluster) + runFileOffset;
                var chunk = ReadVolumeBytes(volumeHandle, absoluteOffset, toRead, alignTo: bytesPerSector);
                var usable = Math.Min(toRead, chunk.Length);
                if (usable <= 0)
                {
                    break;
                }

                // Merge with carry so records spanning chunk boundaries stay intact.
                byte[] work;
                int workLength;
                if (carryLength == 0)
                {
                    work = chunk;
                    workLength = usable;
                }
                else
                {
                    work = new byte[carryLength + usable];
                    Buffer.BlockCopy(carry, 0, work, 0, carryLength);
                    Buffer.BlockCopy(chunk, 0, work, carryLength, usable);
                    workLength = carryLength + usable;
                }

                var completeBytes = workLength - (workLength % bytesPerRecord);
                if (completeBytes > 0)
                {
                    ParseRecordChunk(
                        work.AsSpan(0, completeBytes),
                        bytesPerRecord,
                        bytesPerSector,
                        nextRecordNumber,
                        parsedByRecord,
                        cancellationToken);
                    nextRecordNumber += (ulong)(completeBytes / bytesPerRecord);
                }

                var leftover = workLength - completeBytes;
                if (leftover > 0)
                {
                    if (carry.Length < leftover)
                    {
                        carry = new byte[leftover];
                    }

                    Buffer.BlockCopy(work, completeBytes, carry, 0, leftover);
                    carryLength = leftover;
                }
                else
                {
                    carryLength = 0;
                }

                bytesConsumed += usable;
                runFileOffset += usable;
            }
        }

        if (carryLength >= bytesPerRecord)
        {
            var completeBytes = carryLength - (carryLength % bytesPerRecord);
            ParseRecordChunk(
                carry.AsSpan(0, completeBytes),
                bytesPerRecord,
                bytesPerSector,
                nextRecordNumber,
                parsedByRecord,
                cancellationToken);
        }

        if (parsedByRecord.Count == 0)
        {
            throw new InvalidDataException("NTFS $MFT stream parse produced no in-use records.");
        }

        return parsedByRecord;
    }

    private static void ParseRecordChunk(
        ReadOnlySpan<byte> chunk,
        int bytesPerRecord,
        int bytesPerSector,
        ulong startingRecordNumber,
        Dictionary<ulong, NtfsMftParsedRecord> parsedByRecord,
        CancellationToken cancellationToken)
    {
        // One mutable copy per stream chunk so update-sequence can patch in place.
        var mutable = chunk.ToArray();
        var recordCount = mutable.Length / bytesPerRecord;
        for (var index = 0; index < recordCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var recordSpan = mutable.AsSpan(index * bytesPerRecord, bytesPerRecord);
            if (!NtfsMftRecordParser.TryApplyUpdateSequence(recordSpan, bytesPerSector))
            {
                continue;
            }

            var recordNumber = startingRecordNumber + (ulong)index;
            if (!NtfsMftRecordParser.TryParse(recordSpan, recordNumber, out var parsed) || !parsed.InUse)
            {
                continue;
            }

            parsedByRecord[parsed.RecordNumber] = parsed;
        }
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

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
    private readonly record struct ResolvedDirectory(string FullPath, bool IsExcluded);
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

            // Project each bounded parse batch immediately. The old implementation
            // retained every parsed FILE record (millions on a large volume) and then
            // constructed a second link collection before producing the first result.
            // Streaming keeps only directory ancestry plus genuinely unresolved links.
            var projection = new StreamingProjection(
                scanRoot.VolumeRoot,
                scanRoot.RequestedRoot,
                rules,
                cancellationToken);
            foreach (var parsedBatch in StreamParseMftRecordBatches(handle, volumeData, cancellationToken))
            {
                var projected = new List<FileRecord>(parsedBatch.Count);
                foreach (var parsed in parsedBatch)
                {
                    projection.Accept(parsed, projected);
                }

                foreach (var record in projected)
                {
                    yield return record;
                }
            }

            foreach (var projectedBatch in projection.CompleteInBatches())
            {
                foreach (var record in projectedBatch)
                {
                    yield return record;
                }
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

        var projection = new StreamingProjection(
            volumeRoot,
            requestedRoot,
            exclusionRules,
            cancellationToken);
        foreach (var parsed in parsedByRecord.Values)
        {
            var output = new List<FileRecord>();
            projection.Accept(parsed, output);
            foreach (var record in output)
            {
                yield return record;
            }
        }

        foreach (var output in projection.CompleteInBatches())
        {
            foreach (var record in output)
            {
                yield return record;
            }
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

    /// <summary>
    /// Turns parsed records into index records without retaining the complete MFT.
    /// Directory records are normally allocated before their children, so almost all
    /// links can be resolved and emitted in the same bounded batch. Links whose parent
    /// appears later (for example after a directory move) are retried after the scan.
    /// </summary>
    private sealed class StreamingProjection
    {
        private const int CompletionBatchSize = 4096;

        private readonly string _volumeRoot;
        private readonly string _requestedRoot;
        private readonly string _requestedRootWithSeparator;
        private readonly bool _includesWholeVolume;
        private readonly IndexExclusionRules _exclusionRules;
        private readonly CancellationToken _cancellationToken;
        private readonly Dictionary<ulong, DirectoryNode> _directories = new();
        private readonly Dictionary<ulong, ResolvedDirectory> _resolvedDirectories = new();
        private readonly Dictionary<ulong, NtfsMftParsedRecord> _extensionRecords = new();
        private readonly List<NtfsMftParsedRecord> _deferredBaseRecords = new();
        private readonly List<NameLink> _unresolvedLinks = new();

        public StreamingProjection(
            string volumeRoot,
            string requestedRoot,
            IndexExclusionRules exclusionRules,
            CancellationToken cancellationToken)
        {
            _volumeRoot = Path.GetFullPath(volumeRoot);
            _requestedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requestedRoot));
            _requestedRootWithSeparator = _requestedRoot.EndsWith(Path.DirectorySeparatorChar)
                ? _requestedRoot
                : _requestedRoot + Path.DirectorySeparatorChar;
            _includesWholeVolume = string.Equals(
                Path.TrimEndingDirectorySeparator(_volumeRoot),
                _requestedRoot,
                StringComparison.OrdinalIgnoreCase);
            _exclusionRules = exclusionRules;
            _cancellationToken = cancellationToken;

            var root = new DirectoryNode(RootDirectoryRecordNumber, string.Empty);
            _directories[RootDirectoryRecordNumber] = root;
            _resolvedDirectories[RootDirectoryRecordNumber] = new ResolvedDirectory(_volumeRoot, IsExcluded: false);
        }

        public void Accept(NtfsMftParsedRecord parsed, List<FileRecord> output)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (!parsed.IsBaseRecord)
            {
                if (parsed.FileNames.Count > 0)
                {
                    _extensionRecords[parsed.RecordNumber] = parsed;
                }

                return;
            }

            // Attribute-list extension records can appear on either side of the base
            // record. They are rare, so defer only those bases until all extensions are known.
            if (parsed.AttributeListFileReferences.Count > 0)
            {
                _deferredBaseRecords.Add(parsed);
                return;
            }

            RegisterDirectory(parsed, parsed.FileNames);
            AppendLinks(parsed, parsed.FileNames, output, deferIfUnresolved: true);
        }

        public IEnumerable<IReadOnlyList<FileRecord>> CompleteInBatches()
        {
            var deferred = new List<(NtfsMftParsedRecord Record, IReadOnlyList<NtfsMftFileName> Names)>(
                _deferredBaseRecords.Count);
            foreach (var parsed in _deferredBaseRecords)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                var names = CollectDeferredNames(parsed);
                deferred.Add((parsed, names));
                RegisterDirectory(parsed, names);
            }

            var output = new List<FileRecord>(CompletionBatchSize);
            foreach (var item in deferred)
            {
                AppendLinks(item.Record, item.Names, output, deferIfUnresolved: false);
                if (output.Count >= CompletionBatchSize)
                {
                    yield return output;
                    output = new List<FileRecord>(CompletionBatchSize);
                }
            }

            foreach (var link in _unresolvedLinks)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                TryAppendLink(link, output, deferIfUnresolved: false);
                if (output.Count >= CompletionBatchSize)
                {
                    yield return output;
                    output = new List<FileRecord>(CompletionBatchSize);
                }
            }

            if (output.Count > 0)
            {
                yield return output;
            }
        }

        private IReadOnlyList<NtfsMftFileName> CollectDeferredNames(NtfsMftParsedRecord baseRecord)
        {
            var names = new List<NtfsMftFileName>(baseRecord.FileNames.Count + 2);
            names.AddRange(baseRecord.FileNames);
            foreach (var extensionRef in baseRecord.AttributeListFileReferences)
            {
                if (_extensionRecords.TryGetValue(extensionRef, out var extension)
                    && extension.RecordNumber != baseRecord.RecordNumber)
                {
                    names.AddRange(extension.FileNames);
                }
            }

            return names;
        }

        private void RegisterDirectory(
            NtfsMftParsedRecord parsed,
            IReadOnlyList<NtfsMftFileName> names)
        {
            if (!parsed.IsDirectory)
            {
                return;
            }

            if (TryGetPrimaryIndexableName(names, out var primary))
            {
                _directories[parsed.RecordNumber] = new DirectoryNode(
                    NtfsMftRecordParser.GetMftSegmentReferenceNumber(primary.ParentFileReferenceNumber),
                    primary.Name);
            }
            else if (parsed.RecordNumber == RootDirectoryRecordNumber)
            {
                _directories[parsed.RecordNumber] = new DirectoryNode(parsed.RecordNumber, string.Empty);
            }
        }

        private void AppendLinks(
            NtfsMftParsedRecord parsed,
            IReadOnlyList<NtfsMftFileName> names,
            List<FileRecord> output,
            bool deferIfUnresolved)
        {
            var hasWin32Name = HasWin32Name(names);
            foreach (var name in names)
            {
                if (!IsIndexableName(name, hasWin32Name))
                {
                    continue;
                }

                var link = new NameLink(
                    parsed.RecordNumber,
                    NtfsMftRecordParser.GetMftSegmentReferenceNumber(name.ParentFileReferenceNumber),
                    name.Name,
                    parsed.IsDirectory,
                    parsed.SizeBytes,
                    parsed.LastWriteTime);
                TryAppendLink(link, output, deferIfUnresolved);
            }
        }

        private void TryAppendLink(NameLink link, List<FileRecord> output, bool deferIfUnresolved)
        {
            if (!TryBuildPath(link, out var fullPath, out var isExcluded))
            {
                if (deferIfUnresolved)
                {
                    _unresolvedLinks.Add(link);
                }

                return;
            }

            if (isExcluded
                || link.Name.StartsWith('$')
                || !IsRequestedRootOrDescendant(fullPath))
            {
                return;
            }

            try
            {
                output.Add(FileRecord.CreateFromNormalizedPath(
                    fullPath,
                    link.IsDirectory,
                    link.SizeBytes,
                    link.LastWriteTime,
                    link.RecordNumber & 0x0000_FFFF_FFFF_FFFFUL));
            }
            catch (ArgumentException)
            {
                // Ignore malformed names in damaged or transient FILE records.
            }
        }

        private bool TryBuildPath(NameLink link, out string fullPath, out bool isExcluded)
        {
            fullPath = string.Empty;
            isExcluded = false;
            if (string.IsNullOrWhiteSpace(link.Name) && link.RecordNumber != RootDirectoryRecordNumber)
            {
                return false;
            }

            if (link.RecordNumber == RootDirectoryRecordNumber && link.IsDirectory)
            {
                fullPath = _volumeRoot;
                _resolvedDirectories[link.RecordNumber] = new ResolvedDirectory(fullPath, IsExcluded: false);
                return true;
            }

            if (!TryResolveDirectoryPath(link.ParentRecordNumber, resolving: null, out var parent))
            {
                return false;
            }

            fullPath = string.IsNullOrEmpty(link.Name)
                ? parent.FullPath
                : Path.Combine(parent.FullPath, link.Name);
            isExcluded = parent.IsExcluded
                || (link.IsDirectory && _exclusionRules.ShouldExcludeDirectoryName(link.Name));

            if (link.IsDirectory)
            {
                _resolvedDirectories[link.RecordNumber] = new ResolvedDirectory(fullPath, isExcluded);
            }

            return true;
        }

        private bool TryResolveDirectoryPath(
            ulong recordNumber,
            HashSet<ulong>? resolving,
            out ResolvedDirectory resolved)
        {
            if (_resolvedDirectories.TryGetValue(recordNumber, out resolved))
            {
                return true;
            }

            if (recordNumber == RootDirectoryRecordNumber)
            {
                resolved = new ResolvedDirectory(_volumeRoot, IsExcluded: false);
                _resolvedDirectories[recordNumber] = resolved;
                return true;
            }

            if (!_directories.TryGetValue(recordNumber, out var node))
            {
                resolved = default;
                return false;
            }

            resolving ??= new HashSet<ulong>();
            if (!resolving.Add(recordNumber))
            {
                resolved = default;
                return false;
            }

            try
            {
                if (node.ParentRecordNumber == recordNumber)
                {
                    resolved = new ResolvedDirectory(_volumeRoot, IsExcluded: false);
                    _resolvedDirectories[recordNumber] = resolved;
                    return true;
                }

                if (!TryResolveDirectoryPath(node.ParentRecordNumber, resolving, out var parent))
                {
                    resolved = default;
                    return false;
                }

                var fullPath = string.IsNullOrEmpty(node.Name)
                    ? parent.FullPath
                    : Path.Combine(parent.FullPath, node.Name);
                resolved = new ResolvedDirectory(
                    fullPath,
                    parent.IsExcluded || _exclusionRules.ShouldExcludeDirectoryName(node.Name));
                _resolvedDirectories[recordNumber] = resolved;
                return true;
            }
            finally
            {
                resolving.Remove(recordNumber);
            }
        }

        private bool IsRequestedRootOrDescendant(string fullPath)
        {
            return _includesWholeVolume
                || string.Equals(fullPath, _requestedRoot, StringComparison.OrdinalIgnoreCase)
                || fullPath.StartsWith(_requestedRootWithSeparator, StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryGetPrimaryIndexableName(
            IReadOnlyList<NtfsMftFileName> names,
            out NtfsMftFileName primary)
        {
            var hasWin32Name = HasWin32Name(names);
            foreach (var name in names)
            {
                if (IsIndexableName(name, hasWin32Name))
                {
                    primary = name;
                    return true;
                }
            }

            primary = default;
            return false;
        }

        private static bool HasWin32Name(IReadOnlyList<NtfsMftFileName> names)
        {
            foreach (var name in names)
            {
                if (name.Namespace is NtfsFileNameNamespace.Win32 or NtfsFileNameNamespace.Win32AndDos)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsIndexableName(NtfsMftFileName name, bool hasWin32Name)
        {
            return hasWin32Name
                ? name.Namespace is NtfsFileNameNamespace.Win32 or NtfsFileNameNamespace.Win32AndDos
                : name.Namespace != NtfsFileNameNamespace.Dos;
        }
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
    /// Streams $MFT data runs as bounded batches. A batch owns the parsed objects only
    /// until its records have been projected, keeping memory independent of file count.
    /// </summary>
    private static IEnumerable<IReadOnlyList<NtfsMftParsedRecord>> StreamParseMftRecordBatches(
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

        long bytesConsumed = 0;
        ulong nextRecordNumber = 0;
        long parsedRecordCount = 0;
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
            // A large NTFS volume commonly has a single $MFT extent above 2 GiB.
            // Keep the extent length 64-bit and narrow only each bounded chunk;
            // casting the full extent to int overflowed and silently abandoned the
            // fast MFT scan, forcing a very expensive recursive directory fallback.
            var runReadable = GetReadableRunLength(runBytes, remainingValid);
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
                    var parsedBatch = ParseRecordChunk(
                        work,
                        completeBytes,
                        bytesPerRecord,
                        bytesPerSector,
                        nextRecordNumber,
                        cancellationToken);
                    parsedRecordCount += parsedBatch.Count;
                    nextRecordNumber += (ulong)(completeBytes / bytesPerRecord);
                    if (parsedBatch.Count > 0)
                    {
                        yield return parsedBatch;
                    }
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
            var parsedBatch = ParseRecordChunk(
                carry,
                completeBytes,
                bytesPerRecord,
                bytesPerSector,
                nextRecordNumber,
                cancellationToken);
            parsedRecordCount += parsedBatch.Count;
            if (parsedBatch.Count > 0)
            {
                yield return parsedBatch;
            }
        }

        if (parsedRecordCount == 0)
        {
            throw new InvalidDataException("NTFS $MFT stream parse produced no in-use records.");
        }
    }

    internal static long GetReadableRunLength(long runBytes, long remainingValid)
    {
        if (runBytes <= 0 || remainingValid <= 0)
        {
            return 0;
        }

        return Math.Min(runBytes, remainingValid);
    }

    private static List<NtfsMftParsedRecord> ParseRecordChunk(
        byte[] chunk,
        int chunkLength,
        int bytesPerRecord,
        int bytesPerSector,
        ulong startingRecordNumber,
        CancellationToken cancellationToken)
    {
        // ReadVolumeBytes already returns a private mutable buffer. Apply update
        // sequences in place instead of copying every 4 MiB chunk a second time.
        var recordCount = chunkLength / bytesPerRecord;
        var parsedRecords = new List<NtfsMftParsedRecord>(Math.Max(16, recordCount / 2));
        for (var index = 0; index < recordCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var recordSpan = chunk.AsSpan(index * bytesPerRecord, bytesPerRecord);
            if (!IsInUseFileRecord(recordSpan))
            {
                continue;
            }

            if (!NtfsMftRecordParser.TryApplyUpdateSequence(recordSpan, bytesPerSector))
            {
                continue;
            }

            var recordNumber = startingRecordNumber + (ulong)index;
            if (!NtfsMftRecordParser.TryParse(recordSpan, recordNumber, out var parsed) || !parsed.InUse)
            {
                continue;
            }

            parsedRecords.Add(parsed);
        }

        return parsedRecords;
    }

    private static bool IsInUseFileRecord(ReadOnlySpan<byte> record)
    {
        return record.Length >= 0x18
            && record[0] == (byte)'F'
            && record[1] == (byte)'I'
            && record[2] == (byte)'L'
            && record[3] == (byte)'E'
            && (record[0x16] & 0x01) != 0;
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

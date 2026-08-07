using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ListaryOpen.Core.Indexing;
using ListaryOpen.Infrastructure.Indexing.Ntfs;

namespace ListaryOpen.Indexer.Elevated.Ntfs;

public sealed class NtfsUsnJournalReader
{
    private const uint UsnReasonDataOverwrite = 0x0000_0001;
    private const uint UsnReasonDataExtend = 0x0000_0002;
    private const uint UsnReasonDataTruncation = 0x0000_0004;
    private const uint UsnReasonNamedDataOverwrite = 0x0000_0010;
    private const uint UsnReasonNamedDataExtend = 0x0000_0020;
    private const uint UsnReasonNamedDataTruncation = 0x0000_0040;
    private const uint UsnReasonFileCreate = 0x0000_0100;
    private const uint UsnReasonFileDelete = 0x0000_0200;
    private const uint UsnReasonBasicInfoChange = 0x0000_8000;
    private const uint UsnReasonRenameOldName = 0x0000_1000;
    private const uint UsnReasonRenameNewName = 0x0000_2000;
    private const uint UsnReasonHardLinkChange = 0x0001_0000;
    private const uint UsnReasonClose = 0x8000_0000;
    private const uint UsnReasonUpsertMask = UsnReasonDataOverwrite
        | UsnReasonDataExtend
        | UsnReasonDataTruncation
        | UsnReasonNamedDataOverwrite
        | UsnReasonNamedDataExtend
        | UsnReasonNamedDataTruncation
        | UsnReasonFileCreate
        | UsnReasonBasicInfoChange
        | UsnReasonRenameNewName;
    private const uint UsnReasonDeleteMask = UsnReasonFileDelete | UsnReasonRenameOldName;
    private const int UsnOutputPrefixLength = sizeof(ulong);
    private const int UsnRecordV2HeaderLength = 60;
    private const int UsnBufferLength = 1024 * 1024;

    public async IAsyncEnumerable<FileRecord> EnumerateVolumeAsync(
        string volumeRoot,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Prefer full MFT enumeration: includes every hard-link FILE_NAME and avoids
        // per-file CreateFile metadata lookups. Fall back to FSCTL_ENUM_USN_DATA on failure.
        // Probe and obtain the first record before yielding so a parse/read failure can
        // still use the fast USN enumerator instead of forcing the coordinator into a
        // recursive DirectoryInfo compatibility scan.
        if (NtfsMftScanner.TryValidateVolume(volumeRoot, out _))
        {
            var mftScanner = new NtfsMftScanner();
            IAsyncEnumerator<FileRecord>? enumerator = null;
            FileRecord? firstRecord = null;
            try
            {
                enumerator = mftScanner
                    .EnumerateVolumeAsync(volumeRoot, volumeRoot, cancellationToken)
                    .GetAsyncEnumerator(cancellationToken);
                if (await enumerator.MoveNextAsync().ConfigureAwait(false))
                {
                    firstRecord = enumerator.Current;
                }
            }
            catch (OperationCanceledException)
            {
                if (enumerator is not null)
                {
                    await enumerator.DisposeAsync().ConfigureAwait(false);
                }

                throw;
            }
            catch (Exception exception) when (IsExpectedMftFailure(exception))
            {
                if (enumerator is not null)
                {
                    await enumerator.DisposeAsync().ConfigureAwait(false);
                }

                enumerator = null;
            }

            if (enumerator is not null && firstRecord is not null)
            {
                await using (enumerator.ConfigureAwait(false))
                {
                    yield return firstRecord;
                    while (await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        yield return enumerator.Current;
                    }
                }

                yield break;
            }

            if (enumerator is not null)
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }
        }

        await foreach (var record in EnumerateVolumeViaUsnEnumAsync(volumeRoot, cancellationToken)
                           .ConfigureAwait(false))
        {
            yield return record;
        }
    }

    private static bool IsExpectedMftFailure(Exception exception) =>
        exception is InvalidDataException
            or Win32Exception
            or IOException
            or UnauthorizedAccessException
            or ArgumentException;

    private async IAsyncEnumerable<FileRecord> EnumerateVolumeViaUsnEnumAsync(
        string volumeRoot,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        var scanRoot = NtfsScanRoot.Create(volumeRoot);
        var handle = OpenVolume(scanRoot.VolumePath);

        try
        {
            var journalData = QueryJournal(handle);
            var volumeRootFileReferenceNumber = ReadFileReferenceNumber(scanRoot.VolumeRoot);
            var directories = ReadDirectoryEntries(handle, journalData, cancellationToken);
            foreach (var record in NtfsUsnRecordProjector.CreateFileRecordsFromDirectoryMap(
                         scanRoot.VolumeRoot,
                         scanRoot.RequestedRoot,
                         directories,
                         EnumerateEntries(handle, journalData, cancellationToken),
                         new NtfsFileMetadataReader(),
                         cancellationToken,
                         volumeRootFileReferenceNumber,
                         failOnSkippedRecords: true))
            {
                yield return record;
            }
        }
        finally
        {
            NtfsNativeMethods.CloseHandle(handle);
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
            throw CreateWin32Exception("Failed to open NTFS volume.");
        }

        return handle;
    }

    private static ulong ReadFileReferenceNumber(string path)
    {
        var handle = NtfsNativeMethods.CreateFileW(
            path,
            NtfsNativeMethods.GenericRead,
            NtfsNativeMethods.FileShareRead | NtfsNativeMethods.FileShareWrite | NtfsNativeMethods.FileShareDelete,
            IntPtr.Zero,
            NtfsNativeMethods.OpenExisting,
            NtfsNativeMethods.FileFlagBackupSemantics,
            IntPtr.Zero);

        if (handle == NtfsNativeMethods.InvalidHandleValue)
        {
            throw CreateWin32Exception("Failed to open NTFS volume root.");
        }

        try
        {
            if (!NtfsNativeMethods.GetFileInformationByHandle(handle, out var information))
            {
                throw CreateWin32Exception("Failed to read NTFS volume root file reference.");
            }

            return ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow;
        }
        finally
        {
            NtfsNativeMethods.CloseHandle(handle);
        }
    }

    public UsnJournalState ReadJournalState(string volumeRoot)
    {
        var scanRoot = NtfsScanRoot.Create(volumeRoot);
        var handle = OpenVolume(scanRoot.VolumePath);

        try
        {
            var journalData = QueryJournal(handle);
            return new UsnJournalState(
                journalData.UsnJournalId,
                journalData.LowestValidUsn,
                journalData.NextUsn);
        }
        finally
        {
            NtfsNativeMethods.CloseHandle(handle);
        }
    }

    public async IAsyncEnumerable<UsnJournalChange> EnumerateChangesAsync(
        string volumeRoot,
        ulong expectedUsnJournalId,
        long startUsn,
        long endUsn,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        var scanRoot = NtfsScanRoot.Create(volumeRoot);
        var handle = OpenVolume(scanRoot.VolumePath);

        try
        {
            var journalData = QueryJournal(handle);
            if (RequiresFullRescanForJournalSnapshot(journalData, expectedUsnJournalId, endUsn))
            {
                yield return UsnJournalChange.DirectoryRenameOrMove();
                yield break;
            }

            var volumeRootFileReferenceNumber = ReadFileReferenceNumber(scanRoot.VolumeRoot);
            var directories = ReadDirectoryEntries(handle, journalData, cancellationToken);
            var metadataReader = new NtfsFileMetadataReader();
            var resolvedPaths = new Dictionary<ulong, string>();
            var volumeGeometry = TryQueryVolumeGeometry(handle);

            foreach (var entry in EnumerateJournalEntries(handle, journalData, startUsn, endUsn, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var change in CreateJournalChanges(
                             handle,
                             volumeGeometry,
                             scanRoot,
                             entry,
                             directories,
                             resolvedPaths,
                             metadataReader,
                             volumeRootFileReferenceNumber,
                             cancellationToken))
                {
                    yield return change;
                }
            }
        }
        finally
        {
            NtfsNativeMethods.CloseHandle(handle);
        }
    }

    private static Dictionary<ulong, NtfsUsnEntry> ReadDirectoryEntries(
        IntPtr handle,
        NtfsNativeMethods.UsnJournalDataV0 journalData,
        CancellationToken cancellationToken)
    {
        var entries = new Dictionary<ulong, NtfsUsnEntry>();
        foreach (var entry in EnumerateEntries(handle, journalData, cancellationToken))
        {
            if (entry.IsDirectory)
            {
                // Key by MFT segment (low 48 bits) so parent lookups match projector masking.
                entries[entry.FileReferenceNumber & 0x0000_FFFF_FFFF_FFFFUL] = entry;
            }
        }

        return entries;
    }

    internal static NtfsNativeMethods.MftEnumDataV0 CreateFullScanEnumData(
        NtfsNativeMethods.UsnJournalDataV0 journalData)
    {
        return new NtfsNativeMethods.MftEnumDataV0
        {
            StartFileReferenceNumber = 0,
            LowUsn = 0,
            HighUsn = journalData.NextUsn
        };
    }

    internal static NtfsNativeMethods.ReadUsnJournalDataV0 CreateReadJournalData(
        long startUsn,
        ulong usnJournalId)
    {
        return new NtfsNativeMethods.ReadUsnJournalDataV0
        {
            StartUsn = startUsn,
            ReasonMask = uint.MaxValue,
            ReturnOnlyOnClose = 0,
            Timeout = 0,
            BytesToWaitFor = 0,
            UsnJournalId = usnJournalId
        };
    }

    internal static bool RequiresFullRescanForJournalSnapshot(
        NtfsNativeMethods.UsnJournalDataV0 journalData,
        ulong expectedUsnJournalId,
        long endUsn)
    {
        return journalData.UsnJournalId != expectedUsnJournalId
            || journalData.NextUsn < endUsn;
    }

    private static IEnumerable<NtfsUsnEntry> EnumerateEntries(
        IntPtr handle,
        NtfsNativeMethods.UsnJournalDataV0 journalData,
        CancellationToken cancellationToken)
    {
        var enumData = CreateFullScanEnumData(journalData);

        var buffer = new byte[UsnBufferLength];
        var enumDataSize = Marshal.SizeOf<NtfsNativeMethods.MftEnumDataV0>();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var success = NtfsNativeMethods.DeviceIoControl(
                handle,
                NtfsNativeMethods.FsctlEnumUsnData,
                ref enumData,
                enumDataSize,
                buffer,
                buffer.Length,
                out var bytesReturned,
                IntPtr.Zero);

            if (!success)
            {
                var error = Marshal.GetLastWin32Error();
                if (error == NtfsNativeMethods.ErrorHandleEof)
                {
                    break;
                }

                throw new Win32Exception(error, "Failed to enumerate NTFS USN data.");
            }

            if (bytesReturned <= UsnOutputPrefixLength)
            {
                break;
            }

            enumData.StartFileReferenceNumber = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(0, UsnOutputPrefixLength));
            foreach (var entry in ReadEntriesFromBuffer(buffer.AsSpan(UsnOutputPrefixLength, bytesReturned - UsnOutputPrefixLength)))
            {
                yield return entry;
            }
        }
    }

    internal static IEnumerable<NtfsUsnEntry> EnumerateJournalEntries(
        IntPtr handle,
        NtfsNativeMethods.UsnJournalDataV0 journalData,
        long startUsn,
        long endUsn,
        CancellationToken cancellationToken)
    {
        if (startUsn < journalData.LowestValidUsn || startUsn > endUsn)
        {
            throw new ArgumentOutOfRangeException(nameof(startUsn), "USN start is outside the readable journal range.");
        }

        var readData = CreateReadJournalData(startUsn, journalData.UsnJournalId);
        var buffer = new byte[UsnBufferLength];
        var readDataSize = Marshal.SizeOf<NtfsNativeMethods.ReadUsnJournalDataV0>();

        while (readData.StartUsn < endUsn)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var success = NtfsNativeMethods.DeviceIoControl(
                handle,
                NtfsNativeMethods.FsctlReadUsnJournal,
                ref readData,
                readDataSize,
                buffer,
                buffer.Length,
                out var bytesReturned,
                IntPtr.Zero);

            if (!success)
            {
                var error = Marshal.GetLastWin32Error();
                if (error == NtfsNativeMethods.ErrorHandleEof)
                {
                    ThrowIfJournalRangeIncomplete(readData.StartUsn, endUsn);
                    yield break;
                }

                throw new Win32Exception(error, "Failed to read NTFS USN journal.");
            }

            if (bytesReturned <= UsnOutputPrefixLength)
            {
                ThrowIfJournalRangeIncomplete(readData.StartUsn, endUsn);
                yield break;
            }

            var nextUsn = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(0, UsnOutputPrefixLength));
            foreach (var entry in ReadEntriesFromBuffer(buffer.AsSpan(UsnOutputPrefixLength, bytesReturned - UsnOutputPrefixLength)))
            {
                if (entry.Usn < endUsn)
                {
                    yield return entry;
                }
            }

            if (nextUsn <= readData.StartUsn)
            {
                ThrowIfJournalRangeIncomplete(nextUsn, endUsn);
                yield break;
            }

            readData.StartUsn = nextUsn;
        }
    }

    private static IEnumerable<UsnJournalChange> CreateJournalChanges(
        IntPtr volumeHandle,
        VolumeGeometry? volumeGeometry,
        NtfsScanRoot scanRoot,
        NtfsUsnEntry entry,
        IReadOnlyDictionary<ulong, NtfsUsnEntry> directoriesByReferenceNumber,
        IDictionary<ulong, string> resolvedPaths,
        INtfsFileMetadataReader metadataReader,
        ulong volumeRootFileReferenceNumber,
        CancellationToken cancellationToken)
    {
        if (entry.IsDirectory && (HasAnyReason(entry, UsnReasonDeleteMask | UsnReasonRenameNewName)))
        {
            yield return UsnJournalChange.DirectoryRenameOrMove();
            yield break;
        }

        if (IsAmbiguousJournalChange(entry))
        {
            yield return UsnJournalChange.DirectoryRenameOrMove();
            yield break;
        }

        // Hard-link create/delete does not emit FILE_DELETE for the removed name.
        // Re-sync all current names for the file reference from the live MFT record.
        if (!entry.IsDirectory && HasAnyReason(entry, UsnReasonHardLinkChange))
        {
            foreach (var change in CreateHardLinkJournalChanges(
                         volumeHandle,
                         volumeGeometry,
                         scanRoot,
                         entry,
                         directoriesByReferenceNumber,
                         resolvedPaths,
                         volumeRootFileReferenceNumber))
            {
                yield return change;
            }

            yield break;
        }

        if (!NtfsUsnRecordProjector.TryResolvePath(
                entry,
                scanRoot.VolumeRoot,
                directoriesByReferenceNumber,
                resolvedPaths,
                new HashSet<ulong>(),
                volumeRootFileReferenceNumber,
                out var fullPath))
        {
            yield return UsnJournalChange.DirectoryRenameOrMove();
            yield break;
        }

        if (!NtfsUsnRecordProjector.IsRequestedRootOrDescendant(fullPath, scanRoot.RequestedRoot))
        {
            yield break;
        }

        if (HasAnyReason(entry, UsnReasonDeleteMask))
        {
            yield return UsnJournalChange.Delete(fullPath);
            yield break;
        }

        if (!HasAnyReason(entry, UsnReasonUpsertMask))
        {
            yield break;
        }

        if (!metadataReader.TryRead(fullPath, entry.IsDirectory, cancellationToken, out var metadata))
        {
            yield return UsnJournalChange.DirectoryRenameOrMove();
            yield break;
        }

        var segment = entry.FileReferenceNumber & 0x0000_FFFF_FFFF_FFFFUL;
        yield return UsnJournalChange.Upsert(
            FileRecord.Create(
                fullPath,
                entry.IsDirectory,
                metadata.SizeBytes,
                metadata.LastWriteTime,
                segment));
    }

    internal static IEnumerable<UsnJournalChange> CreateHardLinkJournalChanges(
        IntPtr volumeHandle,
        VolumeGeometry? volumeGeometry,
        NtfsScanRoot scanRoot,
        NtfsUsnEntry entry,
        IReadOnlyDictionary<ulong, NtfsUsnEntry> directoriesByReferenceNumber,
        IDictionary<ulong, string> resolvedPaths,
        ulong volumeRootFileReferenceNumber)
    {
        var segment = entry.FileReferenceNumber & 0x0000_FFFF_FFFF_FFFFUL;
        string? usnPath = null;
        if (NtfsUsnRecordProjector.TryResolvePath(
                entry,
                scanRoot.VolumeRoot,
                directoriesByReferenceNumber,
                resolvedPaths,
                new HashSet<ulong>(),
                volumeRootFileReferenceNumber,
                out var resolvedUsnPath))
        {
            usnPath = resolvedUsnPath;
        }

        if (volumeGeometry is null
            || !NtfsHardLinkResolver.TryGetFileNames(
                volumeHandle,
                entry.FileReferenceNumber,
                volumeGeometry.BytesPerFileRecordSegment,
                volumeGeometry.BytesPerSector,
                out var fileNames,
                out var isDirectory,
                out var sizeBytes,
                out var lastWriteTime))
        {
            // Record gone (last link removed) or geometry unavailable.
            if (usnPath is not null
                && NtfsUsnRecordProjector.IsRequestedRootOrDescendant(usnPath, scanRoot.RequestedRoot))
            {
                // Empty live set: resync deletes all indexed names for this FRN plus the USN path.
                yield return UsnJournalChange.HardLinkResync(segment, Array.Empty<FileRecord>());
                yield return UsnJournalChange.Delete(usnPath);
            }
            else if (usnPath is null)
            {
                // Cannot map the changed name and cannot read the live record — force rescan.
                yield return UsnJournalChange.DirectoryRenameOrMove();
            }

            yield break;
        }

        var liveRecords = new List<FileRecord>();
        foreach (var fileName in fileNames)
        {
            var linkEntry = new NtfsUsnEntry(
                entry.FileReferenceNumber,
                fileName.ParentFileReferenceNumber,
                fileName.Name,
                isDirectory);

            if (!NtfsUsnRecordProjector.TryResolvePath(
                    linkEntry,
                    scanRoot.VolumeRoot,
                    directoriesByReferenceNumber,
                    resolvedPaths,
                    new HashSet<ulong>(),
                    volumeRootFileReferenceNumber,
                    out var fullPath))
            {
                continue;
            }

            if (!NtfsUsnRecordProjector.IsRequestedRootOrDescendant(fullPath, scanRoot.RequestedRoot))
            {
                continue;
            }

            liveRecords.Add(FileRecord.Create(
                fullPath,
                isDirectory,
                sizeBytes,
                lastWriteTime,
                segment));
        }

        // Single resync applies live names and purges any other indexed paths for this FRN.
        yield return UsnJournalChange.HardLinkResync(segment, liveRecords);

        // Also path-delete the USN-reported name when it is no longer live (covers FRN=0
        // rows that HardLinkResync cannot find by file_reference alone).
        if (usnPath is not null
            && !liveRecords.Any(record =>
                string.Equals(record.FullPath, usnPath, StringComparison.OrdinalIgnoreCase))
            && NtfsUsnRecordProjector.IsRequestedRootOrDescendant(usnPath, scanRoot.RequestedRoot))
        {
            yield return UsnJournalChange.Delete(usnPath);
        }
    }

    internal sealed record VolumeGeometry(int BytesPerSector, int BytesPerFileRecordSegment);

    private static VolumeGeometry? TryQueryVolumeGeometry(IntPtr handle)
    {
        try
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
            if (!success
                || volumeData.BytesPerSector == 0
                || volumeData.BytesPerFileRecordSegment == 0)
            {
                return null;
            }

            return new VolumeGeometry(
                (int)volumeData.BytesPerSector,
                (int)volumeData.BytesPerFileRecordSegment);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool HasAnyReason(NtfsUsnEntry entry, uint reasonMask)
    {
        return (entry.Reason & reasonMask) != 0;
    }

    internal static bool IsAmbiguousJournalChange(NtfsUsnEntry entry)
    {
        return !entry.IsDirectory
            && HasAnyReason(entry, UsnReasonRenameOldName)
            && (HasAnyReason(entry, UsnReasonClose) || HasAnyReason(entry, UsnReasonRenameNewName));
    }

    internal static void ThrowIfJournalRangeIncomplete(long reachedUsn, long endUsn)
    {
        if (reachedUsn < endUsn)
        {
            throw new InvalidDataException("NTFS USN journal read ended before requested range was complete.");
        }
    }

    private static NtfsNativeMethods.UsnJournalDataV0 QueryJournal(IntPtr handle)
    {
        var journalDataSize = Marshal.SizeOf<NtfsNativeMethods.UsnJournalDataV0>();
        var success = NtfsNativeMethods.DeviceIoControlQueryUsnJournal(
            handle,
            NtfsNativeMethods.FsctlQueryUsnJournal,
            IntPtr.Zero,
            0,
            out var journalData,
            journalDataSize,
            out _,
            IntPtr.Zero);

        if (!success)
        {
            throw CreateWin32Exception("Failed to query NTFS USN journal.");
        }

        return journalData;
    }

    internal static IReadOnlyList<NtfsUsnEntry> ReadEntriesFromBuffer(ReadOnlySpan<byte> recordsBuffer)
    {
        var entries = new List<NtfsUsnEntry>();
        var offset = 0;
        while (offset < recordsBuffer.Length)
        {
            var remaining = recordsBuffer[offset..];
            if (remaining.Length < UsnRecordV2HeaderLength)
            {
                throw new InvalidDataException("NTFS USN data ended in the middle of a record.");
            }

            var recordLength = BinaryPrimitives.ReadUInt32LittleEndian(remaining[0..4]);
            if (recordLength == 0 || recordLength > remaining.Length)
            {
                throw new InvalidDataException("NTFS USN record length exceeds the returned buffer.");
            }

            var record = remaining[..(int)recordLength];
            var parsed = UsnRecordParser.ParseV2(record);
            var entry = new NtfsUsnEntry(
                BinaryPrimitives.ReadUInt64LittleEndian(record[8..16]),
                BinaryPrimitives.ReadUInt64LittleEndian(record[16..24]),
                parsed.Name,
                parsed.IsDirectory,
                BinaryPrimitives.ReadInt64LittleEndian(record[24..32]),
                BinaryPrimitives.ReadUInt32LittleEndian(record[40..44]));

            entries.Add(entry);
            offset += (int)recordLength;
        }

        return entries;
    }

    private static Win32Exception CreateWin32Exception(string message)
    {
        return new Win32Exception(Marshal.GetLastWin32Error(), message);
    }
}

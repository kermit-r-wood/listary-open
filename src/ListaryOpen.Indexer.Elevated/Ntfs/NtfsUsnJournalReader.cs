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
                await Task.Yield();
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
        long startUsn,
        long endUsn,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var scanRoot = NtfsScanRoot.Create(volumeRoot);
        var handle = OpenVolume(scanRoot.VolumePath);

        try
        {
            var journalData = QueryJournal(handle);
            var volumeRootFileReferenceNumber = ReadFileReferenceNumber(scanRoot.VolumeRoot);
            var directories = ReadDirectoryEntries(handle, journalData, cancellationToken);
            var metadataReader = new NtfsFileMetadataReader();
            var resolvedPaths = new Dictionary<ulong, string>();

            foreach (var entry in EnumerateJournalEntries(handle, journalData, startUsn, endUsn, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var change = CreateJournalChange(
                    scanRoot,
                    entry,
                    directories,
                    resolvedPaths,
                    metadataReader,
                    volumeRootFileReferenceNumber,
                    cancellationToken);
                if (change is not null)
                {
                    yield return change;
                    await Task.Yield();
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
                entries[entry.FileReferenceNumber] = entry;
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
                    yield break;
                }

                throw new Win32Exception(error, "Failed to read NTFS USN journal.");
            }

            if (bytesReturned <= UsnOutputPrefixLength)
            {
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
                yield break;
            }

            readData.StartUsn = nextUsn;
        }
    }

    private static UsnJournalChange? CreateJournalChange(
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
            return UsnJournalChange.DirectoryRenameOrMove();
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
            return UsnJournalChange.DirectoryRenameOrMove();
        }

        if (!NtfsUsnRecordProjector.IsRequestedRootOrDescendant(fullPath, scanRoot.RequestedRoot))
        {
            return null;
        }

        if (HasAnyReason(entry, UsnReasonDeleteMask))
        {
            return UsnJournalChange.Delete(fullPath);
        }

        if (!HasAnyReason(entry, UsnReasonUpsertMask))
        {
            return null;
        }

        if (!metadataReader.TryRead(fullPath, entry.IsDirectory, cancellationToken, out var metadata))
        {
            return UsnJournalChange.DirectoryRenameOrMove();
        }

        return UsnJournalChange.Upsert(
            FileRecord.Create(fullPath, entry.IsDirectory, metadata.SizeBytes, metadata.LastWriteTime));
    }

    private static bool HasAnyReason(NtfsUsnEntry entry, uint reasonMask)
    {
        return (entry.Reason & reasonMask) != 0;
    }

    private static NtfsNativeMethods.UsnJournalDataV0 QueryJournal(IntPtr handle)
    {
        var journalDataSize = Marshal.SizeOf<NtfsNativeMethods.UsnJournalDataV0>();
        var success = NtfsNativeMethods.DeviceIoControl(
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

using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ListaryOpen.Core.Indexing;
using ListaryOpen.Infrastructure.Indexing.Ntfs;

namespace ListaryOpen.Indexer.Elevated.Ntfs;

public sealed class NtfsUsnJournalReader
{
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

    private static IReadOnlyList<NtfsUsnEntry> ReadEntriesFromBuffer(ReadOnlySpan<byte> recordsBuffer)
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
                parsed.IsDirectory);

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

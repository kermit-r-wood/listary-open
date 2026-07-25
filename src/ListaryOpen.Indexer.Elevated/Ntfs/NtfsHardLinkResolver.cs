using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ListaryOpen.Indexer.Elevated.Ntfs;

/// <summary>
/// Resolves all hard-link names for a file reference via FSCTL_GET_NTFS_FILE_RECORD
/// and optional attribute-list extension records.
/// </summary>
internal static class NtfsHardLinkResolver
{
    private const int FileRecordOutputHeaderLength = 16;

    public static bool TryGetFileNames(
        IntPtr volumeHandle,
        ulong fileReferenceNumber,
        int bytesPerFileRecordSegment,
        int bytesPerSector,
        out IReadOnlyList<NtfsMftFileName> fileNames,
        out bool isDirectory,
        out long sizeBytes,
        out DateTimeOffset lastWriteTime)
    {
        fileNames = Array.Empty<NtfsMftFileName>();
        isDirectory = false;
        sizeBytes = 0;
        lastWriteTime = default;

        if (!TryReadFileRecord(
                volumeHandle,
                fileReferenceNumber,
                bytesPerFileRecordSegment,
                bytesPerSector,
                out var baseRecord))
        {
            return false;
        }

        if (!baseRecord.InUse)
        {
            return false;
        }

        isDirectory = baseRecord.IsDirectory;
        sizeBytes = baseRecord.SizeBytes;
        lastWriteTime = baseRecord.LastWriteTime;

        var names = new List<NtfsMftFileName>(baseRecord.FileNames);
        foreach (var extensionRef in baseRecord.AttributeListFileReferences)
        {
            if (extensionRef == baseRecord.RecordNumber)
            {
                continue;
            }

            if (TryReadFileRecord(
                    volumeHandle,
                    extensionRef,
                    bytesPerFileRecordSegment,
                    bytesPerSector,
                    out var extension))
            {
                names.AddRange(extension.FileNames);
            }
        }

        fileNames = NtfsMftRecordParser.SelectIndexableFileNames(names).ToList();
        return true;
    }

    private static bool TryReadFileRecord(
        IntPtr volumeHandle,
        ulong fileReferenceNumber,
        int bytesPerFileRecordSegment,
        int bytesPerSector,
        out NtfsMftParsedRecord parsed)
    {
        parsed = null!;
        var input = new NtfsNativeMethods.NtfsFileRecordInputBuffer
        {
            FileReferenceNumber = unchecked((long)fileReferenceNumber)
        };

        // Output: 8-byte FRN + 4-byte length + 4 pad/reserved + record bytes.
        var outputLength = FileRecordOutputHeaderLength + Math.Max(bytesPerFileRecordSegment, 1024) + 1024;
        var output = new byte[outputLength];
        var success = NtfsNativeMethods.DeviceIoControl(
            volumeHandle,
            NtfsNativeMethods.FsctlGetNtfsFileRecord,
            ref input,
            Marshal.SizeOf<NtfsNativeMethods.NtfsFileRecordInputBuffer>(),
            output,
            output.Length,
            out var bytesReturned,
            IntPtr.Zero);

        if (!success || bytesReturned <= FileRecordOutputHeaderLength)
        {
            return false;
        }

        var recordLength = BitConverter.ToInt32(output, 8);
        if (recordLength <= 0 || FileRecordOutputHeaderLength + recordLength > bytesReturned)
        {
            return false;
        }

        var record = new byte[recordLength];
        Buffer.BlockCopy(output, FileRecordOutputHeaderLength, record, 0, recordLength);
        if (!NtfsMftRecordParser.TryApplyUpdateSequence(record, bytesPerSector))
        {
            return false;
        }

        var fallbackRecordNumber = NtfsMftRecordParser.GetMftSegmentReferenceNumber(fileReferenceNumber);
        return NtfsMftRecordParser.TryParse(record, fallbackRecordNumber, out parsed);
    }
}

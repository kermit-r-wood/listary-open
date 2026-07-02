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
        var normalizedRoot = NormalizeVolumeRoot(volumeRoot);
        var volumePath = ToVolumePath(normalizedRoot);
        var handle = OpenVolume(volumePath);

        try
        {
            var entries = ReadEntries(handle, cancellationToken);
            foreach (var record in CreateFileRecords(normalizedRoot, entries, cancellationToken))
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

    private static Dictionary<ulong, UsnEntry> ReadEntries(IntPtr handle, CancellationToken cancellationToken)
    {
        var journalData = QueryJournal(handle);
        var enumData = new NtfsNativeMethods.MftEnumDataV0
        {
            StartFileReferenceNumber = 0,
            LowUsn = 0,
            HighUsn = journalData.NextUsn
        };

        var entries = new Dictionary<ulong, UsnEntry>();
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
            ReadEntriesFromBuffer(buffer.AsSpan(UsnOutputPrefixLength, bytesReturned - UsnOutputPrefixLength), entries);
        }

        return entries;
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

    private static void ReadEntriesFromBuffer(ReadOnlySpan<byte> recordsBuffer, Dictionary<ulong, UsnEntry> entries)
    {
        var offset = 0;
        while (offset < recordsBuffer.Length)
        {
            var remaining = recordsBuffer[offset..];
            if (remaining.Length < UsnRecordV2HeaderLength)
            {
                throw new IOException("NTFS USN data ended in the middle of a record.");
            }

            var recordLength = BinaryPrimitives.ReadUInt32LittleEndian(remaining[0..4]);
            if (recordLength == 0 || recordLength > remaining.Length)
            {
                throw new IOException("NTFS USN record length exceeds the returned buffer.");
            }

            var record = remaining[..(int)recordLength];
            var parsed = UsnRecordParser.ParseV2(record);
            var entry = new UsnEntry(
                BinaryPrimitives.ReadUInt64LittleEndian(record[8..16]),
                BinaryPrimitives.ReadUInt64LittleEndian(record[16..24]),
                parsed.Name,
                parsed.IsDirectory,
                ReadTimestamp(record));

            entries[entry.FileReferenceNumber] = entry;
            offset += (int)recordLength;
        }
    }

    private static IEnumerable<FileRecord> CreateFileRecords(
        string volumeRoot,
        IReadOnlyDictionary<ulong, UsnEntry> entries,
        CancellationToken cancellationToken)
    {
        var resolvedPaths = new Dictionary<ulong, string>();

        foreach (var entry in entries.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryResolvePath(entry, volumeRoot, entries, resolvedPaths, new HashSet<ulong>(), out var fullPath))
            {
                continue;
            }

            yield return FileRecord.Create(fullPath, entry.IsDirectory, 0, entry.LastWriteTime);
        }
    }

    private static bool TryResolvePath(
        UsnEntry entry,
        string volumeRoot,
        IReadOnlyDictionary<ulong, UsnEntry> entries,
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

    private static bool IsRootEntry(UsnEntry entry)
    {
        return entry.FileReferenceNumber == entry.ParentFileReferenceNumber
            || entry.Name == ".";
    }

    private static DateTimeOffset ReadTimestamp(ReadOnlySpan<byte> record)
    {
        var fileTime = BinaryPrimitives.ReadInt64LittleEndian(record[32..40]);
        if (fileTime <= 0)
        {
            return DateTimeOffset.UnixEpoch;
        }

        try
        {
            return new DateTimeOffset(DateTime.FromFileTimeUtc(fileTime), TimeSpan.Zero);
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTimeOffset.UnixEpoch;
        }
    }

    private static string NormalizeVolumeRoot(string volumeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeRoot);

        var trimmed = volumeRoot.Trim();
        if (!Path.IsPathFullyQualified(trimmed))
        {
            throw new ArgumentException("Volume root must be fully qualified.", nameof(volumeRoot));
        }

        var root = Path.GetPathRoot(Path.GetFullPath(trimmed));
        if (string.IsNullOrWhiteSpace(root) || root.Length < 3 || root[1] != ':')
        {
            throw new ArgumentException("Volume root must be a drive-letter path.", nameof(volumeRoot));
        }

        return Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
    }

    private static string ToVolumePath(string volumeRoot)
    {
        return @"\\.\" + volumeRoot[..2];
    }

    private static Win32Exception CreateWin32Exception(string message)
    {
        return new Win32Exception(Marshal.GetLastWin32Error(), message);
    }

    private sealed record UsnEntry(
        ulong FileReferenceNumber,
        ulong ParentFileReferenceNumber,
        string Name,
        bool IsDirectory,
        DateTimeOffset LastWriteTime);
}

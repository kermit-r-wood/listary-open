using System.Buffers.Binary;
using System.Text;
using ListaryOpen.Indexer.Elevated.Ntfs;
using ListaryOpen.Infrastructure.Indexing;

namespace ListaryOpen.Infrastructure.Tests.Indexing;

public sealed class NtfsMftRecordParserTests
{
    [Fact]
    public void TryParseReadsMultipleHardLinkFileNames()
    {
        var record = MftRecordBuilder.Create(
            recordNumber: 42,
            isDirectory: false,
            sizeBytes: 48,
            lastWriteFileTime: ToFileTime(new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc)),
            fileNames:
            [
                (parent: 5, name: "a.txt", ns: NtfsFileNameNamespace.Win32),
                (parent: 10, name: "a1.txt", ns: NtfsFileNameNamespace.Win32)
            ]);

        Assert.True(NtfsMftRecordParser.TryParse(record, fallbackRecordNumber: 42, out var parsed));
        Assert.True(parsed.InUse);
        Assert.False(parsed.IsDirectory);
        Assert.Equal(42ul, parsed.RecordNumber);
        Assert.Equal(48, parsed.SizeBytes);
        Assert.Equal(2, parsed.FileNames.Count);
        Assert.Contains(parsed.FileNames, n => n.Name == "a.txt" && n.ParentFileReferenceNumber == 5);
        Assert.Contains(parsed.FileNames, n => n.Name == "a1.txt" && n.ParentFileReferenceNumber == 10);
    }

    [Fact]
    public void SelectIndexableFileNamesSkipsPureDosShortNames()
    {
        var names = new[]
        {
            new NtfsMftFileName(5, "LONGNA~1.TXT", NtfsFileNameNamespace.Dos),
            new NtfsMftFileName(5, "LongName.txt", NtfsFileNameNamespace.Win32)
        };

        var selected = NtfsMftRecordParser.SelectIndexableFileNames(names).ToList();

        Assert.Single(selected);
        Assert.Equal("LongName.txt", selected[0].Name);
    }

    [Fact]
    public void TryApplyUpdateSequenceRestoresSectorTails()
    {
        var record = new byte[1024];
        record[0] = (byte)'F';
        record[1] = (byte)'I';
        record[2] = (byte)'L';
        record[3] = (byte)'E';

        // USA at offset 0x30: USN + 2 sector replacements
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0x04, 2), 0x30);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0x06, 2), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0x30, 2), 0xABCD); // USN
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0x32, 2), 0x1111); // sector 0 original
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0x34, 2), 0x2222); // sector 1 original

        // Sector tails currently hold USN (as on disk before fixup application by reader)
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(510, 2), 0xABCD);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(1022, 2), 0xABCD);

        Assert.True(NtfsMftRecordParser.TryApplyUpdateSequence(record, bytesPerSector: 512));
        Assert.Equal(0x1111, BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(510, 2)));
        Assert.Equal(0x2222, BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(1022, 2)));
    }

    [Fact]
    public void ProjectRecordsEmitsOnePathPerHardLink()
    {
        const int bytesPerRecord = 1024;
        var mft = new byte[bytesPerRecord * 12];

        // Record 5 = root
        WriteRecord(
            mft,
            recordNumber: 5,
            isDirectory: true,
            sizeBytes: 0,
            fileNames: [(parent: 5, name: ".", ns: NtfsFileNameNamespace.Win32)]);

        // Record 10 = Docs under root
        WriteRecord(
            mft,
            recordNumber: 10,
            isDirectory: true,
            sizeBytes: 0,
            fileNames: [(parent: 5, name: "Docs", ns: NtfsFileNameNamespace.Win32)]);

        // Record 11 = file with two hard links under Docs and root
        WriteRecord(
            mft,
            recordNumber: 11,
            isDirectory: false,
            sizeBytes: 42,
            fileNames:
            [
                (parent: 10, name: "a.txt", ns: NtfsFileNameNamespace.Win32),
                (parent: 5, name: "a1.txt", ns: NtfsFileNameNamespace.Win32)
            ]);

        var volumeData = new NtfsNativeMethods.NtfsVolumeDataBuffer
        {
            BytesPerSector = 512,
            BytesPerCluster = 4096,
            BytesPerFileRecordSegment = (uint)bytesPerRecord,
            MftValidDataLength = mft.Length,
            MftStartLcn = 0
        };

        var records = NtfsMftScanner.ProjectRecords(
                mft,
                volumeData,
                volumeRoot: @"C:\",
                requestedRoot: @"C:\",
                IndexExclusionRules.Default,
                CancellationToken.None)
            .Select(r => r.FullPath)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.Contains(@"C:\Docs", records);
        Assert.Contains(@"C:\Docs\a.txt", records);
        Assert.Contains(@"C:\a1.txt", records);
    }

    [Fact]
    public void TryParseRunsReadsRelativeLcnChain()
    {
        // header: length size 1, offset size 1; length=4; offset=+10; terminator 0
        // then second run: length=2; offset=+5 (absolute LCN 15)
        var pairs = new byte[] { 0x11, 0x04, 0x0A, 0x11, 0x02, 0x05, 0x00 };

        Assert.True(NtfsMftDataRuns.TryParseRuns(pairs, out var runs));
        Assert.Equal(2, runs.Count);
        Assert.Equal(10, runs[0].StartLcn);
        Assert.Equal(4, runs[0].ClusterCount);
        Assert.Equal(15, runs[1].StartLcn);
        Assert.Equal(2, runs[1].ClusterCount);
    }

    private static void WriteRecord(
        byte[] mft,
        int recordNumber,
        bool isDirectory,
        long sizeBytes,
        (ulong parent, string name, NtfsFileNameNamespace ns)[] fileNames)
    {
        const int bytesPerRecord = 1024;
        var record = MftRecordBuilder.Create(
            (ulong)recordNumber,
            isDirectory,
            sizeBytes,
            lastWriteFileTime: ToFileTime(DateTime.UnixEpoch),
            fileNames);
        Buffer.BlockCopy(record, 0, mft, recordNumber * bytesPerRecord, record.Length);
    }

    private static long ToFileTime(DateTime utc)
        => utc.ToFileTimeUtc();
}

/// <summary>
/// Builds minimal valid NTFS FILE records for parser unit tests (no USA needed for single-pass parse).
/// </summary>
internal static class MftRecordBuilder
{
    public static byte[] Create(
        ulong recordNumber,
        bool isDirectory,
        long sizeBytes,
        long lastWriteFileTime,
        (ulong parent, string name, NtfsFileNameNamespace ns)[] fileNames)
    {
        const int recordSize = 1024;
        var buffer = new byte[recordSize];
        buffer[0] = (byte)'F';
        buffer[1] = (byte)'I';
        buffer[2] = (byte)'L';
        buffer[3] = (byte)'E';

        // USA: empty-ish but valid enough to skip if count < 2
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(0x04, 2), 0x30);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(0x06, 2), 0); // no USA

        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(0x14, 2), 0x38); // first attribute
        ushort flags = 0x0001; // in use
        if (isDirectory)
        {
            flags |= 0x0002;
        }

        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(0x16, 2), flags);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0x18, 4), recordSize); // used
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0x1C, 4), recordSize); // allocated
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(0x20, 8), 0); // base = 0
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0x2C, 4), (uint)recordNumber);

        var offset = 0x38;

        // STANDARD_INFORMATION (resident)
        offset = WriteResidentAttribute(
            buffer,
            offset,
            NtfsMftRecordParser.AttributeStandardInformation,
            std =>
            {
                // 0x00 creation, 0x08 modification
                BinaryPrimitives.WriteInt64LittleEndian(std.AsSpan(0x00, 8), lastWriteFileTime);
                BinaryPrimitives.WriteInt64LittleEndian(std.AsSpan(0x08, 8), lastWriteFileTime);
                BinaryPrimitives.WriteInt64LittleEndian(std.AsSpan(0x10, 8), lastWriteFileTime);
                BinaryPrimitives.WriteInt64LittleEndian(std.AsSpan(0x18, 8), lastWriteFileTime);
            },
            valueLength: 0x30);

        foreach (var (parent, name, ns) in fileNames)
        {
            var nameBytes = Encoding.Unicode.GetBytes(name);
            var valueLength = 0x42 + nameBytes.Length;
            offset = WriteResidentAttribute(
                buffer,
                offset,
                NtfsMftRecordParser.AttributeFileName,
                value =>
                {
                    BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(0x00, 8), parent);
                    value[0x40] = (byte)(nameBytes.Length / 2);
                    value[0x41] = (byte)ns;
                    nameBytes.CopyTo(value.AsSpan(0x42));
                },
                valueLength);
        }

        if (!isDirectory)
        {
            // Resident DATA with payload length = sizeBytes (capped for test buffer)
            var dataLen = (int)Math.Min(Math.Max(sizeBytes, 0), 64);
            offset = WriteResidentAttribute(
                buffer,
                offset,
                NtfsMftRecordParser.AttributeData,
                value =>
                {
                    for (var i = 0; i < dataLen; i++)
                    {
                        value[i] = (byte)i;
                    }
                },
                dataLen);
        }

        // END attribute
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset, 4), NtfsMftRecordParser.AttributeEnd);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset + 4, 4), 8);

        return buffer;
    }

    private static int WriteResidentAttribute(
        byte[] buffer,
        int offset,
        uint type,
        Action<byte[]> writeValue,
        int valueLength)
    {
        const int headerLength = 0x18;
        var attributeLength = Align8(headerLength + valueLength);
        var attribute = new byte[attributeLength];
        BinaryPrimitives.WriteUInt32LittleEndian(attribute.AsSpan(0, 4), type);
        BinaryPrimitives.WriteUInt32LittleEndian(attribute.AsSpan(4, 4), (uint)attributeLength);
        attribute[0x08] = 0; // resident
        attribute[0x09] = 0; // name length
        BinaryPrimitives.WriteUInt16LittleEndian(attribute.AsSpan(0x0A, 2), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(attribute.AsSpan(0x10, 4), (uint)valueLength);
        BinaryPrimitives.WriteUInt16LittleEndian(attribute.AsSpan(0x14, 2), headerLength);

        var value = new byte[valueLength];
        writeValue(value);
        value.CopyTo(attribute.AsSpan(headerLength));

        attribute.CopyTo(buffer.AsSpan(offset));
        return offset + attributeLength;
    }

    private static int Align8(int value)
        => (value + 7) & ~7;
}

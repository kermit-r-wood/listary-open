using ListaryOpen.Indexer.Elevated.Ntfs;
using System.Buffers.Binary;
using System.Reflection;
using System.Text;

namespace ListaryOpen.Infrastructure.Tests.Indexing;

public sealed class NtfsUsnJournalReaderTests
{
    [Fact]
    public void CreateFullScanEnumDataUsesJournalNextUsnAsHighUsn()
    {
        var journal = new NtfsNativeMethods.UsnJournalDataV0
        {
            NextUsn = 12345
        };

        var enumData = NtfsUsnJournalReader.CreateFullScanEnumData(journal);

        Assert.Equal(0ul, enumData.StartFileReferenceNumber);
        Assert.Equal(0, enumData.LowUsn);
        Assert.Equal(12345, enumData.HighUsn);
    }

    [Fact]
    public void ReadUsnJournalDataUsesExpectedControlCodeAndFields()
    {
        var data = NtfsUsnJournalReader.CreateReadJournalData(startUsn: 10, usnJournalId: 123);

        Assert.Equal(0x000900bbU, NtfsNativeMethods.FsctlReadUsnJournal);
        Assert.Equal(10, data.StartUsn);
        Assert.Equal(uint.MaxValue, data.ReasonMask);
        Assert.Equal(0u, data.ReturnOnlyOnClose);
        Assert.Equal(0ul, data.Timeout);
        Assert.Equal(0ul, data.BytesToWaitFor);
        Assert.Equal(123ul, data.UsnJournalId);
    }

    [Fact]
    public void ReadUsnJournalDeviceIoControlOverloadAcceptsReadJournalData()
    {
        var overload = typeof(NtfsNativeMethods)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .SingleOrDefault(method =>
                method.Name == "DeviceIoControl"
                && method.GetParameters()[2].ParameterType == typeof(NtfsNativeMethods.ReadUsnJournalDataV0).MakeByRefType());

        Assert.NotNull(overload);
    }

    [Fact]
    public void ReadEntriesFromBufferReadsReferencesUsnAndReason()
    {
        var buffer = CreateUsnRecordBuffer(
            "Invoice.txt",
            fileReferenceNumber: 10,
            parentFileReferenceNumber: 5,
            usn: 1234,
            reason: 0x0000_0200,
            fileAttributes: 0x80u);

        var entry = Assert.Single(NtfsUsnJournalReader.ReadEntriesFromBuffer(buffer));

        Assert.Equal(10ul, entry.FileReferenceNumber);
        Assert.Equal(5ul, entry.ParentFileReferenceNumber);
        Assert.Equal(1234, entry.Usn);
        Assert.Equal(0x0000_0200u, entry.Reason);
        Assert.Equal("Invoice.txt", entry.Name);
        Assert.False(entry.IsDirectory);
    }

    [Fact]
    public void JournalSnapshotHasAdvancedPastRequestedEndRequiresFullRescan()
    {
        var journal = new NtfsNativeMethods.UsnJournalDataV0
        {
            UsnJournalId = 9,
            LowestValidUsn = 50,
            NextUsn = 201
        };

        Assert.True(NtfsUsnJournalReader.RequiresFullRescanForJournalSnapshot(journal, expectedUsnJournalId: 9, endUsn: 200));
    }

    [Fact]
    public void JournalSnapshotWithDifferentJournalIdRequiresFullRescan()
    {
        var journal = new NtfsNativeMethods.UsnJournalDataV0
        {
            UsnJournalId = 10,
            LowestValidUsn = 50,
            NextUsn = 200
        };

        Assert.True(NtfsUsnJournalReader.RequiresFullRescanForJournalSnapshot(journal, expectedUsnJournalId: 9, endUsn: 200));
    }

    [Fact]
    public void CloseSummaryWithRenameOldNameIsAmbiguous()
    {
        var entry = new NtfsUsnEntry(
            FileReferenceNumber: 10,
            ParentFileReferenceNumber: 5,
            Usn: 1234,
            Reason: 0x8000_0000 | 0x0000_1000,
            Name: "NewName.txt",
            IsDirectory: false);

        Assert.True(NtfsUsnJournalReader.IsAmbiguousJournalChange(entry));
    }

    [Fact]
    public void ThrowIfJournalRangeIncompleteRejectsShortRead()
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            NtfsUsnJournalReader.ThrowIfJournalRangeIncomplete(reachedUsn: 199, endUsn: 200));

        Assert.Contains("ended before requested range", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ThrowIfJournalRangeIncompleteAcceptsCompleteRead()
    {
        NtfsUsnJournalReader.ThrowIfJournalRangeIncomplete(reachedUsn: 200, endUsn: 200);
    }

    private static byte[] CreateUsnRecordBuffer(
        string name,
        ulong fileReferenceNumber,
        ulong parentFileReferenceNumber,
        long usn,
        uint reason,
        uint fileAttributes)
    {
        var nameBytes = Encoding.Unicode.GetBytes(name);
        var buffer = new byte[60 + nameBytes.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), (uint)buffer.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4, 2), 2);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(8, 8), fileReferenceNumber);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(16, 8), parentFileReferenceNumber);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(24, 8), usn);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(40, 4), reason);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(52, 4), fileAttributes);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(56, 2), (ushort)nameBytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(58, 2), 60);
        nameBytes.CopyTo(buffer.AsSpan(60));
        return buffer;
    }
}

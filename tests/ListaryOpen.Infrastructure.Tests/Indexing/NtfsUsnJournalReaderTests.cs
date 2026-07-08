using ListaryOpen.Indexer.Elevated.Ntfs;

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
        var data = new NtfsNativeMethods.ReadUsnJournalDataV0
        {
            StartUsn = 10,
            ReasonMask = 0xFFFF_FFFF,
            ReturnOnlyOnClose = 0,
            Timeout = 0,
            BytesToWaitFor = 0,
            UsnJournalId = 123
        };

        Assert.Equal(0x000900bbU, NtfsNativeMethods.FsctlReadUsnJournal);
        Assert.Equal(10, data.StartUsn);
        Assert.Equal(123ul, data.UsnJournalId);
    }
}

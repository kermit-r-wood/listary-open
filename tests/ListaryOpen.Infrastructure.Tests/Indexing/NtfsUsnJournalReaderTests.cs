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
}

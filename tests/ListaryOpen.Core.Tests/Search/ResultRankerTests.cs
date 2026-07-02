using ListaryOpen.Core.Indexing;
using ListaryOpen.Core.Search;
using ListaryOpen.Core.Usage;

namespace ListaryOpen.Core.Tests.Search;

public sealed class ResultRankerTests
{
    [Fact]
    public void RankBoostsFrequentlyUsedRecord()
    {
        var now = new DateTimeOffset(2026, 7, 3, 1, 0, 0, TimeSpan.Zero);
        var records = new[]
        {
            FileRecord.Create("C:\\Docs\\Invoice.xlsx", false, 1, now),
            FileRecord.Create("C:\\Archive\\Invoice.xlsx", false, 1, now)
        };
        var usage = new[]
        {
            new UsageRecord("c:\\archive\\invoice.xlsx", 10, now)
        };

        var ranked = ResultRanker.Rank(
            new SearchQuery("invoice", SearchMode.FilesAndFolders),
            records,
            usage,
            Array.Empty<string>());

        Assert.Equal("C:\\Archive\\Invoice.xlsx", ranked[0].Record.FullPath);
    }

    [Fact]
    public void RankFiltersFoldersOnly()
    {
        var now = new DateTimeOffset(2026, 7, 3, 1, 0, 0, TimeSpan.Zero);
        var records = new[]
        {
            FileRecord.Create("C:\\Docs\\Invoice.xlsx", false, 1, now),
            FileRecord.Create("C:\\Docs\\Invoices", true, 0, now)
        };

        var ranked = ResultRanker.Rank(
            new SearchQuery("invoice", SearchMode.FoldersOnly),
            records,
            Array.Empty<UsageRecord>(),
            Array.Empty<string>());

        Assert.Single(ranked);
        Assert.True(ranked[0].Record.IsDirectory);
    }

    [Fact]
    public void RankBoostsPinnedFolderUsingPathKey()
    {
        var now = new DateTimeOffset(2026, 7, 3, 1, 0, 0, TimeSpan.Zero);
        var records = new[]
        {
            FileRecord.Create("C:\\Archive\\Invoices", true, 0, now),
            FileRecord.Create("C:\\Docs\\Invoices", true, 0, now)
        };

        var ranked = ResultRanker.Rank(
            new SearchQuery("invoice", SearchMode.FoldersOnly),
            records,
            Array.Empty<UsageRecord>(),
            new[] { "c:\\docs\\invoices\\" });

        Assert.Equal("C:\\Docs\\Invoices", ranked[0].Record.FullPath);
    }

    [Fact]
    public void RankUsesPinyinScore()
    {
        var now = new DateTimeOffset(2026, 7, 3, 1, 0, 0, TimeSpan.Zero);
        var records = new[]
        {
            FileRecord.Create("C:\\Docs\\发票2026.xlsx", false, 1, now)
        };

        var ranked = ResultRanker.Rank(
            new SearchQuery("fapiao", SearchMode.FilesAndFolders),
            records,
            Array.Empty<UsageRecord>(),
            Array.Empty<string>());

        var result = Assert.Single(ranked);
        Assert.Equal("C:\\Docs\\发票2026.xlsx", result.Record.FullPath);
    }

    [Fact]
    public void RankOrdersTiesByNameThenPathCaseInsensitive()
    {
        var now = new DateTimeOffset(2026, 7, 3, 1, 0, 0, TimeSpan.Zero);
        var records = new[]
        {
            FileRecord.Create("C:\\Beta\\Invoice.xlsx", false, 1, now),
            FileRecord.Create("C:\\Alpha\\Invoice.xlsx", false, 1, now)
        };

        var ranked = ResultRanker.Rank(
            new SearchQuery("invoice", SearchMode.FilesAndFolders),
            records,
            Array.Empty<UsageRecord>(),
            Array.Empty<string>());

        Assert.Equal(
            new[] { "C:\\Alpha\\Invoice.xlsx", "C:\\Beta\\Invoice.xlsx" },
            ranked.Select(result => result.Record.FullPath));
    }

    [Fact]
    public void RankDoesNotMutateInputs()
    {
        var now = new DateTimeOffset(2026, 7, 3, 1, 0, 0, TimeSpan.Zero);
        var records = new List<FileRecord>
        {
            FileRecord.Create("C:\\Beta\\Invoice.xlsx", false, 1, now),
            FileRecord.Create("C:\\Alpha\\Invoice.xlsx", false, 1, now)
        };
        var usage = new List<UsageRecord>
        {
            new("C:\\Beta\\Invoice.xlsx", 3, now)
        };
        var pinned = new List<string> { "C:\\Alpha" };
        var originalRecordOrder = records.Select(record => record.FullPath).ToArray();
        var originalUsage = usage.ToArray();
        var originalPinned = pinned.ToArray();

        _ = ResultRanker.Rank(
            new SearchQuery("invoice", SearchMode.FilesAndFolders),
            records,
            usage,
            pinned);

        Assert.Equal(originalRecordOrder, records.Select(record => record.FullPath));
        Assert.Equal(originalUsage, usage);
        Assert.Equal(originalPinned, pinned);
    }
}

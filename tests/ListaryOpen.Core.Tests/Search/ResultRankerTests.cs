using ListaryOpen.Core.Indexing;
using ListaryOpen.Core.Search;
using ListaryOpen.Core.Usage;

namespace ListaryOpen.Core.Tests.Search;

public sealed class ResultRankerTests
{
    [Fact]
    public void RankOrdersExactNameBeforePrefixBeforeSubstringBeforePath()
    {
        var now = new DateTimeOffset(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);
        var records = new[]
        {
            FileRecord.Create("C:\\Invoice\\unrelated.txt", false, 1, now),
            FileRecord.Create("C:\\Docs\\OldInvoice.txt", false, 1, now),
            FileRecord.Create("C:\\Docs\\InvoiceArchive.txt", false, 1, now),
            FileRecord.Create("C:\\Docs\\Invoice", false, 1, now)
        };

        var ranked = ResultRanker.Rank(
            new SearchQuery("invoice", SearchMode.FilesAndFolders),
            records,
            Array.Empty<UsageRecord>(),
            Array.Empty<string>());

        Assert.Equal(
            new[] { "Invoice", "InvoiceArchive.txt", "OldInvoice.txt", "unrelated.txt" },
            ranked.Select(result => result.Record.Name));
    }

    [Fact]
    public void RankUsageCannotPromoteSubstringAboveExactName()
    {
        var now = DateTimeOffset.UtcNow;
        var exact = FileRecord.Create("C:\\Docs\\Invoice", false, 1, now);
        var substring = FileRecord.Create("C:\\Archive\\OldInvoiceBackup.txt", false, 1, now);

        var ranked = ResultRanker.Rank(
            new SearchQuery("invoice", SearchMode.FilesAndFolders),
            [substring, exact],
            [new UsageRecord(substring.FullPath, 100_000, now)],
            Array.Empty<string>());

        Assert.Equal(exact.FullPath, ranked[0].Record.FullPath);
    }

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
    public void RankExcludesFrequentlyUsedRecordWhenQueryDoesNotMatchText()
    {
        var now = new DateTimeOffset(2026, 7, 3, 1, 0, 0, TimeSpan.Zero);
        var records = new[]
        {
            FileRecord.Create("C:\\Archive\\Budget.xlsx", false, 1, now)
        };
        var usage = new[]
        {
            new UsageRecord("c:\\archive\\budget.xlsx", 100, now)
        };

        var ranked = ResultRanker.Rank(
            new SearchQuery("invoice", SearchMode.FilesAndFolders),
            records,
            usage,
            Array.Empty<string>());

        Assert.Empty(ranked);
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
    public void RankExcludesPinnedFolderWhenQueryDoesNotMatchText()
    {
        var now = new DateTimeOffset(2026, 7, 3, 1, 0, 0, TimeSpan.Zero);
        var records = new[]
        {
            FileRecord.Create("C:\\Pinned\\Reports", true, 0, now)
        };

        var ranked = ResultRanker.Rank(
            new SearchQuery("invoice", SearchMode.FoldersOnly),
            records,
            Array.Empty<UsageRecord>(),
            new[] { "c:\\pinned\\reports\\" });

        Assert.Empty(ranked);
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
    public void RankUsesOneNowForTiedUsageRecency()
    {
        var now = DateTimeOffset.UtcNow.AddDays(-10);
        var records = new[]
        {
            FileRecord.Create("C:\\Beta\\Invoice.xlsx", false, 1, now),
            FileRecord.Create("C:\\Alpha\\Invoice.xlsx", false, 1, now)
        };
        var usage = new[]
        {
            new UsageRecord("c:\\beta\\invoice.xlsx", 1, now),
            new UsageRecord("c:\\alpha\\invoice.xlsx", 1, now)
        };

        var ranked = ResultRanker.Rank(
            new SearchQuery("invoice", SearchMode.FilesAndFolders),
            DelayBeforeSecond(records),
            usage,
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

    private static IEnumerable<FileRecord> DelayBeforeSecond(IEnumerable<FileRecord> records)
    {
        var index = 0;
        foreach (var record in records)
        {
            if (index == 1)
            {
                Thread.Sleep(250);
            }

            index++;
            yield return record;
        }
    }
}

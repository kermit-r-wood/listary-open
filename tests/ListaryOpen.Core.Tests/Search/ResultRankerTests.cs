using ListaryOpen.Core.Indexing;
using ListaryOpen.Core.Search;
using ListaryOpen.Core.Usage;

namespace ListaryOpen.Core.Tests.Search;

public sealed class ResultRankerTests
{
    [Fact]
    public void RankPlacesPreferredRootBeforeBetterGlobalTextMatch()
    {
        var now = DateTimeOffset.UtcNow;
        var globalExact = FileRecord.Create("C:\\Elsewhere\\report", false, 1, now);
        var currentSubstring = FileRecord.Create("C:\\Projects\\old-report.txt", false, 1, now);

        var ranked = ResultRanker.Rank(
            new SearchQuery(
                "report",
                SearchMode.FilesAndFolders,
                preferredRoot: "C:\\Projects"),
            [globalExact, currentSubstring],
            Array.Empty<UsageRecord>(),
            Array.Empty<string>());

        Assert.Equal(currentSubstring.FullPath, ranked[0].Record.FullPath);
    }

    [Fact]
    public void RankDoesNotBoostPreferredRootDescendantsAboveGlobalTextQuality()
    {
        var now = DateTimeOffset.UtcNow;
        var globalExact = FileRecord.Create("C:\\Elsewhere\\report", false, 1, now);
        var descendantSubstring = FileRecord.Create(
            "C:\\Projects\\Nested\\old-report.txt",
            false,
            1,
            now);

        var ranked = ResultRanker.Rank(
            new SearchQuery(
                "report",
                SearchMode.FilesAndFolders,
                preferredRoot: "C:\\Projects"),
            [descendantSubstring, globalExact],
            Array.Empty<UsageRecord>(),
            Array.Empty<string>());

        Assert.Equal(globalExact.FullPath, ranked[0].Record.FullPath);
    }

    [Fact]
    public void RankOrdersExactNameBeforePrefixBeforeSubstringAndExcludesParentOnlyMatches()
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
            new[] { "Invoice", "InvoiceArchive.txt", "OldInvoice.txt" },
            ranked.Select(result => result.Record.Name));
    }

    [Fact]
    public void PlainQueryDoesNotReturnEveryChildOfAMatchingParentFolder()
    {
        var now = DateTimeOffset.UtcNow;
        var project = FileRecord.Create(@"C:\Projects\listary_open", true, 0, now);
        var child = FileRecord.Create(@"C:\Projects\listary_open\.git", true, 0, now);

        var ranked = ResultRanker.Rank(
            new SearchQuery("listary_open", SearchMode.FilesAndFolders),
            [child, project],
            Array.Empty<UsageRecord>(),
            Array.Empty<string>());

        Assert.Equal(project.FullPath, Assert.Single(ranked).Record.FullPath);
    }

    [Fact]
    public void RankPrefersRealProjectFolderOverPercentEncodedSessionFolder()
    {
        var now = DateTimeOffset.UtcNow;
        var realProject = FileRecord.Create(
            @"C:\Users\paulx\OneDrive\projects\listary_open",
            true,
            0,
            now);
        var sessionFolder = FileRecord.Create(
            @"C:\Users\paulx\.grok\sessions\C%3A%5CUsers%5Cpaulx%5COneDrive%5Cprojects%5Clistary_open",
            true,
            0,
            now);

        var ranked = ResultRanker.Rank(
            new SearchQuery("listary_open", SearchMode.FilesAndFolders),
            [sessionFolder, realProject],
            Array.Empty<UsageRecord>(),
            Array.Empty<string>());

        Assert.Equal(2, ranked.Count);
        Assert.Equal(realProject.FullPath, ranked[0].Record.FullPath);
        Assert.Equal(sessionFolder.FullPath, ranked[1].Record.FullPath);
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
    public void RankMatchesMultipleTermsIndependentlyOfInputOrder()
    {
        var record = FileRecord.Create(
            "C:\\Docs\\Invoice 2026.xlsx",
            false,
            1,
            DateTimeOffset.UtcNow);

        var ranked = ResultRanker.Rank(
            new SearchQuery("2026 invoice", SearchMode.FilesAndFolders),
            [record],
            Array.Empty<UsageRecord>(),
            Array.Empty<string>());

        Assert.Equal(record, Assert.Single(ranked).Record);
    }

    [Fact]
    public void RankRequiresQuotedPhraseToBeContiguous()
    {
        var record = FileRecord.Create(
            "C:\\Docs\\Invoice Final 2026.xlsx",
            false,
            1,
            DateTimeOffset.UtcNow);

        var ranked = ResultRanker.Rank(
            new SearchQuery("\"invoice 2026\"", SearchMode.FilesAndFolders),
            [record],
            Array.Empty<UsageRecord>(),
            Array.Empty<string>());

        Assert.Empty(ranked);
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
    public void RankBoostsStartMenuShortcutWhenTextTierMatches()
    {
        var now = new DateTimeOffset(2026, 7, 3, 1, 0, 0, TimeSpan.Zero);
        var app = FileRecord.Create(
            @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Code.lnk",
            false,
            1,
            now);
        var file = FileRecord.Create(@"C:\Docs\code-notes.txt", false, 1, now);

        var ranked = ResultRanker.Rank(
            new SearchQuery("code", SearchMode.FilesAndFolders),
            new[] { file, app },
            Array.Empty<UsageRecord>(),
            Array.Empty<string>());

        Assert.Equal(2, ranked.Count);
        // Both are name-substring tier; app soft-boost wins among equal text quality.
        Assert.Equal(app.FullPath, ranked[0].Record.FullPath);
    }

    [Fact]
    public void RankDoesNotLetWeakAppBeatExactNonAppName()
    {
        var now = new DateTimeOffset(2026, 7, 3, 1, 0, 0, TimeSpan.Zero);
        var exactFile = FileRecord.Create(@"C:\Docs\report.pdf", false, 1, now);
        var weakApp = FileRecord.Create(
            @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Report Tool.lnk",
            false,
            1,
            now);

        var ranked = ResultRanker.Rank(
            new SearchQuery("report.pdf", SearchMode.FilesAndFolders),
            new[] { weakApp, exactFile },
            Array.Empty<UsageRecord>(),
            Array.Empty<string>());

        Assert.Equal(exactFile.FullPath, ranked[0].Record.FullPath);
    }

    [Fact]
    public void RankFiltersToApplicationsWhenAppOperatorPresent()
    {
        var now = new DateTimeOffset(2026, 7, 3, 1, 0, 0, TimeSpan.Zero);
        var app = FileRecord.Create(
            @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Code.lnk",
            false,
            1,
            now);
        var file = FileRecord.Create(@"C:\Docs\code-notes.txt", false, 1, now);

        var ranked = ResultRanker.Rank(
            new SearchQuery("app: code", SearchMode.FilesAndFolders),
            new[] { file, app },
            Array.Empty<UsageRecord>(),
            Array.Empty<string>());

        var result = Assert.Single(ranked);
        Assert.Equal(app.FullPath, result.Record.FullPath);
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
    public void RankUsesTwoCharacterPinyinInitials()
    {
        var now = new DateTimeOffset(2026, 7, 3, 1, 0, 0, TimeSpan.Zero);
        var records = new[]
        {
            FileRecord.Create("C:\\Docs\\合同.docx", false, 1, now),
            FileRecord.Create("C:\\Docs\\other.txt", false, 1, now)
        };

        var ranked = ResultRanker.Rank(
            new SearchQuery("ht", SearchMode.FilesAndFolders),
            records,
            Array.Empty<UsageRecord>(),
            Array.Empty<string>());

        Assert.Contains(ranked, item => item.Record.Name == "合同.docx");
        Assert.Equal("pinyin", ranked.First(item => item.Record.Name == "合同.docx").MatchReason);
    }

    [Fact]
    public void RankPrefersShortPinyinOverLatinSubstringNoise()
    {
        var now = new DateTimeOffset(2026, 7, 3, 1, 0, 0, TimeSpan.Zero);
        var records = new[]
        {
            FileRecord.Create("C:\\Docs\\whitelist.txt", false, 1, now),
            FileRecord.Create("C:\\Docs\\height.md", false, 1, now),
            FileRecord.Create("C:\\Docs\\photo.jpg", false, 1, now),
            FileRecord.Create("C:\\Docs\\合同.docx", false, 1, now)
        };

        var ranked = ResultRanker.Rank(
            new SearchQuery("ht", SearchMode.FilesAndFolders),
            records,
            Array.Empty<UsageRecord>(),
            Array.Empty<string>());

        Assert.Equal("合同.docx", ranked[0].Record.Name);
        Assert.Equal("pinyin", ranked[0].MatchReason);
    }

    [Fact]
    public void RankPrefersPathSegmentMatchOverUnrelatedNameFuzzy()
    {
        var now = new DateTimeOffset(2026, 7, 3, 1, 0, 0, TimeSpan.Zero);
        var pathHit = FileRecord.Create("C:\\Projects\\中大成赢\\notes.txt", false, 1, now);
        var nameNoise = FileRecord.Create("C:\\Other\\xyz.txt", false, 1, now);

        var ranked = ResultRanker.Rank(
            new SearchQuery(@"Projects\中大", SearchMode.FilesAndFolders),
            new[] { nameNoise, pathHit },
            Array.Empty<UsageRecord>(),
            Array.Empty<string>());

        Assert.Equal(pathHit.FullPath, ranked[0].Record.FullPath);
        Assert.Equal("path-segment", ranked[0].MatchReason);
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

    [Fact]
    public void RankStopsEnumeratingRecordsAfterCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var enumeratedCount = 0;

        Assert.Throws<OperationCanceledException>(() => ResultRanker.Rank(
            new SearchQuery("invoice", SearchMode.FilesAndFolders),
            CancelDuringEnumeration(cancellation, () => enumeratedCount++),
            Array.Empty<UsageRecord>(),
            Array.Empty<string>(),
            cancellation.Token));

        Assert.InRange(enumeratedCount, 8, 72);
    }

    [Fact]
    public void RankChecksCancellationWhenAllRecordsAreFilteredOut()
    {
        using var cancellation = new CancellationTokenSource();
        var enumeratedCount = 0;

        Assert.Throws<OperationCanceledException>(() => ResultRanker.Rank(
            new SearchQuery("invoice", SearchMode.FoldersOnly),
            CancelDuringEnumeration(cancellation, () => enumeratedCount++),
            Array.Empty<UsageRecord>(),
            Array.Empty<string>(),
            cancellation.Token));

        Assert.InRange(enumeratedCount, 8, 72);
    }

    [Fact]
    public void RankWithCancelableTokenPreservesNormalResults()
    {
        var now = new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero);
        var records = new[]
        {
            FileRecord.Create("C:\\Docs\\OldInvoice.txt", false, 1, now),
            FileRecord.Create("C:\\Docs\\InvoiceArchive.txt", false, 1, now),
            FileRecord.Create("C:\\Docs\\Invoice", false, 1, now)
        };
        var query = new SearchQuery("invoice", SearchMode.FilesAndFolders);
        var expected = ResultRanker.Rank(
            query,
            records,
            Array.Empty<UsageRecord>(),
            Array.Empty<string>());
        using var cancellation = new CancellationTokenSource();

        var actual = ResultRanker.Rank(
            query,
            records,
            Array.Empty<UsageRecord>(),
            Array.Empty<string>(),
            cancellation.Token);

        Assert.Equal(expected, actual);
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

    private static IEnumerable<FileRecord> CancelDuringEnumeration(
        CancellationTokenSource cancellation,
        Action onEnumerated)
    {
        for (var index = 0; index < 1_000; index++)
        {
            onEnumerated();
            if (index == 7)
            {
                cancellation.Cancel();
            }

            yield return FileRecord.Create(
                $"C:\\Docs\\Invoice-{index:D4}.txt",
                false,
                1,
                DateTimeOffset.UnixEpoch);
        }
    }
}
